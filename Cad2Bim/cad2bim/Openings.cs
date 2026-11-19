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

        // Mutable (not const): CadToBimViewModel sets these from CadToBimSettings.DoorMinRadiusMm/
        // DoorMaxRadiusMm before a detect pass, the same way it sets Wall.SMin/SMax. Defaults match
        // the settings' own defaults, so the standalone engine and CLI behave exactly as before.
        public static double SwingMinRadiusMm = 500.0;
        public static double SwingMaxRadiusMm = 1_500.0;

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

            // Windows drawn as lines across a gap in the wall: the wall stops, the window
            // lines carry on to the next piece. Those lines also pair into thin "frame walls",
            // which must neither host a door nor take the window marks.
            var gaps = FindWindowGaps(walls, swings, windowLines);
            var frames = new HashSet<Wall>(gaps.SelectMany(gap => gap.Frames));

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
                    if (frames.Contains(candidate)) continue;
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
                    if (frames.Contains(candidate)) continue;
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

            // A gap window sits past its host's end, so its jambs are not clamped to the host.
            foreach (var gap in gaps) {
                Point centre = PointAlong(gap.Host.Centerline, (gap.From + gap.To) / 2);
                openings.Add(Across(gap.Host, centre, gap.To - gap.From, null, clamp: false));
            }

            return Merge(openings);
        }

        /// <summary>How close a window line's start, or the next piece of wall, has to come to
        /// where a wall stops (or where the run of window lines stops) to carry on from it.</summary>
        public const double WindowGapAbutMm = 50.0;

        /// <summary>How far past the wall's face a window line may sit and still be in the
        /// wall: sills and frames are drawn a little proud of it.</summary>
        public const double WindowGapSideSlackMm = 50.0;

        /// <summary>Slack around a gap window for a wall made of its lines to count as a frame.</summary>
        public const double WindowFrameSlackMm = 25.0;

        /// <summary>How far off the wall's direction a window line may run.</summary>
        public const double WindowGapAngleDegrees = 5.0;

        /// <summary>Gap windows whose middles are this close are one window found twice.</summary>
        public const double WindowGapSameMm = 150.0;

        /// <summary>
        /// The thin walls the classifier paired out of window lines lying in a wall gap: a
        /// sill, glass or frame line matched with its neighbour, not fabric. Callers drop them
        /// from the wall list before anything else uses it; left in, they take the window
        /// marks and door swings meant for the real wall, and stop that wall being stretched
        /// across the window to meet the next piece.
        /// </summary>
        public static List<Wall> WindowFrameWalls(
                IReadOnlyList<Wall> walls, IReadOnlyList<Arc> swings, IReadOnlyList<Segment> windowLines) =>
            FindWindowGaps(walls, swings, windowLines)
                .SelectMany(gap => gap.Frames)
                .Distinct()
                .ToList();

        private sealed class WindowGapCandidate {
            public readonly Wall Host;
            public readonly int HostIndex;
            public readonly double From;
            public readonly double To;
            public readonly bool TwoSided;
            public readonly Point Middle;
            public readonly List<Wall> Frames;

            public WindowGapCandidate(Wall host, int hostIndex, double from, double to, bool twoSided, List<Wall> frames) {
                Host = host;
                HostIndex = hostIndex;
                From = from;
                To = to;
                TwoSided = twoSided;
                Frames = frames;
                Middle = PointAlong(host.Centerline, (from + to) / 2);
            }
        }

        /// <summary>
        /// Windows drawn as a run of lines in a gap where the wall stops. The sill, glass and
        /// frame lines run on from the wall's end, parallel to it, to the next piece of the
        /// same wall - or, with two or more lines and no next piece, simply for a window's
        /// width. From and To are along the host's centreline and lie past one of its ends,
        /// so they are below zero or above its length. Frames are the walls paired out of
        /// the window lines in the gap.
        /// </summary>
        private static List<(Wall Host, double From, double To, List<Wall> Frames)> FindWindowGaps(
                IReadOnlyList<Wall> walls, IReadOnlyList<Arc> swings, IReadOnlyList<Segment> windowLines) {

            var found = new List<(Wall Host, double From, double To, List<Wall> Frames)>();
            if (walls.Count == 0 || windowLines.Count == 0) return found;

            List<Segment> lines = windowLines.Where(line => line.Length > 0).ToList();
            if (lines.Count == 0) return found;

            var lineIndex = new SegmentIndex(lines, 2000);
            var wallIndex = new SegmentIndex(walls.Select(w => w.Centerline).ToList(), 2000);
            double reachOut = OpeningMaxWidthMm + WindowGapAbutMm;

            var candidates = new List<WindowGapCandidate>();

            for (int wi = 0; wi < walls.Count; wi++) {
                Wall wall = walls[wi];
                Segment line = wall.Centerline;
                double length = line.Length;
                if (length <= 0) continue;

                double side = (wall.Thickness / 2.0) + WindowGapSideSlackMm;

                for (int endIndex = 0; endIndex < 2; endIndex++) {
                    bool atP2 = endIndex == 1;

                    // Distance outward from this end of the wall; negative is back along it.
                    double Out(Point p, out double offset) {
                        double at = DistanceAlong(line, p, out offset);
                        return atP2 ? at - length : -at;
                    }

                    Point stop = atP2 ? line.P2 : line.P1;
                    var probe = new Segment(stop, PointAlong(line, atP2 ? length + reachOut : -reachOut));

                    var spans = new List<(double A, double B)>();
                    foreach (int k in lineIndex.Near(probe, side)) {
                        Segment candidate = lines[k];
                        if (!candidate.isParallelTo(line, WindowGapAngleDegrees)) continue;

                        double t1 = Out(candidate.P1, out double off1);
                        double t2 = Out(candidate.P2, out double off2);
                        if (off1 > side || off2 > side) continue;

                        spans.Add((Math.Min(t1, t2), Math.Max(t1, t2)));
                    }
                    if (spans.Count == 0) continue;

                    spans.Sort((a, b) => a.A.CompareTo(b.A));

                    // The run starts at the wall's end and carries on while each line picks up
                    // within WindowGapAbutMm of where the run so far stops.
                    double e = double.NaN;
                    int count = 0;
                    foreach (var span in spans) {
                        if (double.IsNaN(e)) {
                            if (span.A >= -WindowGapAbutMm && span.A <= WindowGapAbutMm && span.B > WindowGapAbutMm) {
                                e = span.B;
                                count = 1;
                            }
                            continue;
                        }
                        if (span.A > e + WindowGapAbutMm) break;
                        e = Math.Max(e, span.B);
                        count++;
                    }
                    if (double.IsNaN(e)) continue;

                    // Other walls on this line: the next piece, real wall in the way, and the
                    // frame walls made of the window lines themselves.
                    var others = new List<(Wall Wall, double Near, double Far, bool InRun, bool Tight)>();
                    foreach (int j in wallIndex.Near(probe, side + wall.Thickness)) {
                        if (j == wi) continue;
                        Wall other = walls[j];
                        Segment otherLine = other.Centerline;
                        if (otherLine.Length <= 0) continue;
                        if (!otherLine.isParallelTo(line, WindowGapAngleDegrees)) continue;

                        double t1 = Out(otherLine.P1, out double off1);
                        double t2 = Out(otherLine.P2, out double off2);
                        double off = Math.Max(off1, off2);

                        // InRun is BuildSizing.CoverOpenings' "next piece of the same run".
                        bool inRun = off <= Math.Max(wall.Thickness, other.Thickness) / 2.0;
                        bool tight = off <= (wall.Thickness / 2.0) + WindowFrameSlackMm;
                        if (!inRun && !tight) continue;

                        others.Add((other, Math.Min(t1, t2), Math.Max(t1, t2), inRun, tight));
                    }

                    // The next piece: starts where the run stops and goes on past it.
                    Wall? neighbour = null;
                    double neighbourNear = 0, bestMiss = double.MaxValue;
                    foreach (var other in others) {
                        if (!other.InRun) continue;
                        double miss = Math.Abs(other.Near - e);
                        if (miss > WindowGapAbutMm || other.Far <= e) continue;
                        if (miss >= bestMiss) continue;
                        neighbour = other.Wall;
                        neighbourNear = other.Near;
                        bestMiss = miss;
                    }
                    if (neighbour is not null) e = neighbourNear;

                    // Real wall standing in the gap: these lines are not a window across it.
                    bool blocked = others.Any(other =>
                        other.InRun && !ReferenceEquals(other.Wall, neighbour) &&
                        other.Far > WindowGapAbutMm && other.Near < e - WindowGapAbutMm &&
                        !(other.Near >= -WindowFrameSlackMm && other.Far <= e + WindowFrameSlackMm));
                    if (blocked) continue;

                    if (e < WindowMinWidthMm || e > OpeningMaxWidthMm) continue;
                    if (neighbour is null && count < 2) continue;

                    double gapEnd = e;
                    List<Wall> frames = others
                        .Where(other => other.Tight &&
                                        other.Near >= -WindowFrameSlackMm && other.Far <= gapEnd + WindowFrameSlackMm)
                        .Select(other => other.Wall)
                        .ToList();

                    candidates.Add(new WindowGapCandidate(
                        wall, wi,
                        atP2 ? length : -e,
                        atP2 ? length + e : 0,
                        neighbour is not null,
                        frames));
                }
            }

            // A frame wall can itself look like a wall stopping at a window; it hosts nothing.
            var allFrames = new HashSet<Wall>(candidates.SelectMany(c => c.Frames));
            candidates.RemoveAll(c => allFrames.Contains(c.Host));

            // The same window is found from both pieces it sits between: keep one, on the
            // longer piece, the same way every time.
            var kept = new List<WindowGapCandidate>();
            foreach (WindowGapCandidate candidate in candidates
                         .OrderByDescending(c => c.TwoSided)
                         .ThenByDescending(c => c.Host.Centerline.Length)
                         .ThenBy(c => c.HostIndex)) {
                if (kept.Any(k => Segment.PointDistance(k.Middle, candidate.Middle) <= WindowGapSameMm)) continue;
                kept.Add(candidate);
            }

            // A door hinged in the gap makes it a doorway; the lines there are its leaf and frame.
            List<Arc> doors = swings.Where(IsDoorSwing).ToList();
            kept.RemoveAll(gap => doors.Any(arc => DoorInGap(gap, arc)));

            foreach (WindowGapCandidate gap in kept) found.Add((gap.Host, gap.From, gap.To, gap.Frames));
            return found;
        }

        private static bool DoorInGap(WindowGapCandidate gap, Arc swing) {
            Segment line = gap.Host.Centerline;
            double along = DistanceAlong(line, swing.Center, out _);
            if (along < gap.From - swing.Radius || along > gap.To + swing.Radius) return false;

            double clamped = Math.Min(Math.Max(along, gap.From), gap.To);
            double distance = Segment.PointDistance(swing.Center, PointAlong(line, clamped));
            return distance <= Math.Max(gap.Host.Thickness, DoorHostReachMm);
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
        private static Opening Across(Wall wall, Point centre, double width, Arc? swing, bool clamp = true) {
            Segment line = wall.Centerline;
            double dx = (line.P2.x - line.P1.x) / line.Length;
            double dy = (line.P2.y - line.P1.y) / line.Length;

            double along = DistanceAlong(line, centre, out _);
            double half = width / 2.0;
            double reach = Math.Max(wall.Thickness, 100) / 2.0;

            Segment Jamb(double at) {
                Point on = PointAlong(line, clamp ? Math.Min(Math.Max(at, 0), line.Length) : at);
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
            double angle = Math.Acos(Math.Min(Math.Max(cosine, 0.0), 1.0)) * 180.0 / Math.PI;

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
