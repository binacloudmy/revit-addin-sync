// BinaVibe.BulkEdit — renumber_elements planner, Revit-free (AI-220).
//
// The numbering rule used to live inside the write loop, so nothing could say
// what a renumber WOULD do before it ran. It is now a plan: the rows arrive in
// the order they are numbered (Revit orders them by element id or by position),
// the plan assigns each writable row its new mark, and both the dry run (the
// approval card) and the real write read the same plan.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.BulkEdit
{
    public sealed class RenumberRow
    {
        public long Id { get; init; }
        /// <summary>The mark it has now ("" when empty).</summary>
        public string? Current { get; init; }
        /// <summary>The parameter exists on it and is not read-only.</summary>
        public bool Writable { get; init; }
        /// <summary>The room it belongs to (by_room mode only).</summary>
        public string? RoomNumber { get; init; }
    }

    public sealed class RenumberPlan
    {
        public IReadOnlyList<Change> Changes { get; }
        /// <summary>Elements in scope (the category, narrowed by level).</summary>
        public int Matched { get; }
        /// <summary>by_room only: elements with no room, left unnumbered.</summary>
        public int SkippedNoRoom { get; }
        /// <summary>Elements without the parameter, or with it read-only.</summary>
        public int NotWritable { get; }

        private RenumberPlan(List<Change> changes, int matched, int noRoom, int notWritable)
        { Changes = changes; Matched = matched; SkippedNoRoom = noRoom; NotWritable = notWritable; }

        /// <summary>
        /// Without by_room: element i gets prefix + (start + i). The counter moves
        /// for every element, including one that cannot be written, so marks do
        /// not shift when one is skipped.
        /// With by_room: the mark is prefix + the room number, plus A, B, C… when a
        /// room holds several. An element with no room is skipped; a room's slot is
        /// taken even when the element cannot be written.
        /// </summary>
        public static RenumberPlan Build(IReadOnlyList<RenumberRow> ordered, bool byRoom, int start, string prefix)
        {
            var rows = ordered ?? new List<RenumberRow>();
            var changes = new List<Change>();
            int noRoom = 0, notWritable = 0;
            if (byRoom)
            {
                var perRoom = new Dictionary<string, int>();
                foreach (var r in rows)
                {
                    var room = r.RoomNumber;
                    if (string.IsNullOrWhiteSpace(room)) { noRoom++; continue; }
                    perRoom.TryGetValue(room!, out var seen);
                    perRoom[room!] = seen + 1;
                    var suffix = seen == 0 ? "" : ((char)('A' + seen - 1)).ToString();
                    if (!r.Writable) { notWritable++; continue; }
                    changes.Add(new Change { Id = r.Id, From = r.Current ?? "", To = prefix + room + suffix });
                }
            }
            else
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (!rows[i].Writable) { notWritable++; continue; }
                    changes.Add(new Change { Id = rows[i].Id, From = rows[i].Current ?? "", To = prefix + (start + i).ToString() });
                }
            }
            return new RenumberPlan(changes, rows.Count, noRoom, notWritable);
        }

        /// <summary>What the approval card shows: counts, the first marks, the last mark.</summary>
        public Dictionary<string, object?> ToPreview(int firstCount = 8) => new()
        {
            ["ok"] = true,
            ["dry_run"] = true,
            ["matched"] = Matched,
            ["would_set"] = Changes.Count,
            ["unchanged"] = Changes.Count(c => c.From == c.To),
            ["replaces_existing"] = Changes.Count(c => c.From.Length > 0 && c.From != c.To),
            ["skipped_no_room"] = SkippedNoRoom,
            ["not_writable"] = NotWritable,
            ["first"] = Changes.Take(firstCount).Select(c => (object)new Dictionary<string, object?>
                { ["id"] = c.Id, ["from"] = c.From, ["to"] = c.To }).ToList(),
            ["last_mark"] = Changes.Count > 0 ? Changes[Changes.Count - 1].To : null,
            ["nothing"] = Changes.Count == 0,
        };
    }
}
