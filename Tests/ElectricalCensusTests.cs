// ElectricalCensus: one walk of a drawing counted into block classes, layers and drawing
// facts. Every drawing here is built in memory (CensusFixture); one test round-trips a DXF
// the test writes itself. No real drawing is ever read or checked in.

using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using BinaVibe.Mcp.Tools.Electrical;
using CSMath;
using Xunit;
using static Tests.CensusFixture;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class ElectricalCensusTests
    {
        private static CadDocument MmDrawing()
        {
            var doc = new CadDocument();
            doc.Header.InsUnits = UnitsType.Millimeters;
            // A 30 m x 20 m frame so the drawing reads as a building in millimetres.
            Line(doc, "FRAME", 0, 0, 30000, 0);
            Line(doc, "FRAME", 0, 20000, 30000, 20000);
            return doc;
        }

        private static CensusLayer LayerOf(ElectricalCensusResult census, string name) =>
            census.Layers.Single(layer => layer.Name == name);

        [Fact]
        public void Counts_instances_per_block_and_lists_their_layers()
        {
            CadDocument doc = MmDrawing();
            BlockRecord socket = SquareBlock(doc, "SOCKET-13A", 100);
            BlockRecord light = SquareBlock(doc, "DOWNLIGHT", 200);
            for (int i = 0; i < 5; i++) Insert(doc, socket, 1000 + (i * 500), 1000, "E-POWER");
            Insert(doc, socket, 9000, 1000, "E-LIGHT");
            Insert(doc, light, 5000, 5000, "E-LIGHT");

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            CensusBlockClass skt = census.Blocks[0];
            Assert.Equal("SOCKET-13A", skt.Key);
            Assert.Equal(6, skt.Count);
            Assert.Equal("E-POWER", skt.Layers[0].Name);
            Assert.Equal(5, skt.Layers[0].Count);
            Assert.Equal("E-LIGHT", skt.Layers[1].Name);
            Assert.Equal(100, skt.WidthMm, 6);
            Assert.False(skt.Anonymous);
            Assert.False(skt.IsContainer);
            Assert.Equal(1, census.Blocks.Single(b => b.Key == "DOWNLIGHT").Count);
            Assert.Equal(7, census.Totals.Inserts);
            Assert.Equal(7, census.Totals.Named);
        }

        private static BlockRecord AnonymousSquare(CadDocument doc, string name, double size)
        {
            double h = size / 2;
            var record = new BlockRecord(name) { IsAnonymous = true };
            record.Entities.Add(new Line(new XYZ(-h, -h, 0), new XYZ(h, -h, 0)));
            record.Entities.Add(new Line(new XYZ(h, -h, 0), new XYZ(h, h, 0)));
            record.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = h / 2 });
            doc.BlockRecords.Add(record);
            return record;
        }

        [Fact]
        public void Anonymous_blocks_with_the_same_shape_are_one_class()
        {
            CadDocument doc = MmDrawing();
            Insert(doc, AnonymousSquare(doc, "*U1", 300), 1000, 1000, "E-POWER");
            Insert(doc, AnonymousSquare(doc, "*U2", 300), 2000, 1000, "E-POWER");
            Insert(doc, AnonymousSquare(doc, "*U3", 600), 3000, 1000, "E-POWER");

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            List<CensusBlockClass> anonymous = census.Blocks.Where(b => b.Anonymous).ToList();
            Assert.Equal(2, anonymous.Count);
            CensusBlockClass pair = anonymous.Single(b => b.Count == 2);
            Assert.StartsWith("anon:", pair.Key, StringComparison.Ordinal);
            Assert.Equal(13, pair.Key.Length);
            Assert.Equal(2, pair.RecordNames.Count);
            Assert.Equal(3, census.Totals.Anonymous);
        }

        [Fact]
        public void Anonymous_extent_is_rounded_to_10mm_for_the_signature()
        {
            CadDocument doc = MmDrawing();
            Insert(doc, AnonymousSquare(doc, "*U1", 300), 1000, 1000, "E-POWER");
            Insert(doc, AnonymousSquare(doc, "*U2", 302), 2000, 1000, "E-POWER");
            Insert(doc, AnonymousSquare(doc, "*U3", 320), 3000, 1000, "E-POWER");

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            List<CensusBlockClass> anonymous = census.Blocks.Where(b => b.Anonymous).ToList();
            Assert.Equal(2, anonymous.Count);
            Assert.Equal(2, anonymous[0].Count);
        }

        [Fact]
        public void Insert_on_layer_0_inside_a_block_takes_the_parents_layer()
        {
            CadDocument doc = MmDrawing();
            BlockRecord fitting = SquareBlock(doc, "LUM-LED-A", 1200);
            var group = new BlockRecord("ROW-OF-LIGHTS");
            group.Entities.Add(MakeInsert(doc, fitting, 0, 0, "0"));
            group.Entities.Add(MakeInsert(doc, fitting, 2000, 0, "E-EMERGENCY"));
            doc.BlockRecords.Add(group);
            Insert(doc, group, 5000, 5000, "E-LIGHT");

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            CensusBlockClass fl = census.Blocks.Single(b => b.Key == "LUM-LED-A");
            Assert.Equal(2, fl.Count);
            Assert.Contains(fl.Layers, l => l.Name == "E-LIGHT" && l.Count == 1);
            Assert.Contains(fl.Layers, l => l.Name == "E-EMERGENCY" && l.Count == 1);
            Assert.DoesNotContain(fl.Layers, l => l.Name == "0");
            Assert.Equal(1, fl.MinDepth);
        }

        [Fact]
        public void Container_block_is_flagged_and_its_children_still_counted()
        {
            CadDocument doc = MmDrawing();
            BlockRecord socket = SquareBlock(doc, "SOCKET-13A", 100);
            var board = new BlockRecord("KITCHEN-SET");
            board.Entities.Add(MakeInsert(doc, socket, 0, 0, "E-POWER"));
            board.Entities.Add(MakeInsert(doc, socket, 300, 0, "E-POWER"));
            doc.BlockRecords.Add(board);
            Insert(doc, board, 5000, 5000, "E-POWER");
            Insert(doc, board, 8000, 5000, "E-POWER");

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            CensusBlockClass set = census.Blocks.Single(b => b.Key == "KITCHEN-SET");
            Assert.True(set.IsContainer);
            Assert.Equal(0, set.MaxDepth);
            CensusBlockClass skt = census.Blocks.Single(b => b.Key == "SOCKET-13A");
            Assert.False(skt.IsContainer);
            Assert.Equal(4, skt.Count);
            Assert.Equal(4, census.Totals.Nested);
            Assert.Equal(2, census.Totals.TopLevel);
        }

        [Fact]
        public void Attribute_tags_keep_three_sample_values()
        {
            CadDocument doc = MmDrawing();
            BlockRecord board = SquareBlock(doc, "DB", 400);
            foreach (string name in new[] { "DB-1", "DB-2", "DB-2", "DB-3", "DB-4" })
            {
                Insert insert = Insert(doc, board, 1000, 1000, "E-POWER");
                insert.Attributes.Add(new AttributeEntity { Tag = "TAG", Value = name });
                insert.Attributes.Add(new AttributeEntity { Tag = "RATING", Value = "63A" });
            }

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            CensusBlockClass db = census.Blocks.Single(b => b.Key == "DB");
            CensusAttribute tag = db.Attributes.Single(a => a.Tag == "TAG");
            Assert.Equal(new[] { "DB-1", "DB-2", "DB-3" }, tag.Samples);
            Assert.Equal(new[] { "63A" }, db.Attributes.Single(a => a.Tag == "RATING").Samples);
        }

        [Fact]
        public void Layer_counts_split_lines_polylines_arcs_texts_inserts()
        {
            CadDocument doc = MmDrawing();
            string layer = "E-POWER";
            Line(doc, layer, 0, 0, 1000, 0);
            Line(doc, layer, 0, 500, 1000, 500);
            var open = new LwPolyline { Layer = Layer(doc, layer) };
            open.Vertices.Add(new LwPolyline.Vertex(new XY(0, 1000)));
            open.Vertices.Add(new LwPolyline.Vertex(new XY(1000, 1000)));
            open.Vertices.Add(new LwPolyline.Vertex(new XY(1000, 2000)));
            doc.Entities.Add(open);
            var closed = new LwPolyline { Layer = Layer(doc, layer), IsClosed = true };
            closed.Vertices.Add(new LwPolyline.Vertex(new XY(0, 3000)));
            closed.Vertices.Add(new LwPolyline.Vertex(new XY(100, 3000)));
            closed.Vertices.Add(new LwPolyline.Vertex(new XY(100, 3100)));
            doc.Entities.Add(closed);
            doc.Entities.Add(new Arc { Center = new XYZ(5000, 5000, 0), Radius = 500, StartAngle = 0, EndAngle = Math.PI, Layer = Layer(doc, layer) });
            Text(doc, layer, 2000, 2000, "13A");
            Insert(doc, SquareBlock(doc, "SOCKET", 100), 3000, 3000, layer);

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            CensusLayer power = LayerOf(census, layer);
            Assert.Equal(2, power.Lines);
            Assert.Equal(1, power.OpenPolylines);
            Assert.Equal(1, power.ClosedPolylines);
            Assert.Equal(1, power.Arcs);
            Assert.Equal(1, power.Texts);
            Assert.Equal(1, power.Inserts);
            // 1 + 1 + 2 + 0.341 m of straight line and half a 500 mm circle loose; the insert's
            // own square sits on layer 0, so it is not counted here.
            Assert.Equal(4.0 + 0.2 + Math.Sqrt(0.02) + (Math.PI * 0.5), power.LooseLengthM, 2);
            Assert.Equal(0, power.InBlocksLengthM, 6);
        }

        [Fact]
        public void Open_polylines_over_1m_are_counted()
        {
            CadDocument doc = MmDrawing();
            string layer = "E-CONDUIT";
            Line(doc, layer, 0, 0, 900, 0);
            Line(doc, layer, 0, 1000, 1500, 1000);
            var route = new LwPolyline { Layer = Layer(doc, layer) };
            route.Vertices.Add(new LwPolyline.Vertex(new XY(0, 2000)));
            route.Vertices.Add(new LwPolyline.Vertex(new XY(600, 2000)));
            route.Vertices.Add(new LwPolyline.Vertex(new XY(600, 2600)));
            doc.Entities.Add(route);

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            Assert.Equal(2, LayerOf(census, layer).OpenPolylinesOver1m);
        }

        [Fact]
        public void Axis_aligned_share_separates_routes_from_arcs()
        {
            CadDocument doc = MmDrawing();
            for (int i = 0; i < 5; i++)
            {
                Line(doc, "E-CONDUIT", 0, i * 1000, 5000, i * 1000);
                Line(doc, "E-CONDUIT", 5000, i * 1000, 5000, (i * 1000) + 800);
                doc.Entities.Add(new Arc
                {
                    Center = new XYZ(10000, i * 1000, 0), Radius = 800, StartAngle = 0.3, EndAngle = 2.8,
                    Layer = Layer(doc, "E-WIRING"),
                });
            }

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            Assert.True(LayerOf(census, "E-CONDUIT").AxisAlignedShare >= 0.99);
            Assert.True(LayerOf(census, "E-WIRING").AxisAlignedShare < 0.2);
        }

        [Fact]
        public void Units_report_inferred_when_the_header_lies()
        {
            var doc = new CadDocument();
            doc.Header.InsUnits = UnitsType.Inches;
            Line(doc, "FRAME", 0, 0, 40000, 0);
            Line(doc, "FRAME", 0, 30000, 40000, 30000);

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            Assert.Equal(1.0, census.Drawing.MmPerUnit);
            Assert.Equal("inferred", census.Drawing.UnitsSource);
            Assert.Equal("Inches", census.Drawing.HeaderUnits);
            Assert.Equal(25.4, census.Drawing.HeaderMmPerUnit);
            Assert.Equal(40, census.Drawing.WidthM, 6);
        }

        [Fact]
        public void Units_report_header_when_it_is_believable()
        {
            var doc = new CadDocument();
            doc.Header.InsUnits = UnitsType.Meters;
            Line(doc, "FRAME", 0, 0, 40, 0);
            Line(doc, "FRAME", 0, 30, 40, 30);
            Line(doc, "E-CONDUIT", 0, 10, 10, 10);

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            Assert.Equal(1000.0, census.Drawing.MmPerUnit);
            Assert.Equal("header", census.Drawing.UnitsSource);
            Assert.Equal(10, LayerOf(census, "E-CONDUIT").LengthM, 6);
            Assert.Equal(1, LayerOf(census, "E-CONDUIT").OpenPolylinesOver1m);
        }
    }
}
