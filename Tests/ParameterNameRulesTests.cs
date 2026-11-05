// The name rule of the parameter pull (ClickUp 14zefhy2nny): Bina stores the name the user typed,
// Revit compares parameter names without regard to case. Comparing exactly would miss the model's
// own parameter, and the pull would bind a second shared parameter whose name already exists.
// Revit-free.

using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class ParameterNameRulesTests
    {
        [Theory]
        [InlineData("Mark", "Mark", true)]
        [InlineData("Mark", "mark", true)]
        [InlineData("Mark", "MARK", true)]
        [InlineData("  Fire Rating  ", "fire rating", true)]
        [InlineData("Mark", "Marks", false)]
        [InlineData("Fire Rating", "Fire  Rating", false)] // a different name, not just spacing
        [InlineData("", "Mark", false)]
        [InlineData(null, "Mark", false)]
        [InlineData(null, null, true)] // both empty: the caller rejects blank names anyway
        public void Matches_is_trimmed_and_case_insensitive(string left, string right, bool expected) =>
            Assert.Equal(expected, ParameterNameRules.Matches(left, right));

        [Theory]
        [InlineData("Mark", "Mark")]
        [InlineData("  Mark  ", "Mark")]
        [InlineData(null, "")]
        public void Normalize_trims_and_never_returns_null(string input, string expected) =>
            Assert.Equal(expected, ParameterNameRules.Normalize(input));

        [Theory]
        [InlineData("Mark", true)]
        [InlineData("  Mark ", true)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void IsUsable_rejects_blank_names(string name, bool expected) =>
            Assert.Equal(expected, ParameterNameRules.IsUsable(name));
    }
}
