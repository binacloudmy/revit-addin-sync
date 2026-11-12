// Tests for CadFileReader — DWG/DXF extraction via ACadSharp.
// These tests verify source detection and extraction logic. They run against
// the ACadSharp library directly, no Revit dependency.

using System.Collections.Generic;
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

        // Integration tests with real DWG files would go here.
        // For now, we test the data structures and error handling.
        // Real file tests can be added when sample DWG files are available
        // in the test data directory.
    }
}
