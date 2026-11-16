// CadRenderSource.EmitInsert honours an INSERT's extrusion direction. An insert with
// extrusion (0,0,-1) stores its insert point and rotation in an OCS whose X runs along
// world -X; read as world coordinates the block lands mirrored through x = 0. These tests
// build the inserts by hand and drive CadRenderSource.Walk, so no DWG is needed.

using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Cad2Bim.Services;
using CSMath;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimInsertOcsTests
    {
        private sealed class LineSink : ICadSink
        {
            public List<List<(double X, double Y)>> Polylines { get; } = new();

            public void Polyline(IReadOnlyList<(double X, double Y)> points, bool isClosed, string layer, CadSource source) =>
                Polylines.Add(points.ToList());

            public void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters, string layer, CadSource source) { }

            public void Text(double x, double y, double height, string value, string layer, CadSource source) { }
        }

        private static BlockRecord BlockWithLine(CadDocument doc, string name, XYZ from, XYZ to)
        {
            var record = new BlockRecord(name);
            record.Entities.Add(new Line(from, to));
            doc.BlockRecords.Add(record);
            return record;
        }

        private static void AssertNear((double X, double Y) expected, (double X, double Y) actual)
        {
            Assert.Equal(expected.X, actual.X, 6);
            Assert.Equal(expected.Y, actual.Y, 6);
        }

        [Fact]
        public void OcsXform_of_plus_Z_is_identity()
        {
            Assert.Equal(CadRenderSource.Xform.Identity, CadRenderSource.OcsXform(new XYZ(0, 0, 1)));
        }

        [Fact]
        public void OcsXform_of_minus_Z_mirrors_x()
        {
            CadRenderSource.Xform ocs = CadRenderSource.OcsXform(new XYZ(0, 0, -1));

            AssertNear((-3, 4), ocs.Apply(3, 4));
        }

        [Fact]
        public void A_minus_Z_insert_lands_where_it_is_drawn()
        {
            // The Two-Bedrooms 'nevera' insert: point (-767.7, 675) in OCS, X scale -1,
            // rotation 90, extrusion -Z. In world it sits at x ~ +794, not -794.
            var doc = new CadDocument();
            BlockRecord block = BlockWithLine(doc, "NEVERA", new XYZ(226, 26, 0), new XYZ(227, 26, 0));
            doc.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(-767.7, 675, 0),
                XScale = -1,
                Rotation = Math.PI / 2,
                Normal = new XYZ(0, 0, -1),
            });

            var sink = new LineSink();
            CadRenderSource.Walk(doc, sink);

            List<(double X, double Y)> line = Assert.Single(sink.Polylines);
            AssertNear((793.7, 449), line[0]);
            AssertNear((793.7, 448), line[1]);
        }

        [Fact]
        public void A_plus_Z_insert_is_unchanged()
        {
            var doc = new CadDocument();
            BlockRecord block = BlockWithLine(doc, "PLAIN", new XYZ(0, 0, 0), new XYZ(10, 0, 0));
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(100, 50, 0), Rotation = Math.PI / 2 });

            var sink = new LineSink();
            CadRenderSource.Walk(doc, sink);

            List<(double X, double Y)> line = Assert.Single(sink.Polylines);
            AssertNear((100, 50), line[0]);
            AssertNear((100, 60), line[1]);
        }

        private sealed class ArcSink : ICadSink
        {
            public List<(List<(double X, double Y)> Points, ArcParams? Parameters)> Arcs { get; } = new();

            public void Polyline(IReadOnlyList<(double X, double Y)> points, bool isClosed, string layer, CadSource source) { }

            public void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters, string layer, CadSource source) =>
                Arcs.Add((points.ToList(), parameters));

            public void Text(double x, double y, double height, string value, string layer, CadSource source) { }
        }

        private static (double X, double Y) At(ArcParams arc, double angle) =>
            (arc.CenterX + (arc.Radius * Math.Cos(angle)), arc.CenterY + (arc.Radius * Math.Sin(angle)));

        [Fact]
        public void A_quarter_arc_in_a_mirrored_insert_stays_an_arc_on_the_mirrored_side()
        {
            // Door swing 0..90 deg, r 900, hinge at the block origin; inserted at (5000,1000)
            // with X scale -1. Mirrored it sweeps 90..180 deg: from (5000,1900) to (4100,1000).
            var doc = new CadDocument();
            var block = new BlockRecord("DOOR");
            block.Entities.Add(new ACadSharp.Entities.Arc
            {
                Center = new XYZ(0, 0, 0), Radius = 900, StartAngle = 0, EndAngle = Math.PI / 2,
            });
            doc.BlockRecords.Add(block);
            doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(5000, 1000, 0), XScale = -1 });

            var sink = new ArcSink();
            CadRenderSource.Walk(doc, sink);

            var (points, parameters) = Assert.Single(sink.Arcs);
            ArcParams arc = Assert.NotNull(parameters);
            Assert.Equal(5000, arc.CenterX, 6);
            Assert.Equal(1000, arc.CenterY, 6);
            Assert.Equal(900, arc.Radius, 6);
            Assert.Equal(90, (arc.EndAngle - arc.StartAngle) * 180 / Math.PI, 9);
            AssertNear((5000, 1900), At(arc, arc.StartAngle));
            AssertNear((4100, 1000), At(arc, arc.EndAngle));

            // The tessellated points run the drawn way round, so the ends are swapped.
            AssertNear(At(arc, arc.EndAngle), points[0]);
            AssertNear(At(arc, arc.StartAngle), points[^1]);
        }

        [Fact]
        public void MapAngles_of_a_rotated_reflection_matches_the_mapped_points()
        {
            // Reflection across the line at 15 deg composed with scale 2: (A,B,C,D) =
            // 2 * (cos 30, sin 30, sin 30, -cos 30).
            double phi = Math.PI / 6;
            var xform = new CadRenderSource.Xform(2 * Math.Cos(phi), 2 * Math.Sin(phi),
                                                  2 * Math.Sin(phi), -2 * Math.Cos(phi), 10, 20);

            Assert.True(xform.TryConformal(out double scale, out double rotation, out bool mirrored));
            Assert.True(mirrored);
            Assert.Equal(2, scale, 9);

            double start = 10 * Math.PI / 180, end = 70 * Math.PI / 180;
            var (mappedStart, mappedEnd) = CadRenderSource.Xform.MapAngles(start, end, rotation, mirrored);
            var (cx, cy) = xform.Apply(0, 0);
            var arc = new ArcParams(cx, cy, 2, mappedStart, mappedEnd);

            AssertNear(xform.Apply(Math.Cos(end), Math.Sin(end)), At(arc, mappedStart));
            AssertNear(xform.Apply(Math.Cos(start), Math.Sin(start)), At(arc, mappedEnd));
            AssertNear(xform.Apply(Math.Cos((start + end) / 2), Math.Sin((start + end) / 2)), At(arc, (mappedStart + mappedEnd) / 2));
            Assert.Equal(60, (mappedEnd - mappedStart) * 180 / Math.PI, 9);
        }

        [Fact]
        public void A_sheared_map_still_turns_arcs_into_chords()
        {
            var shear = new CadRenderSource.Xform(1, 0, 0.5, 1, 0, 0);

            Assert.False(shear.TryConformal(out _, out _, out _));
        }

        [Fact]
        public void A_minus_Z_insert_nested_in_a_plus_Z_insert_is_mirrored_inside_its_parent()
        {
            // Child: a line (0,0)-(10,0), inserted at (5,0) with extrusion -Z -> in parent space
            // it runs (-5,0)-(-15,0). Parent: inserted at (1000,2000), no rotation.
            var doc = new CadDocument();
            BlockRecord child = BlockWithLine(doc, "CHILD", new XYZ(0, 0, 0), new XYZ(10, 0, 0));
            var parent = new BlockRecord("PARENT");
            parent.Entities.Add(new Insert(child) { InsertPoint = new XYZ(5, 0, 0), Normal = new XYZ(0, 0, -1) });
            doc.BlockRecords.Add(parent);
            doc.Entities.Add(new Insert(parent) { InsertPoint = new XYZ(1000, 2000, 0) });

            var sink = new LineSink();
            CadRenderSource.Walk(doc, sink);

            List<(double X, double Y)> line = Assert.Single(sink.Polylines);
            AssertNear((995, 2000), line[0]);
            AssertNear((985, 2000), line[1]);
        }
    }
}
