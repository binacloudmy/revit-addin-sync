// create_dimensions — dimension between elements in a plan view.
// adapted from mcp-servers-for-revit (MIT) — CreateDimensionEventHandler.cs.
// Kept: per-element reference extraction (wall PlanarFace best-alignment pick,
// FamilyInstance solid/GeometryInstance walk, horizontal-face skip), the
// Line.CreateBound + doc.Create.NewDimension flow. Dropped: their point-pair
// FindReferenceAtPoint mode and AIResult envelope.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class Dimensioning
    {
        public static Dictionary<string, object?> CreateDimensions(UIApplication app, Document doc, JsonElement args)
        {
            var uidoc = app.ActiveUIDocument;
            var idsEl = args.TryGetProperty("element_ids", out var v) && v.ValueKind == JsonValueKind.Array
                ? v : throw new InvalidOperationException("element_ids required (2+ element ids)");
            var ids = new List<long>();
            foreach (var item in idsEl.EnumerateArray())
                if (item.TryGetInt64(out var n)) ids.Add(n);
            if (ids.Count < 2)
                throw new InvalidOperationException("element_ids needs at least 2 ids to dimension between");

            View view;
            var viewId = ArgsHelp.GetLong(args, "view_id");
            if (viewId.HasValue)
                view = doc.GetElement(new ElementId(viewId.Value)) as View
                    ?? throw new InvalidOperationException($"view {viewId} not found");
            else
                view = uidoc.ActiveView;
            if (view is View3D)
                throw new InvalidOperationException("create_dimensions needs a plan/section view, not 3D");

            // direction: unit vector the dimension measures ALONG. Default X.
            XYZ direction = new XYZ(1, 0, 0);
            if (args.TryGetProperty("direction", out var dirEl) && dirEl.ValueKind == JsonValueKind.Array)
            {
                var d = new List<double>();
                foreach (var n in dirEl.EnumerateArray())
                    if (n.TryGetDouble(out var dd)) d.Add(dd);
                if (d.Count >= 2 && (Math.Abs(d[0]) + Math.Abs(d[1])) > 1e-9)
                    direction = new XYZ(d[0], d[1], 0).Normalize();
            }

            var references = new ReferenceArray();
            Element? firstElement = null;
            foreach (var id in ids)
            {
                var el = doc.GetElement(new ElementId(id))
                    ?? throw new InvalidOperationException($"element {id} not found");
                firstElement ??= el;
                var refs = GetReferences(el, view, direction);
                if (refs.Count == 0)
                    throw new InvalidOperationException(
                        $"no dimensionable face found on element {id} for that direction");
                references.Append(refs[0]);
            }
            if (references.Size < 2)
                throw new InvalidOperationException("fewer than 2 references resolved — cannot dimension");

            // Dimension line: through line_point_mm if given, else offset
            // 1000mm from the first element's bbox, perpendicular to direction.
            XYZ linePoint;
            var lp = ArgsHelp.GetPointMm(args, "line_point_mm");
            if (lp != null) linePoint = lp;
            else
            {
                var bb = firstElement!.get_BoundingBox(view)
                    ?? throw new InvalidOperationException("cannot locate first element in view");
                var perpendicular = new XYZ(-direction.Y, direction.X, 0);
                linePoint = (bb.Min + bb.Max) / 2 + perpendicular * (1000.0 / 304.8);
            }
            var lineDir = direction;
            var dimLine = Line.CreateBound(linePoint - lineDir * 100, linePoint + lineDir * 100);

            using var tx = new Transaction(doc, "BINA: create dimensions");
            TxGuard.StartSwallowing(tx);
            try
            {
                var dim = doc.Create.NewDimension(view, dimLine, references);
                tx.Commit();

                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["new_ids"] = new List<long> { dim.Id.Value },
                    ["measured_count"] = references.Size,
                    ["view"] = view.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // adapted from mcp-servers-for-revit (MIT): pick the planar face whose
        // normal best aligns with the dimension direction; skip horizontal
        // faces (|normal.Z| > 0.9 — top/bottom, useless in plan).
        private static List<Reference> GetReferences(Element element, View view, XYZ direction)
        {
            var references = new List<Reference>();
            var options = new Options { View = view, ComputeReferences = true };

            IEnumerable<Solid> Solids(GeometryElement geo)
            {
                foreach (var obj in geo)
                {
                    if (obj is Solid s && s.Faces.Size > 0) yield return s;
                    else if (obj is GeometryInstance gi)
                        foreach (var sub in gi.GetInstanceGeometry())
                            if (sub is Solid ss && ss.Faces.Size > 0) yield return ss;
                }
            }

            var geometry = element.get_Geometry(options);
            if (geometry != null)
            {
                Reference? bestRef = null;
                double bestAlignment = -1;
                foreach (var solid in Solids(geometry))
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (face is not PlanarFace planar) continue;
                        if (Math.Abs(planar.FaceNormal.Z) > 0.9) continue;   // skip top/bottom
                        double alignment = Math.Abs(planar.FaceNormal.DotProduct(direction));
                        if (alignment > bestAlignment)
                        {
                            bestAlignment = alignment;
                            bestRef = face.Reference;
                        }
                    }
                }
                if (bestRef != null) references.Add(bestRef);
            }
            if (references.Count == 0 && element is Wall wall)
                references.Add(new Reference(wall));   // OSS fallback: the element itself
            return references;
        }
    }
}
