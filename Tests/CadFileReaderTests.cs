// Tests for CadFileReader — DWG/DXF extraction via ACadSharp.
// These tests verify source detection and extraction logic. They run against
// the ACadSharp library directly, no Revit dependency.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
using BinaVibe.Mcp.Tools;
using CSMath;
using Xunit;

namespace Tests
{
    public class CadFileReaderTests
    {
        [Fact]
        public void DetectSource_returns_plain_autocad_for_empty_classes()
        {
            // When no AEC_/AECC_/AECB_ classes are present, source is plain AutoCAD
            var info = new CadSourceInfo();
            Assert.Equal("plain_autocad", info.Source);
            Assert.True(info.Supported);
            Assert.Null(info.Warning);
        }

        [Fact]
        public void CadBlock_stores_name_and_attributes()
        {
            var block = new CadBlock
            {
                Name = "DR-900",
                X = 5000.5,
                Y = 3000.25,
                Rotation = 90.0,
                Layer = "A-DOOR",
                Attributes = new Dictionary<string, string>
                {
                    ["WIDTH"] = "900",
                    ["HEIGHT"] = "2100",
                    ["FIRE_RATING"] = "FD30",
                }
            };

            Assert.Equal("DR-900", block.Name);
            Assert.Equal(90.0, block.Rotation);
            Assert.Equal("A-DOOR", block.Layer);
            Assert.Equal("900", block.Attributes["WIDTH"]);
            Assert.Equal("2100", block.Attributes["HEIGHT"]);
            Assert.Equal("FD30", block.Attributes["FIRE_RATING"]);
        }

        [Fact]
        public void CadText_stores_content_and_position()
        {
            var text = new CadText
            {
                Type = "MTEXT",
                Content = "LIVING ROOM",
                X = 5000,
                Y = 4000,
                Layer = "A-ANNO-TEXT",
                Height = 250,
            };

            Assert.Equal("MTEXT", text.Type);
            Assert.Equal("LIVING ROOM", text.Content);
            Assert.Equal("A-ANNO-TEXT", text.Layer);
            Assert.Equal(250, text.Height);
        }

        [Fact]
        public void Extract_returns_error_for_missing_file()
        {
            var result = CadFileReader.Extract("/nonexistent/file.dwg");

            Assert.False(result.Ok);
            Assert.Contains("not found", result.Error);
        }

        [Fact]
        public void Extract_returns_error_for_unsupported_extension()
        {
            var result = CadFileReader.Extract("file.pdf");

            Assert.False(result.Ok);
            Assert.Contains("Unsupported", result.Error);
        }

        [Fact]
        public void GetBlockCensus_returns_error_for_missing_file()
        {
            var result = CadFileReader.GetBlockCensus("/nonexistent/file.dwg");

            Assert.False((bool)result["ok"]!);
            Assert.NotNull(result["error"]);
        }

        [Fact]
        public void GetTextByLayer_returns_error_for_missing_file()
        {
            var result = CadFileReader.GetTextByLayer("/nonexistent/file.dwg");

            Assert.False((bool)result["ok"]!);
            Assert.NotNull(result["error"]);
        }

        [Fact]
        public void ExtractToDict_returns_error_for_missing_file()
        {
            var result = CadFileReader.ExtractToDict("/nonexistent/file.dwg");

            Assert.False((bool)result["ok"]!);
            Assert.NotNull(result["error"]);
        }

        [Fact]
        public void ReadText_returns_error_for_missing_file()
        {
            var result = CadFileReader.ReadText("/nonexistent/file.dwg");

            Assert.False(result.Ok);
            Assert.Contains("not found", result.Error);
        }

        [Fact]
        public void ReadText_returns_error_for_unsupported_extension()
        {
            var result = CadFileReader.ReadText("file.pdf");

            Assert.False(result.Ok);
            Assert.Contains("Unsupported", result.Error);
        }

