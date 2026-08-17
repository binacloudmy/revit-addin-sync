using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync
{
    /// <summary>
    /// Matches a downloaded/linked file name to a project discipline by its
    /// filename prefix (the "Architecture_", "Structure_", … convention used
    /// throughout FederateDisciplinesCommand and SyncCommand). Prefixes used to
    /// be a hardcoded four-discipline list; they now come from the project's
    /// discipline registry — ShortCode where the discipline has one, else Code
    /// — so custom disciplines and future renames work without an add-in
    /// release. MainFile is never matched: it is a federation output, not a
    /// discipline that files are named/prefixed for.
    ///
    /// HVAC -&gt; Mechanical: the backend renamed the historical "HVAC" system
    /// discipline's code to "Mechanical" (bina-be migration
    /// 1767200000000-rename-hvac-to-mechanical.ts), independently of and before
    /// the custom-disciplines work this add-in change is part of. New files are
    /// named with the current registry prefix (Mechanical's ShortCode, "ME").
    /// Files already on a user's disk from before that rename may still carry
    /// the old "HVAC_" prefix, so it is kept as an accepted alias on READ only
    /// — it is never produced for new files. See task-8-report.md for the full
    /// trade-off writeup.
    /// </summary>
    public static class DisciplinePrefixMatcher
    {
        private static readonly Dictionary<string, string[]> LegacyPrefixAliases =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mechanical"] = new[] { "HVAC" }
            };

        /// <summary>
        /// The prefix used when naming new files for this discipline: ShortCode
        /// if set, else Code (the same fallback the old hardcoded list amounted
        /// to, since the four literal prefixes were the disciplines' codes).
        /// Returns null only if the discipline has neither — callers must skip
        /// rather than build a path/prefix out of a null.
        /// </summary>
        public static string GetPrefix(BimDiscipline discipline)
        {
            if (discipline == null) return null;
            if (!string.IsNullOrWhiteSpace(discipline.ShortCode)) return discipline.ShortCode;
            return string.IsNullOrWhiteSpace(discipline.Code) ? null : discipline.Code;
        }

        /// <summary>All prefixes accepted when READING an existing file name for
        /// this discipline: the current prefix plus any legacy aliases.</summary>
        public static IEnumerable<string> GetAcceptedPrefixes(BimDiscipline discipline)
        {
            string prefix = GetPrefix(discipline);
            if (!string.IsNullOrEmpty(prefix)) yield return prefix;

            if (discipline?.Code != null && LegacyPrefixAliases.TryGetValue(discipline.Code, out var aliases))
            {
                foreach (var alias in aliases) yield return alias;
            }
        }

        /// <summary>
        /// Finds the discipline (excluding MainFile) whose prefix matches the
        /// start of fileName ("&lt;prefix&gt;_..."), case-insensitively. Returns
        /// null if none match (callers should then treat the file as MainFile,
        /// matching pre-existing GetDisciplineTypeFromFileName behaviour).
        /// </summary>
        public static BimDiscipline Match(string fileName, IEnumerable<BimDiscipline> disciplines)
        {
            if (string.IsNullOrEmpty(fileName) || disciplines == null) return null;

            foreach (var discipline in disciplines)
            {
                if (discipline == null || discipline.IsMainFile) continue;
                foreach (var prefix in GetAcceptedPrefixes(discipline))
                {
                    if (string.IsNullOrEmpty(prefix)) continue;
                    if (fileName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
                        return discipline;
                }
            }
            return null;
        }

        /// <summary>Convenience for building the "expected prefixes" text shown
        /// to the user (e.g. in the "No Discipline Files" dialog).</summary>
        public static string DescribePrefixes(IEnumerable<BimDiscipline> disciplines)
        {
            var prefixes = (disciplines ?? Enumerable.Empty<BimDiscipline>())
                .Where(d => d != null && !d.IsMainFile)
                .Select(GetPrefix)
                .Where(p => !string.IsNullOrEmpty(p));
            return string.Join(", ", prefixes.Select(p => p + "_"));
        }
    }
}
