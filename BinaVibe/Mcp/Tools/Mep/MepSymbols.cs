// Family-symbol resolution for placement — Layer 0, discipline-agnostic.
//
// WHY THIS FILE EXISTS. FamilySymbol covers ANNOTATION symbols as well as
// model ones: tags, keynotes, symbols, title blocks. A name-only lookup over
// FilteredElementCollector.OfClass(typeof(FamilySymbol)) therefore happily
// returns an "Assembly Tag" and the caller places it as though it were a
// socket. Observed in UAT: with the family library gated on the free plan, the
// agent had no real outlet family to load, the name it asked for matched a tag
// symbol, and tags went into the model at socket positions.
//
// The rule this enforces: a placement tool NEVER falls back to a symbol of the
// wrong category. It fails, and it NAMES what it found and in which category,
// so the agent can say "that family is not loaded" instead of producing
// convincing rubbish. A wrong element in the model is worse than no element —
// it looks finished.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    /// <summary>Outcome of a placement-symbol lookup. Either Symbol is set, or
    /// Reason explains what was found instead — never both empty.</summary>
    internal sealed class SymbolPick
    {
        public FamilySymbol? Symbol;
        /// <summary>Drafter-facing explanation when Symbol is null.</summary>
        public string Reason = "";
        /// <summary>Name-matching symbols that were REFUSED, with the category
        /// that got them refused. Reported so the mismatch is visible.</summary>
        public List<Dictionary<string, object?>> Rejected = new();

        public bool Found => Symbol != null;
    }

    internal static class MepSymbols
    {
        /// <summary>Does this name identify that symbol? Accepts the bare type
        /// name or the "Family : Type" form the model usually emits.</summary>
        private static bool NameMatches(FamilySymbol fs, string name) =>
            string.Equals(fs.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"{fs.FamilyName} : {fs.Name}", name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals($"{fs.FamilyName}: {fs.Name}", name, StringComparison.OrdinalIgnoreCase);

        internal static bool IsAnnotation(FamilySymbol fs)
        {
            try { return fs.Category?.CategoryType == CategoryType.Annotation; }
            catch { return false; }
        }

        internal static string CategoryName(FamilySymbol fs)
        {
            try { return fs.Category?.Name ?? "(no category)"; }
            catch { return "(no category)"; }
        }

        /// <summary>Resolve a symbol that is safe to PLACE as a model element.
        ///
        /// <paramref name="allowed"/> is the set of categories the caller can
        /// actually place. Empty means "any model category" — still never an
        /// annotation one.
        ///
        /// Ordering is deliberate: an exact category match wins, then any other
        /// model category (reported as a warning by the caller, because it is
        /// usually a drafter using an unusual family), and a name match in a
        /// disallowed or annotation category is a REFUSAL carrying the
        /// evidence.</summary>
        internal static SymbolPick ResolvePlaceable(
            Document doc, string name, IReadOnlyCollection<BuiltInCategory> allowed)
        {
            var pick = new SymbolPick();
            if (string.IsNullOrWhiteSpace(name))
            {
                pick.Reason = "no family type name was given";
                return pick;
            }

            var matches = new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(fs => NameMatches(fs, name))
                .ToList();

            if (matches.Count == 0)
            {
                pick.Reason =
                    $"no family type named '{name}' is loaded in this model. "
                    + "Load it first (search_family_library then load_family), or pass a name from "
                    + "list_family_types. Do NOT substitute a different family.";
                return pick;
            }

            var allowedSet = new HashSet<long>(allowed.Select(c => (long)c));

            var exact = matches.FirstOrDefault(fs =>
                !IsAnnotation(fs)
                && fs.Category != null
                && (allowedSet.Count == 0 || allowedSet.Contains(fs.Category.Id.Value)));
            if (exact != null)
            {
                pick.Symbol = exact;
                return pick;
            }

            foreach (var fs in matches)
            {
                pick.Rejected.Add(new Dictionary<string, object?>
                {
                    ["type_id"] = fs.Id.Value,
                    ["family"] = fs.FamilyName,
                    ["type_name"] = fs.Name,
                    ["category"] = CategoryName(fs),
                    ["is_annotation"] = IsAnnotation(fs),
                });
            }

            var annotationOnly = matches.All(IsAnnotation);
            var found = string.Join(", ",
                matches.Select(fs => $"'{fs.FamilyName} : {fs.Name}' ({CategoryName(fs)})").Distinct());

            pick.Reason = annotationOnly
                // The exact UAT failure: a tag symbol wearing the right name.
                ? $"'{name}' matches only an ANNOTATION symbol — {found}. That is a tag, not a "
                  + "placeable device, and placing it would put tag graphics in the model where the "
                  + "devices should be. The real family is not loaded: load it "
                  + "(search_family_library then load_family) and try again."
                : $"'{name}' matches {found}, which is not a category this tool can place"
                  + (allowedSet.Count == 0 ? "." : $" (expected one of: {string.Join(", ", allowed)}).")
                  + " Pass a name from list_family_types for the right category, or load the correct "
                  + "family — do NOT substitute a different one.";
            return pick;
        }

        /// <summary>Electrical outlet/device categories a socket-style
        /// placement may legitimately land in. OST_ElectricalFixtures is the
        /// normal one; the rest are here because drafters do author outlets and
        /// device families under the sibling device categories.</summary>
        internal static IReadOnlyCollection<BuiltInCategory> ElectricalDeviceCategories { get; } = new[]
        {
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_LightingDevices,
            BuiltInCategory.OST_DataDevices,
            BuiltInCategory.OST_CommunicationDevices,
            BuiltInCategory.OST_SecurityDevices,
            BuiltInCategory.OST_FireAlarmDevices,
            BuiltInCategory.OST_NurseCallDevices,
            BuiltInCategory.OST_TelephoneDevices,
            BuiltInCategory.OST_ElectricalEquipment,
        };

        /// <summary>Sockets specifically. Kept separate from the broad device
        /// list so place_socket_points cannot quietly place a light fitting.</summary>
        internal static IReadOnlyCollection<BuiltInCategory> SocketCategories { get; } = new[]
        {
            BuiltInCategory.OST_ElectricalFixtures,
        };
    }
}
