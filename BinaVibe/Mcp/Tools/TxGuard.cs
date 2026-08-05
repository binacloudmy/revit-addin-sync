// TxGuard — starts a Revit Transaction with warning auto-suppression AND
// error-safe rollback so a mutation can never (a) hard-freeze the UI thread on
// a modal failure dialog, nor (b) silently report success after Revit rolled
// the change back.
//
// Warnings ("walls overlap", "room not enclosed", …) block the UI thread on a
// modal dialog a human often cannot even see, because the Copilot pane has
// focus — an UNBOUNDED freeze. SwallowWarnings deletes them. On an ERROR it
// captures the text and returns ProceedWithRollBack, so Revit rolls back
// silently instead of escalating to a modal; CommitOrThrow then turns that
// rollback into a clean exception rather than a false success.
//
// SafeRollBack exists because CommitOrThrow throws AFTER Revit has already
// ENDED the transaction, so the obvious `catch { tx.RollBack(); throw; }` has
// two failure modes:
//
//   1. Revit rolled the commit back. RollBack() on an already-ended
//      transaction throws, and THAT second exception reaches the agent —
//      destroying Revit's own message, the one naming the real mismatch.
//   2. The commit SUCCEEDED and a later line threw (a post-commit read like
//      ElectricalSystem.CircuitNumber). RollBack() targets a COMMITTED
//      transaction, throws, and the tool reports a write that is still in the
//      model as failed.
//
// Rolling back only a still-Started transaction fixes (1) and stops (2) from
// masking; (2) additionally needs the caller to keep post-commit reads out of
// the try block (see CircuitCommit.BuildCreatedRow).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class TxGuard
    {
        // Errors captured by the most recent preprocessor run on THIS thread.
        // Tools run synchronously on Revit's single UI thread, so a thread-static
        // hand-off from the preprocessor to CommitOrThrow is safe and avoids
        // threading a preprocessor reference through every call site.
        [ThreadStatic] private static List<string>? _lastErrors;

        /// <summary>Starts <paramref name="tx"/> with a SwallowWarnings failure
        /// preprocessor attached. Use in place of a bare <c>tx.Start()</c> for
        /// any write that could trip a Revit warning/error dialog. Pair with
        /// <see cref="CommitOrThrow"/> instead of a bare <c>tx.Commit()</c>.</summary>
        public static void StartSwallowing(Transaction tx)
        {
            _lastErrors = null;
            tx.Start();
            var fho = tx.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(new SwallowWarnings());
            fho.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fho);
        }

        /// <summary>Commits a StartSwallowing'd transaction. If Revit rolled the
        /// transaction back (a hard error the preprocessor could not swallow),
        /// throws with the captured Revit error text instead of returning a
        /// silent false-success. No modal dialog is ever shown.</summary>
        public static void CommitOrThrow(Transaction tx)
        {
            var status = tx.Commit();
            if (status == TransactionStatus.RolledBack)
            {
                var msg = (_lastErrors != null && _lastErrors.Count > 0)
                    ? string.Join("; ", _lastErrors.Distinct())
                    : "Revit rejected the change and rolled it back.";
                throw new InvalidOperationException($"Revit error — {msg}");
            }
        }

        /// <summary>Rolls <paramref name="tx"/> back ONLY if it is still open.
        /// Use in place of a bare <c>tx.RollBack()</c> in a catch block — see
        /// the file header for the two ways the bare call destroys the real
        /// error or reports a committed write as failed.</summary>
        public static void SafeRollBack(Transaction tx)
        {
            try
            {
                if (ShouldRollBack(tx.GetStatus())) tx.RollBack();
            }
            catch
            {
                // Revit already ended it — there is nothing to undo, and an
                // exception from HERE would replace the one we are unwinding.
            }
        }

        /// <summary>TransactionGroup counterpart of <see cref="SafeRollBack(Transaction)"/>.
        /// Assimilate() ends the group, so a throw after it hits the same trap.</summary>
        public static void SafeRollBack(TransactionGroup group)
        {
            try
            {
                if (ShouldRollBack(group.GetStatus())) group.RollBack();
            }
            catch { }
        }

        /// <summary>Commit <paramref name="items"/> one at a time inside ONE
        /// TransactionGroup — the shape every per-item-tolerant mutate tool
        /// uses. An item that throws is handed to <paramref name="onFailure"/>
        /// and the loop continues, so one refusal does not cost the others;
        /// Assimilate() then collapses the survivors into a single undo step.
        ///
        /// <paramref name="commitOne"/> owns its own Transaction (StartSwallowing
        /// / CommitOrThrow / SafeRollBack); this wrapper owns only the GROUP.
        ///
        /// The failure list stays in the caller's closure on purpose: each
        /// tool's failure row has a different key set, and those keys are a
        /// wire contract with the backend.</summary>
        public static void ForEachInGroup<T>(
            Document doc, string groupName, IEnumerable<T> items,
            Action<T> commitOne, Action<T, Exception> onFailure)
        {
            using var group = new TransactionGroup(doc, groupName);
            group.Start();
            try
            {
                foreach (var item in items)
                {
                    try { commitOne(item); }
                    catch (Exception ex) { onFailure(item, ex); }
                }
                group.Assimilate();
            }
            catch { SafeRollBack(group); throw; }
        }

        /// <summary>The whole decision, separated so it is unit-testable without
        /// a live Document (a real Transaction needs one).</summary>
        internal static bool ShouldRollBack(TransactionStatus status)
            => status == TransactionStatus.Started;

        internal static void RecordErrors(IEnumerable<string> errors)
            => (_lastErrors ??= new List<string>()).AddRange(errors);
    }

    /// <summary>Deletes warnings during a transaction so a write can never block
    /// on a modal failure dialog. On any ERROR it captures the message text and
    /// returns <c>ProceedWithRollBack</c> — so a hard error ("cannot be ignored")
    /// rolls the transaction back cleanly instead of popping a modal on the UI
    /// thread. Mirrors CodeExecutor.__FailHandler.</summary>
    public sealed class SwallowWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            int swallowed = 0;
            var errors = new List<string>();
            foreach (var f in a.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning)
                {
                    a.DeleteWarning(f);
                    swallowed++;
                }
                else
                {
                    errors.Add(f.GetDescriptionText());
                }
            }

            if (swallowed > 0)
                Debug.WriteLine(
                    $"[BinaVibe][warnings] swallowed {swallowed} warning(s) during '{a.GetTransactionName()}'");

            if (errors.Count > 0)
            {
                TxGuard.RecordErrors(errors);
                Debug.WriteLine(
                    $"[BinaVibe][errors] rolling back '{a.GetTransactionName()}': {string.Join("; ", errors)}");
                return FailureProcessingResult.ProceedWithRollBack;
            }

            return FailureProcessingResult.Continue;
        }
    }
}
