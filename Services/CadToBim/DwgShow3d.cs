// AI-605 demo: "tunjuk 3D" in the Copilot after read_dwg_plan listed the plans. The add-in
// answers it locally (no bina-ai tool, so no tool-description tokens): one plan from the
// sheet read_dwg_plan read last, built by the #272 builder on the lowest level, where the
// drawing has it (StoreyMode.KeepPosition). Revit-free; the pane hands the request on.

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Cad2Bim;

namespace RevitWebAppSync.Services.CadToBim
{
    public static class DwgShow3d
    {
        private static readonly Regex AskRx = new Regex(@"^\s*(tunjuk|show)\s*(me\s+)?(the\s+)?3\s*d\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NumberRx = new Regex(@"^(pelan\s+|plan\s+)?(\d{1,3})$", RegexOptions.IgnoreCase);

        /// <summary>The plans read_dwg_plan lists (same filter as DwgPlanReader.Build).</summary>
        public static List<DwgPlan> Listed(DwgPlanSheet sheet) =>
            sheet.Plans.Where(p => p.Cluster.Walls.Count >= DwgPlanReader.MinPlanWalls && !p.IsDetail).ToList();

        /// <summary>True when the add-in answers the text itself: "tunjuk 3D" / "show 3D" after read_dwg_plan read a sheet,
        /// followed by nothing, a plan number, or words of a listed plan's title. Anything else ("show 3D view of the roof")
        /// goes to the engine. On true, <paramref name="plan"/> is the pick, or null with <paramref name="problem"/> to show.</summary>
        public static bool Intercepts(DwgPlanSheet sheet, string text, out DwgPlan plan, out string problem)
        {
            plan = null;
            problem = null;
            if (string.IsNullOrWhiteSpace(text) || sheet == null) return false;
            Match ask = AskRx.Match(text);
            if (!ask.Success) return false;
            List<DwgPlan> listed = Listed(sheet);
            if (listed.Count == 0) return false;

            string rest = text.Substring(ask.Length).Trim().TrimEnd('.', '?', '!').Trim();
            if (rest.Length == 0)
            {
                if (listed.Count == 1) plan = listed[0];
            }
            else
            {
                // A title before a number: "aras 1" is the plan titled ARAS 1, not plan n=1. A bare "1" is n=1
                // unless a plan is titled exactly "1"; the number counts only as the whole remainder.
                var exact = listed.Where(p => string.Equals(Name(p).Trim(), rest, System.StringComparison.OrdinalIgnoreCase)).ToList();
                Match n = NumberRx.Match(rest);
                var byTitle = exact.Count > 0 || n.Success ? exact
                    : listed.Where(p => Name(p).IndexOf(rest, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (byTitle.Count == 1) plan = byTitle[0];
                else if (byTitle.Count == 0 && n.Success) plan = listed.FirstOrDefault(p => p.N == int.Parse(n.Groups[2].Value));
                else if (byTitle.Count == 0) return false;   // not a plan: let the engine answer
            }
            if (plan != null) return true;

            problem = "Pelan yang mana? Taip contohnya \"tunjuk 3D " + listed[0].N + "\". Pelan: "
                      + string.Join("; ", listed.Take(10).Select(p => p.N + " " + Name(p)));
            return true;
        }

        public static string Name(DwgPlan p) => p.Title ?? p.Level ?? "Pelan " + p.N;

        /// <summary>The one line above Ya / Tidak.</summary>
        public static string Summary(DwgPlan p) =>
            "Bina 3D: " + Name(p) + " (pelan " + p.N + ") di aras terendah model, kedudukan ikut lukisan: "
            + p.Cluster.Walls.Count + " dinding, " + p.Doors.Count + " pintu, " + p.Windows.Count + " tingkap, "
            + p.Rooms.Count + " bilik.";

        /// <summary>The same request the pane's Confirm sends, for this one plan on one level.</summary>
        public static BuildRequest Request(string drawingPath, DwgPlan p, CadToBimSettings settings) => new BuildRequest
        {
            Kind = BuildJobKind.Build,
            Target = BuildTarget.AddToProject,
            DrawingPath = drawingPath,
            Walls = p.Cluster.Walls.ToList(),
            Openings = p.Doors.Concat(p.Windows).Where(o => o.Wall != null).ToList(),   // as the pane sends them
            Spaces = p.Rooms.ToList(),
            Storeys = StoreyMode.KeepPosition,
            LevelId = null,   // the lowest level
            TemplatePath = string.IsNullOrWhiteSpace(settings.TemplatePath) ? null : settings.TemplatePath,
            WindowSillMm = settings.WindowSillMm,
            DoorHeightMm = settings.DoorHeightMm,
            WindowHeightMm = settings.WindowHeightMm,
            JoinWalls = settings.JoinWalls,
            BuiltWallIds = new List<long>(),
        };
    }
}
