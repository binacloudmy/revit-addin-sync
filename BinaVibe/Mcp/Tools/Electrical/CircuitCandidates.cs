// suggest_circuits — propose power circuits for placed devices.
//
// READ-ONLY, and INSPECT on purpose: firing the Ya/Tidak gate on a call that
// changes nothing trains drafters to reflex-tap Ya. CircuitCommit commits.
//
// THIS FILE IS THE ft<->mm BOUNDARY for circuiting; everything handed to
// CircuitGrouping/PhaseBalance is mm/VA.
//
// No regulatory number is baked in — the six standards args are required, so a
// standards change is a recipe re-ingest, not an addin release. No panel is
// ever fabricated: a model without one returns ok:true + a blocker
// (CircuitBlockers). There is deliberately no include_circuited arg — freeing
// devices is remove_from_circuit's job, behind its own confirmation.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.Electrical.ElecReads;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static partial class CircuitCandidates
    {

        public static Dictionary<string, object?> Suggest(Document doc, JsonElement args)
        {
            // ── required standards args ───────────────────────────────────
            var voltageV = ArgsHelp.GetDouble(args, "voltage_v");
            var vaSocket = ArgsHelp.GetDouble(args, "va_per_socket");
            var vaLight = ArgsHelp.GetDouble(args, "va_per_lighting_point");
            var maxVa = ArgsHelp.GetDouble(args, "max_va_per_circuit");
            var maxDevices = ArgsHelp.GetLong(args, "max_devices_per_circuit");
            var breakerLadder = ArgsHelp.GetDoubleList(args, "breaker_ratings_a");

            var missing = new List<string>();
            if (!voltageV.HasValue) missing.Add("voltage_v");
            if (!vaSocket.HasValue) missing.Add("va_per_socket");
            if (!vaLight.HasValue) missing.Add("va_per_lighting_point");
            if (!maxVa.HasValue) missing.Add("max_va_per_circuit");
            if (!maxDevices.HasValue) missing.Add("max_devices_per_circuit");
            if (breakerLadder.Count == 0) missing.Add("breaker_ratings_a");
            if (missing.Count > 0)
                return ToolResult.FailMissingArgs(
                    missing, "standards args", "electrical design standards",
                    "electrical_circuiting");

            double? maxSpanMm = ArgsHelp.GetDouble(args, "max_group_span_mm");
            bool balancePhases = ArgsHelp.GetBool(args, "balance_phases") ?? true;
            long? pinnedPanelId = ArgsHelp.GetLong(args, "panel_id");
            int maxCandidates = (int)(ArgsHelp.GetLong(args, "max_candidates") ?? 50);

            // ── panels first: a missing panel is a blocker, not an error ──
            var panels = FindPanels(doc);
            var skippedPanels = CircuitBlockers.SkippedPanelRows(panels);
            var usable = panels.Where(p => p.Usable).ToList();
            if (pinnedPanelId.HasValue)
            {
                usable = usable.Where(p => p.Info.Id == pinnedPanelId.Value).ToList();
                if (usable.Count == 0)
                    return ToolResult.Fail("panel_id " + pinnedPanelId.Value + " is not a usable panel " +
                        "(not found, or no distribution system) — call suggest_circuits " +
                        "without panel_id to see what the model has");
            }
            if (usable.Count == 0)
                return CircuitBlockers.NoPanel(panels.Count, skippedPanels);

            // ── devices ───────────────────────────────────────────────────
            var (devices, skippedDevices, circuited) = CollectDevices(
                doc, args, vaSocket!.Value, vaLight!.Value);

            if (devices.Count == 0)
                return CircuitBlockers.NothingToGroup(skippedDevices, circuited);

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
    }
}
