#nullable enable
// Drafter-facing names and one-line reasons for what a preview changes beyond
// the elements a tool aimed at (live-test L16; spec §7.5). Revit-free.
//
//   W3 keys elements with no category by class ("class:ParameterFilterElement").
//   Those get a friendly name here ("View filters"); a class not listed is
//   "an internal Revit setting", never a raw class name.
//   Some categories change along with an edit because Revit regenerates them
//   (room boundaries, pipe and duct centre lines). Those get a short reason so
//   a routine edit does not read as an unexplained side effect.

using System;
using System.Collections.Generic;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ChangeNames
    {
        private static readonly Dictionary<string, (string En, string Ms)> Classes =
            new Dictionary<string, (string En, string Ms)>(StringComparer.Ordinal)
            {
                ["ParameterFilterElement"] = ("View filters", "Penapis pandangan"),
                ["SunAndShadowSettings"] = ("Sun settings", "Tetapan matahari"),
                ["FillPatternElement"] = ("Fill patterns", "Corak isian"),
                ["LinePatternElement"] = ("Line patterns", "Corak garisan"),
                ["SketchPlane"] = ("Sketch planes", "Satah lakaran"),
                ["ViewSheetSet"] = ("Sheet sets", "Set helaian"),
            };

        private static readonly Dictionary<string, (string En, string Ms)> Reasons =
            new Dictionary<string, (string En, string Ms)>(StringComparer.OrdinalIgnoreCase)
            {
                ["Rooms"] = ("room boundaries recompute", "sempadan bilik dikira semula"),
                ["Spaces"] = ("space boundaries recompute", "sempadan ruang dikira semula"),
                ["Areas"] = ("area boundaries recompute", "sempadan kawasan dikira semula"),
                ["Center Line"] = ("pipe/duct centre lines regenerate", "garis tengah paip/salur dijana semula"),
                ["Center Lines"] = ("pipe/duct centre lines regenerate", "garis tengah paip/salur dijana semula"),
                ["Sun Path"] = ("Revit adds it with the view", "Revit menambahnya bersama pandangan"),
            };

        /// <summary>The key's class name when it is a "class:&lt;Name&gt;" key, else null.</summary>
        public static string? ClassOf(string? key)
        {
            var k = (key ?? "").Trim();
            if (!k.StartsWith(ConfirmCardModel.ClassKeyPrefix, StringComparison.Ordinal)) return null;
            return k.Substring(ConfirmCardModel.ClassKeyPrefix.Length).Trim();
        }

        /// <summary>A friendly name for a class key, or null when the class is not listed.</summary>
        public static string? FriendlyClass(string? className, bool ms)
        {
            if (string.IsNullOrWhiteSpace(className)) return null;
            return Classes.TryGetValue(className!.Trim(), out var n) ? (ms ? n.Ms : n.En) : null;
        }

        /// <summary>Why a category changes along with an edit, or null.</summary>
        public static string? ReasonFor(string? category, bool ms)
        {
            if (string.IsNullOrWhiteSpace(category)) return null;
            return Reasons.TryGetValue(category!.Trim(), out var r) ? (ms ? r.Ms : r.En) : null;
        }

        public static string InternalSetting(bool ms) => ms ? "tetapan dalaman Revit" : "an internal Revit setting";
    }
}
