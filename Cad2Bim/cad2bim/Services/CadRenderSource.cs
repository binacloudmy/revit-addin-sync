using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using Cad2Bim.ViewModels.Shapes;

// Cad2Bim.Arc / Cad2Bim.Point (the classification model) shadow the ACadSharp entities of the
// same name inside this namespace, so the CAD ones are always spelled through these aliases.
using CadArc = ACadSharp.Entities.Arc;
using CadPoint = ACadSharp.Entities.Point;

namespace Cad2Bim.Services {
    /// <summary>
    /// Turns a CadDocument straight into render-ready polylines — the viewport's ground truth.
    /// Blocks are flattened into world coordinates, every curve is tessellated, nothing is
    /// classified or filtered. Deliberately independent of Geometry.cs/CadLoader, which models
    /// only what the classifier consumes.
    /// </summary>
    public static class CadRenderSource {
        // Chord resolution for tessellated curves: one vertex per ~3 degrees of sweep.
        private const double StepAngle = Math.PI / 60.0;
        private const int MinCurvePoints = 2;
        private const int MaxCurvePoints = 512;

        // Blocks nest; this only guards against a self-referencing definition.
        private const int MaxDepth = 16;

        public static CadDocument Read(string filePath) =>
            filePath.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)
                ? new DxfReader(filePath).Read()
                : new DwgReader(filePath).Read();

        /// <summary>Model-space entities, flattened to polylines. Text/annotation glyphs are skipped.</summary>
        public static List<object> Flatten(CadDocument document) {
            List<object> shapes = new();
            foreach (Entity entity in document.Entities) {
                Emit(entity, shapes, Xform.Identity, 0);
            }
            return shapes;
        }

        /// <summary>
        /// A 2D affine map, block space to world space: (x, y) -> (Ax + Cy + E, Bx + Dy + F).
        /// Block contents are mapped through this rather than through ACadSharp's
        /// Insert.Explode()/Entity.ApplyTransform(), which drops the translation on
        /// mirrored inserts (negative scale) for LwPolyline, Ellipse and nested Insert.
        /// </summary>
        private readonly record struct Xform(double A, double B, double C, double D, double E, double F) {
            public static readonly Xform Identity = new(1, 0, 0, 1, 0, 0);

            public (double X, double Y) Apply(double x, double y) =>
                ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

            // this ∘ inner: inner runs first, so inner maps into this one's input space.
            public Xform Compose(Xform inner) => new(
                (A * inner.A) + (C * inner.B),
                (B * inner.A) + (D * inner.B),
                (A * inner.C) + (C * inner.D),
                (B * inner.C) + (D * inner.D),
                (A * inner.E) + (C * inner.F) + E,
                (B * inner.E) + (D * inner.F) + F);
        }

        private static void Emit(Entity entity, List<object> shapes, Xform xform, int depth) {
            if (depth > MaxDepth || entity.IsInvisible) {
                return;
            }

            switch (entity) {
                case Line line:
                    Add(shapes, xform, false, (line.StartPoint.X, line.StartPoint.Y), (line.EndPoint.X, line.EndPoint.Y));
                    break;

                case LwPolyline lwPolyline:
                    EmitPolyline(shapes, xform, lwPolyline.IsClosed,
                        lwPolyline.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList());
                    break;

                case Polyline2D polyline2D:
                    EmitPolyline(shapes, xform, polyline2D.IsClosed,
                        polyline2D.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList());
                    break;

                case Polyline3D polyline3D:
                    Add(shapes, xform, polyline3D.IsClosed,
                        polyline3D.Vertices.Select(v => (v.Location.X, v.Location.Y)).ToList());
                    break;

                // Arc derives from Circle, so it has to be matched first.
                case CadArc arc:
                    Add(shapes, xform, false, Flat(arc.PolygonalVertexes(CurvePoints(arc.Sweep))));
                    break;

                case Circle circle:
                    Add(shapes, xform, true, Flat(circle.PolygonalVertexes(CurvePoints(2 * Math.PI))));
                    break;

                case Ellipse ellipse:
                    Add(shapes, xform, ellipse.IsFullEllipse,
                        Flat(ellipse.PolygonalVertexes(
                            CurvePoints(ellipse.EndParameter - ellipse.StartParameter))));
                    break;

                case Spline spline:
                    if (spline.TryPolygonalVertexes(MaxCurvePoints / 4, out var splinePoints)) {
                        Add(shapes, xform, spline.IsClosed, Flat(splinePoints));
                    }
                    break;

                case Solid solid:
                    Add(shapes, xform, true,
                        (solid.FirstCorner.X, solid.FirstCorner.Y),
                        (solid.SecondCorner.X, solid.SecondCorner.Y),
                        // DXF stores SOLID corners in bow-tie order: 3rd and 4th are swapped.
                        (solid.FourthCorner.X, solid.FourthCorner.Y),
                        (solid.ThirdCorner.X, solid.ThirdCorner.Y));
                    break;

                case Leader leader:
                    Add(shapes, xform, false, leader.Vertices.Select(v => (v.X, v.Y)).ToList());
                    break;

                case Insert insert:
                    EmitInsert(insert, shapes, xform, depth);
                    break;

                // Boundary outlines only — the pattern fill itself is not drawn. Explode() here
                // just converts the boundary paths to entities; it applies no transform of its own.
                case Hatch hatch:
                    foreach (Entity child in hatch.Explode()) {
                        Emit(child, shapes, xform, depth + 1);
                    }
                    break;

                // A dimension's lines and arrowheads live in an anonymous block, stored in the
                // coordinate space the dimension itself sits in.
                case Dimension dimension when dimension.Block is not null:
                    foreach (Entity child in dimension.Block.Entities) {
                        Emit(child, shapes, xform, depth + 1);
                    }
                    break;

                // Nothing to stroke: TextEntity, MText, AttributeEntity, CadPoint, Viewport, ...
                case CadPoint:
                default:
                    break;
            }
        }

