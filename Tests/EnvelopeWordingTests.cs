#nullable enable
// summary/next are paraphrased to drafters: no query syntax, handles, regexes
// or second language in them. Revit-free.

using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Check;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class EnvelopeWordingTests
    {
        private static FindPlan PlanOf(string json)
        {
            Assert.True(QueryParser.TryParse(JsonDocument.Parse(json).RootElement.Clone(), out var plan, out var error), error);
            return plan;
        }

        private static Envelope EmptyFind(params string[] notSearched)
        {
            var outcome = new FindOutcome { Sources = { "host" }, Categories = { "Doors" } };
            outcome.NotSearched.AddRange(notSearched);
            outcome.Queries.Add(new QueryOutcome { Count = 0, Filter = "category = Doors", Subject = "Doors" });
            var env = FindResultBuilder.Build(PlanOf("{\"queries\":[{\"category\":\"Doors\"}]}"), outcome);
            Assert.Empty(EnvelopeInvariants.Violations(env));
            return env;
        }

        [Fact]
        public void Find_LoadedLinkLeftOut_IsNotToldToLoad()
        {
            var env = EmptyFind("link: STR_v3 (loaded; this search was host only)");
            Assert.DoesNotContain("load", env.Next!.Replace("loaded", ""), System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("source:", env.Next);
            Assert.Contains("Search again with the links included", env.Next);
        }

        [Fact]
        public void Find_UnloadedLink_PointsAtManageLinks()
        {
            var env = EmptyFind("link: ARC_Core (unloaded)");
            Assert.Contains("reload unloaded links in Manage Links", env.Next);
        }

        [Fact]
        public void Find_ClosedWorksets_OnlyInSearched()
        {
            var env = EmptyFind("worksets: MEP, Site (closed)");
            Assert.Contains("worksets: MEP, Site (closed)", env.Searched!.NotSearched);
            Assert.DoesNotContain("workset", env.Summary, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workset", env.Next!, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Find_ParameterConditionIsWords()
        {
            var q = PlanOf("{\"queries\":[{\"category\":\"Doors\",\"parameter\":{\"name\":\"Fire Rating\",\"op\":\"ne\",\"value\":\"60 min\"}}]}").Queries[0];
            Assert.Equal("Doors where Fire Rating is not 60 min", FindText.Subject(q, "Doors", null));
        }

        [Fact]
        public void Visibility_HasNoMalayFix()
        {
            var f = new VisibilityFacts { Ref = new ElementRef(1, null), Category = "Doors", CollectorVisible = false, ElementHidden = true };
            var env = VisibilityReasons.Build("Level 1", "FloorPlan", null, new[] { f });
            var reason = env.Data!.Value.GetProperty("groups")[0].GetProperty("reasons")[0];
            Assert.True(reason.TryGetProperty("fix_en", out _));
            Assert.False(reason.TryGetProperty("fix_ms", out _));
            Assert.DoesNotContain("fix_ms", env.Next);
        }

        [Fact]
        public void Code_LongOutputKeepsHeadAndTail()
        {
            var text = string.Join("\n", Enumerable.Range(1, 500).Select(i => "Wall " + i));
            var shown = BinaVibe.Copilot.Code.CodeEnvelopes.HeadTail(text, out var cut);
            Assert.True(cut);
            Assert.True(shown.Length <= 2100);
            Assert.StartsWith("Wall 1\n", shown);
            Assert.EndsWith("Wall 500", shown);
            Assert.Contains("lines omitted", shown);
            Assert.Equal("12 walls", BinaVibe.Copilot.Code.CodeEnvelopes.HeadTail("12 walls", out var short_));
            Assert.False(short_);
        }

        [Fact]
        public void Check_SummaryNamesRulesByTitle()
        {
            var rule = new Rule { Id = "MH-07", TitleEn = "Door marks follow D-nn", Cite = "BINA §1", Severity = RuleSeverity.Warning,
                Assertion = new RuleAssertion { Kind = AssertionKinds.Matches, Param = "Mark", Regex = "^D-\\d{2}$" } };
            var set = new RuleSet { Id = "model-health", Version = "1", Rules = { rule } };
            var run = new RuleRun(rule);
            run.Add(new ElementRef(1, null), Outcome.Fail("'Mark' = 'd1'; it is not in the required format", "d1"));
            var result = CheckReport.Build(set, new[] { run }, null);

            Assert.Equal("Door marks follow D-nn", result.Rules.Single().Title);
            var summary = CheckReport.Summary(set, result, new SkippedRule[0]);
            Assert.Contains("Fail: Door marks follow D-nn (1).", summary);
            Assert.DoesNotContain("MH-07", summary);
        }

        [Fact]
        public void Writes_NoChangeNextImpliesNoCard()
        {
            Assert.DoesNotContain("card", WriteEnvelopes.NoChangeNext);
            Assert.True(WriteEnvelopes.NoChangeNext.Length <= EnvelopeLimits.NextMaxChars);
        }
    }
}
