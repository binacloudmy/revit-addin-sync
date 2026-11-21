using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-466 (Ashraf, 20 Nov): a backend "warning" on an Ai-class check counts as a PASS
    /// (in the % and in the gagal count). A7.2 (Human class) is untouched; the Issues tab
    /// still lists warnings. WPF-free: runs on the Mac harness.
    /// </summary>
    public class SpecWarningPolicyTests
    {
        // The five Ai checks the backend can send a "warning" for, with a rule that maps to each.
        public static readonly object[][] WarningCapable =
        {
            new object[] { "A2.1", "Project Naming", "File naming convention", 0L },
            new object[] { "Da.1", "Doors", "Naming convention", 204511L },
            new object[] { "Db.1", "Doors", "Missing type parameter: Kod_Jenis_jkr_stt", 204600L },
            new object[] { "Db.2", "Walls", "Bahan_Utama_jkr_stt material code valid", 205100L },
            new object[] { "Dd.1", "Windows", "Category code matches Revit category", 206300L },
        };
        public static System.Collections.Generic.IEnumerable<object[]> Cases() => WarningCapable;

        private static SpecResult B(string cat, string rule, string status, long el, string issue = "") =>
            new SpecResult { Source = SpecReport.Backend, Category = cat, Rule = rule, Status = status, ElementId = el, IssueId = issue };

        [Theory, MemberData(nameof(Cases))]
        public void Warning_only_is_a_pass(string id, string cat, string rule, long el)
        {
            Assert.Equal(id, SpecReport.MapBackend(cat, rule, el)?.Id);
            var r = SpecReport.Build(new[] { B(cat, rule, "warning", el, "w1") });
            var c = r.For(id);
            Assert.Equal(SpecState.Pass, c.State);
            Assert.Equal(1, c.Passed);
            Assert.Equal(0, c.Failed);
            Assert.Equal(0, r.FailChecks);
        }

        [Theory, MemberData(nameof(Cases))]
        public void Warning_beside_a_fail_still_fails(string id, string cat, string rule, long el)
        {
            var r = SpecReport.Build(new[] { B(cat, rule, "warning", el, "w1"), B(cat, rule, "fail", el + 1, "f1") });
            Assert.Equal(SpecState.Fail, r.For(id).State);
            Assert.Equal(1, r.FailChecks);
        }

        [Fact]
        public void Status_helpers_treat_warning_as_pass_not_fail()
        {
            Assert.True(SpecReport.IsPass("warning"));
            Assert.True(SpecReport.IsPass(" Warning "));
            Assert.False(SpecReport.IsFail("warning"));
            Assert.True(SpecReport.IsFail("fail"));
            Assert.False(SpecReport.IsPass("cannot_verify"));
        }

        [Fact]
        public void An_unmapped_warning_does_not_count_toward_OtherFailing()
        {
            var r = SpecReport.Build(new[]
            {
                B("Doors", "JKR code assigned", "warning", 204511, "w-other"),
                B("Doors", "JKR code prefix", "fail", 204512, "f-other"),
            });
            Assert.Equal(2, r.Others.Count);
            Assert.Equal(1, r.OtherFailing);
            Assert.All(r.Others.Where(o => o.Rule == "JKR code assigned"), o => Assert.Equal(SpecState.Pass, o.State));
        }

        [Fact]
        public void A_warning_still_gives_an_issue_and_stays_an_AI_verdict_on_the_Issues_tab()
        {
            var w = new SpecResult { Source = SpecReport.Model, CheckId = "Db.1", Status = "warning", IssueId = "SPEC-Db.1-1", ElementId = 5 };
            Assert.True(SpecModelIssues.GivesIssue(w));
            Assert.True(SpecModelIssues.IsAiVerdict(w));
        }

        [Fact]
        public void A_warning_beside_a_pass_keeps_its_issue_id_on_the_check()
        {
            var r = SpecReport.Build(new[] { B("Doors", "Naming convention", "warning", 204511, "w1") });
            Assert.Contains("w1", r.For("Da.1").IssueIds);
        }

        [Fact]
        public void A72_stays_a_person_call_whatever_the_status()
        {
            var r = SpecReport.Build(new[]
            {
                new SpecResult { Source = SpecReport.Model, CheckId = "A7.2", Status = "warning", Coverage = SpecCoverage.Context, IssueId = "SPEC-A7.2-w" },
            });
            Assert.Equal(SpecState.NeedsPerson, r.For("A7.2").State);
            Assert.Equal(0, r.For("A7.2").Passed + r.For("A7.2").Failed);
        }

        // ── AC4: the % denominator is the spec's checks (AI-474) ──

        [Fact]
        public void Pct_counts_the_61_checks_and_never_the_Others()
        {
            var rows = JArray.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "spec_rows.json")));
            var ai = rows.Where(j => (string)j["class"] == "Ai").Select(j => (string)j["id"]).ToList();
            var other = rows.Where(j => (string)j["class"] != "Ai").Select(j => (string)j["id"]).ToList();
            Assert.Equal(39, ai.Count);

            var results = ai.Concat(other)
                .Select(id => { var x = SpecChecks.Pass(id, "ok"); x.Source = SpecReport.Model; return x; })
                .ToList();
            // Others entries (no spec row): a pass and a fail, neither may enter the %.
            results.Add(B("Doors", "JKR code assigned", "pass", 1));
            results.Add(B("Doors", "JKR code prefix", "fail", 2));
            var r = SpecReport.Build(results);

            // AI-474: the % counts checks, not cells; List and Human checks are PASS, so a run
            // where every check passes is 61 / 61. Others entries still never enter it.
            var (passed, total) = SpecChecklist.Score(r);
            Assert.Equal(61, total);
            Assert.Equal(61, passed);
            Assert.Equal(100, SpecChecklist.Pct(r));
            Assert.Equal(1, r.OtherFailing);
        }
    }
}
