using System.Runtime.Intrinsics.Arm;
using ACadSharp;
using ACadSharp.IO;

namespace Cad2Bim {
    public record Point(double x, double y);
    
    // Primitives
    public abstract class GeometryElement { public List<Point> Points { get; protected set; } = new(); }
    public class TextElement {
        public Point P1 { get; init; } // top-left
        public Point P2 { get; init; } // bottom-right
        public String Text { get; init; } = string.Empty;
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
        public Point Center { get; init; }
        public double Radius { get; init; }
    }

    // Building Elements
    public class Wall : BuildingElement {
        public static double SMin = 0.05;
        public static double SMax = 0.40;

        public double Thickness { get; }
        public bool IsOutdoor { get; set; }

        public Wall(Segment e1, Segment e2) {
            double d = Segment.Distance(e1, e2);

            if (!e1.isParallelTo(e2)) throw new ArgumentException("Wall segments must be parallel.");
            if (d < SMin || d > SMax) throw new ArgumentException("Wall thickness out of bounds.");

            Geometry = new List<GeometryElement> {e1, e2};
            Thickness = d;
        }
    }

    public class Opening : BuildingElement {
        public Wall Wall { get; }
        public bool IsDoor { get; }

        public Opening(Segment e1, Segment e2, Wall wall, Arc? arc = null) {
            if (arc is not null) Geometry.Add(arc);

            Wall = wall;
            IsDoor = arc is not null;
        }        
    }

    public class Space : BuildingElement {
        public const double AMin = 1.0; // predefined minimum area
        public double Area { get; set; }

        public Space(TextElement text, List<Wall> walls, List<Opening> openinngs) {
            Text = new List<TextElement> { text };
            SubElements = walls.Cast<BuildingElement>().Concat(openinngs).ToList();
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
                            Radius = arc.Radius
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

    public class CadClassifier {

        // Bw = {e1, e2 | both segments, e1 != e2, parallel, SMin <= d(e1, e2) <= SMax}.
        // Two additions on top of the definition, both about which partner a face is paired with
        // rather than which pairs are admissible: candidates must overlap when projected onto the
        // shared direction, and the closest admissible candidate wins instead of the first one
        // scanned. Pairing is still exclusive - a face belongs to at most one wall.
        public static List<Wall> ClassifyWalls(List<Segment> Segments) {

            List<Wall> walls = new List<Wall>();
            HashSet<Segment> used = new HashSet<Segment>();

            for (int i=0; i < Segments.Count; i++) {
                Segment s1 = Segments[i];
                if(used.Contains(s1)) continue;

                Segment? nearest = null;
                double nearestDistance = double.MaxValue;

                for (int j=i+1; j < Segments.Count; j++) {
                    Segment s2 = Segments[j];

                    if(used.Contains(s2)) continue;
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

        public static void ClassifyOpenings() { return; }
        public static void ClassifySpaces() { return; }

        public static void SplitWalls() { return; } // split walls into indoor and outdoor
        public static void CreateTopologicalPoint() { return; }
    }
}