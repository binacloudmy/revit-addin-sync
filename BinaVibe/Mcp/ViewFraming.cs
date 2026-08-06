// ViewFraming — zoom a view onto a rectangle AFTER Revit has actually switched to it.
//
// Why this exists rather than a ZoomAndCenterRectangle call at the end of the tool:
//
//   uidoc.ActiveView = view;                       // Revit DEFERS the real switch
//   foreach (var uiv in uidoc.GetOpenUIViews()) …  // still the OLD view set
//
// Setting ActiveView inside an API context does not take effect until that context
// ends, so any UIView obtained on the next line belongs to the view we are leaving.
// Three fixes were attempted against the symptom before the cause was found — a
// space-planning Build kept landing with the school half off-screen, and each
// attempt zoomed a view the user was no longer looking at (2026-08-06).
//
// So: record the request, and let the Idling handler apply it on the next callback,
// by which time the switch has happened. One-shot — it clears itself whether it
// succeeds or fails, because a framing request that retries forever would fight the
// user for control of their own camera.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp
{
    internal static class ViewFraming
    {
        private static readonly object _gate = new object();
        private static ElementId _viewId;
        private static XYZ _min, _max;
        private static DateTime _expiresUtc;

        /// <summary>Ask for <paramref name="viewId"/> to be framed on the rectangle
        /// once Revit has switched to it. Replaces any earlier pending request —
        /// the newest Build is the one the user is looking at.</summary>
        internal static void Request(ElementId viewId, XYZ min, XYZ max)
        {
            if (viewId == null || min == null || max == null) return;
            lock (_gate)
            {
                _viewId = viewId;
                _min = min;
                _max = max;
                // A TIME budget, not a call budget.
                //
                // This was "8 attempts" and never once fired: the Idling handler
                // calls SetRaiseWithoutDelay while a request is pending, so Revit
                // raises Idling as fast as it can and all eight were spent within
                // milliseconds — long before the deferred ActiveView switch had
                // happened. The request then cleared itself and the camera never
                // moved (four rounds of wrong diagnoses, 2026-08-06).
                _expiresUtc = DateTime.UtcNow.AddSeconds(10);
            }
        }

        internal static bool HasPending
        {
            get { lock (_gate) { return _viewId != null && DateTime.UtcNow < _expiresUtc; } }
        }

        /// <summary>Apply a pending request if the view is now open. Safe to call on
        /// every Idling callback; never throws.</summary>
        internal static void TryApply(UIApplication app)
        {
            ElementId viewId;
            XYZ min, max;
            lock (_gate)
            {
                if (_viewId == null) return;
                if (DateTime.UtcNow >= _expiresUtc) { Clear(); return; }   // gave it long enough
                viewId = _viewId;
                min = _min;
                max = _max;
            }

            try
            {
                var uidoc = app?.ActiveUIDocument;
                if (uidoc == null) return;
                // Only frame the view the user is actually on. If they navigated
                // away between the Build and this callback, their choice wins.
                if (uidoc.ActiveView == null || uidoc.ActiveView.Id != viewId) return;

                foreach (var uiv in uidoc.GetOpenUIViews())
                {
                    if (uiv.ViewId != viewId) continue;
                    uiv.ZoomAndCenterRectangle(min, max);
                    lock (_gate) { Clear(); }
                    return;
                }
            }
            catch
            {
                lock (_gate) { Clear(); }
            }
        }

        private static void Clear()
        {
            _viewId = null;
            _min = null;
            _max = null;
            _expiresUtc = DateTime.MinValue;
        }
    }
}
