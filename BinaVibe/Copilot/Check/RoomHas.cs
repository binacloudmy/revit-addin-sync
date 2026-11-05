#nullable enable
// room_has (AI-269 reliability 1.6): "every selected room has at least one element of these categories" (doors, windows).
// Revit-free: RevitGeometricKinds.RoomHas reads the doors and windows once and hands over, per element, the rooms either
// side of it; the verdicts are made here. A door with FromRoom A and ToRoom B counts for both rooms. A room that is not
// placed, or has no area (not enclosed), gets NO outcome: room_placed and room_enclosed already count those, and a fail
// here would count them twice.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Check
{
    /// <summary>How many elements of the rule's categories touch each room (by room id).</summary>
    public sealed class RoomHasCounts
    {
        private readonly Dictionary<long, int> _byRoom = new Dictionary<long, int>();

        /// <summary>One element (a door, a window) between two rooms; either may be null (outside). Counts once per room.</summary>
        public void Add(long? fromRoom, long? toRoom)
        {
            if (fromRoom is long f) Bump(f);
            if (toRoom is long t && t != fromRoom) Bump(t);
        }

        private void Bump(long room) => _byRoom[room] = (_byRoom.TryGetValue(room, out var n) ? n : 0) + 1;

        public int For(long room) => _byRoom.TryGetValue(room, out var n) ? n : 0;
    }

    /// <summary>A room as the rule sees it.</summary>
    public sealed class RoomFacts
    {
        public ElementRef Ref = null!;
        public long Id;
        public bool Placed;
        public double AreaM2;
        /// <summary>"2.03 Tandas Lelaki": number and name.</summary>
        public string Label = "";
    }

    public static class RoomHasEvaluation
    {
        /// <summary>"Doors", "Doors or Windows".</summary>
        public static string CategoriesText(IReadOnlyList<string> categories) => string.Join(" or ", categories.Select(c => c.Trim()).Where(c => c.Length > 0));

        public static Outcome Verdict(int count, IReadOnlyList<string> categories, string roomLabel) =>
            count >= 1
                ? Outcome.Pass(count.ToString(System.Globalization.CultureInfo.InvariantCulture), count + " found")
                : Outcome.Fail("No " + CategoriesText(categories) + " in room " + roomLabel + ".", "0");

        /// <summary>Adds one outcome per placed, enclosed room. <paramref name="unreadable"/> (the elements could not be read)
        /// makes every such room not_verified, never a pass.</summary>
        public static void Run(RuleRun run, IEnumerable<RoomFacts> rooms, RoomHasCounts? counts, IReadOnlyList<string> categories, string? unreadable)
        {
            foreach (var room in rooms)
            {
                if (!room.Placed || room.AreaM2 <= 0) continue;
                run.Add(room.Ref, unreadable != null || counts == null
                    ? Outcome.NotVerified(unreadable ?? "the " + CategoriesText(categories) + " could not be read", null, OutcomeCodes.Unreadable)
                    : Verdict(counts.For(room.Id), categories, room.Label));
            }
        }
    }
}
