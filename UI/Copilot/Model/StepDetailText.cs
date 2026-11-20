#nullable enable
// r17 (live e8083027, 21 Nov, laptop): a schedule made in one prompt showed "Ran: ... (added 6, deleted 0; ...)" under Steps > Details,
// and the tester read "deleted" on a create. The step line drops a zero count; a real count is always kept. Words only, pure, so a test pins them.
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class StepDetailText
    {
        private static readonly Regex ZeroDelete = new Regex(@",\s*deletes?\s+0(?=\s*[;)])", RegexOptions.CultureInvariant);
        private static readonly Regex ZeroAdd = new Regex(@"\(\s*adds?\s+0\s*,\s*", RegexOptions.CultureInvariant);

        /// <summary>The tool summary for the step row: "(added 6, deleted 0; ...)" reads "(added 6; ...)", "(added 0, deleted 2; ...)"
        /// reads "(deleted 2; ...)"; every other text is unchanged. The envelope the server reads keeps the whole summary.</summary>
        public static string Of(string? summary)
        {
            var s = summary ?? "";
            return ZeroAdd.Replace(ZeroDelete.Replace(s, ""), "(");
        }

        private static readonly Dictionary<string, string> Malay = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "Schedules", "jadual" }, { "Schedule", "jadual" }, { "Sheets", "helaian" }, { "Sheet", "helaian" }, { "Views", "pandangan" }, { "View", "pandangan" },
            { "Doors", "pintu" }, { "Door", "pintu" }, { "Windows", "tingkap" }, { "Window", "tingkap" }, { "Walls", "dinding" }, { "Wall", "dinding" },
            { "Levels", "aras" }, { "Level", "aras" }, { "Rooms", "bilik" }, { "Room", "bilik" }, { "Floors", "lantai" }, { "Floor", "lantai" },
            { "Ceilings", "siling" }, { "Ceiling", "siling" }, { "Roofs", "bumbung" }, { "Roof", "bumbung" }, { "Grids", "grid" }, { "Grid", "grid" },
        };

        /// <summary>A created headline ("1 Schedule", "3 Sheets · 1 Level") with the category names a Malay drafter reads ("1 jadual"):
        /// the Undo notice of a Malay turn (live 646bdf7f: "1 Schedule removed." in a Malay reply). An unknown name stays as Revit says it.</summary>
        public static string MalayNames(string headline) =>
            Regex.Replace(headline ?? "", @"(\d+) ([A-Z][A-Za-z]*(?: [A-Z][A-Za-z]*)*)", m => Malay.TryGetValue(m.Groups[2].Value, out var w) ? m.Groups[1].Value + " " + w : m.Value);
    }
}
