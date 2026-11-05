#nullable enable
// bind.view_id (AI-269 reliability 4.1): a tool request that depends on the active view carries the id of the view the
// turn started in. Just before the add-in runs it, the live active view is compared; a different view means nothing of
// that request runs and every call answers view_changed. Revit-free: the live view comes from the snapshot the router
// already reads. Absent bind, a view-less turn, or a snapshot that could not be read (stale) = run as before.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ViewBinding
    {
        /// <summary>Null when the request may run; otherwise the sentence for the model naming both views.</summary>
        public static string? Mismatch(ToolBindDto? bind, SnapshotModel? live, SnapshotView? startView)
        {
            if (bind?.ViewId is not long want) return null;
            if (live == null || live.Stale || live.View == null) return null;          // cannot tell: do not block
            if (live.View.Id == want) return null;
            var was = startView != null && startView.Id == want && !string.IsNullOrWhiteSpace(startView.Name) ? "'" + startView.Name + "'" : "view " + want;
            var now = string.IsNullOrWhiteSpace(live.View.Name) ? "view " + live.View.Id : "'" + live.View.Name + "'";
            return "The drafter is in " + now + " now, not in " + was + " where this turn started, and this call depends on the active view. "
                   + "Nothing was run. Ask the drafter to go back to " + was + " (or start a new turn here), then send it again.";
        }

        /// <summary>One failure envelope per call: code view_changed, status rejected, nothing run.</summary>
        public static Envelope[] Refusals(ToolRequestDto request, string message)
        {
            var phase = request.Kind == ToolRequestKinds.Write ? EnvelopePhase.Preview : EnvelopePhase.Read;
            return (request.Calls ?? new List<ToolCallDto>()).Select(_ =>
            {
                var e = EnvelopeFactory.Failed(ErrorCodes.ViewChanged, message);
                e.Phase = phase;
                e.Status = EnvelopeStatus.Rejected;
                return e;
            }).ToArray();
        }
    }
}
