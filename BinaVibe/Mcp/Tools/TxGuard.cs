// TxGuard — starts a Revit Transaction with warning auto-suppression so a
// mutation can never hard-freeze the UI thread on a modal warning dialog
// ("walls overlap", "room not enclosed", …). A warning dialog blocks the UI
// thread waiting on a human who often can't even see it (the Copilot pane has
// focus) → an UNBOUNDED freeze. SwallowWarnings deletes warnings during the
// transaction so the commit proceeds silently instead of waiting.
//
// Warnings only — ERRORS still roll the transaction back (a real failure must
// fail). Deleted warnings are logged to DebugView so suppression is never
// silent.
//
// Promoted from WarmupHandler.cs (the warm-up edit proved the pattern; this
// makes it shared so every real Mutators/BatchExecutor write gets it too).

using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class TxGuard
    {
        /// <summary>Starts <paramref name="tx"/> with a SwallowWarnings failure
        /// preprocessor attached. Use in place of a bare <c>tx.Start()</c> for
        /// any write that could trip a Revit warning dialog.</summary>
        public static void StartSwallowing(Transaction tx)
        {
            tx.Start();
            var fho = tx.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(new SwallowWarnings());
            fho.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fho);
        }
    }

    /// <summary>Deletes warnings during a transaction so a write can never block
    /// on a modal failure dialog. Errors are left untouched (they still roll the
    /// transaction back). Logs how many warnings it swallowed.</summary>
    public sealed class SwallowWarnings : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            int swallowed = 0;
            foreach (var f in a.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning)
                {
                    a.DeleteWarning(f);
                    swallowed++;
                }
            }
            if (swallowed > 0)
                System.Diagnostics.Debug.WriteLine(
                    $"[BinaVibe][warnings] swallowed {swallowed} warning(s) during '{a.GetTransactionName()}'");
            return FailureProcessingResult.Continue;
        }
    }
}
