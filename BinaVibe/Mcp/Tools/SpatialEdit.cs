// spatial_edit — one atomic tool for move / copy / rotate / delete with a
// selector, a dry_run preview, risk checks and geometry verification
// (bina-ai R2 Task 23: family A move/copy/rotate/delete, family B mirror/align/array).
//
//   spatial_edit {op, selection?|element_ids?|category+predicate?,
//                 dx_mm?, dy_mm?, dz_mm?, angle_deg?, axis_x_mm?, axis_y_mm?, dry_run}
//
// Positions are millimetres in project coordinates: LocationPoint, else the
// LocationCurve midpoint, else the bounding-box centre. Datums (Level/Grid)
// are never changed; pinned and grouped elements are skipped for move/copy/
// rotate; hosted dependents are reported as risks. Apply = ONE transaction,
// then Regenerate and re-read: positions within 1 mm, absence for delete,
// new ids (and their positions) for copy.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BinaVibe.Spatial;

namespace BinaVibe.Mcp.Tools
{
    internal static class SpatialEdit
    {
        private const double MmPerFoot = 304.8;
        private const double ToleranceMm = 1.0;

        private static Vec? PositionMm(Document doc, Element e)
        {
            try
            {
                XYZ? p = null;
                if (e.Location is LocationPoint lp) p = lp.Point;
                else if (e.Location is LocationCurve lc) p = lc.Curve.Evaluate(0.5, true);
                else
                {
                    var bb = e.get_BoundingBox(null);
                    if (bb != null) p = (bb.Min + bb.Max) / 2.0;
                }
                if (p == null) return null;
                return new Vec(p.X * MmPerFoot, p.Y * MmPerFoot, p.Z * MmPerFoot);
            }
            catch { return null; }
        }

        private static List<Element> Select(UIDocument uidoc, Document doc, JsonElement args, out Dictionary<string, object?>? error)
        {
            error = null;
            if (ArgsHelp.GetBool(args, "selection") == true)
            {
                var ids = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).Where(e => e != null).ToList();
                if (ids.Count == 0) error = new() { ["ok"] = false, ["error"] = "nothing is selected in Revit — select the elements first or name them (category + predicate)" };
                return ids!;
            }
            var explicitIds = ArgsHelp.GetLongList(args, "element_ids");
            if (explicitIds.Count > 0)
                return explicitIds.Select(id => doc.GetElement(ElemIds.From(id))).Where(e => e != null).ToList()!;
            var category = ArgsHelp.GetString(args, "category");
            if (string.IsNullOrWhiteSpace(category))
            {
                error = new() { ["ok"] = false, ["error"] = "no selector: pass selection=true, element_ids, or category (+ predicate)" };
                return new List<Element>();
            }
            var bic = Inspectors.ResolveCategoryRobust(doc, category!);
            if (bic == null) { error = new() { ["ok"] = false, ["error"] = $"category '{category}' not recognised" }; return new List<Element>(); }
            var predicate = ArgsHelp.GetString(args, "predicate");
            return new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType()
                .Where(e => Inspectors.PredicateMatches(e, doc, predicate)).ToList();
        }

        private static int DependentsOf(Document doc, Element e)
        {
            try
            {
                var deps = e.GetDependentElements(null);
                return Math.Max(0, deps.Count(id => id != e.Id));
            }
            catch { return 0; }
        }

        private static SpatialRow ToRow(Document doc, Element e)
        {
            var pos = PositionMm(doc, e) ?? new Vec(0, 0, 0);
            var typeEl = e.GetTypeId().Value != ElementId.InvalidElementId.Value ? doc.GetElement(e.GetTypeId()) : null;
            return new SpatialRow
            {
                Id = (long)e.Id.Value,
                Name = string.IsNullOrEmpty(typeEl?.Name) ? (e.Name ?? e.Id.Value.ToString()) : $"{typeEl!.Name} #{e.Id.Value}",
                X = pos.X, Y = pos.Y, Z = pos.Z,
                Pinned = e.Pinned,
                Grouped = e.GroupId.Value != ElementId.InvalidElementId.Value,
                IsDatum = e is Level || e is Grid,
                Dependents = DependentsOf(doc, e),
            };
        }

