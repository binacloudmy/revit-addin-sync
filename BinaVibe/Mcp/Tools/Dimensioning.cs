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
            var dryRun = ArgsHelp.GetBool(args, "dry_run") ?? false;
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            if (ArgsHelp.GetBool(args, "selection") == true)
                ids = uidoc.Selection.GetElementIds().Select(id => (long)id.Value).ToList();
            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "nothing selected and no element_ids given — select at least 2 elements first" };
            if (ids.Count < 2 && !dryRun)
                throw new InvalidOperationException("element_ids needs at least 2 ids to dimension between");

            View view;
            var viewId = ArgsHelp.GetLong(args, "view_id");
            if (viewId.HasValue)
                view = doc.GetElement(ElemIds.From(viewId.Value)) as View
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

            // Plan (dimensions pack): which elements resolve a reference along the direction.
            var references = new ReferenceArray();
            Element? firstElement = null;
            var rows = new List<BinaVibe.Dimensions.ReferenceRow>();
            foreach (var id in ids)
            {
                var el = doc.GetElement(ElemIds.From(id));
                if (el == null) { rows.Add(new BinaVibe.Dimensions.ReferenceRow { Id = id, Name = id.ToString(), Found = false }); continue; }
                var refs = GetReferences(el, view, direction);
                var typeEl = el.GetTypeId().Value != ElementId.InvalidElementId.Value ? doc.GetElement(el.GetTypeId()) : null;
                var name = string.IsNullOrEmpty(typeEl?.Name) ? (el.Name ?? id.ToString()) : $"{typeEl!.Name} #{id}";
                rows.Add(new BinaVibe.Dimensions.ReferenceRow { Id = id, Name = name, Found = refs.Count > 0 });
                if (refs.Count == 0) continue;
                firstElement ??= el;
                references.Append(refs[0]);
            }
            var plan = BinaVibe.Dimensions.ElementDimensionPlan.Build(rows, Math.Abs(direction.Y) > Math.Abs(direction.X) ? "y" : "x");
            if (dryRun || !plan.WouldCreate)
                return plan.ToPreview(view.Name, new[] { direction.X, direction.Y });

            // Dimension line: through line_point_mm if given, else offset
            // 1000mm from the first element's bbox, perpendicular to direction.
            XYZ linePoint;
            var lp = ArgsHelp.GetPointMm(args, "line_point_mm");
            if (lp != null) linePoint = lp;
            else
            {
                var bb = firstElement!.get_BoundingBox(view)
                    ?? throw new InvalidOperationException($"cannot locate first element {ids[0]} in view '{view.Name}'");
                var perpendicular = new XYZ(-direction.Y, direction.X, 0);
                linePoint = (bb.Min + bb.Max) / 2 + perpendicular * (1000.0 / 304.8);
            }
            var lineDir = direction;
            var dimLine = Line.CreateBound(linePoint - lineDir * 100, linePoint + lineDir * 100);

            Dimension dim;
            using (var tx = new Transaction(doc, "BinaVibe: create dimensions"))
            {
                TxGuard.StartSwallowing(tx);
                try { dim = doc.Create.NewDimension(view, dimLine, references); tx.Commit(); }
                catch { tx.RollBack(); throw; }
            }
            doc.Regenerate();
            var expected = new[] { new BinaVibe.Dimensions.ExpectedChain { Id = (long)dim.Id.Value, Segments = plan.ExpectedSegments, TotalMm = -1 } };
            var verified = BinaVibe.Dimensions.ChainVerification.Verify(expected, id =>
            {
                var d = doc.GetElement(ElemIds.From(id)) as Dimension;
                if (d == null) return null;
                var m = ChainMetrics(d);
                return (m.segments, -1.0);   // total is not planned for element chains; segments are
            }, toleranceMm: 1.0);
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["new_ids"] = new List<long> { (long)dim.Id.Value },
                ["measured_count"] = references.Size,
                ["would_create"] = 1,
                ["view"] = view.Name,
                ["verified"] = verified,
                ["transactions"] = new List<string> { "BinaVibe: create dimensions" },
            };
        }

        /// <summary>(segments, total_mm) of a chain — the same reading list_dimensions reports.</summary>
        internal static (int segments, double totalMm) ChainMetrics(Dimension d)
        {
            int segments; double total = 0;
            if (d.NumberOfSegments > 0)
            {
                segments = d.NumberOfSegments;
                foreach (DimensionSegment seg in d.Segments) total += seg.Value.HasValue ? seg.Value.Value * 304.8 : 0.0;
            }
            else
            {
                segments = 1;
                total = d.Value.HasValue ? d.Value.Value * 304.8 : 0.0;
            }
            return (segments, total);
        }


        // ─── list_dimensions ────────────────────────────────────────────────
        /// <summary>
        /// args: { view_id?: long, limit?: int }
        /// Read-only: the dimension chains PRESENT in a view, so the copilot can
        /// verify work instead of re-placing it. Revit stores lengths in feet
        /// (x304.8 -> mm); every length here is millimetres, per the units
        /// contract the python side enforces.
        /// </summary>
        public static Dictionary<string, object?> ListDimensions(Document doc, JsonElement args)
        {
            View view;
            var viewId = ArgsHelp.GetLong(args, "view_id");
            if (viewId.HasValue)
                view = doc.GetElement(ElemIds.From(viewId.Value)) as View
                    ?? throw new InvalidOperationException($"view {viewId} not found");
            else
                view = doc.ActiveView
                    ?? throw new InvalidOperationException("no active view — open a view first");

            var limit = (int)(ArgsHelp.GetLong(args, "limit") ?? 50);
            if (limit <= 0) limit = 50;

            // View-scoped collector: never sweeps the whole model.
            var all = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(Dimension))
                .Cast<Dimension>()
                .ToList();

            var items = new List<object>();
            foreach (var d in all.Take(limit))
            {
                try { items.Add(Describe(doc, d)); }
                catch (Exception ex)
                {
                    // One unreadable dimension must not cost the drafter the
                    // whole report — name it and carry on.
                    items.Add(new Dictionary<string, object?>
                    {
                        ["id"] = d.Id.Value,
                        ["error"] = ex.Message,
                    });
                }
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["view"] = new Dictionary<string, object?>
                {
                    ["id"] = view.Id.Value,
                    ["name"] = view.Name,
                    ["type"] = view.ViewType.ToString(),
                },
                ["count"] = all.Count,
                ["truncated"] = all.Count > limit,
                ["dimensions"] = items,
            };
        }

        private static Dictionary<string, object?> Describe(Document doc, Dimension d)
        {
            // Segment values. A SINGLE-segment dimension reports
            // NumberOfSegments == 0 and carries its length on Value instead —
            // and that is exactly the shape of an overall grid-to-grid string,
            // so treating 0 as "empty chain" would misreport the most common
            // case this tool exists to verify.
            var segmentMm = new List<object>();
            double totalMm = 0;
            int segments;
            if (d.NumberOfSegments > 0)
            {
                segments = d.NumberOfSegments;
                foreach (DimensionSegment seg in d.Segments)
                {
                    var v = seg.Value.HasValue ? seg.Value.Value * 304.8 : 0.0;
                    segmentMm.Add(Math.Round(v, 1));
                    totalMm += v;
                }
            }
            else
            {
                segments = 1;
                var v = d.Value.HasValue ? d.Value.Value * 304.8 : 0.0;
                segmentMm.Add(Math.Round(v, 1));
                totalMm = v;
            }

            // Axis, not a raw vector — the model reports "below the building",
            // not a direction triple.
            string direction = "unknown";
            try
            {
                if (d.Curve is Line line)
                {
                    var dir = line.Direction;
                    if (Math.Abs(dir.X) > 0.9) direction = "horizontal";
                    else if (Math.Abs(dir.Y) > 0.9) direction = "vertical";
                    else direction = "angled";
                }
            }
            catch { /* dimension without a resolvable curve — leave "unknown" */ }

            List<object>? originMm = null;
            try
            {
                var o = d.Origin;
                if (o != null)
                    originMm = new List<object>
                    {
                        Math.Round(o.X * 304.8, 1),
                        Math.Round(o.Y * 304.8, 1),
                        Math.Round(o.Z * 304.8, 1),
                    };
            }
            catch { /* Origin throws on some dimension shapes — optional field */ }

            // What the chain dimensions. Paired with list_grids ids this is how
            // the model says "this is the Grid 1-13 string" instead of guessing.
            var referenced = new List<object>();
            try
            {
                foreach (Reference r in d.References)
                    if (r?.ElementId != null && r.ElementId != ElementId.InvalidElementId)
                        referenced.Add(r.ElementId.Value);
            }
            catch { /* references unavailable — optional field */ }

            return new Dictionary<string, object?>
            {
                ["id"] = d.Id.Value,
                ["type_name"] = doc.GetElement(d.GetTypeId())?.Name,
                ["segments"] = segments,
                ["total_mm"] = Math.Round(totalMm, 1),
                ["segment_mm"] = segmentMm,
                ["direction"] = direction,
                ["origin_mm"] = originMm,
                ["referenced_ids"] = referenced,
            };
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
