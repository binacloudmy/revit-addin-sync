#nullable enable
// W6 Task 4 (lean-harness plan §5 W6): CheckReport turns rule runs into the
// §2.5 check result (totals that add up, at most 5 findings per rule, a fail
// handle per failing rule, the not_verified reason), and CheckArgs parses the
// add-in's check args (rule_set_content + scope) into typed DTOs, refusing
// bad input as invalid_args (ruling R14). The envelope tests assemble the
// envelope from the Contracts types, the way CheckTool hands the same pieces
// to EnvelopeBuilder, and hold it to the §2.1 invariants.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Check;
using BinaVibe.Copilot.Contracts;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CheckReportTests
    {
        private sealed class RecordingHandles : IHandleStore
        {
            public List<(IReadOnlyList<ElementRef> Refs, string Label)> Puts { get; } = new();
            public bool Throw { get; set; }

            public string Put(IReadOnlyList<ElementRef> refs, string label)
            {
                if (Throw) throw new NotImplementedException("W2");
                Puts.Add((refs.ToList(), label));
                return "h_" + Puts.Count.ToString("x8");
            }

            public IReadOnlyList<ElementRef>? Get(string handle) => null;
        }

        private static RuleSet ModelHealth() => RuleSetLoader.Parse(CheckFixtures.Parse(CheckFixtures.ModelHealthJson()));

        private static Rule RuleOf(RuleSet set, string id) => set.Rules.Single(r => r.Id == id);

        private static ElementRef R(long id, long? link = null) => new ElementRef(id, link);

        // ------------------------------------------------------------------
        // CheckReport
        // ------------------------------------------------------------------

        [Fact]
        public void TotalsAddUpAndTopCapped()
        {
            var set = ModelHealth();
            var marks = new RuleRun(RuleOf(set, "MH-07"));
            for (var i = 1; i <= 20; i++)
                marks.Add(R(i), i % 3 == 0 ? Outcome.Fail("'Mark' is empty", "", OutcomeCodes.Empty) : Outcome.Pass("D" + i));
            marks.Add(R(900, 77), Outcome.NotVerified("link STR_v3 is not loaded", null, OutcomeCodes.SourceUnavailable));

            var rooms = new RuleRun(RuleOf(set, "MH-03"));
            for (var i = 100; i < 104; i++) rooms.Add(R(i), Outcome.Pass("placed"));

            var widths = new RuleRun(RuleOf(set, "MH-08"));   // unverified_value: every outcome not_verified
            widths.Add(R(201), Outcome.Pass("900 mm")).Add(R(202), Outcome.Fail("too wide", "2700 mm"));

            var handles = new RecordingHandles();
            var result = CheckReport.Build(set, new[] { marks, rooms, widths }, handles);

            Assert.Equal("model-health", result.RuleSetId);
            Assert.Equal("2026.10.0", result.Version);
            Assert.Equal(3, result.Rules.Count);
            Assert.Equal(result.Rules.Sum(r => r.Pass), result.Totals.Pass);
            Assert.Equal(result.Rules.Sum(r => r.Fail), result.Totals.Fail);
            Assert.Equal(result.Rules.Sum(r => r.NotVerified), result.Totals.NotVerified);
            Assert.Equal(result.Totals.Pass + result.Totals.Fail + result.Totals.NotVerified, result.Totals.Checked);
            Assert.Equal(27, result.Totals.Checked);
            Assert.All(result.Rules, r => Assert.InRange(r.Top.Count, 0, CheckLimits.MaxTopFindings));

            var mh07 = result.Rules.Single(r => r.RuleId == "MH-07");
            Assert.Equal((14, 6, 1), (mh07.Pass, mh07.Fail, mh07.NotVerified));
            Assert.Equal("BINA-MH §5.1", mh07.Cite);
            Assert.Equal("warning", mh07.Severity);
            Assert.Equal(5, mh07.Top.Count);
            Assert.All(mh07.Top, f => Assert.Contains("empty", f.Reason));   // fails come before not_verified
            Assert.NotNull(mh07.FailHandle);
            Assert.Contains("not loaded", mh07.NotVerifiedReason ?? "");

            var put = Assert.Single(handles.Puts, p => p.Label.Contains("MH-07"));
            Assert.Equal(new long[] { 3, 6, 9, 12, 15, 18 }, put.Refs.Select(r => r.Id));   // every fail, not just the top 5

            var mh03 = result.Rules.Single(r => r.RuleId == "MH-03");
            Assert.Equal((4, 0, 0), (mh03.Pass, mh03.Fail, mh03.NotVerified));
            Assert.Empty(mh03.Top);
            Assert.Null(mh03.FailHandle);
            Assert.Null(mh03.NotVerifiedReason);

            var mh08 = result.Rules.Single(r => r.RuleId == "MH-08");
            Assert.Equal((0, 0, 2), (mh08.Pass, mh08.Fail, mh08.NotVerified));
            Assert.Contains("threshold", mh08.NotVerifiedReason);
            Assert.All(mh08.Top, f => Assert.StartsWith("not verified", f.Reason));
        }

        [Fact]
        public void TopFillsWithNotVerifiedWhenFewerThanFiveFail()
        {
            var set = ModelHealth();
            var run = new RuleRun(RuleOf(set, "MH-07"))
                .Add(R(1), Outcome.Fail("'Mark' is empty"))
                .Add(R(2), Outcome.NotVerified("could not be read"))
                .Add(R(3), Outcome.NotVerified("could not be read"));
            var top = CheckReport.Build(set, new[] { run }, new RecordingHandles()).Rules.Single().Top;
            Assert.Equal(new long[] { 1, 2, 3 }, top.Select(f => f.Ref.Id));
            Assert.DoesNotContain("not verified", top[0].Reason);
        }

        [Fact]
        public void FailHandleTakesSetLevelRefsAndSkipsDocumentLevelOnes()
        {
            var set = ModelHealth();
            var run = new RuleRun(RuleOf(set, "MH-02"));
            WarningGroups.AddTo(run, WarningGroups.Group(new[]
            {
                new WarningItem("g-1", "Highlighted walls overlap", new[] { R(1), R(2) }),
                new WarningItem("g-2", "Elements have duplicate Mark values", new ElementRef[0]),
            }));
            var handles = new RecordingHandles();
            var rule = CheckReport.Build(set, new[] { run }, handles).Rules.Single();

            Assert.Equal(2, rule.Fail);
            Assert.Equal(new long[] { 1, 2 }, Assert.Single(handles.Puts).Refs.Select(r => r.Id));
            Assert.Contains(rule.Top, f => f.Ref.Id == Evaluated.NoElement);
        }

        [Fact]
        public void HandleStoreFailureLeavesTheResultIntact()
        {
            var set = ModelHealth();
            var run = new RuleRun(RuleOf(set, "MH-07")).Add(R(1), Outcome.Fail("'Mark' is empty"));
            var rule = CheckReport.Build(set, new[] { run }, new RecordingHandles { Throw = true }).Rules.Single();
            Assert.Equal(1, rule.Fail);
            Assert.Null(rule.FailHandle);
        }

        [Fact]
        public void NothingToCheckIsSaidNotHidden()
        {
            var set = ModelHealth();
            var run = new RuleRun(RuleOf(set, "MH-07")) { NothingToCheck = "nothing to check: no Doors matched in host" };
            var rule = CheckReport.Build(set, new[] { run }, new RecordingHandles()).Rules.Single();
            Assert.Equal((0, 0, 0), (rule.Pass, rule.Fail, rule.NotVerified));
            Assert.Contains("nothing to check", rule.NotVerifiedReason);
        }

        [Fact]
        public void NotVerifiedReasonListsTheCommonestFirst()
        {
            var set = ModelHealth();
            var run = new RuleRun(RuleOf(set, "MH-07"))
                .Add(R(1), Outcome.NotVerified("workset 'A' is closed"))
                .Add(R(2), Outcome.NotVerified("link S is not loaded"))
                .Add(R(3), Outcome.NotVerified("link S is not loaded"));
            var reason = CheckReport.Build(set, new[] { run }, null).Rules.Single().NotVerifiedReason!;
            Assert.StartsWith("link S is not loaded (2)", reason);
            Assert.Contains("workset 'A' is closed (1)", reason);
        }

        [Fact]
        public void ResultJsonHasTheContractShape()
        {
            var set = ModelHealth();
            var run = new RuleRun(RuleOf(set, "MH-07")).Add(R(1), Outcome.Fail("'Mark' is empty", "")).Add(R(5, 77), Outcome.Pass("D1"));
            var json = JsonDocument.Parse(CopilotJson.Serialize(CheckReport.Build(set, new[] { run }, new RecordingHandles()))).RootElement;

            foreach (var key in new[] { "rule_set_id", "version", "totals", "rules" }) Assert.True(json.TryGetProperty(key, out _), key);
            foreach (var key in new[] { "checked", "pass", "fail", "not_verified" }) Assert.True(json.GetProperty("totals").TryGetProperty(key, out _), key);
            var rule = json.GetProperty("rules")[0];
            foreach (var key in new[] { "rule_id", "cite", "severity", "pass", "fail", "not_verified", "top", "fail_handle" })
                Assert.True(rule.TryGetProperty(key, out _), key);
            Assert.False(rule.TryGetProperty("not_verified_reason", out _));   // null is omitted
            var finding = rule.GetProperty("top")[0];
            Assert.Equal(1, finding.GetProperty("ref").GetProperty("id").GetInt64());
            Assert.False(finding.GetProperty("ref").TryGetProperty("link", out _));
            Assert.True(finding.TryGetProperty("reason", out _));
        }

        // ------------------------------------------------------------------
        // Summary, status, next, searched → a valid envelope
        // ------------------------------------------------------------------

        private static (CheckResult Result, RuleSet Set) Mixed()
        {
            var set = ModelHealth();
            var runs = new[]
            {
                new RuleRun(RuleOf(set, "MH-07")).Add(R(1), Outcome.Fail("'Mark' is empty")).Add(R(2), Outcome.Pass("D2")),
                new RuleRun(RuleOf(set, "MH-08")).Add(R(3), Outcome.Pass("900 mm")),
                new RuleRun(RuleOf(set, "MH-03")).Add(R(4), Outcome.Pass("placed")),
            };
            return (CheckReport.Build(set, runs, new RecordingHandles()), set);
        }

        [Fact]
        public void SummaryCountsFailsAndNotVerified()
        {
            var (result, set) = Mixed();
            var summary = CheckReport.Summary(set, result, new[] { new SkippedRule("MH-02", "document-wide") });
            Assert.InRange(EnvelopeInvariants.CodePoints(summary), 1, EnvelopeLimits.SummaryMaxChars);
            Assert.Contains("model-health", summary);
            Assert.Contains("1 fail", summary);
            Assert.Contains("MH-07", summary);
            Assert.Contains("1 not verified", summary);
            Assert.Contains("MH-08", summary);
            Assert.Contains("1 rule skipped", summary);
        }

        [Fact]
        public void SummaryStaysWithinTheCapOnHugeSets()
        {
            var json = CheckFixtures.EditedModelHealth(o =>
            {
                var rules = o["rules"]!.AsArray();
                var template = rules[6]!.ToJsonString();
                for (var i = 0; i < 80; i++)
                {
                    var copy = System.Text.Json.Nodes.JsonNode.Parse(template)!;
                    copy["id"] = "JKR-NAMING-LONG-RULE-ID-" + i;
                    rules.Add(copy);
                }
            });
            var set = RuleSetLoader.Parse(json);
            var runs = set.Rules.Select(r => new RuleRun(r).Add(R(1), Outcome.Fail("x"))).ToList();
            var summary = CheckReport.Summary(set, CheckReport.Build(set, runs, null), new SkippedRule[0]);
            Assert.InRange(EnvelopeInvariants.CodePoints(summary), 1, EnvelopeLimits.SummaryMaxChars);
        }

        [Fact]
        public void StatusIsPartialWhenAnythingIsNotVerified()
        {
            var (result, _) = Mixed();
            Assert.Equal(EnvelopeStatus.Partial, CheckReport.Status(result));
            var set = ModelHealth();
            var clean = CheckReport.Build(set, new[] { new RuleRun(RuleOf(set, "MH-03")).Add(R(1), Outcome.Pass()) }, null);
            Assert.Equal(EnvelopeStatus.Done, CheckReport.Status(clean));
            var empty = CheckReport.Build(set, new[] { new RuleRun(RuleOf(set, "MH-03")) { NothingToCheck = "nothing to check" } }, null);
            Assert.Equal(EnvelopeStatus.Partial, CheckReport.Status(empty));
        }

        [Fact]
        public void EnvelopeFromTheReportPiecesIsValid()
        {
            var (result, set) = Mixed();
            var scope = new CheckScope { Level = "Level 1" };
            var envelope = new Envelope
            {
                Ok = true,
                Status = CheckReport.Status(result),
                Phase = EnvelopePhase.Read,
                Summary = CheckReport.Summary(set, result, new SkippedRule[0]),
                Data = CopilotJson.ToElement(result),
                Count = result.Totals.Checked,
                Searched = CheckReport.Searched(new[] { "host" }, new[] { "Doors", "Rooms", "Doors" }, scope, new[] { "link STR_v3 is not loaded" }),
                Next = CheckReport.Next(result),
            };

            Assert.Empty(EnvelopeInvariants.Violations(envelope));
            Assert.Equal(new[] { "Doors", "Rooms" }, envelope.Searched!.Categories);
            Assert.Contains("Level 1", envelope.Searched.Filter);
            Assert.Single(envelope.Searched.NotSearched);
            Assert.InRange(EnvelopeInvariants.CodePoints(envelope.Next!), 1, EnvelopeLimits.NextMaxChars);
        }

        [Fact]
        public void EmptyCheckStillCarriesSearched()
        {
            var set = ModelHealth();
            var result = CheckReport.Build(set, new RuleRun[0], null);
            var envelope = new Envelope
            {
                Ok = true,
                Status = CheckReport.Status(result),
                Phase = EnvelopePhase.Read,
                Summary = CheckReport.Summary(set, result, new SkippedRule[0]),
                Count = result.Totals.Checked,
                Searched = CheckReport.Searched(new string[0], new string[0], new CheckScope(), new string[0]),
            };
            Assert.Equal(0, envelope.Count);
            Assert.Empty(EnvelopeInvariants.Violations(envelope));
        }

        // ------------------------------------------------------------------
        // CheckArgs: typed parsing of rule_set_content + scope (R14)
        // ------------------------------------------------------------------

        private static string Args(string scopeJson = "") =>
            "{\"rule_set_content\":" + CheckFixtures.ModelHealthJson() + (scopeJson.Length > 0 ? ",\"scope\":" + scopeJson : "") + "}";

        [Fact]
        public void Args_ParseRuleSetAndScope()
        {
            Assert.True(CheckArgs.TryParse(CheckFixtures.Parse(Args("{\"level\":\"Level 2\",\"category\":\"Doors\",\"link\":\"STR_v3\",\"selection\":true}")), out var args, out var error), error);
            Assert.Equal("model-health", args!.RuleSet.Id);
            Assert.Equal("Level 2", args.Scope.Level);
            Assert.Equal("Doors", args.Scope.Category);
            Assert.Equal("STR_v3", args.Scope.Link);
            Assert.True(args.Scope.Selection);
        }

        [Fact]
        public void Args_ScopeIsOptionalAndNullable()
        {
            Assert.True(CheckArgs.TryParse(CheckFixtures.Parse(Args()), out var a, out _));
            Assert.True(a!.Scope.IsEmpty);
            Assert.True(CheckArgs.TryParse(CheckFixtures.Parse(Args("null")), out var b, out _));
            Assert.True(b!.Scope.IsEmpty);
            Assert.True(CheckArgs.TryParse(CheckFixtures.Parse(Args("{\"level\":null,\"selection\":false,\"later\":1}")), out var c, out _));
            Assert.True(c!.Scope.IsEmpty);
        }

        [Theory]
        [InlineData("[]", "object")]
        [InlineData("{\"rule_set\":\"model-health\"}", "rule_set_content")]
        [InlineData("{\"rule_set_content\":\"model-health\"}", "rule_set_content")]
        [InlineData("{\"rule_set_content\":{\"id\":\"x\"}}", "rule_set_content is invalid")]
        public void Args_BadRuleSetIsInvalidArgs(string json, string expected)
        {
            Assert.False(CheckArgs.TryParse(CheckFixtures.Parse(json), out _, out var error));
            Assert.Contains(expected, error);
        }

        [Theory]
        [InlineData("[]", "scope")]
        [InlineData("{\"level\":2}", "scope.level")]
        [InlineData("{\"selection\":\"yes\"}", "scope.selection")]
        [InlineData("{\"category\":\"   \"}", "scope.category")]
        public void Args_BadScopeIsInvalidArgs(string scope, string expected)
        {
            Assert.False(CheckArgs.TryParse(CheckFixtures.Parse(Args(scope)), out _, out var error));
            Assert.Contains(expected, error);
        }

        // ------------------------------------------------------------------
        // CheckScope
        // ------------------------------------------------------------------

        [Fact]
        public void Scope_DocumentWideRulesAreSkippedWhenTheScopeNarrows()
        {
            var set = ModelHealth();
            Assert.True(new CheckScope().Applies(RuleOf(set, "MH-02"), out _));
            Assert.False(new CheckScope { Level = "Level 1" }.Applies(RuleOf(set, "MH-02"), out var why));
            Assert.Contains("document-wide", why);
            Assert.False(new CheckScope { Selection = true }.Applies(RuleOf(set, "MH-05"), out _));
        }

        [Theory]
        [InlineData("Doors", true)]
        [InlineData("doors", true)]
        [InlineData("OST_Doors", true)]
        [InlineData("Rooms", false)]
        public void Scope_CategoryKeepsOnlyRulesAboutIt(string category, bool applies)
        {
            Assert.Equal(applies, new CheckScope { Category = category }.Applies(RuleOf(ModelHealth(), "MH-07"), out _));
        }

        [Fact]
        public void Scope_AdmitsByLevelAndSelection()
        {
            var e = new ElementValues { Ref = R(10), LevelName = "Level 2" };
            var linked = new ElementValues { Ref = R(10, 77), LevelName = "Level 2" };
            Assert.True(new CheckScope().Admits(e, null));
            Assert.True(new CheckScope { Level = "level 2" }.Admits(e, null));
            Assert.False(new CheckScope { Level = "Level 3" }.Admits(e, null));
            Assert.True(new CheckScope { Selection = true }.Admits(e, new HashSet<long> { 10 }));
            Assert.False(new CheckScope { Selection = true }.Admits(linked, new HashSet<long> { 10 }));
            Assert.False(new CheckScope { Selection = true }.Admits(e, null));
        }

        [Fact]
        public void Scope_DescribesItself()
        {
            Assert.Null(new CheckScope().Describe());
            var text = new CheckScope { Level = "Level 2", Category = "Doors", Link = "STR_v3", Selection = true }.Describe()!;
            Assert.Contains("Level 2", text);
            Assert.Contains("Doors", text);
            Assert.Contains("STR_v3", text);
            Assert.Contains("selection", text);
        }
    }
}
