// create_circuits — the write half of the circuiting workflow. MUTATE tool:
// the addin's ConfirmGate shows a Ya/Tidak card before this runs.
//
// Takes a plan_id + indices, never device lists. The reviewed grouping is
// read back out of CircuitPlanCache, so the drafter's confirmation and the
// circuits that get committed cannot drift apart.
//
// One TransactionGroup, single undo, per-circuit failure tolerance — a panel
// rejecting one circuit must not destroy the others (SocketPlacement's
// pattern, not BatchExecutor's roll-it-all-back).
//
// PHASE BALANCE IS A PROPOSAL. Circuits are committed round-robin across the
// proposed phases so Revit's sequential slot fill approximates the balance,
// then the ACTUAL slot is read back and reported next to the proposal.
// Deliberate v1 non-goal: PanelScheduleView.MoveSlotTo slot surgery.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class CircuitCommit
    {
        public static Dictionary<string, object?> CreateCircuits(Document doc, JsonElement args)
        {
            var planId = ArgsHelp.GetString(args, "plan_id")
                ?? throw new ArgumentException("missing plan_id");
            var plan = CircuitPlanCache.Get(planId, SocketCandidates.DocKey(doc));

            var wanted = ArgsHelp.GetLongList(args, "circuit_indices");
            var circuits = wanted.Count == 0
                ? plan.Circuits
                : plan.Circuits.Where(c => wanted.Contains(c.Index)).ToList();
            if (circuits.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"no circuits selected from plan {planId} " +
                                $"(plan holds {plan.Circuits.Count} circuits; indices are 0-based)",
                };

            var namePrefix = ArgsHelp.GetString(args, "load_name_prefix");

            // Round-robin across proposed phases (see file header).
            var commitOrder = RoundRobinByPhase(circuits);

            var created = new List<object>();
            var failed = new List<object>();

            using var group = new TransactionGroup(doc, "BinaVibe: create_circuits");
            group.Start();
            try
            {
                foreach (var pc in commitOrder)
                {
                    try
                    {
                        created.Add(CommitOne(doc, pc, namePrefix));
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new Dictionary<string, object?>
                        {
                            ["index"] = pc.Index,
                            ["reason"] = ex.Message,
                        });
                    }
                }
                group.Assimilate();
            }
            catch { group.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = planId,
                ["count"] = created.Count,
                ["created"] = created.OrderBy(r =>
                    (long)(((Dictionary<string, object?>)r)["index"] ?? 0L)).ToList(),
                ["failed"] = failed,
            };
        }

        private static Dictionary<string, object?> CommitOne(
            Document doc, PlannedCircuit pc, string? namePrefix)
        {
            // Re-verify against the LIVE model: the plan may be minutes old.
            var memberIds = new List<ElementId>();
            var dropped = new List<object>();
            foreach (var d in pc.Devices)
            {
                var el = doc.GetElement(ElemIds.From(d.Id)) as FamilyInstance;
                if (el == null)
                {
                    dropped.Add(Drop(d.Id, "device_gone"));
                    continue;
                }
                var systems = el.MEPModel?.GetElectricalSystems();
                if (systems != null &&
                    systems.Any(s => s.SystemType == ElectricalSystemType.PowerCircuit))
                {
                    dropped.Add(Drop(d.Id, "already_circuited_since_plan"));
                    continue;
                }
                memberIds.Add(el.Id);
            }
            if (memberIds.Count == 0)
                throw new InvalidOperationException(
                    "every device in circuit " + pc.Index +
                    " is gone or already circuited — re-run suggest_circuits");

            var panel = doc.GetElement(ElemIds.From(pc.PanelId)) as FamilyInstance
                ?? throw new InvalidOperationException(
                    "panel " + pc.PanelId + " no longer exists — re-run suggest_circuits");

            using var tx = new Transaction(doc, "BinaVibe: create circuit");
            TxGuard.StartSwallowing(tx);
            try
            {
                var sys = ElectricalSystem.Create(doc, memberIds, ElectricalSystemType.PowerCircuit);

                try
                {
                    sys.SelectPanel(panel);
                }
                catch (Exception ex)
                {
                    // Voltage / distribution-system mismatch, panel full, ...
                    // Surface Revit's own words — they name the mismatch. But
                    // a voltage mismatch READS like a panel problem while the
                    // voltage actually comes from the DEVICE connectors, so a
                    // voltage-flavoured rejection carries the redirect that
                    // stops the swap-the-DB-box loop.
                    var msg = "panel_rejected: " + ex.Message;
                    if (ex.Message.IndexOf("volt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ex.Message.IndexOf("do not match", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ex.Message.IndexOf("not compatible", StringComparison.OrdinalIgnoreCase) >= 0)
                        msg += " — NOTE: a circuit's voltage comes from the DEVICE " +
                               "connectors, not the panel, so this is usually NOT a " +
                               "wrong-panel problem. 0 V means a family's connector has " +
                               "no Voltage set: fix it with set_connector_electrical_data " +
                               "(check list_electrical_settings for the project's voltage " +
                               "first). Adding, deleting or swapping panels will NOT " +
                               "clear this.";
                    throw new InvalidOperationException(msg);
                }

                if (pc.BreakerA > 0)
                    sys.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM)?.Set(
                        UnitUtils.ConvertToInternalUnits(pc.BreakerA, UnitTypeId.Amperes));

                if (!string.IsNullOrWhiteSpace(namePrefix))
                {
                    var nameParam = sys.get_Parameter(BuiltInParameter.RBS_ELEC_CIRCUIT_NAME);
                    if (nameParam != null && !nameParam.IsReadOnly)
                        nameParam.Set(namePrefix + " " + pc.LoadClass + " " + pc.Index);
                }

                TxGuard.CommitOrThrow(tx);

                return new Dictionary<string, object?>
                {
                    ["index"] = pc.Index,
                    ["circuit_id"] = sys.Id.Value,
                    ["panel_id"] = pc.PanelId,
                    ["panel_name"] = pc.PanelName,
                    ["circuit_number"] = sys.CircuitNumber ?? "",
                    ["actual_slot"] = SafeStartSlot(sys),
                    ["proposed_phase"] = pc.ProposedPhase,
                    ["rating_a"] = pc.BreakerA > 0 ? pc.BreakerA : (object?)null,
                    ["load_class"] = pc.LoadClass,
                    ["device_count"] = memberIds.Count,
                    ["dropped_devices"] = dropped,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        /// <summary>Interleave circuits phase 0,1,2,0,1,2... so sequential slot
        /// fill lands them near the proposed balance. Stable within a phase.</summary>
        private static List<PlannedCircuit> RoundRobinByPhase(IReadOnlyList<PlannedCircuit> circuits)
        {
            var buckets = circuits
                .GroupBy(c => c.ProposedPhase)
                .OrderBy(g => g.Key)
                .Select(g => new Queue<PlannedCircuit>(g.OrderBy(c => c.Index)))
                .ToList();
            var order = new List<PlannedCircuit>(circuits.Count);
            while (order.Count < circuits.Count)
                foreach (var q in buckets)
                    if (q.Count > 0) order.Add(q.Dequeue());
            return order;
        }

        private static object? SafeStartSlot(ElectricalSystem sys)
        {
            try { return sys.StartSlot; }
            catch { return null; }   // unassigned / not applicable
        }

        private static Dictionary<string, object?> Drop(long id, string reason) => new()
        {
            ["id"] = id, ["reason"] = reason,
        };
    }
}
