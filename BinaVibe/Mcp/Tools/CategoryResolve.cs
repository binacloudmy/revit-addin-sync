// Category-name resolver shared by Inspectors (list_family_types,
// find_elements_by_filter, count_by) and ElementFilter (filter_elements).
// Extracted verbatim from Inspectors.ResolveBuiltInCategory — see that
// method's original comment: unknown categories must FAIL LOUDLY rather
// than fall through to an unfiltered collector (that's what forced the
// multi-round tool tours on "tandas"-style questions).
//
// The old 7-entry switch silently returned null for everything else — and
// callers then ran UNFILTERED collectors, handing the agent 500 junk types
// ("Arrowhead" as a plumbing fixture). Resolving generically ("Plumbing
// Fixtures" -> OST_PlumbingFixtures) is what fixed that.
//
// Every decision now lives in CategoryNames (Revit-free, unit-tested); all
// that is left here is asking the enum whether a candidate name exists.

using System;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class CategoryResolve
    {
        public static BuiltInCategory? Resolve(string nameOrFriendly)
        {
            foreach (var candidate in CategoryNames.Candidates(nameOrFriendly))
                if (Enum.TryParse<BuiltInCategory>(candidate, true, out var bic))
                    return bic;
            return null;
        }
    }
}
