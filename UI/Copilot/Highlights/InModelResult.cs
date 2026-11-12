#nullable enable
// The result, shown in the model (result-pattern spec, Nov 2026):
//   rings     one TemporaryGraphicsManager control per touched element, in the
//             active view (no transaction, no model change, follows pan/zoom/orbit);
//   overlay   the toast (headline · Undo · close) and two docked callouts, with a
//             leader to one ring of each group in non-perspective views;
//   idling    while the view navigates the leaders hide; 150 ms after the zoom
//             corners settle they are redrawn. A view change clears everything.
// Everything Revit-side runs through RevitThread (Revit's API context); the
// overlay runs on the UI thread. Clear() first, then Undo, so a failed undo
// never leaves rings behind.

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
        public const int FadeSeconds = 8;
        public const int SettleMs = 150;
        private const int MaxRingsPerState = 400;

        private readonly Func<UIApplication?> _app;
        private readonly HighlightOverlay _overlay;
        private readonly Dispatcher _ui;

        private HighlightSet? _set;
        private ReceiptLanguage _lang;
        private Action? _undo;
        private OverlayModel? _model;
        private readonly List<int> _controls = new List<int>();
        private readonly Dictionary<string, List<long>> _groupInstances = new Dictionary<string, List<long>>();
        private readonly List<(MarkState State, long Id, XYZ Point)> _points = new List<(MarkState, long, XYZ)>();
        private long _viewId;
        private bool _perspective;
        private bool _kept;
        private bool _after = true;
        private DispatcherTimer? _fade;
        // Every Show and Clear bumps it: a Show whose Revit-side work finishes after a newer Show or a Clear must not draw its toast.
        private int _generation;
        private UIApplication? _idlingApp;
        private XYZ? _c0, _c1;
        private readonly Stopwatch _since = Stopwatch.StartNew();
        private long _changedAt;
        private bool _dirty;

        public InModelResult(Func<UIApplication?> app, HighlightOverlay overlay)
        {
            _app = app; _overlay = overlay; _ui = Dispatcher.CurrentDispatcher;
        }

        public bool IsShowing => _set != null;

        // ─── Public ───────────────────────────────────────────────────────────

        /// <summary>Draw a turn's result in the model. <paramref name="undo"/> runs the turn's Undo.</summary>
        public void Show(HighlightSet set, ReceiptLanguage lang, Action? undo)
        {
            Clear();
            if (set == null || set.IsEmpty) return;
            _set = set; _lang = lang; _undo = undo; _kept = false; _after = true;
            var dpi = HighlightOverlay.DeviceScale();
            var generation = _generation;
            Run("show-in-model", set.Groups.Count + set.Ids.Values.Sum(v => v.Count()), app => { Resolve(app, set); AddRings(app, dpi); }, () =>
            {
                if (generation != _generation) return;   // a newer result, or a clear, came first
                _model = BuildModel(set);
                var app = _app();
                if (app != null && _overlay.ShowResult(app, _model)) { StartIdling(app); StartFade(); }
            });
        }

        /// <summary>Remove the rings and the overlay. Safe to call when nothing is shown.</summary>
        public void Clear()
        {
            _generation++;
            StopFade(); StopIdling();
            _overlay.ClearResult();
            _set = null; _model = null; _points.Clear(); _groupInstances.Clear();
            if (_controls.Count == 0) return;
            var ids = _controls.ToList(); _controls.Clear();
            Run("clear-rings", ids.Count, app => RemoveRings(app, ids), null);
        }

        /// <summary>Select a group's elements (instances of a type group) in Revit: one SetElementIds call, no zoom.</summary>
        public void SelectGroup(TypeGroup g)
        {
            if (g == null) return;
            Run("select-group", g.Instances, app =>
            {
                var uidoc = app.ActiveUIDocument; if (uidoc == null) return;
                var ids = InstancesOf(uidoc.Document, g).Select(ElemIds.From).ToList();
                if (ids.Count > 0) uidoc.Selection.SetElementIds(ids);
            }, null);
        }

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

        private void Resolve(UIApplication app, HighlightSet set)
        {
            var uidoc = app.ActiveUIDocument; var doc = uidoc?.Document; var view = uidoc?.ActiveView;
            if (doc == null || view == null) return;
            _viewId = view.Id.Value;
            _perspective = view is View3D v3 && v3.IsPerspective;
            _points.Clear(); _groupInstances.Clear(); _perState.Clear();

            // Type groups: the rings sit on the instances, not on the types.
            var typeIds = new HashSet<long>(set.TypeIds.Values.SelectMany(s => s));
            var byType = new Dictionary<long, List<Element>>();
            // A type's dependents are its instances: a lookup per type. Only a type that gives none is searched for in a scan of the model.
            var missing = new HashSet<long>(typeIds);
            try
            {
                var notTypes = new ElementIsElementTypeFilter(true);
                foreach (var tid in typeIds)
                    if (doc.GetElement(ElemIds.From(tid)) is ElementType t)
                    {
                        var list = new List<Element>();
                        foreach (var dep in t.GetDependentElements(notTypes)) { var el = doc.GetElement(dep); if (el != null) list.Add(el); }
                        if (list.Count > 0) { byType[tid] = list; missing.Remove(tid); }
                    }
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] dependents: " + ex.Message); byType.Clear(); missing = new HashSet<long>(typeIds); }
            if (missing.Count > 0)
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    var t = e.GetTypeId(); if (t == null || t == ElementId.InvalidElementId) continue;
                    var tid = t.Value; if (!missing.Contains(tid)) continue;
                    if (!byType.TryGetValue(tid, out var list)) byType[tid] = list = new List<Element>();
                    list.Add(e);
                }
            foreach (var g in set.Groups)
                _groupInstances[g.TypeName] = g.IsType
                    ? g.Ids.SelectMany(t => byType.TryGetValue(t, out var l) ? l.Select(e => (long)e.Id.Value) : Enumerable.Empty<long>()).ToList()
                    : g.Ids.ToList();

            foreach (var kv in set.TypeIds)
                foreach (var tid in kv.Value)
                    if (byType.TryGetValue(tid, out var list))
                        foreach (var e in list) AddPoint(kv.Key, e, view);
            foreach (var kv in set.Ids)
                foreach (var id in kv.Value)
                {
                    var e = doc.GetElement(ElemIds.From(id));
                    if (e != null) AddPoint(kv.Key, e, view);
                }
        }

        private readonly Dictionary<MarkState, int> _perState = new Dictionary<MarkState, int>();

        private void AddPoint(MarkState state, Element e, View view)
        {
            _perState.TryGetValue(state, out var have);
            if (have >= MaxRingsPerState) return;   // a counter, not a re-count of every point for every element
            var p = LocationOf(e, view);
            if (p == null) return;
            _points.Add((state, e.Id.Value, p));
            _perState[state] = have + 1;
        }

        private static XYZ? LocationOf(Element e, View view)
        {
            try
            {
                if (e.Location is LocationPoint lp) return lp.Point;
                if (e.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Evaluate(0.5, true);
                var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
                return bb == null ? null : (bb.Min + bb.Max) / 2;
            }
            catch { return null; }
        }

        private void AddRings(UIApplication app, double dpi)
        {
            var doc = app.ActiveUIDocument?.Document; var view = app.ActiveUIDocument?.ActiveView;
            if (doc == null || view == null || _points.Count == 0) return;
            var tgm = TemporaryGraphicsManager.GetTemporaryGraphicsManager(doc);
            foreach (var (state, _, pt) in _points)
            {
                try { _controls.Add(tgm.AddControl(new InCanvasControlData(RingImages.PathFor(state, dpi), pt), view.Id)); }
                catch (Exception ex) { Debug.WriteLine("[BINA] ring: " + ex.Message); }
            }
        }

        private static void RemoveRings(UIApplication app, List<int> ids)
        {
            var doc = app.ActiveUIDocument?.Document; if (doc == null) return;
            var tgm = TemporaryGraphicsManager.GetTemporaryGraphicsManager(doc);
            foreach (var i in ids) { try { tgm.RemoveControl(i); } catch { /* already gone */ } }
        }

        // The instances of a type group, kept per model: a Zoom or Select after the toast faded (Clear drops _groupInstances) must not scan
        // every element of the model again (a 2.3 s freeze in the 0.0.106 probe).
        private readonly Dictionary<string, List<long>> _instanceCache = new Dictionary<string, List<long>>();
        private string _instanceCacheDoc = "";

        private IEnumerable<long> InstancesOf(Document doc, TypeGroup g)
        {
            if (_groupInstances.TryGetValue(g.TypeName, out var cached) && cached.Count > 0) return cached;
            if (!g.IsType) return g.Ids;
            var docKey = doc.PathName + "|" + doc.Title;
            if (docKey != _instanceCacheDoc) { _instanceCache.Clear(); _instanceCacheDoc = docKey; }
            var key = string.Join(",", g.Ids);
            if (_instanceCache.TryGetValue(key, out var kept) && kept.Count > 0) return kept;

            // The type's own dependents are its instances: a lookup, not a scan of the whole model.
            var found = new List<long>();
            try
            {
                var notTypes = new ElementIsElementTypeFilter(true);
                foreach (var tid in g.Ids)
                    if (doc.GetElement(ElemIds.From(tid)) is ElementType t)
                        foreach (var dep in t.GetDependentElements(notTypes)) found.Add(dep.Value);
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

        // ─── Overlay model ────────────────────────────────────────────────────

        private OverlayModel BuildModel(HighlightSet set)
        {
            var card = set.Card;
            var kind = card?.Kind ?? ResultKind.None;
            var color = kind == ResultKind.Findings ? HighlightSet.Red : kind == ResultKind.Changes ? HighlightSet.Green : HighlightSet.Blue;
            var m = new OverlayModel
            {
                Headline = set.Headline, Detail = set.Detail + (FadesAway(kind) ? " · fades in 8 s, click to keep" : ""),
                Color = color, ShowBeforeAfter = kind == ResultKind.Changes,
                Undo = _undo == null ? null : (Action)(() => { var u = _undo; Clear(); u(); }),
                Close = Clear,
                Keep = () => { _kept = true; StopFade(); },
                SetAfter = after => { _after = after; ToggleRings(after); },
            };

            if (kind == ResultKind.Changes)
            {
                foreach (var g in set.Groups.Take(2))
                {
                    var count = _groupInstances.TryGetValue(g.TypeName, out var inst) && inst.Count > 0 ? inst.Count : (g.Instances > 0 ? g.Instances : g.Ids.Count);
                    var c = new OverlayCallout { Title = Tail(g.TypeName, 28), Count = count, Color = HighlightSet.Green, OpenTypeProperties = g.IsType ? () => OpenTypeProperties(g) : null };
                    foreach (var r in g.Rows.Take(3)) c.Rows.Add((r.Param, r.Before ?? "", r.After ?? ""));
                    if (g.Rows.Count > 3) c.Note = "+ " + (g.Rows.Count - 3) + " more";
                    c.Anchor = AnchorFor(MarkState.Changed, inst);
                    m.Callouts.Add(c);
                }
            }
            else if (kind == ResultKind.Findings && card != null)
            {
                foreach (var f in card.Findings.Take(2))
                {
                    var c = new OverlayCallout { Title = f.Title, Count = f.Ids.Count, Color = f.Fixable ? HighlightSet.Red : HighlightSet.Amber, Dashed = !f.Fixable, Note = f.Fixable ? "Fixable from the result card." : "Needs you." };
                    c.Anchor = AnchorFor(f.Fixable ? MarkState.Fixable : MarkState.NeedsYou, f.Ids);
                    m.Callouts.Add(c);
                }
            }
            else if (kind == ResultKind.Selection && card != null)
            {
                m.Callouts.Add(new OverlayCallout { Title = "Selection", Count = card.Selected, Color = HighlightSet.Blue, Note = card.Detail });
            }
            else if (kind == ResultKind.Created && card != null)
            {
                m.Callouts.Add(new OverlayCallout { Title = "Created", Count = card.Created.Count, Color = HighlightSet.Blue, Note = card.Detail, Anchor = AnchorFor(MarkState.Created, card.Created.Select(c => c.Id).ToList()) });
            }
            return m;
        }

        private (double X, double Y)? AnchorFor(MarkState state, IEnumerable<long>? ids)
        {
            if (_perspective || ids == null) return null;
            var want = new HashSet<long>(ids);
            var app = _app(); var uidoc = app?.ActiveUIDocument; if (uidoc == null) return null;
            UIView? ui; View? view;
            try { view = uidoc.ActiveView; ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id); } catch { return null; }
            if (ui == null || view == null) return null;
            IList<XYZ> corners; try { corners = ui.GetZoomCorners(); } catch { return null; }
            if (corners == null || corners.Count < 2) return null;
            var c0 = V(corners[0]); var c1 = V(corners[1]); var right = V(view.RightDirection); var up = V(view.UpDirection);
            foreach (var p in _points.Where(p => p.State == state && want.Contains(p.Id)))
            {
                var pct = ViewProjection.ToPercent(V(p.Point), c0, c1, right, up);
                if (ViewProjection.InView(pct)) return pct;
            }
            return null;
        }

        private static Vec3 V(XYZ p) => new Vec3(p.X, p.Y, p.Z);
        private static string Tail(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : "…" + s.Substring(s.Length - n + 1);

        private void ToggleRings(bool after)
        {
            if (_set == null) return;
            if (!after)
            {
                var ids = _controls.ToList(); _controls.Clear();
                if (ids.Count > 0) Run("rings-off", ids.Count, app => RemoveRings(app, ids), null);
            }
            else if (_controls.Count == 0)
            {
                var dpi = HighlightOverlay.DeviceScale();
                Run("rings-on", _points.Count, app => AddRings(app, dpi), null);
            }
        }

        // ─── Fade and navigation ──────────────────────────────────────────────

        private void StartFade()
        {
            StopFade();
            if (_set?.Card == null || !FadesAway(_set.Card.Kind)) return;
            _fade = new DispatcherTimer { Interval = TimeSpan.FromSeconds(FadeSeconds) };
            _fade.Tick += (_, __) => { StopFade(); if (!_kept) Clear(); };
            _fade.Start();
        }

        /// <summary>A result that changed or made things fades with its toast; a selection or a finding stays until cleared.</summary>
        internal static bool FadesAway(ResultKind kind) => kind == ResultKind.Changes || kind == ResultKind.Created;

        private void StopFade() { if (_fade != null) { _fade.Stop(); _fade = null; } }

        private void StartIdling(UIApplication app)
        {
            StopIdling();
            _idlingApp = app; _c0 = _c1 = null; _dirty = false;
            try { app.Idling += OnIdling; } catch (Exception ex) { Debug.WriteLine("[BINA] idling: " + ex.Message); _idlingApp = null; }
        }

        private void StopIdling()
        {
            if (_idlingApp == null) return;
            try { _idlingApp.Idling -= OnIdling; } catch { /* ignore */ }
            _idlingApp = null;
        }

        private void OnIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            try
            {
                var app = sender as UIApplication ?? _idlingApp; var uidoc = app?.ActiveUIDocument;
                var view = uidoc?.ActiveView;
                if (view == null || view.Id.Value != _viewId) { Clear(); return; }
                var ui = uidoc!.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                var corners = ui?.GetZoomCorners();
                if (corners == null || corners.Count < 2) return;
                var moved = _c0 == null || !corners[0].IsAlmostEqualTo(_c0) || !corners[1].IsAlmostEqualTo(_c1);
                if (moved)
                {
                    _c0 = corners[0]; _c1 = corners[1]; _changedAt = _since.ElapsedMilliseconds; _dirty = true;
                    _overlay.HideLeaders();
                    e.SetRaiseWithoutDelay();
                }
                else if (_dirty && _since.ElapsedMilliseconds - _changedAt >= SettleMs)
                {
                    _dirty = false;
                    if (_model != null)
                    {
                        foreach (var c in _model.Callouts) { /* anchors are recomputed from the live view */ }
                        Reanchor();
                        _overlay.Refresh(app!);
                    }
                }
                else if (_dirty) e.SetRaiseWithoutDelay();
            }
            catch (Exception ex) { Debug.WriteLine("[BINA] in-model idle: " + ex.Message); }
        }

        private void Reanchor()
        {
            if (_model == null || _set == null) return;
            var card = _set.Card; var kind = card?.Kind ?? ResultKind.None;
            for (var i = 0; i < _model.Callouts.Count; i++)
            {
                var c = _model.Callouts[i];
                if (kind == ResultKind.Changes && i < _set.Groups.Count)
                    c.Anchor = AnchorFor(MarkState.Changed, _groupInstances.TryGetValue(_set.Groups[i].TypeName, out var inst) ? inst : null);
                else if (kind == ResultKind.Findings && card != null && i < card.Findings.Count)
                    c.Anchor = AnchorFor(card.Findings[i].Fixable ? MarkState.Fixable : MarkState.NeedsYou, card.Findings[i].Ids);
            }
        }
    }
}
