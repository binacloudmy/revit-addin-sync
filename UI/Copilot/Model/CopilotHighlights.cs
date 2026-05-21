using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Viewport highlight markers.
    ///
    /// The prototype placed canned markers (fixed %-coords + canned labels like "Level 1→L1").
    /// That is mock data, so it is intentionally NOT emitted here. Truthful markers require
    /// projecting real element bounding-box centers to screen coordinates against the active
    /// UIView — that projection isn't implemented yet, so we return none rather than fake
    /// positions. (Vetted "select"/"open" already give real native Revit highlighting via
    /// uidoc.Selection/ShowElements.)
    ///
    /// TODO(real-highlights): have the executor return affected ElementIds, then project each
    /// element's bbox center to screen via the active view transform + GetWindowRectangle.
    /// </summary>
    public static class CopilotHighlights
    {
        public static List<HighlightMarker> For(string toolId) => new List<HighlightMarker>();
    }
}
