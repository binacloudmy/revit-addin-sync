// AI-638: the ONE state object behind the Check screens (2-7), plus the reader snapshot it is
// built from. Revit-free and WPF-free so the Tests project links it. Every count the pane shows
// is computed from here, never stored (epic correction #2).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services.CadToBim;

namespace RevitWebAppSync.UI.CadToBim.Check
{
    public enum CheckScreen { Empty, Loading, Error, Preflight, Consultant, Gate1, Gate2, Questions, Rows }

    /// <summary>Source badge. Green: Project/DwgHeader/Drawing/TitleBlock. Amber: JkrDefault/AiGuess/DrawingSays. Red: Missing. Grey: Pending.</summary>
    public enum Src { Project, DwgHeader, Drawing, TitleBlock, JkrDefault, AiGuess, DrawingSays, Missing, Pending }

    public enum GateState { Pending, Passed, Blocked }

    // shortcut: mirrors AI-647's DwgWarning {Kind, Name, Detail, Count}; swap for that type at the rebase onto AI-647.
    public sealed class SheetWarning
    {
        public string Kind { get; set; }    // xref_missing | xref_unloaded | proxy_entity | image_missing | host_warning
        public string Name { get; set; }
        public string Detail { get; set; }
        public int Count { get; set; } = 1;
    }

    public sealed class LayerSnap { public string Name; public int Count; public string Role; }

    public sealed class PlanSnap
    {
        public int N { get; set; }
        public string Title { get; set; }
        public string Level { get; set; }
        public string Elevation { get; set; }
        public bool IsDetail { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public int Walls { get; set; }
        public int Doors { get; set; }
        public int Windows { get; set; }
        public int Rooms { get; set; }
        /// <summary>Wall thickness rounded to 10 mm → count.</summary>
        public Dictionary<int, int> WidthsMm { get; set; } = new Dictionary<int, int>();
        /// <summary>Wall centrelines x1,y1,x2,y2 in mm, only inside this plan's box (the crop).</summary>
        public List<double[]> Segs { get; set; } = new List<double[]>();
        [JsonIgnore] public BoxMm Box => new BoxMm(MinX, MinY, MaxX, MaxY);
    }

