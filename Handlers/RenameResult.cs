using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Outcome of one JKR write batch (JkrRenameHandler). Revit-free so the test
    /// host can exercise the failure bookkeeping.
    /// </summary>
    public class RenameResult
    {
        /// <summary>Shown when Revit refused a change without saying why.</summary>
        public const string UnknownFailureReason = "Revit did not apply this change.";

        public int Renamed { get; set; }
        public int ParamFixed { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public string Error { get; set; }

        /// <summary>Every failed fix, one line each ("what: why"), for the post-batch
        /// summary. Built by the handler from <see cref="FailReasons"/>.</summary>
        public string FailDetails { get; set; } = "";

        /// <summary>Element IDs that failed/skipped — kept for callers that only need
        /// element-level granularity. Don't use this when an element can have multiple
        /// fixes (rename + several param fixes) and you need to distinguish which
        /// specific fix failed; use FailedFixKeys instead.</summary>
        public HashSet<long> FailedElementIds { get; set; } = new HashSet<long>();

        /// <summary>Per-fix failure keys — distinguishes "this rename failed" from
        /// "this Sistem param fix failed" on the same element. Format:
        ///   "r:{elementId}"           for rename_type
        ///   "p:{elementId}:{paramName}" for set_parameter
        /// Critical for FixAll bookkeeping: when one of three fixes on element X
        /// fails, the other two should still count as successes (and reverse on
        /// Reset). Element-level FailedElementIds.Contains(X) returns true for all
        /// three and over-flags the survivors.</summary>
        public HashSet<string> FailedFixKeys { get; set; } = new HashSet<string>();

        /// <summary>Why each fix failed, keyed like <see cref="FailedFixKeys"/>. Shown
        /// on the issue's row and in its detail view.</summary>
        public Dictionary<string, string> FailReasons { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Failure lines in the order the fixes ran, for <see cref="FailDetails"/>.</summary>
        public List<(string What, string Why)> FailLines { get; } = new List<(string What, string Why)>();

        /// <summary>Compute a fix key matching the format used by FailedFixKeys.</summary>
        public static string MakeFixKey(string action, long elementId, string parameterName)
        {
            if (string.Equals(action, "rename_type", StringComparison.OrdinalIgnoreCase))
                return $"r:{elementId}";
            return $"p:{elementId}:{parameterName ?? ""}";
        }

        /// <summary>Record one failed fix. <paramref name="what"/> names the change
        /// for the summary list (e.g. "Kod_Jenis on 12345").</summary>
        public void RecordFailure(string fixKey, long elementId, string what, string reason, bool skipped = false)
        {
            if (fixKey == null) throw new ArgumentNullException(nameof(fixKey));
            if (skipped) Skipped++; else Failed++;
            FailedElementIds.Add(elementId);
            FailedFixKeys.Add(fixKey);
            var why = string.IsNullOrWhiteSpace(reason) ? UnknownFailureReason : reason.Trim();
            FailReasons[fixKey] = why;
            FailLines.Add((what, why));
        }

        /// <summary>The recorded reason for one fix, or null when it did not fail.</summary>
        public string ReasonFor(string action, long elementId, string parameterName)
        {
            var key = MakeFixKey(action, elementId, parameterName);
            if (FailReasons.TryGetValue(key, out var why)) return why;
            return FailedFixKeys.Contains(key) ? UnknownFailureReason : null;
        }

        /// <summary>Discard per-fix successes: used when Revit refused the whole commit,
        /// so nothing in the batch reached the model.</summary>
        public void ClearApplied()
        {
            Renamed = 0;
            ParamFixed = 0;
        }

        /// <summary>The summary list: at most <paramref name="max"/> lines, then
        /// "…and n more", each reason in <paramref name="lang"/> ("ms" / "en";
        /// null = the pane's language).</summary>
        public string BuildFailDetails(int max, string lang = null)
        {
            if (FailLines.Count == 0) return "";
            var lines = FailLines.Select(l =>
            {
                var why = RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.FixReason(l.Why, lang);
                return string.IsNullOrWhiteSpace(l.What) ? why : $"{l.What}: {why}";
            }).ToList();
            if (max <= 0 || lines.Count <= max) return string.Join("\n", lines);
            return string.Join("\n", lines.Take(max)) + "\n" + RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.AndMore(lines.Count - max, lang);
        }
    }
}
