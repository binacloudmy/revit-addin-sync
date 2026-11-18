#nullable enable
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
        public void Linework_and_text_on_layer_0_inside_a_block_take_the_inserts_layer()
        {
            // PARENT holds a CHILD insert on E-EMERGENCY and then, after it, its own line,
            // arc and text on layer 0. CHILD's line is on layer 0 too. The parent's pieces belong
            // to the layer PARENT is placed on, the child's to the child's, and none to "0".
            CadDocument doc = MmDrawing();
            var child = new BlockRecord("CHILD");
            child.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(1000, 0, 0)) { Layer = Layer(doc, "0") });
            doc.BlockRecords.Add(child);

            var parent = new BlockRecord("PARENT");
            parent.Entities.Add(MakeInsert(doc, child, 0, 0, "E-EMERGENCY"));
            parent.Entities.Add(new Line(new XYZ(0, 500, 0), new XYZ(2000, 500, 0)) { Layer = Layer(doc, "0") });
            parent.Entities.Add(new Arc { Center = new XYZ(0, 0, 0), Radius = 100, StartAngle = 0, EndAngle = Math.PI, Layer = Layer(doc, "0") });
            parent.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 800, 0), Height = 100, Value = "L1", Layer = Layer(doc, "0") });
            doc.BlockRecords.Add(parent);
            Insert(doc, parent, 5000, 5000, "E-LIGHT");
            Line(doc, "0", 0, 10000, 3000, 10000); // loose, in model space: stays on "0"

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            CensusLayer light = LayerOf(census, "E-LIGHT");
            Assert.Equal(1, light.Lines);
            Assert.Equal(1, light.Arcs);
            Assert.Equal(1, light.Texts);
            Assert.Equal(2.0 + (Math.PI * 0.1), light.InBlocksLengthM, 3);

            CensusLayer emergency = LayerOf(census, "E-EMERGENCY");
            Assert.Equal(1, emergency.Lines);
            Assert.Equal(1.0, emergency.InBlocksLengthM, 6);

            CensusLayer zero = LayerOf(census, "0");
            Assert.Equal(1, zero.Lines);
            Assert.Equal(0, zero.Arcs);
            Assert.Equal(0, zero.Texts);
            Assert.Equal(0, zero.InBlocksLengthM, 6);
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
            Assert.Equal(6, power.Lines); // two loose, four in the insert's square
            Assert.Equal(1, power.OpenPolylines);
            Assert.Equal(1, power.ClosedPolylines);
            Assert.Equal(1, power.Arcs);
            Assert.Equal(1, power.Texts);
            Assert.Equal(1, power.Inserts);
            // 1 + 1 + 2 + 0.341 m of straight line and half a 500 mm circle loose; the insert's
            // own square is on layer 0, so it is drawn on the insert's layer, in a block.
            Assert.Equal(4.0 + 0.2 + Math.Sqrt(0.02) + (Math.PI * 0.5), power.LooseLengthM, 2);
            Assert.Equal(0.4, power.InBlocksLengthM, 6);
        }

        [Fact]
        public void A_nan_vertex_is_skipped_and_never_reaches_the_json()
        {
            CadDocument doc = MmDrawing();
            string layer = "E-CONDUIT";
            var route = new LwPolyline { Layer = Layer(doc, layer) };
            route.Vertices.Add(new LwPolyline.Vertex(new XY(0, 1000)));
            route.Vertices.Add(new LwPolyline.Vertex(new XY(2000, 1000)));
            route.Vertices.Add(new LwPolyline.Vertex(new XY(double.NaN, 1000)));
            route.Vertices.Add(new LwPolyline.Vertex(new XY(2000, 3000)));
            route.Vertices.Add(new LwPolyline.Vertex(new XY(2000, double.PositiveInfinity)));
            doc.Entities.Add(route);
            doc.Entities.Add(new Arc { Center = new XYZ(double.NaN, 0, 0), Radius = 500, StartAngle = 0, EndAngle = Math.PI, Layer = Layer(doc, layer) });
            Text(doc, layer, double.NaN, 500, "13A");
            BlockRecord socket = SquareBlock(doc, "SOCKET", 100);
            Insert(doc, socket, double.NaN, 3000, "E-POWER");
            Insert(doc, socket, 3000, 3000, "E-POWER");

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            CensusLayer conduit = LayerOf(census, layer);
            Assert.Equal(2.0, conduit.LengthM, 6);
            Assert.False(double.IsNaN(conduit.AxisAlignedShare));
            Assert.Equal(30, census.Drawing.WidthM, 6);
            Assert.Equal(2, census.Blocks.Single(b => b.Key == "SOCKET").Count);

            string json = System.Text.Json.JsonSerializer.Serialize(census.ToWire());
            Assert.DoesNotContain("NaN", json);
            Assert.DoesNotContain("Infinity", json);
        }

        [Fact]
        public void Wire_replaces_a_non_finite_number_with_null()
        {
            var census = new ElectricalCensusResult();
            census.Drawing.MmPerUnit = double.NaN;
            census.Layers.Add(new CensusLayer { Name = "X", LengthM = double.PositiveInfinity });
            census.TimingsSeconds["walk"] = double.NaN;

            string json = System.Text.Json.JsonSerializer.Serialize(census.ToWire());

            Assert.Contains("\"mm_per_unit\":null", json);
            Assert.Contains("\"total\":null", json);
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

        /// <summary>Two 6 x 5 m rooms of double-line walls, the second 30 m to the right.</summary>
        private static CadDocument TwoRooms(string wallLayer = "A-WALL")
        {
            CadDocument doc = MmDrawing();
            Room(doc, wallLayer, 0, 0, 6000, 5000);
            Room(doc, wallLayer, 36000, 0, 6000, 5000);
            return doc;
        }

        [Fact]
        public void Wall_layer_pairs_like_walls_and_a_conduit_layer_does_not()
        {
            CadDocument doc = TwoRooms();
            // Conduit: single orthogonal runs, nowhere near a parallel partner.
            for (int i = 0; i < 10; i++) Line(doc, "E-CONDUIT", 1000, 1000 + (i * 1000), 5000, 1000 + (i * 1000));

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            CensusLayer wall = LayerOf(census, "A-WALL");
            Assert.True(wall.WallPairs >= 8, "pairs " + wall.WallPairs);
            Assert.True(wall.PairedLengthShare >= 0.25, "share " + wall.PairedLengthShare);
            Assert.True(wall.PairsLikeWalls);
            Assert.True(wall.WallPairsUnmerged >= 8);
            Assert.Null(wall.WallsSkipped);

            CensusLayer conduit = LayerOf(census, "E-CONDUIT");
            Assert.Equal(0, conduit.WallPairs);
            Assert.False(conduit.PairsLikeWalls);
        }

        [Fact]
        public void Wall_test_uses_its_own_bounds_and_leaves_the_shared_ones_alone()
        {
            // A pane set to 1-2 m walls must neither stop the census finding 150 mm walls nor
            // be changed by it, even for a moment.
            double savedMin = Cad2Bim.Wall.SMin, savedMax = Cad2Bim.Wall.SMax;
            Cad2Bim.Wall.SMin = 1000;
            Cad2Bim.Wall.SMax = 2000;
            try
            {
                var faces = new List<Cad2Bim.Segment>
                {
                    new Cad2Bim.Segment(new Cad2Bim.Point(0, 0), new Cad2Bim.Point(3000, 0)),
                    new Cad2Bim.Segment(new Cad2Bim.Point(0, 150), new Cad2Bim.Point(3000, 150)),
                };
                Assert.Single(Cad2Bim.CadClassifier.ClassifyWalls(faces, 50, 400));
                Assert.Empty(Cad2Bim.CadClassifier.ClassifyWalls(faces));

                ElectricalCensusResult census = ElectricalCensus.Build(TwoRooms());

                Assert.True(LayerOf(census, "A-WALL").PairsLikeWalls);
                Assert.Equal(1000, Cad2Bim.Wall.SMin);
                Assert.Equal(2000, Cad2Bim.Wall.SMax);
            }
            finally
            {
                Cad2Bim.Wall.SMin = savedMin;
                Cad2Bim.Wall.SMax = savedMax;
            }
        }

        [Fact]
        public void A_layer_over_the_segment_cap_is_skipped()
        {
            CadDocument doc = TwoRooms();
            ElectricalCensusResult census = ElectricalCensus.Build(doc, new CensusOptions { MaxWallTestSegments = 4 });

            CensusLayer wall = LayerOf(census, "A-WALL");
            Assert.Equal("too_many_segments", wall.WallsSkipped);
            Assert.False(wall.PairsLikeWalls);
        }

        [Fact]
        public void Near_wall_counts_instances_within_400mm()
        {
            CadDocument doc = TwoRooms();
            BlockRecord socket = SquareBlock(doc, "SOCKET-13A", 100);
            Insert(doc, socket, 350, 2500, "E-POWER");    // 200 mm off the left wall's inner face
            Insert(doc, socket, 3000, 4500, "E-POWER");   // 350 mm below the top wall's inner face
            Insert(doc, socket, 3000, 2500, "E-POWER");   // the middle of the room

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            Assert.Equal(2, census.Blocks.Single(b => b.Key == "SOCKET-13A").NearWall);
        }

        [Fact]
        public void Two_rooms_30m_apart_are_two_plans_with_their_titles()
        {
            CadDocument doc = TwoRooms();
            Text(doc, "A-TEXT", 1000, -600, "PELAN LANTAI TINGKAT 1", 500);
            Text(doc, "A-TEXT", 1000, 2500, "BILIK TIDUR", 200);
            Text(doc, "A-TEXT", 37000, -600, "Pelan Bumbung", 400);
            BlockRecord light = SquareBlock(doc, "DOWNLIGHT", 200);
            Insert(doc, light, 3000, 2500, "E-LIGHT");
            Insert(doc, light, 39000, 2500, "E-LIGHT");
            Insert(doc, light, 40000, 2500, "E-LIGHT");
            Insert(doc, light, 20000, 15000, "E-LIGHT"); // on the sheet, in no plan

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            Assert.Equal(2, census.Plans.Count);
            CensusPlan first = census.Plans.Single(p => p.Titles.Contains("PELAN LANTAI TINGKAT 1"));
            CensusPlan second = census.Plans.Single(p => p.Titles.Contains("Pelan Bumbung"));
            Assert.DoesNotContain("BILIK TIDUR", first.Titles);
            Assert.Equal(1, first.Symbols);
            Assert.Equal(2, second.Symbols);
            Assert.InRange(first.WidthM, 5.8, 6.0);
            Assert.True(first.Walls >= 4);
            Assert.Equal(3, census.Totals.InsidePlans);
        }

        [Fact]
        public void Near_texts_keeps_the_five_most_common_within_reach()
        {
            CadDocument doc = MmDrawing();
            BlockRecord socket = SquareBlock(doc, "SOCKET", 100); // reach: 1.5 x 100, raised to 300
            string[] labels = { "13A", "13A", "13A", "2G", "2G", "15A", "USB", "WP", "SW" };
            for (int i = 0; i < labels.Length; i++)
            {
                double x = 1000 + (i * 2000);
                Insert(doc, socket, x, 1000, "E-POWER");
                Text(doc, "E-TEXT", x + 150, 1000, labels[i], 100);
                Text(doc, "E-TEXT", x + 500, 1000, "FAR AWAY", 100);
            }

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            List<CensusCount> near = census.Blocks.Single(b => b.Key == "SOCKET").NearTexts;
            Assert.Equal(5, near.Count);
            Assert.Equal("13A", near[0].Name);
            Assert.Equal(3, near[0].Count);
            Assert.Equal("2G", near[1].Name);
            Assert.Equal(2, near[1].Count);
            Assert.DoesNotContain(near, n => n.Name == "FAR AWAY");
        }

        [Fact]
        public void Aec_warning_is_carried_and_does_not_throw()
        {
            CadDocument doc = TwoRooms();
            doc.Classes.AddOrUpdate(new ACadSharp.Classes.DxfClass
            {
                DxfName = "AEC_WALL",
                CppClassName = "AecDbWall",
                ApplicationName = "AecArchBase",
                IsAnEntity = true,
                InstanceCount = 12,
                ClassNumber = 600,
            });

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            Assert.False(census.Drawing.AecRefused);
            Assert.NotNull(census.Drawing.AecWarning);
            Assert.Contains("12", census.Drawing.AecWarning);
        }

        [Fact]
        public void Symbols_are_exploded_when_no_insert_sits_off_the_background()
        {
            CadDocument doc = TwoRooms();
            BlockRecord door = SquareBlock(doc, "DOOR-900", 900);
            Insert(doc, door, 3000, 150, "A-WALL");
            for (int i = 0; i < 20; i++) Line(doc, "E-LIGHT", 1000, 1000 + (i * 100), 1100, 1000 + (i * 100));

            Assert.True(ElectricalCensus.Build(doc).SymbolsAreExploded);

            Insert(doc, SquareBlock(doc, "SOCKET-13A", 100), 3000, 2500, "E-POWER");
            Assert.False(ElectricalCensus.Build(doc).SymbolsAreExploded);
        }

        private static readonly string[] LegendNames = { "SOCKET-13A", "DOWNLIGHT", "FAN", "SWITCH-1G", "DB" };

        /// <summary>A legend off to the right of the plans: one of each symbol, top to bottom,
        /// with its description 1 m to its right.</summary>
        private static void AddLegend(CadDocument doc, double x, double topY)
        {
            Text(doc, "E-TEXT", x, topY + 800, "LEGEND", 400);
            for (int i = 0; i < LegendNames.Length; i++)
            {
                double y = topY - (i * 600);
                Insert(doc, SquareBlock(doc, LegendNames[i], 200), x, y, "E-LEGEND");
                Text(doc, "E-TEXT", x + 1000, y - 100, LegendNames[i] + " DESCRIPTION", 200);
            }
        }

        [Fact]
        public void Legend_column_yields_block_and_description_rows()
        {
            CadDocument doc = TwoRooms();
            AddLegend(doc, 60000, 10000);

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            Assert.Equal(5, census.Legend.Count);
            Assert.Equal("SOCKET-13A", census.Legend[0].Block);
            Assert.Equal("SOCKET-13A DESCRIPTION", census.Legend[0].Text);
            Assert.Equal("DB DESCRIPTION", census.Legend[4].Text);
            Assert.Equal(1, census.Totals.LegendTitleTexts);
            Assert.False(census.LegendTruncated);
        }

        [Fact]
        public void A_row_of_identical_sockets_is_not_a_legend()
        {
            CadDocument doc = TwoRooms();
            BlockRecord socket = SquareBlock(doc, "SOCKET-13A", 200);
            for (int i = 0; i < 6; i++)
            {
                Insert(doc, socket, 60000, 10000 - (i * 600), "E-POWER");
                Text(doc, "E-TEXT", 61000, 10000 - (i * 600) - 100, "13A", 200);
            }

            ElectricalCensusResult census = ElectricalCensus.Build(doc);

            Assert.Empty(census.Legend);
        }

        [Fact]
        public void Caps_set_truncated_flags()
        {
            CadDocument doc = TwoRooms();
            AddLegend(doc, 60000, 10000);
            var options = new CensusOptions { MaxBlockClasses = 2, MaxLayers = 2, MaxPlans = 1, MaxLegendRows = 2 };

            ElectricalCensusResult census = ElectricalCensus.Build(doc, options);

            Assert.Equal(2, census.Blocks.Count);
            Assert.True(census.BlocksTruncated);
            Assert.Equal(2, census.Layers.Count);
            Assert.True(census.LayersTruncated);
            Assert.Single(census.Plans);
            Assert.True(census.PlansTruncated);
            Assert.Equal(2, census.Legend.Count);
            Assert.True(census.LegendTruncated);

            ElectricalCensusResult uncapped = ElectricalCensus.Build(doc);
            Assert.False(uncapped.BlocksTruncated || uncapped.LayersTruncated || uncapped.PlansTruncated || uncapped.LegendTruncated);
        }

        private static IEnumerable<string> Keys(object? node)
        {
            if (node is Dictionary<string, object?> map)
            {
                foreach (KeyValuePair<string, object?> pair in map)
                {
                    yield return pair.Key;
                    foreach (string key in Keys(pair.Value)) yield return key;
                }
            }
            else if (node is List<object?> list)
            {
                foreach (object? item in list)
                {
                    foreach (string key in Keys(item)) yield return key;
                }
            }
        }

        [Fact]
        public void Output_carries_no_instance_coordinates()
        {
            CadDocument doc = TwoRooms();
            BlockRecord socket = SquareBlock(doc, "SOCKET-20A", 100);
            Insert(doc, socket, 3000, 2500, "E-POWER");
            Insert insert = Insert(doc, socket, 3123.457, 2500, "E-POWER");
            insert.Attributes.Add(new AttributeEntity { Tag = "TAG", Value = "S1" });
            AddLegend(doc, 60000, 10000);

            Dictionary<string, object?> wire = ElectricalCensus.Build(doc).ToWire();
            string json = System.Text.Json.JsonSerializer.Serialize(wire);

            string[] banned = { "x", "y", "points", "at", "point", "position", "location" };
            Assert.DoesNotContain(Keys(wire), key => banned.Contains(key, StringComparer.OrdinalIgnoreCase));
            Assert.DoesNotContain("123457", json);
            Assert.DoesNotContain("3123", json);
            Assert.Contains("\"blocks\"", json);
            Assert.Contains("\"timings_s\"", json);
        }

        [Fact]
        public void Reads_a_dxf_written_by_the_test()
        {
            CadDocument doc = TwoRooms();
            BlockRecord socket = SquareBlock(doc, "SOCKET-13A", 100);
            for (int i = 0; i < 4; i++) Insert(doc, socket, 1000 + (i * 1000), 2500, "E-POWER");
            Text(doc, "A-TEXT", 1000, -600, "PELAN LANTAI TINGKAT 1", 500);

            string path = WriteDxf(doc);
            try
            {
                ElectricalCensusResult census = ElectricalCensus.Read(path);

                CensusBlockClass skt = census.Blocks.Single(b => b.Key == "SOCKET-13A");
                Assert.Equal(4, skt.Count);
                Assert.Equal("E-POWER", skt.Layers[0].Name);
                Assert.True(LayerOf(census, "A-WALL").PairsLikeWalls);
                Assert.Equal(2, census.Plans.Count);
                Assert.Contains(census.Plans, plan => plan.Titles.Contains("PELAN LANTAI TINGKAT 1"));
                Assert.True(census.TimingsSeconds.ContainsKey("read"));
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
