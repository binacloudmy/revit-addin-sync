#nullable enable
// Plan 1.5: live Snowdon P2 (turn 1b732b81, 19 Nov) refused `3/4"` and `0.75 in` for a pipe Diameter,
// which wanted a bare number in the display unit, and a second card appeared. A pipe size is a Measured
// spec (PipeSize), not Length. Lengths now read inch fractions, mixed numbers and feet-inches, and a
// Measured parameter shown in a length unit reads every length form, converted to its display unit, so
// `3/4"` binds exactly what the bare `0.75` binds. A parameter shown in any other unit never reads a
// length, and a fraction with no unit is refused unless the display unit is inches.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ValueParserImperialTests
    {
        private static double Mm(string raw, string? displayUnit = "mm")
        {
            var v = ValueParser.Parse(raw, ValueKind.Length, displayUnit);
            Assert.True(v.Ok, raw + ": " + v.Error);
            Assert.Equal(ValueKind.Length, v.Kind);
            return v.Number;
        }

        [Theory]
        [InlineData("3/4\"", 19.05)]
        [InlineData("3/4 \"", 19.05)]
        [InlineData("3/4in", 19.05)]
        [InlineData("3/4 in", 19.05)]
        [InlineData("3/4 inch", 19.05)]
        [InlineData("3 / 4 inches", 19.05)]
        [InlineData("3/4 inci", 19.05)]       // Malay
        [InlineData("3/4 INCI", 19.05)]
        [InlineData("3/4”", 19.05)]           // typographic quote
        [InlineData("3/4″", 19.05)]           // double prime
        [InlineData("3/4''", 19.05)]          // two single quotes
        [InlineData("¾\"", 19.05)]
        [InlineData("1 1/2\"", 38.1)]
        [InlineData("1-1/2\"", 38.1)]
        [InlineData("1  1/2 in", 38.1)]       // double space
        [InlineData("1 1/2 inci", 38.1)]
        [InlineData("1 1/2\"", 38.1)]    // no-break space
        [InlineData("1½\"", 38.1)]
        [InlineData("1 ½ in", 38.1)]
        [InlineData("1⁄2\"", 12.7)]           // fraction slash
        [InlineData("0.75 in", 19.05)]
        [InlineData("0.75\"", 19.05)]
        [InlineData("0.75”", 19.05)]          // typographic inch mark, no fraction
        [InlineData("6”", 152.4)]
        [InlineData("6″", 152.4)]
        [InlineData("6 ″", 152.4)]
        [InlineData("6''", 152.4)]
        [InlineData("6’", 1828.8)]            // typographic foot mark
        [InlineData("1.5 in", 38.1)]
        [InlineData("19.05 mm", 19.05)]
        [InlineData("1.905 cm", 19.05)]
        [InlineData("0.01905 m", 19.05)]
        public void Length_ReadsInchFractionsAndUnits(string raw, double mm)
        {
            Assert.Equal(mm, Mm(raw, "mm"), 6);
            Assert.Equal(mm, Mm(raw, "m"), 6); // a unit given wins over the display unit
        }

        [Theory]
        [InlineData("2'-6\"", 762.0)]
        [InlineData("2' - 6\"", 762.0)]
        [InlineData("2'6\"", 762.0)]
        [InlineData("2' 6\"", 762.0)]
        [InlineData("2'-6", 762.0)]
        [InlineData("2’-6”", 762.0)]          // typographic quotes
        [InlineData("2′ 6″", 762.0)]          // primes
        [InlineData("2' 6 1/2\"", 774.7)]
        [InlineData("2'-6 1/2\"", 774.7)]
        [InlineData("2'-6-1/2\"", 774.7)]
        [InlineData("2' 6½\"", 774.7)]
        [InlineData("2 ft 6 in", 762.0)]
        [InlineData("2ft 6in", 762.0)]
        [InlineData("2 feet 6 inches", 762.0)]
        [InlineData("2 kaki 6 inci", 762.0)]  // Malay
        [InlineData("2 KAKI 6 INCI", 762.0)]
        [InlineData("2'", 609.6)]
        [InlineData("2 ft", 609.6)]
        [InlineData("2 kaki", 609.6)]
        [InlineData("1.5'", 457.2)]
        [InlineData("0' 3/4\"", 19.05)]
        public void Length_ReadsFeetInches(string raw, double mm)
        {
            Assert.Equal(mm, Mm(raw), 6);
        }

        [Fact]
        public void Length_BareFractionTakesInchesOnlyWhereTheDisplayUnitIsInches()
        {
            Assert.Equal(19.05, Mm("3/4", "in"), 6);
            Assert.Equal(38.1, Mm("1 1/2", "in"), 6);
            Assert.Equal(38.1, Mm("1-1/2", "in"), 6);
            foreach (var unit in new[] { "mm", "m", "cm", "ft" })
            {
                var v = ValueParser.Parse("3/4", ValueKind.Length, unit);
                Assert.False(v.Ok, unit);
                Assert.Contains("no unit", v.Error);
            }
            Assert.False(ValueParser.Parse("1 1/2", ValueKind.Length, "mm").Ok);
            Assert.False(ValueParser.Parse("3/4", ValueKind.Length, null).Ok);
        }

        [Fact]
        public void Length_OldFormsAreUnchanged()
        {
            Assert.Equal(9 * 304.8 + 6 * 25.4, Mm("9' 6\"", "mm"), 6);
            Assert.Equal(9 * 304.8, Mm("9'", "mm"), 6);
            Assert.Equal(6 * 25.4, Mm("6 in", "mm"), 6);
            Assert.Equal(2700.0, Mm("2700", "mm"), 6);
            Assert.Equal(2700.0, Mm("2,700 mm", "mm"), 6);
            Assert.Equal(2700.0, Mm("2.7 m", "mm"), 6);
            Assert.Equal(304.8, Mm("1", "ft"), 6);
            Assert.Equal(25.4, Mm("1", "in"), 6);
            Assert.Equal("length|19.05", ValueParser.Parse("3/4\"", ValueKind.Length, "mm").Canonical);
            Assert.Equal(ValueParser.Parse("19.05 mm", ValueKind.Length, "mm").Canonical, ValueParser.Parse("3/4\"", ValueKind.Length, "mm").Canonical);
        }

        [Theory]
        [InlineData("3/0\"")]           // zero denominator
        [InlineData("1 0/0 in")]
        [InlineData("3/4/5\"")]
        [InlineData("1/2-3/4\"")]       // a range of fractions
        [InlineData("1/2 - 3/4 in")]
        [InlineData("3/4\" x 1/2\"")]
        [InlineData("3/4\" or 1\"")]
        [InlineData("about 3/4\"")]
        [InlineData("lebih kurang 3/4 inci")]
        [InlineData("3/4 mm")]          // fractions only in inches or feet-inches
        [InlineData("1/2 m")]
        [InlineData("1 5/4\"")]         // a fraction past one after a whole number
        [InlineData("-3/4\"")]
        [InlineData("3/4\"\"")]
        [InlineData("2'-6\" x 3'")]
        [InlineData("2' 3' ")]
        [InlineData("/4\"")]
        [InlineData("3/\"")]
        [InlineData("1/4'")]
        [InlineData("1.5 1/2\"")]      // a decimal before a fraction is a guess, not one length
        [InlineData("1.5-1/2\"")]
        [InlineData("2' 6.5 1/2\"")]
        [InlineData("2'-6.5-1/2\"")]
        [InlineData("0.5 3/4 in")]
        [InlineData("1 - 1/2\"")]      // a spaced hyphen may mean subtraction
        [InlineData("1 -1/2\"")]
        [InlineData("1- 1/2\"")]
        [InlineData("2' 6 - 1/2\"")]            // fractions of a foot are not read; give feet-inches
        public void Length_RejectsWhatIsNotOneLength(string raw)
        {
            foreach (var unit in new[] { "mm", "in" })
            {
                var v = ValueParser.Parse(raw, ValueKind.Length, unit);
                Assert.False(v.Ok, raw + " (" + unit + ") gave " + v.Display);
                Assert.False(string.IsNullOrWhiteSpace(v.Error));
            }
        }

        [Theory]
        [InlineData("3/4\"", "in", 0.75)]
        [InlineData("3/4 in", "in", 0.75)]
        [InlineData("0.75 in", "in", 0.75)]
        [InlineData("0.75\"", "in", 0.75)]
        [InlineData("3/4 inci", "in", 0.75)]
        [InlineData("¾”", "in", 0.75)]
        [InlineData("3/4", "in", 0.75)]          // bare fraction in an inch project
        [InlineData("0.75", "in", 0.75)]         // bare number: unchanged
        [InlineData("1 1/2\"", "in", 1.5)]
        [InlineData("1-1/2\"", "in", 1.5)]
        [InlineData("6”", "in", 6)]
        [InlineData("0' - 0 3/4\"", "in", 0.75)]   // Revit's own feet-and-fractional-inches spelling
        [InlineData("1 1/2", "in", 1.5)]
        [InlineData("1.5 in", "in", 1.5)]
        [InlineData("38.1 mm", "in", 1.5)]
        [InlineData("2'-6\"", "in", 30)]
        [InlineData("2 ft", "in", 24)]
        [InlineData("3/4\"", "mm", 19.05)]       // a metric project shows the size in mm
        [InlineData("1 1/2\"", "mm", 38.1)]
        [InlineData("20 mm", "mm", 20)]
        [InlineData("2 cm", "mm", 20)]
        [InlineData("20", "mm", 20)]
        public void Measured_PipeSizeShownInALengthUnitReadsLengths(string raw, string displayUnit, double shown)
        {
            var v = ValueParser.Parse(raw, ValueKind.Measured, displayUnit);
            Assert.True(v.Ok, raw + ": " + v.Error);
            Assert.Equal(ValueKind.Measured, v.Kind);
            Assert.Null(v.Unit); // a number in the display unit, as a bare number is
            Assert.Equal(shown, v.Number, 9);
        }

        [Fact]
        public void Measured_LengthFormsBindTheSameValueAsTheBareNumber()
        {
            // The ripple card shows the model's value; the add-in must write exactly that value, and the plan
            // hash binds the canonical, so 3/4" and 0.75 (inch project) are one write.
            var bare = ValueParser.Parse("0.75", ValueKind.Measured, "in");
            foreach (var raw in new[] { "3/4\"", "3/4 in", "0.75 in", "¾\"", "3/4", "19.05 mm" })
            {
                var v = ValueParser.Parse(raw, ValueKind.Measured, "in");
                Assert.True(v.Ok, raw + ": " + v.Error);
                Assert.Equal(bare.Canonical, v.Canonical);
                Assert.Equal(bare.Display, v.Display);
            }
            Assert.Equal("measured|0.75", bare.Canonical);
            Assert.Equal("measured|20", ValueParser.Parse("20 mm", ValueKind.Measured, "mm").Canonical);
            var back = ParsedValue.FromCanonical(ValueParser.Parse("1 1/2\"", ValueKind.Measured, "in").Canonical);
            Assert.True(back.Ok);
            Assert.Equal(1.5, back.Number, 9);
        }

        [Theory]
        [InlineData("3/4\"", "w")]
        [InlineData("0.75 in", "w")]
        [InlineData("2'-6\"", "w")]
        [InlineData("20 mm", "w")]
        [InlineData("1 1/2 inci", "%")]
        [InlineData("3/4\"", null)]
        [InlineData("0.75 in", null)]
        [InlineData("20 mm", null)]
        [InlineData("3/4", null)]
        [InlineData("3/4", "w")]
        public void Measured_NeverReadsALengthWhereTheDisplayUnitIsNotALength(string raw, string? displayUnit)
        {
            var v = ValueParser.Parse(raw, ValueKind.Measured, displayUnit);
            Assert.False(v.Ok, raw + " (" + displayUnit + ") gave " + v.Display);
            Assert.False(string.IsNullOrWhiteSpace(v.Error));
        }

        [Fact]
        public void Measured_OtherUnitsAndSlopesAreUnchanged()
        {
            Assert.Equal("measured@w|10", ValueParser.Parse("10 W", ValueKind.Measured, null).Canonical);
            Assert.Equal("measured@%|1", ValueParser.Parse("1:100", ValueKind.Measured, "%").Canonical);
            Assert.Equal("measured@l/s|10", ValueParser.Parse("10 l/s", ValueKind.Measured, null).Canonical);
            Assert.Equal("measured|230", ValueParser.Parse("230", ValueKind.Measured, null).Canonical);
            Assert.Equal("measured|0.75", ValueParser.Parse("0.75", ValueKind.Measured, "w").Canonical);
        }

        [Theory]
        [InlineData(ValueKind.Area, "m2")]
        [InlineData(ValueKind.Volume, "m3")]
        [InlineData(ValueKind.Angle, "°")]
        [InlineData(ValueKind.Number, null)]
        [InlineData(ValueKind.Integer, null)]
        public void OtherKindsNeverReadALength(ValueKind kind, string? displayUnit)
        {
            foreach (var raw in new[] { "3/4\"", "0.75 in", "2'-6\"", "1 1/2" })
                Assert.False(ValueParser.Parse(raw, kind, displayUnit).Ok, kind + " " + raw);
        }

        [Fact]
        public void TextKeepsFractionsAsGiven()
        {
            Assert.Equal("3/4\"", ValueParser.Parse("3/4\"", ValueKind.Text, null).Text);
        }
    }
}
