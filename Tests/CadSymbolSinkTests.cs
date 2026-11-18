// CadRenderSource tells a sink that also implements ICadSymbolSink about every block insert:
// its block, layer, world point, rotation, scale and attributes, before its contents. A sink
// without the interface must see exactly what it saw before. Documents are built in memory.

using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using Cad2Bim.Services;
using CSMath;
using Xunit;
using static Tests.CensusFixture;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class CadSymbolSinkTests
    {
        private sealed record SymbolCall(CadBlockInfo Block, string Layer, double X, double Y, double Rotation,
                                         double ScaleX, double ScaleY, bool Mirrored, int Depth,
                                         IReadOnlyDictionary<string, string> Attributes);

        private class PlainSink : ICadSink
        {
            public List<string> Calls { get; } = new();

            public void Polyline(IReadOnlyList<(double X, double Y)> points, bool isClosed, string layer, CadSource source) =>
                Calls.Add("poly " + layer + " " + source + " " +
                          string.Join(";", points.Select(p => p.X.ToString("0.###") + "," + p.Y.ToString("0.###"))));

            public void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters, string layer, CadSource source) =>
                Calls.Add("arc " + layer + " " + points.Count);

            public void Text(double x, double y, double height, string value, string layer, CadSource source) =>
                Calls.Add("text " + layer + " " + value);
        }

        private sealed class SymbolSink : PlainSink, ICadSymbolSink
        {
            public List<SymbolCall> Symbols { get; } = new();

            public void Symbol(CadBlockInfo block, string layer, double x, double y, double rotationRad,
                               double scaleX, double scaleY, bool mirrored, int depth,
                               IReadOnlyDictionary<string, string> attributes)
            {
                Symbols.Add(new SymbolCall(block, layer, x, y, rotationRad, scaleX, scaleY, mirrored, depth, attributes));
                Calls.Add("symbol " + block.Name);
            }
        }

        private static CadDocument NestedDrawing()
        {
            var doc = new CadDocument();
            BlockRecord child = SquareBlock(doc, "CHILD", 100);
            var parent = new BlockRecord("PARENT");
            parent.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(500, 0, 0)));
            parent.Entities.Add(MakeInsert(doc, child, 200, 0, "E-LIGHT"));
            doc.BlockRecords.Add(parent);
            Insert(doc, parent, 1000, 2000, "E-POWER");
            Line(doc, "A-BLDG", 0, 0, 5000, 0);
            return doc;
        }

        [Fact]
        public void A_sink_without_the_symbol_interface_gets_the_same_calls_as_before()
        {
            CadDocument doc = NestedDrawing();

            var plain = new PlainSink();
            CadRenderSource.Walk(doc, plain);
            var withSymbols = new SymbolSink();
            CadRenderSource.Walk(doc, withSymbols);

            Assert.Equal(plain.Calls, withSymbols.Calls.Where(c => !c.StartsWith("symbol ", StringComparison.Ordinal)));
            Assert.Equal(2, withSymbols.Symbols.Count);
        }

        [Fact]
        public void Top_level_insert_reports_name_layer_point_rotation_and_scale()
        {
            var doc = new CadDocument();
            BlockRecord block = SquareBlock(doc, "SOCKET-13A", 100);
            Insert(doc, block, 1000, 2000, "E-POWER", rotationDeg: 90, sx: 2, sy: 2);

            var sink = new SymbolSink();
            CadRenderSource.Walk(doc, sink);

            SymbolCall call = Assert.Single(sink.Symbols);
            Assert.Equal("SOCKET-13A", call.Block.Name);
            Assert.Equal("E-POWER", call.Layer);
            Assert.Equal(1000, call.X, 6);
            Assert.Equal(2000, call.Y, 6);
            Assert.Equal(Math.PI / 2, call.Rotation, 6);
            Assert.Equal(2, call.ScaleX, 6);
            Assert.Equal(2, call.ScaleY, 6);
            Assert.False(call.Mirrored);
            Assert.Equal(0, call.Depth);
        }

        [Fact]
        public void Nested_insert_is_reported_at_depth_1_in_world_coordinates()
        {
            var sink = new SymbolSink();
            CadRenderSource.Walk(NestedDrawing(), sink);

            SymbolCall child = sink.Symbols.Single(s => s.Block.Name == "CHILD");
            Assert.Equal(1, child.Depth);
            Assert.Equal("E-LIGHT", child.Layer);
            Assert.Equal(1200, child.X, 6);
            Assert.Equal(2000, child.Y, 6);
            Assert.Equal(0, sink.Symbols.Single(s => s.Block.Name == "PARENT").Depth);
        }

        [Fact]
        public void Symbol_is_called_before_the_blocks_contents()
        {
            var sink = new SymbolSink();
            CadRenderSource.Walk(NestedDrawing(), sink);

            int parent = sink.Calls.IndexOf("symbol PARENT");
            int child = sink.Calls.IndexOf("symbol CHILD");
            int parentLine = sink.Calls.FindIndex(c => c.StartsWith("poly 0 Block 1000,2000;1500,2000", StringComparison.Ordinal));

            Assert.True(parent >= 0 && child > parent, string.Join("\n", sink.Calls));
            Assert.True(parentLine > parent, string.Join("\n", sink.Calls));
            Assert.Equal("symbol PARENT", sink.Calls[0]);
        }

        [Fact]
        public void Minsert_reports_one_symbol_per_cell()
        {
            var doc = new CadDocument();
            BlockRecord block = SquareBlock(doc, "DOWNLIGHT", 100);
            Insert insert = Insert(doc, block, 0, 0, "E-LIGHT");
            insert.RowCount = 2;
            insert.ColumnCount = 3;
            insert.RowSpacing = 1000;
            insert.ColumnSpacing = 2000;

            var sink = new SymbolSink();
            CadRenderSource.Walk(doc, sink);

            Assert.Equal(6, sink.Symbols.Count);
            Assert.Contains(sink.Symbols, s => Math.Abs(s.X - 4000) < 1e-6 && Math.Abs(s.Y - 1000) < 1e-6);
        }

        [Fact]
        public void Mirrored_insert_sets_mirrored()
        {
            var doc = new CadDocument();
            BlockRecord block = SquareBlock(doc, "SWITCH", 100);
            Insert(doc, block, 0, 0, "E-LIGHT", sx: -1);
            Insert(doc, block, 500, 0, "E-LIGHT");

            var sink = new SymbolSink();
            CadRenderSource.Walk(doc, sink);

            Assert.True(sink.Symbols[0].Mirrored);
            Assert.False(sink.Symbols[1].Mirrored);
            Assert.Equal(1, sink.Symbols[0].ScaleX, 6);
        }

        [Fact]
        public void Flipped_extrusion_insert_reports_the_drawn_point()
        {
            var doc = new CadDocument();
            BlockRecord block = SquareBlock(doc, "FAN", 100);
            Insert insert = Insert(doc, block, -812.4, 640, "E-POWER");
            insert.Normal = new XYZ(0, 0, -1);

            var sink = new SymbolSink();
            CadRenderSource.Walk(doc, sink);

            SymbolCall call = Assert.Single(sink.Symbols);
            Assert.Equal(812.4, call.X, 6);
            Assert.Equal(640, call.Y, 6);
            Assert.True(call.Mirrored);
        }

        [Fact]
        public void Attributes_arrive_by_tag()
        {
            var doc = new CadDocument();
            BlockRecord block = SquareBlock(doc, "DB", 300);
            Insert insert = Insert(doc, block, 0, 0, "E-POWER");
            insert.Attributes.Add(new AttributeEntity { Tag = "TAG", Value = "DB-1" });
            insert.Attributes.Add(new AttributeEntity { Tag = "tag", Value = "ignored, the first wins" });
            insert.Attributes.Add(new AttributeEntity { Tag = "", Value = "no tag" });
            insert.Attributes.Add(new AttributeEntity { Tag = "RATING", Value = "63A" });

            var sink = new SymbolSink();
            CadRenderSource.Walk(doc, sink);

            IReadOnlyDictionary<string, string> attributes = Assert.Single(sink.Symbols).Attributes;
            Assert.Equal(2, attributes.Count);
            Assert.Equal("DB-1", attributes["Tag"]);
            Assert.Equal("63A", attributes["RATING"]);
        }

        [Fact]
        public void Dynamic_block_copy_reports_its_source_name()
        {
            // BlockRecord.Source is read-only in ACadSharp 3.6.51 (it is resolved from the
            // dynamic-block graph of a real file), so an in-memory copy cannot name its source.
            // The fallback: an anonymous record with no source keeps its raw name and says so.
            var doc = new CadDocument();
            var record = new BlockRecord("*U12") { IsAnonymous = true };
            record.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(100, 0, 0)));
            doc.BlockRecords.Add(record);
            Insert(doc, record, 0, 0, "E-POWER");

            var sink = new SymbolSink();
            CadRenderSource.Walk(doc, sink);

            CadBlockInfo info = Assert.Single(sink.Symbols).Block;
            Assert.True(info.IsAnonymous);
            Assert.StartsWith("*U", info.RecordName, StringComparison.Ordinal);
            Assert.Equal(info.RecordName, info.Name);
        }

        [Fact]
        public void Local_extent_is_the_blocks_box_relative_to_its_base_point()
        {
            var doc = new CadDocument();
            var record = new BlockRecord("OFFSET");
            record.BlockEntity.BasePoint = new XYZ(100, 100, 0);
            record.Entities.Add(new Line(new XYZ(50, 80, 0), new XYZ(250, 80, 0)));
            record.Entities.Add(new Line(new XYZ(250, 80, 0), new XYZ(250, 180, 0)));
            record.Entities.Add(new Circle { Center = new XYZ(100, 100, 0), Radius = 10 });
            doc.BlockRecords.Add(record);
            Insert(doc, record, 5000, 5000, "E-POWER", rotationDeg: 30, sx: 3, sy: 3);

            var sink = new SymbolSink();
            CadRenderSource.Walk(doc, sink);

            CadBlockInfo info = Assert.Single(sink.Symbols).Block;
            Assert.True(info.HasGeometry);
            Assert.Equal(-50, info.MinX, 6);
            Assert.Equal(-20, info.MinY, 6);
            Assert.Equal(150, info.MaxX, 6);
            Assert.Equal(80, info.MaxY, 6);
            Assert.Equal(200, info.Width, 6);
            Assert.Equal(100, info.Height, 6);
            Assert.Equal(2, info.EntityCounts["Line"]);
            Assert.Equal(1, info.EntityCounts["Circle"]);
            Assert.Equal(0, info.NestedInserts);
        }

        [Fact]
        public void Unresolved_xref_block_is_flagged()
        {
            var doc = new CadDocument();
            var xref = new BlockRecord("ARCH-BG", @"C:\nowhere\arch.dwg", false);
            doc.BlockRecords.Add(xref);
            Insert(doc, xref, 0, 0, "XREF");

            var sink = new SymbolSink();
            CadRenderSource.Walk(doc, sink);

            CadBlockInfo info = Assert.Single(sink.Symbols).Block;
            Assert.True(info.IsUnresolvedXref);
            Assert.False(info.HasGeometry);
        }
    }
}
