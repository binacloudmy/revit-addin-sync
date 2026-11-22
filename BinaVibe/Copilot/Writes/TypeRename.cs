#nullable enable
// AI-510: renaming a TYPE through set_parameters ("Type Name", type_param true). Revit-free.
//
// "Type Name" (ALL_MODEL_TYPE_NAME / SYMBOL_NAME_PARAM) is not a settable Parameter through the API: it is read-only
// there, so set_parameters refused it as not_allowed and Copilot fell back to run_revit_code (a second server card).
// A type is renamed through Element.Name. This class holds the rules that do not need Revit: which parameter names
// mean a rename, which names Revit refuses, and which names a sibling type already holds. The Revit side
// (SetParametersTool) feeds it the owner's current name and the names of the types in the same family and category.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BinaVibe.Copilot.Writes
{
    public static class TypeRename
    {
        /// <summary>Characters Revit refuses in an element name.</summary>
        public static readonly char[] ForbiddenChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>True for the parameter names that, on a type, mean "rename the type". "Name" only counts on a type:
        /// on an instance (a grid, a level, a view) it is an ordinary parameter and never reaches this class.</summary>
        public static bool IsRenameParam(string? param)
        {
            var p = (param ?? "").Trim();
            return p.Equals("Type Name", StringComparison.OrdinalIgnoreCase) || p.Equals("Name", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True for "Type Name" only: it always means the type.</summary>
        public static bool IsTypeNameParam(string? param) =>
            (param ?? "").Trim().Equals("Type Name", StringComparison.OrdinalIgnoreCase);

        private static readonly string[] InstanceNameCategories = { "Grids", "Levels", "Views", "Sheets", "Rooms", "Areas", "Spaces", "Schedules", "Scope Boxes", "Reference Planes", "Revision Clouds", "Matchline" };

        /// <summary>True for categories whose elements carry an instance-level "Name" (grids, levels, views, sheets, rooms...).</summary>
        public static bool NameIsInstanceProperty(string? category) =>
            InstanceNameCategories.Any(c => string.Equals(c, (category ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>Why "Name" with type_param true is refused on such an element (BM and EN; nothing is written).</summary>
        public static string InstanceNameRefusal(string? category) =>
            "'Name' is an instance property of " + (string.IsNullOrWhiteSpace(category) ? "these elements" : category) +
            ", so type_param true would rename their TYPE (every instance). Resend with type_param false to rename the element, or use 'Type Name' to rename the type; nothing was changed. " +
            "('Name' ialah sifat instance bagi elemen ini; type_param true akan menukar nama TYPE. Hantar semula dengan type_param false, atau guna 'Type Name'; tiada apa-apa diubah.)";

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        /// <summary>Revit's limit on an element name.</summary>
        public const int MaxNameLength = 255;

        /// <summary>The name to apply, or null when it is not a usable name; <paramref name="problem"/> says why.</summary>
        public static string? Normalize(string? given, out string? problem)
        {
            // AI-dc1b: "(TKm481a)  7140 x 750" (two spaces) is one name with single spaces; also tabs and line breaks.
            var name = Whitespace.Replace((given ?? "").Trim(), " ");
            if (name.Length == 0) { problem = "A type name cannot be empty."; return null; }
            var bad = name.IndexOfAny(ForbiddenChars);
            if (bad >= 0)
            {
                problem = "A type name cannot contain '" + name[bad] + "' (Revit refuses \\ : { } [ ] | ; < > ? ` ~).";
                return null;
            }
            if (name.Length > MaxNameLength)
            {
                problem = "A type name is at most " + MaxNameLength + " characters; this one has " + name.Length + ".";
                return null;
            }
            problem = null;
            return name;
        }

        /// <summary>True when a <see cref="Check"/> verdict means "a sibling already holds the name" (skip that type, not the batch).</summary>
        public static bool IsDuplicateVerdict(string? verdict) =>
            verdict != null && verdict.IndexOf("already exists in this family and category", StringComparison.Ordinal) >= 0;

        /// <summary>
        /// Checks one rename. <paramref name="siblingNames"/> are the names of the OTHER types of the same family and category
        /// (never the type itself). Returns null when the rename may go ahead, "same" when the type already has the name
        /// (nothing to write), else the refusal text.
        /// </summary>
        public static string? Check(string? given, string? currentName, IEnumerable<string> siblingNames, out string name)
        {
            var n = Normalize(given, out var problem);
            name = n ?? "";
            if (n == null) return problem;
            if (string.Equals(n, currentName, StringComparison.Ordinal)) return "same";
            var clash = siblingNames.FirstOrDefault(s => string.Equals(s, n, StringComparison.OrdinalIgnoreCase));
            if (clash != null)
                return "A type named '" + clash + "' already exists in this family and category, so '" + n + "' would be a duplicate; give a different name.";
            return null;
        }
    }
}
