#nullable enable
// Envelope wording the cloud model paraphrases to drafters ("jumbled replies"):
// summary and next are plain words, with query syntax, handles, ids, raw regexes
// and a second language kept in structured fields. Revit-free.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using BinaVibe.Copilot.Check;
using BinaVibe.Copilot.Code;
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

        private static void AssertPlain(Envelope e)
        {
            Assert.Empty(EnvelopeInvariants.Violations(e));
            foreach (var text in new[] { e.Summary, e.Next ?? "" })
            {
                Assert.DoesNotContain("source:", text);
                Assert.DoesNotContain(" ~ ", text);
                Assert.DoesNotContain("!=", text);
                Assert.DoesNotContain("h_", text);
                Assert.DoesNotContain("fix_ms", text);
            }
        }

        // ── find ────────────────────────────────────────────────────────────

        [Fact]
        public void Find_LoadedLinkOutsideHostSearch_NeverSaysLoadIt_AndRetryIsData()
        {
            var plan = PlanOf("{\"queries\":[{\"category\":\"Structural Columns\"}]}");
            var outcome = new FindOutcome
            {
                Sources = { "host" },
                Categories = { "Structural Columns" },
                NotSearched = { "link: STR_v3 (loaded; this search covered the host model only)" },
                LoadedLinksOutside = 1,
            };
            outcome.Queries.Add(new QueryOutcome { Count = 0, Filter = "category = Structural Columns", Subject = "Structural Columns" });

            var env = FindResultBuilder.Build(plan, outcome);

            AssertPlain(env);
            Assert.DoesNotContain("Load", env.Next!);
            Assert.DoesNotContain("load it", env.Next!, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Only the host model was searched", env.Next);
            Assert.DoesNotContain("…", env.Next);
            Assert.Equal("all", env.Data!.Value.GetProperty("retry").GetProperty("source").GetString());
        }

        [Fact]
        public void Find_UnloadedLink_SaysNotLoaded_AndNoRetry()
        {
            var plan = PlanOf("{\"queries\":[{\"category\":\"Doors\"}]}");
            var outcome = new FindOutcome { Sources = { "host" }, Categories = { "Doors" }, NotSearched = { "link: ARC_Core (unloaded; 3 handle elements)" } };
            outcome.Queries.Add(new QueryOutcome { Count = 0, Filter = "category = Doors", Subject = "Doors" });

            var env = FindResultBuilder.Build(plan, outcome);

            AssertPlain(env);
            Assert.StartsWith("Link ARC_Core is not loaded", env.Next);
            Assert.False(env.Data!.Value.TryGetProperty("retry", out _));
        }

        [Fact]
        public void Find_ClosedWorksets_InNotSearchedOnly_AndInNextOnlyOncePerTurn()
        {
            var plan = PlanOf("{\"queries\":[{\"category\":\"Doors\"}]}");
            FindOutcome Outcome(bool first)
            {
                var o = new FindOutcome { Sources = { "host" }, Categories = { "Doors" }, Handle = "h_0000abcd", ClosedWorksets = { "MEP", "Site" }, FirstClosedWorksetNotice = first };
                o.Rows.Add(new FoundRow { Ref = new ElementRef(1, null), Category = "Doors", Type = "D1", GroupKey = "D1" });
                o.Queries.Add(new QueryOutcome { Count = 1, Filter = "category = Doors", Subject = "Doors" });
                return o;
            }

            var first = FindResultBuilder.Build(plan, Outcome(first: true));
            var later = FindResultBuilder.Build(plan, Outcome(first: false));

            foreach (var env in new[] { first, later })
            {
                AssertPlain(env);
                Assert.DoesNotContain("workset", env.Summary, System.StringComparison.OrdinalIgnoreCase);
                Assert.Contains("worksets: MEP, Site (closed, so their elements were not searched)", env.Searched!.NotSearched);
            }
            Assert.Contains("worksets are closed", first.Next);
            Assert.DoesNotContain("workset", later.Next!, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Find_EmptyNext_EverythingAtOnce_FitsWholeSentences()
        {
            var plan = PlanOf("{\"queries\":[{\"category\":\"Doors\"}]}");
            var outcome = new FindOutcome
            {
                Sources = { "host" }, Categories = { "Doors" },
                NotSearched = { "link: ARC_Core_Very_Long_Link_Name_For_Block_A_2026 (unloaded)", "link: STR_v3 (loaded; this search covered the host model only)" },
                LoadedLinksOutside = 1, ClosedWorksets = { "MEP" }, FirstClosedWorksetNotice = true,
                NearMatches = { "D1: 3 on Level 2" },
            };
            outcome.Queries.Add(new QueryOutcome { Count = 0, Filter = "category = Doors", Subject = "Doors" });

            var env = FindResultBuilder.Build(plan, outcome);

            AssertPlain(env);
            Assert.True(EnvelopeInvariants.CodePoints(env.Next!) <= EnvelopeLimits.NextMaxChars);
            Assert.DoesNotContain("…", env.Next);
            Assert.EndsWith(".", env.Next);
            Assert.StartsWith("Link ARC_Core_Very_Long_Link_Name_For_Block_A_2026 is not loaded", env.Next);
        }

        [Fact]
        public void TurnNotices_FirstPerTurnOnly()
        {
            Assert.True(TurnNotices.First("turn-a", "closed_worksets"));
            Assert.False(TurnNotices.First("turn-a", "closed_worksets"));
            Assert.True(TurnNotices.First("turn-b", "closed_worksets"));
            Assert.True(TurnNotices.First("", "closed_worksets"));   // unknown turn: always say it
        }

        [Fact]
        public void Find_ParameterConditionInSubject_IsWords_FilterKeepsOperator()
        {
            var plan = PlanOf("{\"queries\":[{\"category\":\"Doors\",\"parameter\":{\"name\":\"Fire Rating\",\"op\":\"ne\",\"value\":\"60 min\"}}]}");
            var q = plan.Queries[0];
            Assert.Equal("Doors where Fire Rating is not 60 min", FindText.Subject(q, "Doors", null));
            Assert.Contains("Fire Rating != 60 min", FindText.Filter(q, "Doors", null));
        }

        // ── inspect ─────────────────────────────────────────────────────────

        [Fact]
        public void Visibility_OneFixLanguage_NoFieldNamesInNext()
        {
            var f = new VisibilityFacts { Ref = new ElementRef(1, null), Category = "Doors", CollectorVisible = false, ElementHidden = true };
            var env = VisibilityReasons.Build("Level 1", "FloorPlan", null, new[] { f });

            AssertPlain(env);
            var reason = env.Data!.Value.GetProperty("groups")[0].GetProperty("reasons")[0];
            Assert.True(reason.TryGetProperty("fix", out var fix));
            Assert.False(string.IsNullOrWhiteSpace(fix.GetString()));
            Assert.False(reason.TryGetProperty("fix_ms", out _));
            Assert.False(reason.TryGetProperty("fix_en", out _));
            Assert.Contains("drafter's language only", env.Next);
        }

        // ── run_revit_code ──────────────────────────────────────────────────

        [Fact]
        public void Code_LongOutput_ReturnsHeadAndTailUnder2KB()
        {
            var output = new OutputBuffer();
            for (var i = 1; i <= 400; i++) output.Print("Wall " + i + ": Basic Wall 200mm");
            var env = CodeEnvelopes.Read("List walls", output, new CodeRunResult());

            Assert.Empty(EnvelopeInvariants.Violations(env));
            var data = env.Data!.Value;
            var text = data.GetProperty("output").GetString()!;
            Assert.True(Encoding.UTF8.GetByteCount(text) <= OutputBuffer.ReturnedBytes);
            Assert.StartsWith("Wall 1: ", text);
            Assert.EndsWith("Wall 400: Basic Wall 200mm", text);
            var omitted = data.GetProperty("omitted_lines").GetInt32();
            Assert.Contains("… " + omitted + " lines omitted …", text);
            Assert.True(data.GetProperty("truncated").GetBoolean());
            Assert.Equal(400, data.GetProperty("lines").GetInt32());
            Assert.Contains("start and end", env.Next);
        }

        [Fact]
        public void Code_ShortOutput_IsUnchanged()
        {
            var output = new OutputBuffer();
            output.Print("12 walls");
            var env = CodeEnvelopes.Read("Count walls", output, new CodeRunResult());
            Assert.Equal("12 walls", env.Data!.Value.GetProperty("output").GetString());
            Assert.False(env.Data.Value.TryGetProperty("omitted_lines", out _));
            Assert.Null(env.Next);
        }

        [Fact]
        public void Code_PointsPrintInMillimetres()
        {
            Assert.Equal("(1000, 2500, 0) mm", OutputBuffer.PointMm(1000 / 304.8, 2500 / 304.8, 0));
        }

        // ── check ───────────────────────────────────────────────────────────

        private static Rule Rule(string id, string title, RuleAssertion a) => new Rule
        {
            Id = id, TitleEn = title, TitleMs = title, Cite = "BINA §1", Severity = RuleSeverity.Warning, Assertion = a,
        };

        [Fact]
        public void Check_NamesRulesByTitle_WithPlainExpectation_RegexInPatternOnly()
        {
            var marks = Rule("MH-07", "Door marks follow D-nn", new RuleAssertion { Kind = AssertionKinds.Matches, Param = "Mark", Regex = "^D-\\d{2}$", Example = "D-01" });
            var height = Rule("MH-11", "Door height 2100 to 2400 mm", new RuleAssertion { Kind = AssertionKinds.Between, Param = "Height", Min = 2100, Max = 2400, Unit = "mm" });
            var set = new RuleSet { Id = "model-health", Version = "1", Rules = { marks, height } };
            var r1 = new RuleRun(marks);
            r1.Add(new ElementRef(1, null), Outcome.Fail("'Mark' = 'd1'; it should look like 'D-01'", "d1"));
            var r2 = new RuleRun(height);
            r2.Add(new ElementRef(2, null), Outcome.Pass("2100 mm"));

            var result = CheckReport.Build(set, new[] { r1, r2 }, null);
            var summary = CheckReport.Summary(set, result, new List<SkippedRule>());

            var mh07 = result.Rules[0];
            Assert.Equal("Door marks follow D-nn", mh07.Title);
            Assert.Equal("'Mark' follows the required format, for example 'D-01'", mh07.Expectation);
            Assert.Equal("^D-\\d{2}$", mh07.Pattern);
            Assert.Equal("'Height' is between 2100 mm and 2400 mm", result.Rules[1].Expectation);
            Assert.Null(result.Rules[1].Pattern);
            Assert.Contains("Fail: Door marks follow D-nn (1).", summary);
            Assert.DoesNotContain("^D-", summary);
            Assert.True(summary.Length <= EnvelopeLimits.SummaryMaxChars);

            var json = JsonSerializer.Serialize(mh07);
            Assert.Contains("\"title\":", json);
            Assert.Contains("\"expectation\":", json);
            Assert.Contains("\"pattern\":", json);
        }

        // ── writes ──────────────────────────────────────────────────────────

        [Fact]
        public void Writes_NoChangeNext_ImpliesNoCard()
        {
            Assert.DoesNotContain("card", WriteEnvelopes.NoChangeNext);
            Assert.DoesNotContain("reviewing", WriteEnvelopes.NoChangeNext);
            Assert.Contains("Nothing would change", WriteEnvelopes.NoChangeNext);
            Assert.True(WriteEnvelopes.NoChangeNext.Length <= EnvelopeLimits.NextMaxChars);
        }

        [Fact]
        public void Writes_SiblingMessageNamesPositionInRequest()
        {
            var call = new ToolCallDto { Tool = ToolNames.SetParameters };
            var e = WriteEnvelopes.NotRunBecauseSibling(2, 3, call, EnvelopeFactory.Failed(ErrorCodes.NotFound, "No parameter 'Markk'.", new[] { "Mark" }), EnvelopePhase.Preview);
            Assert.StartsWith("Not applied: call 2 of 3 (set_parameters) in the same batch failed (not_found)", e.Error!.Message);
            var single = WriteEnvelopes.NotRunBecauseSibling(1, 1, call, EnvelopeFactory.Failed(ErrorCodes.RuntimeError, "x"), EnvelopePhase.Preview);
            Assert.StartsWith("Not applied: call 1 (set_parameters)", single.Error!.Message);
        }
    }
}
