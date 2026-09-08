using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>
    /// Clears the join complaints that bulk wall creation raises. Warnings are deleted
    /// outright; errors are resolved the way the dialog's own default button would, so the
    /// run continues instead of stopping on the first of a thousand.
    /// </summary>
    public sealed class SilenceJoinFailures : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            IList<FailureMessageAccessor> failures = accessor.GetFailureMessages();
            if (failures.Count == 0) return FailureProcessingResult.Continue;

            bool resolved = false;

            foreach (FailureMessageAccessor failure in failures)
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    accessor.DeleteWarning(failure);
                    continue;
                }

                if (failure.HasResolutions())
                {
                    accessor.ResolveFailure(failure);
                    resolved = true;
                }
            }

            return resolved
                ? FailureProcessingResult.ProceedWithCommit
                : FailureProcessingResult.Continue;
        }
    }
}
