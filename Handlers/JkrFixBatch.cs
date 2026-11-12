using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Handlers
{
    /// <summary>Non-generic constants and labels for <see cref="JkrFixBatch{TFix}"/>.</summary>
    public static class JkrFixBatch
    {
        /// <summary>From this many distinct elements, the confirm dialog warns that
        /// Revit may pause: the batch runs in one synchronous call (one undo step),
        /// so Revit does not redraw until it finishes.</summary>
        public const int PauseWarningElementCount = 500;

        /// <summary>True when a batch that changes this many elements should carry the
        /// "Revit may pause" warning.</summary>
        public static bool MayPauseRevit(int affectedElements) => affectedElements >= PauseWarningElementCount;

        /// <summary>
        /// Distinct elements a batch changes. A type rename or a type-level parameter
        /// write changes every instance of that type, so those instances count too.
        /// Counting stops at <paramref name="stopAt"/> (the warning only needs to know
        /// whether the threshold is reached), and <paramref name="instances"/> is only
        /// walked when some step is type-wide and the direct count is still below it.
        /// </summary>
        /// <param name="directIds">Elements changed directly (instance writes, Grid/Level
        /// renames, and each renamed type itself).</param>
        /// <param name="typeWideTypeIds">Types whose every instance changes.</param>
        /// <param name="instances">Model instances as (element id, type id); lazy.</param>
        public static int CountAffected(IEnumerable<long> directIds, ICollection<long> typeWideTypeIds,
                                        IEnumerable<(long Id, long TypeId)> instances, int stopAt)
        {
            if (stopAt <= 0) throw new ArgumentOutOfRangeException(nameof(stopAt));
            var seen = new HashSet<long>();
            foreach (var id in directIds ?? Enumerable.Empty<long>())
            {
                seen.Add(id);
                if (seen.Count >= stopAt) return stopAt;
            }
            if (typeWideTypeIds == null || typeWideTypeIds.Count == 0 || instances == null)
                return seen.Count;

            foreach (var (id, typeId) in instances)
            {
                if (!typeWideTypeIds.Contains(typeId)) continue;
                seen.Add(id);
                if (seen.Count >= stopAt) return stopAt;
            }
            return seen.Count;
        }

        /// <summary>Busy-state text shown in the pane while a batch is applied.</summary>
        public static string BusyLabel(int changes) =>
            $"Applying {changes} change{(changes == 1 ? "" : "s")}…";
    }

    /// <summary>
    /// Revit-free plan for one JKR write batch. JkrRenameHandler applies every step
    /// inside one assimilated TransactionGroup, so the batch is a single undo step.
    /// TFix is JkrFixAction in the add-in (generic so the test host, which cannot
    /// load RevitAPI, can exercise the planning rules).
    /// </summary>
    public sealed class JkrFixBatch<TFix>
    {
        public sealed class Step
        {
            public bool IsRename { get; internal set; }
            public long ElementId { get; internal set; }
            /// <summary>Set for renames.</summary>
            public string NewName { get; internal set; }
            /// <summary>Set for parameter fixes.</summary>
            public TFix Fix { get; internal set; }
        }

        public IReadOnlyList<Step> Steps { get; }
        public int ChangeCount => Steps.Count;
        public int ElementCount { get; }
        public bool IsEmpty => Steps.Count == 0;
        /// <summary>Distinct element ids in the steps only. Use
        /// JkrAffectedElements.Count in the add-in to include the instances of renamed
        /// types; this is the fallback when that count is not available.</summary>
        public bool MayPauseRevit => JkrFixBatch.MayPauseRevit(ElementCount);
        public string BusyLabel() => JkrFixBatch.BusyLabel(ChangeCount);

        private JkrFixBatch(List<Step> steps)
        {
            Steps = steps;
            ElementCount = steps.Select(s => s.ElementId).Distinct().Count();
        }

        /// <summary>Renames first, then parameter fixes by ascending priority (stable,
        /// so equal priorities keep their queue order). The inputs are copied, so
        /// clearing the handler's queues afterwards does not change the batch.</summary>
        public static JkrFixBatch<TFix> Build(
            IEnumerable<(long ElementId, string NewName)> renames,
            IEnumerable<TFix> parameterFixes,
            Func<TFix, long> elementOf,
            Func<TFix, int> priorityOf)
        {
            if (elementOf == null) throw new ArgumentNullException(nameof(elementOf));
            if (priorityOf == null) throw new ArgumentNullException(nameof(priorityOf));

            var steps = new List<Step>();
            foreach (var (elementId, newName) in renames ?? Enumerable.Empty<(long, string)>())
                steps.Add(new Step { IsRename = true, ElementId = elementId, NewName = newName });

            foreach (var fix in (parameterFixes ?? Enumerable.Empty<TFix>()).OrderBy(priorityOf))
                steps.Add(new Step { IsRename = false, ElementId = elementOf(fix), Fix = fix });

            return new JkrFixBatch<TFix>(steps);
        }
    }
}
