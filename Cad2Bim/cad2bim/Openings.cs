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

        /// <summary>
        /// Finds doors from their swings, and windows from the linework on the window layer.
        ///
        /// The jamb-pair method reads openings out of the wall itself, which is right when the
        /// wall is drawn with them and useless when it is not: on a real plan the door lives on
        /// a door layer as a swing arc and a leaf, and the wall simply stops. Nothing about the
        /// wall says a door is there. The drawing says it, on a layer named for it.
        ///
        /// So a swing is matched to the wall it opens through, and its radius is the leaf width,
        /// which is the clear opening. Window linework crossing a wall gives its extent the same
        /// way. Both are the drawing's own statement rather than an inference from a gap.
        /// </summary>
        public static List<Opening> ClassifyOpeningsFromSymbols(
                List<Wall> walls, IReadOnlyList<Arc> swings, IReadOnlyList<Segment> windowLines) {

            var openings = new List<Opening>();
            if (walls.Count == 0) return openings;

            var centerlines = walls.Select(w => w.Centerline).ToList();
            var index = new SegmentIndex(centerlines, 2000);

            foreach (Arc arc in swings) {
                if (!IsDoorSwing(arc)) continue;

                // The swing hangs off the wall it opens through: its centre sits on the jamb,
                // so the wall it belongs to is the one that centre is nearest to.
                var probe = new Segment(arc.Center, arc.Center);
                Wall? host = null;
                double bestOffset = double.MaxValue;

                foreach (int i in index.Near(probe, arc.Radius * 1.5)) {
                    Wall candidate = walls[i];
                    double along = DistanceAlong(candidate.Centerline, arc.Center, out double offset);

                    if (along < -arc.Radius || along > candidate.Centerline.Length + arc.Radius) continue;
                    if (offset > Math.Max(candidate.Thickness * 2.0, DoorHostReachMm)) continue;
                    if (offset >= bestOffset) continue;

                    host = candidate;
                    bestOffset = offset;
                }

                if (host is null) continue;

                openings.Add(Across(host, arc.Center, arc.Radius, arc));
            }

            // Window linework, grouped by the wall it crosses and then by where along it.
            var byWall = new Dictionary<Wall, List<double>>();

            foreach (Segment line in windowLines) {
                Point middle = Segment.Midpoint(line);
                var probe = new Segment(middle, middle);

                foreach (int i in index.Near(probe, 500)) {
                    Wall candidate = walls[i];
                    double along = DistanceAlong(candidate.Centerline, middle, out double offset);

                    if (offset > Math.Max(candidate.Thickness, 150)) continue;
                    if (along < 0 || along > candidate.Centerline.Length) continue;

                    if (!byWall.TryGetValue(candidate, out List<double>? positions)) {
                        positions = new List<double>();
                        byWall[candidate] = positions;
                    }
                    positions.Add(along);
                    break;
                }
            }

            foreach (var (wall, positions) in byWall) {
                positions.Sort();

                // One window is a run of marks along the wall; a gap between them is two windows.
                double start = positions[0];
                double end = positions[0];

                for (int i = 1; i <= positions.Count; i++) {
                    bool split = i == positions.Count || positions[i] - end > WindowSplitGapMm;

                    if (split) {
                        double width = end - start;
                        if (width >= WindowMinWidthMm && width <= OpeningMaxWidthMm) {
                            openings.Add(Across(wall, PointAlong(wall.Centerline, (start + end) / 2), width, null));
                        }

                        if (i == positions.Count) break;
                        start = positions[i];
                    }

                    end = positions[i];
                }
            }

            return Merge(openings);
        }

        /// <summary>How far a swing's hinge may sit from the wall centreline and still be
        /// taken as opening through it. A door is hinged at the jamb, which is at the wall
        /// face, and drawings put that anywhere within a wall's width of the line.</summary>
        public const double DoorHostReachMm = 400.0;

        /// <summary>
        /// Collapses openings that describe the same hole.
        ///
        /// Window linework is not one line per window: there is a frame, a sill, a pair of
        /// leaves, sometimes a fixed light beside an opening one. Each of those marks the wall
        /// at nearly the same place, and read separately they become separate windows - 181 of
        /// them on a house with perhaps thirty. Openings on one wall whose spans overlap are
        /// one opening, and a door wins over a plain opening at the same place, because a swing
        /// is a positive statement and a gap is not.
        /// </summary>
        private static List<Opening> Merge(List<Opening> openings) {
            var kept = new List<Opening>();

            foreach (IGrouping<Wall, Opening> onWall in openings.GroupBy(o => o.Wall)) {
                Segment line = onWall.Key.Centerline;

                var spans = onWall
                    .Select(o => {
                        double at = DistanceAlong(line, o.Position, out _);
                        return (Start: at - (o.Width / 2), End: at + (o.Width / 2), Opening: o);
                    })
                    .OrderBy(span => span.Start)
                    .ToList();

                var current = spans[0];

                for (int i = 1; i <= spans.Count; i++) {
                    bool separate = i == spans.Count || spans[i].Start > current.End;

                    if (separate) {
                        kept.Add(current.Opening);
                        if (i == spans.Count) break;
                        current = spans[i];
                        continue;
                    }

                    // Overlapping: keep the door if either is one, else the wider.
                    bool takeNew = spans[i].Opening.IsDoor && !current.Opening.IsDoor;
                    if (!current.Opening.IsDoor && !spans[i].Opening.IsDoor &&
                        spans[i].Opening.Width > current.Opening.Width) {
                        takeNew = true;
                    }

                    current = (Math.Min(current.Start, spans[i].Start),
                               Math.Max(current.End, spans[i].End),
                               takeNew ? spans[i].Opening : current.Opening);
                }
            }

            return kept;
        }

        /// <summary>Two jambs across the wall, the width apart, centred where the symbol sat.
        /// The Opening model is built from its jambs, and a symbol-found opening has none of
        /// its own - so they are placed where the wall says they must be.</summary>
        private static Opening Across(Wall wall, Point centre, double width, Arc? swing) {
            Segment line = wall.Centerline;
            double dx = (line.P2.x - line.P1.x) / line.Length;
            double dy = (line.P2.y - line.P1.y) / line.Length;

            double along = DistanceAlong(line, centre, out _);
            double half = width / 2.0;
            double reach = Math.Max(wall.Thickness, 100) / 2.0;

            Segment Jamb(double at) {
                Point on = PointAlong(line, Math.Clamp(at, 0, line.Length));
                return new Segment(
                    new Point(on.x - (-dy * reach), on.y - (dx * reach)),
                    new Point(on.x + (-dy * reach), on.y + (dx * reach)));
            }

            return new Opening(Jamb(along - half), Jamb(along + half), wall, swing);
        }

        /// <summary>Window marks further apart than this along one wall are separate windows.</summary>
        public const double WindowSplitGapMm = 1200.0;

        /// <summary>Narrowest window worth reporting. Below this the marks are a frame detail
        /// rather than an opening, and a house comes back with more windows than rooms.</summary>
        public const double WindowMinWidthMm = 600.0;

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
