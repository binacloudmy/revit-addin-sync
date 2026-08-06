// Transaction hygiene shared by every MEP tool.
//
// THE RULE THIS FILE EXISTS TO ENFORCE: never write
//     catch { tx.RollBack(); }
// TxGuard.CommitOrThrow calls tx.Commit() FIRST. If Revit rolled the change
// back, the transaction is already ENDED and only then does CommitOrThrow
// throw. Calling RollBack() on a non-Started transaction throws from inside
// the catch block, and that second exception REPLACES the real Revit error
// text SwallowWarnings captured — the drafter reads "transaction has not been
// started" instead of "the panel does not have enough slots".
//
// `using var tx` already disposes an unfinished transaction, so SafeRollback
// exists for ERROR-MESSAGE FIDELITY, not leak prevention. That is exactly why
// it is easy to get wrong and worth a named helper.
//
// Promoted verbatim from MutatorsMepRouting.SafeRollback (which stays as a
// thin forwarder so the routing file's diff stays small).
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal static class MepTx
    {
        /// <summary>Roll back only a transaction Revit still considers open.</summary>
        public static void SafeRollback(Transaction? tx)
        {
            if (tx != null && tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
        }

        /// <summary>Same trap, group flavour: RollBack() after Assimilate()
        /// throws, and Assimilate itself can throw, so the catch that guards
        /// it must not assume the group is still Started.</summary>
        public static void SafeRollback(TransactionGroup? group)
        {
            if (group != null && group.GetStatus() == TransactionStatus.Started) group.RollBack();
        }

        /// <summary>Standard failure row. ``error_code`` is optional and only
        /// set where the agent can branch on it (e.g. "not_revit_mep").</summary>
        public static Dictionary<string, object?> Failure(string error, string? errorCode = null)
        {
            var row = new Dictionary<string, object?> { ["ok"] = false, ["error"] = error };
            if (errorCode != null) row["error_code"] = errorCode;
            return row;
        }

        /// <summary>Failure carrying a machine-readable blocker instead of
        /// ok:false. Used where the answer is "nothing to do, and here is
        /// why" — ok:false makes the agent self-heal and retry forever.</summary>
        public static Dictionary<string, object?> Blocked(string code, string detail,
            Dictionary<string, object?>? extra = null)
        {
            var blocker = new Dictionary<string, object?> { ["code"] = code, ["detail"] = detail };
            if (extra != null)
                foreach (var kv in extra) blocker[kv.Key] = kv.Value;
            return new Dictionary<string, object?> { ["ok"] = true, ["blocker"] = blocker };
        }
    }
}
