// Electrical validators — validate_panel_schedule / check_circuit_loads /
// check_code_compliance. Three SEPARATE read-only tools (no Transaction), so
// the agent can call exactly the check a question needs.
//
// Every threshold arrives from the caller (ultimately the backend recipe);
// a rule whose numbers were not supplied — or whose facts are not derivable
// from this model — is reported as a "skipped" finding with a reason, never
// silently passed. Rule arithmetic lives in ElecFindings/WireSizing (pure,
// unit-tested); this file only gathers facts from the model.
//
// The output is engineering INPUT, not sign-off: the backend docstrings and
// recipe carry the "electrical engineer signs off, not this tool" contract.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.Electrical.ElecReads;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class ElecValidation
    {

        // ─── validate_panel_schedule ────────────────────────────────────
        public static Dictionary<string, object?> ValidatePanelSchedule(Document doc, JsonElement args)
        {
            var utilPct = ArgsHelp.GetDouble(args, "max_panel_utilization_pct");
            var imbalancePct = ArgsHelp.GetDouble(args, "max_phase_imbalance_pct");
            var missing = new List<string>();
            if (!utilPct.HasValue) missing.Add("max_panel_utilization_pct");
            if (!imbalancePct.HasValue) missing.Add("max_phase_imbalance_pct");
            if (missing.Count > 0)
                return MissingArgs(missing);

            var wantedPanels = ArgsHelp.GetLongList(args, "panel_ids");
            var panels = CircuitCandidates.FindPanels(doc)
                .Where(p => wantedPanels.Count == 0 || wantedPanels.Contains(p.Info.Id))
                .ToList();

            var findings = new List<Finding>();

            // Orphaned circuits are a document-level defect: a circuit with no
            // panel belongs to every panel's report.
            var allCircuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(s => s.SystemType == ElectricalSystemType.PowerCircuit)
                .OrderBy(s => s.Id.Value)
                .ToList();
            foreach (var sys in allCircuits.Where(s => s.BaseEquipment == null))
                findings.Add(new Finding
                {
                    Check = "orphaned_circuit",
                    Status = "fail",
                    Elements = { sys.Id.Value },
                    Reason = "circuit " + (sys.CircuitNumber ?? sys.Id.Value.ToString()) +
                             " is not assigned to any panel",
                });

            // Panel schedules present in the document, matched by panel.
            var scheduleViews = new FilteredElementCollector(doc)
                .OfClass(typeof(PanelScheduleView)).Cast<PanelScheduleView>()
                .ToList();

            foreach (var p in panels)
            {
                if (!p.Usable)
                {
                    findings.Add(new Finding
                    {
                        Check = "panel_unconfigured",
                        Status = "fail",
                        Elements = { p.Info.Id },
                        Reason = "panel '" + p.Info.Name + "' has no distribution system — " +
                                 "circuits cannot be assigned to it",
                    });
                    continue;
                }

                var fed = allCircuits
                    .Where(s => s.BaseEquipment != null && s.BaseEquipment.Id.Value == p.Info.Id)
                    .ToList();

                // Duplicate breaker slots (circuit numbers) on one panel.
                findings.AddRange(ElecFindings.DoubleAssignedSlots(
                    fed.Select(s => ((long)s.Id.Value, p.Info.Id, s.CircuitNumber ?? "")).ToList()));

                // Schedule exists for the panel.
                bool hasSchedule = scheduleViews.Any(v =>
                {
                    try { return v.GetPanel()?.Value == p.Info.Id; }
                    catch { return false; }
                });
                findings.Add(new Finding
                {
                    Check = "schedule_missing",
                    Status = hasSchedule ? "pass" : "fail",
                    Elements = { p.Info.Id },
                    Reason = hasSchedule
                        ? "panel '" + p.Info.Name + "' has a panel schedule"
                        : "panel '" + p.Info.Name + "' has no panel schedule view — " +
                          "the schedule cannot be populated",
                });

                // Utilization vs mains.
                if (!p.Info.MainsA.HasValue)
                {
                    findings.Add(Finding.Skipped("panel_overloaded",
                        "panel '" + p.Info.Name + "' has no mains rating — utilization not verifiable"));
                }
                else
                {
                    double voltageGuess = 230.0;   // reporting scale only; the
                    // capacity comparison is VA-vs-VA via SpareVa, which uses
                    // the same figure on both sides.
                    double capacityVa = (PhaseBalance.SpareVa(p.Info, 0, voltageGuess) ?? 0)
                                        + p.Info.ConnectedVa;
                    double limitVa = capacityVa * utilPct!.Value / 100.0;
                    var f = new Finding
                    {
                        Check = "panel_overloaded",
                        Elements = { p.Info.Id },
                        Value = Math.Round(p.Info.ConnectedVa),
                        Limit = Math.Round(limitVa),
                        Unit = "VA",
                    };
                    if (p.Info.ConnectedVa > limitVa)
                    {
                        f.Status = "fail";
                        f.Reason = "panel '" + p.Info.Name + "' connected load " +
                                   Math.Round(p.Info.ConnectedVa) + " VA exceeds " +
                                   utilPct.Value + "% of capacity";
                    }
                    else
                    {
                        f.Reason = "panel '" + p.Info.Name + "' within " + utilPct.Value + "% utilization";
                    }
                    findings.Add(f);
                }

                // Phase imbalance: the per-phase split is not derivable without
                // slot surgery in v1 — reported skipped, never guessed.
                if (p.Info.Phases == 3)
                    findings.Add(Finding.Skipped("phase_imbalance",
                        "panel '" + p.Info.Name + "' per-phase load split is not derivable in v1 — " +
                        "review the panel schedule slots manually (limit " +
                        imbalancePct!.Value + "%)"));
            }

            return FindingsResult(findings, new Dictionary<string, object?>
            {
                ["panel_count"] = panels.Count,
                ["circuit_count"] = allCircuits.Count,
            });
        }

        // ─── check_circuit_loads ────────────────────────────────────────
        public static Dictionary<string, object?> CheckCircuitLoads(Document doc, JsonElement args)
        {
            var voltageV = ArgsHelp.GetDouble(args, "voltage_v");
            var dropLighting = ArgsHelp.GetDouble(args, "max_voltage_drop_pct_lighting");
            var dropPower = ArgsHelp.GetDouble(args, "max_voltage_drop_pct_power");
            var tableRows = ReadTable(args, "sizing_table");
            var missing = new List<string>();
            if (!voltageV.HasValue) missing.Add("voltage_v");
            if (!dropLighting.HasValue) missing.Add("max_voltage_drop_pct_lighting");
            if (!dropPower.HasValue) missing.Add("max_voltage_drop_pct_power");
            if (tableRows == null) missing.Add("sizing_table");
            if (missing.Count > 0)
                return MissingArgs(missing);

            List<SizingRow> sizing;
            try { sizing = WireSizing.ParseTable(tableRows!); }
            catch (ArgumentException ex)
            {
                return ToolResult.Fail(ex.Message);
            }

            // Diversity margin on the breaker; 1.0 = load may reach the rating.
            double maxLoadRatio = ArgsHelp.GetDouble(args, "max_load_ratio") ?? 1.0;

            var wanted = ArgsHelp.GetLongList(args, "circuit_ids");
            var circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(s => s.SystemType == ElectricalSystemType.PowerCircuit)
                .Where(s => wanted.Count == 0 || wanted.Contains(s.Id.Value))
                .OrderBy(s => s.Id.Value)
                .ToList();

            var findings = new List<Finding>();
            foreach (var sys in circuits)
            {
                long id = sys.Id.Value;
                string label = sys.CircuitNumber ?? id.ToString();

                double va = ParamAs(sys, BuiltInParameter.RBS_ELEC_APPARENT_LOAD, UnitTypeId.VoltAmperes);
                double volts = ParamAs(sys, BuiltInParameter.RBS_ELEC_VOLTAGE, UnitTypeId.Volts);
                if (volts <= 0) volts = voltageV!.Value;
                double ratingA = ParamAs(sys, BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM, UnitTypeId.Amperes);
                bool threePhase = SafePoles(sys) == 3;

                // load vs breaker
                if (ratingA <= 0)
                    findings.Add(Finding.Skipped("load_vs_breaker",
                        "circuit " + label + " has no breaker rating set"));
                else
                    findings.Add(ElecFindings.LoadVsBreaker(id, va, volts, ratingA, maxLoadRatio, threePhase));

                // voltage drop — ONLY over a real routed length.
                double lengthMm = SafeLengthMm(sys);
                bool routedPath = SafePathMode(sys) == ElectricalCircuitPathMode.Custom;
                if (lengthMm <= 0)
                {
                    findings.Add(Finding.Skipped("voltage_drop",
                        "circuit " + label + " has no length — route it first " +
                        "(create_circuit_routes); voltage drop is never estimated from " +
                        "crow-fly distance"));
                    continue;
                }

                double amps = WireSizing.CalcAmps(va, volts, threePhase);
                var row = WireSizing.Pick(Math.Max(amps, 0.001), sizing);
                if (row == null)
                {
                    findings.Add(Finding.Skipped("voltage_drop",
                        "circuit " + label + ": no sizing_table row covers " +
                        Math.Round(amps, 1) + " A — cannot resolve mv_per_a_m"));
                    continue;
                }

                double limit = IsLightingCircuit(sys) ? dropLighting!.Value : dropPower!.Value;
                findings.Add(ElecFindings.VoltageDrop(
                    id, amps, row.MvPerAM, lengthMm, volts, limit, threePhase,
                    routedPath ? "circuit_path" : "revit_length_" + SafePathMode(sys).ToString().ToLowerInvariant()));
            }

            return FindingsResult(findings, new Dictionary<string, object?>
            {
                ["circuit_count"] = circuits.Count,
            });
        }

        // ─── check_code_compliance ──────────────────────────────────────
        public static Dictionary<string, object?> CheckCodeCompliance(Document doc, JsonElement args)
        {
            var spacingMm = ArgsHelp.GetDouble(args, "max_socket_spacing_mm");
            var keywords = ArgsHelp.GetStringList(args, "dedicated_circuit_keywords");
            var missing = new List<string>();
            if (!spacingMm.HasValue) missing.Add("max_socket_spacing_mm");
            if (keywords.Count == 0) missing.Add("dedicated_circuit_keywords");
            if (missing.Count > 0)
                return MissingArgs(missing);

            var roomIds = ArgsHelp.GetLongList(args, "room_ids");
            var levelFilter = ArgsHelp.GetString(args, "level");

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => r.Area > 1e-9)
                .Where(r => roomIds.Count == 0 || roomIds.Contains(r.Id.Value))
                .Where(r => levelFilter == null ||
                            string.Equals(r.Level?.Name, levelFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Id.Value)
                .ToList();

            var sockets = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalFixtures)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(fi => fi.Location is LocationPoint)
                .ToList();

            var findings = new List<Finding>();

            // ── receptacle spacing per room boundary run ──────────────────
            foreach (var room in rooms)
            {
                var loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions
                {
                    SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
                });
                if (loops == null || loops.Count == 0)
                {
                    findings.Add(Finding.Skipped("receptacle_spacing",
                        "room " + room.Id.Value + " (" + room.Name + ") has no boundary"));
                    continue;
                }

                var inRoom = sockets
                    .Where(fi => fi.Room != null && fi.Room.Id == room.Id)
                    .ToList();

                foreach (var loop in loops)
                {
                    foreach (var seg in loop)
                    {
                        var curve = seg.GetCurve();
                        if (curve == null) continue;
                        double runLenMm = curve.Length * MmPerFoot;
                        if (runLenMm < 1.0) continue;

                        var stations = new List<double>();
                        var ids = new List<long>();
                        foreach (var fi in inRoom)
                        {
                            var pt = ((LocationPoint)fi.Location!).Point;
                            var proj = curve.Project(new XYZ(pt.X, pt.Y, curve.GetEndPoint(0).Z));
                            if (proj == null) continue;
                            double offMm = proj.XYZPoint.DistanceTo(new XYZ(pt.X, pt.Y, proj.XYZPoint.Z)) * MmPerFoot;
                            if (offMm > 300) continue;   // not on this wall run
                            stations.Add(curve.GetEndPoint(0).DistanceTo(proj.XYZPoint) * MmPerFoot);
                            ids.Add(fi.Id.Value);
                        }

                        var f = ElecFindings.ReceptacleSpacing(
                            "room:" + room.Id.Value + " wall:" + (seg.ElementId?.Value ?? 0),
                            stations, runLenMm, spacingMm!.Value, ids);
                        // Room context makes the row actionable.
                        f.Reason = room.Name + ": " + f.Reason;
                        findings.Add(f);
                    }
                }
            }

            // ── dedicated circuits by keyword ─────────────────────────────
            var allDevices = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalFixtures)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Concat(new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>())
                .OrderBy(fi => fi.Id.Value);

            foreach (var fi in allDevices)
            {
                string name = ((fi.Symbol?.FamilyName ?? "") + " " + fi.Name).ToLowerInvariant();
                var hit = keywords.FirstOrDefault(k =>
                    !string.IsNullOrWhiteSpace(k) && name.Contains(k.Trim().ToLowerInvariant()));
                if (hit == null) continue;

                var circuit = fi.MEPModel?.GetElectricalSystems()
                    ?.FirstOrDefault(s => s.SystemType == ElectricalSystemType.PowerCircuit);
                if (circuit == null)
                {
                    findings.Add(new Finding
                    {
                        Check = "dedicated_circuit",
                        Status = "warning",
                        Elements = { fi.Id.Value },
                        Reason = "'" + fi.Name + "' matches dedicated-circuit keyword '" + hit +
                                 "' but is not on any circuit yet",
                    });
                    continue;
                }

                int members = circuit.Elements.Size;
                var f = new Finding
                {
                    Check = "dedicated_circuit",
                    Elements = { fi.Id.Value, circuit.Id.Value },
                    Value = members,
                    Limit = 1,
                    Unit = "devices",
                };
                if (members > 1)
                {
                    f.Status = "fail";
                    f.Reason = "'" + fi.Name + "' (keyword '" + hit + "') shares circuit " +
                               (circuit.CircuitNumber ?? "?") + " with " + (members - 1) +
                               " other device(s) — needs a dedicated circuit";
                }
                else
                {
                    f.Reason = "'" + fi.Name + "' is on a dedicated circuit";
                }
                findings.Add(f);
            }

            return FindingsResult(findings, new Dictionary<string, object?>
            {
                ["room_count"] = rooms.Count,
                ["socket_count"] = sockets.Count,
            });
        }

        // ─── shared plumbing ────────────────────────────────────────────
        /// <summary>Thresholds are jurisdiction-dependent, so a rule whose
        /// numbers were not supplied is refused, never defaulted.</summary>
        private static Dictionary<string, object?> MissingArgs(List<string> names) =>
            ToolResult.FailMissingArgs(
                names, "rule args", "jurisdiction-dependent thresholds",
                "electrical_validation");

        private static Dictionary<string, object?> FindingsResult(
            List<Finding> findings, Dictionary<string, object?> extra)
        {
            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["findings"] = findings.Select(f => (object)f.ToDict()).ToList(),
                ["counts"] = Finding.Counts(findings),
            };
            foreach (var kv in extra) result[kv.Key] = kv.Value;
            return result;
        }

        private static List<IReadOnlyDictionary<string, double>>? ReadTable(
            JsonElement args, string name)
        {
            if (args.ValueKind != JsonValueKind.Object ||
                !args.TryGetProperty(name, out var v) ||
                v.ValueKind != JsonValueKind.Array)
                return null;
            var rows = new List<IReadOnlyDictionary<string, double>>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;
                var row = new Dictionary<string, double>();
                foreach (var p in item.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number &&
                        p.Value.TryGetDouble(out var d))
                        row[p.Name] = d;
                rows.Add(row);
            }
            return rows;
        }





        /// <summary>All members are lighting fixtures/devices — then the
        /// lighting voltage-drop limit applies.</summary>
        private static bool IsLightingCircuit(ElectricalSystem sys)
        {
            bool any = false;
            foreach (Element el in sys.Elements)
            {
                any = true;
                var bic = el.Category != null ? (BuiltInCategory)el.Category.Id.Value : 0;
                if (bic != BuiltInCategory.OST_LightingFixtures &&
                    bic != BuiltInCategory.OST_LightingDevices)
                    return false;
            }
            return any;
        }
    }
}
