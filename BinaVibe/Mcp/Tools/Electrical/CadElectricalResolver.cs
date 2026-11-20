#nullable enable
// CadElectricalResolver - phase 1 of the CAD electrical to BIM work (spec 2026-11-17 §5.5).
//
// The Revit reads behind plan_cad_electrical, and nothing else: the model's walls per level
// as plain centrelines, the devices already in it, its family types, and "room at this
// point" and "ceiling at this point" lookups. CadModelFit and CadSymbolResolve do the
// deciding on that plain data (D6).
//
// THIS FILE IS THE ft<->mm BOUNDARY for the plan: what it hands out is millimetres in
// project-internal coordinates; what it reads from the API is feet. Read-only: no
// Transaction is opened here.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>What a drawing plan needs from the model, read once per call.</summary>
    internal sealed class ModelSnapshot
    {
        /// <summary>Wall centrelines per level name, local and linked.</summary>
        public Dictionary<string, List<ModelWall>> WallsByLevel = new Dictionary<string, List<ModelWall>>(StringComparer.Ordinal);
        /// <summary>Every device-category instance in the model, mm.</summary>
        public List<(double X, double Y, double Z)> ExistingDevices = new List<(double X, double Y, double Z)>();
        /// <summary>The model's "Family : Type" names matching a mapping's text.</summary>
        public Func<string, IReadOnlyList<string>> FamilyTypeMatches = _ => Array.Empty<string>();
        /// <summary>"Family : Type" to its hosting kind (CadElectricalTools.HostingKind).</summary>
        public Dictionary<string, string> HostingByType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>"Family : Type" to its category id.</summary>
        public Dictionary<string, long> CategoryByType = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public List<Level> Levels = new List<Level>();
        /// <summary>Every device-category instance, with its id and contact height, mm: what a
        /// conduit run's drop can reach.</summary>
        public List<PresentDevice> Devices = new List<PresentDevice>();
        /// <summary>The model's conduit types, "Family : Type", with fittings and trade sizes.</summary>
        public List<RunTypeInfo> ConduitTypes = new List<RunTypeInfo>();
        /// <summary>The model's cable tray types, "Family : Type", with fittings.</summary>
        public List<RunTypeInfo> TrayTypes = new List<RunTypeInfo>();
    }

    internal static class CadElectricalResolver
    {
        private const double MmPerFoot = 304.8;
        /// <summary>A wall is on a level when it stands at this height above the level.</summary>
        private const double WallProbeMm = 1000;
        /// <summary>A room is looked for at this height above the level.</summary>
        private const double RoomProbeMm = 1000;
        /// <summary>Arcs become chords about this long.</summary>
        private const double ChordMm = 500;

        public static ModelSnapshot Read(Document doc)
        {
            var snapshot = new ModelSnapshot
            {
                Levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).ToList(),
            };

            foreach (Level level in snapshot.Levels)
                snapshot.WallsByLevel[level.Name] = new List<ModelWall>();

            AddWalls(doc, Transform.Identity, false, snapshot);
            foreach (RevitLinkInstance link in Links(doc))
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                Transform t;
                try { t = link.GetTotalTransform(); } catch { continue; }
                AddWalls(linkDoc, t, true, snapshot);
            }

            snapshot.Devices = ExistingDevices(doc);
            snapshot.ExistingDevices = snapshot.Devices.Select(d => (d.X, d.Y, d.Z)).ToList();
            snapshot.ConduitTypes = ConduitTypes(doc);
            snapshot.TrayTypes = TrayTypes(doc);

            var names = new FilteredElementCollector(doc).WhereElementIsElementType()
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Select(fs => (Full: CadElectricalTools.FullName(fs), Type: fs.Name, Family: fs.Family, Category: CategoryId(fs)))
                .ToList();
            foreach (var n in names)
            {
                snapshot.HostingByType[n.Full] = CadElectricalTools.HostingKind(n.Family);
                if (n.Category.HasValue) snapshot.CategoryByType[n.Full] = n.Category.Value;
            }

            snapshot.FamilyTypeMatches = text =>
            {
                string wanted = text.Trim();
                List<string> full = names.Where(n => string.Equals(n.Full, wanted, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Full).Distinct(StringComparer.Ordinal).ToList();
                if (full.Count > 0) return full;
                return names.Where(n => string.Equals(n.Type, wanted, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Full).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
            };
            return snapshot;
        }

        /// <summary>Every device-category instance with a location point, in mm: plan position
        /// from the location point, Z its contact height (<see cref="ContactHeight"/>) and
        /// whether that can be trusted, the origin's Z, the ceiling it is hosted on, its
        /// category and its level's elevation.</summary>
        internal static List<PresentDevice> ExistingDevices(Document doc)
        {
            var devices = new List<PresentDevice>();
            var filter = new ElementMulticategoryFilter(CadElectricalTools.DeviceCategories.ToList());
            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter))
            {
                if (!(el.Location is LocationPoint lp)) continue;
                double zFt = lp.Point.Z;
                long? ceilingId = null;
                bool known = true;
                if (el is FamilyInstance fi)
                {
                    Contact c = ContactHeight(doc, fi);
                    zFt = c.ZFt;
                    ceilingId = c.CeilingId;
                    known = c.Known;
                }
                devices.Add(new PresentDevice
                {
                    X = lp.Point.X * MmPerFoot,
                    Y = lp.Point.Y * MmPerFoot,
                    Z = zFt * MmPerFoot,
                    ZKnown = known,
                    OriginZ = lp.Point.Z * MmPerFoot,
                    CeilingId = ceilingId,
                    Id = el.Id.Value,
                    CategoryId = CategoryId(el),
                    LevelZMm = OwnLevel(doc, el)?.Elevation * MmPerFoot,
                });
            }
            return devices;
        }

        /// <summary>An element's category id; null when it has none.</summary>
        internal static long? CategoryId(Element el)
        {
            try { return el.Category?.Id.Value; } catch { return null; }
        }

        /// <summary>An element's own level: its LevelId, else its schedule or reference level.</summary>
        internal static Level? OwnLevel(Document doc, Element el)
        {
            Level? level = null;
            try { level = doc.GetElement(el.LevelId) as Level; } catch { level = null; }
            return level
                   ?? LevelParam(doc, el, BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM)
                   ?? LevelParam(doc, el, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
        }

        /// <summary>The level band a point on this level stands in, mm: the level's elevation up
        /// to the next level's (null high when there is none above).</summary>
        internal static (double Low, double? High) LevelBand(IReadOnlyList<Level> levels, Level level)
        {
            double low = level.Elevation * MmPerFoot;
            double? high = null;
            foreach (Level l in levels)
            {
                double z = l.Elevation * MmPerFoot;
                if (z > low + OverheadPlacement.SameLevelMm && (!high.HasValue || z < high.Value)) high = z;
            }
            return (low, high);
        }

        /// <summary>The model's conduit types with their trade sizes (ConduitSizesMm).</summary>
        internal static List<RunTypeInfo> ConduitTypes(Document doc)
        {
            List<(string Standard, double Mm)> sizes = ConduitSizeTable(doc);
            var types = new List<RunTypeInfo>();
            foreach (ConduitType t in new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<ConduitType>())
            {
                bool withFittings = false;
                try { withFittings = t.IsWithFitting; } catch { withFittings = false; }
                types.Add(new RunTypeInfo
                {
                    Name = t.FamilyName + " : " + t.Name,
                    TypeName = t.Name,
                    WithFittings = withFittings,
                    SizesMm = ConduitSizesMm(t, sizes),
                });
            }
            return types.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The model's cable tray types.</summary>
        internal static List<RunTypeInfo> TrayTypes(Document doc)
        {
            var types = new List<RunTypeInfo>();
            foreach (CableTrayType t in new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<CableTrayType>())
            {
                bool withFittings = false;
                try { withFittings = t.IsWithFitting; } catch { withFittings = false; }
                types.Add(new RunTypeInfo { Name = t.FamilyName + " : " + t.Name, TypeName = t.Name, WithFittings = withFittings });
            }
            return types.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Every conduit standard's trade sizes in the model's size settings, mm
        /// (nominal diameter, the sizes offered in size lists). Empty when it cannot be read.</summary>
        internal static List<(string Standard, double Mm)> ConduitSizeTable(Document doc)
        {
            var table = new List<(string Standard, double Mm)>();
            try
            {
                ConduitSizeSettings settings = ConduitSizeSettings.GetConduitSizeSettings(doc);
                foreach (KeyValuePair<string, ConduitSizes> standard in settings)
                {
                    foreach (ConduitSize size in standard.Value)
                    {
                        if (size.UsedInSizeLists) table.Add((standard.Key, size.NominalDiameter * MmPerFoot));
                    }
                }
            }
            catch { /* no size settings: the diameter is not checked */ }
            return table;
        }

        /// <summary>A conduit type's sizes: its standard's when the standard can be read and is in
        /// the table, else every standard's (a looser check, never a wrong refusal).</summary>
        internal static List<double> ConduitSizesMm(ConduitType type, List<(string Standard, double Mm)> table)
        {
            string? standard = null;
            try
            {
                Parameter? p = type.get_Parameter(BuiltInParameter.CONDUIT_STANDARD_TYPE_PARAM);
                standard = p?.AsValueString() ?? p?.AsString();
            }
            catch { standard = null; }

            List<double> own = table.Where(s => standard != null && string.Equals(s.Standard, standard, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Mm).ToList();
            List<double> sizes = own.Count > 0 ? own : table.Select(s => s.Mm).ToList();
            return sizes.Select(mm => Math.Round(mm, 2)).Distinct().OrderBy(mm => mm).ToList();
        }

        /// <summary>Where a device meets its host, feet, how that was read, and the ceiling it is on.</summary>
        internal readonly struct Contact
        {
            public readonly double ZFt;
            /// <summary>host_face | ceiling_underside | level_offset | vertical_plane | location</summary>
            public readonly string Via;
            public readonly long? CeilingId;
            /// <summary>False when ZFt is a work-plane-based family's origin on a plane that is not
            /// vertical: the origin can sit off that plane, so the height is not trusted.</summary>
            public readonly bool Known;
            public Contact(double zFt, string via, long? ceilingId, bool known = true)
            { ZFt = zFt; Via = via; CeilingId = ceilingId; Known = known; }
        }

        /// <summary>
        /// A device's contact height: where it meets its host, under its origin. Not the
        /// LocationPoint's Z, because a work-plane-based family's origin can sit off its work
        /// plane by a per-family amount (a live test read +175 mm for one fitting and -2044 mm for
        /// another, both hosted on the same underside with "Offset from Host" 0).
        ///   on a face (HostFace readable): that face's Z under the origin, plus the instance's
        ///     offset from it along the face normal;
        ///   on a ceiling with no face (a ceiling-hosted family): the ceiling's underside there;
        ///   a work-plane-based family on a vertical plane (a wall face, read or not, or a
        ///     vertical work plane with no host): the LocationPoint's Z. Its origin can sit off
        ///     the plane only along the plane's normal, which is level, so Z is honest;
        ///   on a level or a horizontal work plane (LevelBase): that plane plus the offset
        ///     parameter read back (OffsetParams), work-plane-based and unhosted families alike;
        ///   anything else: the LocationPoint's Z, which an unhosted family's origin honours.
        ///     For a work-plane-based family this is not trusted (Known false).
        /// </summary>
        internal static Contact ContactHeight(Document doc, FamilyInstance fi)
        {
            XYZ? at = (fi.Location as LocationPoint)?.Point;
            if (at == null)
            {
                BoundingBoxXYZ? bb = null;
                try { bb = fi.get_BoundingBox(null); } catch { bb = null; }
                return new Contact(bb != null ? (bb.Min.Z + bb.Max.Z) / 2 : 0, "location", null, known: false);
            }

            Element? host = null;
            try { host = fi.Host; } catch { host = null; }
            long? ceilingId = host is Ceiling ? host.Id.Value : (long?)null;

            // On a face: the face's Z under the origin, moved by the instance's own offset.
            try
            {
                Reference? r = fi.HostFace;
                if (host != null && r != null && host.GetGeometryObjectFromReference(r) is Face face)
                {
                    // A wall's side face is vertical: it has no Z under the origin, and a
                    // device on it keeps its location Z (below).
                    double? z = FaceZAt(face, host, at.X, at.Y);
                    double? nz = z.HasValue ? NormalZ(face, at.X, at.Y, z.Value) : null;
                    if (z.HasValue && nz.HasValue && Math.Abs(nz.Value) >= 0.1)
                    {
                        double offset = DoubleParam(fi, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM) ?? 0;
                        return new Contact(z.Value + (offset * nz.Value), "host_face", ceilingId);
                    }
                }
            }
            catch { /* a host face that cannot be read (in a link, gone after an edit) */ }

            if (host is Ceiling ceiling)
            {
                BoundingBoxXYZ? bb = null;
                try { bb = ceiling.get_BoundingBox(null); } catch { bb = null; }
                if (bb != null)
                {
                    double? z = UndersideAt(BottomFaces(ceiling), at.X, at.Y, bb.Min.Z - 1, bb.Max.Z + 1);
                    if (z.HasValue) return new Contact(z.Value, "ceiling_underside", ceilingId);
                }
            }

            // On a vertical plane: the origin's Z. A face-based socket that the wall overload put
            // on a wall plane, or on a vertical work plane with no host, used to fall through to
            // the level rule below and read the level plus an "Offset from Host" of about 0: the
            // floor, a mount height under where it stands.
            bool workPlane = IsWorkPlaneBased(fi);
            if (workPlane && OnVerticalPlane(fi, host))
                return new Contact(at.Z, "vertical_plane", ceilingId);

            // On a level (or a horizontal work plane): the plane plus the offset parameter read
            // back, never the origin. A live test read a work-plane light's origin 2044 mm under
            // a level + offset that had taken.
            if (LevelBase(doc, fi, null, out double baseFt))
            {
                double? contactMm = OverheadPlacement.LevelContactMm(
                    baseFt * MmPerFoot,
                    OffsetParams(fi).Select(b => DoubleParam(fi, b) * MmPerFoot).ToList());
                if (contactMm.HasValue) return new Contact(contactMm.Value / MmPerFoot, "level_offset", null);
            }

            return new Contact(at.Z, "location", ceilingId, known: !workPlane);
        }

        private static bool IsWorkPlaneBased(FamilyInstance fi)
        {
            try { return fi.Symbol.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased; }
            catch { return false; }
        }

        /// <summary>True when a work-plane-based instance stands on a vertical plane: hosted on a
        /// wall, or its own work plane normal (GetTotalTransform().BasisZ) level.</summary>
        private static bool OnVerticalPlane(FamilyInstance fi, Element? host)
        {
            if (host is Wall) return true;
            try { return Math.Abs(fi.GetTotalTransform().BasisZ.Z) < VerticalPlaneNormalZ; }
            catch { return false; }
        }

        /// <summary>A plane whose normal's Z is below this is vertical (a wall face).</summary>
        private const double VerticalPlaneNormalZ = 0.1;

        /// <summary>The offset parameters of a device placed on a level, the one that applies
        /// first: a work-plane-based family's is "Offset from Host", an unhosted one's
        /// "Elevation from Level".</summary>
        internal static BuiltInParameter[] OffsetParams(FamilyInstance fi)
        {
            bool workPlane;
            try { workPlane = fi.Symbol.Family.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased; }
            catch { workPlane = false; }
            return workPlane
                ? new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM }
                : new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM };
        }

        /// <summary>
        /// The height, feet, that a device placed on a level measures its offset from: a level
        /// host's elevation; a horizontal sketch or reference plane host's Z; with no host, the
        /// instance's level (LevelId, then its schedule or reference level). False for a device
        /// on a wall, floor, ceiling or other element (its offset is from that host's face, not
        /// a level), or when no level is found and <paramref name="fallback"/> is null.
        /// </summary>
        internal static bool LevelBase(Document doc, FamilyInstance fi, Level? fallback, out double baseFt)
        {
            baseFt = 0;
            FamilyPlacementType placement;
            try { placement = fi.Symbol.Family.FamilyPlacementType; } catch { return false; }
            if (placement != FamilyPlacementType.WorkPlaneBased && placement != FamilyPlacementType.OneLevelBased)
                return false;

            Element? host = null;
            try { host = fi.Host; } catch { host = null; }
            if (host is Level hostLevel) { baseFt = hostLevel.Elevation; return true; }
            if (host is SketchPlane || host is ReferencePlane)
            {
                try
                {
                    Plane plane = host is SketchPlane sp ? sp.GetPlane() : ((ReferencePlane)host).GetPlane();
                    if (Math.Abs(plane.Normal.Z) < 0.9) return false;
                    baseFt = plane.Origin.Z;
                    return true;
                }
                catch { return false; }
            }
            if (host != null) return false;

            Level? level = doc.GetElement(fi.LevelId) as Level
                           ?? LevelParam(doc, fi, BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM)
                           ?? LevelParam(doc, fi, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)
                           ?? fallback;
            if (level == null) return false;
            baseFt = level.Elevation;
            return true;
        }

        private static Level? LevelParam(Document doc, Element el, BuiltInParameter bip)
        {
            try
            {
                Parameter? p = el.get_Parameter(bip);
                return p != null && p.StorageType == StorageType.ElementId ? doc.GetElement(p.AsElementId()) as Level : null;
            }
            catch { return null; }
        }

        /// <summary>A face's Z over (x, y), feet: the plane's for a planar face, else where a
        /// vertical through the point crosses it within the host's box.</summary>
        private static double? FaceZAt(Face face, Element host, double x, double y)
        {
            if (face is PlanarFace pf)
            {
                XYZ o = pf.Origin, n = pf.FaceNormal;
                return OverheadPlacement.PlaneZAt(o.X, o.Y, o.Z, n.X, n.Y, n.Z, x, y);
            }
            BoundingBoxXYZ? bb = null;
            try { bb = host.get_BoundingBox(null); } catch { bb = null; }
            if (bb == null) return null;
            return UndersideAt(new[] { face }, x, y, bb.Min.Z - 1, bb.Max.Z + 1);
        }

        /// <summary>The Z of a face's normal at a point on it; null when it cannot be read.</summary>
        internal static double? NormalZ(Face face, double x, double y, double z)
        {
            try
            {
                if (face is PlanarFace pf) return pf.FaceNormal.Z;
                IntersectionResult? hit = face.Project(new XYZ(x, y, z));
                return hit == null ? (double?)null : face.ComputeNormal(hit.UVPoint).Normalize().Z;
            }
            catch { return null; }
        }

        private static double? DoubleParam(Element el, BuiltInParameter bip)
        {
            try
            {
                Parameter? p = el.get_Parameter(bip);
                return p != null && p.HasValue && p.StorageType == StorageType.Double ? p.AsDouble() : (double?)null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The room at a plan point on this level, mm in and out: the model's own rooms
        /// first, then each loaded link's, through the inverse of its transform. Looked for at
        /// 1000 mm above the level. Floor Z is the room's level plus its base offset; it is not the
        /// height basis for a drawing plan (CadSymbolResolve measures from the target level).
        /// </summary>
        public static Func<double, double, RoomHit?> RoomLookup(Document doc, Level level)
        {
            double zFt = level.Elevation + (RoomProbeMm / MmPerFoot);
            var links = new List<(Document Doc, Transform T, Transform Inverse)>();
            foreach (RevitLinkInstance link in Links(doc))
            {
                Document? linkDoc = link.GetLinkDocument();
                if (linkDoc == null) continue;
                try
                {
                    Transform t = link.GetTotalTransform();
                    links.Add((linkDoc, t, t.Inverse));
                }
                catch { /* a link with no usable transform finds no rooms */ }
            }

            return (xMm, yMm) =>
            {
                var at = new XYZ(xMm / MmPerFoot, yMm / MmPerFoot, zFt);
                Room? room = null;
                try { room = doc.GetRoomAtPoint(at); } catch { room = null; }
                if (room != null)
                    return new RoomHit { Id = room.Id.Value, Name = room.Name ?? "", FloorZMm = SocketCandidates.RoomFloorZMm(doc, room) };

                foreach (var link in links)
                {
                    try { room = link.Doc.GetRoomAtPoint(link.Inverse.OfPoint(at)); } catch { room = null; }
                    if (room == null) continue;
                    double floorFt = SocketCandidates.RoomFloorZMm(link.Doc, room) / MmPerFoot;
                    return new RoomHit
                    {
                        Id = room.Id.Value,
                        Name = room.Name ?? "",
                        FloorZMm = link.T.OfPoint(new XYZ(0, 0, floorFt)).Z * MmPerFoot,
                        Linked = true,
                    };
                }
                return null;
            };
        }

        /// <summary>
        /// The local ceiling over a plan point on this level, mm in and out, with its underside
        /// there. A ceiling is on the level when its LevelId is the level's. Each ceiling's box is
        /// tried first; then a vertical line through the point must cross one of its bottom faces
        /// (HostObjectUtils.GetBottomFaces), which is the point-in-polygon test, holes and slopes
        /// included, and gives the underside's Z at that point. Two ceilings over one point give
        /// the lower underside. Ceilings in a link are not looked at: there is nothing local to host
        /// on. A ceiling whose faces cannot be read is skipped.
        /// </summary>
        public static Func<double, double, CeilingHit?> CeilingLookup(Document doc, Level level)
        {
            var ceilings = new List<(long Id, BoundingBoxXYZ Box, List<Face> Bottom)>();
            foreach (Ceiling ceiling in new FilteredElementCollector(doc).OfClass(typeof(Ceiling)).Cast<Ceiling>())
            {
                if (ceiling.LevelId != level.Id) continue;
                BoundingBoxXYZ? bb;
                try { bb = ceiling.get_BoundingBox(null); } catch { bb = null; }
                if (bb == null) continue;
                List<Face> bottom = BottomFaces(ceiling);
                if (bottom.Count > 0) ceilings.Add((ceiling.Id.Value, bb, bottom));
            }

            return (xMm, yMm) =>
            {
                double x = xMm / MmPerFoot, y = yMm / MmPerFoot;
                CeilingHit? best = null;
                foreach (var c in ceilings)
                {
                    if (x < c.Box.Min.X || x > c.Box.Max.X || y < c.Box.Min.Y || y > c.Box.Max.Y) continue;
                    double? z = UndersideAt(c.Bottom, x, y, c.Box.Min.Z - 1, c.Box.Max.Z + 1);
                    if (!z.HasValue) continue;
                    double zMm = z.Value * MmPerFoot;
                    if (best == null || zMm < best.UndersideZMm) best = new CeilingHit { Id = c.Id, UndersideZMm = zMm };
                }
                return best;
            };
        }

        /// <summary>The ceiling's bottom faces, read once.</summary>
        internal static List<Face> BottomFaces(Ceiling ceiling)
        {
            var faces = new List<Face>();
            IList<Reference> refs;
            try { refs = HostObjectUtils.GetBottomFaces(ceiling); } catch { return faces; }
            foreach (Reference r in refs)
            {
                try
                {
                    if (ceiling.GetGeometryObjectFromReference(r) is Face face) faces.Add(face);
                }
                catch { /* a face that cannot be read is left out */ }
            }
            return faces;
        }

        /// <summary>Where a vertical line through (x, y), feet, crosses the lowest of these faces;
        /// null when it misses them all (the point is outside the ceiling's outline, or in a hole).</summary>
        internal static double? UndersideAt(IReadOnlyList<Face> faces, double x, double y, double zLowFt, double zHighFt)
        {
            Line line;
            try { line = Line.CreateBound(new XYZ(x, y, zLowFt), new XYZ(x, y, zHighFt)); } catch { return null; }
            double? lowest = null;
            foreach (Face face in faces)
            {
                try
                {
                    if (face.Intersect(line, out IntersectionResultArray? hits) != SetComparisonResult.Overlap || hits == null)
                        continue;
                    for (int i = 0; i < hits.Size; i++)
                    {
                        double z = hits.get_Item(i).XYZPoint.Z;
                        if (!lowest.HasValue || z < lowest.Value) lowest = z;
                    }
                }
                catch { /* a face that cannot be intersected is not a hit */ }
            }
            return lowest;
        }

        private static List<RevitLinkInstance> Links(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();

        /// <summary>
        /// One document's walls, carried into the host's coordinates, filed under every host
        /// level whose elevation + 1000 mm they stand across. The centreline is the location
        /// curve moved off the wall's location line (WALL_KEY_REF_PARAM) to the wall's middle.
        /// </summary>
        private static void AddWalls(Document source, Transform t, bool linked, ModelSnapshot snapshot)
        {
            foreach (Wall wall in new FilteredElementCollector(source).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (!(wall.Location is LocationCurve lc) || lc.Curve == null) continue;

                BoundingBoxXYZ? bb;
                try { bb = wall.get_BoundingBox(null); } catch { bb = null; }
                if (bb == null) continue;
                double z1 = t.OfPoint(bb.Min).Z, z2 = t.OfPoint(bb.Max).Z;
                double zMin = Math.Min(z1, z2), zMax = Math.Max(z1, z2);

                double widthFt;
                try { widthFt = wall.Width; } catch { continue; }
                XYZ offset = CentreOffset(wall, widthFt);

                List<XYZ> points = Flatten(lc.Curve);
                var walls = new List<ModelWall>();
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    XYZ a = t.OfPoint(points[i] + offset), b = t.OfPoint(points[i + 1] + offset);
                    walls.Add(new ModelWall
                    {
                        Id = wall.Id.Value,
                        X1 = a.X * MmPerFoot,
                        Y1 = a.Y * MmPerFoot,
                        X2 = b.X * MmPerFoot,
                        Y2 = b.Y * MmPerFoot,
                        ThicknessMm = widthFt * MmPerFoot,
                        Linked = linked,
                        TopZMm = zMax * MmPerFoot,
                    });
                }

                foreach (Level level in snapshot.Levels)
                {
                    double probe = level.Elevation + (WallProbeMm / MmPerFoot);
                    if (probe < zMin || probe > zMax) continue;
                    snapshot.WallsByLevel[level.Name].AddRange(walls);
                }
            }
        }

        /// <summary>
        /// From the location line to the wall's middle, in the wall's own document. Finish and
        /// core faces are both taken as half the width off the middle; Orientation points to
        /// the exterior face.
        /// </summary>
        private static XYZ CentreOffset(Wall wall, double widthFt)
        {
            int key = 0;
            try
            {
                Parameter? p = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                if (p != null && p.StorageType == StorageType.Integer) key = p.AsInteger();
            }
            catch { key = 0; }

            double toCentre;
            switch ((WallLocationLine)key)
            {
                case WallLocationLine.FinishFaceExterior:
                case WallLocationLine.CoreExterior:
                    toCentre = -widthFt / 2;
                    break;
                case WallLocationLine.FinishFaceInterior:
                case WallLocationLine.CoreInterior:
                    toCentre = widthFt / 2;
                    break;
                default:
                    toCentre = 0;
                    break;
            }
            if (toCentre == 0) return XYZ.Zero;

            XYZ orientation;
            try { orientation = wall.Orientation; } catch { return XYZ.Zero; }
            var flat = new XYZ(orientation.X, orientation.Y, 0);
            return flat.IsZeroLength() ? XYZ.Zero : flat.Normalize() * toCentre;
        }

        /// <summary>A curve as points: a line is its two ends, anything else chords of about
        /// 500 mm.</summary>
        private static List<XYZ> Flatten(Curve curve)
        {
            var points = new List<XYZ>();
            if (curve is Line)
            {
                points.Add(curve.GetEndPoint(0));
                points.Add(curve.GetEndPoint(1));
                return points;
            }

            int n = (int)Math.Ceiling(curve.Length * MmPerFoot / ChordMm);
            if (n < 2) n = 2;
            for (int i = 0; i <= n; i++) points.Add(curve.Evaluate((double)i / n, true));
            return points;
        }
    }
}
