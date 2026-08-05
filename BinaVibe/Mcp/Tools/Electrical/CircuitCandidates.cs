// suggest_circuits — propose power circuits for placed devices.
//
// READ-ONLY. No Transaction is ever opened here; this tool proposes, the
// drafter reviews, and create_circuits (Electrical/CircuitCommit.cs) commits.
// Registered as an INSPECT tool for the same confirm-fatigue reason as
// suggest_socket_points: firing the Ya/Tidak gate on a call that changes
// nothing trains drafters to reflex-tap Ya.
//
// THIS FILE IS THE ft<->mm BOUNDARY for circuiting. Everything handed to
// CircuitGrouping/PhaseBalance is mm/VA; everything read from the Revit API
// is feet/internal units.
//
// NO REGULATORY NUMBER IS BAKED IN. The six standards args are required; the
// Malaysian-practice values live in the electrical_circuiting recipe so a
// standards change needs a recipe re-ingest, not an addin release.
//
// NO PANEL IS EVER FABRICATED. A model without a usable panel returns
// ok:true with a structured blocker — deliberately NOT ok:false, because a
// missing distribution board is a drafter task, not a tool misuse the agent
// should self-heal around. "Every candidate was skipped" is the same class of
// answer and returns the same shape (see NothingToGroup); it used to be
// ok:false, which is what turned "all 10 sockets are already circuited" into
// a retry loop in UAT 2026-08-04.
//
// NO include_circuited / replace_existing ARG, deliberately. This tool is
// INSPECT and shows no Ya/Tidak card, so a plan that quietly carried a
// disconnect would reach the model behind a confirmation whose text says
// "create circuits" — and CircuitCommit would drop those devices as
// already_circuited_since_plan anyway unless it too gained destructive power.
// Freeing devices is remove_from_circuit's job, behind its own gate. Reverse
// this only if UAT shows the agent cannot sequence blocker -> disconnect ->
// re-suggest; it would then need PlannedCircuit.RequiresDisconnect plus a
// matching refusal in CircuitCommit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class CircuitCandidates
    {
        private const double MmPerFoot = 304.8;

        public static Dictionary<string, object?> Suggest(Document doc, JsonElement args)
        {
            // ── required standards args ───────────────────────────────────
            var voltageV = ArgsHelp.GetDouble(args, "voltage_v");
            var vaSocket = ArgsHelp.GetDouble(args, "va_per_socket");
            var vaLight = ArgsHelp.GetDouble(args, "va_per_lighting_point");
            var maxVa = ArgsHelp.GetDouble(args, "max_va_per_circuit");
            var maxDevices = ArgsHelp.GetLong(args, "max_devices_per_circuit");
            var breakerLadder = GetDoubleList(args, "breaker_ratings_a");

            var missing = new List<string>();
            if (!voltageV.HasValue) missing.Add("voltage_v");
            if (!vaSocket.HasValue) missing.Add("va_per_socket");
            if (!vaLight.HasValue) missing.Add("va_per_lighting_point");
            if (!maxVa.HasValue) missing.Add("max_va_per_circuit");
            if (!maxDevices.HasValue) missing.Add("max_devices_per_circuit");
            if (breakerLadder.Count == 0) missing.Add("breaker_ratings_a");
            if (missing.Count > 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "missing required standards args: " + string.Join(", ", missing) +
                                ". These are electrical design standards, not defaults the addin " +
                                "may assume — take the values from the electrical_circuiting " +
                                "recipe and pass them explicitly.",
                };

            double? maxSpanMm = ArgsHelp.GetDouble(args, "max_group_span_mm");
            bool balancePhases = ArgsHelp.GetBool(args, "balance_phases") ?? true;
            long? pinnedPanelId = ArgsHelp.GetLong(args, "panel_id");
            int maxCandidates = (int)(ArgsHelp.GetLong(args, "max_candidates") ?? 50);

            // ── panels first: a missing panel is a blocker, not an error ──
            var panels = FindPanels(doc);
            var skippedPanels = panels
                .Where(p => !p.Usable)
                .Select(p => (object)new Dictionary<string, object?>
                {
                    ["id"] = p.Info.Id, ["name"] = p.Info.Name, ["reason"] = p.SkipReason,
                    // The one fact that stops the place-delete-replace churn:
                    // an unusable panel is a SETTING, not a placement mistake.
                    ["fix"] = "call set_distribution_system on this panel (use " +
                              "list_electrical_settings to pick one) — re-placing or swapping " +
                              "the panel will not help. If the assignment is refused for a " +
                              "voltage mismatch, the panel FAMILY's connector is wrong: fix it " +
                              "with set_connector_electrical_data",
                })
                .ToList();
            var usable = panels.Where(p => p.Usable).ToList();
            if (pinnedPanelId.HasValue)
            {
                usable = usable.Where(p => p.Info.Id == pinnedPanelId.Value).ToList();
                if (usable.Count == 0)
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = "panel_id " + pinnedPanelId.Value + " is not a usable panel " +
                                    "(not found, or no distribution system) — call suggest_circuits " +
                                    "without panel_id to see what the model has",
                    };
            }
            if (usable.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["blocker"] = new Dictionary<string, object?>
                    {
                        ["code"] = "no_panel",
                        ["detail"] = "no usable electrical panel (Electrical Equipment with a " +
                                     "distribution system) exists in this model. If panels EXIST " +
                                     "but are unusable, see skipped_panels: assign a distribution " +
                                     "system with set_distribution_system (call " +
                                     "list_electrical_settings first for what this project " +
                                     "defines). Only when the model has NO panel at all must a " +
                                     "drafter place one — and even then, placing more panels " +
                                     "never fixes an unusable one, so do not place, delete or " +
                                     "swap panels in a loop",
                        ["panels_found"] = panels.Count,
                        ["skipped_panels"] = skippedPanels,
                    },
                    ["circuits"] = new List<object>(),
                    ["skipped_devices"] = new List<object>(),
                    ["panels"] = new List<object>(),
                    ["count"] = 0,
                };

            // ── devices ───────────────────────────────────────────────────
            var (devices, skippedDevices, circuited) = CollectDevices(
                doc, args, vaSocket!.Value, vaLight!.Value);

            if (devices.Count == 0)
                return NothingToGroup(skippedDevices, circuited);

            // ── group + assign ────────────────────────────────────────────
            // Grouping reference point: the lowest-id usable panel. With one
            // panel this is exact; with several it only seeds chain order, and
            // the id ordering keeps it deterministic.
            var refPanel = usable.OrderBy(p => p.Info.Id).First();
            var opts = new GroupingOptions((int)maxDevices!.Value, maxVa!.Value, maxSpanMm);
            var groups = CircuitGrouping.Group(devices, refPanel.XMm, refPanel.YMm, opts);

            bool truncated = false;
            if (groups.Count > maxCandidates)
            {
                groups = groups.Take(maxCandidates).ToList();
                truncated = true;
            }

            var assignments = PhaseBalance.Assign(
                groups, usable.Select(p => p.Info).ToList(), voltageV!.Value);
            if (!balancePhases)
                foreach (var a in assignments) a.ProposedPhase = 0;

            // ── build + cache the plan ────────────────────────────────────
            var ladder = breakerLadder.OrderBy(b => b).ToList();
            var plan = new CircuitPlan
            {
                VoltageV = voltageV.Value,
                ParamsUsed = new Dictionary<string, object?>
                {
                    ["voltage_v"] = voltageV.Value,
                    ["va_per_socket"] = vaSocket.Value,
                    ["va_per_lighting_point"] = vaLight.Value,
                    ["max_va_per_circuit"] = maxVa.Value,
                    ["max_devices_per_circuit"] = maxDevices.Value,
                    ["breaker_ratings_a"] = ladder.Cast<object>().ToList(),
                    ["max_group_span_mm"] = maxSpanMm,
                    ["balance_phases"] = balancePhases,
                    ["panel_id"] = pinnedPanelId,
                },
            };

            var circuitRows = new List<object>();
            foreach (var g in groups)
            {
                var a = assignments.First(x => x.CircuitIndex == g.Index);
                var panel = usable.First(p => p.Info.Id == a.PanelId);
                bool threePhase = panel.Info.Phases == 3;
                double calcAmps = WireSizing.CalcAmps(g.TotalVa, voltageV.Value, threePhase);
                double? breakerA = ladder.Cast<double?>().FirstOrDefault(b => b!.Value >= calcAmps);

                var pc = new PlannedCircuit
                {
                    Index = g.Index,
                    LoadClass = g.LoadClass,
                    Devices = g.DevicesInChainOrder,
                    TotalVa = g.TotalVa,
                    CalcAmps = calcAmps,
                    PanelId = panel.Info.Id,
                    PanelName = panel.Info.Name,
                    ProposedPhase = a.ProposedPhase,
                    BreakerA = breakerA ?? 0,
                    Feasible = a.Feasible,
                };
                pc.Notes.AddRange(g.Notes);
                if (!string.IsNullOrEmpty(a.Reason)) pc.Notes.Add(a.Reason);
                if (!breakerA.HasValue)
                    pc.Notes.Add("no breaker in breaker_ratings_a covers " +
                                 Math.Round(calcAmps, 1) + " A");
                plan.Circuits.Add(pc);

                circuitRows.Add(new Dictionary<string, object?>
                {
                    ["index"] = pc.Index,
                    ["load_class"] = pc.LoadClass,
                    ["panel_id"] = pc.PanelId,
                    ["panel_name"] = pc.PanelName,
                    ["proposed_phase"] = pc.ProposedPhase,
                    ["breaker_a"] = breakerA,
                    ["total_va"] = Math.Round(pc.TotalVa),
                    ["calc_amps"] = Math.Round(calcAmps, 2),
                    ["device_ids"] = pc.Devices.Select(d => (object)d.Id).ToList(),
                    ["device_count"] = pc.Devices.Count,
                    ["feasible"] = pc.Feasible,
                    ["notes"] = pc.Notes.Cast<object>().ToList(),
                });
            }

            var planId = CircuitPlanCache.Store(plan, SocketCandidates.DocKey(doc));

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = planId,
                ["count"] = circuitRows.Count,
                ["params_used"] = plan.ParamsUsed,
                ["circuits"] = circuitRows,
                ["skipped_devices"] = skippedDevices,
                ["panels"] = usable.Select(p => (object)new Dictionary<string, object?>
                {
                    ["id"] = p.Info.Id,
                    ["name"] = p.Info.Name,
                    ["phases"] = p.Info.Phases,
                    ["mains_a"] = p.Info.MainsA.HasValue ? Math.Round(p.Info.MainsA.Value, 1) : (object)"unknown",
                    ["connected_va"] = Math.Round(p.Info.ConnectedVa),
                    ["distribution_system"] = p.DistSystem,
                }).ToList(),
                ["skipped_panels"] = skippedPanels,
                ["truncated"] = truncated,
            };
        }

        // ─── nothing to group ───────────────────────────────────────────

        /// <summary>A device that was skipped because it is ALREADY on a power
        /// circuit, carried with the circuit that owns it. CollectDevices used
        /// to resolve this and throw it away, which is why the response could
        /// only say "already_circuited" and never which circuit.</summary>
        private sealed class CircuitedDevice
        {
            public long DeviceId;
            public long CircuitId;
            public string CircuitNumber = "";
            public long? PanelId;
            public string PanelName = "";
        }

        /// <summary>Every candidate was skipped. This is a drafter-actionable
        /// dead end, NOT a tool misuse, so it returns ok:true + a structured
        /// blocker for the same reason the no_panel branch does: ok:false is
        /// the agent's self-heal-retry signal, and there is nothing here for a
        /// retry to fix. The blocker carries the ids the next call needs, so
        /// the agent never has to improvise a discovery hop.</summary>
        private static Dictionary<string, object?> NothingToGroup(
            List<object> skippedDevices, List<CircuitedDevice> circuited)
        {
            var byReason = skippedDevices
                .OfType<Dictionary<string, object?>>()
                .GroupBy(r => (r.TryGetValue("reason", out var v) ? v?.ToString() : null) ?? "unknown")
                .OrderByDescending(g => g.Count())
                .ToList();

            string code;
            string detail;
            var blocker = new Dictionary<string, object?>();

            int alreadyCircuited = byReason.FirstOrDefault(g => g.Key == "already_circuited")?.Count() ?? 0;
            var levelKeys = byReason.Where(g => g.Key.StartsWith("level_mismatch")).ToList();
            int levelSkipped = levelKeys.Sum(g => g.Count());

            if (alreadyCircuited > 0 && alreadyCircuited == skippedDevices.Count)
            {
                code = "all_devices_already_circuited";
                detail = alreadyCircuited + " device(s) are already on power circuits, so there is " +
                         "nothing left to group. THIS IS OFTEN THE COMPLETE AND CORRECT ANSWER: " +
                         "say which circuits they are on (see existing_circuits) and stop. Only if " +
                         "the drafter explicitly wants them RE-circuited — a different grouping, " +
                         "a different panel or a different breaker — call remove_from_circuit with " +
                         "those device_ids, then re-run suggest_circuits. Do NOT retry this call " +
                         "unchanged, and do NOT place, delete or swap panels: neither frees a device.";
                blocker["existing_circuits"] = circuited
                    .GroupBy(c => c.CircuitId)
                    .OrderBy(g => g.Key)
                    .Select(g => (object)new Dictionary<string, object?>
                    {
                        ["circuit_id"] = g.Key,
                        ["circuit_number"] = g.First().CircuitNumber,
                        ["panel_id"] = g.First().PanelId,
                        ["panel_name"] = g.First().PanelName,
                        ["device_ids"] = g.Select(c => (object)c.DeviceId).OrderBy(x => (long)x).ToList(),
                        ["device_count"] = g.Count(),
                    })
                    .ToList();
                blocker["next_tool"] = "remove_from_circuit";
                blocker["next_args_hint"] = new Dictionary<string, object?>
                {
                    ["device_ids"] = circuited.Select(c => (object)c.DeviceId).OrderBy(x => (long)x).ToList(),
                };
            }
            else if (levelSkipped > 0 && levelSkipped == skippedDevices.Count)
            {
                code = "level_filter_excluded_everything";
                detail = "every candidate was excluded by the level filter — the levels actually " +
                         "found were: " +
                         string.Join(", ", levelKeys
                             .Select(g => g.Key.Substring("level_mismatch:".Length))
                             .Distinct()) +
                         ". Re-run with a level name that matches one of those, or omit level.";
                blocker["levels_found"] = levelKeys
                    .Select(g => (object)g.Key.Substring("level_mismatch:".Length))
                    .Distinct().ToList();
            }
            else
            {
                code = "no_circuitable_devices";
                detail = "no candidate could be circuited. Reasons: " +
                         string.Join(", ", byReason.Select(g => g.Key + " x" + g.Count())) +
                         ". connector_voltage_unset is fixed with set_connector_electrical_data; " +
                         "no_electrical_connector means the family has no power connector at all " +
                         "and only a drafter can add one in the Family Editor.";
            }

            blocker["code"] = code;
            blocker["detail"] = detail;
            blocker["skipped_count"] = skippedDevices.Count;

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["blocker"] = blocker,
                ["circuits"] = new List<object>(),
                ["count"] = 0,
                ["skipped_devices"] = skippedDevices,
                ["skipped_by_reason"] = byReason.ToDictionary(
                    g => g.Key, g => (object?)g.Count()),
            };
        }

        // ─── panels ─────────────────────────────────────────────────────
        internal sealed class PanelFacts
        {
            public PanelInfo Info = new();
            public double XMm, YMm, ZMm;
            public string DistSystem = "";
            public bool Usable;
            public string SkipReason = "";
        }

        /// <summary>Every Electrical Equipment instance, classified usable
        /// (has a distribution system) or skipped-with-reason. Shared with
        /// CircuitCommit for commit-time re-verification.</summary>
        internal static List<PanelFacts> FindPanels(Document doc)
        {
            var outRows = new List<PanelFacts>();
            var instances = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .OrderBy(fi => fi.Id.Value);

            foreach (var fi in instances)
            {
                var f = new PanelFacts();
                f.Info.Id = fi.Id.Value;
                f.Info.Name = fi.Symbol != null ? fi.Symbol.FamilyName + " : " + fi.Name : fi.Name;

                if (fi.Location is LocationPoint lp)
                {
                    f.XMm = lp.Point.X * MmPerFoot;
                    f.YMm = lp.Point.Y * MmPerFoot;
                    f.ZMm = lp.Point.Z * MmPerFoot;
                }

                var eq = fi.MEPModel as ElectricalEquipment;
                var dist = eq?.DistributionSystem;
                if (eq == null || dist == null)
                {
                    f.Usable = false;
                    f.SkipReason = "no_distribution_system";
                    outRows.Add(f);
                    continue;
                }

                f.DistSystem = dist.Name;
                f.Info.Phases = dist.ElectricalPhase == ElectricalPhase.ThreePhase ? 3 : 1;
                f.Info.PhaseVa = new double[f.Info.Phases];   // per-phase split of
                // existing load is not derivable without slot surgery — treated
                // as balanced; the commit reports actual slots so nothing hides.

                var mains = fi.get_Parameter(BuiltInParameter.RBS_ELEC_MAINS);
                if (mains != null && mains.HasValue && mains.AsDouble() > 1e-9)
                    f.Info.MainsA = UnitUtils.ConvertFromInternalUnits(
                        mains.AsDouble(), UnitTypeId.Amperes);

                double connectedVa = 0;
                var assigned = fi.MEPModel?.GetAssignedElectricalSystems();
                if (assigned != null)
                    foreach (var sys in assigned)
                    {
                        var load = sys.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD);
                        if (load != null && load.HasValue)
                            connectedVa += UnitUtils.ConvertFromInternalUnits(
                                load.AsDouble(), UnitTypeId.VoltAmperes);
                    }
                f.Info.ConnectedVa = connectedVa;
                f.Usable = true;
                outRows.Add(f);
            }
            return outRows;
        }

        // ─── devices ────────────────────────────────────────────────────

        /// <summary>Instance level, then the host's level, then the schedule-level
        /// parameter. Mirrors SocketPlacement.ResolveLevel — a hosted family
        /// carries no LevelId of its own. Shared with CircuitInventory so the
        /// wall-hosted-socket bug documented in CollectDevices cannot be
        /// re-introduced by a second copy of this walk.</summary>
        internal static string DeviceLevelName(Document d, Element fi)
        {
            var byId = (d.GetElement(fi.LevelId) as Level)?.Name;
            if (!string.IsNullOrEmpty(byId)) return byId!;

            if (fi is FamilyInstance inst && inst.Host != null)
            {
                var hostLevel = (d.GetElement(inst.Host.LevelId) as Level)?.Name;
                if (!string.IsNullOrEmpty(hostLevel)) return hostLevel!;
            }

            var p = fi.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            if (p != null && p.HasValue)
            {
                var byParam = (d.GetElement(p.AsElementId()) as Level)?.Name;
                if (!string.IsNullOrEmpty(byParam)) return byParam!;
            }
            return "";
        }

        private static readonly Dictionary<string, BuiltInCategory> CategoryWords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["electrical_fixtures"] = BuiltInCategory.OST_ElectricalFixtures,
            ["lighting_fixtures"] = BuiltInCategory.OST_LightingFixtures,
            ["lighting_devices"] = BuiltInCategory.OST_LightingDevices,
        };

        private static readonly HashSet<BuiltInCategory> LightingCategories = new()
        {
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_LightingDevices,
        };

        private static (List<ElecDevice> Devices, List<object> Skipped,
                        List<CircuitedDevice> Circuited) CollectDevices(
            Document doc, JsonElement args, double vaSocket, double vaLight)
        {
            var deviceIds = ArgsHelp.GetLongList(args, "device_ids");
            var levelFilter = ArgsHelp.GetString(args, "level");
            var catWords = ArgsHelp.GetStringList(args, "categories");

            var cats = new List<BuiltInCategory>();
            foreach (var w in catWords)
            {
                if (!CategoryWords.TryGetValue(w.Trim(), out var bic))
                    throw new ArgumentException(
                        "unknown category '" + w + "' — supported: " +
                        string.Join(", ", CategoryWords.Keys));
                cats.Add(bic);
            }
            if (cats.Count == 0)
                cats.AddRange(new[]
                {
                    BuiltInCategory.OST_ElectricalFixtures,
                    BuiltInCategory.OST_LightingFixtures,
                });

            var candidates = new List<FamilyInstance>();
            if (deviceIds.Count > 0)
            {
                foreach (var id in deviceIds)
                {
                    if (doc.GetElement(ElemIds.From(id)) is FamilyInstance fi)
                        candidates.Add(fi);
                }
            }
            else
            {
                foreach (var bic in cats)
                    candidates.AddRange(new FilteredElementCollector(doc)
                        .OfCategory(bic)
                        .WhereElementIsNotElementType()
                        .OfType<FamilyInstance>());
            }

            var devices = new List<ElecDevice>();
            var skipped = new List<object>();
            var circuited = new List<CircuitedDevice>();
            void Skip(long id, string reason) => skipped.Add(new Dictionary<string, object?>
            {
                ["id"] = id, ["reason"] = reason,
            });

            foreach (var fi in candidates.OrderBy(fi => fi.Id.Value))
            {
                long id = fi.Id.Value;

                // Explicitly requested ids must exist.
                if (fi.Category == null) { Skip(id, "no_category"); continue; }

                // Level, read the way SocketPlacement.ResolveLevel and
                // RoutePlanner do. fi.LevelId alone is NOT enough: a
                // wall-hosted socket — which is every socket place_socket_points
                // creates — reports InvalidElementId, so the name came back ""
                // and matched no filter. Every device was then dropped by the
                // `continue` below WITHOUT a skipped_devices row, producing
                // "no circuit-able devices found ... skipped: 0", which is
                // indistinguishable from a model with no sockets in it. That is
                // why circuiting only worked when device_ids were passed by
                // hand (UAT 2026-08-04).
                var levelName = DeviceLevelName(doc, fi);
                if (levelFilter != null &&
                    !string.Equals(levelName, levelFilter, StringComparison.OrdinalIgnoreCase))
                {
                    // A reported skip, not a silent drop: the caller asked for a
                    // level, and "your filter excluded these" is a different
                    // answer from "there is nothing here".
                    Skip(id, "level_mismatch:" +
                             (string.IsNullOrEmpty(levelName) ? "unknown" : levelName));
                    continue;
                }

                // Electrical connector required — ElectricalSystem.Create
                // rejects members without one, better to say so up front.
                var cm = fi.MEPModel?.ConnectorManager;
                bool hasElec = false;
                if (cm != null)
                    foreach (Connector c in cm.Connectors)
                        if (c.Domain == Domain.DomainElectrical) { hasElec = true; break; }
                if (!hasElec) { Skip(id, "no_electrical_connector"); continue; }

                // A 0 V device makes a 0 V circuit, and no distribution
                // system serves 0 V — SelectPanel would reject it at commit
                // with Revit wording that READS like a panel problem and sends
                // the agent off swapping DB boxes. The connector's voltage is
                // surfaced as RBS_ELEC_VOLTAGE on the instance or its type
                // (Connector itself exposes no voltage in the API). Skip only
                // on an AFFIRMATIVE zero — a family that exposes no voltage
                // parameter at all stays in (unknown, not proven broken) and
                // the commit-time panel_rejected redirect covers it.
                var voltParam = fi.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE);
                if (voltParam == null || !voltParam.HasValue)
                    voltParam = fi.Symbol?.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE);
                if (voltParam != null && voltParam.HasValue &&
                    UnitUtils.ConvertFromInternalUnits(
                        voltParam.AsDouble(), UnitTypeId.Volts) <= 1e-9)
                {
                    Skip(id, "connector_voltage_unset");
                    continue;
                }

                // Already on a power circuit: a device's power connector can
                // only belong to one, and GetElectricalSystems() lists every
                // system this instance is a member of. The owning circuit is
                // captured, not discarded — it is what lets the caller answer
                // "they are already on P1/3" and hand remove_from_circuit the
                // exact ids instead of making the agent hunt for them.
                var systems = fi.MEPModel?.GetElectricalSystems();
                var owner = systems?.FirstOrDefault(
                    s => s.SystemType == ElectricalSystemType.PowerCircuit);
                if (owner != null)
                {
                    circuited.Add(new CircuitedDevice
                    {
                        DeviceId = id,
                        CircuitId = owner.Id.Value,
                        CircuitNumber = SafeCircuitNumber(owner),
                        PanelId = owner.BaseEquipment?.Id.Value,
                        PanelName = owner.BaseEquipment?.Name ?? "",
                    });
                    Skip(id, "already_circuited");
                    continue;
                }

                if (fi.Location is not LocationPoint lp)
                {
                    Skip(id, "no_point_location");
                    continue;
                }

                var bic = (BuiltInCategory)fi.Category.Id.Value;
                bool isLighting = LightingCategories.Contains(bic);

                double va;
                string loadSource;
                var loadParam = fi.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD);
                if (loadParam != null && loadParam.HasValue && loadParam.AsDouble() > 1e-9)
                {
                    va = UnitUtils.ConvertFromInternalUnits(
                        loadParam.AsDouble(), UnitTypeId.VoltAmperes);
                    loadSource = "parameter";
                }
                else
                {
                    va = isLighting ? vaLight : vaSocket;
                    loadSource = "default_arg";
                }

                devices.Add(new ElecDevice
                {
                    Id = id,
                    XMm = lp.Point.X * MmPerFoot,
                    YMm = lp.Point.Y * MmPerFoot,
                    ZMm = lp.Point.Z * MmPerFoot,
                    Va = va,
                    LoadSource = loadSource,
                    LoadClass = isLighting ? "lighting" : "receptacle",
                    LevelName = levelName,
                });
            }

            // Explicit ids that resolved to nothing at all.
            foreach (var id in deviceIds)
                if (!devices.Any(d => d.Id == id) &&
                    !skipped.OfType<Dictionary<string, object?>>().Any(s => Equals(s["id"], id)))
                    Skip(id, "not_found_or_not_a_family_instance");

            return (devices, skipped, circuited);
        }

        /// <summary>CircuitNumber throws on a circuit Revit considers
        /// incomplete — an unassigned one, for instance.</summary>
        private static string SafeCircuitNumber(ElectricalSystem sys)
        {
            try { return sys.CircuitNumber ?? ""; }
            catch { return ""; }
        }

        private static List<double> GetDoubleList(JsonElement el, string name)
        {
            var vals = new List<double>();
            if (el.ValueKind != JsonValueKind.Object) return vals;
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return vals;
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d)) vals.Add(d);
            return vals;
        }
    }
}
