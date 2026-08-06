// Layer 2 chaining: several writes, one undo, one honest failure report.
//
// A TransactionGroup with ONE TRANSACTION PER STEP — not a single big
// transaction. Three reasons, all of them load-bearing:
//   * ElectricalSystem.Create -> SelectPanel -> NewWires each read state the
//     previous step committed (slot allocation, circuit number, wire paths);
//   * NewMechanicalSystem is documented to "regenerate the document even in
//     manual regeneration mode", so it is not safely nestable in a shared
//     transaction with later reads;
//   * Assimilate() still collapses the whole group into one Ctrl+Z, and
//     RollBack() on the group discards every committed sub-transaction — so
//     per-step transactions cost nothing in atomicity.
//
// STEPS CALL *Core METHODS, NEVER TOOL ENTRY POINTS. A tool entry point owns
// its own Transaction (nesting one is illegal in Revit) and converts failure
// into a returned {ok:false} — which a chain would sail straight past, because
// nothing threw.
//
// Required vs BestEffort is a real distinction, not decoration: in
// place_and_circuit_device, creating the circuit is Required and drawing wires
// is BestEffort, because "there is no suitable view open" must not destroy a
// correct circuit.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal sealed class ChainStep
    {
        public string Name = "";

        /// <summary>A Required step that throws rolls the WHOLE group back. A
        /// BestEffort one is recorded as failed and the chain continues.</summary>
        public bool Required = true;

        /// <summary>Runs inside its own Transaction. THROW on failure — a
        /// returned dictionary is treated as success.</summary>
        public Func<Dictionary<string, object?>> Run = () => new Dictionary<string, object?>();
    }

    internal sealed class StepResult
    {
        public string Name = "";
        public bool Ok;
        public string? Error;
        public Dictionary<string, object?> Data = new();
    }

    internal static class MepStepChain
    {
        /// <summary>Run the chain. Never throws — every outcome is a row.</summary>
        public static Dictionary<string, object?> Run(
            Document doc, string groupName, IReadOnlyList<ChainStep> steps)
        {
            var results = new List<StepResult>();
            TransactionGroup? group = null;

            try
            {
                group = new TransactionGroup(doc, groupName);
                group.Start();

                for (int i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    Transaction? tx = null;
                    try
                    {
                        tx = new Transaction(doc, $"{groupName}: {step.Name}");
                        TxGuard.StartSwallowing(tx);
                        var data = step.Run() ?? new Dictionary<string, object?>();
                        TxGuard.CommitOrThrow(tx);
                        results.Add(new StepResult { Name = step.Name, Ok = true, Data = data });
                    }
                    catch (Exception ex)
                    {
                        // MepTx.SafeRollback, never a bare tx.RollBack():
                        // CommitOrThrow has already ENDED the transaction when
                        // Revit rejected the change, and rolling back a
                        // non-Started transaction throws a second exception
                        // that replaces the real Revit message.
                        MepTx.SafeRollback(tx);
                        results.Add(new StepResult { Name = step.Name, Ok = false, Error = ex.Message });

                        if (!step.Required) continue;

                        MepTx.SafeRollback(group);
                        return Failed(groupName, results, i, ex.Message, rolledBack: true);
                    }
                }

                group.Assimilate();   // N transactions, one undo step
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["rolled_back"] = false,
                    ["steps"] = results.Select(Row).ToList(),
                    ["step_count"] = results.Count,
                    ["failed_optional_steps"] = results.Where(r => !r.Ok).Select(r => r.Name).ToList(),
                };
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(group);
                return Failed(groupName, results, results.Count, ex.Message, rolledBack: true);
            }
        }

        private static Dictionary<string, object?> Failed(
            string groupName, List<StepResult> results, int index, string error, bool rolledBack)
            => new()
            {
                ["ok"] = false,
                ["failed_step"] = index < results.Count ? results[index].Name : groupName,
                ["failed_step_index"] = index,
                ["error"] = error,
                // Load-bearing: it tells the agent that every id in the earlier
                // steps' data is GONE and must not be fed to a follow-up call.
                // Without it the agent cheerfully retries against a dead id.
                ["rolled_back"] = rolledBack,
                ["steps"] = results.Select(Row).ToList(),
            };

        private static Dictionary<string, object?> Row(StepResult r)
        {
            var row = new Dictionary<string, object?>
            {
                ["name"] = r.Name,
                ["ok"] = r.Ok,
            };
            if (r.Error != null) row["error"] = r.Error;
            if (r.Data.Count > 0) row["data"] = r.Data;
            return row;
        }

        /// <summary>Pull a value out of an earlier step's data.</summary>
        public static T? Value<T>(Dictionary<string, object?> chainResult, string stepName, string key)
        {
            if (chainResult.TryGetValue("steps", out var s) && s is List<Dictionary<string, object?>> rows)
            {
                foreach (var row in rows)
                {
                    if (!Equals(row.GetValueOrDefault("name"), stepName)) continue;
                    if (row.GetValueOrDefault("data") is Dictionary<string, object?> data
                        && data.TryGetValue(key, out var v) && v is T typed)
                        return typed;
                }
            }
            return default;
        }
    }
}