    /// <summary>What one DWG read returned, in the plain form the pane, the fixtures and the tests share.</summary>
    public sealed class SheetSnapshot
    {
        public string File { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Engine { get; set; } = "ACadSharp";
        public string HeaderUnit { get; set; } = "";
        public double UsedMmPerUnit { get; set; } = 1;
        public bool UnitsPlausible { get; set; } = true;
        public bool UnitsNeedConfirmation { get; set; }
        public List<LayerSnap> Layers { get; set; } = new List<LayerSnap>();
        public List<SheetWarning> Warnings { get; set; } = new List<SheetWarning>();
        public List<string> OpeningBlocks { get; set; } = new List<string>();
        public List<PlanSnap> Plans { get; set; } = new List<PlanSnap>();
        public int DetailsSkipped { get; set; }
        /// <summary>Entities in model space (what the pre-flight row counts).</summary>
        public int ModelSpaceEntities { get; set; }
        /// <summary>The DWG header's model-space extents in mm (EXTMIN/EXTMAX x the resolved unit). The Revit
        /// link's bounding box covers the same geometry, so the two tie reader coordinates to the model.</summary>
        public double ExtMinX { get; set; }
        public double ExtMinY { get; set; }
        public double ExtMaxX { get; set; }
        public double ExtMaxY { get; set; }
        // an unset header holds +-1e20 sentinels: treated as no extents
        [JsonIgnore] public BoxMm? Extents => ExtMaxX > ExtMinX && ExtMaxY > ExtMinY && Math.Abs(ExtMinX) < 1e12 && Math.Abs(ExtMaxX) < 1e12
            && Math.Abs(ExtMinY) < 1e12 && Math.Abs(ExtMaxY) < 1e12 ? new BoxMm(ExtMinX, ExtMinY, ExtMaxX, ExtMaxY) : (BoxMm?)null;


        /// <summary>The only adapter from the reader. Same plan filter as read_dwg_plan's listing.</summary>
        public static SheetSnapshot From(DwgPlanSheet sheet, long sizeBytes, string engine = "ACadSharp")
        {
            var units = DwgPlanReader.UnitsVerdict(sheet.HeaderUnit, sheet.HeaderMmPerUnit, sheet.UsedMmPerUnit);
            var settings = new CadToBimSettings();
            var snap = new SheetSnapshot
            {
                File = sheet.File,
                SizeBytes = sizeBytes,
                Engine = engine,
                HeaderUnit = sheet.HeaderUnit,
                UsedMmPerUnit = sheet.UsedMmPerUnit,
                UnitsPlausible = (bool)units["plausible"],
                UnitsNeedConfirmation = units.TryGetValue("needs_confirmation", out object nc) && nc is bool b && b,
                Layers = sheet.Census.OrderByDescending(kv => kv.Value)
                    .Select(kv => new LayerSnap { Name = kv.Key, Count = kv.Value, Role = DwgPlanReader.RoleOf(kv.Key, settings).Role }).ToList(),
                // shortcut: today's warnings are strings; AI-647 makes them structured (map Kind/Name/Detail/Count 1:1 at rebase).
                Warnings = sheet.Warnings.Select(w => new SheetWarning { Kind = "host_warning", Detail = w }).ToList(),
                OpeningBlocks = sheet.OpeningBlocks.Select(o => o.Name).ToList(),
                DetailsSkipped = sheet.Plans.Count(p => p.Cluster.Walls.Count >= DwgPlanReader.MinPlanWalls && p.IsDetail),
            };
            foreach (DwgPlan p in sheet.Plans.Where(p => p.Cluster.Walls.Count >= DwgPlanReader.MinPlanWalls && !p.IsDetail))
            {
                var c = p.Cluster;
                snap.Plans.Add(new PlanSnap
                {
                    N = p.N, Title = p.Title, Level = p.Level, Elevation = p.Elevation,
                    MinX = c.MinX, MinY = c.MinY, MaxX = c.MaxX, MaxY = c.MaxY,
                    Walls = c.Walls.Count, Doors = p.Doors.Count, Windows = p.Windows.Count, Rooms = p.Rooms.Count,
                    WidthsMm = c.Walls.GroupBy(w => (int)(Math.Round(w.Thickness / 10.0) * 10)).ToDictionary(g => g.Key, g => g.Count()),
                    Segs = c.Walls.Select(w => w.Centerline)
                        .Where(s => c.MinX <= Math.Min(s.P1.x, s.P2.x) && Math.Max(s.P1.x, s.P2.x) <= c.MaxX
                                 && c.MinY <= Math.Min(s.P1.y, s.P2.y) && Math.Max(s.P1.y, s.P2.y) <= c.MaxY)
                        .Select(s => new[] { Math.Round(s.P1.x), Math.Round(s.P1.y), Math.Round(s.P2.x), Math.Round(s.P2.y) }).ToList(),
                });
            }
            return snap;
        }

        /// <summary>Read a DWG for the Check pane: the read_dwg_plan pipeline plus the model-space entity count.</summary>
        // shortcut: reads through CadRenderSource like DwgPlanReader.ReadFile (no cache); switch to AI-647's IDwgReader at the rebase.
        public static SheetSnapshot Read(string path, CadToBimSettings settings)
        {
            ACadSharp.CadDocument document = Cad2Bim.Services.CadRenderSource.Read(path);
            DwgPlanSheet sheet = DwgPlanReader.Read(document, settings);
            sheet.File = Path.GetFileName(path);
            SheetSnapshot snap = From(sheet, new FileInfo(path).Length);
            snap.ModelSpaceEntities = document.Entities.Count();
            double k = sheet.UsedMmPerUnit;
            (snap.ExtMinX, snap.ExtMinY) = (document.Header.ModelSpaceExtMin.X * k, document.Header.ModelSpaceExtMin.Y * k);
            (snap.ExtMaxX, snap.ExtMaxY) = (document.Header.ModelSpaceExtMax.X * k, document.Header.ModelSpaceExtMax.Y * k);
            return snap;
        }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
        public static SheetSnapshot FromJson(string json) => JsonConvert.DeserializeObject<SheetSnapshot>(json);
    }

    public sealed class LevelInfo { public string Name; public double ElevationMm; }

