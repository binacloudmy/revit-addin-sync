// BuildReport — what one job on the build handler produced (a build, an undo, a selection,
// a check of what is still in the model). Deliberately free of Revit types: the Tests
// project's RevitAPI reference is metadata-only at runtime, so a Dictionary<Wall, ElementId>
// here would make CadToBimSession untestable. Ids travel as long (ElementIdCompat.ToLong /
// ToElementId on the Revit side).

using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>What a created or skipped item is, for the Review step.</summary>
    public enum BuildCategory { Wall, Door, Window, Room }

    /// <summary>Why the builder left an item out. One reason per check, so the Review can say
    /// the real one instead of a catch-all.</summary>
    public enum SkipReason
    {
        /// <summary>Wall centreline under a millimetre, or under Revit's short-curve tolerance.</summary>
        TooShort,
        /// <summary>The level for this plan could not be made.</summary>
        NoLevel,
        /// <summary>Revit threw, returned nothing, or removed the element again.</summary>
        RevitRefused,
        /// <summary>The model has no door (or window) family to place.</summary>
        NoFamily,
        /// <summary>The wall the opening sits in was not built.</summary>
        HostNotBuilt,
        /// <summary>Opening narrower than Cad2BimBuilder's minimum (300 mm).</summary>
        TooNarrow,
        /// <summary>The level has no floor plan view, and room separation lines need one.</summary>
        NoPlanView,
        /// <summary>The room's outline has fewer than three usable edges.</summary>
        OutlineTooShort,
    }

    /// <summary>One item the build left out, at its drawing position (mm, before any storey
    /// shift) so the pane can mark it in the viewport.</summary>
    public sealed class SkippedItem
    {
        public BuildCategory Category { get; set; }
        public SkipReason Reason { get; set; }
        /// <summary>Extra words for the reason, e.g. the level name for NoPlanView.</summary>
        public string Detail { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>Skipped items of one category with one reason: one Review row.</summary>
    public sealed class SkipGroup
    {
        public BuildCategory Category { get; set; }
        public SkipReason Reason { get; set; }
        public string Detail { get; set; }
        public List<SkippedItem> Items { get; } = new();
        public int Count => Items.Count;

        /// <summary>"2 doors".</summary>
        public string CountText => Count + " " + BuildReport.Noun(Category, Count);

        /// <summary>"host wall not built".</summary>
        public string ReasonText => BuildReport.ReasonText(Category, Reason, Detail);
    }

    /// <summary>What a job on the build handler does (the handler's one request slot).</summary>
    public enum BuildJobKind
    {
        /// <summary>Create walls, openings and rooms (Cad2BimBuilder.Build).</summary>
        Build,
        /// <summary>Report which of ElementIds are no longer in the active model.</summary>
        CheckBuilt,
        /// <summary>Delete ElementIds in one transaction ("Undo this build").</summary>
        Undo,
        /// <summary>Select ElementIds in the active view and zoom to them.</summary>
        Select,
    }

    public sealed class BuildReport
    {
        public BuildJobKind Kind = BuildJobKind.Build;

        public int Walls;
        public int Doors;
        public int Windows;
        public int Rooms;
        public int SkippedOpenings;
        public int SkippedWalls;

        /// <summary>Rooms the classifier found but that were not placed (no plan view, short
        /// outline, or Revit refused). Every one is also in Skipped with its reason.</summary>
        public int SkippedRooms;

        /// <summary>NewFile target only: the .rvt that was written.</summary>
        public string OutputPath;

        /// <summary>Which target the build used.</summary>
        public BuildTarget Target;

        /// <summary>The document the ids belong to (PathName, or Title when unsaved).
        /// Undo and Select refuse when a different document is active.</summary>
        public string DocumentKey;

        public TimeSpan Elapsed;

        /// <summary>null = success. Innermost exception message otherwise; the
        /// TransactionGroup was rolled back and nothing below is in the model.</summary>
        public string Error;

        /// <summary>CAD wall -> Revit wall id (ElementId value), for every wall the
        /// build created and that survived Assimilate(). Keyed by reference.</summary>
        public Dictionary<Cad2Bim.Wall, long> WallIds = new();

        public List<long> DoorIds = new();
        public List<long> WindowIds = new();
        public List<long> RoomIds = new();

        /// <summary>Levels and floor plans this build made, and helpers it drew (room separation
        /// lines, sketch planes). Not shown as "created"; deleted by Undo this build.</summary>
        public List<long> NewLevelIds = new();
        public List<long> NewViewIds = new();
        public List<long> HelperIds = new();
        public List<string> NewLevelNames = new();

        /// <summary>Levels walls were placed on, in build order (for the Review headline).</summary>
        public List<string> LevelNames = new();

        /// <summary>Every item left out, with its real reason and drawing position.</summary>
        public List<SkippedItem> Skipped = new();

        /// <summary>Build: ids the pane believed built that are gone from the model (the drafter
        /// pressed Ctrl+Z). When set with Stale, nothing was built. CheckBuilt: the gone ids.</summary>
        public List<long> MissingIds = new();

        /// <summary>The build stopped before creating anything because MissingIds was not empty:
        /// the pane's pending list was out of date.</summary>
        public bool Stale;

        /// <summary>Undo: elements deleted. Select: elements selected.</summary>
        public int Affected;

        public bool Ok => Error == null;

        /// <summary>Every id this build put in the model, in a safe delete order: rooms and
        /// their lines first, then openings, walls, views, and the levels last.</summary>
        public List<long> AllCreatedIds()
        {
            var ids = new List<long>();
            ids.AddRange(RoomIds);
            ids.AddRange(HelperIds);
            ids.AddRange(DoorIds);
            ids.AddRange(WindowIds);
            ids.AddRange(WallIds.Values);
            ids.AddRange(NewViewIds);
            ids.AddRange(NewLevelIds);
            return ids;
        }

        /// <summary>The created ids of one category.</summary>
        public List<long> IdsOf(BuildCategory category)
        {
            switch (category)
            {
                case BuildCategory.Wall: return WallIds.Values.ToList();
                case BuildCategory.Door: return new List<long>(DoorIds);
                case BuildCategory.Window: return new List<long>(WindowIds);
                default: return new List<long>(RoomIds);
            }
        }

        /// <summary>Records one skipped item and keeps the old per-kind counters in step.</summary>
        public void Skip(BuildCategory category, SkipReason reason, double x, double y, string detail = null)
        {
            Skipped.Add(new SkippedItem { Category = category, Reason = reason, Detail = detail, X = x, Y = y });
            switch (category)
            {
                case BuildCategory.Wall: SkippedWalls++; break;
                case BuildCategory.Room: SkippedRooms++; break;
                default: SkippedOpenings++; break;
            }
        }

        /// <summary>Skipped items grouped by category, reason and detail; walls first, then
        /// doors, windows, rooms, and within a category the biggest group first.</summary>
        public List<SkipGroup> SkipGroups() => Group(Skipped);

        public static List<SkipGroup> Group(IEnumerable<SkippedItem> items)
        {
            var groups = new List<SkipGroup>();
            if (items == null) return groups;

            foreach (SkippedItem item in items)
            {
                if (item == null) continue;
                SkipGroup group = groups.FirstOrDefault(g =>
                    g.Category == item.Category && g.Reason == item.Reason &&
                    string.Equals(g.Detail ?? "", item.Detail ?? "", StringComparison.Ordinal));
                if (group == null)
                {
                    group = new SkipGroup { Category = item.Category, Reason = item.Reason, Detail = item.Detail };
                    groups.Add(group);
                }
                group.Items.Add(item);
            }

            return groups
                .OrderBy(g => (int)g.Category)
                .ThenByDescending(g => g.Count)
                .ThenBy(g => (int)g.Reason)
                .ToList();
        }

        public static string Noun(BuildCategory category, int count)
        {
            bool one = count == 1;
            switch (category)
            {
                case BuildCategory.Wall: return one ? "wall" : "walls";
                case BuildCategory.Door: return one ? "door" : "doors";
                case BuildCategory.Window: return one ? "window" : "windows";
                default: return one ? "room" : "rooms";
            }
        }

        public static string ReasonText(BuildCategory category, SkipReason reason, string detail)
        {
            switch (reason)
            {
                case SkipReason.TooShort: return "centreline too short";
                case SkipReason.NoLevel: return "level could not be made";
                case SkipReason.RevitRefused: return "Revit would not place";
                case SkipReason.NoFamily:
                    return category == BuildCategory.Window ? "no window family in the model" : "no door family in the model";
                case SkipReason.HostNotBuilt: return "host wall not built";
                case SkipReason.TooNarrow: return "narrower than 300 mm";
                case SkipReason.NoPlanView:
                    return (string.IsNullOrEmpty(detail) ? "level" : detail) + " has no plan view";
                case SkipReason.OutlineTooShort: return "outline too short";
                default: return reason.ToString();
            }
        }
    }
}
