using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace Cad2Bim {
    public partial class CadClassifier {
        // A jamb is the short line closing the wall off at the side of an opening: it runs
        // across the wall, so it is perpendicular to the centreline and about as long as the
        // wall is thick.
        public const double JambLengthMinRatio = 0.6;
        public const double JambLengthMaxRatio = 1.6;
        public const double JambPerpendicularToleranceDegrees = 15.0;

        // Clear widths outside this range are not openings: below is a construction line,
        // above is usually a room-wide gap where the drafter simply stopped drawing.
        public const double OpeningMinWidthMm = 400.0;
        public const double OpeningMaxWidthMm = 3_000.0;

        // A door swing: roughly a quarter turn, radius equal to the leaf width.
        public const double SwingMinSweepDegrees = 60.0;
        public const double SwingMaxSweepDegrees = 120.0;
        public const double SwingMinRadiusMm = 500.0;
        public const double SwingMaxRadiusMm = 1_500.0;

        /// <summary>
        /// Openings are read from the jambs, not from the gap: a gap in the linework is
        /// indistinguishable from linework the drafter never drew, whereas a pair of jambs is
        /// a positive statement that something passes through the wall here. A door swing
        /// hinged at one of the jambs makes the opening a door; without one it is a window or
        /// a plain opening, which is as far as a floor plan alone can settle it.
        /// </summary>
        public static List<Opening> ClassifyOpenings(
                List<Wall> walls, IReadOnlyList<Segment> segments, IReadOnlyList<Arc> arcs) {

            var openings = new List<Opening>();
            List<Arc> swings = arcs.Where(IsDoorSwing).ToList();

            // A jamb sits on the wall, so only segments near the centreline can be one.
            var index = new SegmentIndex(segments, 1000);

            foreach (Wall wall in walls) {
                Segment centerline = wall.Centerline;
                double length = centerline.Length;
                if (length <= 0) continue;

                // Jamb candidates on this wall, keyed by where they sit along it.
                var jambs = new List<(double Distance, Segment Segment)>();

                foreach (int index2 in index.Near(centerline, wall.Thickness * 2)) {
                    Segment candidate = segments[index2];
                    if (wall.Geometry.Contains(candidate)) continue;

                    double jambLength = candidate.Length;
                    if (jambLength < wall.Thickness * JambLengthMinRatio) continue;
                    if (jambLength > wall.Thickness * JambLengthMaxRatio) continue;

                    if (!IsPerpendicular(centerline, candidate, JambPerpendicularToleranceDegrees)) continue;

                    Point midpoint = Segment.Midpoint(candidate);
                    double along = DistanceAlong(centerline, midpoint, out double offset);

                    // The jamb has to sit on the wall, not merely point across it.
                    if (offset > wall.Thickness * 0.75) continue;
                    if (along < 0 || along > length) continue;

                    jambs.Add((along, candidate));
                }

                if (jambs.Count < 2) continue;

                jambs.Sort((a, b) => a.Distance.CompareTo(b.Distance));

                // Consecutive jambs only: a wall with four jambs holds two openings, not six.
                for (int i = 0; i + 1 < jambs.Count; i++) {
                    double clearWidth = jambs[i + 1].Distance - jambs[i].Distance;
                    if (clearWidth < OpeningMinWidthMm || clearWidth > OpeningMaxWidthMm) continue;

                    Point from = PointAlong(centerline, jambs[i].Distance);
                    Point to = PointAlong(centerline, jambs[i + 1].Distance);
                    Arc? swing = FindSwing(swings, from, to, clearWidth);

                    openings.Add(new Opening(jambs[i].Segment, jambs[i + 1].Segment, wall, swing));
                    i++; // both jambs are spent
                }
            }

            return openings;
        }

        private static bool IsDoorSwing(Arc arc) {
            if (arc.Radius < SwingMinRadiusMm || arc.Radius > SwingMaxRadiusMm) return false;

            double sweep = arc.SweepDegrees;
            return sweep >= SwingMinSweepDegrees && sweep <= SwingMaxSweepDegrees;
        }

        /// <summary>
        /// A swing belongs to an opening when it is hinged at one of its jambs — the arc
        /// centre sits on a jamb and the leaf is about as wide as the clear opening.
        /// Proximity alone would claim the swing of the door in the next room.
        /// </summary>
        private static Arc? FindSwing(List<Arc> swings, Point from, Point to, double clearWidth) {
            Arc? best = null;
            double bestDistance = double.MaxValue;

            foreach (Arc swing in swings) {
                if (Math.Abs(swing.Radius - clearWidth) > clearWidth * 0.4) continue;

                double distance = Math.Min(
                    Segment.PointDistance(swing.Center, from),
                    Segment.PointDistance(swing.Center, to));

                if (distance > clearWidth * 0.5) continue;
                if (distance >= bestDistance) continue;

                best = swing;
                bestDistance = distance;
            }

            return best;
        }

        internal static bool IsPerpendicular(Segment a, Segment b, double toleranceDegrees) {
            double ax = a.P2.x - a.P1.x, ay = a.P2.y - a.P1.y;
            double bx = b.P2.x - b.P1.x, by = b.P2.y - b.P1.y;

            double lengthA = Math.Sqrt((ax * ax) + (ay * ay));
            double lengthB = Math.Sqrt((bx * bx) + (by * by));
            if (lengthA == 0 || lengthB == 0) return false;

            double cosine = Math.Abs(((ax * bx) + (ay * by)) / (lengthA * lengthB));
            double angle = Math.Acos(Math.Clamp(cosine, 0.0, 1.0)) * 180.0 / Math.PI;

            return Math.Abs(angle - 90.0) <= toleranceDegrees;
        }

        /// <summary>Distance of a point along a segment, plus how far off the line it sits.</summary>
        internal static double DistanceAlong(Segment segment, Point point, out double offset) {
            double dx = segment.P2.x - segment.P1.x;
            double dy = segment.P2.y - segment.P1.y;
            double length = Math.Sqrt((dx * dx) + (dy * dy));

            if (length == 0) {
                offset = Segment.PointDistance(segment.P1, point);
                return 0;
            }

            dx /= length;
            dy /= length;

            double along = ((point.x - segment.P1.x) * dx) + ((point.y - segment.P1.y) * dy);
            offset = Math.Abs(((point.x - segment.P1.x) * -dy) + ((point.y - segment.P1.y) * dx));
            return along;
        }
    }
}
