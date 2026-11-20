using System.Linq;
using Newtonsoft.Json;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-436: the Project Checklist (rules 1, 21–24, 26). Every item flips only on its
    /// own re-check result, never because a button was pressed or a write was made.
    /// </summary>
    public class ProjectChecklistTests
    {
        private static JkrProjectRecheckResponse Reply(bool passed, bool applicable = true, string error = "", string reason = "")
            => new JkrProjectRecheckResponse
            {
                Passed = passed, Applicable = applicable, Error = error,
                Checks = passed ? new System.Collections.Generic.List<JkrProjectRecheckCheck>()
                                : new System.Collections.Generic.List<JkrProjectRecheckCheck> { new JkrProjectRecheckCheck { Status = "fail", Reason = reason, ActualValue = "House" } },
            };

        [Fact]
        public void The_six_rules_are_listed_in_order_and_start_unchecked()
        {
            var m = new ProjectChecklistModel();
            Assert.Equal(new[] { 1, 21, 22, 23, 24, 26 }, m.Items.Select(i => i.Rule));
            Assert.All(m.Items, i => Assert.Equal(ProjectCheckState.NotChecked, i.State));
        }

        [Fact]
        public void A_passing_recheck_flips_only_its_own_item()
        {
            var m = new ProjectChecklistModel();
            m.ApplyRecheck(1, Reply(true));
            Assert.Equal(ProjectCheckState.Pass, m.Item(1).State);
            Assert.All(m.Items.Where(i => i.Rule != 1), i => Assert.Equal(ProjectCheckState.NotChecked, i.State));
        }

        [Fact]
        public void A_failing_recheck_keeps_the_item_failed_and_says_why()
        {
            var m = new ProjectChecklistModel();
            m.ApplyRecheck(1, Reply(false, reason: "File name does not follow the pattern."));
            Assert.Equal(ProjectCheckState.Fail, m.Item(1).State);
            Assert.Contains("does not follow", m.Item(1).Detail);
        }

        [Fact]
        public void A_pass_goes_back_to_fail_when_the_next_recheck_fails()
        {
            var m = new ProjectChecklistModel();
            m.ApplyRecheck(24, Reply(true));
            m.ApplyRecheck(24, Reply(false, reason: "x"));
            Assert.Equal(ProjectCheckState.Fail, m.Item(24).State);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_unanswered_recheck_is_never_a_pass(bool nullReply)
        {
            var m = new ProjectChecklistModel();
            m.ApplyRecheck(23, Reply(true));
            m.ApplyRecheck(23, nullReply ? null : Reply(true, error: "Server error: 404"));
            Assert.Equal(ProjectCheckState.Unavailable, m.Item(23).State);
        }

        [Fact]
        public void A_rule_with_nothing_to_evaluate_is_not_applicable_not_passed()
        {
            var m = new ProjectChecklistModel();
            m.ApplyRecheck(21, Reply(false, applicable: false));
            Assert.Equal(ProjectCheckState.NotApplicable, m.Item(21).State);
        }

        [Fact]
        public void Pressing_an_action_asks_the_host_but_changes_no_state()
        {
            var m = new ProjectChecklistModel();
            m.ApplyRecheck(22, Reply(false, reason: "BPEP not confirmed"));
            int? asked = null;
            m.ActionRequested += r => asked = r;
            m.Act(22);
            Assert.Equal(22, asked);
            Assert.Equal(ProjectCheckState.Fail, m.Item(22).State);
        }

        [Fact]
        public void The_LOD_action_points_at_the_LOD_box_and_needs_no_host_work()
        {
            var m = new ProjectChecklistModel();
            int focused = 0;
            int? asked = null;
            m.FocusLodRequested += () => focused++;
            m.ActionRequested += r => asked = r;
            m.Act(21);
            Assert.Equal(1, focused);
            Assert.Null(asked);
            Assert.Equal(ProjectCheckState.NotChecked, m.Item(21).State);
        }

        [Fact]
        public void Begin_shows_checking_and_the_result_replaces_it()
        {
            var m = new ProjectChecklistModel();
            m.Begin(24);
            Assert.Equal(ProjectCheckState.Checking, m.Item(24).State);
            m.ApplyRecheck(24, Reply(true));
            Assert.Equal(ProjectCheckState.Pass, m.Item(24).State);
        }

        [Fact]
        public void Every_failing_item_has_a_control_and_rule_26_has_the_progress_tile()
        {
            var m = new ProjectChecklistModel();
            foreach (var i in m.Items.Where(i => i.Rule != 26)) m.ApplyRecheck(i.Rule, Reply(false, reason: "r"));
            m.ApplyNamingAdoption("40% (4/10)");
            foreach (var i in m.Items)
            {
                Assert.True(i.HasAction || i.ShowProgress, "rule " + i.Rule + " is red with no control");
                Assert.False(string.IsNullOrWhiteSpace(i.Title));
                Assert.False(string.IsNullOrWhiteSpace(i.Guidance));
            }
            Assert.Equal(ProjectCheckAction.SaveAs, m.Item(1).Action);
            Assert.Equal(ProjectCheckAction.FocusLod, m.Item(21).Action);
            Assert.Equal(ProjectCheckAction.ConfirmBpep, m.Item(22).Action);
            Assert.Equal(ProjectCheckAction.SaveAs, m.Item(23).Action);
            Assert.Equal(ProjectCheckAction.LoadSharedParams, m.Item(24).Action);
            Assert.True(m.Item(26).ShowProgress);
        }

        [Fact]
        public void The_text_follows_the_language()
        {
            var m = new ProjectChecklistModel();
            m.SetLang(PanelStrings.En);
            var en = m.Item(24).ActionLabel;
            m.SetLang(PanelStrings.Ms);
            Assert.NotEqual(en, m.Item(24).ActionLabel);
        }

        // ── rule 26: live progress ──

        [Theory]
        [InlineData("66.7% (10/15)", 66.7, 10, 15)]
        [InlineData("100.0% (8/8)", 100.0, 8, 8)]
        [InlineData("0% (0/12)", 0.0, 0, 12)]
        public void Naming_adoption_is_read_from_the_backends_check(string actual, double pct, int named, int total)
        {
            Assert.True(NamingAdoption.TryParse(actual, out var a));
            Assert.Equal(pct, a.Percent, 1);
            Assert.Equal(named, a.Named);
            Assert.Equal(total, a.Total);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("n/a")]
        public void Unreadable_adoption_text_is_not_a_number(string actual)
            => Assert.False(NamingAdoption.TryParse(actual, out _));

        [Fact]
        public void The_pass_line_is_one_constant_at_100_percent()
        {
            Assert.Equal(100.0, ProjectChecklistModel.NamingPassPercent);
            var m = new ProjectChecklistModel();
            m.ApplyNamingAdoption("99.9% (999/1000)");
            Assert.Equal(ProjectCheckState.Fail, m.Item(26).State);
            m.ApplyNamingAdoption("100.0% (1000/1000)");
            Assert.Equal(ProjectCheckState.Pass, m.Item(26).State);
        }

        [Fact]
        public void Rule_26_recomputes_each_time_the_element_rules_run_and_shows_the_numbers()
        {
            var m = new ProjectChecklistModel();
            m.ApplyNamingAdoption("40% (4/10)");
            Assert.Equal(40.0, m.Item(26).Percent, 1);
            m.ApplyNamingAdoption("70% (7/10)");
            Assert.Equal(70.0, m.Item(26).Percent, 1);
            Assert.Contains("7", m.Item(26).Detail);
            Assert.Contains("10", m.Item(26).Detail);
        }

        [Fact]
        public void A_scan_without_the_adoption_check_leaves_rule_26_not_checked_never_passed()
        {
            var m = new ProjectChecklistModel();
            m.ApplyNamingAdoption("100% (5/5)");
            m.ApplyNamingAdoption(null);
            Assert.Equal(ProjectCheckState.NotChecked, m.Item(26).State);
        }

        // ── wire ──

        [Theory]
        [InlineData(1, "file-name")]
        [InlineData(21, "lod")]
        [InlineData(22, "bpep")]
        [InlineData(23, "template")]
        [InlineData(24, "shared-params")]
        public void Each_rule_has_its_endpoint(int rule, string key)
        {
            Assert.Equal(key, ProjectChecklistModel.EndpointKey(rule));
            Assert.Equal("/v1/compliance/project-recheck/" + key, ProjectChecklistModel.EndpointPath(rule));
        }

        [Fact]
        public void Rule_26_has_no_endpoint()
            => Assert.Null(ProjectChecklistModel.EndpointKey(26));

        [Fact]
        public void The_backends_answer_deserialises()
        {
            var json = "{\"rule_number\":1,\"rule_key\":\"file-name\",\"passed\":false,\"applicable\":true,\"checks\":[{\"status\":\"fail\",\"reason\":\"bad name\",\"actual_value\":\"House\"}],\"error\":\"\"}";
            var r = JsonConvert.DeserializeObject<JkrProjectRecheckResponse>(json);
            Assert.Equal(1, r.RuleNumber);
            Assert.False(r.Passed);
            Assert.True(r.Applicable);
            Assert.Equal("bad name", r.Checks.Single().Reason);
        }
    }
}
