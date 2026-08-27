using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Runtime.Intrinsics.Arm;
using ACadSharp;
using ACadSharp.IO;

namespace Cad2Bim {
    public record Point(double x, double y);
    
    // Primitives
    public abstract class GeometryElement {
        public List<Point> Points { get; protected set; } = new();

        /// <summary>The CAD layer this came off - the drawing's own statement of what the
        /// linework is for, and the cheapest filter there is.</summary>
        public string Layer { get; set; } = string.Empty;
    }

    public class TextElement {
        public Point P1 { get; init; } = new(0, 0); // top-left
        public Point P2 { get; init; } = new(0, 0); // bottom-right
        public String Text { get; init; } = string.Empty;
        public string Layer { get; init; } = string.Empty;
    }

    public abstract class BuildingElement {
        public List<GeometryElement> Geometry { get; protected set; } = new();
        public List<TextElement> Text { get; protected set; } = new();
        public List<BuildingElement> SubElements { get; protected set; } = new();
    }

    // Lines and arcs
    public class Segment : GeometryElement {
        public Segment(Point p1, Point p2) => Points = new List<Point> { p1, p2 };
        public Point P1 => Points[0];
        public Point P2 => Points[1];

        private (double dx, double dy) Direction() {
            double dx = P2.x - P1.x;
            double dy = P2.y - P1.y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            return (dx / len, dy / len);
        }

        public bool isParallelTo(Segment other, double angleToleranceDegrees = 2.0) {
            var (dx1, dy1) = Direction();
            var (dx2, dy2) = other.Direction();

            double cross = Math.Abs(dx1 * dy2 - dy1 * dx2);
            double angleRad = Math.Asin(Math.Clamp(cross, -1.0, 1.0));
            double angleDeg = angleRad * (180.0 / Math.PI);

            return angleDeg <= angleToleranceDegrees;
        }

