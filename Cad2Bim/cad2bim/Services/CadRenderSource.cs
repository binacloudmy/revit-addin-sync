using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using Cad2Bim.ViewModels.Shapes;

// Cad2Bim.Arc / Cad2Bim.Point (the classification model) shadow the ACadSharp entities of the
// same name inside this namespace, so the CAD ones are always spelled through these aliases.
using CadArc = ACadSharp.Entities.Arc;
using CadPoint = ACadSharp.Entities.Point;

namespace Cad2Bim.Services {
    /// <summary>Where a piece of geometry came from, so a consumer can ignore what it does not
    /// want. Dimension leaders and hatch boundaries draw fine but are not building fabric.</summary>
    public enum CadSource { Geometry, Block, Hatch, Dimension }

    /// <summary>An arc in world coordinates. Null wherever the block transform was not a
    /// rotation and a uniform scale — under a squashed or sheared insert an arc is an ellipse,
    /// and calling it an arc would put a door swing in the wrong place.</summary>
    public readonly record struct ArcParams(
        double CenterX, double CenterY, double Radius, double StartAngle, double EndAngle);

    /// <summary>
    /// Receives the drawing one piece at a time, already flattened into world coordinates.
    /// The viewport wants polylines; the classifier wants arcs kept as arcs and text kept as
    /// text. Both are fed from the one traversal, so neither can drift from the other.
    /// </summary>
    public interface ICadSink {
        void Polyline(IReadOnlyList<(double X, double Y)> points, bool isClosed, string layer, CadSource source);
        void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters, string layer, CadSource source);
        void Text(double x, double y, double height, string value, string layer, CadSource source);
    }

    /// <summary>
    /// Walks a CadDocument into world coordinates: blocks flattened, curves tessellated,
    /// nothing classified or filtered. What each consumer keeps is its own business.
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

        /// <summary>Feed every model-space entity to a sink.</summary>
        public static void Walk(CadDocument document, ICadSink sink) {
            foreach (Entity entity in document.Entities) {
                Emit(entity, sink, Xform.Identity, 0, CadSource.Geometry);
            }
        }

        /// <summary>Model-space entities, flattened to polylines. Text is skipped.</summary>
        public static List<object> Flatten(CadDocument document) {
            var sink = new PolylineSink();
            Walk(document, sink);
            return sink.Shapes;
        }

        /// <summary>The viewport's view of a drawing: strokable outlines and nothing else.</summary>
        private sealed class PolylineSink : ICadSink {
            public List<object> Shapes { get; } = new();

            public void Polyline(IReadOnlyList<(double X, double Y)> points, bool isClosed,
                                 string layer, CadSource source) {
                if (points.Count >= 2) Shapes.Add(new PolylineShape(points, isClosed));
            }

            public void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters,
                            string layer, CadSource source) {
                if (points.Count >= 2) Shapes.Add(new PolylineShape(points, false));
            }

            public void Text(double x, double y, double height, string value,
                             string layer, CadSource source) { }
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

            /// <summary>True when the map is a rotation and a uniform scale, which is the only
            /// case where an arc stays an arc.</summary>
            public bool TryConformal(out double scale, out double rotation) {
                scale = Math.Sqrt(Math.Abs((A * D) - (B * C)));
                rotation = Math.Atan2(B, A);

                if (scale <= 0) return false;

                double tolerance = scale * 1e-6;
                return Math.Abs(A - D) <= tolerance && Math.Abs(B + C) <= tolerance;
            }
        }

        private static void Emit(Entity entity, ICadSink sink, Xform xform, int depth, CadSource source) {
            if (depth > MaxDepth || entity.IsInvisible) {
                return;
            }

            string layer = entity.Layer?.Name ?? string.Empty;

            switch (entity) {
                case Line line:
                    Add(sink, xform, false, layer, source,
                        (line.StartPoint.X, line.StartPoint.Y), (line.EndPoint.X, line.EndPoint.Y));
                    break;

                case LwPolyline lwPolyline:
                    EmitPolyline(sink, xform, lwPolyline.IsClosed, layer, source,
                        lwPolyline.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList());
                    break;

                case Polyline2D polyline2D:
                    EmitPolyline(sink, xform, polyline2D.IsClosed, layer, source,
                        polyline2D.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList());
                    break;

                case Polyline3D polyline3D:
                    Add(sink, xform, polyline3D.IsClosed, layer, source,
                        polyline3D.Vertices.Select(v => (v.Location.X, v.Location.Y)).ToList());
                    break;

                // Arc derives from Circle, so it has to be matched first.
                case CadArc arc:
                    EmitArc(sink, xform, arc, layer, source);
                    break;

                case Circle circle:
                    Add(sink, xform, true, layer, source,
                        Flat(circle.PolygonalVertexes(CurvePoints(2 * Math.PI))));
                    break;

                case Ellipse ellipse:
                    Add(sink, xform, ellipse.IsFullEllipse, layer, source,
                        Flat(ellipse.PolygonalVertexes(
                            CurvePoints(ellipse.EndParameter - ellipse.StartParameter))));
                    break;

                case Spline spline:
                    if (spline.TryPolygonalVertexes(MaxCurvePoints / 4, out var splinePoints)) {
                        Add(sink, xform, spline.IsClosed, layer, source, Flat(splinePoints));
                    }
                    break;

                case Solid solid:
                    Add(sink, xform, true, layer, source,
                        (solid.FirstCorner.X, solid.FirstCorner.Y),
                        (solid.SecondCorner.X, solid.SecondCorner.Y),
                        // DXF stores SOLID corners in bow-tie order: 3rd and 4th are swapped.
                        (solid.FourthCorner.X, solid.FourthCorner.Y),
                        (solid.ThirdCorner.X, solid.ThirdCorner.Y));
                    break;

                case Leader leader:
                    Add(sink, xform, false, layer, source, leader.Vertices.Select(v => (v.X, v.Y)).ToList());
                    break;

                case Insert insert:
                    EmitInsert(insert, sink, xform, depth, layer);
                    break;

                // Boundary outlines only — the pattern fill itself is not drawn. Explode() here
                // just converts the boundary paths to entities; it applies no transform of its own.
                case Hatch hatch:
                    foreach (Entity child in hatch.Explode()) {
                        Emit(child, sink, xform, depth + 1, CadSource.Hatch);
                    }
                    break;

                // A dimension's lines and arrowheads live in an anonymous block, stored in the
                // coordinate space the dimension itself sits in.
                case Dimension dimension when dimension.Block is not null:
                    foreach (Entity child in dimension.Block.Entities) {
                        Emit(child, sink, xform, depth + 1, CadSource.Dimension);
                    }
                    break;

                // Room names, door marks and grid labels: nothing to stroke, but the classifier
                // needs them, so they reach the sink and the viewport drops them.
                case TextEntity text:
                    EmitText(sink, xform, text.InsertPoint.X, text.InsertPoint.Y,
                             text.Height, text.Value, layer, source);
                    break;

                case MText mtext:
                    EmitText(sink, xform, mtext.InsertPoint.X, mtext.InsertPoint.Y,
                             mtext.Height, mtext.Value, layer, source);
                    break;

                case CadPoint:
                default:
                    break;
            }
        }

        private static void EmitInsert(Insert insert, ICadSink sink, Xform xform, int depth, string layer) {
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
                        Emit(child, sink, composed, depth + 1, CadSource.Block);
                    }
                }
            }

            // Block attributes carry the door mark, the room number, the window code — the
            // drawing's own words for what the block is. They sit in world space already.
            foreach (AttributeEntity attribute in insert.Attributes) {
                EmitText(sink, xform, attribute.InsertPoint.X, attribute.InsertPoint.Y,
                         attribute.Height, attribute.Value, layer, CadSource.Block);
            }
        }

        private static void EmitArc(ICadSink sink, Xform xform, CadArc arc, string layer, CadSource source) {
            var points = MapAll(xform, Flat(arc.PolygonalVertexes(CurvePoints(arc.Sweep))));

            ArcParams? parameters = null;
            if (xform.TryConformal(out double scale, out double rotation)) {
                var (centerX, centerY) = xform.Apply(arc.Center.X, arc.Center.Y);
                parameters = new ArcParams(centerX, centerY, arc.Radius * scale,
                                           arc.StartAngle + rotation, arc.EndAngle + rotation);
            }

            sink.Arc(points, parameters, layer, source);
        }

        private static void EmitText(ICadSink sink, Xform xform, double x, double y,
                                     double height, string? value, string layer, CadSource source) {
            if (string.IsNullOrWhiteSpace(value)) return;

            var (mappedX, mappedY) = xform.Apply(x, y);
            xform.TryConformal(out double scale, out _);
            sink.Text(mappedX, mappedY, height * (scale > 0 ? scale : 1.0), value!, layer, source);
        }

        private static void EmitPolyline(ICadSink sink, Xform xform, bool isClosed, string layer,
                                         CadSource source,
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

            Add(sink, xform, isClosed, layer, source, points);
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

        private static (double X, double Y)[] MapAll(Xform xform, IReadOnlyList<(double X, double Y)> points) {
            var mapped = new (double X, double Y)[points.Count];
            for (int i = 0; i < points.Count; i++) {
                mapped[i] = xform.Apply(points[i].X, points[i].Y);
            }
            return mapped;
        }

        private static void Add(ICadSink sink, Xform xform, bool isClosed, string layer, CadSource source,
                                params (double X, double Y)[] points) =>
            Add(sink, xform, isClosed, layer, source, (IReadOnlyList<(double X, double Y)>)points);

        private static void Add(ICadSink sink, Xform xform, bool isClosed, string layer, CadSource source,
                                IReadOnlyList<(double X, double Y)> points) {
            if (points.Count < 2) {
                return;
            }

            sink.Polyline(MapAll(xform, points), isClosed, layer, source);
        }
    }
}
