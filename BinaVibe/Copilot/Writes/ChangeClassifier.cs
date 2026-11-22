#nullable enable
// How the write engine names and judges what Revit changed (rulings R35, R43).
// Revit-free.
//
// R43: an element with no category is NOT silently bookkeeping. Its change is
// reported as a side effect under the key "class:<ClassName>" (the card groups
// every class:* key into one "Other settings: N" row), it takes part in the
// commit shape check, and it disqualifies add-only — unless its
// class is on the explicit benign list, which is the only way an uncategorised
// change is ignored.
//
// R35: the add-only judgement ignores side-effect modifications (not the tool's
// own edits) of existing elements in Rooms, Areas, Spaces, tag categories,
// Views and Schedules; any other modified existing element, or any deletion,
// disqualifies the batch.
// R57: model-written code cannot be told apart from Revit's automatic
// recompute, so for run_revit_code every recorded change is the tool's own
// edit: any modification of an existing element by code disqualifies add-only.
// set_parameters keeps R35 (what it reports is its edit, the rest is recompute).

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Writes
{
    public static class ChangeClassifier
    {
        public const string ClassPrefix = "class:";

        /// <summary>Revit classes whose uncategorised changes are pure bookkeeping (never shown,
        /// never compared, never disqualifying).
        /// TODO(live Revit, R43): seeded empty on purpose. Populate it from the class names that
        /// real previews report under class:* side effects, one class at a time, each with a
        /// note on why changing it can never alter the drafter's model.</summary>
        public static IReadOnlyCollection<string> BenignClasses => Benign;

        private static readonly HashSet<string> Benign = new(StringComparer.Ordinal);

        private static readonly HashSet<string> SideEffectCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "Rooms", "Areas", "Spaces", "Views", "Schedules",
        };

        /// <summary>The side-effect key for an uncategorised element of <paramref name="className"/>.</summary>
        public static string ClassKey(string? className) =>
            ClassPrefix + (string.IsNullOrWhiteSpace(className) ? "Element" : className!.Trim());

        public static bool IsClassKey(string? category) =>
            category != null && category.StartsWith(ClassPrefix, StringComparison.Ordinal);

        public static bool IsBenign(string? className) =>
            !string.IsNullOrEmpty(className) && Benign.Contains(className!);

        /// <summary>An element Revit adds by itself when a title block lands on a new sheet: the title block family's
        /// revision schedule (a ViewSchedule whose IsTitleblockRevisionSchedule is true). Nobody asked for it, so it is
        /// treated like a benign change: out of data.created / created_count, the touched and side-effect counts, the
        /// created summary and the toast (live R5 B4: 4 sheets read "4 Sheets and 4 Schedules created"). An ordinary
        /// ViewSchedule the drafter's code made is never one. Only an ADDED element can be one.</summary>
        public static bool IsTitleBlockByProduct(string? className, bool isTitleblockRevisionSchedule) =>
            string.Equals(className, "ViewSchedule", StringComparison.Ordinal) && isTitleblockRevisionSchedule;

        /// <summary>The ids the add-only judgement treats as the tool's own edits (R35, R57):
        /// for run_revit_code every recorded (non-benign) change; for other tools what they reported.</summary>
        public static IReadOnlyList<long> TouchedForApproval(string? tool, IEnumerable<long> recorded, IEnumerable<long> reported) =>
            string.Equals(tool, BinaVibe.Copilot.Contracts.ToolNames.RunRevitCode, StringComparison.Ordinal)
                ? (recorded ?? Array.Empty<long>()).Distinct().ToList()
                : (reported ?? Array.Empty<long>()).Distinct().ToList();

        /// <summary>The touched set on a run_revit_code envelope (live-test L16): every recorded edit
        /// and deletion, but only the additions the document's own collector can see. An addition it
        /// cannot see is a piece Revit created along (internal parts of a viewport, sketch planes…):
        /// a side effect, so the card counts what the code made.</summary>
        public static IReadOnlyList<long> CodeTouched(IEnumerable<long> added, IEnumerable<long> modified, IEnumerable<long> deleted,
                                                      ISet<long> visibleAdded) =>
            (added ?? Array.Empty<long>()).Where(id => visibleAdded != null && visibleAdded.Contains(id))
                .Concat(modified ?? Array.Empty<long>())
                .Concat(deleted ?? Array.Empty<long>())
                .Distinct().ToList();

        /// <summary>R35: a side-effect modification in this category does not stop a batch from
        /// counting as add-only. Uncategorised (class:*) changes never qualify.</summary>
        public static bool IsAllowedSideEffect(string? category)
        {
            if (string.IsNullOrWhiteSpace(category) || IsClassKey(category)) return false;
            var c = category!.Trim();
            return SideEffectCategories.Contains(c)
                   || c.Equals("Tags", StringComparison.OrdinalIgnoreCase)
                   || c.EndsWith(" Tags", StringComparison.OrdinalIgnoreCase);
        }
    }
}