    /// <summary>The open Revit document, read in a valid API context (the ribbon command).</summary>
    public sealed class TargetInfo
    {
        public string DocTitle { get; set; }
        public bool Workshared { get; set; }
        public List<LevelInfo> Levels { get; set; } = new List<LevelInfo>();
        /// <summary>The existing main level: the lowest one. Never created by the pane (AI-631, 2026-11-27).</summary>
        [JsonIgnore] public string MainLevel => Levels.OrderBy(l => l.ElevationMm).FirstOrDefault()?.Name;
    }

    public sealed class Question
    {
        public string Title { get; set; }
        public string Evidence { get; set; }
        public List<string> Options { get; set; } = new List<string>();
        public string Answer { get; set; }
        public int PlanN { get; set; }
        /// <summary>Source badge shown after the answer (e.g. "Drawing").</summary>
        public string Source { get; set; }
    }

    public sealed class ConsultantInfo { public string Name { get; set; } public string Note { get; set; } public string Rules { get; set; } }

    /// <summary>Engine output that does not exist yet. Null fields = pending (the product default).
    /// Filled only from a fixture JSON in UiHarness/tests, or behind the cadToBim.uiFixtures dev flag.</summary>
    public sealed class EngineShortcuts
    {
        public ConsultantInfo Consultant { get; set; }         // shortcut: fixture, replaced by AI-634 (title-block reader)
        public List<Question> Questions { get; set; }          // shortcut: fixture, replaced by AI-632 / AI-629 / AI-630
        public int CandidateCount { get; set; }                // shortcut: fixture, replaced by AI-632
        public string LegendRow { get; set; }                  // shortcut: fixture, replaced by pending #8 (legend reader)
        public string DimensionRow { get; set; }               // shortcut: fixture, replaced by AI-631 (dimension check)

        public static EngineShortcuts FromJson(string json) => JsonConvert.DeserializeObject<EngineShortcuts>(json);
    }

    public sealed class PlanMap
    {
        public PlanSnap Plan { get; set; }
        public string TargetLevel { get; set; }
        /// <summary>The main plan (its storey names the main level): the only one built by default (owner, 2026-11-27).</summary>
        public bool IsMain { get; set; }
        /// <summary>Why it is the main plan: "level" (its storey names the build level), "title" (its title says storey 1), "first" (first on the sheet).</summary>
        public string MainBy { get; set; }
        /// <summary>The user mapped it in Gate 1 (Edit mapping); only then is a non-main plan built.</summary>
        public bool Mapped { get; set; }
        public bool Built => IsMain || Mapped;
        /// <summary>What the Gate 1 level picker shows: empty until mapped, so picking the same level still counts.</summary>
        public string PickedLevel => Mapped || IsMain ? TargetLevel : null;
    }

    public sealed class CheckState
    {
        public CheckScreen Screen { get; set; } = CheckScreen.Empty;
        public string Error { get; set; }
        public SheetSnapshot Sheet { get; set; }
        public TargetInfo Target { get; set; } = new TargetInfo();
        public EngineShortcuts Engine { get; set; } = new EngineShortcuts();
        public List<PlanMap> Plans { get; set; } = new List<PlanMap>();
        public GateState Gate1 { get; set; }
        public GateState Gate2 { get; set; }
        public bool ConsultantConfirmed { get; set; }
        public double WallHeightMm { get; set; } = 3000;
        /// <summary>Set when the Revit preview (underlay link / zoom) failed; shown as one line on screen 2.</summary>
        public string PreviewError { get; set; }
        /// <summary>The preview link was made (the bridge said so).</summary>
        public bool Linked { get; set; }
        /// <summary>A one-line note from the Revit side, shown on screen 2.</summary>
        public string PreviewNotice { get; set; }

        public List<Question> Questions => Engine.Questions ?? new List<Question>();
        public string BuildLevel => Target.MainLevel ?? "Level 1";

        // derived, never stored
        public IEnumerable<PlanMap> BuiltPlans => Plans.Where(p => p.Built && p.TargetLevel == BuildLevel);
        public int Walls => BuiltPlans.Sum(p => p.Plan.Walls);
        public int Doors => BuiltPlans.Sum(p => p.Plan.Doors);
        public int NotBuilt => Plans.Count(p => !p.Built);
        public int Answered => Questions.Count(q => q.Answer != null);
        public bool CanBuild => Gate1 == GateState.Passed && Gate2 == GateState.Passed && Answered == Questions.Count;

