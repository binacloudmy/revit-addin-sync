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

        public void ShowUnderlay(string dwgPath, double mmPerUnit, IReadOnlyList<BoxMm> plans) => Queue(app =>
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument) { Fail("no project is open"); return; }
            if (doc.IsWorkshared) return;   // never linked into a workshared model (the pane says so on screen 2)
            if (!(doc.ActiveView is ViewPlan view)) { Fail("open a floor plan view, then open the drawing again"); return; }
            Unlink();
            Hook(app.Application);   // idempotent; normally already hooked from the constructor
            Guard(() => RemoveLeftovers(doc), "removing a leftover preview");   // before a new link: same tag, same rule
            _last = (dwgPath, mmPerUnit, view.Id);
            Link(doc, view);
            if (plans.Count > 0)
                Zoom(app, new BoxMm(plans.Min(b => b.MinX), plans.Min(b => b.MinY), plans.Max(b => b.MaxX), plans.Max(b => b.MaxY)));
        });

        public void RemoveUnderlay() => Queue(_ => { Unlink(); _last = default; });

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
            if (_linkDoc == null || !_linkDoc.IsValidObject || _link == ElementId.InvalidElementId) { _linkDoc = null; _link = ElementId.InvalidElementId; return; }
            Element link = _linkDoc.GetElement(_link);
            if (link != null)
            {
                using (var t = new Transaction(_linkDoc, "BINA: remove DWG preview"))
                {
                    t.Start();
                    _linkDoc.Delete(WithUnsharedTypes(_linkDoc, new List<ElementId> { _link }));
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
            app.DocumentOpened += (_, e) => { Document opened = e.Document; Queue(_ => CleanupAfterOpen(opened)); };   // runs when Revit is idle
            app.DocumentClosing += (_, e) =>
            {
                if (!Same(e.Document)) return;
                // no transaction is allowed here; the tagged link goes on the next open (and is never saved: DocumentSaving removes it)
                Log("preview: document closing with the link still in it; it is removed the next time the document opens");
                _linkDoc = null; _link = ElementId.InvalidElementId; _last = default;
            };
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

        private static void Zoom(UIApplication app, BoxMm box)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            UIView ui = uidoc?.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id);
            ui?.ZoomAndCenterRectangle(new XYZ(box.MinX / MmPerFoot, box.MinY / MmPerFoot, 0), new XYZ(box.MaxX / MmPerFoot, box.MaxY / MmPerFoot, 0));
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
