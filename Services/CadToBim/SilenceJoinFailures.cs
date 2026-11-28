using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>
    /// Keeps bulk wall creation from raising a dialog per failure. Every warning is deleted
    /// outright; every error is resolved the way the dialog's own default button would (which
    /// can delete the failing elements), so the run continues instead of stopping on the first
    /// of a thousand. AI-642: each one is recorded in the FailureLog first (grouped by
    /// FailureDefinitionId, joins apart), so the pane can show them in Review instead of losing them.
    /// </summary>
    public sealed class SilenceJoinFailures : IFailuresPreprocessor
    {
        private readonly FailureLog _log;

        public SilenceJoinFailures(FailureLog log = null) { _log = log; }

        // Every FailureDefinitionId on the join families of BuiltInFailures; read once by reflection.
        private static readonly Lazy<HashSet<Guid>> JoinIds = new Lazy<HashSet<Guid>>(() =>
            new[] { typeof(BuiltInFailures.JoinElementsFailures), typeof(BuiltInFailures.AutoJoinFailures), typeof(BuiltInFailures.WallJoinFailures) }
                .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                .Where(p => p.PropertyType == typeof(FailureDefinitionId))
                .Select(p => { try { return ((FailureDefinitionId)p.GetValue(null)).Guid; } catch { return Guid.Empty; } })
                .Where(g => g != Guid.Empty)
                .ToHashSet());

        private static readonly Lazy<Guid> OffAxis = new Lazy<Guid>(() => BuiltInFailures.InaccurateFailures.InaccurateWall.Guid);
        private static readonly Lazy<Guid> Overlap = new Lazy<Guid>(() => BuiltInFailures.OverlapFailures.WallsOverlap.Guid);

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            IList<FailureMessageAccessor> failures = accessor.GetFailureMessages();
            if (failures.Count == 0) return FailureProcessingResult.Continue;

            bool resolved = false;

            foreach (FailureMessageAccessor failure in failures)
            {
                bool warning = failure.GetSeverity() == FailureSeverity.Warning;
                bool resolvable = !warning && failure.HasResolutions();
                Record(failure, error: !warning, resolved: resolvable);

                if (warning)
                {
                    accessor.DeleteWarning(failure);
                    continue;
                }

                if (resolvable)
                {
                    accessor.ResolveFailure(failure);
                    resolved = true;
                }
            }

            return resolved
                ? FailureProcessingResult.ProceedWithCommit
                : FailureProcessingResult.Continue;
        }

        private void Record(FailureMessageAccessor failure, bool error, bool resolved)
        {
            if (_log == null) return;
            try
            {
                Guid id = failure.GetFailureDefinitionId().Guid;
                bool join = JoinIds.Value.Contains(id);
                _log.Add(id, failure.GetDescriptionText(), join, error, resolved,
                    failure.GetFailingElementIds().Select(ElementIdCompat.ToLong),
                    id == OffAxis.Value ? "offaxis" : id == Overlap.Value ? "overlap" : null);
            }
            catch
            {
                // Recording must never stop the build; the failure is still handled below.
            }
        }
    }
}
