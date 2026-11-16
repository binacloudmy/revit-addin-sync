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
