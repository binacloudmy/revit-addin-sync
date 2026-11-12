// Pure tests for the create-mode thickness→type selection. No Revit at runtime.

using System.Collections.Generic;
using BinaVibe.Mcp.Tools;
using Xunit;

namespace Tests
{
    public class CadWallBandingTests
    {
        private static List<ThicknessBand> Bands() => new()
        {
            new ThicknessBand(90, 150, "Brick 114"),
            new ThicknessBand(180, 250, "Generic 200"),
        };

        [Fact]
        public void Contained_thickness_picks_its_band()
        {
            Assert.Equal("Brick 114", CadWallBanding.PickType(114, Bands(), null));
            Assert.Equal("Generic 200", CadWallBanding.PickType(200, Bands(), null));
        }

        [Fact]
        public void Unmatched_thickness_uses_fallback_not_nearest()
        {
            // 500mm is nearest to the 180–250 band, but nearest is deliberately NOT
            // used — with no fallback it is left unbuilt (null), with a fallback it
            // takes the fallback type.
            Assert.Null(CadWallBanding.PickType(500, Bands(), null));
            Assert.Equal("Default", CadWallBanding.PickType(500, Bands(), "Default"));
        }

        [Fact]
        public void No_bands_falls_straight_through_to_fallback()
        {
            Assert.Equal("Generic 100", CadWallBanding.PickType(114, new List<ThicknessBand>(), "Generic 100"));
            Assert.Null(CadWallBanding.PickType(114, new List<ThicknessBand>(), null));
        }

        [Fact]
        public void Window_bounds_are_inclusive_and_open_ended()
        {
            Assert.True(CadWallBanding.InWindow(114, 90, 400));
            Assert.True(CadWallBanding.InWindow(90, 90, 400));   // inclusive low
            Assert.True(CadWallBanding.InWindow(400, 90, 400));  // inclusive high
            Assert.False(CadWallBanding.InWindow(50, 90, 400));
            Assert.False(CadWallBanding.InWindow(500, 90, 400));
            Assert.True(CadWallBanding.InWindow(9999, null, null)); // no window = always in
            Assert.True(CadWallBanding.InWindow(50, null, 400));    // open low bound
        }
    }
}
