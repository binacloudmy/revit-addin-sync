using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    /// <summary>How a row counts for a re-check: a user decision (Ignored, Approved, Manual,
    /// needs a person) is Other and is never changed by one.</summary>
    public enum RecheckState { Open, Fixed, Other }

    /// <summary>The slice of a pane row the re-check reads (IssueVm implements it).</summary>
    public interface IRecheckRow
    {
        string Id { get; }
        long RevitElementId { get; }
        RecheckState RecheckState { get; }
        bool FixVerified { get; set; }
    }

    /// <summary>What one targeted re-check decided. Revit-free.</summary>
    public sealed class JkrRecheckOutcome<T> where T : class, IRecheckRow
    {
        /// <summary>The pane's new row list.</summary>
        public List<T> Issues { get; } = new List<T>();
        /// <summary>Resolved rows whose rule fails again (Ctrl+Z): replaced by the fresh Open row.</summary>
        public List<T> Reopened { get; } = new List<T>();
        /// <summary>Open rows whose rule now passes: dropped, as a full scan drops them.</summary>
        public List<T> Passed { get; } = new List<T>();
        /// <summary>Rows the caller just wrote that the re-check passed (caller marks them Fixed).</summary>
        public List<T> Verified { get; } = new List<T>();
        /// <summary>Rows the caller just wrote that the re-check still fails.</summary>
        public List<T> StillFailing { get; } = new List<T>();
    }

    /// <summary>
    /// AI-427: the rule a write → re-check applies to the pane's rows. A row is
    /// Resolved because its rule re-passed, never because we wrote it. A rule
    /// "fails" on an element when the fresh scan returns a non-Fixed row with the
    /// row's Id (a Fixed row in the fresh list is the audit store's re-injected
    /// pass, not a failure). Only rows on the affected elements change; every
    /// other row, and every Ignored / Manual decision, is left alone.
    /// </summary>
    public static class JkrRecheck
    {
        /// <param name="current">The pane's rows now.</param>
        /// <param name="affectedElementIds">Revit element ids the write or undo touched.</param>
        /// <param name="fresh">Rows from the fresh scan (any element).</param>
        /// <param name="scanComplete">False when the backend failed: absence from the scan then proves nothing.</param>
        /// <param name="justWritten">Rows the caller just wrote; left Open here, reported in Verified / StillFailing.</param>
        public static JkrRecheckOutcome<T> Reconcile<T>(
            IReadOnlyCollection<T> current,
            IReadOnlyCollection<long> affectedElementIds,
            IReadOnlyCollection<T> fresh,
            bool scanComplete,
            IReadOnlyCollection<T> justWritten = null) where T : class, IRecheckRow
        {
            var affected = new HashSet<long>(affectedElementIds ?? new long[0]);
            var written = new HashSet<T>(justWritten ?? new T[0]);

            var failing = new Dictionary<string, T>();
            foreach (var f in fresh ?? new T[0])
                if (!string.IsNullOrEmpty(f.Id) && f.RecheckState != RecheckState.Fixed && !failing.ContainsKey(f.Id))
                    failing[f.Id] = f;

            var o = new JkrRecheckOutcome<T>();
            var seen = new HashSet<string>();
            foreach (var row in current)
            {
                if (!string.IsNullOrEmpty(row.Id)) seen.Add(row.Id);
                bool hit = row.RevitElementId > 0 && affected.Contains(row.RevitElementId);
                if (!hit) { o.Issues.Add(row); continue; }

                bool fails = !string.IsNullOrEmpty(row.Id) && failing.ContainsKey(row.Id);

                if (written.Contains(row))
                {
                    o.Issues.Add(row);
                    if (!scanComplete) continue;
                    (fails ? o.StillFailing : o.Verified).Add(row);
                }
                else if (row.RecheckState == RecheckState.Fixed)
                {
                    if (fails) { o.Issues.Add(failing[row.Id]); o.Reopened.Add(row); }
                    else { if (scanComplete) row.FixVerified = true; o.Issues.Add(row); }
                }
                else if (row.RecheckState == RecheckState.Open)
                {
                    if (!fails && scanComplete) o.Passed.Add(row);
                    else o.Issues.Add(row);
                }
                else o.Issues.Add(row);   // Ignored / Approved / Manual / needs a person: the user's call
            }

            foreach (var f in failing.Values)
                if (!seen.Contains(f.Id) && f.RevitElementId > 0 && affected.Contains(f.RevitElementId))
                    o.Issues.Add(f);

            return o;
        }
    }
}
