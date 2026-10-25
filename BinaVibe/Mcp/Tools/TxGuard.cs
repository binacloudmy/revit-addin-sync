// TxGuard — starts a Revit Transaction with warning auto-suppression AND
// error-safe rollback so a mutation can never (a) hard-freeze the UI thread on
// a modal failure dialog, nor (b) silently report success after Revit rolled
// the change back.
//
// Warnings ("walls overlap", "room not enclosed", …) block the UI thread on a
// modal dialog a human often can't even see (the Copilot pane has focus) → an
// UNBOUNDED freeze. SwallowWarnings deletes them so the commit proceeds.
//
// ERRORS ("Instance(s) of … not cutting anything", "cannot be ignored") used to
// leak past the preprocessor (it returned Continue), so Revit escalated to a
// modal error dialog — the same freeze, one severity up. Now we mirror the
// codegen path's __FailHandler (CodeExecutor.cs): on any error we capture the
// text and return ProceedWithRollBack, so Revit rolls back silently instead of
// popping a dialog. CommitOrThrow then converts that rollback into a clean
// exception (surfaced to the agent as a tool error) rather than a false success.
//
// Promoted from WarmupHandler.cs (the warm-up edit proved the pattern; this
// makes it shared so every real Mutators/BatchExecutor write gets it too).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Guard;

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

        /// <summary>Commits a StartSwallowing'd transaction. Any status other
        /// than <c>Committed</c> throws: a rollback (a hard error the
        /// preprocessor turned into ProceedWithRollBack) carries the captured
        /// Revit error text; Pending/Error/anything else says Revit did not
        /// commit. Never a silent false-success, never a modal dialog. The only
        /// <c>Commit(</c> allowed under BinaVibe/Mcp/Tools and BinaVibe/Copilot
        /// (TxGuardContractTests).</summary>
        public static void CommitOrThrow(Transaction tx)
        {
            var status = tx.Commit();
            if (status != TransactionStatus.Committed)
                throw new InvalidOperationException($"Revit error — {NotCommittedText(status)}");
        }

        /// <summary>Commits an edit scope (StairsEditScope, …) with the same
        /// SwallowWarnings preprocessor. EditScope.Commit reports no status:
        /// an error the preprocessor captured means Revit rolled the merged
        /// transaction back, so that throws with the error text.</summary>
        public static void CommitOrThrow(EditScope scope)
        {
            _lastErrors = null;
            scope.Commit(new SwallowWarnings());
            if (_lastErrors != null && _lastErrors.Count > 0)
                throw new InvalidOperationException($"Revit error — {string.Join("; ", _lastErrors.Distinct())}");
        }

        private static string NotCommittedText(TransactionStatus status)
        {
            if (_lastErrors != null && _lastErrors.Count > 0) return string.Join("; ", _lastErrors.Distinct());
            return status == TransactionStatus.RolledBack
                ? "Revit rejected the change and rolled it back."
                : $"Revit did not commit the change (transaction status {status}).";
        }

        internal static void RecordErrors(IEnumerable<string> errors)
            => (_lastErrors ??= new List<string>()).AddRange(errors);
    }

    /// <summary>Deletes warnings during a transaction so a write can never block
    /// on a modal failure dialog. On any ERROR it captures the message text and
    /// returns <c>ProceedWithRollBack</c> — so a hard error ("cannot be ignored")
    /// rolls the transaction back cleanly instead of popping a modal on the UI
    /// thread. Mirrors CodeExecutor.__FailHandler.
    /// Every failure (warning or error: text, severity, definition id, element
    /// ids) is first recorded into <see cref="FailureLog.Current"/>, so the
    /// envelope reports what was deleted (spec §7.3.2). FailureScope's
    /// FailuresProcessing backstop runs the same <see cref="Process"/>.</summary>
    public sealed class SwallowWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a) => Process(a);

        public static FailureProcessingResult Process(FailuresAccessor a)
        {
            int swallowed = 0;
            var errors = new List<string>();
            foreach (var f in a.GetFailureMessages())
            {
                bool isWarning = f.GetSeverity() == FailureSeverity.Warning;
                var text = f.GetDescriptionText();
                Record(f, text, isWarning);
                if (isWarning)
                {
                    a.DeleteWarning(f);
                    swallowed++;
                }
                else
                {
                    errors.Add(text);
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

        // Never lets logging break failure handling: a throw here would leave
        // Revit to show the very modal this preprocessor exists to prevent.
        private static void Record(FailureMessageAccessor f, string text, bool isWarning)
        {
            try
            {
                var ids = new List<long>();
                foreach (var id in f.GetFailingElementIds()) ids.Add((long)id.Value);
                foreach (var id in f.GetAdditionalElementIds()) ids.Add((long)id.Value);
                FailureLog.Current.Add(text, isWarning ? WarningSeverity.Warning : WarningSeverity.Error,
                    f.GetFailureDefinitionId()?.Guid.ToString(), ids);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BinaVibe][warnings] could not log a failure: {ex.Message}");
            }
        }
    }
}
