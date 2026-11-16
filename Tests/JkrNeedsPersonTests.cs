using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Step 4: the backend's cannot_verify status stays "needs a person". Such an
    /// item is never a failure, never in the %, gets no fix / ignore, shows no ✓/✗,
    /// and sits in the Manual tab. Each backend status maps to the right state.
    /// </summary>
    public class JkrNeedsPersonTests
    {
        // ── Status mapping ──

        [Theory]
        [InlineData("fail", IssueVerification.Ai)]
        [InlineData("FAIL", IssueVerification.Ai)]
        [InlineData("warning", IssueVerification.Ai)]
        [InlineData("cannot_verify", IssueVerification.Human)]
        [InlineData(" Cannot_Verify ", IssueVerification.Human)]
        [InlineData("needs_review", IssueVerification.Human)]   // unknown → never claim an AI verdict
        [InlineData("", IssueVerification.Human)]
        [InlineData(null, IssueVerification.Human)]
        public void Each_backend_status_maps_to_who_decides(string status, IssueVerification expected)
        {
            Assert.Equal(expected, JkrCheckStatus.VerificationFor(status));
        }

        [Theory]
        [InlineData("pass", false)]
        [InlineData("PASS", false)]
        [InlineData("fail", true)]
        [InlineData("warning", true)]
        [InlineData("cannot_verify", true)]
        [InlineData(null, true)]
        public void Only_pass_is_left_out_of_the_list(string status, bool listed)
        {
            Assert.Equal(listed, JkrCheckStatus.IsListed(status));
        }

        // ── Item behaviour ──

        private static IssueVm Ai(IssueStatus s = IssueStatus.Open, bool fixable = true) => new IssueVm
        {
            Status = s, AutoFixable = fixable, FixAction = fixable ? "set_parameter" : "",
            Priority = IssuePriority.Low, Actual = "x", Required = "y",
        };

        private static IssueVm Human(IssueStatus s = IssueStatus.Open) => new IssueVm
        {
            Status = s, Verification = IssueVerification.Human, Priority = IssuePriority.Low,
            Actual = "x", Required = "y", Description = "No CDE access.",
        };

        [Fact]
        public void A_needs_a_person_item_is_not_an_open_failure()
        {
            var h = Human();
            Assert.True(h.NeedsPerson);
            Assert.False(h.IsAiOpen);
            Assert.True(h.InManualTab);
        }

        [Fact]
        public void A_needs_a_person_item_offers_no_fix_ignore_approve_or_mark_manual()
        {
            var h = Human();
            h.AutoFixable = true; h.FixAction = "set_parameter"; // even if the backend attached a fix
            Assert.False(h.IsActionable);
            Assert.False(h.ShowAutoFixButton);
            Assert.False(h.ShowIgnoreButton);
            Assert.False(h.ShowApproveButton);
            Assert.False(h.ShowMarkManualButton);
        }

        [Fact]
        public void A_needs_a_person_item_shows_no_tick_or_cross_but_its_reason()
        {
            var h = Human();
            Assert.False(h.HasActualOrRequired);          // no red actual → green required line
            Assert.Equal("Manual review", h.ManualReviewLabel);
            Assert.Equal("", Ai().ManualReviewLabel);
        }

        [Fact]
        public void An_ai_item_is_unchanged()
        {
            var a = Ai();
            Assert.False(a.NeedsPerson);
            Assert.True(a.IsAiOpen);
            Assert.True(a.IsActionable);
            Assert.True(a.ShowAutoFixButton);
            Assert.True(a.ShowIgnoreButton);
            Assert.True(a.HasActualOrRequired);
            Assert.False(a.InManualTab);
        }

        [Fact]
        public void Marked_manual_by_the_user_is_in_the_manual_tab_too()
        {
            Assert.True(Ai(IssueStatus.ManualFixNeeded).InManualTab);
        }

        // ── Counts and % ──

        [Fact]
        public void Needs_a_person_items_are_never_in_the_percent()
        {
            var issues = new List<IssueVm>
            {
                Ai(IssueStatus.Fixed), Ai(IssueStatus.Ignored), Ai(), Ai(),
                Human(), Human(), Human(), Human(),
            };
            Assert.Equal(4, JkrIssueCounts.Total(issues));
            Assert.Equal(2, JkrIssueCounts.Open(issues));
            Assert.Equal(1, JkrIssueCounts.Resolved(issues));
            Assert.Equal(1, JkrIssueCounts.Ignored(issues));
            Assert.Equal(4, JkrIssueCounts.NeedsPerson(issues));
            Assert.Equal(4, JkrIssueCounts.Manual(issues));
            Assert.Equal(50, JkrIssueCounts.Percent(issues));   // 2 of 4 AI items, people's items ignored
        }

        [Fact]
        public void Adding_needs_a_person_items_does_not_move_the_percent()
        {
            var ai = new List<IssueVm> { Ai(IssueStatus.Fixed), Ai(), Ai(), Ai() };
            var before = JkrIssueCounts.Percent(ai);
            ai.AddRange(Enumerable.Range(0, 10).Select(_ => Human()));
            Assert.Equal(before, JkrIssueCounts.Percent(ai));
        }

        [Fact]
        public void Only_needs_a_person_items_gives_zero_percent_and_no_open_failures()
        {
            var issues = new List<IssueVm> { Human(), Human() };
            Assert.Equal(0, JkrIssueCounts.Total(issues));
            Assert.Equal(0, JkrIssueCounts.Open(issues));
            Assert.Equal(0, JkrIssueCounts.Percent(issues));
            Assert.Equal(2, JkrIssueCounts.Manual(issues));
        }

        [Fact]
        public void A_needs_a_person_item_with_an_old_ignored_decision_is_still_not_counted()
        {
            // e.g. an audit file Ignored overlay from before the item became cannot_verify
            var issues = new List<IssueVm> { Human(IssueStatus.Ignored), Ai() };
            Assert.Equal(0, JkrIssueCounts.Ignored(issues));
            Assert.Equal(1, JkrIssueCounts.Total(issues));
            Assert.Equal(1, JkrIssueCounts.Manual(issues));
        }

        // ── Test fixture: cannot_verify entries ──

        [Fact]
        public void Fixture_cannot_verify_entries_are_always_listed_without_reading_the_model()
        {
            var reads = 0;
            var fx = new JkrFixture
            {
                Issues =
                {
                    new JkrFixtureIssue { Id = "H", Status = "cannot_verify", ElementId = 0, Reason = "No CDE access." },
                    new JkrFixtureIssue { Id = "A", ElementId = 1, FixAction = "rename_type", Expected = "x" },
                }
            };
            var found = JkrFixtureFile.Evaluate(fx, _ => { reads++; return new JkrFixtureObservation(true, "x"); });
            Assert.Equal(new[] { "H" }, found.Select(f => f.Issue.Id));
            Assert.Equal(1, reads);   // only the AI entry was read
        }

        [Fact]
        public void Fixture_cannot_verify_needs_a_reason_but_no_fix()
        {
            var ok = new JkrFixture { Issues = { new JkrFixtureIssue { Id = "H", Status = "cannot_verify", Reason = "why" } } };
            Assert.Empty(JkrFixtureFile.Validate(ok));

            var bad = new JkrFixture { Issues = { new JkrFixtureIssue { Id = "H", Status = "cannot_verify" } } };
            Assert.Contains(JkrFixtureFile.Validate(bad), e => e.StartsWith("H: cannot_verify needs a reason"));
        }

        [Fact]
        public void Fixture_rejects_an_unknown_status()
        {
            var fx = new JkrFixture
            {
                Issues = { new JkrFixtureIssue { Id = "A", Status = "maybe", ElementId = 1, FixAction = "rename_type", Expected = "x" } }
            };
            Assert.Contains(JkrFixtureFile.Validate(fx), e => e.StartsWith("A: status must be"));
        }

        [Fact]
        public void An_old_fixture_entry_without_status_is_a_fail()
        {
            Assert.Equal("fail", new JkrFixtureIssue().Status);
            Assert.False(new JkrFixtureIssue().IsCannotVerify);
        }
    }
}
