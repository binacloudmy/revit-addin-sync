#nullable enable
// W2: argument parsing for the read tools (model_overview here; find_elements
// in Task 3). Revit-free: args JSON -> a normalized plan or an invalid_args
// message the model can act on.

using System.Collections.Generic;
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
        // ── find_elements ─────────────────────────────────────────────────

        private static FindPlan Plan(string json)
        {
            Assert.True(QueryParser.TryParse(J(json), out var plan, out var error), error);
            return plan;
        }

        private static string Error(string json)
        {
            Assert.False(QueryParser.TryParse(J(json), out _, out var error));
            return error;
        }

        [Fact]
        public void Find_InfersCategoryFromMalayWord_AndKeepsTheRestAsText()
        {
            var q = Plan("{\"queries\":[{\"text\":\"pintu kalis api\"}]}").Queries.Single();
            Assert.Equal("OST_Doors", q.CategoryOst);
            Assert.True(q.CategoryInferred);
            var term = Assert.Single(q.Terms);
            Assert.Equal("kalis api", term.Label);
            Assert.Contains("fire rated", term.Variants);
            Assert.Contains("kalis api", term.Variants);
        }

        [Fact]
        public void Find_ExtractsLevelPhraseFromText()
        {
            var q = Plan("{\"queries\":[{\"text\":\"tingkap aras 3\"}]}").Queries.Single();
            Assert.Equal("OST_Windows", q.CategoryOst);
            Assert.Equal("aras 3", q.LevelHint);
            Assert.Empty(q.Terms);

            var en = Plan("{\"queries\":[{\"text\":\"doors on Level 2\"}]}").Queries.Single();
            Assert.Equal("level 2", en.LevelHint);
            Assert.Equal("OST_Doors", en.CategoryOst);
            Assert.Empty(en.Terms);
        }

        [Fact]
        public void Find_ExplicitLevelWinsOverTextLevel()
        {
            var q = Plan("{\"queries\":[{\"text\":\"pintu aras 3\",\"level\":\"Level 2\"}]}").Queries.Single();
            Assert.Equal("Level 2", q.Level);
            Assert.Null(q.LevelHint);
        }

        [Fact]
        public void Find_ExplicitCategoryWins_SameCategoryWordIsDropped()
        {
            var q = Plan("{\"queries\":[{\"text\":\"door fire\",\"category\":\"Doors\"}]}").Queries.Single();
            Assert.Equal("Doors", q.CategoryText);
            Assert.Equal("OST_Doors", q.CategoryOst);
            Assert.False(q.CategoryInferred);
            var term = Assert.Single(q.Terms);
            Assert.Contains("kebakaran", term.Variants);
        }

        [Fact]
        public void Find_ExplicitMalayCategoryResolvesThroughSynonyms()
        {
            var q = Plan("{\"queries\":[{\"category\":\"pintu\"}]}").Queries.Single();
            Assert.Equal("OST_Doors", q.CategoryOst);
        }

        [Fact]
        public void Find_DropsStopWords()
        {
            var q = Plan("{\"queries\":[{\"text\":\"semua pintu yang ada di aras 2\"}]}").Queries.Single();
            Assert.Equal("OST_Doors", q.CategoryOst);
            Assert.Equal("aras 2", q.LevelHint);
            Assert.Empty(q.Terms);
        }

        [Fact]
        public void Find_PlainWordsBecomeTerms()
        {
            // A category word right after a code ("CL-02A grid", "600x600 grid") is part
            // of a type name, not the Grids category.
            var q = Plan("{\"queries\":[{\"text\":\"CL-02A grid\"}]}").Queries.Single();
            Assert.Null(q.CategoryOst);
            Assert.Contains(q.Terms, t => t.Variants.Contains("cl 02a"));
            Assert.Contains(q.Terms, t => t.Variants.Contains("grid"));
        }

        [Theory]
        [InlineData("host", FindSourceKind.Host, null)]
        [InlineData("ALL", FindSourceKind.All, null)]
        [InlineData("link:STR_v3", FindSourceKind.Link, "STR_v3")]
        [InlineData("link: STR_v3.rvt", FindSourceKind.Link, "STR_v3")]
        public void Find_ParsesSource(string source, FindSourceKind kind, string? link)
        {
            var q = Plan("{\"queries\":[{\"category\":\"Walls\",\"source\":\"" + source + "\"}]}").Queries.Single();
            Assert.Equal(kind, q.Source.Kind);
            Assert.Equal(link, q.Source.LinkName);
        }

        [Fact]
        public void Find_DefaultsToHostSource()
        {
            Assert.Equal(FindSourceKind.Host, Plan("{\"queries\":[{\"category\":\"Walls\"}]}").Queries[0].Source.Kind);
        }

        [Fact]
        public void Find_RejectsBadSource()
        {
            Assert.Contains("source", Error("{\"queries\":[{\"category\":\"Walls\",\"source\":\"linked\"}]}"));
            Assert.Contains("source", Error("{\"queries\":[{\"category\":\"Walls\",\"source\":\"link:\"}]}"));
        }

        [Fact]
        public void Find_ParsesParameterConditions()
        {
            var gt = Plan("{\"queries\":[{\"category\":\"Ceilings\",\"parameter\":{\"name\":\"Height Offset From Level\",\"op\":\"gt\",\"value\":\"2.7 m\"}}]}")
                .Queries[0].Parameter!;
            Assert.Equal("gt", gt.Op);
            Assert.Equal(2.7, gt.Number!.Value, 6);
            Assert.Equal("m", gt.Unit);

            var empty = Plan("{\"queries\":[{\"category\":\"Doors\",\"parameter\":{\"name\":\"Fire Rating\",\"op\":\"empty\"}}]}").Queries[0].Parameter!;
            Assert.Equal("empty", empty.Op);

            var num = Plan("{\"queries\":[{\"category\":\"Doors\",\"parameter\":{\"name\":\"Width\",\"op\":\"eq\",\"value\":900}}]}").Queries[0].Parameter!;
            Assert.Equal(900, num.Number!.Value);
            Assert.Equal("", num.Unit);
        }

        [Theory]
        [InlineData("{\"name\":\"Width\",\"op\":\"gt\"}", "value")]
        [InlineData("{\"name\":\"Width\",\"op\":\"gt\",\"value\":\"wide\"}", "number")]
        [InlineData("{\"name\":\"Width\",\"op\":\"between\",\"value\":1}", "op")]
        [InlineData("{\"op\":\"eq\",\"value\":1}", "name")]
        public void Find_RejectsBadParameterConditions(string condition, string mentions)
        {
            Assert.Contains(mentions, Error("{\"queries\":[{\"category\":\"Doors\",\"parameter\":" + condition + "}]}"));
        }

        [Fact]
        public void Find_ParsesGroupByHandleHighlightAndClampsLimit()
        {
            var plan = Plan("{\"queries\":[{\"category\":\"Doors\"}],\"group_by\":\"param:Fire Rating\",\"handle\":\"h_7f3a91c2\",\"highlight\":true,\"zoom\":true,\"limit\":80}");
            Assert.Equal("param", plan.GroupBy!.Kind);
            Assert.Equal("Fire Rating", plan.GroupBy.Param);
            Assert.Equal("h_7f3a91c2", plan.Handle);
            Assert.True(plan.Highlight);
            Assert.True(plan.Zoom);
            Assert.Equal(QueryParser.MaxLimit, plan.Limit);
            Assert.Equal("level", Plan("{\"queries\":[{\"category\":\"Doors\"}],\"group_by\":\"Level\"}").GroupBy!.Kind);
            Assert.Contains("group_by", Error("{\"queries\":[{\"category\":\"Doors\"}],\"group_by\":\"workset\"}"));
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"queries\":[]}")]
        [InlineData("{\"queries\":[{},{},{},{},{},{}]}")]
        public void Find_RejectsZeroOrMoreThanFiveQueries(string json)
        {
            Assert.Contains("1-5 queries", Error(json));
        }

        [Fact]
        public void Find_EmptyQueryNeedsAHandle()
        {
            Assert.Contains("query 1", Error("{\"queries\":[{}]}"));
            Assert.True(QueryParser.TryParse(J("{\"queries\":[{}],\"handle\":\"h_7f3a91c2\"}"), out _, out _));
        }

        [Fact]
        public void Find_DescribesTheFilterInWords()
        {
            var q = Plan("{\"queries\":[{\"text\":\"pintu kalis api\",\"level\":\"Level 3\"}]}").Queries[0];
            Assert.Equal("category = Doors AND level = 'Level 3' AND text ~ 'kalis api' (fire rated)",
                FindText.Filter(q, "Doors", new[] { "Level 3" }));
            Assert.Equal("Doors on Level 3 matching 'kalis api' (fire rated)", FindText.Subject(q, "Doors", new[] { "Level 3" }));
        }

        // ── level names, text and parameter matching ──────────────────────

        [Fact]
        public void LevelMatcher_PrefersExactThenPureNumberedLevels()
        {
            // aras / level / tingkat / lvl are the same word; leading zeros do not matter.
            var levels = new[] { "Level 1", "Level 3", "Level 3A", "Aras 3 Mezzanine", "Roof" };
            Assert.Equal(new[] { "Level 3" }, LevelMatcher.Match("aras 3", levels));
            Assert.Equal(new[] { "Level 3" }, LevelMatcher.Match("Level 03", levels));
            Assert.Equal(new[] { "Level 3" }, LevelMatcher.Match("3", levels));
            Assert.Equal(new[] { "ARAS 03", "Level 3" }, LevelMatcher.Match("level 3", new[] { "ARAS 03", "Level 3", "Level 13" }));
            Assert.Equal(new[] { "Aras 3 Mezzanine" }, LevelMatcher.Match("aras 3", new[] { "Level 1", "Aras 3 Mezzanine" }));
            Assert.Equal(new[] { "Roof" }, LevelMatcher.Match("bumbung", levels));
            Assert.Empty(LevelMatcher.Match("aras 7", levels));
            Assert.Equal(new[] { "Ground Floor" }, LevelMatcher.Match("aras bawah", new[] { "Ground Floor", "Level 1" }));
        }

        [Fact]
        public void TextMatch_UsesSynonymsCompactFormsAndWordBoundaries()
        {
            var terms = Plan("{\"queries\":[{\"text\":\"pintu kalis api\"}]}").Queries[0].Terms;
            Assert.True(TextMatch.Matches(terms, new[] { "Door - Fire Rated 60 min" }));
            Assert.True(TextMatch.Matches(terms, new[] { "D-FireRated-900" }));
            Assert.True(TextMatch.Matches(terms, new[] { null, "Pintu Kalis Api 1 Jam" }));
            Assert.False(TextMatch.Matches(terms, new[] { "Single Flush 900" }));

            var shortTerm = new List<TermGroup> { new TermGroup("fr", new List<string> { "fr" }) };
            Assert.False(TextMatch.Matches(shortTerm, new[] { "Door Frame" }));
            Assert.True(TextMatch.Matches(shortTerm, new[] { "Door FR 60" }));
        }

        [Theory]
        [InlineData("gt", "2.3 m", "2400 mm", "mm", true)]
        [InlineData("lt", "2400", "2400 mm", "mm", false)]
        [InlineData("eq", "2,400 mm", "2400 mm", "mm", true)]
        [InlineData("eq", "2400", "2400 mm", "mm", true)]
        [InlineData("ne", "60 min", "120 min", "", true)]
        [InlineData("eq", "fire rated", "Fire Rated", "", true)]
        [InlineData("contains", "60", "FRL 60/60/60", "", true)]
        [InlineData("empty", null, null, "", true)]
        [InlineData("empty", null, "", "", true)]
        [InlineData("not_empty", null, "D01", "", true)]
        [InlineData("gt", "2", "abc", "", false)]
        public void ParamCondition_EvaluatesInDisplayUnits(string op, string? value, string? display, string unit, bool expected)
        {
            Assert.True(ParamCondition.TryCreate("P", op, value, out var c, out var error), error);
            Assert.Equal(expected, c.Evaluate(display, unit));
        }
    }
}