        private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.x - b.x, 2) + Math.Pow(a.y - b.y, 2));

        public static double PointDistance(Point a, Point b) => Distance(a, b);

        public double Length => Distance(P1, P2);

        public static Point Midpoint(Segment s) => new((s.P1.x + s.P2.x) / 2, (s.P1.y + s.P2.y) / 2);

        /// <summary>
        /// The line running between two parallel faces, clipped to the stretch where they
        /// actually overlap. Taking the plain midpoint of the four endpoints instead would
        /// stretch a wall past its real extent whenever one face runs longer than the other,
        /// which is the normal case at a junction.
        /// </summary>
        public static Segment Midline(Segment a, Segment b) {
            double dx = a.P2.x - a.P1.x;
            double dy = a.P2.y - a.P1.y;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            if (len == 0) return new Segment(a.P1, a.P2);

            dx /= len;
            dy /= len;

            double Project(Point p) => ((p.x - a.P1.x) * dx) + ((p.y - a.P1.y) * dy);
            Point At(double t) => new(a.P1.x + (dx * t), a.P1.y + (dy * t));

            double b1 = Project(b.P1);
            double b2 = Project(b.P2);
            double start = Math.Max(0, Math.Min(b1, b2));
            double end = Math.Min(len, Math.Max(b1, b2));

            // Offset the overlap span half-way towards the other face.
            Point onA1 = At(start);
            Point onA2 = At(end);
            Point mid = Midpoint(b);
            double offsetX = 0, offsetY = 0;

            {
                // Perpendicular from a's line towards b, half the separation.
                double side = ((mid.x - a.P1.x) * -dy) + ((mid.y - a.P1.y) * dx);
                double half = side / 2.0;
                offsetX = -dy * half;
                offsetY = dx * half;
            }

            return new Segment(
                new Point(onA1.x + offsetX, onA1.y + offsetY),
                new Point(onA2.x + offsetX, onA2.y + offsetY));
        }

        public static double Distance(Segment a, Segment b) {
            Point mid = new((a.P1.x + a.P2.x) / 2, (a.P1.y + a.P2.y) / 2);
            return DistancePointToLine(mid, b.P1, b.P2);
        }

        public static bool Overlaps(Segment a, Segment b) {
            double dx = a.P2.x - a.P1.x;
            double dy = a.P2.y - a.P1.y;
            double len = Math.Sqrt(dx * dx + dy * dy);

            if (len == 0) return false; // degenerate segment has no direction to project onto

            dx /= len;
            dy /= len;

            double Project(Point p) => (p.x - a.P1.x) * dx + (p.y - a.P1.y) * dy;

            double b1 = Project(b.P1);
            double b2 = Project(b.P2);

            // a spans [0, len] by construction; overlap is the intersection of the two spans.
            double start = Math.Max(0, Math.Min(b1, b2));
            double end = Math.Min(len, Math.Max(b1, b2));

            return end - start > 0;
        }

        private static double DistancePointToLine(Point p, Point linePt1, Point linePt2) {
            double dx = linePt2.x - linePt1.x;
            double dy = linePt2.y - linePt1.y;
            double lineLen = Math.Sqrt(dx * dx + dy * dy);

            if (lineLen == 0) return Distance(p, linePt1); // degenerate line

            // |cross product| / |line vector| = perpendicular distance
            double cross = Math.Abs(dx * (linePt1.y - p.y) - (linePt1.x - p.x) * dy);
            return cross / lineLen;
        }

    }

    public class Arc : GeometryElement {
        public Point Center { get; init; } = new(0, 0);
        public double Radius { get; init; }

        // Radians, CCW from +X, as DWG stores them. A door swing is recognised by its sweep,
        // so an arc carrying only centre and radius cannot be told apart from a full circle.
        public double StartAngle { get; init; }
        public double EndAngle { get; init; }

        public double SweepDegrees {
            get {
                double sweep = (EndAngle - StartAngle) * 180.0 / Math.PI;
                while (sweep < 0) sweep += 360.0;
                while (sweep >= 360.0) sweep -= 360.0;
                return sweep;
            }
        }

        public Point PointAt(double angle) =>
            new(Center.x + (Radius * Math.Cos(angle)), Center.y + (Radius * Math.Sin(angle)));

        public Point StartPoint => PointAt(StartAngle);
        public Point EndPoint => PointAt(EndAngle);
    }

    // Building Elements
    public class Wall : BuildingElement {
        // Millimetres. Drawings are normalised to mm on load (see Units), so one threshold
        // pair is correct for every file whatever units it was authored in.
        public static double SMin = Units.DefaultMinWallThicknessMm;
        public static double SMax = Units.DefaultMaxWallThicknessMm;

        /// <summary>Shortest run of line that can be a wall face, in millimetres.
        ///
        /// Reading the whole drawing means reading its curves too, and a curve arrives as a
        /// run of short chords that are near enough parallel and near enough apart to pass
        /// every other test — a tessellated circle pairs with itself into dozens of "walls".
        /// A wall face is a long straight run; nothing else here is.</summary>
        public static double MinFaceLength = 300.0;

        public double Thickness { get; }
        public bool IsOutdoor { get; set; }

        /// <summary>The wall's own line: midway between its two faces, spanning the stretch
        /// where they actually run alongside each other. Everything downstream — junctions,
        /// openings, room boundaries — works on this rather than on the face pair.</summary>
        public Segment Centerline { get; }

        public Wall(Segment e1, Segment e2) {
            double d = Segment.Distance(e1, e2);

            if (!e1.isParallelTo(e2)) throw new ArgumentException("Wall segments must be parallel.");
            if (d < SMin || d > SMax) throw new ArgumentException("Wall thickness out of bounds.");

            Geometry = new List<GeometryElement> {e1, e2};
            Thickness = d;
            Centerline = Segment.Midline(e1, e2);
        }
    }

    public class Opening : BuildingElement {
        public Wall Wall { get; }
        public bool IsDoor { get; }

        /// <summary>Where the opening sits on the wall's centreline.</summary>
        public Point Position { get; }

        /// <summary>Clear width between the two jambs.</summary>
        public double Width { get; }

        public Opening(Segment e1, Segment e2, Wall wall, Arc? arc = null) {
            Geometry.Add(e1);
            Geometry.Add(e2);
            if (arc is not null) Geometry.Add(arc);

            Wall = wall;
            IsDoor = arc is not null;

            Point m1 = Segment.Midpoint(e1);
            Point m2 = Segment.Midpoint(e2);
            Position = new((m1.x + m2.x) / 2, (m1.y + m2.y) / 2);
            Width = Segment.PointDistance(m1, m2);
        }
    }

    public class Space : BuildingElement {
        public const double AMin = 1.0; // predefined minimum area

        public double Area { get; set; }

        /// <summary>The closed loop of wall centrelines enclosing this space, in order.</summary>
        public List<Point> Boundary { get; } = new();

        /// <summary>Room name taken from the drawing's own text, when a label falls inside.</summary>
        public string? Name => Text.Count > 0 ? Text[0].Text : null;

        public Space(TextElement text, List<Wall> walls, List<Opening> openinngs) {
            Text = new List<TextElement> { text };
            SubElements = walls.Cast<BuildingElement>().Concat(openinngs).ToList();
        }

        /// <summary>A space found geometrically, before any label is matched to it.</summary>
        public Space(List<Point> boundary, List<Wall> walls) {
            Boundary = boundary;
            Area = PolygonArea(boundary);
            SubElements = walls.Cast<BuildingElement>().ToList();
        }

        /// <summary>Shoelace formula; absolute, so winding order does not matter here.</summary>
        public static double PolygonArea(IReadOnlyList<Point> polygon) {
            if (polygon.Count < 3) return 0;

            double twiceArea = 0;
            for (int i = 0; i < polygon.Count; i++) {
                Point a = polygon[i];
                Point b = polygon[(i + 1) % polygon.Count];
                twiceArea += (a.x * b.y) - (b.x * a.y);
            }

            return Math.Abs(twiceArea) / 2.0;
        }
    }

    public class CadLoader {
        public static (List<GeometryElement> AllGeometry, List<TextElement> AllText) LoadCadEntities(string filePath) {
            CadDocument cadDocument = filePath.EndsWith(".dxf")
                ? new DxfReader(filePath).Read()
                : new DwgReader(filePath).Read();

            return LoadCadEntities(cadDocument);
        }

        // Overload for callers that already hold the document (the viewport reads it too), so a
        // file is parsed once per load.
        public static (List<GeometryElement> AllGeometry, List<TextElement> AllText) LoadCadEntities(CadDocument cadDocument) {
            List<GeometryElement> geometries = new();
            List<TextElement> texts = new();

            foreach (var entity in cadDocument.Entities) {
                switch (entity) {
                    case ACadSharp.Entities.Line line:
                        geometries.Add(new Segment(
                            new Point(line.StartPoint.X, line.StartPoint.Y),
                            new Point(line.EndPoint.X, line.EndPoint.Y)));
                        break;

                    case ACadSharp.Entities.Arc arc:
                        geometries.Add(new Arc {
                            Center = new Point(arc.Center.X, arc.Center.Y),
                            Radius = arc.Radius,
                            StartAngle = arc.StartAngle,
                            EndAngle = arc.EndAngle
                        });
                        break;

                    case ACadSharp.Entities.TextEntity text:
                        texts.Add(new TextElement {
                            P1 = new Point(text.InsertPoint.X, text.InsertPoint.Y),
                            P2 = new Point(text.InsertPoint.X + text.Height * text.Value.Length * 0.6,
                                           text.InsertPoint.Y + text.Height), // rough bbox estimate
                            Text = text.Value
                        });
                        break;
                }
            }

            return (geometries, texts);
        }
    }

    public partial class CadClassifier {

        // Bw = {e1, e2 | both segments, e1 != e2, parallel, SMin <= d(e1, e2) <= SMax}.
        // Two additions on top of the definition, both about which partner a face is paired with
        // rather than which pairs are admissible: candidates must overlap when projected onto the
        // shared direction, and the closest admissible candidate wins instead of the first one
        // scanned. Pairing is still exclusive - a face belongs to at most one wall.
        public static List<Wall> ClassifyWalls(IReadOnlyList<Segment> Segments) {

            List<Wall> walls = new List<Wall>();
            HashSet<Segment> used = new HashSet<Segment>();

            // Only segments within SMax of this one can be its far face, and the grid answers
            // that without walking the whole drawing. Same pairs as a full scan, same order.
            var index = new SegmentIndex(Segments, Math.Max(Wall.SMax * 4, 1000));

            for (int i=0; i < Segments.Count; i++) {
                Segment s1 = Segments[i];
                if(used.Contains(s1)) continue;
                if(s1.Length < Wall.MinFaceLength) continue;

                Segment? nearest = null;
                double nearestDistance = double.MaxValue;

                foreach (int j in index.Near(s1, Wall.SMax)) {
                    if(j <= i) continue;
                    Segment s2 = Segments[j];

                    if(used.Contains(s2)) continue;
                    if(s2.Length < Wall.MinFaceLength) continue;
                    if(!s1.isParallelTo(s2)) continue;

                    double d = Segment.Distance(s1, s2);
                    if(d<Wall.SMin || d>Wall.SMax) continue;
                    if(!Segment.Overlaps(s1, s2)) continue;

                    if(d >= nearestDistance) continue;

                    nearest = s2;
                    nearestDistance = d;
                }

                if(nearest is null) continue;

                walls.Add(new Wall(s1, nearest));

                used.Add(s1);
                used.Add(nearest);
            }

            return walls;
        }

        // ClassifyOpenings  -> Openings.cs
        // ClassifySpaces    -> Spaces.cs
        // SplitWalls        -> Spaces.cs
        // CreateTopologicalPoints -> Topology.cs
    }
}