// AutoCAD Architecture / Civil 3D / MEP walls, doors and the like arrive from ACadSharp as
// UnknownEntity/ProxyEntity with no native fields — CadRenderSource.Emit's default case hands
// their cached Entity.ProxyGeometries to EmitProxy, which turns the ODA proxy-graphics
// primitives into the same ICadSink calls a native entity would have produced. These tests
// drive EmitProxy directly with hand-built primitives (its documented, internal, pure entry
// point) plus one end-to-end CadRenderSource.Walk test for the entity-routing + handle-based
// layer resolution CadDocument wiring.

using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Entities.ProxyGraphics;
using ACadSharp.Tables;
using Cad2Bim.Services;
using CSMath;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimProxyGraphicsTests
    {
        private static readonly CadRenderSource.Xform Identity = CadRenderSource.Xform.Identity;
        private static readonly Func<ulong, string> NoLayer = _ => null;

        private sealed class RecordingSink : ICadSink
        {
            public List<(List<(double X, double Y)> Points, bool IsClosed, string Layer, CadSource Source)> Polylines { get; } = new();
            public List<(List<(double X, double Y)> Points, ArcParams? Parameters, string Layer, CadSource Source)> Arcs { get; } = new();
            public List<(double X, double Y, double Height, string Value, string Layer, CadSource Source)> Texts { get; } = new();

            public void Polyline(IReadOnlyList<(double X, double Y)> points, bool isClosed, string layer, CadSource source) =>
                Polylines.Add((points.ToList(), isClosed, layer, source));

            public void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters, string layer, CadSource source) =>
                Arcs.Add((points.ToList(), parameters, layer, source));

            public void Text(double x, double y, double height, string value, string layer, CadSource source) =>
                Texts.Add((x, y, height, value, layer, source));
        }

        [Fact]
        public void ProxyPolyline_EmitsOpenPolyline()
        {
            var sink = new RecordingSink();
            var poly = new ProxyPolyline { Points = new List<XYZ> { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0) } };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { poly }, NoLayer, "L1", Identity, sink);

            var (points, isClosed, layer, source) = Assert.Single(sink.Polylines);
            Assert.False(isClosed);
            Assert.Equal("L1", layer);
            Assert.Equal(CadSource.Proxy, source);
            Assert.Equal(3, points.Count);
            Assert.Equal((10, 10), points[2]);
        }

        [Fact]
        public void ProxyPolygon_EmitsClosedPolyline()
        {
            var sink = new RecordingSink();
            var polygon = new ProxyPolygon {
                Points = new List<XYZ> { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0) },
            };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { polygon }, NoLayer, "L1", Identity, sink);

            Assert.True(Assert.Single(sink.Polylines).IsClosed);
        }

        [Fact]
        public void ProxyCircularArc_EmitsArcWithParams()
        {
            var sink = new RecordingSink();
            var arc = new ProxyCircularArc {
                Center = new XYZ(0, 0, 0), Radius = 100,
                StartVectorDirection = new XYZ(1, 0, 0), SweepAngle = Math.PI / 2,
            };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { arc }, NoLayer, "L1", Identity, sink);

            var (points, parameters, _, source) = Assert.Single(sink.Arcs);
            Assert.Equal(CadSource.Proxy, source);
            Assert.True(parameters.HasValue);
            Assert.Equal(0, parameters.Value.CenterX, 6);
            Assert.Equal(0, parameters.Value.CenterY, 6);
            Assert.Equal(100, parameters.Value.Radius, 6);
            Assert.Equal(0, parameters.Value.StartAngle, 6);
            Assert.Equal(Math.PI / 2, parameters.Value.EndAngle, 6);
            Assert.Equal(100, points[0].X, 6);
            Assert.Equal(0, points[0].Y, 6);
            Assert.Equal(0, points[points.Count - 1].X, 3);
            Assert.Equal(100, points[points.Count - 1].Y, 3);
        }

        [Fact]
        public void ProxyCircle_EmitsClosedPolylineAroundCenter()
        {
            var sink = new RecordingSink();
            var circle = new ProxyCircle { Center = new XYZ(10, 20, 0), Radius = 50 };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { circle }, NoLayer, "L1", Identity, sink);

            var (points, isClosed, _, _) = Assert.Single(sink.Polylines);
            Assert.True(isClosed);
            Assert.Equal(60, points[0].X, 6);
            Assert.Equal(20, points[0].Y, 6);
        }

        [Fact]
        public void ProxyCirclePt3_FitsCircleThroughThreePoints()
        {
            var sink = new RecordingSink();
            var circlePt3 = new ProxyCirclePt3 {
                Point1 = new XYZ(10, 0, 0), Point2 = new XYZ(0, 10, 0), Point3 = new XYZ(-10, 0, 0),
            };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { circlePt3 }, NoLayer, "L1", Identity, sink);

            var points = Assert.Single(sink.Polylines).Points;
            Assert.All(points, p => Assert.Equal(10, Math.Sqrt((p.X * p.X) + (p.Y * p.Y)), 3));
        }

        [Fact]
        public void ProxyCircularArc3Pt_SweepsThroughTheMiddlePoint()
        {
            var sink = new RecordingSink();
            var arc3Pt = new ProxyCircularArc3Pt {
                Point1 = new XYZ(10, 0, 0), Point2 = new XYZ(0, 10, 0), Point3 = new XYZ(-10, 0, 0),
            };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { arc3Pt }, NoLayer, "L1", Identity, sink);

            var (_, parameters, _, _) = Assert.Single(sink.Arcs);
            Assert.True(parameters.HasValue);
            Assert.Equal(10, parameters.Value.Radius, 6);
            Assert.Equal(0, parameters.Value.StartAngle, 6);
            Assert.Equal(Math.PI, parameters.Value.EndAngle, 6);
        }

        [Fact]
        public void PushPopModelTransform_AppliesThenRestoresTheTransform()
        {
            var sink = new RecordingSink();
            var push = new ProxyPushModelTransform { TransformationMatrix = Matrix4.CreateTranslation(100, 50, 0) };
            var pop = new ProxyPopModelTransform();
            var inner = new ProxyPolyline { Points = new List<XYZ> { new(0, 0, 0), new(1, 1, 0) } };
            var outer = new ProxyPolyline { Points = new List<XYZ> { new(0, 0, 0), new(1, 1, 0) } };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { push, inner, pop, outer }, NoLayer, "L1", Identity, sink);

            Assert.Equal(2, sink.Polylines.Count);
            Assert.Equal((100, 50), sink.Polylines[0].Points[0]);
            Assert.Equal((0, 0), sink.Polylines[1].Points[0]);
        }

        [Fact]
        public void ProxySubentLayer_OverridesTheLayerForWhatFollows()
        {
            var sink = new RecordingSink();
            Func<ulong, string> layerByHandle = handle => handle == 42UL ? "OVERRIDE" : null;
            var subentLayer = new ProxySubentLayer { LayerIndex = 42 };
            var polyline = new ProxyPolyline { Points = new List<XYZ> { new(0, 0, 0), new(1, 1, 0) } };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { subentLayer, polyline }, layerByHandle, "FALLBACK", Identity, sink);

            Assert.Equal("OVERRIDE", Assert.Single(sink.Polylines).Layer);
        }

        [Fact]
        public void UnrecognisedPrimitive_ProducesNoLinework()
        {
            var sink = new RecordingSink();
            var color = new ProxySubentColor { ColorIndex = 3 };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { color }, NoLayer, "L1", Identity, sink);

            Assert.Empty(sink.Polylines);
            Assert.Empty(sink.Arcs);
            Assert.Empty(sink.Texts);
        }

        [Fact]
        public void ProxyShell_EmitsOneClosedPolylinePerFace()
        {
            var sink = new RecordingSink();
            var shell = new ProxyShell {
                Vertices = new List<XYZ> { new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0) },
                Faces = new List<int[]> { new[] { 0, 1, 2, 3 } },
            };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { shell }, NoLayer, "L1", Identity, sink);

            var (points, isClosed, _, _) = Assert.Single(sink.Polylines);
            Assert.True(isClosed);
            Assert.Equal(4, points.Count);
        }

        [Fact]
        public void ProxyMesh_EmitsOneQuadPerGridCell()
        {
            var sink = new RecordingSink();
            var mesh = new ProxyMesh {
                RowCount = 2, ColumnCount = 2,
                Vertices = new List<XYZ> { new(0, 0, 0), new(10, 0, 0), new(0, 10, 0), new(10, 10, 0) },
            };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { mesh }, NoLayer, "L1", Identity, sink);

            Assert.Equal(4, Assert.Single(sink.Polylines).Points.Count);
        }

        [Fact]
        public void ProxyText_ReachesTheSink()
        {
            var sink = new RecordingSink();
            var text = new ProxyText { StartPoint = new XYZ(5, 5, 0), Height = 3, Text = "ROOM" };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { text }, NoLayer, "L1", Identity, sink);

            var (x, y, _, value, _, source) = Assert.Single(sink.Texts);
            Assert.Equal("ROOM", value);
            Assert.Equal(5, x, 6);
            Assert.Equal(5, y, 6);
            Assert.Equal(CadSource.Proxy, source);
        }

        [Fact]
        public void ProxyLwPolyine_ReadsItsWrappedLwPolylineVertices()
        {
            var sink = new RecordingSink();
            var lwPolyline = new LwPolyline(new[] {
                new LwPolyline.Vertex(0, 0),
                new LwPolyline.Vertex(10, 0),
                new LwPolyline.Vertex(10, 10),
            });
            var wrapped = new ProxyLwPolyine { Entity = lwPolyline };

            CadRenderSource.EmitProxy(new IProxyGeometry[] { wrapped }, NoLayer, "L1", Identity, sink);

            var (points, _, _, source) = Assert.Single(sink.Polylines);
            Assert.Equal(3, points.Count);
            Assert.Equal(CadSource.Proxy, source);
        }

        [Fact]
        public void Walk_RoutesAProxyEntityThroughEmitProxy_WithHandleBasedLayerResolution()
        {
            var document = new CadDocument();
            var layer = new Layer("PROXYLAYER");
            document.Layers.Add(layer);

            var entity = new ProxyEntity();
            entity.ProxyGeometries.Add(new ProxyPolyline { Points = new List<XYZ> { new(1, 2, 0), new(3, 4, 0) } });
            entity.ProxyGeometries.Add(new ProxySubentLayer { LayerIndex = (int)layer.Handle });
            entity.ProxyGeometries.Add(new ProxyPolyline { Points = new List<XYZ> { new(5, 6, 0), new(7, 8, 0) } });
            document.Entities.Add(entity);

            var sink = new RecordingSink();
            CadRenderSource.Walk(document, sink);

            Assert.Equal(2, sink.Polylines.Count);
            Assert.Equal(CadSource.Proxy, sink.Polylines[0].Source);
            Assert.Equal("PROXYLAYER", sink.Polylines[1].Layer);
        }
    }
}
