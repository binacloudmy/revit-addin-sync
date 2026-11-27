// read_dwg_plan (AI-607): the Revit-free half. Reads an attached DWG with ACadSharp
// through the same Cad2Bim pipeline the CAD to BIM pane runs (CadToBimViewModel.Detect:
// survey, wall-layer focus, face pairing, outline walls, dedupe, rooms, symbol openings,
// plan clusters), then shapes a size-budgeted answer for the model: the plans on the
// sheet to pick from, and per picked plan walls by thickness, doors, windows, outline
// and rooms. Nothing here touches Revit; the file never leaves the PC.
//
// Units are never fixed silently: when the header's unit makes the sheet an impossible
// size (elec.dwg said inches for a mm drawing, 25.4x), Units.Resolve reads the geometry
// in the inferred unit and the answer says so, with needs_confirmation, so the copilot
// asks the user before anything is built.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.Entities;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using Cad2Bim;
using RevitWebAppSync.UI.CadToBim;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>One plan cluster with what fell inside it.</summary>
    public sealed class DwgPlan
    {
        public int N;
        public PlanCluster Cluster;
        public string Title;
        public string Level;
        public string Elevation;
        public List<Opening> Doors = new List<Opening>();
        public List<Opening> Windows = new List<Opening>();
        public List<Space> Rooms = new List<Space>();
        public int LooseEnds;
        /// <summary>No title and small or few walls: a detail, a stair strip or an elevation, not a floor plan.</summary>
        public bool IsDetail;
    }

    /// <summary>Everything read from one drawing, in millimetres.</summary>
    public sealed class DwgPlanSheet
    {
        public string File = "";
        public string HeaderUnit = "";
        public double? HeaderMmPerUnit;
        public double UsedMmPerUnit = 1.0;
        public double SheetWidthMm;
        public double SheetHeightMm;
        public List<DwgPlan> Plans = new List<DwgPlan>();
        public IReadOnlyDictionary<string, int> Census = new Dictionary<string, int>();
        public List<(string Name, int Count)> OpeningBlocks = new List<(string, int)>();
        public List<string> Warnings = new List<string>();
    }

    public static class DwgPlanReader
    {
        public const string NoDwgMessage =
            "Attach a DWG first: no drawing is attached to this chat. Ask the user to attach the .dwg with the paperclip, then call read_dwg_plan again.";

        /// <summary>At most this many plans may be detailed in one call (AI-605: the user picks 1-3).</summary>
        public const int MaxPickedPlans = 3;

        /// <summary>The list call's next (read_dwg_plan step 3): the list answers a list question; a question about a plan's content reads it.</summary>
        public const string ListNext = "The plans above answer a list question: reply with all of them by n; ask nothing. "
            + "If the user asked what is in a plan, call read_dwg_plan with plans.";

        /// <summary>A cluster with fewer walls is a title block, a detail or a stray fragment, not a floor plan.</summary>
        public const int MinPlanWalls = 4;   // a one-room hut is still a plan

        /// <summary>Row caps that keep the answer small on a 37-plan sheet.</summary>
        public const int MaxPlanRows = 40;
        public const int MaxLayerRows = 20;
        public const int MaxRoomNames = 15;
        public const int MaxWidthRows = 8;

        /// <summary>Labels this close outside a plan's walls still belong to it (titles sit under the plan).</summary>
        public const double TitleReachMm = 3000.0;

        /// <summary>A plan's own label sits under it or at its bottom edge, at most this far from its walls.</summary>
        public const double LabelReachMm = CadClassifier.PlanGapMm;
        public const double LabelSlackMm = 1000.0;

        /// <summary>An untitled cluster below either size is a detail, not a floor plan (stair strips are 1-7 m wide).</summary>
        public const int DetailMaxWalls = 20;
        public const double DetailMinSideMm = 8000.0;

        // ───────── reading ─────────

        // One-entry cache: the listing call and the detail call that follows read the same file.
        private static readonly object CacheLock = new object();
        private static string _cacheKey;
        private static DwgPlanSheet _cacheSheet;

        /// <summary>Read the file (cached by path and write time).</summary>
        public static DwgPlanSheet ReadFile(string path, CadToBimSettings settings)
        {
            string key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            lock (CacheLock)
            {
                if (_cacheKey == key && _cacheSheet != null) return _cacheSheet;
            }

            CadDocument document = Cad2Bim.Services.CadRenderSource.Read(path);
            DwgPlanSheet sheet = Read(document, settings);
            sheet.File = Path.GetFileName(path);

            lock (CacheLock)
            {
                _cacheKey = key;
                _cacheSheet = sheet;
            }
            return sheet;
        }

        /// <summary>The CadToBimViewModel.Detect pipeline on an in-memory document, kept per plan.</summary>
        public static DwgPlanSheet Read(CadDocument document, CadToBimSettings settings)
        {
            var sheet = new DwgPlanSheet
            {
                HeaderUnit = document.Header.InsUnits.ToString(),
                HeaderMmPerUnit = Units.FromHeader(document),
            };
            var noRoles = new Dictionary<string, LayerRole>();

            CadModel survey = ModelSource.Read(document, settings.ToLayerFilter());
            sheet.Census = survey.LayerCensus;
            sheet.UsedMmPerUnit = survey.Scale;

            int readable = survey.Segments.Count + survey.Outlines.Count + survey.Arcs.Count;
            if (CadSourceVerdict.IsRefused(
                    document.Classes.Select(c => (c.DxfName, c.IsAnEntity, c.InstanceCount)), readable, out string aecWarning))
                throw new NotSupportedException(CadToBimViewModel.UnsupportedSourceMessage);
            if (!string.IsNullOrEmpty(aecWarning)) sheet.Warnings.Add(aecWarning);
            if (readable == 0)
            {
                sheet.Warnings.Add("No linework in model space: the plan may be drawn only in a layout (paper space) or an xref.");
                return sheet;
            }

            List<string> wallLayers = survey.LayerCensus.Keys
                .Where(l => CadToBimViewModel.RoleOf(l, settings, noRoles) == LayerRole.Wall).ToList();
            CadModel model = survey;
            if (wallLayers.Count > 0)
            {
                LayerFilter focused = settings.ToLayerFilter();
                focused.Include.AddRange(wallLayers);
                focused.Include.AddRange(survey.LayerCensus.Keys
                    .Where(l => CadToBimViewModel.RoleOf(l, settings, noRoles) == LayerRole.Opening));
                model = ModelSource.Read(document, focused);
            }
            else
            {
                sheet.Warnings.Add("No layer is named for walls; walls were paired from every layer, so expect false walls.");
            }

            CadToBimViewModel.ApplyEngineThresholds(settings, settings.SMinMm, settings.SMaxMm);

            List<Segment> wallSegments = model.Segments
                .Where(s => s.Layer.Length == 0 || CadToBimViewModel.RoleOf(s.Layer, settings, noRoles) == LayerRole.Wall)
                .ToList();
            if (wallSegments.Count == 0) wallSegments = model.Segments.ToList();
            wallSegments = CadClassifier.MergeCollinearSegments(wallSegments);
            List<Wall> walls = CadClassifier.ClassifyWalls(wallSegments);
            var wallFaces = new HashSet<Segment>(wallSegments);
            walls.AddRange(CadClassifier.ClassifyWallsElsewhere(walls, model.Segments.Where(s => !wallFaces.Contains(s)).ToList()));
            walls.AddRange(CadClassifier.WallsFromOutlines(model.Outlines));
            walls = CadClassifier.DeduplicateWalls(walls);

            // Same calls as CadToBimViewModel.Elaborate; the graph is kept for loose ends.
            WallGraph graph = CadClassifier.CreateTopologicalPoints(walls);
            List<Space> spaces = CadClassifier.ClassifySpaces(graph, model.Texts);
            CadClassifier.SplitWalls(walls, spaces);
            // Swings come from the survey (every layer but the excluded ones): a quarter arc of leaf
            // radius is a door wherever it was filed, and drawings file them on layers like "dr" that
            // no door hint matches (366 swings on SEK.DAIF). Same scale, or the radii would disagree.
            IReadOnlyList<Cad2Bim.Arc> swings = survey.Scale == model.Scale ? survey.Arcs : model.Arcs;
            List<Opening> openings = CadClassifier.ClassifyOpeningsFromSymbols(
                walls, swings, model.Segments.Where(s => settings.IsWindowLayer(s.Layer)).ToList());

            // resplit: plans stacked on one sheet only come apart once their neighbours are cut away (ASWARA).
            List<PlanCluster> clusters = CadClassifier.ClusterPlans(walls, model.Texts, resplit: true);
            AttachNearbyTitles(clusters, model.Texts);
            Dictionary<PlanCluster, TextElement> labels = PickLabels(clusters, model.Texts);
            var claimed = new HashSet<TextElement>(labels.Values);

            if (walls.Count > 0)
            {
                sheet.SheetWidthMm = clusters.Max(c => c.MaxX) - clusters.Min(c => c.MinX);
                sheet.SheetHeightMm = clusters.Max(c => c.MaxY) - clusters.Min(c => c.MinY);
            }

            for (int i = 0; i < clusters.Count; i++)
            {
                PlanCluster c = clusters[i];
                var plan = new DwgPlan { N = i + 1, Cluster = c };
                (plan.Title, plan.Level, plan.Elevation) = LevelText(c.Texts.Where(t => !claimed.Contains(t)).Select(t => t.Text));
                if (labels.TryGetValue(c, out TextElement label)) (plan.Title, plan.Level) = (label.Text, Level(label.Text));
                plan.IsDetail = plan.Title == null && (c.Walls.Count < DetailMaxWalls || Math.Min(c.Width, c.Height) < DetailMinSideMm);
                foreach (Opening o in openings.Where(o => c.Contains(o.Position)))
                    (o.IsDoor ? plan.Doors : plan.Windows).Add(o);
                plan.Rooms.AddRange(spaces.Where(s => s.Boundary.Count > 0 && c.Contains(Centroid(s.Boundary))));
                plan.LooseEnds = graph.Nodes.Count(n => n.Degree < 2 && c.Contains(n.Position));
                sheet.Plans.Add(plan);
            }

            NumberDuplicates(sheet.Plans.Where(p => !p.IsDetail).ToList());
            string missed = UnclaimedLabels(model.Texts, claimed, sheet.Plans.Where(p => !p.IsDetail).Select(p => p.Cluster).ToList());
            if (missed != null) sheet.Warnings.Add(missed);

            sheet.OpeningBlocks = OpeningBlocks(document, settings);
            return sheet;
        }

        // Plan labels are short capitals that open with a plan or level word: "ARAS 2 (ASRAMA213 ORG)",
        // "PELAN BUMBUNG". Mixed case is a note ("Aras Lantai"), and notes are not titles.
        private static readonly Regex LabelLeadRx = new Regex(
            @"^\s*(PELAN|FLOOR\s+PLAN|ARAS|LANTAI|TINGKAT|LEVEL|ROOF|BUMBUNG|(GROUND|FIRST|SECOND|THIRD|UPPER|LOWER)\s+FLOOR)\b");

        public static bool IsPlanLabel(string text) =>
            !string.IsNullOrWhiteSpace(text) && text.Length <= 60 && text == text.ToUpperInvariant()
            && (LabelLeadRx.IsMatch(text) || TitleRx.IsMatch(text));

        /// <summary>
        /// Each plan's own label: the nearest plan label under it or at its bottom edge. Closest
        /// pairs are settled first and a label serves one plan only, so a plan cannot borrow its
        /// neighbour's label (ASWARA's ARAS 2 had taken the BUMBUNG roof notes beside it).
        /// </summary>
        public static Dictionary<PlanCluster, TextElement> PickLabels(List<PlanCluster> clusters, IReadOnlyList<TextElement> texts)
        {
            var pairs = new List<(double Gap, PlanCluster Plan, TextElement Label)>();
            foreach (TextElement t in texts.Where(t => IsPlanLabel(t.Text)))
                foreach (PlanCluster c in clusters)
                {
                    if (t.P1.y > c.MinY + LabelSlackMm) continue;   // above the bottom edge: inside or over the plan
                    double dx = Math.Max(0, Math.Max(c.MinX - Math.Max(t.P1.x, t.P2.x), Math.Min(t.P1.x, t.P2.x) - c.MaxX));
                    double dy = Math.Max(0, c.MinY - Math.Max(t.P1.y, t.P2.y));
                    double gap = Math.Sqrt((dx * dx) + (dy * dy));
                    if (gap <= LabelReachMm) pairs.Add((gap, c, t));
                }

            var picked = new Dictionary<PlanCluster, TextElement>();
            var used = new HashSet<TextElement>();
            foreach (var p in pairs.OrderBy(p => p.Gap))
            {
                if (picked.ContainsKey(p.Plan) || used.Contains(p.Label)) continue;
                picked[p.Plan] = p.Label;
                used.Add(p.Label);
            }
            return picked;
        }

        /// <summary>Two plans with one name get "(2)", "(3)" in listing order, so the user can tell them apart.</summary>
        public static void NumberDuplicates(List<DwgPlan> plans)
        {
            foreach (var group in plans.Where(p => (p.Title ?? p.Level) != null).GroupBy(p => p.Title ?? p.Level).Where(g => g.Count() > 1).ToList())
            {
                int k = 1;
                foreach (DwgPlan p in group.OrderBy(p => p.N).Skip(1)) p.Title = group.Key + " (" + ++k + ")";
            }
        }

        /// <summary>
        /// A warning naming the plan labels that sit outside every plan found and belong to none: their plans were missed.
        /// Only labels drawn as large as the titles that were used count, and not lines of a stacked list: a title
        /// block's "SITE PLAN / KEY PLAN" and an elevation's small level tags are not plans (elec.dwg had 15 such).
        /// </summary>
        public static string UnclaimedLabels(IReadOnlyList<TextElement> texts, ISet<TextElement> claimed, List<PlanCluster> plans)
        {
            static double H(TextElement t) => t.P2.y - t.P1.y;
            double minHeight = claimed.Count == 0 ? 0 : claimed.Min(H) - 1e-6;
            List<TextElement> labels = texts.Where(t => IsPlanLabel(t.Text)).ToList();
            bool InList(TextElement t) => labels.Any(o => o != t && Math.Abs(o.P1.x - t.P1.x) < H(t) && Math.Abs(o.P1.y - t.P1.y) < 2 * H(t));
            List<string> names = labels
                .Where(t => !claimed.Contains(t) && H(t) >= minHeight && !plans.Any(c => c.Contains(t.P1)) && !InList(t))
                .Select(t => t.Text).Distinct().ToList();
            if (names.Count == 0) return null;
            return names.Count + " plan label" + (names.Count == 1 ? "" : "s") + " sit outside every plan found, so "
                + (names.Count == 1 ? "its plan" : "their plans") + " may be missing or listed as details: "
                + string.Join(", ", names.Take(5).Select(n => "\"" + n + "\"")) + (names.Count > 5 ? ", ..." : "") + ".";
        }

        /// <summary>Titles sit under or beside a plan, outside its walls: a label outside every plan joins the nearest one within reach.</summary>
        private static void AttachNearbyTitles(List<PlanCluster> clusters, IReadOnlyList<TextElement> texts)
        {
            foreach (TextElement t in texts)
            {
                if (clusters.Any(c => c.Contains(t.P1))) continue;
                PlanCluster best = null;
                double bestGap = TitleReachMm;
                foreach (PlanCluster c in clusters)
                {
                    double dx = Math.Max(0, Math.Max(c.MinX - t.P1.x, t.P1.x - c.MaxX));
                    double dy = Math.Max(0, Math.Max(c.MinY - t.P1.y, t.P1.y - c.MaxY));
                    double gap = Math.Sqrt((dx * dx) + (dy * dy));
                    if (gap <= bestGap) { best = c; bestGap = gap; }
                }
                best?.Texts.Add(t);
            }
        }

        private static Cad2Bim.Point Centroid(List<Cad2Bim.Point> ring) =>
            new Cad2Bim.Point(ring.Average(p => p.x), ring.Average(p => p.y));

        private static readonly Regex TitleRx = new Regex(@"^\s*(PELAN|FLOOR\s+PLAN|PLAN\b)|\bPLAN\s*$", RegexOptions.IgnoreCase);
        // Most specific first: "PELAN LANTAI ARAS 1" is level ARAS 1, not LANTAI ARAS.
        private static readonly Regex[] LevelRxs =
        {
            new Regex(@"\bARAS\s+(BAWAH|BUMBUNG|ATAS|\d+[A-Z]?)\b", RegexOptions.IgnoreCase),
            new Regex(@"\b((GROUND|FIRST|SECOND|THIRD|UPPER|LOWER|ROOF)\s+FLOOR|TINGKAT\s+\w+|LEVEL\s+\w+|LANTAI\s+\w+|ROOF|BUMBUNG)\b", RegexOptions.IgnoreCase),
        };
        private static readonly Regex ElevationRx = new Regex(
            @"\b(A\.?\s?L\.?|F\.?F\.?L\.?|R\.?L\.?)\s*[:=]?\s*[+\-]?\d+(?:[.,]\d+)?", RegexOptions.IgnoreCase);

        /// <summary>Title, level and elevation text from a plan's labels (ARAS 1, A.L:45.650). Null when not found.</summary>
        public static (string Title, string Level, string Elevation) LevelText(IEnumerable<string> labels)
        {
            string title = null, level = null, elevation = null;
            foreach (string raw in labels)
            {
                string s = (raw ?? "").Trim();
                if (s.Length == 0 || s.Length > 120) continue;
                if (title == null && TitleRx.IsMatch(s)) title = s;
                level ??= Level(s);
                if (elevation == null) { Match m = ElevationRx.Match(s); if (m.Success) elevation = m.Value.Replace(" ", ""); }
            }
            if (title != null) level = Level(title) ?? level;   // the title's own level beats a stray label
            return (title, level, elevation);
        }

        private static string Level(string s)
        {
            foreach (Regex rx in LevelRxs)
            {
                Match m = rx.Match(s);
                if (m.Success) return m.Value.ToUpperInvariant();
            }
            return null;
        }

        /// <summary>Top-level block inserts on door/window layers or named for doors/windows (names only; a block carries no size).</summary>
        private static List<(string Name, int Count)> OpeningBlocks(CadDocument document, CadToBimSettings settings)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Insert insert in document.Entities.OfType<Insert>())
            {
                string name = insert.Block?.Name ?? "";
                if (name.Length == 0) continue;
                if (!settings.IsOpeningLayer(insert.Layer?.Name ?? "") && !settings.IsOpeningLayer(name)) continue;
                counts.TryGetValue(name, out int n);
                counts[name] = n + 1;
            }
            return counts.OrderByDescending(kv => kv.Value).Take(10).Select(kv => (kv.Key, kv.Value)).ToList();
        }

        // ───────── units ─────────

        /// <summary>
        /// The unit verdict. Plausible only when the header names a unit and Units.Resolve kept it
        /// (the sheet came out building-sized). Anything else needs the user's word before a build.
        /// </summary>
        public static Dictionary<string, object> UnitsVerdict(string headerUnit, double? headerMmPerUnit, double usedMmPerUnit)
        {
            bool plausible = headerMmPerUnit.HasValue && Math.Abs(headerMmPerUnit.Value - usedMmPerUnit) < 1e-9;
            var v = new Dictionary<string, object>
            {
                ["header"] = headerUnit,
                ["header_mm_per_unit"] = headerMmPerUnit,
                ["used_mm_per_unit"] = usedMmPerUnit,
                ["plausible"] = plausible,
                ["needs_confirmation"] = !plausible,
            };
            if (!plausible)
                v["note"] = headerMmPerUnit.HasValue
                    ? "The header says " + headerUnit + " (" + Num(headerMmPerUnit.Value) + " mm per unit), which makes the sheet an impossible size; "
                      + "sizes below are read at " + Num(usedMmPerUnit) + " mm per unit instead, unconfirmed."
                    : "The header gives no unit; sizes below are read at " + Num(usedMmPerUnit) + " mm per unit, guessed from the drawing's size, unconfirmed.";
            return NoNulls(v);
        }

        // ───────── arguments ─────────

        public sealed class Args
        {
            public string DwgRef;
            public List<int> Plans = new List<int>();
        }

        public static bool TryParseArgs(JsonElement args, out Args parsed, out string error)
        {
            parsed = new Args();
            error = null;
            if (args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null) return true;
            var r = new ArgReader();
            if (r.IsObject(args, "read_dwg_plan arguments"))
            {
                r.Known(args, "", "dwg_ref", "plans");
                parsed.DwgRef = r.Str(args, "dwg_ref");
                List<long> plans = r.Longs(args, "plans");
                if (plans != null)
                {
                    if (plans.Count > MaxPickedPlans) r.Fail("plans takes at most " + MaxPickedPlans + " plan numbers.");
                    if (plans.Any(p => p < 1)) r.Fail("plans are the n numbers from the listing (1 or more).");
                    parsed.Plans = plans.Distinct().Select(p => (int)Math.Min(p, int.MaxValue)).ToList();
                }
            }
            error = r.Ok ? null : r.Error;
            return r.Ok;
        }

        // ───────── envelopes ─────────

        public const string NoModelMessage =
            "No Revit model is open, so a model:... ref cannot be read. Open the model, or attach the DWG to the chat.";

        /// <summary>Why a non-"att:" ref cannot be read before looking in the model; null (with its element id) when it can.</summary>
        public static string ModelRefProblem(string dwgRef, bool modelOpen, out long id)
        {
            string raw = dwgRef.StartsWith("model:", StringComparison.OrdinalIgnoreCase) ? dwgRef.Substring(6) : dwgRef;
            if (!long.TryParse(raw, out id)) return "dwg_ref must be the \"att:...\" or \"model:...\" ref from the [Attached DWG] block.";
            return modelOpen ? null : NoModelMessage;
        }

        /// <summary>An "att:" ref: a DWG attached in the pane (DwgScratchCache.IsAttachmentRef calls this).</summary>
        public static bool IsAttachmentRef(string dwgRef) =>
            !string.IsNullOrEmpty(dwgRef) && dwgRef.StartsWith("att:", StringComparison.Ordinal);

        /// <summary>True when the call's dwg_ref resolves in the open model (model:&lt;id&gt; or any other non-"att:" string),
        /// so the open-document and same-document checks apply. No ref, a non-string ref or an "att:" ref never reads the model.</summary>
        public static bool NeedsOpenModel(JsonElement args) =>
            args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("dwg_ref", out var r)
            && r.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(r.GetString())
            && !IsAttachmentRef(r.GetString());

        public static Envelope NoDwg() => ReadEnvelope.Fail(ErrorCodes.NotFound, NoDwgMessage, Array.Empty<string>());

        /// <summary>The answer: with no plans picked, the sheet listing; with plans, their detail.</summary>
        public static Envelope Build(DwgPlanSheet sheet, IReadOnlyList<int> picked)
        {
            var units = UnitsVerdict(sheet.HeaderUnit, sheet.HeaderMmPerUnit, sheet.UsedMmPerUnit);
            bool unitsOk = (bool)units["plausible"];
            List<DwgPlan> real = sheet.Plans.Where(p => p.Cluster.Walls.Count >= MinPlanWalls && !p.IsDetail).ToList();

            var data = new Dictionary<string, object>
            {
                ["file"] = sheet.File,
                ["units"] = units,
            };
            if (sheet.Warnings.Count > 0) data["warnings"] = sheet.Warnings;

            if (picked == null || picked.Count == 0)
            {
                data["sheet_m"] = Metres(sheet.SheetWidthMm) + " x " + Metres(sheet.SheetHeightMm);
                data["plans_found"] = real.Count;
                int detailCount = sheet.Plans.Count(p => p.Cluster.Walls.Count >= MinPlanWalls && p.IsDetail);
                if (detailCount > 0) data["details_skipped"] = detailCount;
                int fragments = sheet.Plans.Count - real.Count - detailCount;
                if (fragments > 0) data["fragments_skipped"] = fragments;
                data["plans"] = real.Take(MaxPlanRows).Select(Row).ToList();
                if (real.Count > MaxPlanRows) data["plans_not_listed"] = real.Count - MaxPlanRows;
                data["layers"] = LayerRows(sheet);
                var ambiguous = Ambiguous(sheet);
                if (ambiguous.Count > 0) data["ambiguous_layers"] = ambiguous;
                if (sheet.OpeningBlocks.Count > 0)
                    data["door_window_blocks"] = sheet.OpeningBlocks.Select(b => new Dictionary<string, object> { ["name"] = b.Name, ["count"] = b.Count }).ToList();

                string summary = real.Count == 0
                    ? "No floor plan found in " + sheet.File + " (no wall pairs " + Num(Wall.SMin) + "-" + Num(Wall.SMax) + " mm apart)."
                    : sheet.File + ": " + real.Count + " plan" + (real.Count == 1 ? "" : "s") + " on the sheet"
                      + (unitsOk ? "" : "; units need confirming") + ".";
                string next = real.Count == 0 ? null
                    : unitsOk ? ListNext
                    : ListNext + " Say the sizes assume " + Num(sheet.UsedMmPerUnit) + " mm per unit, unconfirmed.";
                return ReadEnvelope.Done(summary, data, next: next);
            }

            var unknown = picked.Where(n => sheet.Plans.All(p => p.N != n)).ToList();
            if (unknown.Count > 0)
                return ReadEnvelope.Fail(ErrorCodes.NotFound,
                    "No plan " + string.Join(", ", unknown) + " in " + sheet.File + "; it has plans 1-" + sheet.Plans.Count + ". Call read_dwg_plan without plans to list them.",
                    real.Take(10).Select(p => p.N + ": " + (p.Title ?? p.Level ?? "untitled")));

            var details = picked.Select(n => Detail(sheet.Plans.First(p => p.N == n))).ToList();
            data["plans"] = details;
            var assumptions = new List<string> { "Door height 2100 mm", "Window height 1200 mm", "Window sill 900 mm" };
            if (!unitsOk) assumptions.Insert(0, "Sizes read at " + Num(sheet.UsedMmPerUnit) + " mm per drawing unit, not confirmed by the user");
            data["assumptions"] = assumptions;
            var chosen = picked.Select(n => sheet.Plans.First(p => p.N == n)).ToList();
            string detailSummary = "Plan " + string.Join(", ", picked) + ": "
                + chosen.Sum(p => p.Cluster.Walls.Count) + " walls, " + chosen.Sum(p => p.Doors.Count) + " doors, "
                + chosen.Sum(p => p.Windows.Count) + " windows" + (unitsOk ? "." : "; units need confirming.");
            return ReadEnvelope.Done(detailSummary, data,
                next: unitsOk ? null : "Answer with these counts and say the sizes assume " + Num(sheet.UsedMmPerUnit) + " mm per unit, unconfirmed.");
        }

        private static Dictionary<string, object> Row(DwgPlan p) => NoNulls(new Dictionary<string, object>
        {
            ["n"] = p.N,
            ["title"] = p.Title,
            ["level"] = p.Level,
            ["elevation"] = p.Elevation,
            ["size_m"] = Metres(p.Cluster.Width) + " x " + Metres(p.Cluster.Height),
            ["centre_m"] = Metres((p.Cluster.MinX + p.Cluster.MaxX) / 2) + ", " + Metres((p.Cluster.MinY + p.Cluster.MaxY) / 2),
            ["walls"] = p.Cluster.Walls.Count,
        });

        private static Dictionary<string, object> Detail(DwgPlan p)
        {
            var row = Row(p);
            List<Wall> walls = p.Cluster.Walls;
            row["walls"] = new Dictionary<string, object>
            {
                ["count"] = walls.Count,
                ["by_thickness_mm"] = walls.GroupBy(w => Round10(w.Thickness))
                    .OrderByDescending(g => g.Count()).Take(MaxWidthRows)
                    .Select(g => new Dictionary<string, object>
                    {
                        ["mm"] = g.Key,
                        ["count"] = g.Count(),
                        ["length_m"] = Math.Round(g.Sum(w => w.Centerline.Length) / 1000.0, 1),
                    }).ToList(),
            };
            row["doors"] = Openings(p.Doors);
            row["windows"] = Openings(p.Windows);

            // shortcut: "closed" = rooms cover at least half the plan's box; a true outer-boundary trace belongs to build_from_dwg (AI-608).
            double roomArea = p.Rooms.Sum(r => r.Area);
            row["outline_closed"] = p.Rooms.Count > 0 && p.Cluster.Area > 0 && roomArea >= 0.5 * p.Cluster.Area;
            row["loose_wall_ends"] = p.LooseEnds;

            var names = p.Rooms.Where(r => !string.IsNullOrWhiteSpace(r.Name)).Select(r => r.Name).Distinct().ToList();
            row["rooms"] = NoNulls(new Dictionary<string, object>
            {
                ["count"] = p.Rooms.Count,
                ["named"] = names.Take(MaxRoomNames).ToList(),
                ["more_names"] = names.Count > MaxRoomNames ? (object)(names.Count - MaxRoomNames) : null,
            });
            return row;
        }

        private static Dictionary<string, object> Openings(List<Opening> list) => new Dictionary<string, object>
        {
            ["count"] = list.Count,
            ["widths_mm"] = list.GroupBy(o => Round10(WidthOf(o))).OrderByDescending(g => g.Count()).Take(MaxWidthRows)
                .Select(g => new Dictionary<string, object> { ["mm"] = g.Key, ["count"] = g.Count() }).ToList(),
        };

        /// <summary>A door is as wide as its leaf, the swing radius. Opening.Width clamps the jambs to the
        /// host wall piece, and a swing hinges on the short stub beside the doorway (330 mm and 0 mm on SEK.DAIF).</summary>
        private static double WidthOf(Opening o) =>
            o.Geometry.OfType<Cad2Bim.Arc>().FirstOrDefault()?.Radius ?? o.Width;

        /// <summary>Proposed role per layer, biggest first, with the reason.</summary>
        private static List<Dictionary<string, object>> LayerRows(DwgPlanSheet sheet)
        {
            var settings = new CadToBimSettings();
            return sheet.Census.OrderByDescending(kv => kv.Value)
                .Select(kv => (Layer: kv.Key, Count: kv.Value, Role: RoleOf(kv.Key, settings)))
                .OrderByDescending(x => x.Role.Role != "other").ThenByDescending(x => x.Count)
                .Take(MaxLayerRows)
                .Select(x => new Dictionary<string, object>
                {
                    ["name"] = x.Layer.Length == 0 ? "(no layer)" : x.Layer,
                    ["segments"] = x.Count,
                    ["role"] = x.Role.Role,
                    ["reason"] = x.Role.Reason,
                }).ToList();
        }

        /// <summary>wall / door / window / door_or_window / other, from the same name hints the CAD to BIM pane uses.</summary>
        public static (string Role, string Reason) RoleOf(string layer, CadToBimSettings settings)
        {
            bool wall = settings.IsWallLayer(layer);
            bool window = settings.IsWindowLayer(layer);
            bool door = Regex.IsMatch(layer ?? "", "door|pintu", RegexOptions.IgnoreCase);
            if (wall && (door || window)) return ("wall", "name mentions a wall and an opening; read as wall");
            if (wall) return ("wall", "name mentions a wall");
            if (door && window) return ("door_or_window", "name mentions doors and windows");
            if (door) return ("door", "name mentions a door");
            if (window) return ("window", "name mentions a window");
            return ("other", "no wall, door or window word in the name");
        }

        /// <summary>Layers whose name points two ways; the best guess is pre-selected for one ask_user question.</summary>
        private static List<Dictionary<string, object>> Ambiguous(DwgPlanSheet sheet)
        {
            var settings = new CadToBimSettings();
            return sheet.Census.Keys
                .Where(l => settings.IsWallLayer(l) && settings.IsOpeningLayer(l))
                .Take(10)
                .Select(l => new Dictionary<string, object>
                {
                    ["name"] = l,
                    ["guess"] = "wall",
                    ["why"] = "the name mentions both a wall and a door or window",
                }).ToList();
        }

        /// <summary>Null means absent on the wire, and dictionary values are not covered by WhenWritingNull.</summary>
        private static Dictionary<string, object> NoNulls(Dictionary<string, object> d)
        {
            foreach (string key in d.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) d.Remove(key);
            return d;
        }

        private static double Round10(double mm) => Math.Round(mm / 10.0) * 10.0;

        private static string Metres(double mm) => (mm / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        private static string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
