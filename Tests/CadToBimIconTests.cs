// Ribbon icons are embedded by the Resources\Icons\*.png glob and bound by
// App.LoadIcon("CadToBim", 16|32). A PNG rasterised at the wrong pixel size
// is what blows a ribbon button out (Resources/Icons/README.md), and a
// missing file is a null Image with no error at all — so the three sizes are
// pinned here, straight from the PNG IHDR chunk.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CadToBimIconTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static string Icons(params string[] parts) =>
            Path.Combine(new[] { RepoRoot(), "Resources", "Icons" }.Concat(parts).ToArray());

        [Theory]
        [InlineData(16)]
        [InlineData(32)]
        [InlineData(64)]
        public void Png_ExistsAtItsNominalPixelSize(int size)
        {
            var path = Icons($"CadToBim{size}.png");
            Assert.True(File.Exists(path), "missing " + path);

            var bytes = File.ReadAllBytes(path);
            // PNG layout: 8-byte signature, IHDR length (4), "IHDR" (4),
            // width (4, big-endian), height (4, big-endian).
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());
            int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            Assert.Equal(size, width);
            Assert.Equal(size, height);
        }

        [Theory]
        [InlineData(16, "1.3")]
        [InlineData(32, "1.8")]
        public void SvgMaster_IsOnItsGrid_WithTheSetsInkAndOneAccent(int grid, string strokeWidth)
        {
            var svg = File.ReadAllText(Icons("svg", $"CadToBim{grid}.svg"));
            Assert.Contains($"viewBox=\"0 0 {grid} {grid}\"", svg);
            Assert.Contains($"stroke-width=\"{strokeWidth}\"", svg);
            Assert.Contains("#33383D", svg);   // graphite structure (README rule)
            Assert.Contains("#1B6EC2", svg);   // the one accent
            Assert.DoesNotContain("#D93B94", svg); // never two accents: no AI magenta here
        }

        [Fact]
        public void Generator_CarriesTheDrawing()
        {
            // README: the script holds the drawings; the PNGs are output.
            var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "gen-ribbon-icons.mjs"));
            Assert.Contains("CadToBim: {", script);
        }
    }
}
