// SymbolLookup — resolving a family type by name, once, for every caller.
//
// Why this file exists: there were two rules. DesignSpec.FindSymbol accepted
// every spelling a model plausibly sends — the bare type name ("900 x 2000mm"),
// "Family : Type" (how Revit DISPLAYS a type, so models copy it), and a bare
// family name — and when it failed it listed what the project actually has.
// Mutators' own lookups (place_door, place_window, place_window_array) matched
// the type name and nothing else, and said only "type 'X' not found".
//
// So the SAME name worked through build_design and was rejected through
// place_door, and the rejection carried nothing to correct it with. A drafter
// hitting that reasonably concludes the tool is broken.
//
// The failure message matters as much as the matching. DesignSpec's version put
// the available types after the words "known:", which read — to a human and to
// a model — as "this is what it tried to place", and a 2026-08-18 report
// diagnosed a hardcoded door type on exactly that misreading. The list is what
// is AVAILABLE; it now says so.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class SymbolLookup
    {
        /// <summary>Every loadable type in a category, unfiltered.</summary>
        internal static List<FamilySymbol> InCategory(Document doc, BuiltInCategory bic) =>
            new FilteredElementCollector(doc).WhereElementIsElementType()
                .OfCategory(bic).Cast<FamilySymbol>().ToList();

        /// <summary>The type <paramref name="name"/> refers to.
        ///
        /// Accepts the type name, "Family : Type", or a bare family name (first
        /// type of that family). A blank name takes the first type in the
        /// category — a caller that did not care gets something that works.
        ///
        /// Returns null ONLY when the category is empty (nothing of this kind is
        /// loaded in the project) — a different problem from a name that does
        /// not match, and one the caller has to phrase for itself. A name that
        /// matches nothing throws, listing what the project does have.</summary>
        internal static FamilySymbol? Find(Document doc, BuiltInCategory bic, string? name)
        {
            var all = InCategory(doc, bic);
            if (all.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(name)) return all[0];

            var trimmed = name.Trim();
            var byType = all.FirstOrDefault(s =>
                string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (byType != null) return byType;

            var colon = trimmed.LastIndexOf(':');
            if (colon > 0)
            {
                var fam = trimmed.Substring(0, colon).Trim();
                var typ = trimmed.Substring(colon + 1).Trim();
                var combo = all.FirstOrDefault(s =>
                    string.Equals(s.FamilyName, fam, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.Name, typ, StringComparison.OrdinalIgnoreCase));
                if (combo != null) return combo;
            }

            var byFamily = all.FirstOrDefault(s =>
                string.Equals(s.FamilyName, trimmed, StringComparison.OrdinalIgnoreCase));
            if (byFamily != null) return byFamily;

            throw new ArgumentException(NotFound(bic, trimmed, all));
        }

        /// <summary>The message for a name that matched nothing.
        ///
        /// Says plainly that the list is what the project HAS, not what was
        /// attempted — the previous wording ("known: ...") was read as the
        /// latter. Also says which spellings are accepted, because a drafter
        /// looking at "M_Door-Passage-Single-Flush : 900 x 2000mm" needs to know
        /// they may paste it verbatim.</summary>
        internal static string NotFound(BuiltInCategory bic, string requested,
                                        IReadOnlyList<FamilySymbol> all)
        {
            var shown = all.Take(8).Select(s => $"{s.FamilyName} : {s.Name}");
            var more = all.Count > 8 ? $" (+{all.Count - 8} more)" : "";
            return $"no type matching '{requested}' is loaded in {bic}. "
                 + $"This project HAS: {string.Join(", ", shown)}{more}. "
                 + "Pass the type name ('900 x 2000mm'), the full "
                 + "'Family : Type', or a family name.";
        }
    }
}
