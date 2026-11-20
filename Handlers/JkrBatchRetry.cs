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

        /// <summary>One attempt at a set of steps in one transaction.</summary>
        public sealed class Attempt<TResult>
        {
            public bool Committed;
            public string Status = "";
            public TResult Result;
            public List<string> Errors = new List<string>();
            public HashSet<long> FailingIds = new HashSet<long>();
        }

        /// <summary>Run a commit. A commit that throws counts as a refused commit: the
        /// exception message is recorded in <paramref name="errors"/> (no element id)
        /// and false is returned, so the caller retries instead of losing the batch.</summary>
        public static bool TryCommit(Func<bool> commit, List<string> errors)
        {
            try { return commit(); }
            catch (Exception ex)
            {
                var text = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                if (!errors.Contains(text)) errors.Add(text);
                return false;
            }
        }

        /// <summary>
        /// Apply all steps in one go; when that commit is refused, retry without the
        /// steps on the elements named in the failure, then step by step. Steps whose
        /// own commit is refused go to <paramref name="onFailed"/> with the reason;
        /// every other step is kept via <paramref name="onCommitted"/>.
        /// </summary>
        public static void Apply<TStep, TResult>(
            List<TStep> steps, Func<TStep, long> elementId,
            Func<List<TStep>, Attempt<TResult>> run,
            Action<TResult> onCommitted, Action<TStep, string> onFailed)
        {
            var first = run(steps);
            if (first.Committed) { onCommitted(first.Result); return; }

            var (suspects, rest) = Split(steps, elementId, first.FailingIds);
            var restDone = false;
            if (suspects.Count > 0 && rest.Count > 0)
            {
                var second = run(rest);
                if (second.Committed) { onCommitted(second.Result); restDone = true; }
            }
            foreach (var step in restDone ? suspects : steps)
            {
                var one = run(new List<TStep> { step });
                if (one.Committed) onCommitted(one.Result);
                else onFailed(step, CommitReason(one.Errors, one.Status));
            }
        }
    }
}
