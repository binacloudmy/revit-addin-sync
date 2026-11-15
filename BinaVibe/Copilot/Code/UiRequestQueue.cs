#nullable enable
// UI requests made by run_revit_code (ruling R59). Revit-free.
//
// Read code always runs inside an always-rolled-back TransactionGroup, so a
// view switch, zoom or selection made inside the run could point at state the
// rollback removes. The code therefore never calls them: the rewriter routes
// uidoc.RequestViewChange / ShowElements / Selection.SetElementIds to host
// shims that queue the request here. After the rollback (or a write's commit)
// the host plans the queue against the model as it now is:
//   view       - the last request wins; applied only if that id is still a view;
//   show       - every id asked for, once, in first-seen order;
//   select     - the last request wins (element ids, or references by stable representation);
//   zoom       - every zoom, in order, on an open view (UIView.ZoomToFit / Zoom / ZoomSheetSize /
//                ZoomAndCenterRectangle);
// and every id (or view) that no longer exists is dropped and reported.
// When: a read applies the plan after its own rollback; a commit hands it to ctx.AfterCommit,
// which W3 runs only after the batch is assimilated (ruling R76); a preview never applies it.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Code
{
    /// <summary>When a run's UI requests may be applied.</summary>
    public enum UiDelivery
    {
        /// <summary>A read: after its own always-rolled-back group.</summary>
        AfterRollback,
        /// <summary>A preview: never; the drafter has not approved anything.</summary>
        Never,
        /// <summary>A commit: on ctx.AfterCommit, which W3 runs only after the batch is assimilated (R76).</summary>
        AfterCommit,
    }

    /// <summary>What a host shim passes to the __ui hook (StatementWrapper's prefix uses the same numbers).</summary>
    public static class UiShimKinds
    {
        public const int View = 0;        // UIDocument.RequestViewChange(View)
        public const int Show = 1;        // UIDocument.ShowElements(Element | ElementId | ICollection<ElementId>)
        public const int Select = 2;      // Selection.SetElementIds(ICollection<ElementId>)
        public const int References = 3;  // Selection.SetReferences(IList<Reference>)
        public const int Zoom = 4;        // UIView.ZoomToFit / Zoom / ZoomSheetSize / ZoomAndCenterRectangle: object[] { view, op, args... }
    }

    public enum UiDispatchResult { NothingToDo, AppliedNow, QueuedAfterCommit, Skipped, NoHook }

    /// <summary>The one place that decides when a run's UI requests are applied, so they run at most once.
    /// Read-mode UI is W4's alone: applied after W4's own rollback and never put on ctx.AfterCommit, even
    /// though W3 runs AfterCommit after read-mode code calls too (W3 7dd3647).</summary>
    public static class UiDispatch
    {
        public static UiDispatchResult Deliver(UiDelivery delivery, UiRequestQueue queue, IList<Action>? afterCommit, Action apply)
        {
            if (queue.Count == 0) return UiDispatchResult.NothingToDo;
            switch (delivery)
            {
                case UiDelivery.AfterRollback:
                    apply();
                    return UiDispatchResult.AppliedNow;
                case UiDelivery.AfterCommit:
                    if (afterCommit == null) return UiDispatchResult.NoHook;
                    afterCommit.Add(apply);
                    return UiDispatchResult.QueuedAfterCommit;
                default:
                    return UiDispatchResult.Skipped;
            }
        }
    }

    public sealed class UiRequestQueue
    {
        public const int View = 0;
        public const int Show = 1;
        public const int Select = 2;

        public const int ZoomToFit = 0;
        public const int ZoomFactor = 1;
        public const int ZoomSheetSize = 2;
        public const int ZoomRectangle = 3;

        private readonly List<(int Kind, long[] Ids)> _requests = new List<(int, long[])>();
        private readonly List<(long Id, string Stable)[]> _references = new List<(long, string)[]>();
        private readonly List<UiZoom> _zooms = new List<UiZoom>();

        /// <summary>Requests queued so far.</summary>
        public int Count => _requests.Count + _references.Count + _zooms.Count;

        /// <summary>Read: after the rollback. Preview: never. Commit: after the batch commits (ctx.AfterCommit).</summary>
        public static UiDelivery DeliveryFor(WritePhase? phase) =>
            phase == null ? UiDelivery.AfterRollback
            : phase == WritePhase.Preview ? UiDelivery.Never
            : UiDelivery.AfterCommit;

        /// <summary>Selection.SetReferences: each reference's element id and stable representation.</summary>
        public void AddReferences(IEnumerable<(long Id, string Stable)> references) =>
            _references.Add((references ?? Enumerable.Empty<(long, string)>()).ToArray());

        /// <summary>A UIView zoom on the view with this id.</summary>
        public void AddZoom(long viewId, int op, params double[] args)
        {
            if (op < ZoomToFit || op > ZoomRectangle) throw new ArgumentOutOfRangeException(nameof(op));
            _zooms.Add(new UiZoom(viewId, op, args ?? Array.Empty<double>()));
        }

        public void Add(int kind, IEnumerable<long> ids)
        {
            if (kind != View && kind != Show && kind != Select) throw new ArgumentOutOfRangeException(nameof(kind));
            _requests.Add((kind, (ids ?? Enumerable.Empty<long>()).ToArray()));
        }

        /// <summary>What to apply now. exists: the id is an element of the document; isView: it is a view.</summary>
        public UiPlan Plan(Func<long, bool> exists, Func<long, bool> isView) => Plan(exists, isView, _ => true);

        /// <summary>The same, where viewOpen says a view id is open in a window (for zooms).</summary>
        public UiPlan Plan(Func<long, bool> exists, Func<long, bool> isView, Func<long, bool> viewOpen)
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

            var references = new List<(long Id, string Stable)>();
            var hasReferences = _references.Count > 0;
            if (hasReferences)
                foreach (var r in _references[_references.Count - 1].Distinct())
                {
                    if (exists(r.Id)) references.Add(r);
                    else dropped.Add(r.Id);
                }

            var zooms = new List<UiZoom>();
            foreach (var z in _zooms)
            {
                if (exists(z.ViewId) && viewOpen(z.ViewId)) zooms.Add(z);
                else dropped.Add(z.ViewId);
            }

            return new UiPlan(viewId, show.ToArray(), hasSelection, select.ToArray(), dropped.ToArray())
            {
                HasReferences = hasReferences,
                References = references,
                Zooms = zooms,
            };
        }
    }

    public sealed class UiZoom
    {
        public UiZoom(long viewId, int op, double[] args)
        {
            ViewId = viewId;
            Op = op;
            Args = args;
        }

        public long ViewId { get; }
        /// <summary>One of UiRequestQueue.ZoomToFit / ZoomFactor / ZoomSheetSize / ZoomRectangle.</summary>
        public int Op { get; }
        /// <summary>ZoomFactor: the factor. ZoomRectangle: corner 1 x, y, z then corner 2 x, y, z.</summary>
        public double[] Args { get; }
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

        /// <summary>Whether the code set the selection by references (Selection.SetReferences).</summary>
        public bool HasReferences { get; internal set; }

        public IReadOnlyList<(long Id, string Stable)> References { get; internal set; } = Array.Empty<(long, string)>();

        public IReadOnlyList<UiZoom> Zooms { get; internal set; } = Array.Empty<UiZoom>();

        public bool Any => ViewId != null || Show.Length > 0 || HasSelection || HasReferences || Zooms.Count > 0 || Dropped.Length > 0;

        /// <summary>For the envelope's data.ui.</summary>
        public Dictionary<string, object?> ToData(bool applied) => new Dictionary<string, object?>
        {
            ["applied"] = applied,
            ["view"] = ViewId,
            ["shown"] = Show.Length,
            ["selected"] = HasSelection ? Select.Length : HasReferences ? References.Count : (int?)null,
            ["zooms"] = Zooms.Count,
            ["dropped"] = Dropped,
        };
    }
}
