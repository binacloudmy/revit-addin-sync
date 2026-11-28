// AI-642 step 1: the Build and Review half of the ONE CheckState (screens 8, 9, 10, 12), the build
// request it makes, and every line of copy those screens show. Revit-free and WPF-free (Tests link it).
// Engine-pending parts (needs-you queue AI-636, provenance AI-627, run ids / per-level AI-628,
// Remove this run AI-633) are neutral "not available yet" lines, never sample data.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RevitWebAppSync.Services.CadToBim;

namespace RevitWebAppSync.UI.CadToBim.Check
{
    public enum RunStage { Building, Done, Failed }

    /// <summary>One build of the main plan onto the main level.</summary>
    public sealed class RunState
    {
        public string Level { get; set; }
        public PlanSnap Plan { get; set; }
        public RunStage Stage { get; set; }
        /// <summary>null while building.</summary>
        public BuildReport Report { get; set; }
        public string Error { get; set; }
        /// <summary>Every wall of the run has left the model (Ctrl+Z): Build is allowed again.</summary>
        public bool Undone { get; set; }
        public string UndoName => BuildText.GroupName(Level);
    }

    public sealed partial class CheckState
    {
        public RunState Run { get; set; }

        public bool Building => Run?.Stage == RunStage.Building;
        /// <summary>A successful run whose walls are still in the model.</summary>
        public bool Built => Run != null && Run.Stage == RunStage.Done && !Run.Undone;
        public PlanMap MainPlan => Plans.FirstOrDefault(p => p.IsMain);
        /// <summary>A plan other than the main one mapped onto the build level: step 1 builds the main plan only.</summary>
        public bool OthersOnBuildLevel => Plans.Any(p => !p.IsMain && p.Mapped && p.TargetLevel == BuildLevel);

        /// <summary>Why Build is off even though Check passed; null = it is not blocked.</summary>
        public string BuildBlock =>
            Target.Workshared ? BuildText.WorksharedBlock
            : Building ? BuildText.BuildingBlock
            : Built ? BuildText.BuiltBlock
            : MainLevelId == null ? BuildText.NoLevelBlock(BuildLevel)
            : OthersOnBuildLevel ? BuildText.OthersBlock(BuildLevel)
            : null;

        public long? MainLevelId => Target.Levels.Where(l => l.Name == BuildLevel).Select(l => (long?)l.Id).FirstOrDefault(id => id > 0);
    }

    /// <summary>The Check state → the builder. Only the main plan, kept where the drawing has it, on the existing main level.</summary>
    public static class CheckBuild
    {
        public static BuildRequest Request(CheckState s, string drawingPath)
        {
            PlanMap main = s.MainPlan;
            DwgPlan plan = main == null ? null : s.Sheet?.Source?.Plans.FirstOrDefault(p => p.N == main.Plan.N);
            if (plan == null) return null;
            var walls = plan.Cluster.Walls.ToList();
            return new BuildRequest
            {
                Target = BuildTarget.AddToProject,
                DrawingPath = drawingPath,
                Walls = walls,
                LayoutWalls = walls,            // KeepPosition places by LayoutWalls ?? Walls: the main plan only, never the sheet
                Openings = plan.Doors.Concat(plan.Windows).ToList(),
                Spaces = plan.Rooms.ToList(),
                Storeys = StoreyMode.KeepPosition,   // lines up with the origin-to-origin preview link (AI-638)
                LevelId = s.MainLevelId,
                HeightMm = s.WallHeightMm,
                GroupName = BuildText.GroupName(s.BuildLevel),
                RequireLevel = true,                 // a missing/renamed level fails with nothing added, never the lowest level
            };
        }
    }

    /// <summary>One line in Review's "From Revit" card: a group of recorded failures.</summary>
    public sealed class FailureRow
    {
        public FailureGroup Group { get; set; }
        public string Text => Group.Count + " × " + (string.IsNullOrWhiteSpace(Group.Text) ? "Revit warning" : Group.Text.Trim());
        public string Kind => Group.Error ? (Group.Resolved ? "resolved error" : "error") : Group.Join ? "join warning" : "warning";
        public string Tone => Group.Error ? "r" : "a";
        public bool CanShow => Group.ElementIds.Count > 0;
    }

    /// <summary>One Accept checklist row (screen 12). Accept itself waits for AI-636.</summary>
    public sealed class AcceptRow
    {
        public string Bold { get; set; }
        public string Note { get; set; }
    }

    public static class BuildText
    {
        public const string WorksharedBlock = "Build isn't available in a workshared model yet (needs Remove this run)";
        public const string BuildingBlock = "Building…";
        public const string BuiltBlock = "Built · undo with Ctrl+Z to rebuild";
        public static string NoLevelBlock(string level) => level + " isn't in this project any more; open the drawing again";
        public static string OthersBlock(string level) => "Only the main plan is built for now: map the other plans off " + level + " to build";
        public const string StopTip = "One level in this run: it is committed as one undo step when it finishes";
        public const string QueuePending = "The needs-you list is not available yet";
        public const string QueuePendingWhy = "Guessed, missing and open items come with the needs-you queue.";
        public const string ChipsPending = "drives a Revit view filter · not available yet";
        public const string HeightsPending = "Not available yet";
        public const string AcceptPending = "Accept isn't available yet (needs-you queue)";
        public const string AcceptNote = "Until then, Ctrl+Z removes the whole level in one step.";
        public const string RemovePending = "Not available yet (Remove this run). Ctrl+Z still removes the whole run.";
        public const string Rolledback = "The build failed and was rolled back: nothing was added.";

