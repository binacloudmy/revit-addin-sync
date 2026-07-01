using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Pure de-dup of Roslyn metadata references by simple name. Kept free of any
    /// Revit/Roslyn/ClosedXML dependency so it can be unit-tested in isolation
    /// (Tests.csproj source-links this file directly). Used by
    /// <see cref="CodeExecutor"/>.BuildReferencesUncached.
    /// </summary>
    public static class ReferenceDedup
    {
        /// <summary>
        /// Given (manifest simple name, location) candidates IN LOAD ORDER, keep the
        /// FIRST location per simple name — exactly the identity Roslyn uses to reject
        /// duplicate references, so it never sees two refs sharing one manifest name.
        /// Comparison is case-insensitive (matching Roslyn/assembly-name semantics).
        /// Dropped duplicates are reported via <paramref name="skipped"/> so callers
        /// can log the collision.
        /// </summary>
        public static List<(string simpleName, string location)> DedupBySimpleName(
            IEnumerable<(string simpleName, string location)> candidates,
            out List<(string simpleName, string location)> skipped)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new List<(string simpleName, string location)>();
            skipped = new List<(string simpleName, string location)>();

            foreach (var entry in candidates)
            {
                // HashSet<string> with an ordinal-ignore-case comparer treats a single
                // null as a distinct key; that mirrors "keep the first, drop the rest".
                if (seen.Add(entry.simpleName))
                    kept.Add(entry);
                else
                    skipped.Add(entry);
            }

            return kept;
        }
    }
}
