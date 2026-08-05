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
// COMMIT BOUNDARY. CommitOne is split at TxGuard.CommitOrThrow: everything
// before it may throw (rolled back, filed under failed[], committed:false);
// everything after it is BuildCreatedRow, which never throws. The old code ran
// the read-back inside the same try, so a throwing CircuitNumber sent a
// COMMITTED circuit into failed[] and its sockets stayed assigned while the
// tool reported failure — UAT 2026-08-04, the report that made the agent loop.
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
using static BinaVibe.Mcp.Tools.Electrical.ElecReads;

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
                return ToolResult.Fail($"no circuits selected from plan {planId} " +
                    $"(plan holds {plan.Circuits.Count} circuits; indices are 0-based)");

            var namePrefix = ArgsHelp.GetString(args, "load_name_prefix");

            // A circuit the proposal marked infeasible ("no panel has spare
            // capacity for N VA") used to commit exactly like a feasible one —
            // pc.Feasible was written by PhaseBalance and then never read, so
            // the warning lived only in a proposal an unattended agent skims.
            var allowInfeasible = ArgsHelp.GetBool(args, "allow_infeasible") ?? false;
            var infeasible = circuits.Where(c => !c.Feasible).ToList();
            if (infeasible.Count > 0 && !allowInfeasible)
                return ToolResult.Fail(infeasible.Count + " of " + circuits.Count + " selected circuit(s) " +
                    "were proposed as INFEASIBLE (see notes on each: usually no panel has " +
                    "spare capacity). Committing them overloads the board. Fix the panel " +
                    "or drop those circuits, or pass allow_infeasible:true to commit anyway.",
                    new Dictionary<string, object?>
                    {
                        ["infeasible"] = infeasible.Select(c => (object)new Dictionary<string, object?>
                        {
                            ["index"] = c.Index,
                            ["panel_id"] = c.PanelId,
                            ["total_va"] = Math.Round(c.TotalVa),
                            ["notes"] = c.Notes.Cast<object>().ToList(),
                        }).ToList(),
                    });

            // Round-robin across proposed phases (see file header).
            var commitOrder = RoundRobinByPhase(circuits);

            var created = new List<object>();
            var failed = new List<object>();

            // committed:false is a promise, not decoration. Every throw out of
            // CommitOne happens before its commit succeeded (the post-commit
            // read-back cannot throw — see BuildCreatedRow), so a failed row
            // means NOTHING reached the model and the devices are still free.
            TxGuard.ForEachInGroup(doc, "BinaVibe: create_circuits", commitOrder,
                pc => created.Add(CommitOne(doc, pc, namePrefix)),
                (pc, ex) => failed.Add(new Dictionary<string, object?>
                {
                    ["index"] = pc.Index,
                    ["committed"] = false,
                    ["reason"] = ex.Message,
                }));

            // ok:false when nothing was created. This used to be an
            // unconditional true, so a run where every circuit failed returned
            // {ok:true, count:0, failed:[...]} — an unattended loop branching on
            // ok read that as done and moved on to routing.
            return new Dictionary<string, object?>
            {
                ["ok"] = created.Count > 0,
                ["plan_id"] = planId,
                ["count"] = created.Count,
                ["created"] = created.OrderBy(r =>
                    (long)(((Dictionary<string, object?>)r)["index"] ?? 0L)).ToList(),
                ["failed"] = failed,
                ["error"] = created.Count > 0 ? null
                    : "no circuit was created — all " + failed.Count +
                      " attempt(s) failed; see failed[] for each reason",
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

            // Existence was the only panel check here, so a panel that LOST its
            // distribution system between propose and commit reached SelectPanel
            // and came back as raw Revit prose. The propose step already knows
            // how to say this properly — ask it the same question.
            var eq = panel.MEPModel as ElectricalEquipment;
            if (eq?.DistributionSystem == null)
                throw new InvalidOperationException(
                    "panel_unusable: panel " + pc.PanelId + " has no distribution system now " +
                    "(it did when the plan was made) — assign one with set_distribution_system " +
                    "and re-run suggest_circuits. Do not swap panels.");

            using var tx = new Transaction(doc, "BinaVibe: create circuit");
            TxGuard.StartSwallowing(tx);
            ElectricalSystem? sys = null;
            long circuitId = 0;
            try
            {
                sys = ElectricalSystem.Create(doc, memberIds, ElectricalSystemType.PowerCircuit);
                circuitId = sys.Id.Value;   // read while we are certainly inside
                                            // the transaction, so the post-commit
                                            // row never has to touch sys.Id

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
            }
            catch
            {
                // Only reachable BEFORE the commit succeeded, so rolling back
                // and reporting a failure is honest here. SafeRollBack because
                // a Revit-forced rollback already ended the transaction and a
                // second RollBack() would replace Revit's message with ours.
                TxGuard.SafeRollBack(tx);
                throw;
            }

            // PAST THE COMMIT LINE. The circuit EXISTS. Nothing below may throw
            // out of CommitOne, because the caller's catch files a throw under
            // failed[] — and a failed[] row for a circuit that is in the model
            // with its sockets assigned is exactly the report that sent the
            // agent looping in UAT 2026-08-04. Reading back a fresh
            // ElectricalSystem is not safe: CircuitNumber and StartSlot both
            // throw on states Revit considers incomplete.
            return BuildCreatedRow(sys!, circuitId, pc, memberIds.Count, dropped);
        }

        /// <summary>Result row for an ALREADY-COMMITTED circuit. Never throws:
        /// a read-back failure degrades the row, it does not undo the write.
        /// The id is passed in rather than re-read for the same reason.</summary>
        private static Dictionary<string, object?> BuildCreatedRow(
            ElectricalSystem sys, long circuitId, PlannedCircuit pc,
            int deviceCount, List<object> dropped)
        {
            var row = new Dictionary<string, object?>
            {
                ["index"] = pc.Index,
                ["circuit_id"] = circuitId,
                ["committed"] = true,
                ["panel_id"] = pc.PanelId,
                ["panel_name"] = pc.PanelName,
                ["proposed_phase"] = pc.ProposedPhase,
                ["rating_a"] = pc.BreakerA > 0 ? pc.BreakerA : (object?)null,
                ["load_class"] = pc.LoadClass,
                ["device_count"] = deviceCount,
                ["dropped_devices"] = dropped,
            };
            try
            {
                row["circuit_number"] = SafeCircuitNumber(sys);
                row["actual_slot"] = SafeStartSlot(sys);
            }
            catch (Exception ex)
            {
                row["circuit_number"] = "";
                row["actual_slot"] = null;
                row["report_error"] = "circuit was created but could not be read back: " + ex.Message;
            }
            return row;
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



        private static Dictionary<string, object?> Drop(long id, string reason) => new()
        {
            ["id"] = id, ["reason"] = reason,
        };
    }
}
