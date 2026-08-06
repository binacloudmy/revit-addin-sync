// Room boundary -> flattened loop polygons (+ merged wall runs), in mm.
//
// Split verbatim out of SocketCandidates.cs when lighting needed the SAME walk:
// sockets consume the wall RUNS, lighting consumes the loop POLYGONS (outer for
// containment, islands for exclusion). Two copies of a GetBoundarySegments walk
// would be two places for the outer-loop rule to drift.
//
// THIS FILE IS A ft->mm BOUNDARY: everything leaving it is mm.
//
// Not Revit-free, so it does NOT link into the Tests project — the pure half is
// SocketLayout (polygon math) and LightingLayout (grid math), both of which are
// linked and tested. See Tests/Tests.csproj for why that line matters.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class RoomBoundary
    {
        /// <summary>Room-side face rather than wall centreline: the reviewed
        /// point is then where the faceplate actually sits, and it is still
        /// within half a wall thickness of the centreline so hosting projects
        /// correctly. One-line flip if hosting proves flaky in UAT.</summary>
        private const SpatialElementBoundaryLocation BoundaryLoc =
            SpatialElementBoundaryLocation.Finish;

        /// <summary>Arc flattening chord, mm. Fixed length rather than
        /// Curve.Tessellate() — Tessellate's chord tolerance is a Revit
        /// heuristic, so candidate coordinates would shift between Revit
        /// versions and could not be pinned in a golden test.</summary>
        private const double ChordMm = 100.0;

        /// <summary>One room's boundary, flattened. Polygons carries EVERY loop
        /// (islands included, regardless of <c>includeIslands</c>) because the
        /// inside test needs them; Runs carries only the loops a caller asked
        /// to place against.</summary>
        internal sealed class Result
        {
            /// <summary>Every boundary loop as an mm polygon, no duplicate
            /// closing vertex.</summary>
            public List<List<Pt2>> Polygons = new();
            /// <summary>Index into Polygons of the loop with the largest |area|,
            /// or -1 when the room has no boundary. Revit does NOT guarantee the
            /// outer loop is index 0, and trusting that puts points on a column
            /// face.</summary>
            public int OuterIndex = -1;
            /// <summary>Merged wall runs, one per contiguous stretch of a single
            /// wall, with LoopPolygon already attached.</summary>
            public List<WallRun> Runs = new();

            public int LoopCount => Polygons.Count;
            public IReadOnlyList<Pt2> Outer =>
                OuterIndex >= 0 ? Polygons[OuterIndex] : Array.Empty<Pt2>();
        }

        /// <summary>Boundary loops to polygons + merged wall runs. Non-wall
        /// segments (room separation lines, columns) still contribute to the
        /// loop polygon — it has to stay closed for the inside test — but
        /// produce no runs and are reported in <paramref name="skippedSegments"/>.</summary>
        internal static Result Build(
            Document doc, Room room, bool includeIslands, List<object> skippedSegments)
        {
            var result = new Result();

            IList<IList<BoundarySegment>> loops;
            try
            {
                loops = room.GetBoundarySegments(
                    new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = BoundaryLoc });
            }
            catch { return result; }

            if (loops == null || loops.Count == 0) return result;

            for (int i = 0; i < loops.Count; i++) result.Polygons.Add(LoopPolygon(loops[i]));

            double bestArea = -1;
            for (int i = 0; i < result.Polygons.Count; i++)
            {
                double a = Math.Abs(SocketLayout.SignedArea(result.Polygons[i]));
                if (a > bestArea) { bestArea = a; result.OuterIndex = i; }
            }

            var skipCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var segments = new List<RawSegment>();
            var loopOf = new Dictionary<int, IReadOnlyList<Pt2>>();

            for (int li = 0; li < loops.Count; li++)
            {
                if (li != result.OuterIndex && !includeIslands) continue;
                loopOf[li] = result.Polygons[li];

                foreach (var seg in loops[li])
                {
                    Curve curve;
                    try { curve = seg.GetCurve(); } catch { curve = null!; }
                    if (curve == null) { Bump(skipCounts, "no_curve"); continue; }

                    bool linked = seg.LinkElementId != ElementId.InvalidElementId;
                    long? hostWallId = null;
                    string runKey;

                    if (linked)
                    {
                        // The wall lives in a Revit link — real geometry, but
                        // nothing local to host against. Still worth a
                        // candidate: an unhosted family can sit there, and
                        // dropping it silently would empty the result on
                        // exactly the coordination models this targets.
                        runKey = $"lw:{seg.LinkElementId.Value}:{seg.ElementId.Value}";
                        Bump(skipCounts, "linked_wall");
                    }
                    else
                    {
                        var el = seg.ElementId != ElementId.InvalidElementId
                            ? doc.GetElement(seg.ElementId) : null;
                        if (el is Wall)
                        {
                            hostWallId = el.Id.Value;
                            runKey = $"w:{hostWallId.Value}";
                        }
                        else
                        {
                            Bump(skipCounts, ClassifyNonWall(el));
                            continue;
                        }
                    }

                    segments.Add(new RawSegment
                    {
                        RunKey = runKey,
                        HostWallId = hostWallId,
                        LoopIndex = li,
                        Points = Flatten(curve),
                    });
                }
            }

            foreach (var kv in skipCounts)
                skippedSegments.Add(new Dictionary<string, object?>
                {
                    ["room_id"] = room.Id.Value,
                    ["reason"] = kv.Key,
                    ["count"] = kv.Value,
                });

            result.Runs = SocketLayout.MergeRuns(segments);
            foreach (var run in result.Runs)
                if (loopOf.TryGetValue(run.LoopIndex, out var poly)) run.LoopPolygon = poly;

            return result;
        }

        /// <summary>Finished floor level of a room, mm. Level elevation plus the
        /// room's own lower offset — a room raised off its level reports neither
        /// on its own.</summary>
        internal static double FloorZMm(Document doc, Room room)
        {
            double ft = 0.0;
            var level = room.Level ?? doc.GetElement(room.LevelId) as Level;
            if (level != null) ft += level.Elevation;
            var lower = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET);
            if (lower != null && lower.StorageType == StorageType.Double) ft += lower.AsDouble();
            return ft * MmPerFoot;
        }

        internal static Pt2 ToPt(XYZ p) => new Pt2(p.X * MmPerFoot, p.Y * MmPerFoot);

        private static string ClassifyNonWall(Element? el)
        {
            if (el == null) return "unknown_host";
            var bic = (BuiltInCategory)(el.Category?.Id.Value ?? 0);
            return bic switch
            {
                BuiltInCategory.OST_RoomSeparationLines => "room_separation_line",
                BuiltInCategory.OST_Columns => "column",
                BuiltInCategory.OST_StructuralColumns => "column",
                _ => "unknown_host",
            };
        }

        private static void Bump(Dictionary<string, int> counts, string key) =>
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

        private static List<Pt2> LoopPolygon(IList<BoundarySegment> loop)
        {
            var pts = new List<Pt2>();
            foreach (var seg in loop)
            {
                Curve c;
                try { c = seg.GetCurve(); } catch { continue; }
                if (c == null) continue;
                var flat = Flatten(c);
                // Drop the shared joint so the polygon has no duplicate vertices.
                int start = (pts.Count > 0 && flat.Count > 0 && SocketLayout.Near(pts[pts.Count - 1], flat[0])) ? 1 : 0;
                for (int i = start; i < flat.Count; i++) pts.Add(flat[i]);
            }
            if (pts.Count > 1 && SocketLayout.Near(pts[0], pts[pts.Count - 1])) pts.RemoveAt(pts.Count - 1);
            return pts;
        }

        /// <summary>Curve to an mm polyline. Lines stay two points; everything
        /// else is sampled at a fixed chord so the output is reproducible.</summary>
        private static List<Pt2> Flatten(Curve curve)
        {
            var pts = new List<Pt2>();
            if (curve is Line)
            {
                pts.Add(ToPt(curve.GetEndPoint(0)));
                pts.Add(ToPt(curve.GetEndPoint(1)));
                return pts;
            }

            double lenMm = curve.Length * MmPerFoot;
            int n = (int)Math.Ceiling(lenMm / ChordMm);
            if (n < 2) n = 2;
            for (int i = 0; i <= n; i++)
                pts.Add(ToPt(curve.Evaluate((double)i / n, true)));
            return pts;
        }
    }
}
