using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>
    /// SilenceJoinFailures that also writes down why. Warnings are deleted and errors resolved
    /// exactly as SilenceJoinFailures does, but before an error is resolved - which for "can't
    /// cut instance out of its host" deletes the door or window - Revit's text is noted in the
    /// PlacedLedger against every element it names, so the Review can say why the element is
    /// gone. Noting never breaks a build: any failure to read is swallowed.
    /// </summary>
    public sealed class RecordingFailures : IFailuresPreprocessor
    {
        private static readonly Guid CannotCut = new Guid(PlacedLedger.CannotCutGuid);

        private readonly PlacedLedger _ledger;

        public RecordingFailures(PlacedLedger ledger)
        {
            _ledger = ledger;
        }

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

                Record(failure);

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

        private void Record(FailureMessageAccessor failure)
        {
            if (_ledger == null) return;
            try
            {
                string text = failure.GetDescriptionText();
                bool cannotCut = failure.GetFailureDefinitionId()?.Guid == CannotCut;

                foreach (ElementId id in failure.GetFailingElementIds())
                    _ledger.NoteFailure(ElementIdCompat.ToLong(id), text, cannotCut);
                foreach (ElementId id in failure.GetAdditionalElementIds())
                    _ledger.NoteFailure(ElementIdCompat.ToLong(id), text, cannotCut);
            }
            catch
            {
                // The note is a nicety; the resolution below is what the build needs.
            }
        }
    }
}
