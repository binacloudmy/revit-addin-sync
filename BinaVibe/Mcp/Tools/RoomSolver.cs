// RoomSolver — Phase 4-6 geometry core for the model-sight stack.
// Pure segment math where possible; Revit lookups (FindRoom, RoomDoors,
// Verify) are isolated so the math stays reviewable without Revit.
// All lengths in FEET (wire contract), angles in radians internally.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace BinaVibe.Mcp.Tools
{
    internal sealed class WallSeg
    {
        public long? WallId;
        public XYZ Start = XYZ.Zero;
        public XYZ End = XYZ.Zero;
        public XYZ InwardNormal = XYZ.BasisY;
        public double LengthFt;
        public bool HasDoor;
        public XYZ Mid => (Start + End) * 0.5;
    }

    internal static class RoomSolver
    {
        public static Room FindRoom(Document doc, string nameOrNumber) =>
            new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => r.Area > 0)
                .FirstOrDefault(r =>
                    string.Equals(r.Name, nameOrNumber, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.Number, nameOrNumber, StringComparison.OrdinalIgnoreCase)
                    || r.Name.IndexOf(nameOrNumber, StringComparison.OrdinalIgnoreCase) >= 0);

        // Boundary loop -> wall segments with inward normals. Inward test:
        // nudge the segment midpoint along the candidate normal and ask the
        // room whether the nudged point is inside (Room.IsPointInRoom).
        public static List<WallSeg> WallSegments(Room room)
        {
            var segs = new List<WallSeg>();
            var opts = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
            };
            var loops = room.GetBoundarySegments(opts);
            if (loops == null) return segs;
            foreach (var loop in loops)
            {
                foreach (var bs in loop)
                {
                    var curve = bs.GetCurve();
                    if (curve == null || curve.Length < 0.1) continue;
                    var s = curve.GetEndPoint(0);
                    var e = curve.GetEndPoint(1);
                    var dir = (e - s).Normalize();
                    var n = new XYZ(-dir.Y, dir.X, 0);        // left normal
                    var mid = (s + e) * 0.5;
                    var probe = mid + n * 0.5;                 // 0.5 ft nudge
                    if (!room.IsPointInRoom(new XYZ(probe.X, probe.Y, mid.Z + 1.0)))
                        n = n.Negate();
                    segs.Add(new WallSeg
                    {
                        WallId = bs.ElementId != ElementId.InvalidElementId
                            ? bs.ElementId.Value : (long?)null,
                        Start = s,
                        End = e,
                        InwardNormal = n,
                        LengthFt = curve.Length,
                    });
                }
            }
            return segs;
        }

        // Doors bounding this room (FromRoom/ToRoom on either side).
        public static List<FamilyInstance> RoomDoors(Document doc, Room room) =>
            new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(d => d.FromRoom?.Id == room.Id || d.ToRoom?.Id == room.Id)
                .ToList();

        // Mark segments whose midpoint is within 3 ft of a door location.
        public static void MarkDoorSegments(List<WallSeg> segs, List<FamilyInstance> doors)
        {
            foreach (var seg in segs)
                seg.HasDoor = doors.Any(d =>
                    d.Location is LocationPoint dl
                    && DistPointToSegXY(dl.Point, seg.Start, seg.End) < 3.0);
        }

        public static WallSeg PickWall(List<WallSeg> segs, string selector,
                                       List<FamilyInstance> doors)
        {
            if (segs.Count == 0) return null;
            var usable = segs.Where(s => s.LengthFt >= 1.5).ToList();
            if (usable.Count == 0) usable = segs;
            switch ((selector ?? "longest").ToLowerInvariant())
            {
                case "furthest_from_door":
                    var dpts = doors
                        .Select(d => (d.Location as LocationPoint)?.Point)
                        .Where(p => p != null)
                        .ToList();
                    if (dpts.Count == 0) goto case "longest";
                    return usable.OrderByDescending(s =>
                        dpts.Min(p => p.DistanceTo(s.Mid))).First();
                case "north": return usable.OrderByDescending(s => s.Mid.Y).First();
                case "south": return usable.OrderBy(s => s.Mid.Y).First();
                case "east": return usable.OrderByDescending(s => s.Mid.X).First();
                case "west": return usable.OrderBy(s => s.Mid.X).First();
                case "longest":
                default:
                    return usable.OrderByDescending(s => s.LengthFt).First();
            }
        }

        // N points along the wall, inset into the room by insetFt.
        // count==1 -> wall midpoint. count>1 -> centered spread, spacingFt apart.
        public static List<XYZ> AnchorPoints(WallSeg seg, int count,
                                             double spacingFt, double insetFt)
        {
            var dir = (seg.End - seg.Start).Normalize();
            var pts = new List<XYZ>();
            if (count <= 1)
            {
                pts.Add(seg.Mid + seg.InwardNormal * insetFt);
                return pts;
            }
            double span = spacingFt * (count - 1);
            var start = seg.Mid - dir * (span / 2.0);
            for (int i = 0; i < count; i++)
                pts.Add(start + dir * (spacingFt * i) + seg.InwardNormal * insetFt);
            return pts;
        }

        // Plan angle (radians, from +X CCW) that faces along `toward`.
        public static double FacingAngle(XYZ toward) =>
            Math.Atan2(toward.Y, toward.X);

        // Room centre in plan, averaged from wall-segment midpoints. Cheap and
        // robust enough to answer "which wall is behind me" without needing the
        // full boundary polygon area centroid.
        public static XYZ RoomCentroid(Room room)
        {
            var segs = WallSegments(room);
            if (segs.Count == 0)
                return (room.Location as LocationPoint)?.Point ?? XYZ.Zero;
            double x = segs.Average(s => s.Mid.X);
            double y = segs.Average(s => s.Mid.Y);
            return new XYZ(x, y, 0);
        }

        // The wall a fixture BACKS ONTO: of the room's segments, the one whose
        // inward normal best aligns with the direction from the fixture toward
        // the room centre. This is deliberately NOT "nearest wall" — in a row of
        // toilet stalls the nearest wall is a side partition whose inward normal
        // is perpendicular to the true facing, which is exactly why a
        // nearest-wall test flips fixtures the wrong way. Returns null when the
        // room has no usable segments.
        public static WallSeg BackingWall(Room room, XYZ fixtureXY)
        {
            var segs = WallSegments(room);
            if (segs.Count == 0) return null;
            var toCentre = RoomCentroid(room) - fixtureXY;
            toCentre = new XYZ(toCentre.X, toCentre.Y, 0);
            if (toCentre.GetLength() < 1e-6)
                // Fixture sits ~at the room centre — no meaningful "behind";
                // fall back to the nearest wall so we still return something.
                return segs.OrderBy(s => DistPointToSegXY(fixtureXY, s.Start, s.End)).First();
            toCentre = toCentre.Normalize();
            return segs.OrderByDescending(s => s.InwardNormal.DotProduct(toCentre)).First();
        }

        public static double DistPointToSegXY(XYZ p, XYZ a, XYZ b)
        {
            var ab = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            var ap = new XYZ(p.X - a.X, p.Y - a.Y, 0);
            double len2 = ab.DotProduct(ab);
            double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ap.DotProduct(ab) / len2));
            var proj = new XYZ(a.X + ab.X * t, a.Y + ab.Y * t, 0);
            return new XYZ(p.X - proj.X, p.Y - proj.Y, 0).GetLength();
        }

        // Typology: find an existing instance of the same type placed in
        // another room — same-name-prefix rooms first ("Tandas ..." family).
        public static FamilyInstance FindTypologyExample(Document doc,
            FamilySymbol sym, Room targetRoom)
        {
            var candidates = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol?.Id == sym.Id
                    && fi.Location is LocationPoint)
                .Where(fi =>
                {
                    try { return fi.Room != null && fi.Room.Id != targetRoom.Id; }
                    catch { return false; }
                })
                .ToList();
            if (candidates.Count == 0) return null;
            string prefix = (targetRoom.Name ?? "").Split(' ').FirstOrDefault() ?? "";
            return candidates.FirstOrDefault(fi =>
                       fi.Room.Name != null
                       && fi.Room.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                   ?? candidates[0];
        }

        // Read the example's placement RELATIVE to its room wall, transplant
        // onto the target wall segment: same distance-from-wall, same offset
        // along the wall measured from the NEAREST corner (handles mirrored
        // rooms), same facing angle relative to the wall's inward normal.
        public static (XYZ point, XYZ facing) TransplantPlacement(
            FamilyInstance example, Room exampleRoom, WallSeg targetSeg)
        {
            var lp = ((LocationPoint)example.Location).Point;
            var exSegs = WallSegments(exampleRoom);
            var exSeg = exSegs.OrderBy(s => DistPointToSegXY(lp, s.Start, s.End)).First();

            double dWall = DistPointToSegXY(lp, exSeg.Start, exSeg.End);
            var exDir = (exSeg.End - exSeg.Start).Normalize();
            double along = new XYZ(lp.X - exSeg.Start.X, lp.Y - exSeg.Start.Y, 0)
                .DotProduct(exDir);
            double fromNearestCorner = Math.Min(along, exSeg.LengthFt - along);
            // facing relative to the wall: signed angle inward-normal -> facing
            double relAngle = FacingAngle(example.FacingOrientation)
                - FacingAngle(exSeg.InwardNormal);

            var tDir = (targetSeg.End - targetSeg.Start).Normalize();
            double tAlong = Math.Min(
                Math.Max(fromNearestCorner, 0.5),
                Math.Max(targetSeg.LengthFt - 0.5, 0.5));
            var pt = targetSeg.Start + tDir * tAlong + targetSeg.InwardNormal * dWall;
            double fAngle = FacingAngle(targetSeg.InwardNormal) + relAngle;
            var facing = new XYZ(Math.Cos(fAngle), Math.Sin(fAngle), 0);
            return (pt, facing);
        }

        // Deterministic post-placement verify (Phase 6). Never throws.
        public static Dictionary<string, object?> Verify(Document doc,
            ElementId newId, Room expectedRoom)
        {
            try
            {
                var el = doc.GetElement(newId) as FamilyInstance;
                if (el == null)
                    return new Dictionary<string, object?>
                    { ["ok"] = false, ["reason"] = "element gone" };
                var lp = (el.Location as LocationPoint)?.Point;
                bool inRoom = false;
                double? distToWallMm = null;
                if (lp != null && expectedRoom != null)
                {
                    inRoom = expectedRoom.IsPointInRoom(new XYZ(lp.X, lp.Y, lp.Z + 1.0));
                    var segs = WallSegments(expectedRoom);
                    if (segs.Count > 0)
                        distToWallMm = Math.Round(segs.Min(s =>
                            DistPointToSegXY(lp, s.Start, s.End)) * 304.8, 0);
                }
                int clashes = 0;
                var bb = el.get_BoundingBox(null);
                if (bb != null)
                {
                    var outline = new Outline(bb.Min, bb.Max);
                    clashes = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType()
                        .WherePasses(new BoundingBoxIntersectsFilter(outline))
                        .OfType<FamilyInstance>()
                        .Count(x => x.Id != el.Id
                            && x.Category?.Id == el.Category?.Id);
                }
                return new Dictionary<string, object?>
                {
                    ["in_room"] = inRoom,
                    ["dist_to_wall_mm"] = distToWallMm,
                    ["clash_count"] = clashes,
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object?>
                { ["ok"] = false, ["reason"] = ex.Message };
            }
        }
    }
}
