// AI-638: the Check pane's only Revit code. Zoom the view to a box, link the DWG into the active
// plan view as the preview underlay, and remove that link again. Every call is queued and run by
// one ExternalEvent (never from the WPF thread). The link is one ImportInstance + its CADLinkType:
// 0 model elements (no walls, doors, levels...), but it IS visible to model: refs and queries.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
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

        /// <summary>Create in a valid API context (App.OnStartup builds the pane host).</summary>
        public RevitViewBridge() => _event = ExternalEvent.Create(this);

        public void ZoomTo(BoxMm box) => Queue(app => Zoom(app, box));

        public void ShowUnderlay(string dwgPath, double mmPerUnit, IReadOnlyList<BoxMm> plans) => Queue(app =>
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument || !(doc.ActiveView is ViewPlan view)) return;   // no plan view open: no preview
            Unlink();
            var options = new DWGImportOptions
            {
                Unit = UnitFor(mmPerUnit),          // the reader's resolved unit, never assumed mm
                Placement = ImportPlacement.Origin, // same origin the builder uses
                ThisViewOnly = true,
            };
            using (var t = new Transaction(doc, "BINA: DWG preview underlay"))
            {
                t.Start();
                if (doc.Link(dwgPath, options, view, out ElementId id)) { _link = id; _linkDoc = doc; t.Commit(); }
                else t.RollBack();
            }
            if (plans.Count > 0)
                Zoom(app, new BoxMm(plans.Min(b => b.MinX), plans.Min(b => b.MinY), plans.Max(b => b.MaxX), plans.Max(b => b.MaxY)));
        });

        public void RemoveUnderlay() => Queue(_ => Unlink());

        private void Unlink()
        {
            if (_linkDoc == null || !_linkDoc.IsValidObject || _link == ElementId.InvalidElementId) { _linkDoc = null; return; }
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

        private static void Zoom(UIApplication app, BoxMm box)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            UIView ui = uidoc?.GetOpenUIViews().FirstOrDefault(v => v.ViewId == uidoc.ActiveView.Id);
            ui?.ZoomAndCenterRectangle(new XYZ(box.MinX / MmPerFoot, box.MinY / MmPerFoot, 0), new XYZ(box.MaxX / MmPerFoot, box.MaxY / MmPerFoot, 0));
        }

        private static ImportUnit UnitFor(double mmPerUnit) => mmPerUnit switch
        {
            1 => ImportUnit.Millimeter,
            10 => ImportUnit.Centimeter,
            1000 => ImportUnit.Meter,
            25.4 => ImportUnit.Inch,
            MmPerFoot => ImportUnit.Foot,
            _ => ImportUnit.Default,
        };

        private void Queue(Action<UIApplication> work)
        {
            _work.Enqueue(work);
            _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_work.TryDequeue(out Action<UIApplication> work))
            {
                try { work(app); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] Check pane view call failed: " + ex.Message); }
            }
        }

        public string GetName() => "BINA CAD to BIM: view preview";
    }
}
