// AI-638: the Check pane's only Revit code. Zoom the view to a box, link the DWG into the active
// plan view as the preview underlay, and remove that link again. Every call is queued and run by
// one ExternalEvent (never from the WPF thread). The link is one ImportInstance + its CADLinkType:
// 0 model elements (no walls, doors, levels...), but it IS visible to model: refs and queries.
// It is never saved into the .rvt: removed on DocumentSaving(As), re-added after Saved(As),
// and dropped on DocumentClosing. Failures go to cad-to-bim.log and to the pane (Failed).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
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

        /// <summary>Create in a valid API context (App.OnStartup builds the pane host).</summary>
        public RevitViewBridge() => _event = ExternalEvent.Create(this);

        public void ZoomTo(BoxMm box) => Queue(app => Zoom(app, box));

        public void ShowUnderlay(string dwgPath, double mmPerUnit, IReadOnlyList<BoxMm> plans) => Queue(app =>
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument) { Fail("no project is open"); return; }
            if (!(doc.ActiveView is ViewPlan view)) { Fail("open a floor plan view, then open the drawing again"); return; }
            Unlink();
            Hook(app.Application);
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
                if (doc.Link(_last.Path, options, view, out ElementId id)) { _link = id; _linkDoc = doc; t.Commit(); }
                else { t.RollBack(); Fail("Revit did not link " + Path.GetFileName(_last.Path)); }
            }
        }

        private void Unlink()
        {
            if (_linkDoc == null || !_linkDoc.IsValidObject || _link == ElementId.InvalidElementId) { _linkDoc = null; _link = ElementId.InvalidElementId; return; }
            Element link = _linkDoc.GetElement(_link);
            if (link != null)
            {
                ElementId type = link.GetTypeId();
                using (var t = new Transaction(_linkDoc, "BINA: remove DWG preview"))
                {
                    t.Start();
                    _linkDoc.Delete(new List<ElementId> { _link, type }.Where(i => i != ElementId.InvalidElementId).ToList());
                    t.Commit();
                }
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
            app.DocumentClosing += (_, e) =>
            {
                if (!Same(e.Document)) return;
                Guard(Unlink, "removing the preview on close");
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
