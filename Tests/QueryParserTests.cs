#nullable enable
// W2: argument parsing for the read tools (model_overview here; find_elements
// in Task 3). Revit-free: args JSON -> a normalized plan or an invalid_args
// message the model can act on.

using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Read;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class QueryParserTests
    {
        private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

        // ── model_overview ────────────────────────────────────────────────

        [Fact]
        public void Overview_ParsesSectionsCategoryAndLimit()
        {
            Assert.True(OverviewSections.TryParse(J("{\"sections\":[\"levels\",\"Types\"],\"category\":\" Doors \",\"limit\":20}"),
                out var a, out var error), error);
            Assert.Equal(new[] { "levels", "types" }, a.Sections);
            Assert.Equal("Doors", a.Category);
            Assert.Equal(20, a.Limit);
        }

        [Fact]
        public void Overview_DefaultsAndClampsLimit_DedupesSections()
        {
            Assert.True(OverviewSections.TryParse(J("{\"sections\":[\"views\",\"views\"]}"), out var a, out _));
            Assert.Equal(new[] { "views" }, a.Sections);
            Assert.Equal(OverviewSections.DefaultLimit, a.Limit);
            Assert.Null(a.Category);

            Assert.True(OverviewSections.TryParse(J("{\"sections\":[\"views\"],\"limit\":5000}"), out var big, out _));
            Assert.Equal(OverviewSections.MaxLimit, big.Limit);
            Assert.True(OverviewSections.TryParse(J("{\"sections\":[\"views\"],\"limit\":0}"), out var small, out _));
            Assert.Equal(1, small.Limit);
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"sections\":[]}")]
        [InlineData("{\"sections\":\"levels\"}")]
        [InlineData("[]")]
        public void Overview_RejectsMissingSections(string json)
        {
            Assert.False(OverviewSections.TryParse(J(json), out _, out var error));
            Assert.Contains("sections", error);
            Assert.Contains("levels", error);              // the allowed list is in the message
        }

        [Fact]
        public void Overview_RejectsUnknownSectionNamingIt()
        {
            Assert.False(OverviewSections.TryParse(J("{\"sections\":[\"levels\",\"doors\"]}"), out _, out var error));
            Assert.Contains("'doors'", error);
            Assert.Contains("types", error);
        }
        // ── fuzzy candidates (not_found and near matches) ────────────────

        [Fact]
        public void Fuzzy_RanksCloseNamesFirst()
        {
            var levels = new[] { "Level 1", "Level 2", "Level 3", "Roof", "Ground Floor" };
            var ranked = Fuzzy.Rank("level 3", levels, 3);
            Assert.Equal("Level 3", ranked[0]);
            Assert.Contains("Level 2", ranked);
            Assert.DoesNotContain("Roof", ranked);
        }

        [Fact]
        public void Fuzzy_HandlesTyposCaseAndAccents()
        {
            var types = new[] { "CL-02A 600x600 Grid", "CL-20 Plasterboard", "Single Flush 900" };
            Assert.Equal("CL-02A 600x600 Grid", Fuzzy.Rank("cl02a", types, 1).Single());
            Assert.Equal("CL-20 Plasterboard", Fuzzy.Rank("plasterbord", types, 1).Single());
            Assert.Equal("Café", Fuzzy.Rank("cafe", new[] { "Café", "Corridor" }, 1).Single());
        }

        [Fact]
        public void Fuzzy_ReturnsEmptyWhenNothingIsClose()
        {
            Assert.Empty(Fuzzy.Rank("zzzz", new[] { "Level 1", "Roof" }, 5));
            Assert.Empty(Fuzzy.Rank("", new[] { "Level 1" }, 5));
        }
    }
}
