#nullable enable
// W6 Task 1 (lean-harness plan §5 W6): RuleSetLoader.Parse turns the cloud's
// args.rule_set_content into a RuleSet, and rejects anything the check engine
// could not run honestly: unknown kinds, unknown units, a unit that does not
// fit the kind, regexes .NET cannot compile, keys outside the closed schema.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using BinaVibe.Copilot.Check;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>Shared fixture access for the W6 check tests.</summary>
    internal static class CheckFixtures
    {
        public static string ContractsDir([CallerFilePath] string thisFile = "")
        {
            var copied = Path.Combine(AppContext.BaseDirectory, "contracts", "copilot");
            if (File.Exists(Path.Combine(copied, "ruleset.schema.json"))) return copied;

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "contracts", "copilot");
                if (File.Exists(Path.Combine(candidate, "ruleset.schema.json"))) return candidate;
            }

            var fromSource = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile) ?? ".", "..", "contracts", "copilot"));
            if (File.Exists(Path.Combine(fromSource, "ruleset.schema.json"))) return fromSource;

            throw new DirectoryNotFoundException("contracts/copilot not found near the test assembly or " + thisFile);
        }

        public static string ModelHealthJson() =>
            File.ReadAllText(Path.Combine(ContractsDir(), "examples", "ruleset-model-health.json"));

        public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

        /// <summary>A one-rule set around the given assertion JSON.</summary>
        public static string OneRule(string assertionJson, string selectorJson = "{\"categories\":[\"Doors\"]}", string extraRuleJson = "") =>
            "{\"id\":\"t-set\",\"version\":\"1\",\"title_en\":\"T\",\"title_ms\":\"T\"," +
            "\"source\":{\"name\":\"test\",\"edition\":\"1\"}," +
            "\"rules\":[{\"id\":\"R-1\",\"title_en\":\"T\",\"title_ms\":\"T\",\"cite\":\"T §1\",\"severity\":\"warning\"," +
            "\"selector\":" + selectorJson + ",\"assertion\":" + assertionJson + extraRuleJson + "," +
            "\"fix_hint_en\":\"fix\",\"fix_hint_ms\":\"baiki\"}]}";

        /// <summary>The model-health example with one JSON edit applied.</summary>
        public static JsonElement EditedModelHealth(Action<JsonObject> edit)
        {
            var node = JsonNode.Parse(ModelHealthJson())!.AsObject();
            edit(node);
            return Parse(node.ToJsonString());
        }
    }

    public class RuleSetParseTests
    {
        private static RuleSetInvalidException Rejects(string json) =>
            Assert.Throws<RuleSetInvalidException>(() => RuleSetLoader.Parse(CheckFixtures.Parse(json)));

        private static RuleSetInvalidException Rejects(JsonElement json) =>
            Assert.Throws<RuleSetInvalidException>(() => RuleSetLoader.Parse(json));

        [Fact]
        public void ParsesContractExample()
        {
            var set = RuleSetLoader.Parse(CheckFixtures.Parse(CheckFixtures.ModelHealthJson()));

            Assert.Equal("model-health", set.Id);
            Assert.Equal("2026.10.0", set.Version);
            Assert.Equal(9, set.Rules.Count);
            Assert.Equal(set.Rules.Count, set.Rules.Select(r => r.Id).Distinct().Count());

            var width = set.Rules.Single(r => r.Id == "MH-08");
            Assert.Equal(AssertionKinds.Between, width.Assertion.Kind);
            Assert.Equal(600, width.Assertion.Min);
            Assert.Equal(2400, width.Assertion.Max);
            Assert.Equal("mm", width.Assertion.Unit);
            Assert.True(width.UnverifiedValue);
            Assert.Equal("Width", width.Selector.HasParam);

            var levels = set.Rules.Single(r => r.Id == "MH-09");
            Assert.Equal(AssertionKinds.LevelMatchesLink, levels.Assertion.Kind);
            Assert.Equal("STR_v3", levels.Assertion.Link);
            Assert.Equal(5, levels.Assertion.ToleranceMm);
            Assert.Equal(SelectorLinks.Host, levels.Selector.Link);

            var warnings = set.Rules.Single(r => r.Id == "MH-02");
            Assert.Empty(warnings.Selector.Categories);
            Assert.Null(warnings.Selector.Link);
        }

        [Fact]
        public void RejectsUnknownKind()
        {
            var ex = Rejects(CheckFixtures.OneRule("{\"kind\":\"looks_nice\"}"));
            Assert.Contains("R-1", ex.Message);
            Assert.Contains("looks_nice", ex.Message);
        }

        [Fact]
        public void RejectsMissingKind()
        {
            var ex = Rejects(CheckFixtures.OneRule("{\"param\":\"Mark\"}"));
            Assert.Contains("kind", ex.Message);
        }

        [Fact]
        public void RejectsUnknownUnit()
        {
            var ex = Rejects(CheckFixtures.OneRule("{\"kind\":\"between\",\"param\":\"Width\",\"min\":1,\"unit\":\"furlong\"}"));
            Assert.Contains("furlong", ex.Message);
        }

        [Fact]
        public void RejectsUnitOfTheWrongDimension()
        {
            var ex = Rejects(CheckFixtures.OneRule("{\"kind\":\"clear_width_ge\",\"min\":850,\"unit\":\"m2\"}"));
            Assert.Contains("m2", ex.Message);
            Assert.Contains("length", ex.Message);
        }

        [Theory]
        [InlineData("mm")]
        [InlineData("cm")]
        [InlineData("m")]
        [InlineData("m2")]
        [InlineData("m²")]
        [InlineData("m3")]
        [InlineData("deg")]
        [InlineData("°")]
        public void AcceptsKnownUnitsOnBetween(string unit)
        {
            var set = RuleSetLoader.Parse(CheckFixtures.Parse(
                CheckFixtures.OneRule("{\"kind\":\"between\",\"param\":\"P\",\"min\":1,\"unit\":\"" + unit + "\"}")));
            Assert.Equal(unit, set.Rules[0].Assertion.Unit);
        }

        [Theory]
        [InlineData("{\"kind\":\"not_empty\"}", "param")]
        [InlineData("{\"kind\":\"matches\",\"param\":\"Mark\"}", "regex")]
        [InlineData("{\"kind\":\"in_list\",\"param\":\"Mark\"}", "values")]
        [InlineData("{\"kind\":\"in_list\",\"param\":\"Mark\",\"values\":[]}", "values")]
        [InlineData("{\"kind\":\"between\",\"param\":\"Width\"}", "min")]
        [InlineData("{\"kind\":\"equals_param\",\"param\":\"Mark\"}", "other_param")]
        [InlineData("{\"kind\":\"count_le\"}", "max")]
        [InlineData("{\"kind\":\"clear_width_ge\"}", "min")]
        [InlineData("{\"kind\":\"turning_circle_ge\",\"unit\":\"mm\"}", "min")]
        public void RejectsMissingKindRequirement(string assertion, string missing)
        {
            var ex = Rejects(CheckFixtures.OneRule(assertion));
            Assert.Contains(missing, ex.Message);
        }

        [Fact]
        public void RejectsBetweenWithMinAboveMax()
        {
            var ex = Rejects(CheckFixtures.OneRule("{\"kind\":\"between\",\"param\":\"Width\",\"min\":900,\"max\":600,\"unit\":\"mm\"}"));
            Assert.Contains("min", ex.Message);
        }

        [Fact]
        public void RejectsRegexDotNetCannotCompile()
        {
            // Python named groups are not .NET syntax: fail at load, not mid-check.
            var ex = Rejects(CheckFixtures.OneRule("{\"kind\":\"matches\",\"param\":\"Mark\",\"regex\":\"^(?P<x>D)\\\\d+$\"}"));
            Assert.Contains("regex", ex.Message);
        }

        [Fact]
        public void RejectsBadTypeNameRegex()
        {
            var ex = Rejects(CheckFixtures.OneRule("{\"kind\":\"exists\",\"param\":\"Mark\"}",
                "{\"categories\":[\"Doors\"],\"type_name_regex\":\"([\"}"));
            Assert.Contains("type_name_regex", ex.Message);
        }

        [Fact]
        public void RejectsDuplicateRuleIds()
        {
            var json = CheckFixtures.EditedModelHealth(o => o["rules"]![1]!["id"] = "MH-01");
            var ex = Rejects(json);
            Assert.Contains("MH-01", ex.Message);
        }

        [Fact]
        public void IgnoresUnknownKeysSoANewerBackendDoesNotBreakThisAddIn()
        {
            // contracts/copilot/README.md: readers ignore unknown keys. Typos are
            // caught where rule files are authored (the backend's schema tests).
            var set = RuleSetLoader.Parse(CheckFixtures.Parse(CheckFixtures.OneRule(
                "{\"kind\":\"duplicate_instances\",\"tolerance_mm\":2,\"future_knob\":true}",
                "{\"categories\":[\"Doors\"],\"room\":\"Tandas OKU\"}",
                ",\"added_later\":{\"x\":1}")));
            Assert.Equal(2, set.Rules.Single().Assertion.ToleranceMm);
            Assert.Equal("Doors", set.Rules.Single().Selector.Categories.Single());
        }

        [Fact]
        public void RejectsUnknownSeverityAndLink()
        {
            var json = CheckFixtures.EditedModelHealth(o =>
            {
                o["rules"]![0]!["severity"] = "fatal";
                o["rules"]![0]!["selector"]!["link"] = "STR_v3";
            });
            var ex = Rejects(json);
            Assert.Contains("fatal", ex.Message);
            Assert.Contains("STR_v3", ex.Message);
        }

        [Fact]
        public void RejectsNegativeTolerance()
        {
            var ex = Rejects(CheckFixtures.OneRule("{\"kind\":\"grid_aligned_with_link\",\"tolerance_mm\":-1}",
                "{\"categories\":[\"Grids\"]}"));
            Assert.Contains("tolerance_mm", ex.Message);
        }

        [Fact]
        public void RejectsWrongTypesAndEmptyRules()
        {
            Assert.Contains("min", Rejects(CheckFixtures.OneRule("{\"kind\":\"between\",\"param\":\"W\",\"min\":\"600\"}")).Message);
            Assert.Contains("rules", Rejects(CheckFixtures.EditedModelHealth(o => o["rules"] = new JsonArray())).Message);
            Assert.Contains("object", Rejects("[]").Message);
        }

        [Fact]
        public void RejectsNonKebabId()
        {
            var ex = Rejects(CheckFixtures.EditedModelHealth(o => o["id"] = "Model Health"));
            Assert.Contains("id", ex.Message);
        }

        [Fact]
        public void AcceptsExplicitNullsForOptionalFields()
        {
            var set = RuleSetLoader.Parse(CheckFixtures.Parse(CheckFixtures.OneRule(
                "{\"kind\":\"not_empty\",\"param\":\"Mark\",\"unit\":null,\"regex\":null}",
                "{\"categories\":[\"Doors\"],\"levels\":null,\"link\":null}",
                ",\"unverified_value\":null")));
            var rule = set.Rules.Single();
            Assert.Null(rule.UnverifiedValue);
            Assert.Null(rule.Selector.Link);
            Assert.Null(rule.Assertion.Unit);
        }

        [Fact]
        public void ListsEveryProblemAtOnce()
        {
            var json = CheckFixtures.EditedModelHealth(o =>
            {
                o["rules"]![0]!["assertion"]!["kind"] = "nope";
                o["rules"]![7]!["assertion"]!["unit"] = "parsec";
            });
            var ex = Rejects(json);
            Assert.True(ex.Problems.Count >= 2, string.Join(" | ", ex.Problems));
            Assert.Contains(ex.Problems, p => p.Contains("MH-01") && p.Contains("nope"));
            Assert.Contains(ex.Problems, p => p.Contains("MH-08") && p.Contains("parsec"));
        }
    }
}
