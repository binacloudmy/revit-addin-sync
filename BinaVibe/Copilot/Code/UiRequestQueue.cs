#nullable enable
// UI requests made by run_revit_code (ruling R59). Revit-free.
//
// Read code always runs inside an always-rolled-back TransactionGroup, so a
// view switch, zoom or selection made inside the run could point at state the
// rollback removes. The code therefore never calls them: the rewriter routes
// uidoc.RequestViewChange / ShowElements / Selection.SetElementIds to host
// shims that queue the request here. After the rollback (or a write's commit)
// the host plans the queue against the model as it now is:
//   view   - the last request wins; applied only if that id is still a view;
//   show   - every id asked for, once, in first-seen order;
//   select - the last request wins;
// and every id that no longer exists is dropped and reported.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Code
{
    public sealed class UiRequestQueue
    {
        public const int View = 0;
        public const int Show = 1;
        public const int Select = 2;

        private readonly List<(int Kind, long[] Ids)> _requests = new List<(int, long[])>();

        /// <summary>Requests queued so far.</summary>
        public int Count => _requests.Count;

        public void Add(int kind, IEnumerable<long> ids)
        {
            if (kind != View && kind != Show && kind != Select) throw new ArgumentOutOfRangeException(nameof(kind));
            _requests.Add((kind, (ids ?? Enumerable.Empty<long>()).ToArray()));
        }

        /// <summary>What to apply now. exists: the id is an element of the document; isView: it is a view.</summary>
        public UiPlan Plan(Func<long, bool> exists, Func<long, bool> isView)
        {
            var dropped = new SortedSet<long>();

            long? viewId = null;
            var lastView = _requests.LastOrDefault(r => r.Kind == View);
            if (lastView.Ids != null)
                foreach (var id in lastView.Ids.Take(1))
                {
                    if (exists(id) && isView(id)) viewId = id;
                    else dropped.Add(id);
                }

            var show = new List<long>();
            var seen = new HashSet<long>();
            foreach (var (kind, ids) in _requests)
                if (kind == Show)
                    foreach (var id in ids)
                        if (seen.Add(id))
                        {
                            if (exists(id)) show.Add(id);
                            else dropped.Add(id);
                        }

            var select = new List<long>();
            var lastSelect = _requests.LastOrDefault(r => r.Kind == Select);
            var hasSelection = lastSelect.Ids != null;
            if (hasSelection)
                foreach (var id in lastSelect.Ids!.Distinct())
                {
                    if (exists(id)) select.Add(id);
                    else dropped.Add(id);
                }

            return new UiPlan(viewId, show.ToArray(), hasSelection, select.ToArray(), dropped.ToArray());
        }
    }

    public sealed class UiPlan
    {
        public UiPlan(long? viewId, long[] show, bool hasSelection, long[] select, long[] dropped)
        {
            ViewId = viewId;
            Show = show;
            HasSelection = hasSelection;
            Select = select;
            Dropped = dropped;
        }

        /// <summary>The view to switch to, or null.</summary>
        public long? ViewId { get; }

        /// <summary>Elements to zoom to (ShowElements); empty for none.</summary>
        public long[] Show { get; }

        /// <summary>Whether the code set the selection (possibly to nothing).</summary>
        public bool HasSelection { get; }

        public long[] Select { get; }

        /// <summary>Ids that no longer exist (or are no longer a view), so nothing was done with them.</summary>
        public long[] Dropped { get; }

        public bool Any => ViewId != null || Show.Length > 0 || HasSelection || Dropped.Length > 0;

        /// <summary>For the envelope's data.ui.</summary>
        public Dictionary<string, object?> ToData(bool applied) => new Dictionary<string, object?>
        {
            ["applied"] = applied,
            ["view"] = ViewId,
            ["shown"] = Show.Length,
            ["selected"] = HasSelection ? Select.Length : (int?)null,
            ["dropped"] = Dropped,
        };
    }
}
