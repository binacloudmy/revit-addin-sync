using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The UiHarness sample runs (data/jkr_run_*.json) through the add-in's own spec
    /// evaluators: the passing sample's model really fails nothing, the failing one
    /// fails what it was built to fail. Keeps the screenshots honest.
    /// </summary>
    [Xunit.Collection("PaneLanguage")]
    public class SpecSampleRunTests
    {
        private static SpecModel Model(string file)
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", file)));
            return json["model"].ToObject<SpecModel>(JsonSerializer.CreateDefault());
        }

        [Fact]
        public void The_passing_sample_model_fails_no_spec_check()
        {
            var report = SpecReport.Build(SpecChecks.Run(Model("jkr_run_compliant.json")));
            var failing = report.Checks.Where(c => c.State == SpecState.Fail)
                .Select(c => c.Check.Id + ": " + string.Join(" | ", c.Notes)).ToList();
            Assert.True(failing.Count == 0, "Failing in the passing sample:\n" + string.Join("\n", failing));
        }

        [Fact]
        public void The_failing_sample_model_fails_the_checks_it_was_built_for()
        {
            var report = SpecReport.Build(SpecChecks.Run(Model("jkr_run_failing.json")));
            var failing = report.Checks.Where(c => c.State == SpecState.Fail).Select(c => c.Check.Id).ToList();
            // Built in: undersized doors and a 35 mm main door, a level without a Dokumen 3D
            // view (C1.5.2, revision 2), an empty sheet, room-schedule gaps, a grid jump, an RC
            // flat roof without waterproofing, a 2700 mm ceiling, an 1800 mm fence.
            foreach (var id in new[] { "A1.1", "A1.2", "A1.3", "A1.4", "A1.5", "A5.2", "C1.5.2", "C1.6.2", "C1.6.3", "C4.1.3" })
                Assert.True(failing.Contains(id), id + " should fail in the failing sample; failing: " + string.Join(", ", failing));
            Assert.Contains(report.For("C1.5.2").Notes, n => n.Contains("Aras 03"));

            // Shown, never failed: the WIP list (a level without a ceiling plan) and the
            // non-JKR materials, which are A7.2 warnings with an issue each.
            Assert.Equal(SpecState.Listed, report.For("C1.2.1").State);
            var a72 = report.For("A7.2");
            Assert.Equal(SpecState.NeedsPerson, a72.State);
            Assert.NotEmpty(a72.IssueIds);
            Assert.True(SpecChecklist.WarningCount(a72) > 0);
            Assert.DoesNotContain("A7.2", failing);
        }

        /// <summary>The sample's BIM010 checker records, as IssueMapper.ToSpecResults builds them.</summary>
        private static System.Collections.Generic.List<SpecResult> CheckerRecords(string file)
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", file)));
            return json["audit"].Select(a =>
            {
                var key = (string)a["section"] + (string)a["row_ref"];
                var na = a["not_verifiable_reason"]?.Type == JTokenType.String;
                return new SpecResult
                {
                    Source = SpecReport.Audit, IssueId = na || (string)a["compliance"] == "yes" ? "" : "BIM010-" + key,
                    Rule = (string)a["rule"], Status = SpecReport.StatusOfCompliance((string)a["compliance"]),
                    ElementCount = (int)a["element_count"], CheckerId = (string)a["checker_id"], RowKey = key,
                    RowLabel = BinaVibe.Mcp.Tools.Audit.Bim010Form.Find(key)?.Description ?? "", Note = (string)a["remark"], NotApplicable = na,
                };
            }).ToList();
        }

        [Theory]
        [InlineData("jkr_run_failing.json")]
        [InlineData("jkr_run_compliant.json")]
        public void Checker_records_beside_the_evaluators_never_change_a_judged_checks_counts(string file)
        {
            // The pane builds its report from the evaluators AND the BIM010 checker records.
            // Where an evaluator judged a check, the checker's record for it is the same
            // finding again: the counts stay the evaluator's (the real model showed C4.1.3
            // "5 of 5" over 4 empty sheets).
            var model = SpecChecks.Run(Model(file));
            var alone = SpecReport.Build(model);
            var both = SpecReport.Build(CheckerRecords(file).Concat(model));
            var judged = alone.Checks.Where(c => c.Check.Class == SpecCellClass.Ai && (c.Passed + c.Failed > 0 || c.PartlyPassed)).ToList();
            Assert.NotEmpty(judged);
            foreach (var a in judged)
            {
                var b = both.For(a.Check.Id);
                Assert.True((a.Passed, a.Failed, a.Unjudged, a.Elements) == (b.Passed, b.Failed, b.Unjudged, b.Elements),
                    $"{a.Check.Id}: alone {a.Passed}/{a.Failed}/{a.Unjudged}/{a.Elements}, with checker records {b.Passed}/{b.Failed}/{b.Unjudged}/{b.Elements}");
                Assert.Equal(a.State, b.State);
                Assert.DoesNotContain(b.IssueIds, x => x.StartsWith("BIM010-"));
            }
            // List-only and human-decides checks the evaluators listed: the checker's remark
            // (views by name only) is dropped, so the row shows the evaluator's text alone.
            foreach (var a in alone.Checks.Where(c => c.Check.Class != SpecCellClass.Ai && c.Listed > 0))
            {
                var b = both.For(a.Check.Id);
                Assert.True(a.Notes.SequenceEqual(b.Notes), a.Check.Id + ": notes changed with the checker records:\n" + string.Join("\n", b.Notes));
                Assert.Equal(a.IssueIds, b.IssueIds);
            }
            // (The % may still differ: checks no evaluator judges, e.g. A4.1 from the
            // base_point checker, are answered by the checker alone.)
            if (file == "jkr_run_failing.json")
            {
                // The checker's C4.1 "no" is dropped: not counted, its remark not shown.
                Assert.True(both.For("C4.1.3").Failed > 0);
                Assert.Equal(alone.For("C4.1.3").Failed, both.For("C4.1.3").Failed);
                Assert.Equal(alone.For("C4.1.3").Notes, both.For("C4.1.3").Notes);
                Assert.Empty(both.For("C4.1.3").NotCounted);
            }
        }
    }
}
