// CategoryResolve — category-name resolver shared by Inspectors and the copilot
// readers. Unknown categories must FAIL LOUDLY (return null) rather than fall
// through to an unfiltered collector.
using System;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{

    // Category-name resolver shared by Inspectors (list_family_types,
    // find_elements_by_filter, count_by) and ElementFilter (filter_elements).
    // Extracted verbatim from Inspectors.ResolveBuiltInCategory — see that
    // method's original comment: unknown categories must FAIL LOUDLY rather
    // than fall through to an unfiltered collector (that's what forced the
    // multi-round tool tours on "tandas"-style questions).
    internal static class CategoryResolve
    {
        public static BuiltInCategory? Resolve(string nameOrFriendly)
        {
            // Accept either the BIC enum literal ("OST_Walls") or the
            // friendly category name ("Walls", "Doors", "Plumbing Fixtures").
            // The old 7-entry switch silently returned null for everything
            // else — and callers then ran UNFILTERED collectors, handing the
            // agent 500 junk types ("Arrowhead" as a plumbing fixture). That
            // garbage is what forced the multi-round tool tours on every
            // "tandas" question. Resolve generically: any friendly name maps
            // to OST_<NameWithoutSpaces>.
            var category = nameOrFriendly;
            if (string.IsNullOrWhiteSpace(category)) return null;
            if (category.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<BuiltInCategory>(category, true, out var bic))
                return bic;
            var compact = "OST_" + category.Replace(" ", "").Replace("-", "");
            if (Enum.TryParse<BuiltInCategory>(compact, true, out var bic2))
                return bic2;
            return category.ToLowerInvariant() switch
            {
                "walls" => BuiltInCategory.OST_Walls,
                "doors" => BuiltInCategory.OST_Doors,
                "windows" => BuiltInCategory.OST_Windows,
                "floors" => BuiltInCategory.OST_Floors,
                "rooms" => BuiltInCategory.OST_Rooms,
                "levels" => BuiltInCategory.OST_Levels,
                "grids" => BuiltInCategory.OST_Grids,
                _ => null,
            };
        }
    }
}
