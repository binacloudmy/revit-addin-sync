using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Handlers
{
    /// <summary>
    /// Revit-free pieces of the retry after Revit refuses a batch at commit time
    /// (JkrRenameHandler.ApplyBatch): which steps to set aside, and the reason
    /// recorded for a step Revit refused.
    /// </summary>
    public static class JkrBatchRetry
    {
        /// <summary>Prefix of the reason recorded for a step Revit refused at commit.</summary>
        public const string CommitRefused = "Revit did not accept this change";

        /// <summary>Steps on the elements Revit named in the failure (suspects) and the rest.</summary>
        public static (List<T> Suspects, List<T> Others) Split<T>(IEnumerable<T> steps, Func<T, long> elementId, ICollection<long> failingIds)
        {
            var list = (steps ?? Enumerable.Empty<T>()).ToList();
            if (failingIds == null || failingIds.Count == 0) return (new List<T>(), list);
            var suspects = list.Where(s => failingIds.Contains(elementId(s))).ToList();
            var rest = list.Where(s => !failingIds.Contains(elementId(s))).ToList();
            return (suspects, rest);
        }

        /// <summary>"Revit did not accept this change: &lt;Revit's message&gt;" (or the status when Revit gave none).</summary>
        public static string CommitReason(IEnumerable<string> errors, string status)
        {
            var text = string.Join(" ", (errors ?? Enumerable.Empty<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct());
            return text.Length > 0 ? CommitRefused + ": " + text : CommitRefused + " (" + (status ?? "") + ").";
        }
    }
}
