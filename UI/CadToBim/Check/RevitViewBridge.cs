// AI-638: the Check pane's only Revit code. Zoom the view to a box, link the DWG into the active
// plan view as the preview underlay, and remove that link again. Every call is queued and run by
// one ExternalEvent (never from the WPF thread). The link is one ImportInstance + its CADLinkType:
// 0 model elements (no walls, doors, levels...), but it IS visible to model: refs and queries.
// It is never saved into the .rvt: removed on DocumentSaving(As), re-added after Saved(As), and
// removed when the pane closes. Revit refuses a transaction inside DocumentClosing, so a link that
// is still there at close is tagged (extensible storage) and removed after the next DocumentOpened
// (queued on the ExternalEvent);
// only links carrying that tag are ever deleted, never the user's own DWG links.
// Failures go to cad-to-bim.log and to the pane (Failed).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services.CadToBim;

namespace RevitWebAppSync.UI.CadToBim.Check
{
    public sealed class RevitViewBridge : IRevitView, IExternalEventHandler
    {
        private const double MmPerFoot = 304.8;
        private readonly ConcurrentQueue<Action<UIApplication>> _work = new ConcurrentQueue<Action<UIApplication>>();
        private readonly ExternalEvent _event;
        private ElementId _link = ElementId.InvalidElementId;
        private Document _linkDoc;
        private (string Path, double MmPerUnit, ElementId ViewId) _last;   // what to re-link after a save
        private Autodesk.Revit.ApplicationServices.Application _hooked;

        public event Action<string> Failed;
        public event Action Linked;
        public event Action<string> Notice;
        public event Action<bool> RunPresent;
        public event Action Unlinked;
        private ElementId _undoneLink = ElementId.InvalidElementId;   // the link Ctrl+Z took, for redo

        /// <summary>Create in a valid API context (App.OnStartup builds the pane host).</summary>
        public RevitViewBridge()
        {
            _event = ExternalEvent.Create(this);
            Queue(app => Hook(app.Application));   // runs once Revit is idle, so DocumentOpened cleans leftovers from the start
        }

        // The tag on every preview link this add-in makes (never changes: old leftovers must still match).
        private static readonly Guid PreviewSchemaId = new Guid("6B1C2D3E-4F50-4A61-8B72-9C83D4E5F638");
        private const string PreviewField = "File";

        private static Schema PreviewSchema()
        {
            Schema schema = Schema.Lookup(PreviewSchemaId);
            if (schema != null) return schema;
            var b = new SchemaBuilder(PreviewSchemaId);
            b.SetSchemaName("BinaCadToBimPreview");
            b.SetDocumentation("Marks a temporary DWG preview link made by the BINA CAD to BIM pane; safe to delete.");
            b.SetVendorId("BINA_CLOUD");                 // = VendorId in RevitWebAppSync.addin
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Vendor);
            b.AddSimpleField(PreviewField, typeof(string));
            return b.Finish();
        }

