using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class FittingWatchPolicyTests
    {
        private static WritePlan Plan(params string[] canonicals)
        {
            var plan = new WritePlan { Tool = ToolNames.SetParameters };
            for (var i = 0; i < canonicals.Length; i++) plan.IntendedValues[(i + 1).ToString()] = canonicals[i];
            return plan;
        }

        private static string Canonical(string raw, ValueKind kind, string displayUnit = null) =>
            ValueParser.Parse(raw, kind, displayUnit).Canonical;

        [Fact]
        public void RunRevitCode_IsAlwaysWatched()
        {
            Assert.True(FittingWatchPolicy.ShouldWatch(ToolNames.RunRevitCode, new WritePlan { Tool = ToolNames.RunRevitCode }));
            Assert.True(FittingWatchPolicy.ShouldWatch(ToolNames.RunRevitCode, null));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("some_future_write_tool")]
        [InlineData("SET_PARAMETERS")]
        public void AToolThatIsNotSetParameters_IsWatched(string tool)
        {
            Assert.True(FittingWatchPolicy.ShouldWatch(tool, Plan("text|A1")));
        }

        [Fact]
        public void SetParameters_TypeChangeByName_IsWatched()
        {
            // "Type" / "Family and Type" are ElementId parameters: a name parses to an element reference.
            var canonical = Canonical("PVC - Class D", ValueKind.ElementId);
            Assert.StartsWith("elementname|", canonical);
            Assert.True(FittingWatchPolicy.ShouldWatch(ToolNames.SetParameters, Plan(canonical)));
        }

        [Fact]
        public void SetParameters_TypeChangeById_IsWatched()
        {
            var canonical = Canonical("142445", ValueKind.ElementId);
            Assert.StartsWith("elementid|", canonical);
            Assert.True(FittingWatchPolicy.ShouldWatch(ToolNames.SetParameters, Plan(canonical)));
        }

        [Fact]
        public void SetParameters_OneElementReferenceAmongOtherValues_IsWatched()
        {
            Assert.True(FittingWatchPolicy.ShouldWatch(ToolNames.SetParameters, Plan("text|A1", "elementid|142445", "text|A2")));
        }

        [Fact]
        public void SetParameters_TextNumbersAndYesNo_AreNotWatched()
        {
            var plan = Plan(
                Canonical("A1", ValueKind.Text),
                Canonical("3", ValueKind.Integer),
                Canonical("ya", ValueKind.YesNo),
                Canonical("1.5", ValueKind.Number),
                Canonical("2700 mm", ValueKind.Length),
                Canonical("12.5 m²", ValueKind.Area, "m2"),
                Canonical("500 l", ValueKind.Volume, "m3"),
                Canonical("90°", ValueKind.Angle, "°"),
                Canonical("10 W", ValueKind.Measured));
            Assert.All(plan.IntendedValues.Values, c => Assert.True(ParsedValue.FromCanonical(c).Ok, c));
            Assert.False(FittingWatchPolicy.ShouldWatch(ToolNames.SetParameters, plan));
        }

        [Fact]
        public void SetParameters_TextThatLooksLikeATypeName_IsNotWatched()
        {
            // The value's kind decides, never its wording: text written into a text parameter stays text.
            Assert.False(FittingWatchPolicy.ShouldWatch(ToolNames.SetParameters, Plan("text|elementid|142445", "text|Family and Type")));
        }

        [Fact]
        public void SetParameters_NothingToWrite_IsNotWatched()
        {
            Assert.False(FittingWatchPolicy.ShouldWatch(ToolNames.SetParameters, Plan()));
        }

        [Theory]
        [InlineData("")]
        [InlineData("no bar")]
        [InlineData("elementid|not-a-number")]
        [InlineData("mystery|1")]
        public void SetParameters_AValueThatCannotBeDecoded_IsWatched(string canonical)
        {
            Assert.True(FittingWatchPolicy.ShouldWatch(ToolNames.SetParameters, Plan("text|A1", canonical)));
        }

        [Fact]
        public void SetParameters_WithoutAPlan_IsWatched()
        {
            Assert.True(FittingWatchPolicy.ShouldWatch(ToolNames.SetParameters, null));
            Assert.True(FittingWatchPolicy.ShouldWatch(ToolNames.SetParameters, new WritePlan { IntendedValues = null }));
        }
    }
}