        /// <summary>What passed on an assumption (amber), for Review's named-assumptions list (AI-642). Derived, never stored.</summary>
        public List<string> Assumptions
        {
            get
            {
                var list = new List<string>();
                if (Gate2 == GateState.Passed && Engine.DimensionRow == null)
                    list.Add("Scale from the DWG header only (" + Sheet?.HeaderUnit + "); the dimension check is not built yet");
                return list;
            }
        }

        /// <summary>Every plan on the existing main level; the drawing's level name is evidence only.</summary>
        private static readonly System.Text.RegularExpressions.Regex StoreyRx =
            new System.Text.RegularExpressions.Regex(@"(?:ARAS|LEVEL|LANTAI|TINGKAT)\s*([0-9A-Z]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // Compact Revit level names: L1, L01, L-1, LV1, LVL 1.
        private static readonly System.Text.RegularExpressions.Regex CompactRx =
            new System.Text.RegularExpressions.Regex(@"^\s*(?:LVL|LV|L)\s*[-_]?\s*0*(\d+[A-Z]?)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Ground words on the Revit side. Rule: ground = storey "1", because on JKR sheets "ARAS 1" is the ground floor
        // (ASWARA). "ARAS BAWAH" (lower ground, SEK.DAIF) stays its own storey "BAWAH" through StoreyRx.
        private static readonly System.Text.RegularExpressions.Regex GroundRx =
            new System.Text.RegularExpressions.Regex(@"^\s*(?:GF|G|GROUND(?:\s+FLOOR)?|GRADE)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>"ARAS 1" / "Level 1" / "L01" / "LVL 1" / "Ground" → "1"; "ARAS 1A" → "1A"; null when the text names no storey.</summary>
        public static string Storey(string text)
        {
            text ??= "";
            if (GroundRx.IsMatch(text)) return "1";
            var m = StoreyRx.Match(text);
            if (!m.Success) m = CompactRx.Match(text);
            if (!m.Success) return null;
            string v = m.Groups[1].Value.ToUpperInvariant().TrimStart('0');   // L01 → 1
            return v.Length == 0 ? "0" : v;
        }

        public static CheckState For(SheetSnapshot sheet, TargetInfo target, EngineShortcuts engine)
        {
            var s = new CheckState { Sheet = sheet, Target = target ?? new TargetInfo(), Engine = engine ?? new EngineShortcuts(), Screen = CheckScreen.Preflight };
            s.Plans = sheet.Plans.Select(p => new PlanMap { Plan = p, TargetLevel = s.BuildLevel }).ToList();
            // the main plan: its storey names the build level (ARAS 1 → L1); else the plan whose title says storey 1;
            // else the first plan on the sheet
            string main = Storey(s.BuildLevel);
            PlanMap pick = main == null ? null : s.Plans.FirstOrDefault(m => Storey(m.Plan.Level) == main);
            string by = "level";
            if (pick == null) { pick = s.Plans.FirstOrDefault(m => (Storey(m.Plan.Level) ?? Storey(m.Plan.Title)) == "1"); by = "title"; }
            if (pick == null) { pick = s.Plans.FirstOrDefault(); by = "first"; }
            if (pick != null) { pick.IsMain = true; pick.MainBy = by; }
            s.Gate2 = sheet.UnitsPlausible && !sheet.UnitsNeedConfirmation ? GateState.Pending : GateState.Blocked;
            return s;
        }
    }

    /// <summary>cadToBim.ui ("legacy" | "check", default legacy) and cadToBim.uiFixtures (dev flag: path to an engine-shortcuts JSON).</summary>
    public static class CheckUiConfig
    {
        // shortcut: read raw from config.json so CadToBimSettings.cs (AI-647's file) stays untouched; becomes two
        // CadToBimSettings fields at the rebase onto AI-647. The settings window's Save drops the raw key, which
        // can only happen in legacy mode and leaves it legacy.
        public static (bool Check, string Fixtures) Read(string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return (false, null);
                var node = JObject.Parse(File.ReadAllText(configPath))[CadToBimSettings.ConfigKey] as JObject;
                return (string.Equals((string)node?["ui"], "check", StringComparison.OrdinalIgnoreCase), (string)node?["uiFixtures"]);
            }
            catch (Exception)
            {
                return (false, null);   // unreadable config = legacy, same policy as CadToBimSettings.Load
            }
        }
    }
}
