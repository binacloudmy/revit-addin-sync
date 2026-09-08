using System;
using System.Collections.Generic;
using System.Linq;

namespace Cad2Bim {
    /// <summary>One floor plan's worth of walls, and where it sits on the sheet.</summary>
    public sealed class PlanCluster {
        public List<Wall> Walls { get; } = new();

        public double MinX { get; set; } = double.MaxValue;
        public double MinY { get; set; } = double.MaxValue;
        public double MaxX { get; set; } = double.MinValue;
        public double MaxY { get; set; } = double.MinValue;

        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public double Area => Width * Height;

        /// <summary>Labels falling inside this plan - the drawing's own title for it is usually
        /// among them.</summary>
        public List<TextElement> Texts { get; } = new();

        public void Add(Wall wall) {
            Walls.Add(wall);

            foreach (Point point in new[] { wall.Centerline.P1, wall.Centerline.P2 }) {
                if (point.x < MinX) MinX = point.x;
                if (point.y < MinY) MinY = point.y;
                if (point.x > MaxX) MaxX = point.x;
                if (point.y > MaxY) MaxY = point.y;
            }
        }

        public bool Contains(Point point) =>
            point.x >= MinX && point.x <= MaxX && point.y >= MinY && point.y <= MaxY;
    }

    public partial class CadClassifier {
        /// <summary>Blank space between two floor plans on one sheet. Rooms sit metres apart;
        /// plans sit tens of metres apart.</summary>
        public const double PlanGapMm = 5000.0;

        /// <summary>
        /// Separates the floor plans a drawing holds side by side.
        ///
        /// A drawing sheet is not a building. This one carries the ground floor, two upper
        /// floors, a service level and a roof laid out next to each other in one model space,
        /// and read literally that becomes a single storey 350 by 280 metres across with every
        /// floor lying flat beside the others. That is not a 3D building, it is a carpet, and
        /// no amount of wall accuracy fixes it.
        ///
        /// The plans are found the way the eye finds them: by the blank space between them.
        /// Walls are grouped into bands where they run continuously, first down the sheet and
        /// then across it, and a gap wider than any room ends a band.
        /// </summary>
        public static List<PlanCluster> ClusterPlans(
                List<Wall> walls, IReadOnlyList<TextElement>? texts = null, double gapMm = PlanGapMm) {

            var clusters = new List<PlanCluster>();
            if (walls.Count == 0) return clusters;

            foreach (List<Wall> band in Band(walls, gapMm, vertical: true)) {
                foreach (List<Wall> column in Band(band, gapMm, vertical: false)) {
                    var cluster = new PlanCluster();
                    foreach (Wall wall in column) cluster.Add(wall);
                    clusters.Add(cluster);
                }
            }

            // Biggest first: the plans that matter lead, and stray fragments fall to the end.
            clusters = clusters.OrderByDescending(cluster => cluster.Area).ToList();

            if (texts is not null) {
                foreach (TextElement text in texts) {
                    PlanCluster? owner = clusters.FirstOrDefault(cluster => cluster.Contains(text.P1));
                    owner?.Texts.Add(text);
                }
            }

            return clusters;
        }

        /// <summary>Runs of walls with no gap wider than the tolerance between them along one
        /// axis. Measured on extents rather than centres: a long wall bridges a gap that its
        /// midpoint would fall outside.</summary>
        private static IEnumerable<List<Wall>> Band(List<Wall> walls, double gapMm, bool vertical) {
            double Low(Wall wall) => vertical
                ? Math.Min(wall.Centerline.P1.y, wall.Centerline.P2.y)
                : Math.Min(wall.Centerline.P1.x, wall.Centerline.P2.x);

            double High(Wall wall) => vertical
                ? Math.Max(wall.Centerline.P1.y, wall.Centerline.P2.y)
                : Math.Max(wall.Centerline.P1.x, wall.Centerline.P2.x);

            List<Wall> sorted = walls.OrderBy(Low).ToList();

            var current = new List<Wall> { sorted[0] };
            double reach = High(sorted[0]);

            for (int i = 1; i < sorted.Count; i++) {
                if (Low(sorted[i]) - reach > gapMm) {
                    yield return current;
                    current = new List<Wall>();
                }

                current.Add(sorted[i]);
                reach = Math.Max(reach, High(sorted[i]));
            }

            yield return current;
        }

        /// <summary>Ends this far apart along one line are the same face, drawn in pieces.</summary>
        public const double FaceJoinGapMm = 150.0;

