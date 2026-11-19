#nullable enable
// CadElectricalResolver - phase 1 of the CAD electrical to BIM work (spec 2026-11-17 §5.5).
//
// The Revit reads behind plan_cad_electrical, and nothing else: the model's walls per level
// as plain centrelines, the devices already in it, its family types, and a "room at this
// point" lookup. CadModelFit and CadSymbolResolve do the deciding on that plain data (D6).
//
// THIS FILE IS THE ft<->mm BOUNDARY for the plan: what it hands out is millimetres in
// project-internal coordinates; what it reads from the API is feet. Read-only: no
// Transaction is opened here.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

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
        public List<Level> Levels = new List<Level>();
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

            snapshot.ExistingDevices = ExistingDevices(doc);

            var names = new FilteredElementCollector(doc).WhereElementIsElementType()
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Select(fs => (Full: CadElectricalTools.FullName(fs), Type: fs.Name, Family: fs.Family))
                .ToList();
            foreach (var n in names)
                snapshot.HostingByType[n.Full] = CadElectricalTools.HostingKind(n.Family);

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

        /// <summary>Every device-category instance with a location point, in mm.</summary>
        internal static List<(double X, double Y, double Z)> ExistingDevices(Document doc)
        {
            var devices = new List<(double X, double Y, double Z)>();
            var filter = new ElementMulticategoryFilter(CadElectricalTools.DeviceCategories.ToList());
            foreach (Element el in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter))
            {
                if (el.Location is LocationPoint lp)
                    devices.Add((lp.Point.X * MmPerFoot, lp.Point.Y * MmPerFoot, lp.Point.Z * MmPerFoot));
            }
            return devices;
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
