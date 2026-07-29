// BinaVibe/Mcp/Tools/IfcConvert/IfcReader.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public enum ConvertScope { Whole, ActiveLevel, Selection }

    /// <summary>Revit API: reads imported-IFC DirectShapes (which carry the IFC entity
    /// type + Psets) and turns each into a neutral IfcElement. Never parses raw .ifc.</summary>
    public sealed class IfcReader
    {
        const double FtToMm = 304.8;

        // Geometry-sanity thresholds (see the per-helper checks below). Kept
        // conservative: when a solid doesn't cleanly reduce to the native
        // primitive we keep the original DirectShape (never approximate).
        const double WallFootprintFill = 0.5;   // solid footprint / bbox footprint; a 45° wall ≈ 0.09
        const double ColumnPrismFill   = 0.90;   // solid vol / bbox vol; a round column ≈ 0.785 → kept
        const double BeamSlopeFraction = 0.02;   // end-to-end rise / axis length allowed (~1.15°) before reject
        const double BeamSlopeTolMm    = 5.0;    // absolute rise tolerance floor, for short beams

        /// <param name="activeLevelName">When scope == ActiveLevel, only elements whose
        /// nearest level matches this name are returned. Null disables the filter (the
        /// caller has already downgraded to whole-model + a warning).</param>
        public List<IfcElement> Read(Document doc, ConvertScope scope,
                                     ICollection<ElementId>? selection = null, string? activeLevelName = null)
        {
            IEnumerable<Element> shapes = new FilteredElementCollector(doc)
                .OfClass(typeof(DirectShape)).Cast<Element>();
            if (scope == ConvertScope.Selection && selection != null)
                shapes = shapes.Where(e => selection.Contains(e.Id));

            var result = new List<IfcElement>();
            foreach (var e in shapes)
            {
                var entity = ClassifyEntity(e);           // reads IfcExportAs / IFC_EXPORT_ELEMENT param
                if (entity == null) continue;              // not an IFC-tagged element
                var el = BuildElement(doc, e, entity.Value);
                // scope="level": keep only elements resolved to the active level.
                // Elements with no derivable level (el.Level == null) can't be
                // placed on a level, so they're excluded from a level-scoped run.
                if (scope == ConvertScope.ActiveLevel && activeLevelName != null &&
                    !string.Equals(el.Level, activeLevelName, StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(el);
            }
            return result;
        }

        static IfcEntity? ClassifyEntity(Element e)
        {
            // Imported IFC carries the source entity in "IfcExportAs" / "Export Type" params,
            // or the DirectShape category maps to the entity. Prefer the param, fall back to category.
            var tag = e.LookupParameter("IfcExportAs")?.AsString()
                      ?? e.LookupParameter("Export Type as")?.AsString();
            var s = (tag ?? e.Category?.Name ?? "").ToLowerInvariant();
            if (s.Contains("wall")) return IfcEntity.Wall;
            if (s.Contains("slab") || s.Contains("floor")) return IfcEntity.Slab;
            if (s.Contains("column")) return IfcEntity.Column;
            if (s.Contains("beam")) return IfcEntity.Beam;
            return null;
        }

        IfcElement BuildElement(Document doc, Element e, IfcEntity entity)
        {
            var solid = LargestSolid(e);
            if (solid == null)
                return new IfcElement { SourceId = e.Id.Value, Entity = entity, Convertible = false, Reason = "no solid geometry" };

            var lvl = NearestLevel(doc, solid);
            var level = lvl?.Name ?? "Level 1";
            double levelElevMm = (lvl?.Elevation ?? 0) * FtToMm;
            var ifcName = e.Name;
            var material = e.LookupParameter("IfcMaterial")?.AsString();

            try
            {
                switch (entity)
                {
                    case IfcEntity.Wall:
                    {
                        var (start, end, height, thickness, ok, reason) = WallAxisFromSolid(solid);
                        if (!ok) return Kept(e, entity, reason);
                        return new IfcElement { SourceId = e.Id.Value, Entity = entity, StartMm = start, EndMm = end,
                            HeightMm = height, ThicknessMm = thickness, Level = level, IfcTypeName = ifcName, Material = material };
                    }
                    case IfcEntity.Slab:
                    {
                        var (loop, thickness, ok, reason) = SlabBoundaryFromSolid(solid);
                        if (!ok) return Kept(e, entity, reason);
                        return new IfcElement { SourceId = e.Id.Value, Entity = entity, BoundaryMm = loop,
                            ThicknessMm = thickness, Level = level, IfcTypeName = ifcName, Material = material };
                    }
                    case IfcEntity.Column:
                    {
                        var (point, ok, reason) = InsertionPointFromSolid(solid);
                        if (!ok) return Kept(e, entity, reason);
                        return new IfcElement { SourceId = e.Id.Value, Entity = entity, PointMm = point,
                            ThicknessMm = ProfileWidth(solid), Level = level, IfcTypeName = ifcName, Material = material };
                    }
                    case IfcEntity.Beam:
                    {
                        var (start, end, ok, reason) = BeamAxisFromSolid(solid);
                        if (!ok) return Kept(e, entity, reason);
                        // I4: CreateBeam places at level.Elevation + start.Z, so the
                        // emitted Z must be LEVEL-RELATIVE (the reader derives absolute
                        // world Z). Subtract the resolved level's elevation to avoid a
                        // double add that would float the beam by one storey.
                        start[2] -= levelElevMm; end[2] -= levelElevMm;
                        return new IfcElement { SourceId = e.Id.Value, Entity = entity, StartMm = start, EndMm = end,
                            ThicknessMm = ProfileWidth(solid), Level = level, IfcTypeName = ifcName, Material = material };
                    }
                    default: return Kept(e, entity, "unsupported entity");
                }
            }
            catch (Exception ex) { return Kept(e, entity, $"geometry error: {ex.Message}"); }
        }

        static IfcElement Kept(Element e, IfcEntity entity, string? reason) =>
            new() { SourceId = e.Id.Value, Entity = entity, Convertible = false, Reason = reason ?? "geometry not convertible" };

        // --- geometry helpers (Revit-in / primitives-out) ---
        static Solid? LargestSolid(Element e)
        {
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            Solid? best = null; double bestVol = 0;
            foreach (var g in e.get_Geometry(opt))
                foreach (var s in Flatten(g))
                    if (s.Volume > bestVol) { best = s; bestVol = s.Volume; }
            return best;
        }
        static IEnumerable<Solid> Flatten(GeometryObject g)
        {
            if (g is Solid s && s.Volume > 1e-6) yield return s;
            else if (g is GeometryInstance gi)
                foreach (var o in gi.GetInstanceGeometry())
                    foreach (var inner in Flatten(o)) yield return inner;
        }

        // The following four return (…, ok=false, reason) when the geometry can't be
        // reduced to a clean native input — that's what drives keep-original+report.
        // Each check is a SANITY GATE: prefer a wrong-count-free keep-as-is over an
        // approximated conversion (Global Constraint: never approximate).
        static (double[] start, double[] end, double height, double thickness, bool ok, string? reason) WallAxisFromSolid(Solid s)
        {
            var bb = s.GetBoundingBox(); if (bb == null) return (default!, default!, 0, 0, false, "wall has no bounding box");
            var min = bb.Min; var max = bb.Max;
            double dx = max.X - min.X, dy = max.Y - min.Y, dz = max.Z - min.Z;
            bool alongX = dx >= dy;
            double length = alongX ? dx : dy, thickness = alongX ? dy : dx;
            if (length < 1e-6 || thickness < 1e-6 || dz < 1e-6) return (default!, default!, 0, 0, false, "degenerate wall extents");
            // C3/I2: require an AXIS-ALIGNED RECTANGULAR footprint. The solid's true
            // horizontal footprint (avg cross-section = Volume/height) must fill most
            // of its axis-aligned bbox footprint. A 45° wall's bbox footprint is much
            // larger than its true footprint (fill ≈ 0.09) → kept-as-is. The threshold
            // is lenient enough to tolerate door/window openings (fill ≈ 0.8+), which
            // reduce volume only modestly, so real orthogonal walls still convert.
            double bboxFootprint = dx * dy;
            double footprintFill = bboxFootprint > 1e-9 ? (s.Volume / dz) / bboxFootprint : 0;
            if (footprintFill < WallFootprintFill) return (default!, default!, 0, 0, false, "angled or non-rectangular wall");
            double cx = (min.X + max.X) / 2, cy = (min.Y + max.Y) / 2;
            var start = alongX ? new[] { min.X, cy, min.Z } : new[] { cx, min.Y, min.Z };
            var end   = alongX ? new[] { max.X, cy, min.Z } : new[] { cx, max.Y, min.Z };
            return (Mm(start), Mm(end), dz * FtToMm, thickness * FtToMm, true, null);
        }
        static (double[][] loop, double thickness, bool ok, string? reason) SlabBoundaryFromSolid(Solid s)
        {
            // bottom-most horizontal planar face → its OUTER CurveLoop (largest area,
            // not the first, which may be an inner opening) as [x,y,z] mm points.
            PlanarFace? bottom = null;
            foreach (Face f in s.Faces)
                if (f is PlanarFace pf && Math.Abs(pf.FaceNormal.Z) > 0.99)
                    if (bottom == null || pf.Origin.Z < bottom.Origin.Z) bottom = pf;
            if (bottom == null) return (default!, 0, false, "slab has no horizontal planar face");
            var loops = bottom.GetEdgesAsCurveLoops();
            if (loops == null || loops.Count == 0) return (default!, 0, false, "slab boundary not found");
            CurveLoop? outer = null; double bestArea = -1;
            foreach (var cl in loops) { double a = LoopArea(cl); if (a > bestArea) { bestArea = a; outer = cl; } }
            if (outer == null) return (default!, 0, false, "slab boundary not found");
            var pts = new List<double[]>();
            foreach (var c in outer)
            {
                // C3/I3: reject any curved edge — the native create_floor loop is
                // straight-line only; an Arc/spline edge would be approximated.
                if (!(c is Line)) return (default!, 0, false, "curved slab edge");
                var p = c.GetEndPoint(0);
                pts.Add(Mm(new[] { p.X, p.Y, p.Z }));
            }
            if (pts.Count < 3) return (default!, 0, false, "slab boundary degenerate");
            var bb = s.GetBoundingBox();
            return (pts.ToArray(), (bb.Max.Z - bb.Min.Z) * FtToMm, true, null);
        }
        static (double[] point, bool ok, string? reason) InsertionPointFromSolid(Solid s)
        {
            var bb = s.GetBoundingBox(); if (bb == null) return (default!, false, "column has no bounding box");
            var min = bb.Min; var max = bb.Max;
            double dx = max.X - min.X, dy = max.Y - min.Y, dz = max.Z - min.Z;
            if (dx < 1e-6 || dy < 1e-6 || dz < 1e-6) return (default!, false, "non-prismatic column");
            // C3: require a VERTICAL PRISM — vertical extent dominant and the solid
            // fills its bbox (rectangular section). A round column fills only ~0.785,
            // so it is kept-as-is (its width can't be derived into a rectangular type).
            if (!(dz > dx && dz > dy)) return (default!, false, "non-prismatic column");
            double bboxVol = dx * dy * dz;
            if (bboxVol < 1e-9 || s.Volume / bboxVol < ColumnPrismFill) return (default!, false, "non-prismatic column");
            var c = (min + max) / 2;
            return (Mm(new[] { c.X, c.Y, min.Z }), true, null);
        }
        static (double[] start, double[] end, bool ok, string? reason) BeamAxisFromSolid(Solid s)
        {
            var bb = s.GetBoundingBox(); if (bb == null) return (default!, default!, false, "beam has no bounding box");
            var min = bb.Min; var max = bb.Max;
            double dx = max.X - min.X, dy = max.Y - min.Y, dz = max.Z - min.Z;
            bool alongX = dx >= dy;
            double length = alongX ? dx : dy;
            if (length < 1e-6) return (default!, default!, false, "degenerate beam length");
            // C3/I3: require a roughly HORIZONTAL beam — gate on the ACTUAL
            // end-to-end SLOPE, not the bbox height. Bbox rise (dz) conflates true
            // axis slope with the beam's own cross-section depth (a normal beam's
            // depth-over-span is ≈0.05–0.20), so a bbox-fraction gate at 0.5 let a
            // beam sloped up to ~20° pass and get emitted as a flat HORIZONTAL
            // segment at mid-Z (both ends set to the same z) — a silent geometric
            // approximation the feature's "never approximate" constraint forbids
            // (e.g. roof rafters).
            // Isolate slope from depth by sampling the solid's own edge vertices
            // near each end of the axis and comparing their AVERAGE Z: depth
            // spreads each end's vertices symmetrically above/below the beam's
            // centerline (cancels out in the average), while true slope shifts
            // both ends' average Z apart by the rise.
            double axisMin = alongX ? min.X : min.Y, axisMax = alongX ? max.X : max.Y;
            double startBand = axisMin + 0.25 * length, endBand = axisMax - 0.25 * length;
            var startZs = new List<double>(); var endZs = new List<double>();
            foreach (Edge edge in s.Edges)
            {
                var curve = edge.AsCurve(); if (curve == null) continue;
                foreach (var pt in new[] { curve.GetEndPoint(0), curve.GetEndPoint(1) })
                {
                    double axisCoord = alongX ? pt.X : pt.Y;
                    if (axisCoord <= startBand) startZs.Add(pt.Z);
                    else if (axisCoord >= endBand) endZs.Add(pt.Z);
                }
            }
            double beamSlopeTolFt = Math.Max(BeamSlopeTolMm / FtToMm, BeamSlopeFraction * length);
            if (startZs.Count > 0 && endZs.Count > 0)
            {
                if (Math.Abs(endZs.Average() - startZs.Average()) > beamSlopeTolFt)
                    return (default!, default!, false, "sloped beam");
            }
            else
            {
                // Couldn't cleanly read per-end Z from the solid's geometry — fall
                // back to a strict bbox-based proxy. Favor KEEP-AS-IS over a wrong
                // conversion when unsure.
                if (dz > beamSlopeTolFt) return (default!, default!, false, "sloped beam");
            }
            double cy = (min.Y + max.Y) / 2, cx = (min.X + max.X) / 2, z = (min.Z + max.Z) / 2;
            var start = alongX ? new[] { min.X, cy, z } : new[] { cx, min.Y, z };
            var end   = alongX ? new[] { max.X, cy, z } : new[] { cx, max.Y, z };
            return (Mm(start), Mm(end), true, null);
        }
        static double ProfileWidth(Solid s)
        {
            var bb = s.GetBoundingBox();
            return Math.Min(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y) * FtToMm;
        }
        static double LoopArea(CurveLoop cl)
        {
            // Shoelace on the XY projection using edge start points (magnitude only).
            var pts = cl.Select(c => c.GetEndPoint(0)).ToList();
            if (pts.Count < 3) return 0;
            double a = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i]; var q = pts[(i + 1) % pts.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return Math.Abs(a) / 2;
        }
        static Level? NearestLevel(Document doc, Solid s)
        {
            double z = s.GetBoundingBox().Min.Z;
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - z)).FirstOrDefault();
        }
        static double[] Mm(double[] ft) => new[] { ft[0] * FtToMm, ft[1] * FtToMm, ft[2] * FtToMm };

        // --- per-entity existing-type readers (C1/I5) ---
        // Each entity resolves against ITS OWN native types, not wall types for all.
        public List<ExistingType> ReadExistingWallTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Select(t => new ExistingType(t.Name, t.Width * FtToMm)).ToList();

        public List<ExistingType> ReadExistingFloorTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                .Select(t => new ExistingType(t.Name, FloorThicknessMm(t))).ToList();

        static double FloorThicknessMm(FloorType t)
        {
            try
            {
                var cs = t.GetCompoundStructure();
                if (cs != null) return cs.GetWidth() * FtToMm;
            }
            catch { /* some FloorTypes have no compound structure */ }
            return 0;
        }

        // Loadable-family types (columns/beams) can't be synthesized from a
        // thickness, so these feed match-only resolution. Width is read from the
        // symbol's b/h profile params when present, else 0 (→ likely kept-as-is).
        public List<ExistingType> ReadExistingColumnTypes(Document doc) =>
            new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Select(fs => new ExistingType(fs.Name, SymbolWidthMm(fs))).ToList();

        public List<ExistingType> ReadExistingBeamTypes(Document doc) =>
            new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Select(fs => new ExistingType(fs.Name, SymbolWidthMm(fs))).ToList();

        static double SymbolWidthMm(FamilySymbol fs)
        {
            double? b = ParamFt(fs, "b") ?? ParamFt(fs, "Width") ?? ParamFt(fs, "w");
            double? h = ParamFt(fs, "h") ?? ParamFt(fs, "Depth") ?? ParamFt(fs, "d");
            if (b.HasValue && h.HasValue) return Math.Min(b.Value, h.Value) * FtToMm;
            if (b.HasValue) return b.Value * FtToMm;
            if (h.HasValue) return h.Value * FtToMm;
            return 0;
        }
        static double? ParamFt(Element e, string name)
        {
            var p = e.LookupParameter(name);
            return (p != null && p.StorageType == StorageType.Double) ? p.AsDouble() : (double?)null;
        }
    }
}
