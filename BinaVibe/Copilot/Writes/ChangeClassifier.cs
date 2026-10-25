#nullable enable
// How the write engine names and judges what Revit changed (rulings R35, R43).
// Revit-free.
//
// R43: an element with no category is NOT silently bookkeeping. Its change is
// reported as a side effect under the key "class:<ClassName>" (the card groups
// every class:* key into one "Other settings: N" row), it takes part in the
// commit shape check, and it disqualifies "Approve this build" — unless its
// class is on the explicit benign list, which is the only way an uncategorised
// change is ignored.
//
// R35: "Approve this build" ignores side-effect modifications (not the tool's
// own edits) of existing elements in Rooms, Areas, Spaces, tag categories,
// Views and Schedules; any other modified existing element, or any deletion,
// disqualifies the batch.

using System;
using System.Collections.Generic;

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
