using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>
    /// Builds viewport highlight markers for a tool result — a port of app.jsx highlightsFor.
    /// Coordinates are % of the active view rect. The visual overlay that renders these lands
    /// in Task 15; this data function is used by the viewmodel from Task 5 onward.
    /// </summary>
    public static class CopilotHighlights
    {
        public static List<HighlightMarker> For(string toolId)
        {
            switch (toolId)
            {
                case "rename":
                case "rename-level-prefix":
                    return new List<HighlightMarker>
                    {
                        new HighlightMarker { XPct = 22, YPct = 32, OldLabel = "Level 1", NewLabel = "L1", Color = "#a16207" },
                        new HighlightMarker { XPct = 50, YPct = 28, OldLabel = "Level 2", NewLabel = "L2", Color = "#a16207" },
                        new HighlightMarker { XPct = 78, YPct = 32, OldLabel = "Level 3", NewLabel = "L3", Color = "#a16207" },
                        new HighlightMarker { XPct = 36, YPct = 62, OldLabel = "Level Roof", NewLabel = "LRoof", Color = "#a16207" },
                    };
                case "select":
                    return new List<HighlightMarker>
                    {
                        new HighlightMarker { XPct = 25, YPct = 70, Color = "#6d28d9", Dot = true },
                        new HighlightMarker { XPct = 35, YPct = 70, Color = "#6d28d9", Dot = true },
                        new HighlightMarker { XPct = 50, YPct = 70, Color = "#6d28d9", Dot = true },
                        new HighlightMarker { XPct = 62, YPct = 70, Color = "#6d28d9", Dot = true },
                        new HighlightMarker { XPct = 73, YPct = 70, Color = "#6d28d9", Dot = true },
                    };
                case "set-param":
                case "set-frr-corridor":
                    return new List<HighlightMarker>
                    {
                        new HighlightMarker { XPct = 28, YPct = 55, Color = "#15803d", NewLabel = "FRR-60" },
                        new HighlightMarker { XPct = 50, YPct = 55, Color = "#15803d", NewLabel = "FRR-60" },
                        new HighlightMarker { XPct = 72, YPct = 55, Color = "#15803d", NewLabel = "FRR-60" },
                    };
                case "walls-missing-frr":
                case "ubbl-rooms":
                    return new List<HighlightMarker>
                    {
                        new HighlightMarker { XPct = 30, YPct = 50, Color = "#b91c1c", NewLabel = "No FRR", Warn = true },
                        new HighlightMarker { XPct = 65, YPct = 52, Color = "#b91c1c", NewLabel = "No FRR", Warn = true },
                        new HighlightMarker { XPct = 48, YPct = 75, Color = "#b91c1c", NewLabel = "No FRR", Warn = true },
                    };
                case "tag-walls":
                    return new List<HighlightMarker>
                    {
                        new HighlightMarker { XPct = 22, YPct = 58, Color = "#4338ca", NewLabel = "W-01" },
                        new HighlightMarker { XPct = 42, YPct = 58, Color = "#4338ca", NewLabel = "W-02" },
                        new HighlightMarker { XPct = 60, YPct = 58, Color = "#4338ca", NewLabel = "W-03" },
                        new HighlightMarker { XPct = 78, YPct = 58, Color = "#4338ca", NewLabel = "W-04" },
                    };
                default:
                    return new List<HighlightMarker>();
            }
        }
    }
}
