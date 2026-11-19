using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// Step 11: the issue detail (frames f-detail, f-preview, f-fixed, f-ignore,
    /// m-detail) — header, card per status, moving through the list it opened
    /// with, keys, and the Fix / Ignore / review / Locate requests.
    /// </summary>
    public class DetailScreenTests
    {
        private static IssueVm Issue(string id, string key, IssuePriority p = IssuePriority.Medium,
                                     IssueStatus st = IssueStatus.Open, bool human = false, bool fixable = false)
            => new IssueVm
            {
                Id = id, BorangKey = key, Title = "Issue " + id, Description = "why " + id,
                Priority = p, Status = st, Verification = human ? IssueVerification.Human : IssueVerification.Ai,
                AutoFixable = fixable, FixAction = fixable ? "rename_type" : "", FixValue = fixable ? "jkrAR_x" : "",
                Actual = "Door 1", Required = "jkr…", RevitCategory = "Doors",
                Element = new ElementRef { Id = "204511", Name = "PT2p600a" },
                Spec = new SpecRef { Doc = "" },
            };

        private static (DetailScreenModel detail, ResultsScreenModel results, PanelCore core) New(params IssueVm[] issues)
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core) { FileLabel = "jkrAR24.rvt" };
            core.SetLod("300");
            core.SetLang(PanelStrings.En);
            core.BeginExternalRun();
            core.FinishExternalRun();
            var results = new ResultsScreenModel(core, start);
            results.Load(issues, AuditSummary.Build(Enumerable.Empty<Bim010Cell>()));
            return (new DetailScreenModel(core, results), results, core);
        }

        [Fact]
        public void Opens_at_the_row_with_the_list_as_it_was()
        {
            var (d, r, _) = New(Issue("1", "A1", IssuePriority.High), Issue("2", "A4"), Issue("3", "A5"));
            var opened = 0;
            d.Opened += () => opened++;
            d.Open("2");
            Assert.Equal(1, opened);
            Assert.True(d.IsOpen);
            Assert.Equal("Issue 2 of 3", d.IdxLabel);
            Assert.Equal(2 / 3.0, d.ProgFraction, 3);
            Assert.True(d.CanPrev);
            Assert.True(d.CanNext);
            Assert.Equal(new[] { false, true, false }, d.Dots.Select(x => x.IsOn));

            d.Open("nope");                                     // unknown id: nothing happens
            Assert.Equal("2", d.Card.Id);
        }

        [Fact]
        public void An_open_issue_with_a_fix_shows_the_suggested_fix_and_ignore()
        {
            var (d, r, _) = New(Issue("1", "A4", IssuePriority.High, fixable: true));
            d.Open("1");
            var c = d.Card;
            Assert.Equal(("High", "High"), (c.SvLabel, c.SvKind));
            Assert.StartsWith("Borang A4 · ", c.Where);
            Assert.Equal("Issue 1", c.Title);
            Assert.Equal("why 1", c.Desc);
            Assert.Equal("Doors [204511] PT2p600a", c.El);
            Assert.Equal(("jkr…", "Door 1", "bad"), (c.Req, c.Act, c.ActKind));
            Assert.True(c.CanFix);
            Assert.Equal("jkrAR_x", c.Prop);
            Assert.True(c.CanIgnore);
            Assert.False(c.HasSteps);                            // the fix block, not the steps
            Assert.False(c.IsManual);

            List<string> fix = null, ign = null;
            r.FixRequested += ids => fix = ids;
            r.IgnoreRequested += ids => ign = ids;
            d.Fix();
            d.Ignore();
            Assert.Equal(new[] { "1" }, fix);
            Assert.Equal(new[] { "1" }, ign);
        }

        [Fact]
        public void While_a_fix_is_written_fix_and_ignore_are_off()
        {
            var (d, r, _) = New(Issue("1", "A4", fixable: true));
            d.Open("1");
            r.BusyText = "Applying 3 changes…";
            Assert.False(d.CanAct);
            var asked = 0;
            r.FixRequested += _ => asked++;
            r.IgnoreRequested += _ => asked++;
            d.Fix();
            d.Ignore();
            d.HandleKey(PanelKey.F);
            d.HandleKey(PanelKey.A);
            Assert.Equal(0, asked);
        }

        [Fact]
        public void A_fixed_issue_shows_as_resolved_and_offers_no_actions()
        {
            var (d, _, core) = New(Issue("1", "A5", st: IssueStatus.Fixed, fixable: true));
            core.SetTab(PanelTab.Resolved);
            d.Open("1");
            var c = d.Card;
            Assert.Equal(("Resolved", "ok"), (c.SvLabel, c.SvKind));
            Assert.False(c.CanFix);
            Assert.False(c.CanIgnore);
        }

        [Fact]
        public void An_ignored_issue_still_prints_as_not_comply()
        {
            var (d, _, core) = New(Issue("1", "A6", st: IssueStatus.Ignored));
            core.SetTab(PanelTab.Ignore);
            d.Open("1");
            Assert.Equal(("Ignored", "ign"), (d.Card.SvLabel, d.Card.SvKind));
            Assert.False(d.Card.CanIgnore);
        }

        [Fact]
        public void A_manual_item_shows_the_reason_and_mark_as_reviewed_never_a_verdict()
        {
            var (d, r, core) = New(Issue("1", "A1", human: true));
            core.SetTab(PanelTab.Manual);
            d.Open("1");
            var c = d.Card;
            Assert.True(c.IsManual);
            Assert.Equal(("Manual review", "man", "man"), (c.SvLabel, c.SvKind, c.ActKind));
            Assert.Equal("why 1", c.Reason);
            Assert.Equal("", c.Desc);                            // the reason is not shown twice
            Assert.False(c.CanFix || c.CanIgnore);
            Assert.Equal("Mark as reviewed", c.ReviewLabel);

            var asked = 0;
            r.FixRequested += _ => asked++;
            r.IgnoreRequested += _ => asked++;
            d.Fix();
            d.Ignore();
            Assert.Equal(0, asked);

            d.ToggleReviewed();
            Assert.True(d.Card.IsReviewed);
            Assert.Equal("✓ Reviewed", d.Card.ReviewLabel);
            d.ToggleReviewed();
            Assert.False(d.Card.IsReviewed);
        }

        [Fact]
        public void A_failed_fix_shows_its_error_and_steps_show_without_a_ready_made_fix()
        {
            var failed = Issue("1", "A4", fixable: true);
            failed.FixError = "Type name already used in this family";
            var (d, _, _) = New(failed, Issue("2", "A6"));
            d.StepsOf = id => new[] { "Open the type", "Rename it" };
            d.Open("1");
            Assert.True(d.Card.HasFixError);
            Assert.Equal("Type name already used in this family", d.Card.FixError);
            Assert.True(d.Card.CanFix);                          // still open: it can be tried again
            Assert.False(d.Card.HasSteps);
            d.Next();
            Assert.Equal("2", d.Card.Id);
            Assert.Equal(new[] { "Open the type", "Rename it" }, d.Card.Steps);
            Assert.Equal("How to fix manually", d.HowToFixLabel);
        }

        [Fact]
        public void Keys_move_fix_ignore_and_escape_closes_on_that_row()
        {
            var (d, r, core) = New(Issue("1", "A1"), Issue("2", "A4", fixable: true), Issue("3", "A5"));
            d.Open("1");
            Assert.Equal(KeyOutcome.Handled, d.HandleKey(PanelKey.J));
            Assert.Equal("2", d.Card.Id);
            d.HandleKey(PanelKey.Right);
            Assert.Equal("3", d.Card.Id);
            d.HandleKey(PanelKey.Right);                         // stays at the end
            Assert.Equal("3", d.Card.Id);
            Assert.False(d.CanNext);
            d.HandleKey(PanelKey.K);
            Assert.Equal("2", d.Card.Id);

            List<string> fix = null;
            r.FixRequested += ids => fix = ids;
            d.HandleKey(PanelKey.F);
            Assert.Equal(new[] { "2" }, fix);

            Assert.Equal(KeyOutcome.Handled, d.HandleKey(PanelKey.Escape));
            Assert.False(d.IsOpen);
            Assert.Equal(1, core.Sel);                           // ⏎ on the list reopens issue 2
            Assert.Equal(KeyOutcome.None, d.HandleKey(PanelKey.J));
        }

        [Fact]
        public void Ignoring_keeps_the_card_in_the_list_it_opened_with()
        {
            var (d, r, core) = New(Issue("1", "A1"), Issue("2", "A4"));
            d.Open("1");
            // The host ignores it and reloads; the Open tab no longer lists it.
            r.Load(new[] { Issue("1", "A1", st: IssueStatus.Ignored), Issue("2", "A4") },
                   AuditSummary.Build(Enumerable.Empty<Bim010Cell>()));
            Assert.True(d.IsOpen);
            Assert.Equal("Issue 1 of 2", d.IdxLabel);
            Assert.Equal("Ignored", d.Card.SvLabel);
            d.Next();
            Assert.Equal("2", d.Card.Id);
        }

        [Fact]
        public void Locate_closes_the_detail_then_asks_the_host()
        {
            var (d, _, _) = New(Issue("1", "A1"), Issue("2", "A4"));
            string located = null;
            d.LocateRequested += id => { located = id; Assert.False(d.IsOpen); };
            d.Open("2");
            d.Locate();
            Assert.Equal("2", located);

            d.IsLocatable = _ => false;                          // project-level check: no button, no request
            located = null;
            d.Open("1");
            Assert.False(d.Card.CanLocate);
            d.Locate();
            Assert.Null(located);
            Assert.True(d.IsOpen);
        }

        [Fact]
        public void The_source_opens_only_when_there_is_a_pdf()
        {
            var (d, _, _) = New(Issue("1", "A1"));
            string opened = null;
            d.SourceRequested += id => opened = id;
            d.Open("1");
            Assert.False(d.Card.CanOpenSource);
            d.OpenSource();
            Assert.Null(opened);
            d.HasSourcePdf = _ => true;
            Assert.True(d.Card.CanOpenSource);
            d.OpenSource();
            Assert.Equal("1", opened);
        }

        [Fact]
        public void A_long_list_shows_a_window_of_dots_around_the_current_issue()
        {
            var issues = Enumerable.Range(1, 40).Select(i => Issue(i.ToString(), "A1")).ToArray();
            var (d, _, _) = New(issues);
            d.Open("20");
            var dots = d.Dots;
            Assert.Equal(DetailScreenModel.MaxDots, dots.Count);
            Assert.Contains(dots, x => x.IsOn && x.Index == 19);
            d.GoTo(39);
            Assert.Equal(39, d.Dots.Last().Index);
            Assert.True(d.Dots.Last().IsOn);
            Assert.Equal("Issue 40 of 40", d.IdxLabel);
        }

        [Fact]
        public void Reset_or_a_new_scan_closes_the_detail()
        {
            var (d, _, core) = New(Issue("1", "A1"));
            d.Open("1");
            core.Reset();
            Assert.False(d.IsOpen);

            var (d2, _, core2) = New(Issue("1", "A1"));
            d2.Open("1");
            core2.BeginExternalRun();
            Assert.False(d2.IsOpen);
        }

        [Fact]
        public void Labels_follow_the_language()
        {
            var (d, _, core) = New(Issue("1", "A1"), Issue("2", "A4"));
            d.Open("1");
            Assert.Equal(("Element", "Required", "In model"), (d.ElementLabel, d.RequiredLabel, d.ActualLabel));
            core.SetLang(PanelStrings.Ms);
            Assert.Equal(PanelStrings.Get(PanelStrings.Ms, "idx") + " 1 " + PanelStrings.Get(PanelStrings.Ms, "of") + " 2", d.IdxLabel);
            Assert.Equal("Cara membaiki secara manual", d.HowToFixLabel);
        }
    }
}
