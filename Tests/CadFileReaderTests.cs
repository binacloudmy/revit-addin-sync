// Tests for CadFileReader — DWG/DXF extraction via ACadSharp.
// These tests verify source detection and extraction logic. They run against
// the ACadSharp library directly, no Revit dependency.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using BinaVibe.Mcp.Tools;
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

        // Integration tests with real DWG files would go here.
        // For now, we test the data structures and error handling.
        // Real file tests can be added when sample DWG files are available
        // in the test data directory.
    }
}
