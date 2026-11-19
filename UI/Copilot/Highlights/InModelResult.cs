#nullable enable
// The result, shown in the model (the "Gabung" result view, Nov 2026):
//   marks   TemporaryGraphicsManager controls for the marked elements that are IN the active view (owner view for a
//           tag or dimension, a view-filtered collector for the rest): a small filled dot per element, in the
//           outcome's colour; a thin hollow frame round a tag, dimension or other annotation. Dots of one colour and
//           tone that would overlap on screen become one badge with their count (MarkCluster). The marks stay after
//           the drafter clicks elsewhere. No transaction, no model change; the marks follow pan and zoom.
//   bar     the overlay window's slim bar in the first corner with no mark under it: what was done, the truth about
//           where the marks are (with Zoom or Go to view), Select all, Hide, Undo and close (MarkKey decides the words).
//   stays   nothing fades. The result stays until the drafter hides it (Hide; the pill then offers Show again), the
//           next turn starts, or Undo runs. A view change re-resolves the marks for the new view instead of clearing
//           them.
//   idling  on a view change: re-resolve; 150 ms after a real zoom settles: size the frames, group the dots again,
//           and draw again when that changed.
//   focus   a card row's group: open its view when its elements are in another one, select them in Revit, zoom to
//           them; the other groups' marks dim (drawn again).
//   click   a click on a dot (MarkClickHandler) selects that one element in Revit, dims the groups other than its
//           row's, and raises MarkClicked so the card scrolls to the row and flashes it; a click on a badge selects
//           its elements. Focus is a zoom and a selection: no transaction, no model change.
// Revit-side work runs through TracedRevit (Revit's API context) or in Idling (already
// in it); the overlay runs on the UI thread. Clear() first, then Undo, so a failed
// undo never leaves marks behind.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Mcp;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    public sealed class InModelResult
    {
        public const int SettleMs = 150;
        /// <summary>The most marks drawn for one result; the bar still counts every element.</summary>
        public const int MaxMarks = 400;

        private sealed class Mark
        {
            public long Id;
            public int Group;
            public MarkOutcome Outcome;
            /// <summary>A tag, a dimension or another annotation: a hollow frame round it (a dot would hide its text).</summary>
            public bool Frame;
            public XYZ Point = XYZ.Zero;
            public BoundingBoxXYZ? Box;
            /// <summary>A frame's size step; a dot has one size.</summary>
            public int Size = MarkKey.Sizes[0];
            public MarkTone Tone;
            /// <summary>Its place in what needs a look (1, 2, 3 ... in the card's order; G4), 0 for none: an amber numbered
            /// badge, never dim, never on a count badge.</summary>
            public int Number;
        }

        /// <summary>One drawn control: a dot, a frame, or a badge that stands for the dots that would overlap on screen.</summary>
        private sealed class Drawn
        {
            public List<Mark> Marks = new List<Mark>();
            public XYZ Point = XYZ.Zero;
            public string Image = "";
        }

        private readonly Func<UIApplication?> _app;
        private readonly HighlightOverlay _overlay;
        private readonly Dispatcher _ui;

        private HighlightSet? _set;
        private Action? _undo;
        private Func<bool>? _canUndo;
        private readonly MarkSession _session = new MarkSession();
        private OverlayModel? _model;
        private readonly List<int> _controls = new List<int>();
        // Each drawn control's marks: a click on a control (its index) is that dot's element, or the elements of a badge.
        private readonly Dictionary<int, Drawn> _byControl = new Dictionary<int, Drawn>();
        // What is drawn now (images and their elements): a settled zoom draws again only when it changed.
        private string _layoutKey = "";
        // The focused row's elements (a card row's, or the row of the mark clicked last): the other marks are dim.
        private HashSet<long>? _focusIds;
        // Every element of the result that exists in the model (the selection the turn ends with, and Select all).
        private List<long> _resultIds = new List<long>();
        // What needs a look, in the card's order (MarkKey.LookOrder), each element's place in it, and where the bar's
        // stepper is (-1: not stepped yet).
        private List<long> _lookOrder = new List<long>();
        private Dictionary<long, int> _lookPlace = new Dictionary<long, int>();
        private int _lookAt = -1;
        // The focused row's elements are in another view, which was asked to open: zoom to them once it is open.
        private List<long>? _pendingZoom;
        private readonly Dictionary<string, List<long>> _groupInstances = new Dictionary<string, List<long>>();
        private readonly List<Mark> _marks = new List<Mark>();
        private List<ElementPlace>? _places;
        private long _viewId;
        private string _viewName = "";
        private string _docKey = "";
        private bool _perspective;
        private bool _darkCanvas = true;
        private double _dpi = 1.0;
        // Every Show and Clear bumps it: a Show whose Revit-side work finishes after a newer Show or a Clear must not draw its bar.
        private int _generation;
        private UIApplication? _idlingApp;
        private XYZ? _c0, _c1;
        private readonly Stopwatch _since = Stopwatch.StartNew();
        private long _changedAt;
        private bool _dirty;
        // The repaint's own nudge of the zoom (Repaint): which way the next one goes, and that the corners' next move is ours.
        private bool _nudgeOut, _ownMove, _settleOwn;

        public InModelResult(Func<UIApplication?> app, HighlightOverlay overlay)
        {
            _app = app; _overlay = overlay; _ui = Dispatcher.CurrentDispatcher;
            MarkClickHandler.Clicked += OnMarkClicked;
        }

        /// <summary>A mark was clicked in the model: its element is now Revit's selection. Raised on the UI thread; the card of
        /// <see cref="MarkClick.TurnId"/> scrolls to the row and flashes it.</summary>
        public event Action<MarkClick>? MarkClicked;

        /// <summary>The view the bar's Go to view opens (the elements are all in another view), or null.</summary>
        public (long Id, string Name)? OtherView =>
            _model?.Key?.OpenViewId is long id ? (id, _model.Key.OpenViewName ?? "") : ((long, string)?)null;

        /// <summary>Where the shown result is, in the bar's words ("in Level 1, this view"); "" when nothing is shown.</summary>
        /// <summary>Something of this result is drawn in the view right now (a dot, a badge or a frame). False when its elements are hidden in the view
        /// or off it: "Hide marks" would then change nothing the drafter can see.</summary>
        public bool AnyDrawn => _marks.Count > 0 || _controls.Count > 0;
        public string WhereLine => _set == null || !IsShowing ? "" : _model?.Key?.Where ?? "";

        /// <summary>Raised on the UI thread whenever the shown result, or whether it is hidden, changed (the card relabels
        /// "Hide marks" / "Show in model").</summary>
        public event Action? Changed;

        public bool IsShowing => _set != null && _session.KeyVisible;
        private bool Hidden => _session.PillVisible;

        /// <summary>What the model shows of this turn's result.</summary>
        /// <summary>The stepper's words ("2 of 5") for the shown result's marks that need a look; "" when there are none (the card's stepper, which was the key's).</summary>
        public string StepLabelOf(string? turnId) => _set != null && _lookOrder.Count > 0 && _session.StateOf(turnId) == MarksState.Shown ? MarkKey.StepLabel(_lookAt, _lookOrder.Count) : "";

        public MarksState StateOf(string? turnId) => _set == null ? MarksState.None : _session.StateOf(turnId);

        /// <summary>This undo was of the turn whose result is in the view (MarkSession.GoneWith).</summary>
        public bool GoneWith(BinaVibe.Copilot.Undo.UndoChange? change) => _set != null && _session.GoneWith(change);

        // ─── Public ───────────────────────────────────────────────────────────

        /// <summary>Draw a turn's result in the model. <paramref name="undo"/> runs the turn's Undo; the bar offers it
        /// while <paramref name="canUndo"/> says it can run. <paramref name="select"/> (the turn just ended and changed or
        /// made something; G1): every element of the result that exists becomes Revit's selection, as after Paste, in one
        /// call through <see cref="Select"/>; no zoom, no view change. Not when an old turn's result is shown again.</summary>
        public void Show(HighlightSet set, Action? undo, Func<bool>? canUndo = null, bool select = false)
        {
            Clear();
            if (set == null || set.IsEmpty) return;
            _set = set; _undo = undo; _canUndo = canUndo; _session.Show(set.TurnId);
            _dpi = HighlightOverlay.DeviceScale();
            _lookOrder = MarkKey.LookOrder(set); _lookAt = -1;
            _lookPlace = new Dictionary<long, int>();
            for (var i = 0; i < _lookOrder.Count; i++) _lookPlace[_lookOrder[i]] = i;
            var generation = _generation;
            var selectAll = select && set.SelectsAtTheEnd;
            Run("show-in-model", set.Kinds.Sum(k => k.Ids.Count), app =>
            {
                if (set.FromSelection) FillFromSelection(app, set);   // r12: a find's highlight, read as Revit holds it now
                Resolve(app, set); AddMarks(app); StartIdling(app);
                if (selectAll && _resultIds.Count > 0) Select(app, _resultIds.Select(ElemIds.From).ToList(), "turn-end", generation);
            }, () =>
            {
                if (generation != _generation) return;   // a newer result, or a clear, came first
                if (set.FromSelection && set.Kinds.All(k => k.Ids.Count == 0)) { Clear(); return; }   // nothing was selected after all
                var app = _app();
                if (app != null) Draw(app);
                RaiseChanged();
            });
        }

        /// <summary>r12: the Found group of a <see cref="HighlightSet.FromSelection"/> set takes Revit's selection of the open model (at
        /// most <see cref="MaxMarks"/> are drawn, as for any result), and its category when they share one. No transaction.</summary>
        private static void FillFromSelection(UIApplication app, HighlightSet set)
        {
            var uidoc = app.ActiveUIDocument; var doc = uidoc?.Document;
            var k = set.Kinds.FirstOrDefault();
            if (uidoc == null || doc == null || k == null) return;
            try
            {
                var ids = uidoc.Selection.GetElementIds().Select(i => (long)i.Value).ToList();
                k.Ids = ids; k.Count = ids.Count;
                var cats = ids.Take(MaxMarks).Select(i => doc.GetElement(ElemIds.From(i))?.Category?.Name ?? "").Distinct().ToList();
                if (cats.Count == 1 && cats[0].Length > 0) k.Category = cats[0];
                set.Headline = MarkKey.Title(set.Kinds);
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] marks from selection: " + ex.Message); }
        }

        /// <summary>The bar's Hide (or the card's "Hide marks"): the marks and the bar go, the pill stays.</summary>
        public void Hide()
        {
            if (_set == null || !_session.Hide()) return;
            RemoveControls("marks-hide");
            var app = _app();
            if (app != null) Draw(app);
            RaiseChanged();
        }

        /// <summary>The pill's "Show again" (or the card's "Show in model"): exactly what was there, in the view open now.</summary>
        public void ShowAgain()
        {
            if (_set == null || !_session.ShowAgain()) return;
            var set = _set; var generation = _generation;
            Run("marks-show-again", _marks.Count, app => { Resolve(app, set); AddMarks(app); }, () =>
            {
                if (generation != _generation) return;
                var app = _app();
                if (app != null) Draw(app);
                RaiseChanged();
            });
        }

        /// <summary>Remove the marks and the bar. Safe to call when nothing is shown.</summary>
        public void Clear()
        {
            _generation++;
            StopIdling();
            _overlay.ClearResult();
            var had = _set != null;
            _set = null; _model = null; _session.Clear(); _marks.Clear(); _places = null; _groupInstances.Clear(); _resultIds = new List<long>();
            _lookOrder = new List<long>(); _lookPlace = new Dictionary<long, int>(); _lookAt = -1;
            ClearFocus();
            RemoveControls("clear-marks");
            if (had) RaiseChanged();
        }

        private void ClearFocus() { _focusIds = null; _pendingZoom = null; }

        /// <summary>A card row clicked (or Enter on it; the "Gabung" view, G6): its elements become Revit's selection
        /// (<paramref name="select"/>, which the card always asks for) and the view zooms to them; if none is in the open
        /// view, the view that holds most of them opens and the zoom follows there (OnIdling). The other groups' marks dim.
        /// Hidden marks are shown again first. No transaction.</summary>
        public void Focus(MarkGroup target, bool select)
        {
            if (_set == null || target == null) return;
            var wasHidden = Hidden;
            if (wasHidden) _session.ShowAgain();
            var set = _set; var generation = _generation;
            List<ElementId>? selectNext = null;   // set by the zoom job: the select follows in its own job (SelectAfterRedraw)
            long zoomedView = 0; var zoomedDoc = ""; HashSet<long>? mine = null;
            Run(select ? "focus-select" : "focus-group", Math.Max(1, target.Count), app =>
            {
                if (generation != _generation) return;
                var uidoc = app.ActiveUIDocument; var doc = uidoc?.Document; var view = uidoc?.ActiveView;
                if (uidoc == null || doc == null || view == null) return;
                var ids = (target.Kind == MarkKind.TypeInstances ? TypeInstances(doc, target) : MarkFocus.ElementIds(target, set) ?? new List<long>())
                    .Distinct().Where(id => doc.GetElement(ElemIds.From(id)) != null).ToList();
                _focusIds = new HashSet<long>(ids); _pendingZoom = null; mine = _focusIds;
                if (wasHidden || _controls.Count == 0) { Resolve(app, set); AddMarks(app); }
                else ApplyTones(app);
                if (ids.Count == 0) return;
                var eids = ids.Select(ElemIds.From).ToList();
                var here = VisibleIn(doc, view, eids);
                if (here.Count > 0)
                {
                    BinaVibe.Mcp.Tools.ViewZoom.ZoomTo(uidoc, here); _ownMove = false;   // a real zoom: the marks are sized for it
                    // The select is a second job, queued behind Revit's redraw: the view holds the keyboard from the select (ViewActivation), and in a
                    // big plan the redraw after the zoom takes seconds, in which the drafter's keys were lost.
                    if (select) { selectNext = eids; zoomedView = view.Id.Value; zoomedDoc = DocKey(doc); }
                    return;
                }
                if (select) Select(app, eids, "focus-select", generation);
                // None in the open view: open the one that holds most of them (a tag's owner view); the zoom follows there.
                var other = MostOwner(doc, eids, view.Id);
                if (other != null) { _pendingZoom = ids; uidoc.RequestViewChange(other); }
            }, () =>
            {
                if (generation != _generation) return;
                var app = _app();
                if (app != null) Draw(app);
                RaiseChanged();
                var eids = selectNext;
                if (eids != null) SelectAfterRedraw("focus-select-2", "focus-select", eids, zoomedView, zoomedDoc, generation, () => ReferenceEquals(_focusIds, mine));   // not for a newer row click
            });
        }

        /// <summary>The bar's Select all (A on the card): every element of the result is Revit's selection again, as when the
        /// turn ended, and every mark as usual (no dim group). No zoom, no view change.</summary>
        public void SelectAll()
        {
            if (_set == null) return;
            ClearFocus();
            if (Hidden) _session.ShowAgain();
            var set = _set; var generation = _generation;
            Run("select-all", Math.Max(1, _resultIds.Count), app =>
            {
                if (generation != _generation) return;
                if (_controls.Count == 0) { Resolve(app, set); AddMarks(app); }
                else ApplyTones(app);
                var ids = _resultIds.Select(ElemIds.From).ToList();
                if (ids.Count > 0) Select(app, ids, "select-all", generation);
            }, () =>
            {
                if (generation != _generation) return;
                var app = _app();
                if (app != null) Draw(app);
                RaiseChanged();
            });
        }

        /// <summary>The bar's stepper (G4): ‹ (-1), › (+1) or the "1 of 2" between them (0) selects that element of what
        /// needs a look in Revit and zooms to it, like Revit's Review Warnings; when it is in another view, that view opens
        /// and the zoom follows there (OnIdling). The marks keep their tones.</summary>
        public void Step(int delta)
        {
            if (_set == null || _lookOrder.Count == 0) return;
            _lookAt = MarkKey.StepTo(_lookAt, delta, _lookOrder.Count);
            if (_lookAt < 0) return;
            var id = _lookOrder[_lookAt];
            if (Hidden) _session.ShowAgain();
            var set = _set; var generation = _generation; var at = _lookAt;
            List<ElementId>? selectNext = null;   // set by the zoom job when the element is in the open view
            long zoomedView = 0; var zoomedDoc = "";   // the view and document it zoomed in: the select checks they are still the active ones
            // TWO Revit jobs, not one. The zoom makes Revit redraw the view, and in a large plan that takes seconds
            // (live: ~5 s on the first press). ViewActivation.Show (the selection's show) holds the keyboard and drops every key
            // until Revit's idle calls, so with the select in the same job the view kept the keyboard through the whole
            // redraw and the drafter's keys were lost. The zoom runs first, alone; Revit redraws; the select is queued
            // after it (its own job, at Revit's next idle turn) and holds the keyboard only for the short idle calls.
            Run("look-step", 1, app =>
            {
                if (generation != _generation) return;
                var uidoc = app.ActiveUIDocument; var doc = uidoc?.Document; var view = uidoc?.ActiveView;
                if (uidoc == null || doc == null || view == null) return;
                if (_controls.Count == 0) { Resolve(app, set); AddMarks(app); }
                var eid = ElemIds.From(id);
                if (doc.GetElement(eid) == null) return;
                var eids = new List<ElementId> { eid };
                var here = VisibleIn(doc, view, eids);
                if (here.Count > 0)
                {
                    BinaVibe.Mcp.Tools.ViewZoom.ZoomTo(uidoc, here); _ownMove = false;
                    selectNext = eids; zoomedView = view.Id.Value; zoomedDoc = DocKey(doc);   // the select follows in its own job (below), after Revit's redraw
                    return;
                }
                Select(app, eids, "look-step", generation);
                var other = MostOwner(doc, eids, view.Id);
                if (other != null) { _pendingZoom = new List<long> { id }; uidoc.RequestViewChange(other); }
            }, () =>
            {
                if (generation != _generation) return;
                var app = _app();
                if (app != null) Draw(app);
                RaiseChanged();
                var eids = selectNext;
                if (eids == null) return;
                SelectAfterRedraw("look-step-select", "look-step", eids, zoomedView, zoomedDoc, generation, () => at == _lookAt);   // not for a newer press
            });
        }

        /// <summary>The select of a step or a row click, as its OWN Revit job after the zoom's (the zoom makes Revit redraw, in a large plan for
        /// seconds; ViewActivation.Show holds the keyboard and drops keys until Revit's idle calls, so the two must not share a job). Queued
        /// behind the zoom job, it runs once Revit has redrawn. By then the element may be gone (deleted or undone), the view another one or the
        /// document another: then it selects nothing (the live ids only), and it is skipped for a newer press or result
        /// (<paramref name="current"/>, <paramref name="generation"/>).</summary>
        private void SelectAfterRedraw(string label, string why, List<ElementId> eids, long viewId, string docKey, int generation, Func<bool> current)
        {
            Run(label, Math.Max(1, eids.Count), app =>
            {
                if (generation != _generation || !current()) return;
                var uidoc = app.ActiveUIDocument; var doc = uidoc?.Document; var view = uidoc?.ActiveView;
                if (uidoc == null || doc == null || view == null) return;
                if (view.Id.Value != viewId || DocKey(doc) != docKey) return;   // another view or document since the zoom
                var live = eids.Where(e => doc.GetElement(e) != null).ToList(); // deleted or undone since the zoom
                if (live.Count == 0) return;
                Select(app, live, why, generation);
            }, () =>
            {
                if (generation != _generation) return;
                var a = _app();
                if (a != null) Draw(a);
                RaiseChanged();
            });
        }

        /// <summary>The bar's "Zoom to all" / "Zoom to them": fit the view to every mark of the result in it (the zoom a row click
        /// uses, for the whole result). The focus stays.</summary>
        public void ZoomToAll()
        {
            if (_set == null) return;
            var set = _set;
            // The marks may not be resolved yet (the card's "Zoom to all" on a result whose marks were never shown, or were cleared): resolve them in the
            // job, as Select all does, so the FIRST click zooms instead of finding nothing.
            Run("zoom-all", Math.Max(1, _marks.Count), app =>
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                if (_marks.Count == 0 && _resultIds.Count == 0) Resolve(app, set);
                var ids = (_marks.Count > 0 ? _marks.Select(m => m.Id) : _resultIds).Select(ElemIds.From).ToList();
                if (ids.Count > 0) { BinaVibe.Mcp.Tools.ViewZoom.ZoomTo(uidoc, ids); _ownMove = false; }
            }, null);   // the zoom settles in OnIdling, which draws the bar again with its new count
        }

        /// <summary>How many drawn marks lie inside the view's visible rectangle (MarkKey.OnScreen); null when it cannot be said
        /// (a perspective view, no zoom corners).</summary>
        private int? OnScreenCount(UIApplication app)
        {
            if (_perspective || _marks.Count == 0) return null;
            try
            {
                var uidoc = app.ActiveUIDocument; var view = uidoc?.ActiveView;
                if (uidoc == null || view == null) return null;
                var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                var corners = ui?.GetZoomCorners();
                if (corners == null || corners.Count < 2) return null;
                Vec3 V(XYZ p) => new Vec3(p.X, p.Y, p.Z);
                return MarkKey.OnScreen(_marks.Select(m => V(m.Point)), V(corners[0]), V(corners[1]), V(view.RightDirection), V(view.UpDirection));
            }
            catch { return null; }
        }

        /// <summary>A mark clicked (MarkClickHandler, on Revit's thread): select that one element in Revit, focus the row it
        /// belongs to, name it next to the mark, then tell the card (MarkClicked).</summary>
        private void OnMarkClicked(Document doc, int index)
        {
            try
            {
                if (_set == null || !_byControl.TryGetValue(index, out var drawn) || drawn.Marks.Count == 0) return;   // not one of ours
                if (doc != null && _docKey.Length > 0 && DocKey(doc) != _docKey) return;
                var set = _set; var generation = _generation;
                if (drawn.Marks.Count > 1)
                {
                    // A badge: its elements become Revit's selection, in one call.
                    var ids = drawn.Marks.Select(m => m.Id).ToList();
                    Run("badge-click", ids.Count, app =>
                    {
                        if (generation != _generation) return;
                        var d = app.ActiveUIDocument?.Document; if (d == null) return;
                        var eids = ids.Select(ElemIds.From).Where(id => d.GetElement(id) != null).ToList();
                        if (eids.Count > 0) Select(app, eids, "badge-click", generation);
                    }, null);
                    return;
                }
                var mark = drawn.Marks[0];
                MarkClick? click = null;
                Run("mark-click", 1, app =>
                {
                    if (generation != _generation) return;
                    var uidoc = app.ActiveUIDocument; var d = uidoc?.Document;
                    if (uidoc == null || d == null) return;
                    var eid = ElemIds.From(mark.Id);
                    var e = d.GetElement(eid);
                    if (e == null) return;
                    Select(app, new List<ElementId> { eid }, "mark-click", generation);   // one element, not the 38 held before
                    var category = ""; try { category = e.Category?.Name ?? ""; } catch { }
                    long typeId = -1; try { var t = e.GetTypeId(); if (t != null && t != ElementId.InvalidElementId) typeId = t.Value; } catch { }
                    var groups = GroupsWithElements(d, set);
                    var at = MarkFocus.Locate(mark.Id, groups);
                    // A row of the card names it ("3 of 8"); a kind (no row has it) only says what it is.
                    var isRow = at != null && at.Value.Index < set.Rows.Count;
                    var row = isRow ? groups[at!.Value.Index].Group : null;
                    click = MarkFocus.Click(set.TurnId, mark.Id, typeId, category, row, isRow ? at!.Value.N : 0, isRow ? at!.Value.M : 0);
                    // The row it belongs to is in focus (the other marks dim), as after a click on that row.
                    _focusIds = row != null ? new HashSet<long>(groups[at!.Value.Index].Elements) : null;
                    _pendingZoom = null;
                    ApplyTones(app);
                }, () =>
                {
                    if (generation != _generation) return;
                    var app = _app();
                    if (app != null) Draw(app);
                    if (click != null)
                        try { MarkClicked?.Invoke(click); } catch (Exception ex) { Debug.WriteLine("[BINA] mark clicked: " + ex.Message); }
                });
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] mark click: " + ex.Message); }
        }

        /// <summary>The card's rows, then the result's kinds, each with its elements (a type's instances found in Revit).</summary>
        private List<(MarkGroup Group, IReadOnlyList<long> Elements)> GroupsWithElements(Document doc, HighlightSet set)
        {
            var list = new List<(MarkGroup, IReadOnlyList<long>)>();
            foreach (var g in set.Rows.Concat(set.Kinds))
                list.Add((g, g.Kind == MarkKind.TypeInstances ? TypeInstances(doc, g) : g.Ids));
            return list;
        }

        /// <summary>Which of the ids the view shows (one view-filtered collector).</summary>
        private static List<ElementId> VisibleIn(Document doc, View view, List<ElementId> ids)
        {
            try { return new FilteredElementCollector(doc, view.Id).WherePasses(new ElementIdSetFilter(ids)).ToElementIds().ToList(); }
            catch (Exception ex) { Debug.WriteLine("[BINA] focus visible: " + ex.Message); return new List<ElementId>(); }
        }

        /// <summary>The view that owns most of the elements (tags and dimensions belong to one view), not the open one; null when
        /// none does (model elements: no one view to name).</summary>
        private static View? MostOwner(Document doc, List<ElementId> ids, ElementId openView)
        {
            var counts = new Dictionary<long, int>();
            foreach (var id in ids)
            {
                try
                {
                    var o = doc.GetElement(id)?.OwnerViewId;
                    if (o == null || o == ElementId.InvalidElementId || o == openView) continue;
                    counts[o.Value] = (counts.TryGetValue(o.Value, out var n) ? n : 0) + 1;
                }
                catch { /* not view-specific */ }
            }
            foreach (var kv in counts.OrderByDescending(kv => kv.Value))
                if (doc.GetElement(ElemIds.From(kv.Key)) is View v && !v.IsTemplate) return v;
            return null;
        }

        /// <summary>Give each mark its tone (dim outside the focused group, usual in it or with no focus). A tone change also
        /// changes which dots go together on a badge (a badge is of one colour and tone), so the marks are drawn again.</summary>
        private void ApplyTones(UIApplication app)
        {
            if (_controls.Count == 0 || _marks.All(m => m.Tone == ToneOf(m))) return;
            Redraw(app);
        }

        /// <summary>Take the drawn controls away and draw the marks again (AddMarks repaints).</summary>
        private void Redraw(UIApplication app)
        {
            var old = _controls.ToList(); _controls.Clear(); _byControl.Clear();
            if (old.Count > 0) RemoveMarks(app, old);
            AddMarks(app);
        }

        /// <summary>The bar's Go to view: make the view that holds the elements the active one (Revit's RequestViewChange; no
        /// transaction). The view change re-resolves the marks there (OnIdling).</summary>
        public void OpenView(long viewId)
        {
            Run("open-view", 1, app =>
            {
                var uidoc = app.ActiveUIDocument; if (uidoc == null) return;
                if (uidoc.Document.GetElement(ElemIds.From(viewId)) is View v && !v.IsTemplate) uidoc.RequestViewChange(v);
            }, null);
        }

        /// <summary>Select a group's elements (instances of a type group) in Revit: one SetElementIds call, no zoom.</summary>
        public void SelectGroup(TypeGroup g)
        {
            if (g == null) return;
            Run("select-group", g.Instances, app =>
            {
                var uidoc = app.ActiveUIDocument; if (uidoc == null) return;
                var ids = InstancesOf(uidoc.Document, g).Select(ElemIds.From).ToList();
                if (ids.Count > 0) Select(app, ids, "select-group", _generation);
            }, null);
        }

        /// <summary>Make <paramref name="ids"/> Revit's selection and have Revit show it (ViewActivation: a selection made
        /// from the pane is held but shown nowhere until the view has been activated), then repaint. The log line says how
        /// many Revit holds and what the activation did.</summary>
        private void Select(UIApplication app, List<ElementId> ids, string why, int generation)
        {
            if (generation != _generation) return;
            var uidoc = app.ActiveUIDocument; if (uidoc == null) return;
            uidoc.Selection.SetElementIds(ids);
            var shown = BinaVibe.Copilot.Writes.ViewActivation.Show(app);
            // With the Project Browser as the active view Revit takes no selection at all (live round 17, P2: "Revit holds 0"
            // for 126 pipes after a click in the browser; the bar's Select all a moment later held 126). Once the drawing
            // view has been activated it is set again, here and, should that not take either, at the next Idling call.
            var held = uidoc.Selection.GetElementIds().Count;
            if (ids.Count > 0 && held == 0)
            {
                uidoc.Selection.SetElementIds(ids);
                held = uidoc.Selection.GetElementIds().Count;
                shown += held > 0 ? ", set again after the activation" : ", not taken: set again at the next Idling call";
                if (held == 0) { _selectAgain = ids; _selectAgainWhy = why; }
            }
            RepaintActive(app);
            try { ClickTrace.Sink("select " + why + " n=" + ids.Count + ": Revit holds " + held + ", " + shown); }
            catch { /* the log is a convenience */ }
        }

        private List<ElementId>? _selectAgain;
        private string _selectAgainWhy = "";

        /// <summary>Zoom the active view to a group's elements: one ZoomAndCenterRectangle (ViewZoom), not ShowElements.</summary>
        public void ZoomGroup(TypeGroup g)
        {
            if (g == null) return;
            Run("zoom-group", g.Instances, app =>
            {
                var uidoc = app.ActiveUIDocument; if (uidoc == null) return;
                var ids = InstancesOf(uidoc.Document, g).Select(ElemIds.From).ToList();
                if (ids.Count > 0) BinaVibe.Mcp.Tools.ViewZoom.ZoomTo(uidoc, ids);
            }, null);
        }

        /// <summary>Select one instance of the group and open Revit's Type Properties dialog.</summary>
        public void OpenTypeProperties(TypeGroup g)
        {
            if (g == null) return;
            Run("type-properties", 1, app =>
            {
                var uidoc = app.ActiveUIDocument; if (uidoc == null) return;
                var first = InstancesOf(uidoc.Document, g).Take(1).Select(ElemIds.From).ToList();
                if (first.Count == 0) return;
                uidoc.Selection.SetElementIds(first);
                var id = RevitCommandId.LookupPostableCommandId(PostableCommand.TypeProperties);
                if (id != null && app.CanPostCommand(id)) app.PostCommand(id);
            }, null);
        }

        // ─── Revit side ───────────────────────────────────────────────────────

        private void Run(string label, int count, Action<UIApplication> work, Action? then)
        {
            // Traced: the click's line in copilot-clicks.log says how long it queued for Revit and how long it ran.
            var task = RevitWebAppSync.UI.Copilot.Ports.TracedRevit.Run<object?>(label, count,
                app => { try { work(app); } catch (Exception ex) { Debug.WriteLine("[BINA] in-model: " + ex.Message); } return null; }, null);
            if (then != null)
                task.ContinueWith(_ => _ui.BeginInvoke(then), System.Threading.Tasks.TaskScheduler.Default);
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); } catch (Exception ex) { Debug.WriteLine("[BINA] in-model changed: " + ex.Message); }
        }

        private static string DocKey(Document doc) => doc.PathName + "|" + doc.Title;

        /// <summary>Where every marked element is, for the active view; and the marks to draw (only elements in it).</summary>
        private void Resolve(UIApplication app, HighlightSet set)
        {
            var uidoc = app.ActiveUIDocument; var doc = uidoc?.Document; var view = uidoc?.ActiveView;
            _marks.Clear(); _places = null;
            if (doc == null || view == null) return;
            _viewId = view.Id.Value; _viewName = view.Name ?? "";
            _docKey = DocKey(doc);
            _perspective = view is View3D v3 && v3.IsPerspective;
            try
            {
                var bg = app.Application.BackgroundColor;
                _darkCanvas = bg == null || (0.2126 * bg.Red + 0.7152 * bg.Green + 0.0722 * bg.Blue) < 128;
            }
            catch { _darkCanvas = true; }

            // Each group's elements; a type group's are its instances.
            var groups = new List<(int Group, MarkGroup Kind, List<Element> Elements)>();
            for (var i = 0; i < set.Kinds.Count; i++)
            {
                var k = set.Kinds[i];
                var els = new List<Element>();
                IEnumerable<long> ids = k.Kind == MarkKind.TypeInstances ? TypeInstances(doc, k) : k.Ids;
                foreach (var id in ids) { var e = doc.GetElement(ElemIds.From(id)); if (e != null) els.Add(e); }
                groups.Add((i, k, els));
            }

            // What the active view shows: one view-filtered collector over every id.
            var visible = new HashSet<long>();
            var all = groups.SelectMany(g => g.Elements).Select(e => e.Id).Distinct().ToList();
            _resultIds = all.Select(id => (long)id.Value).ToList();
            if (all.Count > 0)
            {
                try
                {
                    foreach (var id in new FilteredElementCollector(doc, view.Id).WherePasses(new ElementIdSetFilter(all)).ToElementIds())
                        visible.Add(id.Value);
                }
                catch (Exception ex) { Debug.WriteLine("[BINA] marks visible: " + ex.Message); }   // a sheet or schedule: nothing is drawn here
            }

            var places = new List<ElementPlace>();
            var viewNames = new Dictionary<long, (string Name, bool Plan)>();
            var best = new Dictionary<long, Mark>();
            foreach (var (gi, k, els) in groups)
                foreach (var e in els)
                {
                    var id = e.Id.Value;
                    var here = visible.Contains(id);
                    long? owner = null;
                    try { var o = e.OwnerViewId; if (o != null && o != ElementId.InvalidElementId && o.Value != _viewId) owner = o.Value; } catch { }
                    var place = new ElementPlace { Id = id, Group = gi, InActiveView = here };
                    if (!here && owner is long ov)
                    {
                        if (!viewNames.TryGetValue(ov, out var vn))
                        {
                            var ownerView = doc.GetElement(ElemIds.From(ov)) as View;
                            viewNames[ov] = vn = (ownerView?.Name ?? "another view", ownerView is ViewPlan);
                        }
                        place.ViewId = ov; place.ViewName = vn.Name; place.IsPlan = vn.Plan;
                    }
                    places.Add(place);
                    if (!here) continue;
                    // One mark per element: a question first, then a change, then a new thing.
                    if (best.TryGetValue(id, out var had) && HighlightSet.Priority(had.Outcome) >= HighlightSet.Priority(k.Outcome)) continue;
                    if (!best.ContainsKey(id) && best.Count >= MaxMarks) continue;
                    var p = LocationOf(e, view);
                    if (p == null) continue;
                    var frame = MarkKey.IsFrame(KindOf(e, k));
                    BoundingBoxXYZ? box = null;
                    if (frame) { try { box = e.get_BoundingBox(view); } catch { } }   // a frame is sized to it; a dot has one size
                    best[id] = new Mark { Id = id, Group = gi, Outcome = k.Outcome, Frame = frame, Point = p, Box = box };
                }
            foreach (var m in best.Values)
                if (m.Outcome == MarkOutcome.NeedsLook) m.Number = MarkKey.NumberOf(m.Id, _lookPlace);
            _marks.AddRange(best.Values);
            _places = places;
            SizeMarks(uidoc!, view);
        }

        /// <summary>A tag, text or dimension gets a frame whatever its category is called.</summary>
        private static MarkKind KindOf(Element e, MarkGroup k)
        {
            if (e is Dimension) return MarkKind.Dimension;
            if (e is IndependentTag || e is TextNote || e is SpatialElementTag) return MarkKind.Annotation;
            return k.Kind == MarkKind.TypeInstances ? MarkKind.Element : k.Kind;
        }

        /// <summary>Each frame's size step from its box on screen (pixels per foot from the view's window and zoom).
        /// True when any step changed.</summary>
        private bool SizeMarks(UIDocument uidoc, View view)
        {
            var changed = false;
            double pxPerFt = 0;
            try
            {
                var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                var corners = ui?.GetZoomCorners();
                if (ui != null && corners != null && corners.Count >= 2 && !_perspective)
                {
                    var rect = ui.GetWindowRectangle();
                    var widthFt = Math.Abs((corners[1] - corners[0]).DotProduct(view.RightDirection));
                    if (widthFt > 1e-9) pxPerFt = (rect.Right - rect.Left) / (_dpi <= 0 ? 1 : _dpi) / widthFt;
                }
            }
            catch { pxPerFt = 0; }
            foreach (var m in _marks.Where(m => m.Frame))
            {
                var size = MarkKey.Sizes[0];
                if (pxPerFt > 0 && m.Box != null)
                {
                    var (w, h) = Extent(m.Box, view.RightDirection, view.UpDirection);
                    size = MarkKey.SizeFor(w * pxPerFt, h * pxPerFt);
                }
                if (size != m.Size) { m.Size = size; changed = true; }
            }
            return changed;
        }

        private static (double W, double H) Extent(BoundingBoxXYZ b, XYZ right, XYZ up)
        {
            double r0 = double.MaxValue, r1 = double.MinValue, u0 = double.MaxValue, u1 = double.MinValue;
            foreach (var x in new[] { b.Min.X, b.Max.X })
                foreach (var y in new[] { b.Min.Y, b.Max.Y })
                    foreach (var z in new[] { b.Min.Z, b.Max.Z })
                    {
                        var p = new XYZ(x, y, z);
                        var r = p.DotProduct(right); var u = p.DotProduct(up);
                        r0 = Math.Min(r0, r); r1 = Math.Max(r1, r); u0 = Math.Min(u0, u); u1 = Math.Max(u1, u);
                    }
            return (r1 - r0, u1 - u0);
        }

        private static XYZ? LocationOf(Element e, View view)
        {
            try
            {
                // An annotation's mark goes round what is drawn (its box), not its anchor point.
                if (!(e is IndependentTag) && !(e is Dimension) && !(e is TextNote))
                {
                    if (e.Location is LocationPoint lp) return lp.Point;
                    if (e.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Evaluate(0.5, true);
                }
                var bb = e.get_BoundingBox(view);
                return bb == null ? null : (bb.Min + bb.Max) / 2;
            }
            catch { return null; }
        }

        /// <summary>Have Revit paint the view again. Revit draws an in-canvas control that was added, changed or removed only
        /// at the view's next real redraw: in the 0.0.110 probe (live round 13) AddControl took all 16 marks, RefreshActiveView
        /// painted none of them, and one notch of the drafter's zoom painted them all. So the zoom is nudged by a tenth of a
        /// percent, out and in by turns so the view never drifts, and the idle watcher is told the move is ours (it does not
        /// size the marks again for it). A view with no zoom corners gets the plain refresh.</summary>
        private void Repaint(UIDocument uidoc, View view)
        {
            try
            {
                var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                var corners = ui?.GetZoomCorners();
                if (ui == null || corners == null || corners.Count < 2 || _perspective) { uidoc.RefreshActiveView(); return; }
                var nudge = (corners[1] - corners[0]) * (_nudgeOut ? MarkKey.RepaintNudge : -MarkKey.RepaintNudge);
                _nudgeOut = !_nudgeOut;
                ui.ZoomAndCenterRectangle(corners[0] - nudge, corners[1] + nudge);
                _ownMove = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[BINA] mark repaint: " + ex.Message);
                try { uidoc.RefreshActiveView(); } catch { /* nothing more to try */ }
            }
        }

        private void RepaintActive(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument; var view = uidoc?.ActiveView;
            if (uidoc != null && view != null) Repaint(uidoc, view);
        }

        private static readonly System.Reflection.MethodInfo? SetTooltip =
            typeof(TemporaryGraphicsManager).GetMethod("SetTooltip", new[] { typeof(int), typeof(string) });

        private void AddMarks(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument; var doc = uidoc?.Document; var view = uidoc?.ActiveView;
            if (uidoc == null || doc == null || view == null || _marks.Count == 0) return;
            MarkClickHandler.EnsureRegistered();   // once per session: a click on a mark comes back to OnMarkClicked
            if (_idlingApp == null) StartIdling(app);   // a subscription Revit refused is asked for again here, in its context
            var tgm = TemporaryGraphicsManager.GetTemporaryGraphicsManager(doc);
            string? error = null; var image = "";
            foreach (var m in _marks) m.Tone = ToneOf(m);
            List<Drawn> layout;
            try { layout = Layout(uidoc, view); }
            catch (Exception ex) { layout = new List<Drawn>(); error = ex.GetType().Name + ": " + ex.Message; }
            _layoutKey = LayoutKey(layout);
            foreach (var d in layout)
            {
                try
                {
                    image = d.Image;
                    var index = tgm.AddControl(new InCanvasControlData(d.Image, d.Point), view.Id);
                    _controls.Add(index);
                    _byControl[index] = d;
                    // Revit's own tooltip for a control reads "Control": say what the mark is
                    // (SetTooltip is only in the newer hosts' API, so it is looked up, not linked).
                    try { var tip = TooltipOf(d); if (tip != null) SetTooltip?.Invoke(tgm, new object[] { index, tip }); }
                    catch (Exception ex) { Debug.WriteLine("[BINA] mark tooltip: " + ex.Message); }
                }
                catch (Exception ex) { error ??= ex.GetType().Name + ": " + ex.Message; Debug.WriteLine("[BINA] mark: " + error); }
            }
            if (Environment.GetEnvironmentVariable("BINA_MARK_PROBE") == "1") Probe(tgm, uidoc, view, image);
            Repaint(uidoc, view);
            // What Revit took, in the clicks log: live round 13 had a key that said "Showing all 16" over a view with no mark.
            try
            {
                var first = _marks[0].Point; var inRevit = -1;
                try { inRevit = tgm.GetAll().Count; } catch { /* an older host */ }
                string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                ClickTrace.Sink("marks in " + view.Name + ": due " + _marks.Count + ", controls " + _controls.Count + ", Revit holds " + inRevit
                    + ", first at (" + F(first.X) + ", " + F(first.Y) + ", " + F(first.Z) + ") ft, image " + System.IO.Path.GetFileName(image)
                    + (error != null ? ", first error " + error : ""));
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] mark trace: " + ex.Message); }
        }

        /// <summary>What to draw for the marks (G2, G3): a frame for each annotation; a dot for each other element, where the
        /// dots of one colour and tone that would overlap on screen (MarkCluster) become one badge with their count, at the
        /// middle of their points. With no screen points (a perspective view) every dot is drawn alone.</summary>
        private List<Drawn> Layout(UIDocument uidoc, View view)
        {
            var list = new List<Drawn>();
            foreach (var m in _marks.Where(m => m.Frame && m.Number == 0))
                list.Add(new Drawn { Marks = { m }, Point = m.Point, Image = MarkImages.FramePath(m.Outcome, m.Size, _darkCanvas, MarkKey.ImageScale, m.Tone) });
            var screen = ScreenOf(uidoc, view);
            // What needs a look: an amber numbered badge each (G4), never merged into a count; two that would overlap are
            // one badge with the lower number and a "+", on that one's element.
            var numbered = _marks.Where(m => m.Number > 0).OrderBy(m => m.Number).ToList();
            var near = screen == null
                ? numbered.Select((_, i) => new List<int> { i }).ToList()
                : MarkCluster.Group(numbered.Select(m => screen(m.Point)).ToList(), MarkKey.BadgeDip);
            foreach (var members in near)
            {
                // The badge of several shows the lowest number, or the one the stepper is on (live G-G: the bar said "3 of
                // 16" and the canvas showed only "1+").
                var stepped = _lookAt >= 0 && _lookAt < _lookOrder.Count ? _lookOrder[_lookAt] : -1;
                var first = members.Select(i => numbered[i]).FirstOrDefault(m => m.Id == stepped) ?? numbered[members[0]];
                list.Add(new Drawn
                {
                    Marks = members.Select(i => numbered[i]).ToList(), Point = first.Point,
                    Image = MarkImages.BadgePath(MarkOutcome.NeedsLook, MarkCluster.NumberLabel(first.Number, members.Count > 1), _darkCanvas, MarkKey.ImageScale),
                });
            }
            foreach (var g in _marks.Where(m => !m.Frame && m.Number == 0).GroupBy(m => (m.Outcome, m.Tone)))
            {
                var dots = g.ToList();
                var groups = screen == null
                    ? dots.Select((_, i) => new List<int> { i }).ToList()
                    : MarkCluster.Group(dots.Select(m => screen(m.Point)).ToList(), MarkKey.DotDip);
                foreach (var members in groups)
                {
                    var d = new Drawn { Marks = members.Select(i => dots[i]).ToList() };
                    if (d.Marks.Count == 1)
                    {
                        d.Point = d.Marks[0].Point;
                        d.Image = MarkImages.DotPath(g.Key.Outcome, _darkCanvas, MarkKey.ImageScale, g.Key.Tone);
                    }
                    else
                    {
                        // The middle of their points in the model is the middle on screen too (a parallel projection).
                        d.Point = d.Marks.Aggregate(XYZ.Zero, (sum, m) => sum + m.Point) / d.Marks.Count;
                        d.Image = MarkImages.BadgePath(g.Key.Outcome, MarkCluster.CountLabel(d.Marks.Count), _darkCanvas, MarkKey.ImageScale, g.Key.Tone);
                    }
                    list.Add(d);
                }
            }
            return list;
        }

        /// <summary>A mark's tone: dim outside the focused group, except a numbered one (what needs a look is never dimmed).</summary>
        private MarkTone ToneOf(Mark m) => m.Number > 0 ? MarkTone.Normal : MarkFocus.ToneOf(m.Id, _focusIds);

        private static string LayoutKey(List<Drawn> layout) =>
            string.Join("|", layout.Select(d => d.Image + ":" + string.Join(",", d.Marks.Select(m => m.Id))));

        /// <summary>The settled zoom moved the marks on screen: the frames' sizes or the dots that go together changed.</summary>
        private bool Relaid(UIDocument uidoc, View view)
        {
            SizeMarks(uidoc, view);
            return LayoutKey(Layout(uidoc, view)) != _layoutKey;
        }

        /// <summary>A model point's place on screen in DIP (from the zoom corners and the view's window); null when it cannot
        /// be said (a perspective view, no zoom corners).</summary>
        private Func<XYZ, (double X, double Y)>? ScreenOf(UIDocument uidoc, View view)
        {
            if (_perspective) return null;
            try
            {
                var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                var corners = ui?.GetZoomCorners();
                if (ui == null || corners == null || corners.Count < 2) return null;
                var rect = ui.GetWindowRectangle();
                var scale = _dpi <= 0 ? 1 : _dpi;
                double w = (rect.Right - rect.Left) / scale, h = (rect.Bottom - rect.Top) / scale;
                if (w <= 0 || h <= 0) return null;
                Vec3 V(XYZ p) => new Vec3(p.X, p.Y, p.Z);
                var c0 = V(corners[0]); var c1 = V(corners[1]); var right = V(view.RightDirection); var up = V(view.UpDirection);
                return p =>
                {
                    var pct = ViewProjection.ToPercent(V(p), c0, c1, right, up);
                    return pct == null ? (double.NaN, double.NaN) : (pct.Value.XPct * w / 100, pct.Value.YPct * h / 100);
                };
            }
            catch { return null; }
        }

        /// <summary>What a mark says when the pointer rests on it: a dot or frame its kind, a badge how many it holds.</summary>
        private string? TooltipOf(Drawn d)
        {
            if (_set == null || d.Marks.Count == 0) return null;
            if (d.Marks.Count > 1) return MarkKey.BadgeTooltip(d.Marks.Count);
            var g = d.Marks[0].Group;
            return g >= 0 && g < _set.Kinds.Count ? MarkKey.Tooltip(_set.Kinds[g]) : null;
        }

        /// <summary>For a test build only (BINA_MARK_PROBE=1): three more controls that tell where a missing mark is lost.
        /// A is the last mark's own image 3 ft to the right of the first mark, shown in all views; B is a solid 32 px square
        /// at the middle of the view, owned by the view; C is a solid 64 px square 5 ft above B, shown in all views.</summary>
        private void Probe(TemporaryGraphicsManager tgm, UIDocument uidoc, View view, string image)
        {
            try
            {
                string Solid(int px)
                {
                    var path = System.IO.Path.Combine(MarkImages.Folder, "probe-" + px + MarkBitmap.Extension);
                    var pixels = new byte[px * px * 4];
                    for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 255; pixels[i + 1] = 0; pixels[i + 2] = 255; pixels[i + 3] = 255; }
                    System.IO.Directory.CreateDirectory(MarkImages.Folder);
                    System.IO.File.WriteAllBytes(path, MarkBitmap.Encode(pixels, px, px));
                    return path;
                }
                var notes = new List<string>();
                void Try(string name, Func<int> add) { try { notes.Add(name + "=" + add()); } catch (Exception ex) { notes.Add(name + " " + ex.GetType().Name + ": " + ex.Message); } }
                var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                var corners = ui?.GetZoomCorners();
                var middle = corners != null && corners.Count >= 2 ? (corners[0] + corners[1]) / 2 : _marks[0].Point;
                if (image.Length > 0) Try("A", () => tgm.AddControl(new InCanvasControlData(image, _marks[0].Point + view.RightDirection * 3), ElementId.InvalidElementId));
                Try("B", () => tgm.AddControl(new InCanvasControlData(Solid(32), middle), view.Id));
                Try("C", () => tgm.AddControl(new InCanvasControlData(Solid(64), middle + view.UpDirection * 5), ElementId.InvalidElementId));
                ClickTrace.Sink("marks probe in " + view.Name + ": " + string.Join(", ", notes));
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] mark probe: " + ex.Message); }
        }

        /// <summary>Remove the drawn controls (through Revit's context).</summary>
        private void RemoveControls(string label)
        {
            if (_controls.Count == 0) return;
            var ids = _controls.ToList(); _controls.Clear(); _byControl.Clear();
            Run(label, ids.Count, app => { RemoveMarks(app, ids); RepaintActive(app); }, null);
        }

        private static void RemoveMarks(UIApplication app, List<int> ids)
        {
            var doc = app.ActiveUIDocument?.Document; if (doc == null) return;
            var tgm = TemporaryGraphicsManager.GetTemporaryGraphicsManager(doc);
            foreach (var i in ids) { try { tgm.RemoveControl(i); } catch { /* already gone */ } }
        }

        /// <summary>A type group's instances (its dependents; a scan of the model only for a type that gives none).</summary>
        private List<long> TypeInstances(Document doc, MarkGroup k)
        {
            var key = string.Join(",", k.Ids);
            if (_groupInstances.TryGetValue(key, out var have)) return have;
            var g = new TypeGroup { TypeName = k.Title, IsType = true, Ids = k.Ids.ToList() };
            var found = InstancesOf(doc, g).ToList();
            _groupInstances[key] = found;
            return found;
        }

        // The instances of a type group, kept per model: a Zoom or Select after the result was cleared must not scan
        // every element of the model again (a 2.3 s freeze in the 0.0.106 probe).
        private readonly Dictionary<string, List<long>> _instanceCache = new Dictionary<string, List<long>>();
        private string _instanceCacheDoc = "";

        private IEnumerable<long> InstancesOf(Document doc, TypeGroup g)
        {
            if (!g.IsType) return g.Ids;
            var docKey = DocKey(doc);
            if (docKey != _instanceCacheDoc) { _instanceCache.Clear(); _instanceCacheDoc = docKey; }
            var key = string.Join(",", g.Ids);
            if (_instanceCache.TryGetValue(key, out var kept) && kept.Count > 0) return kept;

            // The type's dependents, a lookup rather than a scan of the whole model; but only those OF the type are its
            // instances. The rest (the instances' tags in their plans, a legend component) are not: live 13 Nov, F-t04b, 52
            // fixtures were counted as 104 marks, 50 of them "in 2 other views".
            var found = new List<long>();
            try
            {
                var notTypes = new ElementIsElementTypeFilter(true);
                var deps = new List<(long Id, long TypeId)>();
                foreach (var tid in g.Ids)
                    if (doc.GetElement(ElemIds.From(tid)) is ElementType t)
                        foreach (var dep in t.GetDependentElements(notTypes))
                        {
                            var typeId = doc.GetElement(dep)?.GetTypeId();
                            deps.Add((dep.Value, typeId == null || typeId == ElementId.InvalidElementId ? -1 : typeId.Value));
                        }
                found = HighlightSet.InstancesOnly(deps, g.Ids);
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] instances of type: " + ex.Message); found.Clear(); }
            if (found.Count == 0)
            {
                var want = new HashSet<long>(g.Ids);
                found = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .Where(e => e.GetTypeId() != null && want.Contains(e.GetTypeId().Value)).Select(e => (long)e.Id.Value).ToList();
            }
            _instanceCache[key] = found;
            return found;
        }

        // ─── The bar ──────────────────────────────────────────────────────────

        /// <summary>Build the bar (or the pill) from the resolved places and draw it over the active view.</summary>
        private void Draw(UIApplication app)
        {
            if (_set == null) return;
            var key = MarkKey.Build(_set, _places, _viewName);
            var undoOk = _undo != null && SafeCanUndo();
            // The bar speaks about what is on screen, and offers the zoom when marks are off it (never zooms itself).
            if (!Hidden) MarkKey.ApplyOnScreen(key, _marks.Count, OnScreenCount(app));
            if (!Hidden) MarkKey.ApplyNotDrawn(key, _marks.Count, _controls.Count);   // the bar says what is drawn, not what was meant
            key.Step = MarkKey.StepLabel(_lookAt, _lookOrder.Count);
            _model = new OverlayModel
            {
                Key = key,
                Hidden = Hidden,
                SelectAll = _resultIds.Count > 0 ? (Action)SelectAll : null,
                ZoomAll = key.ZoomLabel != null ? (Action)ZoomToAll : null,
                Step = _lookOrder.Count > 0 ? (Action<int>)Step : null,
                Undo = undoOk ? (Action)UndoFromKey : null,
                Hide = Hide,
                ShowAgain = ShowAgain,
                Dismiss = Clear,
                Open = key.OpenViewId is long vid ? (Action)(() => OpenView(vid)) : null,
            };
            if (!_overlay.ShowResult(app, _model)) { /* no view to draw on: the bar comes back on the next view */ }
        }

        /// <summary>Clear first, then Undo: a failed undo never leaves marks behind. Nothing happens when the turn can no
        /// longer be undone (Revit's own Undo ran, or the model changed after it).</summary>
        private void UndoFromKey()
        {
            var u = _undo;
            if (u == null || !SafeCanUndo()) { var app = _app(); if (app != null) Draw(app); return; }
            Clear();
            u();
        }

        private bool SafeCanUndo()
        {
            try { return _canUndo?.Invoke() ?? true; } catch { return false; }
        }

        // ─── Navigation ───────────────────────────────────────────────────────

        /// <summary>Follow the view from now on, through the add-in's one permanent Idling subscription (RevitThread.Idle).
        /// Revit's own Idling event cannot be subscribed here: from the pane's thread it never took (the key did not follow
        /// a change of view or a zoom), and from the work Revit runs it is refused whenever that work runs inside an Idling
        /// call ("Can not subscribe to an event during execution of that event!", live round 13 on 0.0.112), which is
        /// most of the time. One listener for the session: OnIdling returns at once while no result is shown.</summary>
        private void StartIdling(UIApplication app)
        {
            _c0 = _c1 = null; _dirty = false;
            if (_idlingApp != null) return;
            BinaVibe.Copilot.Writes.RevitThread.Idle += OnIdle;
            _idlingApp = app;
            ClickTrace.Sink("marks idle watch on");
        }

        private void OnIdle(UIApplication app, Autodesk.Revit.UI.Events.IdlingEventArgs e) => OnIdling(app, e);

        // What the idle watcher did, in the clicks log (live round 13: on 0.0.111 the key stopped following the view and
        // nothing said why): its first call, each change of view, each settled zoom, and an error once per message.
        private bool _idleSeen; private string _idleError = ""; private int _movedCalls;
        private void IdleNote(string text) { try { ClickTrace.Sink("marks idle: " + text); } catch { /* the log is a convenience */ } }

        /// <summary>The result is gone: nothing to follow. The subscription stays (a call from the pane's thread could not
        /// take it away either).</summary>
        private void StopIdling() { _c0 = _c1 = null; _dirty = false; }

        // Idling runs in Revit's API context on the UI thread: the marks and the overlay are redrawn here directly.
        private void OnIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            try
            {
                if (!_idleSeen) { _idleSeen = true; IdleNote("first call, result " + (_set == null ? "none" : "shown")); }
                if (_set == null) return;
                var app = sender as UIApplication ?? _idlingApp; var uidoc = app?.ActiveUIDocument;
                var view = uidoc?.ActiveView;
                if (app == null || uidoc == null || view == null) return;
                // Another model: this result is not about it.
                if (DocKey(uidoc.Document) != _docKey && _docKey.Length > 0) { Clear(); return; }
                var again = _selectAgain; _selectAgain = null;
                if (again != null)
                {
                    uidoc.Selection.SetElementIds(again);
                    IdleNote("select " + _selectAgainWhy + " n=" + again.Count + " set again: Revit holds " + uidoc.Selection.GetElementIds().Count + ", active view " + view.Name);
                }
                // A click in the Project Browser makes "Project View" the active view: the result's view is still the one on
                // screen (live G-E: the bar said "None of the 52 is visible in this view" over a view full of dots).
                if (view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser) return;
                if (view.Id.Value != _viewId)
                {
                    // A view change keeps the result: the marks are re-resolved for the view that is open now.
                    IdleNote("view " + _viewName + " -> " + view.Name);
                    var old = _controls.ToList(); _controls.Clear(); _byControl.Clear();
                    if (old.Count > 0) RemoveMarks(app, old);
                    Resolve(app, _set);
                    if (!Hidden) AddMarks(app);
                    _c0 = _c1 = null; _dirty = false;
                    // A row whose elements are in this view asked for it: zoom to them now that it is open.
                    var pending = _pendingZoom; _pendingZoom = null;
                    if (pending != null)
                    {
                        var here = VisibleIn(uidoc.Document, view, pending.Select(ElemIds.From).ToList());
                        if (here.Count > 0) { BinaVibe.Mcp.Tools.ViewZoom.ZoomTo(uidoc, here); _ownMove = false; }
                    }
                    Draw(app);
                    RaiseChanged();   // the card's header says where the marks are now ("Go to view" goes or comes)
                    return;
                }
                var ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                var corners = ui?.GetZoomCorners();
                if (corners == null || corners.Count < 2) return;
                var moved = _c0 == null || !corners[0].IsAlmostEqualTo(_c0) || !corners[1].IsAlmostEqualTo(_c1);
                if (moved)
                {
                    // The repaint's nudge, or a real zoom (the drafter's, a row's): a nudge is tiny, and one that follows a
                    // real zoom before it settled does not hide it (the marks are still sized again).
                    var own = _ownMove && (_c0 == null || MarkKey.IsNudge(corners[0].DistanceTo(_c0) + corners[1].DistanceTo(_c1), _c0.DistanceTo(_c1)));
                    _settleOwn = _dirty ? _settleOwn && own : own; _ownMove = false;
                    _c0 = corners[0]; _c1 = corners[1]; _changedAt = _since.ElapsedMilliseconds; _dirty = true;
                    if (++_movedCalls == 400) IdleNote("the zoom corners still change after 400 calls: " + corners[0] + " " + corners[1]);
                    e.SetRaiseWithoutDelay();
                }
                else if (_dirty && _since.ElapsedMilliseconds - _changedAt >= SettleMs)
                {
                    _dirty = false;
                    var own = _settleOwn; _settleOwn = false;
                    IdleNote("zoom settled after " + _movedCalls + " moves" + (own ? " (the repaint's own nudge)" : ""));
                    _movedCalls = 0;
                    // The zoom settled: the frames are sized again and the dots that now overlap on screen go together
                    // (or come apart); drawn again only when that changed, and never for the repaint's own nudge
                    // (drawing again nudges again).
                    if (!own && !Hidden && _controls.Count > 0 && Relaid(uidoc, view)) Redraw(app);
                    Draw(app);
                }
                else if (_dirty) e.SetRaiseWithoutDelay();
            }
            catch (Exception ex)
            {
                var said = ex.GetType().Name + ": " + ex.Message;
                if (said != _idleError) { _idleError = said; IdleNote("error " + said); }
            }
        }
    }
}