        /// <summary>Delete preview links this add-in left in a document (tagged ones only). Needs a context that allows a transaction.</summary>
        /// <returns>How many tagged links were removed.</returns>
        private int RemoveLeftovers(Document doc)
        {
            if (doc == null || !doc.IsValidObject || doc.IsFamilyDocument || doc.IsReadOnly || doc.IsLinked) return 0;
            Schema schema = Schema.Lookup(PreviewSchemaId);
            if (schema == null) return 0;   // no document in this session carries the tag
            var ours = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance))
                .Where(e => e.GetEntity(schema).IsValid()).Select(e => e.Id).ToList();
            if (ours.Count == 0) return 0;
            using (var t = new Transaction(doc, "BINA: remove leftover DWG preview"))
            {
                t.Start();
                doc.Delete(WithUnsharedTypes(doc, ours));
                t.Commit();
            }
            Log("preview: removed " + ours.Count + " leftover preview link(s) from " + doc.Title);
            return ours.Count;
        }

        /// <summary>Our instances, plus each one's CADLinkType only when no other ImportInstance uses it:
        /// every link of one DWG shares one type, so the drafter's own link of the same file must keep it.</summary>
        private static List<ElementId> WithUnsharedTypes(Document doc, ICollection<ElementId> ours)
        {
            var ids = new List<ElementId>(ours);
            var all = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).ToList();
            foreach (ElementId type in ours.Select(i => doc.GetElement(i)?.GetTypeId()).Where(t => t != null && t != ElementId.InvalidElementId).Distinct())
                if (all.All(i => i.GetTypeId() != type || ours.Contains(i.Id))) ids.Add(type);
            return ids;
        }

        /// <summary>Open-time clean-up: log only, never the pane's error line; one notice line when something was removed.</summary>
        private void CleanupAfterOpen(Document doc)
        {
            try
            {
                if (RemoveLeftovers(doc) > 0) Notice?.Invoke("Removed a leftover CAD to BIM preview from this model");
            }
            catch (Exception ex) { Log("preview: open-time clean-up failed: " + ex.Message); }
        }

        public void ZoomTo(BoxMm box) => Queue(app => Zoom(app, box));

        // Reader mm -> model ft. Origin-to-origin keeps the DWG's WCS, so this is mm / 304.8 plus a near-zero
        // offset checked against the link itself (ASWARA: header vs link extents agree to 0.01 ft).
        private XYZ _readerToModel;

        public void ShowUnderlay(string dwgPath, double mmPerUnit, BoxMm mainPlan, BoxMm? drawingExtents) => Queue(app =>
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument) { Fail("no project is open"); return; }
            if (doc.IsWorkshared) return;   // never linked into a workshared model (the pane says so on screen 2)
            if (!(doc.ActiveView is ViewPlan view)) { Fail("open a floor plan view, then open the drawing again"); return; }
            Unlink();
            Hook(app.Application);   // idempotent; normally already hooked from the constructor
            Guard(() => RemoveLeftovers(doc), "removing a leftover preview");   // before a new link: same tag, same rule
            _last = (dwgPath, mmPerUnit, view.Id);
            _readerToModel = null;
            Link(doc, view);
            if (_link != ElementId.InvalidElementId) Guard(() => ZoomToPreview(app, doc, view, mainPlan, drawingExtents), "zooming to the preview");
        });

        /// <summary>The DWG header extents must match the link's own extents in width (±1 %) before reader boxes
        /// are trusted in model space; then zoom to the main plan. Otherwise zoom to the whole link and say why.
        /// Nothing is moved and the crop is never changed: a main plan outside the crop gets one pane line.</summary>
        private void ZoomToPreview(UIApplication app, Document doc, View view, BoxMm main, BoxMm? extents)
        {
            var link = (ImportInstance)doc.GetElement(_link);
            BoundingBoxXYZ bb = link.get_BoundingBox(null);
            var (lo, hi) = Bounds(bb.Transform, Corners(bb.Min, bb.Max));
            UIView ui = app.ActiveUIDocument?.GetOpenUIViews().FirstOrDefault(w => w.ViewId == view.Id);
            if (ui == null) { Log("preview: not zoomed: " + view.Name + " is not the open view"); return; }

            double ratio = extents is BoxMm e0 && e0.Width > 0 ? (hi.X - lo.X) / (e0.Width / MmPerFoot) : double.NaN;
            if (!(extents is BoxMm e) || !new SheetSnapshot { ExtMinX = e.MinX, ExtMinY = e.MinY, ExtMaxX = e.MaxX, ExtMaxY = e.MaxY }.FrameMatches(hi.X - lo.X))
            {
                Log(double.IsNaN(ratio)
                    ? "preview: no header extents to check the frame; zoomed to the whole link"
                    : Invariant($"preview: header vs link width ratio {ratio:0.###} is outside 1 ± 1%; zoomed to the whole link"));
                XYZ pad0 = (hi - lo) * 0.05;
                ui.ZoomAndCenterRectangle(lo - pad0, hi + pad0);
                return;
            }
            _readerToModel = new XYZ(lo.X - e.MinX / MmPerFoot, lo.Y - e.MinY / MmPerFoot, 0);
            Log(Invariant($"preview: reader->model offset X {_readerToModel.X:0.##} Y {_readerToModel.Y:0.##} ft, width ratio {ratio:0.###}"));

            var (mMin, mMax) = Model(main);
            if (view.CropBoxActive && !Overlaps(view.CropBox, mMin, mMax))
            {
                // zooming there would only show empty space: leave the view, the pane's amber line explains
                double metres = Math.Round(DistanceToCrop(view.CropBox, (mMin + mMax) / 2) * MmPerFoot / 1000);
                Log(Invariant($"preview: not zoomed: main plan outside the crop ({metres} m beyond it); the crop was left as is"));
                OutsideCrop?.Invoke(metres);
                return;
            }
            XYZ pad = (mMax - mMin) * 0.05;   // a small margin around the main plan
            ui.ZoomAndCenterRectangle(mMin - pad, mMax + pad);
            Log("preview: zoomed to the main plan " + Fmt((mMin, mMax)) + " in " + view.Name);
        }

        public event Action<double> OutsideCrop;

        /// <summary>Plan distance (ft) from a point to the crop box, measured in the crop's own space.</summary>
        private static double DistanceToCrop(BoundingBoxXYZ crop, XYZ point)
        {
            XYZ p = crop.Transform.Inverse.OfPoint(point);
            double dx = Math.Max(Math.Max(crop.Min.X - p.X, 0), p.X - crop.Max.X);
            double dy = Math.Max(Math.Max(crop.Min.Y - p.Y, 0), p.Y - crop.Max.Y);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>A reader box (mm) in model feet, through the derived offset; null offset = straight mm/304.8.</summary>
        private (XYZ Min, XYZ Max) Model(BoxMm b)
        {
            XYZ o = _readerToModel ?? XYZ.Zero;
            return (new XYZ(b.MinX / MmPerFoot + o.X, b.MinY / MmPerFoot + o.Y, 0), new XYZ(b.MaxX / MmPerFoot + o.X, b.MaxY / MmPerFoot + o.Y, 0));
        }

        private static string Invariant(FormattableString s) => FormattableString.Invariant(s);

        private static XYZ[] Corners(XYZ a, XYZ b) =>
            new[] { new XYZ(a.X, a.Y, a.Z), new XYZ(b.X, a.Y, a.Z), new XYZ(a.X, b.Y, b.Z), new XYZ(b.X, b.Y, b.Z) };

        /// <summary>Axis-aligned bounds of the points after a transform (handles rotated links and boxes).</summary>
        private static (XYZ Min, XYZ Max) Bounds(Transform t, IEnumerable<XYZ> points)
        {
            var q = points.Select(t.OfPoint).ToList();
            return (new XYZ(q.Min(p => p.X), q.Min(p => p.Y), q.Min(p => p.Z)), new XYZ(q.Max(p => p.X), q.Max(p => p.Y), q.Max(p => p.Z)));
        }

        private static string Fmt((XYZ Min, XYZ Max)? b) => b == null ? "none"
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, "X {0:0.#}..{1:0.#} Y {2:0.#}..{3:0.#}", b.Value.Min.X, b.Value.Max.X, b.Value.Min.Y, b.Value.Max.Y);

        /// <summary>XY overlap tested in the crop box's own space, so rotated plan views are right.</summary>
        private static bool Overlaps(BoundingBoxXYZ crop, XYZ min, XYZ max)
        {
            var (lo, hi) = Bounds(crop.Transform.Inverse, Corners(min, max));
            return lo.X <= crop.Max.X && hi.X >= crop.Min.X && lo.Y <= crop.Max.Y && hi.Y >= crop.Min.Y;
        }

        public void RemoveUnderlay() => Queue(_ => { Unlink(); _last = default; _readerToModel = null; });

        private void Link(Document doc, View view)
        {
            var options = new DWGImportOptions
            {
                Unit = UnitFor(_last.MmPerUnit),    // the reader's resolved unit, never assumed mm
                Placement = ImportPlacement.Origin, // same origin the builder uses
                ThisViewOnly = true,
            };
            using (var t = new Transaction(doc, "BINA: DWG preview underlay"))
            {
                t.Start();
                if (doc.Link(_last.Path, options, view, out ElementId id))
                {
                    var tag = new Entity(PreviewSchema());
                    tag.Set(PreviewField, _last.Path);
                    doc.GetElement(id).SetEntity(tag);   // so a leftover can be told apart from the user's own links
                    _link = id; _linkDoc = doc; t.Commit();
                    Log("preview: linked " + Path.GetFileName(_last.Path) + " into " + doc.Title);
                    Linked?.Invoke();
                }
                else { t.RollBack(); Fail("Revit did not link " + Path.GetFileName(_last.Path)); }
            }
        }

        private void Unlink()
        {
            _undoneLink = ElementId.InvalidElementId;   // removed on purpose: a later redo is not our link coming back
            if (_linkDoc == null || !_linkDoc.IsValidObject || _link == ElementId.InvalidElementId) { _linkDoc = null; _link = ElementId.InvalidElementId; return; }
            ElementId id = _link;
            _link = ElementId.InvalidElementId;   // before Commit: DocumentChanged must not read our own removal as the user's undo
            if (_linkDoc.GetElement(id) != null)
            {
                using (var t = new Transaction(_linkDoc, "BINA: remove DWG preview"))
                {
                    t.Start();
                    _linkDoc.Delete(WithUnsharedTypes(_linkDoc, new List<ElementId> { id }));
                    t.Commit();
                }
                Log("preview: removed the link from " + _linkDoc.Title);
            }
            _link = ElementId.InvalidElementId;
            _linkDoc = null;
        }

        // ── keep the preview out of the saved .rvt ──

        private void Hook(Autodesk.Revit.ApplicationServices.Application app)
        {
            if (_hooked != null) return;
            _hooked = app;
            app.DocumentSaving += (_, e) => BeforeSave(e.Document);
            app.DocumentSavingAs += (_, e) => BeforeSave(e.Document);
            app.DocumentSaved += (_, e) => AfterSave(e.Document);
            app.DocumentSavedAs += (_, e) => AfterSave(e.Document);
            app.DocumentChanged += OnDocumentChanged;   // AI-642: undo / redo of a run
            app.DocumentOpened += (_, e) => { Document opened = e.Document; Queue(_ => CleanupAfterOpen(opened)); };   // runs when Revit is idle
            app.DocumentClosing += (_, e) =>
            {
                if (!Same(e.Document)) return;
                // no transaction is allowed here; the tagged link goes on the next open (and is never saved: DocumentSaving removes it)
                Log("preview: document closing with the link still in it; it is removed the next time the document opens");
                _linkDoc = null; _link = ElementId.InvalidElementId; _last = default;
            };
        }

        // ── AI-642: the run's walls, watched so Build re-opens after Ctrl+Z ──
        private HashSet<long> _watch = new HashSet<long>();
        private readonly HashSet<long> _present = new HashSet<long>();
        private Document _watchDoc;

        /// <summary>Called on Revit's main thread (the build's Completed) or with null on a new open.</summary>
        public void Watch(IEnumerable<long> elementIds)
        {
            _watch = new HashSet<long>(elementIds ?? Enumerable.Empty<long>());
            _present.Clear();
            _present.UnionWith(_watch);
            // called on Revit's thread from the build's Completed, so the active document is the one just built into
            _watchDoc = _watch.Count > 0 ? App.UiApp?.ActiveUIDocument?.Document : null;
            if (_watch.Count > 0) Log("run watch: watching " + _watch.Count + " walls in " + (_watchDoc?.Title ?? "the active document"));
        }

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try { WatchLink(e); } catch (Exception ex) { Log("link watch: " + ex.Message); }
            if (_watch.Count == 0) return;
            try
            {
                Document doc = e.GetDocument();
                if (_watchDoc != null && !doc.Equals(_watchDoc)) return;
                bool before = _present.Count > 0;
                foreach (ElementId id in e.GetDeletedElementIds()) _present.Remove(ElementIdCompat.ToLong(id));
                foreach (ElementId id in e.GetAddedElementIds()) { long v = ElementIdCompat.ToLong(id); if (_watch.Contains(v)) _present.Add(v); }
                bool now = _present.Count > 0;
                if (before == now) return;
                Log("run watch: " + (now ? "walls back (redo), " + _present.Count + " of " + _watch.Count + " → Build blocked" : "all " + _watch.Count + " walls gone (undo) → Build allowed")
                    + " in " + doc.Title);
                RunPresent?.Invoke(now);
            }
            catch (Exception ex) { Log("run watch: " + ex.Message); }
        }

        /// <summary>Ctrl+Z of the preview link deletes its ImportInstance; redo adds the same id back.</summary>
        private void WatchLink(DocumentChangedEventArgs e)
        {
            Document doc = e.GetDocument();
            if (_link != ElementId.InvalidElementId && Same(doc) && e.GetDeletedElementIds().Contains(_link))
            {
                _undoneLink = _link;
                _link = ElementId.InvalidElementId;   // so Unlink() never deletes an id that is gone
                Log("preview: the link was undone in " + doc.Title);
                Unlinked?.Invoke();
            }
            else if (_undoneLink != ElementId.InvalidElementId && Same(doc) && e.GetAddedElementIds().Contains(_undoneLink))
            {
                _link = _undoneLink;
                _undoneLink = ElementId.InvalidElementId;
                Log("preview: the link came back (redo) in " + doc.Title);
                Linked?.Invoke();
            }
        }

        public void Show(IEnumerable<long> elementIds)
        {
            var ids = (elementIds ?? Enumerable.Empty<long>()).Select(ElementIdCompat.ToElementId).ToList();
            if (ids.Count == 0) return;
            Queue(app => Guard(() =>
            {
                UIDocument ui = app.ActiveUIDocument;
                if (ui == null) return;
                var live = ids.Where(i => ui.Document.GetElement(i) != null).ToList();
                if (live.Count == 0) { Notice?.Invoke("Those elements are no longer in the model"); return; }
                ui.Selection.SetElementIds(live);
                ui.ShowElements(live);
            }, "showing elements"));
        }

        private bool Same(Document doc) => _linkDoc != null && _linkDoc.IsValidObject && doc != null && doc.Equals(_linkDoc);

        private bool _relink;

        private void BeforeSave(Document doc)
        {
            if (!Same(doc)) return;
            _relink = true;
            Guard(Unlink, "removing the preview before save");
        }

        private void AfterSave(Document doc)
        {
            if (!_relink || doc == null || _last.Path == null) return;
            _relink = false;
            // shortcut: re-linking right in DocumentSaved; if Revit refuses a transaction here, queue it on the ExternalEvent instead.
            Guard(() => { if (doc.GetElement(_last.ViewId) is View view) Link(doc, view); }, "re-adding the preview after save");
        }

        // ── helpers ──

        private void Zoom(UIApplication app, BoxMm box)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            UIView ui = uidoc?.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id);
            var (min, max) = Model(box);   // same mapping + placement move as the preview
            ui?.ZoomAndCenterRectangle(min, max);
        }

        private static ImportUnit UnitFor(double mmPerUnit)
        {
            switch (mmPerUnit)
            {
                case 1: return ImportUnit.Millimeter;
                case 10: return ImportUnit.Centimeter;
                case 1000: return ImportUnit.Meter;
                case 25.4: return ImportUnit.Inch;
                case MmPerFoot: return ImportUnit.Foot;
                default:
                    Log("no Revit import unit for " + mmPerUnit + " mm per drawing unit; linked with Revit's default unit");
                    return ImportUnit.Default;
            }
        }

        private void Guard(Action action, string what)
        {
            try { action(); }
            catch (Exception ex) { Fail(what + " failed: " + ex.Message); }
        }

        private void Fail(string message)
        {
            Log("preview: " + message);
            try { Failed?.Invoke(message); } catch { /* the pane is best-effort */ }
        }

        /// <summary>Same file as the pane's init log: %LOCALAPPDATA%\Bina\RevitSync\logs\cad-to-bim.log.</summary>
        private static void Log(string line)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bina", "RevitSync", "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "cad-to-bim.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] check pane {line}\n");
            }
            catch { /* logging is never fatal */ }
        }

        private void Queue(Action<UIApplication> work)
        {
            _work.Enqueue(work);
            _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_work.TryDequeue(out Action<UIApplication> work))
                Guard(() => work(app), "the view call");
        }

        public string GetName() => "BINA CAD to BIM: view preview";
    }
}
