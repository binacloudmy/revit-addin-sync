using System.Collections.Generic;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot
{
    /// <summary>
    /// The Copilot icon set — a faithful port of the inline SVGs in
    /// design_handoff_revit_copilot/components/shared.jsx (Icons). All paths use a 24×24
    /// viewbox. SVG &lt;circle&gt;/&lt;rect&gt;/&lt;line&gt;/&lt;polyline&gt;/&lt;polygon&gt; primitives are
    /// converted to path mini-language (arcs/lines) so each icon is a single Geometry.
    /// Most icons are stroked (1.6px); the few in <see cref="Filled"/> are fill-rendered.
    /// </summary>
    public static class CopilotIcons
    {
        // Icon name → path-data string (WPF Geometry.Parse compatible).
        public static readonly Dictionary<string, string> Paths = new Dictionary<string, string>
        {
            ["send"]        = "M5,12 h14 M13,6 l6,6 -6,6",
            ["sparkle"]     = "M12,3 v4 M12,17 v4 M3,12 h4 M17,12 h4 M5.6,5.6 l2.8,2.8 M15.6,15.6 l2.8,2.8 M5.6,18.4 l2.8,-2.8 M15.6,8.4 l2.8,-2.8",
            ["sparkleSolid"]= "M12,2 l2.09,6.26 L20,9.27 l-5,4.87 L16.18,22 12,18.27 7.82,22 9,14.14 4,9.27 l5.91,-1.01 L12,2 z",
            ["close"]       = "M6,6 l12,12 M18,6 L6,18",
            ["chevronDown"] = "M6,9 l6,6 6,-6",
            ["chevronRight"]= "M9,6 l6,6 -6,6",
            ["chevronLeft"] = "M15,6 l-6,6 6,6",
            ["plus"]        = "M12,5 v14 M5,12 h14",
            ["search"]      = "M4,11 A7,7 0 1 0 18,11 A7,7 0 1 0 4,11 Z M21,21 l-4.3,-4.3",
            ["code"]        = "M16,18 l6,-6 -6,-6 M8,6 l-6,6 6,6",
            ["play"]        = "M6,4 l14,8 -14,8 V4 z",
            ["copy"]        = "M9,9 H22 V22 H9 Z M5,15 V5 a2,2 0 0 1 2,-2 h10",
            ["history"]     = "M3,12 a9,9 0 1 0 9,-9 a9,9 0 0 0 -6.4,2.6 L3,8 M3,3 v5 h5 M12,7 v5 l4,2",
            ["bookmark"]    = "M19,21 l-7,-5 -7,5 V5 a2,2 0 0 1 2,-2 h10 a2,2 0 0 1 2,2 z",
            ["layers"]      = "M12,2 L2,7 12,12 22,7 12,2 Z M2,17 L12,22 22,17 M2,12 L12,17 22,12",
            ["cube"]        = "M21,16 V8 a2,2 0 0 0 -1,-1.7 l-7,-4 a2,2 0 0 0 -2,0 l-7,4 A2,2 0 0 0 3,8 v8 a2,2 0 0 0 1,1.7 l7,4 a2,2 0 0 0 2,0 l7,-4 A2,2 0 0 0 21,16 z M3.3,7 L12,12 20.7,7 M12,22 L12,12",
            ["door"]        = "M4,21 h16 M6,3 v18 h12 V3 z M13.4,13 A0.6,0.6 0 1 0 14.6,13 A0.6,0.6 0 1 0 13.4,13 Z",
            ["wall"]        = "M3,4 H21 V20 H3 Z M3,10 H21 M3,15 H21 M9,4 V10 M14,10 V15 M11,15 V20",
            ["table"]       = "M3,4 H21 V20 H3 Z M3,10 H21 M3,15 H21 M11,4 V20",
            ["chart"]       = "M4,20 V10 M10,20 V4 M16,20 V14 M22,20 H2",
            ["filter"]      = "M22,3 H2 l8,9.5 V19 l4,2 v-8.5 L22,3 z",
            ["link"]        = "M10,13 a5,5 0 0 0 7,0 l4,-4 a5,5 0 0 0 -7,-7 l-1,1 M14,11 a5,5 0 0 0 -7,0 l-4,4 a5,5 0 0 0 7,7 l1,-1",
            ["menu"]        = "M3,6 h18 M3,12 h18 M3,18 h18",
            ["more"]        = "M11,5 A1,1 0 1 0 13,5 A1,1 0 1 0 11,5 Z M11,12 A1,1 0 1 0 13,12 A1,1 0 1 0 11,12 Z M11,19 A1,1 0 1 0 13,19 A1,1 0 1 0 11,19 Z",
            ["check"]       = "M5,12 l5,5 L20,7",
            ["warning"]     = "M12,9 v4 M12,17 v0.01 M10.3,3.86 L1.82,18 a2,2 0 0 0 1.71,3 h16.94 a2,2 0 0 0 1.71,-3 L13.71,3.86 a2,2 0 0 0 -3.42,0 z",
            ["pin"]         = "M12,2 l3,6 6,1 -4.5,4.5 L18,20 l-6,-3 -6,3 1.5,-6.5 L3,9 l6,-1 z",
            ["undo"]        = "M3,7 v6 h6 M21,17 a9,9 0 0 0 -15,-6.7 L3,13",
            ["selection"]   = "M3,9 V5 a2,2 0 0 1 2,-2 h4 M21,9 V5 a2,2 0 0 0 -2,-2 h-4 M3,15 v4 a2,2 0 0 0 2,2 h4 M21,15 v4 a2,2 0 0 1 -2,2 h-4",
            ["attach"]      = "M21,12.5 l-8.5,8.5 a5.5,5.5 0 0 1 -7.8,-7.8 l9,-9 a3.7,3.7 0 0 1 5.2,5.2 l-9,9 a1.8,1.8 0 0 1 -2.6,-2.6 l8.3,-8.3",
            ["mic"]         = "M9,2 H15 V14 H9 Z M19,11 a7,7 0 0 1 -14,0 M12,18 V22",
            ["bot"]         = "M3,8 H21 V20 H3 Z M8,14 A1,1 0 1 0 10,14 A1,1 0 1 0 8,14 Z M14,14 A1,1 0 1 0 16,14 A1,1 0 1 0 14,14 Z M12,4 V8 M11,3 A1,1 0 1 0 13,3 A1,1 0 1 0 11,3 Z",
        };

        // Icons rendered with Fill rather than Stroke.
        public static readonly HashSet<string> Filled = new HashSet<string>
        {
            "sparkleSolid", "play", "more",
        };

        /// <summary>Parsed, frozen geometry for an icon name; null if unknown.</summary>
        public static Geometry Get(string name)
        {
            if (string.IsNullOrEmpty(name) || !Paths.TryGetValue(name, out var d))
                return null;
            var g = Geometry.Parse(d);
            if (g.CanFreeze) g.Freeze();
            return g;
        }

        public static bool IsFilled(string name) => name != null && Filled.Contains(name);
    }
}
