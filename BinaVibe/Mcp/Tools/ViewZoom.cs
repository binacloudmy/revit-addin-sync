#nullable enable
// Zoom the active view to some elements with ONE call: the union of their bounding boxes in this view, then
// UIView.ZoomAndCenterRectangle. UIDocument.ShowElements also selects, opens views and regenerates, which took seconds on a
// model with thousands of elements (live latency probe, 0.0.106). Falls back to ShowElements only when no element has a
// box in this view.
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class ViewZoom
    {
        private const int MaxBoxes = 5000;
        private const double Pad = 0.15;

        public static void ZoomTo(UIDocument uidoc, IList<ElementId> ids)
        {
            if (uidoc == null || ids == null || ids.Count == 0) return;
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var seen = 0;
            foreach (var id in ids.Take(MaxBoxes))
            {
                BoundingBoxXYZ? box;
                try { box = doc.GetElement(id)?.get_BoundingBox(view); } catch (Exception) { box = null; }
                if (box == null) continue;
                seen++;
                minX = Math.Min(minX, box.Min.X); minY = Math.Min(minY, box.Min.Y); minZ = Math.Min(minZ, box.Min.Z);
                maxX = Math.Max(maxX, box.Max.X); maxY = Math.Max(maxY, box.Max.Y); maxZ = Math.Max(maxZ, box.Max.Z);
            }
            UIView? ui = null;
            try { ui = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id); } catch (Exception) { /* no UI view */ }
            if (seen == 0 || ui == null)
            {
                try { uidoc.ShowElements(ids); } catch (Exception) { /* zoom is best-effort */ }
                return;
            }
            var dx = Math.Max(maxX - minX, 0.5); var dy = Math.Max(maxY - minY, 0.5); var dz = Math.Max(maxZ - minZ, 0.5);
            var px = dx * Pad; var py = dy * Pad; var pz = dz * Pad;
            ui.ZoomAndCenterRectangle(new XYZ(minX - px, minY - py, minZ - pz), new XYZ(maxX + px, maxY + py, maxZ + pz));
        }
    }
}
