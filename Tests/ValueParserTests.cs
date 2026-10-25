#nullable enable
// W3 Task 4 (spec §11 #5; Review Focus 3). Drafters and the model write
// lengths as "2.7m", "2700", "270 cm", "2,700 mm" or "2.700"; they must all
// reach the same length, a bare number must take the parameter's display
// unit, and a value that could mean two things is refused, never guessed.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System.Text.Json;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ValueParserTests
    {
        private static double Mm(string raw, string? displayUnit = "mm")
        {
            var v = ValueParser.Parse(raw, ValueKind.Length, displayUnit);
            Assert.True(v.Ok, raw + ": " + v.Error);
            return v.Number;
        }

        [Theory]
        [InlineData("2.7m", "mm")]
        [InlineData("2700", "mm")]
        [InlineData("270 cm", "mm")]
        [InlineData("2,700 mm", "mm")]
        [InlineData("2.700", "m")]          // bare number in a metre display unit
        [InlineData("2,7 m", "mm")]         // BM decimal comma, unambiguous
        [InlineData("2,70 meter", "mm")]
        [InlineData("2.7", "m")]
        [InlineData(" 2700mm ", "m")]
        [InlineData("2700 milimeter", "mm")]
        [InlineData("270 sentimeter", "mm")]
        [InlineData("2.700,0 mm", "mm")]    // continental thousands + decimal comma
        [InlineData("2 700 mm", "mm")]      // SI thousands space
        [InlineData("+2700", "mm")]
        public void ValueParser_ParsesMixedLengthForms(string raw, string displayUnit)
        {
            Assert.Equal(2700.0, Mm(raw, displayUnit), 6);
        }

        [Fact]
        public void BareNumberTakesTheParametersDisplayUnit()
        {
            Assert.Equal(2700.0, Mm("2700", "mm"), 6);
            Assert.Equal(2_700_000.0, Mm("2700", "m"), 3);
            Assert.Equal(27_000.0, Mm("2700", "cm"), 6);
            Assert.Equal(304.8, Mm("1", "ft"), 6);
            Assert.Equal(2900.0, Mm("2900", "mm"), 6);
        }

        [Fact]
        public void ParsesImperialFeetAndInches()
        {
            Assert.Equal(9 * 304.8 + 6 * 25.4, Mm("9' 6\"", "mm"), 6);
            Assert.Equal(9 * 304.8, Mm("9'", "mm"), 6);
            Assert.Equal(6 * 25.4, Mm("6 in", "mm"), 6);
        }

        [Theory]
        [InlineData("2,700 m")]      // 2.7 m (BM decimal comma) or 2700 m (thousands)?
        [InlineData("2.700 mm")]     // 2.7 mm or 2700 mm (dot thousands)?
        [InlineData("1,500 cm")]
        public void RejectsAmbiguous(string raw)
        {
            var v = ValueParser.Parse(raw, ValueKind.Length, "mm");
            Assert.False(v.Ok);
            Assert.Contains("ambiguous", v.Error);
        }

        [Theory]
        [InlineData("2700 300")]
        [InlineData("2700 x 300")]
        [InlineData("about 2700 mm")]
        [InlineData("2.7.0 m")]
        [InlineData("2,70,0 mm")]
        [InlineData("2700 furlongs")]
        [InlineData("")]
        [InlineData("2700-3000 mm")]
        public void RejectsWhatIsNotOneLength(string raw)
        {
            var v = ValueParser.Parse(raw, ValueKind.Length, "mm");
            Assert.False(v.Ok, raw);
            Assert.False(string.IsNullOrWhiteSpace(v.Error));
        }

        [Fact]
        public void RejectsABareNumberWhenTheDisplayUnitIsUnknown()
        {
            var v = ValueParser.Parse("2700", ValueKind.Length, null);
            Assert.False(v.Ok);
            Assert.Contains("unit", v.Error);
            Assert.True(ValueParser.Parse("2700 mm", ValueKind.Length, null).Ok);
        }

        [Fact]
        public void RejectsAUnitOfTheWrongKind()
        {
            var v = ValueParser.Parse("2700 mm", ValueKind.Area, "m2");
            Assert.False(v.Ok);
            Assert.Contains("length", v.Error);
        }

        [Fact]
        public void ParsesAreasVolumesAndAngles()
        {
            Assert.Equal(12.5, ValueParser.Parse("12.5 m²", ValueKind.Area, "m2").Number, 9);
            Assert.Equal(12.5, ValueParser.Parse("12,5", ValueKind.Area, "m2").Number, 9);
            Assert.Equal(0.5, ValueParser.Parse("500 l", ValueKind.Volume, "m3").Number, 9);
            Assert.Equal(90.0, ValueParser.Parse("90°", ValueKind.Angle, "°").Number, 9);
            Assert.Equal(45.0, ValueParser.Parse("45 darjah", ValueKind.Angle, "°").Number, 9);
        }

        [Fact]
        public void ParsesYesNoInEnglishAndMalay()
        {
            Assert.Equal(1, ValueParser.Parse("ya", ValueKind.YesNo, null).Integer);
            Assert.Equal(0, ValueParser.Parse("Tidak", ValueKind.YesNo, null).Integer);
            Assert.Equal(1, ValueParser.Parse(JsonDocument.Parse("true").RootElement, ValueKind.YesNo, null).Integer);
            Assert.False(ValueParser.Parse("maybe", ValueKind.YesNo, null).Ok);
        }

        [Fact]
        public void ParsesIntegersAndPlainNumbers()
        {
            Assert.Equal(1200, ValueParser.Parse("1,200", ValueKind.Integer, null).Integer);
            Assert.Equal(12, ValueParser.Parse(JsonDocument.Parse("12.0").RootElement, ValueKind.Integer, null).Integer);
            Assert.False(ValueParser.Parse("12.5", ValueKind.Integer, null).Ok);
            Assert.Equal(2.5, ValueParser.Parse("2,5", ValueKind.Number, null).Number, 9);
            Assert.False(ValueParser.Parse("2.5 mm", ValueKind.Number, null).Ok);
        }

        [Fact]
        public void TextIsKeptAsGiven()
        {
            var v = ValueParser.Parse("  D-01 (Pintu kayu) ", ValueKind.Text, null);
            Assert.True(v.Ok);
            Assert.Equal("  D-01 (Pintu kayu) ", v.Text);
            Assert.Equal("2700", ValueParser.Parse(JsonDocument.Parse("2700").RootElement, ValueKind.Text, null).Text);
        }

        [Fact]
        public void JsonNumbersAreBareNumbers()
        {
            var v = ValueParser.Parse(JsonDocument.Parse("2700").RootElement, ValueKind.Length, "mm");
            Assert.True(v.Ok);
            Assert.Equal(2700.0, v.Number, 9);
        }

        [Fact]
        public void CanonicalFormRoundTripsAndIsStable()
        {
            var a = ValueParser.Parse("2.7m", ValueKind.Length, "mm");
            var b = ValueParser.Parse("2,700 mm", ValueKind.Length, "mm");
            Assert.Equal(a.Canonical, b.Canonical);
            Assert.Equal("2700 mm", a.Display);
            var back = ParsedValue.FromCanonical(a.Canonical);
            Assert.True(back.Ok);
            Assert.Equal(ValueKind.Length, back.Kind);
            Assert.Equal(2700.0, back.Number, 9);
            Assert.Equal("text|A,1", ValueParser.Parse("A,1", ValueKind.Text, null).Canonical);
            Assert.Equal("A,1", ParsedValue.FromCanonical("text|A,1").Text);
        }
    }
}