        private static void EmitInsert(Insert insert, List<object> shapes, Xform xform, int depth) {
            var basePoint = insert.Block.BlockEntity.BasePoint;
            double cos = Math.Cos(insert.Rotation);
            double sin = Math.Sin(insert.Rotation);

            // world = insertPoint + R(rotation) * S(scale) * (p - basePoint), and MINSERT array
            // offsets step along the rotated axes.
            int rows = Math.Max((int)insert.RowCount, 1);
            int columns = Math.Max((int)insert.ColumnCount, 1);

            for (int row = 0; row < rows; row++) {
                for (int column = 0; column < columns; column++) {
                    double offsetX = column * insert.ColumnSpacing;
                    double offsetY = row * insert.RowSpacing;

                    double originX = insert.InsertPoint.X + (offsetX * cos) - (offsetY * sin);
                    double originY = insert.InsertPoint.Y + (offsetX * sin) + (offsetY * cos);

                    double a = cos * insert.XScale;
                    double b = sin * insert.XScale;
                    double c = -sin * insert.YScale;
                    double d = cos * insert.YScale;

                    Xform local = new(a, b, c, d,
                        originX - ((a * basePoint.X) + (c * basePoint.Y)),
                        originY - ((b * basePoint.X) + (d * basePoint.Y)));

                    Xform composed = xform.Compose(local);
                    foreach (Entity child in insert.Block.Entities) {
                        Emit(child, shapes, composed, depth + 1);
                    }
                }
            }
        }

        private static void EmitPolyline(List<object> shapes, Xform xform, bool isClosed,
                                         IReadOnlyList<(double X, double Y, double Bulge)> vertices) {
            if (vertices.Count < 2) {
                return;
            }

            List<(double X, double Y)> points = new(vertices.Count);
            int last = isClosed ? vertices.Count : vertices.Count - 1;

            for (int i = 0; i < last; i++) {
                var current = vertices[i];
                var next = vertices[(i + 1) % vertices.Count];
                points.Add((current.X, current.Y));
                AppendBulge(points, current.X, current.Y, next.X, next.Y, current.Bulge);
            }

            if (!isClosed) {
                points.Add((vertices[^1].X, vertices[^1].Y));
            }

            Add(shapes, xform, isClosed, points);
        }

        /// <summary>Interpolates the arc a polyline vertex's bulge describes, endpoints excluded.</summary>
        private static void AppendBulge(List<(double X, double Y)> points,
                                        double x1, double y1, double x2, double y2, double bulge) {
            if (Math.Abs(bulge) < 1e-9) {
                return;
            }

            double dx = x2 - x1;
            double dy = y2 - y1;
            if ((dx * dx) + (dy * dy) < 1e-24) {
                return;
            }

            // bulge = tan(sweep / 4); the arc centre sits off the chord midpoint along its left normal.
            double sweep = 4 * Math.Atan(bulge);
            double offset = (1 - (bulge * bulge)) / (4 * bulge);
            double centerX = ((x1 + x2) / 2) - (offset * dy);
            double centerY = ((y1 + y2) / 2) + (offset * dx);
            double radius = Math.Sqrt(((x1 - centerX) * (x1 - centerX)) + ((y1 - centerY) * (y1 - centerY)));
            double startAngle = Math.Atan2(y1 - centerY, x1 - centerX);

            int steps = CurvePoints(sweep) - 1;
            for (int i = 1; i < steps; i++) {
                double angle = startAngle + (sweep * i / steps);
                points.Add((centerX + (radius * Math.Cos(angle)), centerY + (radius * Math.Sin(angle))));
            }
        }

        private static int CurvePoints(double sweep) =>
            Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / StepAngle) + 1, MinCurvePoints, MaxCurvePoints);

        private static List<(double X, double Y)> Flat(IEnumerable<CSMath.XYZ> vertices) =>
            vertices.Select(v => (v.X, v.Y)).ToList();

        private static void Add(List<object> shapes, Xform xform, bool isClosed, params (double X, double Y)[] points) =>
            Add(shapes, xform, isClosed, (IReadOnlyList<(double X, double Y)>)points);

        private static void Add(List<object> shapes, Xform xform, bool isClosed,
                                IReadOnlyList<(double X, double Y)> points) {
            if (points.Count < 2) {
                return;
            }

            var mapped = new (double X, double Y)[points.Count];
            for (int i = 0; i < points.Count; i++) {
                mapped[i] = xform.Apply(points[i].X, points[i].Y);
            }

            shapes.Add(new PolylineShape(mapped, isClosed));
        }
    }
}
