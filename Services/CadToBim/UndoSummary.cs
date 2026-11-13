// UndoSummary — the words around "Undo this build": what the confirm dialog promises and what
// the pane says afterwards, in the drafter's terms (walls, doors, windows, rooms, new levels and
// floor plans). Revit-free so the Tests project can check the counts.
//
// Why not a raw id count: a build also records helpers (the sketch plane and the room separation
// lines drawn for rooms). Those are deleted too, but deleting the sketch plane takes its lines
// with it, so a raw count promised more (all ids) than the undo then reported (ids it deleted
// itself), and neither matched "Select in model" (walls, doors, windows and rooms only).

using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Services.CadToBim
{
    public static class UndoSummary
    {
        /// <summary>"Delete what this build made: 167 walls, 7 doors, 17 windows and 3 rooms?
        /// One Ctrl+Z in Revit brings them back." Counts what the build recorded.</summary>
        public static string ConfirmText(BuildReport build)
        {
            string what = Describe(build, null);
            if (what.Length == 0) return "Delete what this build made? One Ctrl+Z in Revit brings it back.";
            return "Delete what this build made: " + what + "? One Ctrl+Z in Revit brings them back.";
        }

        /// <summary>The pane's line after an undo: counts only ids of <paramref name="build"/>
        /// that the undo actually removed (<see cref="BuildReport.DeletedIds"/>), in the same
        /// categories as <see cref="ConfirmText"/>, and says what was left behind.</summary>
        public static string DoneText(BuildReport build, BuildReport undo)
        {
            var deleted = new HashSet<long>(undo?.DeletedIds ?? new List<long>());
            string what = Describe(build, deleted);

            string text = what.Length == 0
                ? "Build undone: nothing left to delete, those elements were already gone."
                : "Build undone: deleted " + what + " (one Ctrl+Z brings them back).";

            int left = Left(build, undo);
            if (left > 0)
                text += " " + left + (left == 1 ? " element" : " elements") +
                        " could not be deleted (pinned or protected).";

            return text + " The walls are unbuilt again.";
        }

        /// <summary>User-facing ids of the build (walls, doors, windows, rooms, new levels and
        /// plans) that are still in the model after the undo.</summary>
        public static int Left(BuildReport build, BuildReport undo)
        {
            if (build == null || undo == null) return 0;
            var gone = new HashSet<long>(undo.MissingIds ?? new List<long>());
            return Visible(build).Distinct().Count(id => !gone.Contains(id));
        }

        /// <summary>"167 walls, 7 doors, 17 windows and 3 rooms, plus 1 new level and 1 floor
        /// plan". With <paramref name="only"/> set, counts just those ids. Empty when none.</summary>
        public static string Describe(BuildReport build, ISet<long> only)
        {
            if (build == null) return "";

            var parts = new List<string>();
            foreach (BuildCategory category in new[] { BuildCategory.Wall, BuildCategory.Door, BuildCategory.Window, BuildCategory.Room })
            {
                int n = Count(build.IdsOf(category), only);
                if (n > 0) parts.Add(n + " " + BuildReport.Noun(category, n));
            }

            var extras = new List<string>();
            int levels = Count(build.NewLevelIds, only);
            if (levels > 0) extras.Add(levels + (levels == 1 ? " new level" : " new levels"));
            int plans = Count(build.NewViewIds, only);
            if (plans > 0) extras.Add(plans + (plans == 1 ? " floor plan" : " floor plans"));

            string text = Join(parts);
            string extra = Join(extras);
            if (text.Length == 0) return extra;
            return extra.Length == 0 ? text : text + ", plus " + extra;
        }

        private static IEnumerable<long> Visible(BuildReport build) =>
            build.WallIds.Values
                .Concat(build.DoorIds)
                .Concat(build.WindowIds)
                .Concat(build.RoomIds)
                .Concat(build.NewViewIds)
                .Concat(build.NewLevelIds);

        private static int Count(IEnumerable<long> ids, ISet<long> only) =>
            (ids ?? Enumerable.Empty<long>()).Distinct().Count(id => only == null || only.Contains(id));

        private static string Join(List<string> parts)
        {
            if (parts.Count == 0) return "";
            if (parts.Count == 1) return parts[0];
            return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
        }
    }
}