        public static Dictionary<string, object?> Run(UIDocument uidoc, JsonElement args)
        {
            var doc = uidoc.Document;
            var opName = (ArgsHelp.GetString(args, "op") ?? "").ToLowerInvariant();
            if (!Enum.TryParse<SpatialOp>(opName, true, out var op))
                return new() { ["ok"] = false, ["error"] = $"unknown op '{opName}' — use move, copy, rotate, delete, mirror, align or array" };
            var dryRun = ArgsHelp.GetBool(args, "dry_run") ?? false;

            var targets = Select(uidoc, doc, args, out var err);
            if (err != null) return err;

            Vec? vector = null;
            if (op == SpatialOp.Move || op == SpatialOp.Copy || op == SpatialOp.Array)
            {
                vector = new Vec(ArgsHelp.GetDouble(args, "dx_mm") ?? 0, ArgsHelp.GetDouble(args, "dy_mm") ?? 0, ArgsHelp.GetDouble(args, "dz_mm") ?? 0);
                if (vector.Value.DistanceTo(new Vec(0, 0, 0)) < 0.001)
                    return new() { ["ok"] = false, ["error"] = "zero vector: give dx_mm / dy_mm / dz_mm" };
            }
            double angle = ArgsHelp.GetDouble(args, "angle_deg") ?? 0;
            if (op == SpatialOp.Rotate && Math.Abs(angle) < 0.001)
                return new() { ["ok"] = false, ["error"] = "angle_deg is required for rotate" };
            Vec? axis = null;
            var ax = ArgsHelp.GetDouble(args, "axis_x_mm"); var ay = ArgsHelp.GetDouble(args, "axis_y_mm");
            if (ax.HasValue || ay.HasValue) axis = new Vec(ax ?? 0, ay ?? 0, 0);

            // Family B params: a grid name resolves to an axis + position in mm.
            string? lineAxis = null; double? lineAt = null; string? gridName = null;
            var gridArg = ArgsHelp.GetString(args, "grid");
            if (!string.IsNullOrWhiteSpace(gridArg))
            {
                var grid = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                    .FirstOrDefault(g => string.Equals(g.Name, gridArg, StringComparison.OrdinalIgnoreCase) && g.Curve is Line);
                if (grid == null)
                {
                    var names = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>().Select(g => g.Name).Take(12).ToList();
                    return new() { ["ok"] = false, ["error"] = $"grid '{gridArg}' not found in this view", ["candidates"] = names };
                }
                var line = (Grid)grid; var ln = (Line)line.Curve;
                bool horizontal = Math.Abs(ln.Direction.X) > Math.Abs(ln.Direction.Y);
                lineAxis = horizontal ? "y" : "x";                     // a horizontal grid fixes Y; a vertical grid fixes X
                lineAt = (horizontal ? ln.Origin.Y : ln.Origin.X) * MmPerFoot;
                gridName = grid.Name;
            }
            var p = new SpatialParams
            {
                Vector = vector, AngleDeg = angle, Axis = axis,
                MirrorAxis = op == SpatialOp.Mirror ? (lineAxis ?? ArgsHelp.GetString(args, "axis") ?? "x") : null,
                MirrorAtMm = op == SpatialOp.Mirror ? (lineAt ?? ArgsHelp.GetDouble(args, "at_mm") ?? 0) : 0,
                Copy = ArgsHelp.GetBool(args, "copy") ?? true,
                AlignAxis = op == SpatialOp.Align ? (lineAxis ?? ArgsHelp.GetString(args, "axis")) : null,
                AlignAtMm = op == SpatialOp.Align ? (lineAt ?? ArgsHelp.GetDouble(args, "at_mm")) : null,
                AlignEdge = op == SpatialOp.Align ? ArgsHelp.GetString(args, "edge") : null,
                Count = (int)(ArgsHelp.GetDouble(args, "count") ?? 0),
            };
            if (op == SpatialOp.Align && p.AlignAtMm == null && string.IsNullOrEmpty(p.AlignEdge))
                return new() { ["ok"] = false, ["error"] = "align needs a target: grid, at_mm (+ axis) or edge (left/right/top/bottom/center)" };

            var rows = targets.Select(e => ToRow(doc, e)).ToList();
            var plan = SpatialPlan.Build(rows, op, p);
            if (dryRun)
            {
                var pv = plan.ToPreview(cap: 200);
                if (gridName != null && pv["target"] is Dictionary<string, object?> tgt) { tgt["kind"] = "grid"; tgt["name"] = gridName; }
                if (gridName != null && pv["plane"] is Dictionary<string, object?> pl) { pl["grid"] = gridName; }
                return pv;
            }

            var byId = targets.ToDictionary(e => (long)e.Id.Value, e => e);
            var changeIds = plan.Changes.Select(c => c.Id).Distinct().Where(byId.ContainsKey).Select(id => byId[id].Id).ToList();
            var failures = new List<object>();
            var newIds = new List<long>();
            int changed = 0;
            var txName = $"BinaVibe: spatial_edit {opName}";

            using (var tx = new Transaction(doc, txName))
            {
                TxGuard.StartSwallowing(tx);
                try
                {
                    switch (op)
                    {
                        case SpatialOp.Move:
                            if (changeIds.Count > 0)
                            {
                                ElementTransformUtils.MoveElements(doc, changeIds,
                                    new XYZ(plan.Vector.X / MmPerFoot, plan.Vector.Y / MmPerFoot, plan.Vector.Z / MmPerFoot));
                                changed = changeIds.Count;
                            }
                            break;
                        case SpatialOp.Copy:
                            if (changeIds.Count > 0)
                            {
                                var created = ElementTransformUtils.CopyElements(doc, changeIds,
                                    new XYZ(plan.Vector.X / MmPerFoot, plan.Vector.Y / MmPerFoot, plan.Vector.Z / MmPerFoot));
                                newIds.AddRange(created.Select(id => (long)id.Value));
                                changed = created.Count;
                            }
                            break;
                        case SpatialOp.Rotate:
                            if (changeIds.Count > 0)
                            {
                                var a = new XYZ(plan.Axis.X / MmPerFoot, plan.Axis.Y / MmPerFoot, 0);
                                var line = Line.CreateBound(a, a + XYZ.BasisZ * 10);
                                ElementTransformUtils.RotateElements(doc, changeIds, line, plan.AngleDeg * Math.PI / 180.0);
                                changed = changeIds.Count;
                            }
                            break;
                        case SpatialOp.Delete:
                            foreach (var id in changeIds)
                            {
                                try { doc.Delete(id); changed++; }
                                catch (Exception ex) { failures.Add(new Dictionary<string, object?> { ["id"] = (long)id.Value, ["error"] = ex.Message }); }
                            }
                            break;
                        case SpatialOp.Mirror:
                            if (changeIds.Count > 0)
                            {
                                var normal = string.Equals(plan.MirrorAxis, "y", StringComparison.OrdinalIgnoreCase) ? XYZ.BasisY : XYZ.BasisX;
                                var origin = string.Equals(plan.MirrorAxis, "y", StringComparison.OrdinalIgnoreCase)
                                    ? new XYZ(0, plan.MirrorAtMm / MmPerFoot, 0) : new XYZ(plan.MirrorAtMm / MmPerFoot, 0, 0);
                                var mirrorPlane = Plane.CreateByNormalAndOrigin(normal, origin);
                                var made = ElementTransformUtils.MirrorElements(doc, changeIds, mirrorPlane, plan.CreatesCopies);
                                if (plan.CreatesCopies) newIds.AddRange(made.Select(id => (long)id.Value));
                                changed = plan.CreatesCopies ? made.Count : changeIds.Count;
                            }
                            break;
                        case SpatialOp.Align:
                            foreach (var c in plan.Changes)
                            {
                                if (!byId.TryGetValue(c.Id, out var el)) continue;
                                var delta = new XYZ((c.To.X - c.From.X) / MmPerFoot, (c.To.Y - c.From.Y) / MmPerFoot, 0);
                                try { ElementTransformUtils.MoveElement(doc, el.Id, delta); changed++; }
                                catch (Exception ex) { failures.Add(new Dictionary<string, object?> { ["id"] = c.Id, ["error"] = ex.Message }); }
                            }
                            break;
                        case SpatialOp.Array:
                            for (int k = 1; k <= plan.CopiesPerSource; k++)
                            {
                                var step = new XYZ(plan.Vector.X * k / MmPerFoot, plan.Vector.Y * k / MmPerFoot, plan.Vector.Z * k / MmPerFoot);
                                var made = ElementTransformUtils.CopyElements(doc, changeIds, step);
                                newIds.AddRange(made.Select(id => (long)id.Value));
                                changed += made.Count;
                            }
                            break;
                    }
                    tx.Commit();
                }
                catch { tx.RollBack(); throw; }
            }
            doc.Regenerate();

            Dictionary<string, object?> verified;
            if (op == SpatialOp.Delete)
                verified = SpatialVerification.Absent(plan.Changes.Select(c => c.Id), id => doc.GetElement(ElemIds.From(id)) != null);
            else if (plan.CreatesCopies)
            {
                // copies come back in source order (per step for array); expect each at its planned position
                var expected = new Dictionary<long, Vec>();
                var planned = op == SpatialOp.Array
                    ? plan.Changes.Select((c, i) => (c, i)).OrderBy(t => t.c.CopyIndex).ThenBy(t => t.i).Select(t => t.c).ToList()
                    : plan.Changes.ToList();
                for (int i = 0; i < newIds.Count && i < planned.Count; i++) expected[newIds[i]] = planned[i].To;
                verified = SpatialVerification.Positions(expected, id => { var e = doc.GetElement(ElemIds.From(id)); return e == null ? null : PositionMm(doc, e); }, ToleranceMm);
            }
            else
            {
                var expected = plan.Changes.ToDictionary(c => c.Id, c => c.To);
                verified = SpatialVerification.Positions(expected, id => { var e = doc.GetElement(ElemIds.From(id)); return e == null ? null : PositionMm(doc, e); }, ToleranceMm);
            }

            return new()
            {
                ["ok"] = true,
                ["op"] = opName,
                ["matched"] = plan.Matched,
                ["would_change"] = plan.Changes.Count,
                ["changed"] = changed,
                ["skipped"] = plan.Skipped.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["failures"] = failures,
                ["new_ids"] = newIds.Cast<object>().ToList(),
                ["copies_per_source"] = plan.CopiesPerSource,
                ["risks"] = plan.Risks.Select(r => (object)new Dictionary<string, object?> { ["id"] = r.Id, ["kind"] = r.Kind, ["count"] = r.Count, ["note"] = r.Note }).ToList(),
                ["verified"] = verified,
                ["transactions"] = new List<string> { txName },
                ["headline"] = $"{changed} of {plan.Changes.Count} {opName}d, {verified["matches"]} verified",
            };
        }
    }
}
