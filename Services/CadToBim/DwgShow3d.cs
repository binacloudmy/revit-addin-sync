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
        private static readonly Regex NumberRx = new Regex(@"\b(\d{1,3})\b");

        public static bool IsAsk(string text) => !string.IsNullOrWhiteSpace(text) && AskRx.IsMatch(text);

        /// <summary>The plans read_dwg_plan lists (same filter as DwgPlanReader.Build).</summary>
        public static List<DwgPlan> Listed(DwgPlanSheet sheet) =>
            sheet.Plans.Where(p => p.Cluster.Walls.Count >= DwgPlanReader.MinPlanWalls && !p.IsDetail).ToList();

        /// <summary>The plan the text names: its n ("tunjuk 3D 2"), else words of its title, else the only plan.
        /// null with <paramref name="problem"/> set when none or several match.</summary>
        public static DwgPlan Pick(DwgPlanSheet sheet, string text, out string problem)
        {
            problem = null;
            List<DwgPlan> listed = sheet == null ? new List<DwgPlan>() : Listed(sheet);
            if (listed.Count == 0)
            {
                problem = "Belum ada pelan DWG dibaca. Lampirkan DWG dan minta Copilot senaraikan pelannya dahulu.";
                return null;
            }

            string rest = AskRx.Replace(text ?? "", "").Trim();
            Match n = NumberRx.Match(rest);
            if (n.Success)
            {
                DwgPlan byN = listed.FirstOrDefault(p => p.N == int.Parse(n.Groups[1].Value));
                if (byN != null) return byN;
            }
            if (rest.Length > 0)
            {
                var byTitle = listed.Where(p => p.Title != null && p.Title.IndexOf(rest, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (byTitle.Count == 1) return byTitle[0];
            }
            if (listed.Count == 1 && !n.Success) return listed[0];

            problem = "Pelan yang mana? Taip contohnya \"tunjuk 3D " + listed[0].N + "\". Pelan: "
                      + string.Join("; ", listed.Take(10).Select(p => p.N + " " + Name(p)));
            return null;
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