        public static string GroupName(string level) => "CAD to BIM: " + level;
        public static string UndoLine(RunState r) => "Undo history: \"" + r.UndoName + "\".";

        private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        private static string Plural(int n, string one, string many) => N(n) + " " + (n == 1 ? one : many);

        // ── screen 8 ──
        public static string Progress(RunState r) =>
            r.Level + " · Building · " + Plural(r.Plan?.Walls ?? 0, "wall", "walls") + ", " + Plural((r.Plan?.Doors ?? 0) + (r.Plan?.Windows ?? 0), "door or window", "doors & windows");

        // ── screen 9 ──
        public static string Done(BuildReport b, string level) =>
            level + " · done · " + Plural(b.Walls, "wall", "walls") + " · " + Plural(b.Doors, "door", "doors") + " · " + Plural(b.Windows, "window", "windows")
            + (b.Rooms > 0 ? " · " + Plural(b.Rooms, "room", "rooms") : "");

        /// <summary>The quiet banners, in order: join warnings, other warnings, resolved errors, things not placed.</summary>
        public static List<(string Lead, string Bold, string Post, string Tone)> Banners(BuildReport b)
        {
            var list = new List<(string, string, string, string)>();
            FailureLog f = b.Failures;
            if (f.JoinWarnings > 0) list.Add(("Revit raised ", Plural(f.JoinWarnings, "join warning", "join warnings"), " → added to Review, not deleted.", "a"));
            if (f.OtherWarnings > 0) list.Add(("Revit raised ", Plural(f.OtherWarnings, "other warning", "other warnings"), " → added to Review.", "a"));
            if (f.ResolvedErrors > 0) list.Add(("Revit resolved ", Plural(f.ResolvedErrors, "error", "errors"), " its default way, which can remove elements → in Review.", "r"));
            if (f.OtherErrors > 0) list.Add(("Revit raised ", Plural(f.OtherErrors, "error", "errors"), " it had no fix for → in Review.", "r"));
            int skipped = b.SkippedWalls + b.SkippedOpenings + b.SkippedRooms;
            if (skipped > 0) list.Add(("", Plural(skipped, "item", "items"), " could not be placed (" + N(b.SkippedWalls) + " walls, " + N(b.SkippedOpenings) + " openings, " + N(b.SkippedRooms) + " rooms).", "a"));
            return list;
        }

        // ── screen 10 ──
        public static string Also(CheckState s)
        {
            var parts = new List<string>();
            int a = s.Assumptions.Count;
            if (a > 0) parts.Add(Plural(a, "assumption", "assumptions"));
            FailureLog f = s.Run?.Report?.Failures;
            int warnings = f == null ? 0 : f.JoinWarnings + f.OtherWarnings, errors = f == null ? 0 : f.ResolvedErrors + f.OtherErrors;
            if (warnings > 0) parts.Add(Plural(warnings, "Revit warning", "Revit warnings"));
            if (errors > 0) parts.Add(Plural(errors, "Revit error", "Revit errors"));
            return parts.Count == 0 ? "Also: nothing else from Check or Revit" : "Also: " + string.Join(" · ", parts);
        }

        public static string Totals(BuildReport b) =>
            Plural(b.Walls, "wall", "walls") + " · " + Plural(b.Doors, "door", "doors") + " · " + Plural(b.Windows, "window", "windows") + " · " + Plural(b.Rooms, "room", "rooms") + " built";

        // ── screen 12 ──
        public static List<AcceptRow> AcceptRows(BuildReport b) => new List<AcceptRow>
        {
            new AcceptRow { Bold = "Walls " + N(b.Walls), Note = "type: the model's first basic wall type (no type matching yet)" },
            new AcceptRow { Bold = "Doors " + N(b.Doors), Note = "type: the model's first door type (no type matching yet)" },
            new AcceptRow { Bold = "Windows " + N(b.Windows), Note = "type: the model's first window type (no type matching yet)" },
            new AcceptRow { Bold = "Rooms " + N(b.Rooms), Note = b.Rooms == 0 ? "none found on this plan" : "from closed outlines on the drawing" },
        };

        public static string Status(CheckState s) => s.Screen switch
        {
            CheckScreen.Building => "CAD to BIM · Build · building " + s.Run?.Level,
            CheckScreen.Built => "CAD to BIM · Build · " + (s.Run?.Stage == RunStage.Failed ? "failed, nothing added" : "done"),
            CheckScreen.Review => "CAD to BIM · Review",
            CheckScreen.Accept => "CAD to BIM · Review · accept",
            _ => null,
        };
    }
}