        [Fact]
        public void ReadText_reads_text_and_units_from_a_dxf()
        {
            // Minimal DXF: header declares millimetres ($INSUNITS 4) and ENTITIES
            // holds one TEXT. This pins the DWG/DXF label path (ACadSharp) that the
            // linked-DWG fallback relies on.
            const string dxf =
                "0\nSECTION\n2\nHEADER\n" +
                "9\n$ACADVER\n1\nAC1027\n" +
                "9\n$INSUNITS\n70\n4\n" +
                "0\nENDSEC\n" +
                "0\nSECTION\n2\nENTITIES\n" +
                "0\nTEXT\n8\nROOM\n10\n1000.0\n20\n2000.0\n40\n250.0\n1\nLIVING ROOM\n" +
                "0\nENDSEC\n0\nEOF\n";

            var path = Path.Combine(Path.GetTempPath(), "bina-readtext-" + System.Guid.NewGuid() + ".dxf");
            File.WriteAllText(path, dxf);
            try
            {
                var result = CadFileReader.ReadText(path);

                Assert.True(result.Ok, result.Error);
                Assert.True(result.UnitsKnown);
                Assert.Equal(1.0, result.UnitToMm);
                var text = result.Texts.Single(t => t.Content == "LIVING ROOM");
                Assert.Equal(1000.0, text.X);
                Assert.Equal(2000.0, text.Y);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ReadText_strips_mtext_formatting_codes()
        {
            // MText.Value is the raw DXF string. Leaving {\f...;} and \P in the
            // content made beam labels match nothing ("{\fArial|b0;B1}\P300x600").
            var path = WriteDxf(doc => doc.Entities.Add(new MText("{\\fArial|b0;B1}\\P300x600")
            {
                InsertPoint = new XYZ(100, 200, 0),
                Height = 2.5,
                Layer = doc.Layers["0"],
            }));
            try
            {
                var result = CadFileReader.ReadText(path);

                Assert.True(result.Ok, result.Error);
                var text = Assert.Single(result.Texts);
                Assert.DoesNotContain("\\P", text.Content);
                Assert.DoesNotContain("\\f", text.Content);
                Assert.Contains("B1", text.Content);
                Assert.Contains("300x600", text.Content);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ReadText_reads_text_nested_in_a_block_at_model_coordinates()
        {
            // Structural DWGs keep beam tags inside INSERT blocks, not model
            // space. The block's text is at (0,0); the INSERT moves it to
            // (1000,2000), and ReadText must do the same.
            var path = WriteDxf(doc =>
            {
                var block = new BlockRecord("BEAM");
                doc.BlockRecords.Add(block);
                block.Entities.Add(new TextEntity
                {
                    Value = "B1",
                    InsertPoint = new XYZ(0, 0, 0),
                    Height = 2.5,
                    Layer = doc.Layers["0"],
                });
                doc.Entities.Add(new Insert(block)
                {
                    InsertPoint = new XYZ(1000, 2000, 0),
                });
            });
            try
            {
                var result = CadFileReader.ReadText(path);

                Assert.True(result.Ok, result.Error);
                var text = Assert.Single(result.Texts);
                Assert.Equal("B1", text.Content);
                Assert.Equal(1000.0, text.X);
                Assert.Equal(2000.0, text.Y);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ReadText_reads_block_attributes()
        {
            // Beam size tags are often ATTRIB values on the INSERT rather than
            // TEXT entities in the block definition.
            var path = WriteDxf(doc =>
            {
                var block = new BlockRecord("BEAM");
                doc.BlockRecords.Add(block);
                // The block definition has an attribute template; only the
                // INSERT's value should be returned, not the template default.
                block.Entities.Add(new AttributeDefinition
                {
                    Tag = "SIZE",
                    Value = "TEMPLATE",
                    InsertPoint = new XYZ(0, 0, 0),
                    Height = 2.5,
                    Layer = doc.Layers["0"],
                });
                var insert = new Insert(block) { InsertPoint = new XYZ(1000, 2000, 0) };
                insert.Attributes.Add(new AttributeEntity
                {
                    Tag = "SIZE",
                    Value = "300x600",
                    InsertPoint = new XYZ(1000, 2000, 0),
                    Height = 2.5,
                    Layer = doc.Layers["0"],
                });
                doc.Entities.Add(insert);
            });
            try
            {
                var result = CadFileReader.ReadText(path);

                Assert.True(result.Ok, result.Error);
                var text = Assert.Single(result.Texts);
                Assert.Equal("ATTRIB", text.Type);
                Assert.Equal("300x600", text.Content);
                Assert.Equal(1000.0, text.X);
                Assert.Equal(2000.0, text.Y);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ReadText_infers_units_from_extents_when_the_header_is_unitless()
        {
            // 18 x 24 units is 30 units of diagonal. A floor plan that size is
            // metres, so the scale is 1000 mm/unit - not the old assumption of mm.
            var path = WriteDxf(doc =>
            {
                doc.Header.InsUnits = UnitsType.Unitless;
                doc.Entities.Add(new Line
                {
                    StartPoint = new XYZ(0, 0, 0),
                    EndPoint = new XYZ(18, 24, 0),
                    Layer = doc.Layers["0"],
                });
                doc.Entities.Add(new TextEntity
                {
                    Value = "B1",
                    InsertPoint = new XYZ(1, 1, 0),
                    Height = 0.2,
                    Layer = doc.Layers["0"],
                });
            });
            try
            {
                var result = CadFileReader.ReadText(path);

                Assert.True(result.Ok, result.Error);
                Assert.False(result.UnitsKnown);
                Assert.True(result.UnitsInferred);
                Assert.Equal(1000.0, result.UnitToMm);
            }
            finally { File.Delete(path); }
        }

        private static string WriteDxf(Action<CadDocument> build)
        {
            var doc = new CadDocument();
            doc.CreateDefaults();
            build(doc);

            var path = Path.Combine(Path.GetTempPath(), "bina-readtext-" + Guid.NewGuid() + ".dxf");
            DxfWriter.Write(path, doc);
            return path;
        }

        // Integration tests with real DWG files would go here.
        // For now, we test the data structures and error handling.
        // Real file tests can be added when sample DWG files are available
        // in the test data directory.
    }
}
