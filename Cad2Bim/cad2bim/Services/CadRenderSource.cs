using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
    /// want. Dimension leaders and hatch boundaries draw fine but are not building fabric.
    /// Proxy is AEC-object linework (AutoCAD Architecture / Civil 3D / MEP) recovered from an
    /// entity's cached proxy graphics rather than from a native ACadSharp entity type — it is
    /// building fabric same as Geometry, so LayerFilter.Allows lets it through by default.</summary>
    public enum CadSource { Geometry, Block, Hatch, Dimension, Proxy }

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
    /// What a block definition is, read once per definition: the name to file it under, its
    /// local extent and what it is made of. A census needs the shape of an anonymous block to
    /// tell one anonymous copy from another, and the walker is the only code that opens block
    /// definitions, so it hands this over rather than a bare name.
    /// </summary>
    public sealed class CadBlockInfo {
        internal CadBlockInfo(string name, string recordName, bool isAnonymous, bool isUnresolvedXref,
                              bool hasGeometry, double minX, double minY, double maxX, double maxY,
                              IReadOnlyDictionary<string, int> entityCounts, int nestedInserts) {
            Name = name;
            RecordName = recordName;
            IsAnonymous = isAnonymous;
            IsUnresolvedXref = isUnresolvedXref;
            HasGeometry = hasGeometry;
            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
            EntityCounts = entityCounts;
            NestedInserts = nestedInserts;
        }

        /// <summary>The source block's name for an anonymous copy of a dynamic block (the
        /// drafter's "SOCKET-13A" rather than "*U12"), else the record's own name.</summary>
        public string Name { get; }

        /// <summary>The block record's raw name, e.g. "*U12".</summary>
        public string RecordName { get; }

        /// <summary>An anonymous record whose source block could not be found, so
        /// <see cref="Name"/> is only the raw "*U.." name.</summary>
        public bool IsAnonymous { get; }

        /// <summary>An external reference whose drawing was not bound in: the flag is set and
        /// the definition holds nothing.</summary>
        public bool IsUnresolvedXref { get; }

        /// <summary>False when the definition draws nothing, so the extent below is zero.</summary>
        public bool HasGeometry { get; }

        /// <summary>The definition's box in drawing units, relative to its base point.</summary>
        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        /// <summary>The definition's own entities (not those of nested blocks), by type name.</summary>
        public IReadOnlyDictionary<string, int> EntityCounts { get; }

        /// <summary>How many inserts sit directly in the definition.</summary>
        public int NestedInserts { get; }
    }

    /// <summary>
    /// An optional second interface for an <see cref="ICadSink"/>: a sink that implements it is
    /// also told about every block insert, so it can count symbols as well as stroke them.
    /// Sinks that do not implement it get exactly the calls they always did.
    /// </summary>
    public interface ICadSymbolSink {
        /// <summary>Called for every insert at any nesting depth, once per MINSERT cell, in
        /// world coordinates (drawing units), before that insert's contents reach the
        /// ICadSink. Inserts inside a dimension's own block (arrowheads) are not reported.</summary>
        void Symbol(CadBlockInfo block, string layer, double x, double y, double rotationRad,
                    double scaleX, double scaleY, bool mirrored, int depth,
                    IReadOnlyDictionary<string, string> attributes);
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
        ///
        /// Internal (not private) so EmitProxy — and the tests that drive it directly — can
        /// take one as a parameter without exposing it outside the assembly.
        /// </summary>
        internal readonly record struct Xform(double A, double B, double C, double D, double E, double F) {
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

        /// <summary>
        /// The map from an entity's object coordinate system (OCS) to world XY for extrusion
        /// direction <paramref name="normal"/>: AutoCAD's arbitrary axis algorithm, projected
        /// onto the plan. An INSERT whose extrusion is (0,0,-1) stores its insert point and
        /// rotation in an OCS whose X runs along world -X, so read as world coordinates the
        /// whole block lands mirrored through x = 0, often metres from where it is drawn - and
        /// that phantom copy then stretches the extents Units.Resolve sizes the drawing by.
        ///
        /// Only INSERT extrusion is honoured (see EmitInsert). Arcs, circles, polylines, text,
        /// hatches and ellipses with a flipped extrusion are still read as world coordinates.
        /// </summary>
        internal static Xform OcsXform(CSMath.XYZ normal) {
            double length = Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y) + (normal.Z * normal.Z));
            if (length < 1e-12) return Xform.Identity;

            double nx = normal.X / length, ny = normal.Y / length, nz = normal.Z / length;

            // The usual +Z extrusion: identity, bit for bit, so ordinary drawings are untouched.
            if (nz > 1 - 1e-9) return Xform.Identity;

            // Arbitrary axis algorithm: OCS X = Wy x N near the poles, Wz x N otherwise.
            double ax, ay, az;
            if (Math.Abs(nx) < 1 / 64.0 && Math.Abs(ny) < 1 / 64.0) {
                ax = nz; ay = 0; az = -nx;
            } else {
                ax = -ny; ay = nx; az = 0;
            }

            double axisLength = Math.Sqrt((ax * ax) + (ay * ay) + (az * az));
            ax /= axisLength; ay /= axisLength; az /= axisLength;

            // OCS Y = N x OCS X; only its world X and Y survive the projection onto the plan.
            double bx = (ny * az) - (nz * ay);
            double by = (nz * ax) - (nx * az);

            return new Xform(ax, ay, bx, by, 0, 0);
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
                    EmitInsert(insert, sink, xform, depth, layer, source);
                    break;

                // Boundary outlines only — the pattern fill itself is not drawn. Explode() here
                // just converts the boundary paths to entities; it applies no transform of its own.
                // A hatch's boundary is read as a loop rather than exploded into loose edges.
                // Exploding gives a run of open lines, and a wall drawn as poché is then
                // indistinguishable from its own fill strokes - which is how 1,946 of 1,997
                // partition faces came back as fragments too short to be anything.
                case Hatch hatch:
                    EmitHatch(hatch, sink, xform, layer);
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
                    break;

                // UnknownEntity, ProxyEntity, and anything else the switch above does not
                // recognise: AutoCAD Architecture / Civil 3D / MEP walls, doors and the like
                // arrive as these, with no native fields at all — the only thing ACadSharp
                // still hands back for them is cached proxy graphics.
                default:
                    if (entity.ProxyGeometries != null && entity.ProxyGeometries.Count > 0) {
                        EmitEntityProxy(entity, sink, xform, layer);
                    }
                    break;
            }
        }

        /// <summary>Resolves a layer handle to its name for the proxy path, then hands the
        /// entity's cached proxy graphics to <see cref="EmitProxy"/>.</summary>
        private static void EmitEntityProxy(Entity entity, ICadSink sink, Xform xform, string layer) {
            CadDocument? document = entity.Document;
            string ResolveLayer(ulong handle) {
                if (document == null || handle == 0) return null;
                return document.TryGetCadObject(handle, out ACadSharp.Tables.Layer resolved)
                    ? resolved.Name
                    : null;
            }

            EmitProxy(entity.ProxyGeometries, ResolveLayer, layer, xform, sink);
        }

        /// <summary>
        /// Converts one entity's cached proxy graphics (<see cref="Entity.ProxyGeometries"/>)
        /// into the same sink calls a native entity would have produced. Pure and internal so
        /// tests can drive it with hand-built primitives, with no CadDocument required.
        ///
        /// Primitives not listed below (clip, colour, linetype, marker, material, plot style,
        /// thickness, construction lines, subentity mapper, extents) carry no linework and are
        /// ignored, same as the native switch ignores CadPoint.
        /// </summary>
        internal static void EmitProxy(
            IEnumerable<ACadSharp.Entities.ProxyGraphics.IProxyGeometry> geometries,
            Func<ulong, string> layerByHandle,
            string fallbackLayer,
            Xform xform,
            ICadSink sink) {
            if (geometries == null) return;

            string currentLayer = fallbackLayer;
            var transforms = new Stack<Xform>();
            Xform current = xform;

            foreach (ACadSharp.Entities.ProxyGraphics.IProxyGeometry geometry in geometries) {
                switch (geometry) {
                    // Model-transform stack: everything emitted between a push and its pop is
                    // mapped through the pushed matrix composed with whatever came before it,
                    // same nesting rule as EmitInsert's block transforms.
                    case ACadSharp.Entities.ProxyGraphics.ProxyPushModelTransform push:
                        transforms.Push(current);
                        current = current.Compose(ToXform(push.TransformationMatrix));
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyPushModelTransform2 push2:
                        transforms.Push(current);
                        current = current.Compose(ToXform(push2.TransformationMatrix));
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyPopModelTransform:
                        if (transforms.Count > 0) current = transforms.Pop();
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxySubentLayer subentLayer: {
                        string resolved = layerByHandle?.Invoke((ulong)subentLayer.LayerIndex);
                        if (!string.IsNullOrEmpty(resolved)) currentLayer = resolved;
                        break;
                    }

                    // WithNormal derives from ProxyPolyline, so it must be matched first.
                    case ACadSharp.Entities.ProxyGraphics.ProxyPolylineWithNormal polylineWithNormal:
                        Add(sink, current, false, currentLayer, CadSource.Proxy, ProxyPoints(polylineWithNormal.Points));
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyPolyline polyline:
                        Add(sink, current, false, currentLayer, CadSource.Proxy, ProxyPoints(polyline.Points));
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyPolygon polygon:
                        Add(sink, current, true, currentLayer, CadSource.Proxy, ProxyPoints(polygon.Points));
                        break;

                    // A wrapped real LwPolyline: read it exactly like the native LwPolyline case.
                    case ACadSharp.Entities.ProxyGraphics.ProxyLwPolyine lwPolyine when lwPolyine.Entity != null:
                        EmitPolyline(sink, current, lwPolyine.Entity.IsClosed, currentLayer, CadSource.Proxy,
                            lwPolyine.Entity.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList());
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyCircle circle:
                        EmitCircleFromCenter(sink, current, currentLayer, CadSource.Proxy,
                            circle.Center.X, circle.Center.Y, circle.Radius);
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyCirclePt3 circlePt3:
                        if (TryCircumcircle(circlePt3.Point1, circlePt3.Point2, circlePt3.Point3,
                                out double cx3, out double cy3, out double r3)) {
                            EmitCircleFromCenter(sink, current, currentLayer, CadSource.Proxy, cx3, cy3, r3);
                        }
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyCircularArc arc: {
                        double startAngle = Math.Atan2(arc.StartVectorDirection.Y, arc.StartVectorDirection.X);
                        EmitArcFromCenter(sink, current, currentLayer, CadSource.Proxy,
                            arc.Center.X, arc.Center.Y, arc.Radius, startAngle, startAngle + arc.SweepAngle);
                        break;
                    }

                    case ACadSharp.Entities.ProxyGraphics.ProxyCircularArc3Pt arc3Pt:
                        if (TryArcFrom3Points(arc3Pt.Point1, arc3Pt.Point2, arc3Pt.Point3,
                                out double cx2, out double cy2, out double r2,
                                out double startAngle2, out double endAngle2)) {
                            EmitArcFromCenter(sink, current, currentLayer, CadSource.Proxy,
                                cx2, cy2, r2, startAngle2, endAngle2);
                        }
                        break;

                    // Plan view of an AEC wall/slab is frequently a shell's top face: each face's
                    // vertex loop, closed. Shared-edge dedupe is not required.
                    case ACadSharp.Entities.ProxyGraphics.ProxyShell shell:
                        EmitFacesProxy(sink, current, ResolveTraitsLayer(shell.FaceTraits?.LayerHandles, layerByHandle, currentLayer),
                            shell.Vertices, shell.Faces);
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyMesh mesh:
                        EmitMeshProxy(sink, current, ResolveTraitsLayer(mesh.FaceTraits?.LayerHandles, layerByHandle, currentLayer), mesh);
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyText2 text2:
                        EmitText(sink, current, text2.StartPoint.X, text2.StartPoint.Y, text2.Height, text2.Text,
                            currentLayer, CadSource.Proxy);
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyUnicodeText2 unicodeText2:
                        EmitText(sink, current, unicodeText2.StartPoint.X, unicodeText2.StartPoint.Y, unicodeText2.Height,
                            unicodeText2.Text, currentLayer, CadSource.Proxy);
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyText text:
                        EmitText(sink, current, text.StartPoint.X, text.StartPoint.Y, text.Height, text.Text,
                            currentLayer, CadSource.Proxy);
                        break;

                    case ACadSharp.Entities.ProxyGraphics.ProxyUnicodeText unicodeText:
                        EmitText(sink, current, unicodeText.StartPoint.X, unicodeText.StartPoint.Y, unicodeText.Height,
                            unicodeText.Text, currentLayer, CadSource.Proxy);
                        break;

                    // Clip, colour, linetype, marker, material, plot-style, thickness, mapper,
                    // extents, construction lines and anything unrecognised: no linework to draw.
                    default:
                        break;
                }
            }
        }

        /// <summary>Best-effort layer for a Shell/Mesh's faces from its FaceTraits' handle list —
        /// falls back to the layer already in effect when there is nothing to resolve. ACadSharp
        /// exposes these as raw handles (one set for the whole primitive, not one per face), so
        /// this reads only the first and applies it to every face the primitive emits.</summary>
        private static string ResolveTraitsLayer(IReadOnlyList<ulong> layerHandles, Func<ulong, string> layerByHandle, string fallback) {
            if (layerHandles == null || layerHandles.Count == 0 || layerByHandle == null) return fallback;
            string resolved = layerByHandle(layerHandles[0]);
            return string.IsNullOrEmpty(resolved) ? fallback : resolved;
        }

        private static List<(double X, double Y)> ProxyPoints(IReadOnlyList<CSMath.XYZ> points) =>
            points?.Select(p => (p.X, p.Y)).ToList() ?? new List<(double X, double Y)>();

        /// <summary>Row-vector * matrix convention (CSMath.Matrix4: translation lives in the last
        /// row, M30/M31/M32) collapsed onto the XY plane — the same "drop Z" the rest of this
        /// walker applies, so a proxy transform that only rotates/translates/scales in-plane maps
        /// exactly; one with genuine out-of-plane rotation loses that component, same as every
        /// other case here loses Z.</summary>
        private static Xform ToXform(CSMath.Matrix4 matrix) =>
            new(matrix.M00, matrix.M01, matrix.M10, matrix.M11, matrix.M30, matrix.M31);

        private static void EmitCircleFromCenter(ICadSink sink, Xform xform, string layer, CadSource source,
                                                 double centerX, double centerY, double radius) {
            int count = CurvePoints(2 * Math.PI);
            var points = new (double X, double Y)[count];
            for (int i = 0; i < count; i++) {
                double angle = 2 * Math.PI * i / count;
                points[i] = (centerX + (radius * Math.Cos(angle)), centerY + (radius * Math.Sin(angle)));
            }
            Add(sink, xform, true, layer, source, points);
        }

        private static void EmitArcFromCenter(ICadSink sink, Xform xform, string layer, CadSource source,
                                              double centerX, double centerY, double radius,
                                              double startAngle, double endAngle) {
            double sweep = endAngle - startAngle;
            int count = CurvePoints(sweep);
            var points = new (double X, double Y)[count];
            for (int i = 0; i < count; i++) {
                double angle = startAngle + (sweep * i / (count - 1));
                points[i] = (centerX + (radius * Math.Cos(angle)), centerY + (radius * Math.Sin(angle)));
            }

            var mapped = MapAll(xform, points);

            ArcParams? parameters = null;
            if (xform.TryConformal(out double scale, out double rotation)) {
                var (mappedCenterX, mappedCenterY) = xform.Apply(centerX, centerY);
                parameters = new ArcParams(mappedCenterX, mappedCenterY, radius * scale,
                                           startAngle + rotation, endAngle + rotation);
            }

            sink.Arc(mapped, parameters, layer, source);
        }

        /// <summary>Circumcircle of three 2D points (Z dropped, same as everywhere else in this
        /// walker). False when the points are collinear — there is no circle to report.</summary>
        private static bool TryCircumcircle(CSMath.XYZ p1, CSMath.XYZ p2, CSMath.XYZ p3,
                                            out double centerX, out double centerY, out double radius) {
            centerX = 0; centerY = 0; radius = 0;

            double ax = p1.X, ay = p1.Y;
            double bx = p2.X, by = p2.Y;
            double cx = p3.X, cy = p3.Y;

            double d = 2 * ((ax * (by - cy)) + (bx * (cy - ay)) + (cx * (ay - by)));
            if (Math.Abs(d) < 1e-9) return false;

            double a2 = (ax * ax) + (ay * ay);
            double b2 = (bx * bx) + (by * by);
            double c2 = (cx * cx) + (cy * cy);

            centerX = ((a2 * (by - cy)) + (b2 * (cy - ay)) + (c2 * (ay - by))) / d;
            centerY = ((a2 * (cx - bx)) + (b2 * (ax - cx)) + (c2 * (bx - ax))) / d;
            radius = Math.Sqrt(((ax - centerX) * (ax - centerX)) + ((ay - centerY) * (ay - centerY)));
            return true;
        }

        /// <summary>A 3-point arc: the circle through all three, swept from Point1 through
        /// Point2 to Point3 (the direction that passes through the middle point wins).</summary>
        private static bool TryArcFrom3Points(CSMath.XYZ p1, CSMath.XYZ p2, CSMath.XYZ p3,
                                              out double centerX, out double centerY, out double radius,
                                              out double startAngle, out double endAngle) {
            startAngle = 0; endAngle = 0;
            if (!TryCircumcircle(p1, p2, p3, out centerX, out centerY, out radius)) return false;

            double a1 = Math.Atan2(p1.Y - centerY, p1.X - centerX);
            double aMid = Math.Atan2(p2.Y - centerY, p2.X - centerX);
            double a3 = Math.Atan2(p3.Y - centerY, p3.X - centerX);

            double ForwardSweep(double from, double to) {
                double delta = to - from;
                while (delta < 0) delta += 2 * Math.PI;
                return delta;
            }

            double sweepToEnd = ForwardSweep(a1, a3);
            double sweepToMid = ForwardSweep(a1, aMid);

            startAngle = a1;
            endAngle = sweepToMid <= sweepToEnd ? a1 + sweepToEnd : a1 - (2 * Math.PI - sweepToEnd);
            return true;
        }

        /// <summary>Each face's vertex loop as a closed polyline. Faces is a list of index
        /// arrays into Vertices (ACadSharp: "each integer corresponds to a vertex index");
        /// a negative index (an invisible-edge marker in some DXF mesh encodings) still names
        /// the same vertex once its sign is dropped.</summary>
        private static void EmitFacesProxy(ICadSink sink, Xform xform, string layer,
                                           IReadOnlyList<CSMath.XYZ> vertices, IReadOnlyList<int[]> faces) {
            if (vertices == null || vertices.Count == 0 || faces == null) return;

            foreach (int[] face in faces) {
                if (face == null || face.Length < 3) continue;

                var points = new List<(double X, double Y)>(face.Length);
                bool valid = true;
                foreach (int raw in face) {
                    int index = Math.Abs(raw);
                    if (index >= vertices.Count) { valid = false; break; }
                    points.Add((vertices[index].X, vertices[index].Y));
                }

                if (valid && points.Count >= 3) Add(sink, xform, true, layer, CadSource.Proxy, points);
            }
        }

        /// <summary>A row/column vertex grid (ProxyMesh carries no explicit face list) read as
        /// one closed quad per grid cell.</summary>
        private static void EmitMeshProxy(ICadSink sink, Xform xform, string layer,
                                          ACadSharp.Entities.ProxyGraphics.ProxyMesh mesh) {
            IReadOnlyList<CSMath.XYZ> vertices = mesh.Vertices;
            int rows = mesh.RowCount;
            int columns = mesh.ColumnCount;
            if (vertices == null || rows < 2 || columns < 2) return;

            for (int row = 0; row < rows - 1; row++) {
                for (int column = 0; column < columns - 1; column++) {
                    int i00 = (row * columns) + column;
                    int i01 = i00 + 1;
                    int i11 = i01 + columns;
                    int i10 = i00 + columns;
                    if (i11 >= vertices.Count) continue;

                    var quad = new List<(double X, double Y)> {
                        (vertices[i00].X, vertices[i00].Y),
                        (vertices[i01].X, vertices[i01].Y),
                        (vertices[i11].X, vertices[i11].Y),
                        (vertices[i10].X, vertices[i10].Y),
                    };
                    Add(sink, xform, true, layer, CadSource.Proxy, quad);
                }
            }
        }

        /// <summary>
        /// The outline of each of a hatch's boundary loops, closed.
        ///
        /// The fill strokes are not emitted at all. They pair with each other into false walls,
        /// and they carry nothing the boundary does not: the boundary is the shape the drafter
        /// hatched, which for wall poché is the wall.
        /// </summary>
        private static void EmitHatch(Hatch hatch, ICadSink sink, Xform xform, string layer) {
            foreach (Hatch.BoundaryPath path in hatch.Paths) {
                var points = new List<(double X, double Y)>();

                foreach (Hatch.BoundaryPath.Edge edge in path.Edges) {
                    switch (edge) {
                        case Hatch.BoundaryPath.Line line:
                            points.Add((line.Start.X, line.Start.Y));
                            points.Add((line.End.X, line.End.Y));
                            break;

                        case Hatch.BoundaryPath.Polyline polyline:
                            foreach (var vertex in polyline.Vertices) {
                                points.Add((vertex.X, vertex.Y));
                            }
                            break;

                        case Hatch.BoundaryPath.Arc arc:
                            // Ends only: a wall outline's corners are what matter, and a
                            // rounded end changes the extent by less than a millimetre.
                            points.Add((arc.Center.X + (arc.Radius * Math.Cos(arc.StartAngle)),
                                        arc.Center.Y + (arc.Radius * Math.Sin(arc.StartAngle))));
                            points.Add((arc.Center.X + (arc.Radius * Math.Cos(arc.EndAngle)),
                                        arc.Center.Y + (arc.Radius * Math.Sin(arc.EndAngle))));
                            break;
                    }
                }

                if (points.Count >= 3) {
                    Add(sink, xform, true, layer, CadSource.Hatch, points);
                }
            }
        }

        private static readonly IReadOnlyDictionary<string, string> NoAttributes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static void EmitInsert(Insert insert, ICadSink sink, Xform xform, int depth, string layer,
                                       CadSource source) {
            if (insert.Block == null) return;

            var basePoint = insert.Block.BlockEntity.BasePoint;

            // Only a sink that asked for symbols pays for the block info and the attributes.
            // A dimension's arrowheads are inserts too, but they are not the drawing's symbols.
            ICadSymbolSink? symbols = source == CadSource.Dimension ? null : sink as ICadSymbolSink;
            CadBlockInfo? info = symbols != null ? InfoFor(insert.Block) : null;
            IReadOnlyDictionary<string, string> attributes = symbols != null ? AttributesOf(insert) : NoAttributes;
            double cos = Math.Cos(insert.Rotation);
            double sin = Math.Sin(insert.Rotation);

            // world = insertPoint + R(rotation) * S(scale) * (p - basePoint), and MINSERT array
            // offsets step along the rotated axes.
            int rows = Math.Max((int)insert.RowCount, 1);
            int columns = Math.Max((int)insert.ColumnCount, 1);

            // The insert point and rotation are in the insert's OCS: map that to world first.
            Xform ocs = OcsXform(insert.Normal);
            Xform outer = ocs == Xform.Identity ? xform : xform.Compose(ocs);

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

                    Xform composed = outer.Compose(local);

                    if (symbols != null) {
                        var (worldX, worldY) = outer.Apply(originX, originY);
                        double scaleX = Math.Sqrt((composed.A * composed.A) + (composed.B * composed.B));
                        double scaleY = Math.Sqrt((composed.C * composed.C) + (composed.D * composed.D));
                        bool mirrored = ((composed.A * composed.D) - (composed.B * composed.C)) < 0;

                        symbols.Symbol(info!, layer, worldX, worldY, Math.Atan2(composed.B, composed.A),
                                       scaleX, scaleY, mirrored, depth, attributes);
                    }

                    foreach (Entity child in insert.Block.Entities) {
                        Emit(child, sink, composed, depth + 1, CadSource.Block);
                    }
                }
            }

            // Block attributes carry the door mark, the room number, the window code — the
            // drawing's own words for what the block is. They sit in the parent's space, in the
            // insert's OCS like the insert point does, so they go through the same OCS map.
            foreach (AttributeEntity attribute in insert.Attributes) {
                EmitText(sink, outer, attribute.InsertPoint.X, attribute.InsertPoint.Y,
                         attribute.Height, attribute.Value, layer, CadSource.Block);
            }
        }

        /// <summary>An insert's attributes by tag: empty tags skipped, the first value wins.</summary>
        private static IReadOnlyDictionary<string, string> AttributesOf(Insert insert) {
            Dictionary<string, string>? found = null;

            foreach (AttributeEntity attribute in insert.Attributes) {
                if (string.IsNullOrWhiteSpace(attribute.Tag)) continue;

                found ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!found.ContainsKey(attribute.Tag)) found[attribute.Tag] = attribute.Value ?? string.Empty;
            }

            return found ?? NoAttributes;
        }

        // One CadBlockInfo per definition, however many times it is inserted. Keyed weakly so a
        // document that goes away takes its entries with it.
        private static readonly ConditionalWeakTable<ACadSharp.Tables.BlockRecord, CadBlockInfo> Infos = new();

        private static CadBlockInfo InfoFor(ACadSharp.Tables.BlockRecord block) =>
            Infos.GetValue(block, BuildInfo);

        private static CadBlockInfo BuildInfo(ACadSharp.Tables.BlockRecord block) {
            string recordName = block.Name ?? string.Empty;

            // A dynamic block's every variation is an anonymous copy ("*U12"); the definition
            // it was made from carries the name the drafter knows it by.
            string? sourceName = null;
            bool anonymous = block.IsAnonymous || recordName.StartsWith("*", StringComparison.Ordinal);
            if (anonymous) {
                try {
                    ACadSharp.Tables.BlockRecord? origin = block.Source;
                    if (origin != null && !string.IsNullOrEmpty(origin.Name)) sourceName = origin.Name;
                } catch (Exception) {
                    // A malformed dynamic-block graph is no reason to stop a walk.
                }
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int nestedInserts = 0;
            foreach (Entity entity in block.Entities) {
                string type = entity.GetType().Name;
                counts[type] = counts.TryGetValue(type, out int count) ? count + 1 : 1;
                if (entity is Insert) nestedInserts++;
            }

            bool xref = (block.Flags & ACadSharp.Blocks.BlockTypeFlags.XRef) != 0 ||
                        (block.Flags & ACadSharp.Blocks.BlockTypeFlags.XRefOverlay) != 0;

            // The extent comes from the same Emit the drawing goes through, shifted so the base
            // point is the origin. The probe is not a symbol sink, so this reports no symbols.
            CSMath.XYZ basePoint = block.BlockEntity != null ? block.BlockEntity.BasePoint : CSMath.XYZ.Zero;
            var probe = new ExtentSink();
            var shift = new Xform(1, 0, 0, 1, -basePoint.X, -basePoint.Y);
            foreach (Entity child in block.Entities) {
                Emit(child, probe, shift, 1, CadSource.Block);
            }

            return new CadBlockInfo(
                sourceName ?? recordName, recordName, anonymous && sourceName == null,
                xref && block.Entities.Count == 0, probe.Any,
                probe.Any ? probe.MinX : 0, probe.Any ? probe.MinY : 0,
                probe.Any ? probe.MaxX : 0, probe.Any ? probe.MaxY : 0,
                counts, nestedInserts);
        }

        /// <summary>Keeps only the bounding box of everything it is shown.</summary>
        private sealed class ExtentSink : ICadSink {
            public bool Any;
            public double MinX = double.MaxValue, MinY = double.MaxValue;
            public double MaxX = double.MinValue, MaxY = double.MinValue;

            private void Grow(double x, double y) {
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return;
                Any = true;
                if (x < MinX) MinX = x;
                if (y < MinY) MinY = y;
                if (x > MaxX) MaxX = x;
                if (y > MaxY) MaxY = y;
            }

            public void Polyline(IReadOnlyList<(double X, double Y)> points, bool isClosed,
                                 string layer, CadSource source) {
                foreach (var point in points) Grow(point.X, point.Y);
            }

            public void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters,
                            string layer, CadSource source) {
                foreach (var point in points) Grow(point.X, point.Y);
            }

            // Text has no extent of its own here, only an insertion point.
            public void Text(double x, double y, double height, string value,
                             string layer, CadSource source) => Grow(x, y);
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
                points.Add((vertices[vertices.Count - 1].X, vertices[vertices.Count - 1].Y));
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
            Math.Min(Math.Max((int)Math.Ceiling(Math.Abs(sweep) / StepAngle) + 1, MinCurvePoints), MaxCurvePoints);

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
