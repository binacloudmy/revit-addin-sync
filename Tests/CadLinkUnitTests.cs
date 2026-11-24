#nullable enable
// CadLinkAlign.ImportUnitName and ScaleOff: a DWG is linked in the engine's unit, and a link in
// another unit is refused as scaled. elec.dwg's header says inches over a millimetre drawing:
// Revit linked it 25.4 times the size the engine reads (1354 x 633 m).

using System;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class CadLinkUnitTests
    {
        [Theory]
        [InlineData(1.0, "Millimeter")]
        [InlineData(10.0, "Centimeter")]
        [InlineData(1000.0, "Meter")]
        [InlineData(25.4, "Inch")]
        [InlineData(304.8, "Foot")]
        [InlineData(100.0, null)]
        [InlineData(914.4, null)]
        public void ImportUnitName_maps_the_engine_scale(double mmPerUnit, string? expected) =>
            Assert.Equal(expected, CadLinkAlign.ImportUnitName(mmPerUnit));

        [Fact]
        public void ScaleOff_refuses_a_link_25_times_the_drawing() =>
            Assert.True(CadLinkAlign.ScaleOff(1354490 * 25.4, 633140 * 25.4, 0, 1354490, 633140));

        [Fact]
        public void ScaleOff_passes_a_link_of_the_drawings_size() =>
            Assert.False(CadLinkAlign.ScaleOff(1354490, 633140, 0, 1354490, 633140));

        [Fact]
        public void ScaleOff_compares_a_turned_link_with_the_turned_drawing()
        {
            // A 40 x 10 m drawing turned 30 degrees: its plan box is 39.6 x 28.7 m.
            double r = 30 * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            double w = (40000 * c) + (10000 * s), h = (40000 * s) + (10000 * c);
            Assert.False(CadLinkAlign.ScaleOff(w, h, r, 40000, 10000));
            Assert.True(CadLinkAlign.ScaleOff(w * 0.7, h * 0.7, r, 40000, 10000));
        }

        [Fact]
        public void ScaleOff_cannot_tell_without_an_extent()
        {
            Assert.False(CadLinkAlign.ScaleOff(0, 0, 0, 1354490, 633140));
            Assert.False(CadLinkAlign.ScaleOff(1354490, 633140, 0, 0, 0));
        }

        [Theory]
        [InlineData("accepted", false, false)]
        [InlineData("provisional", false, true)]
        [InlineData("provisional", true, false)]
        [InlineData("failed", false, false)]
        public void NeedsConfirm_only_a_provisional_link_without_confirm(string outcome, bool confirmed, bool expected) =>
            Assert.Equal(expected, CadLinkAlign.NeedsConfirm(new FitResult { Outcome = outcome }, confirmed));
    }
}