        /// <summary>
        /// Joins face linework that lies along one line into whole faces, before any pairing.
        ///
        /// This is where most of a drawing was being lost. A wall face is not one line in a
        /// DWG: a polyline breaks at every vertex, a wall stops and restarts at each door, and
        /// hatch boundaries arrive as runs of short pieces. Measured, two thirds of the
        /// wall-layer linework was under 200 mm - 2,878 segments of 4,482 on one drawing, 3,101
        /// of 4,225 on another - and every one of them was thrown away for being too short to
        /// be a wall face, when together they were exactly that.
        ///
        /// Merging centrelines after pairing, which is what happened until now, cannot recover
        /// them: a face that never paired never became a centreline. The join has to happen
        /// first, on the raw linework.
        /// </summary>
        public static List<Segment> MergeCollinearSegments(
                IReadOnlyList<Segment> segments, double gapMm = FaceJoinGapMm) {

            var pieces = new List<(double Angle, double Offset, Segment Segment)>(segments.Count);

            foreach (Segment segment in segments) {
                double length = segment.Length;
                if (length <= 0) continue;

                double dx = (segment.P2.x - segment.P1.x) / length;
                double dy = (segment.P2.y - segment.P1.y) / length;
                if (dx < 0 || (dx == 0 && dy < 0)) { dx = -dx; dy = -dy; }

                pieces.Add((Math.Atan2(dy, dx), (-dy * segment.P1.x) + (dx * segment.P1.y), segment));
            }

            if (pieces.Count == 0) return new List<Segment>();

            var merged = new List<Segment>();

            foreach (var byAngle in Cluster(pieces.OrderBy(p => p.Angle).ToList(),
                                            p => p.Angle, FaceAngleTolerance)) {
                foreach (var online in Cluster(byAngle.OrderBy(p => p.Offset).ToList(),
                                               p => p.Offset, FaceOffsetToleranceMm)) {
                    JoinAlongLine(online, gapMm, merged);
                }
            }

            return merged;
        }

        /// <summary>Two degrees. A face drawn in pieces does not change direction between them,
        /// but the pieces carry rounding.</summary>
        private const double FaceAngleTolerance = 0.035;

        /// <summary>How far apart two pieces may sit across the line and still be one face.
        /// Tighter than the wall merge: these are the faces themselves, and 30 mm apart is two
        /// faces of a very thin wall, not one face drawn twice.</summary>
        private const double FaceOffsetToleranceMm = 25.0;

        private static void JoinAlongLine(
                List<(double Angle, double Offset, Segment Segment)> online,
                double gapMm, List<Segment> merged) {

            double angle = online[0].Angle;
            double dx = Math.Cos(angle);
            double dy = Math.Sin(angle);

            double Project(Point p) => (p.x * dx) + (p.y * dy);
            Point At(double t, double drop) => new((t * dx) - (drop * dy), (t * dy) + (drop * dx));

            var spans = online
                .Select(p => {
                    double a = Project(p.Segment.P1);
                    double b = Project(p.Segment.P2);
                    return (Start: Math.Min(a, b), End: Math.Max(a, b), p.Offset);
                })
                .OrderBy(span => span.Start)
                .ToList();

            double runStart = spans[0].Start;
            double runEnd = spans[0].End;
            double drop = spans[0].Offset;

            for (int i = 1; i <= spans.Count; i++) {
                bool separate = i == spans.Count || spans[i].Start - runEnd > gapMm;

                if (separate) {
                    merged.Add(new Segment(At(runStart, drop), At(runEnd, drop)));
                    if (i == spans.Count) break;

                    runStart = spans[i].Start;
                    runEnd = spans[i].End;
                    drop = spans[i].Offset;
                    continue;
                }

                runEnd = Math.Max(runEnd, spans[i].End);
            }
        }

        /// <summary>Two walls this close along one line are the same wall drawn twice.</summary>
        public const double DuplicateOffsetMm = 60.0;
        public const double DuplicateThicknessMm = 40.0;

        /// <summary>
        /// Drops walls that duplicate one already found.
        ///
        /// Letting a face serve two walls recovered the ones that were missing, and made it
        /// possible to build the same wall twice from two different pairings. In the model that
        /// reads as doubled linework in plan and two solids in one space in 3D, which is worse
        /// than either error alone - a quantity takeoff counts it twice.
        ///
        /// A duplicate runs along the same line, at the same thickness, over the same stretch.
        /// The longer one is kept: it is the one that found more of the wall.
        /// </summary>
        public static List<Wall> DeduplicateWalls(List<Wall> walls) {
            if (walls.Count < 2) return walls;

            var centerlines = walls.Select(wall => wall.Centerline).ToList();
            var index = new SegmentIndex(centerlines, 2000);
            var dropped = new bool[walls.Count];

            for (int i = 0; i < walls.Count; i++) {
                if (dropped[i]) continue;

                foreach (int j in index.Near(centerlines[i], DuplicateOffsetMm)) {
                    if (j == i || dropped[j]) continue;
                    if (!Duplicates(walls[i], walls[j])) continue;

                    // Keep the longer; on a tie keep the earlier, so the result does not depend
                    // on the order the grid happened to return them in.
                    bool keepFirst = centerlines[i].Length > centerlines[j].Length ||
                                     (centerlines[i].Length == centerlines[j].Length && i < j);

                    dropped[keepFirst ? j : i] = true;
                    if (!keepFirst) break;
                }
            }

            return walls.Where((_, i) => !dropped[i]).ToList();
        }

        private static bool Duplicates(Wall a, Wall b) {
            if (Math.Abs(a.Thickness - b.Thickness) > DuplicateThicknessMm) return false;
            if (!a.Centerline.isParallelTo(b.Centerline)) return false;
            if (Segment.Distance(a.Centerline, b.Centerline) > DuplicateOffsetMm) return false;

            return Segment.Overlaps(a.Centerline, b.Centerline);
        }
    }
}
