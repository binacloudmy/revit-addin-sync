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

        private static IssueVm AiCandidate(string id, string key)
        {
            var i = Issue(id, key);
            i.CheckId = "element.material.02";
            i.RevitElementId = 412907;
            return i;
        }

        private static (DetailScreenModel detail, ResultsScreenModel results, PanelCore core) New(params IssueVm[] issues)
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core) { FileLabel = "jkrAR24.rvt" };
            core.SetLod("300");
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
            Assert.Equal("Issue 2 of 3 open", d.IdxLabel);
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
            Assert.Equal("Issue 1 of 1 in Manual", d.IdxLabel);
            Assert.False(c.CanMarkManual || c.CanReopen || c.HasApply);
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
            Assert.Equal("Issue 1 of 2 ignored", d.IdxLabel);     // live F11: the shown issue's state
            Assert.Equal("Ignored", d.Card.SvLabel);
            d.Next();
            Assert.Equal("2", d.Card.Id);
        }

        [Fact]
        public void The_header_says_the_state_of_the_issue_shown_not_of_the_tab()
        {
            var (d, r, core) = New(Issue("1", "A1", fixable: true), Issue("2", "A4"));
            var none = AuditSummary.Build(Enumerable.Empty<Bim010Cell>());
            d.Open("1");
            Assert.Equal("Issue 1 of 2 open", d.IdxLabel);
            r.Load(new[] { Issue("1", "A1", st: IssueStatus.ManualFixNeeded, fixable: true), Issue("2", "A4") }, none);
            Assert.Equal("Issue 1 of 2 marked manual", d.IdxLabel);
            r.Load(new[] { Issue("1", "A1", fixable: true), Issue("2", "A4") }, none);
            Assert.Equal("Issue 1 of 2 open", d.IdxLabel);                    // reopened
            r.Load(new[] { Issue("1", "A1", st: IssueStatus.Fixed, fixable: true), Issue("2", "A4") }, none);
            Assert.Equal("Issue 1 of 2 resolved", d.IdxLabel);
        }

        [Fact]
        public void A_reviewed_semak_manual_item_says_reviewed()
        {
            var reviewed = Issue("1", "A2", human: true);
            reviewed.Reviewed = true;
            var (d, _, core) = New(reviewed, Issue("2", "A3", human: true));
            core.SetTab(PanelTab.Manual);
            d.Open("1");
            Assert.Equal("Issue 1 of 2 reviewed", d.IdxLabel);
            d.Next();
            Assert.Equal("Issue 2 of 2 in Manual", d.IdxLabel);
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
            Assert.Equal("Issue 40 of 40 open", d.IdxLabel);
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
            Assert.Equal("How to fix manually", d.HowToFixLabel);
        }

        // ── Task 14: Clear Sheet detail (Mark as Manual / Reopen, AI fixer proposal, copy) ──

        [Fact]
        public void An_open_issue_that_may_be_marked_offers_mark_as_manual()
        {
            var (d, r, _) = New(Issue("1", "A4", fixable: true), Issue("2", "A4", IssuePriority.High, fixable: true));
            d.Open("1");
            var c = d.Card;
            Assert.True(c.CanMarkManual);
            Assert.False(c.CanReopen);
            Assert.False(c.IsMarkedManual);
            Assert.True(c.HasApply);
            Assert.Equal("Apply fix", c.ApplyLabel);
            Assert.Equal("Mark as Manual", d.MarkManualLabel);

            string marked = null;
            r.MarkManualRequested += id => marked = id;
            d.MarkManual();
            Assert.Equal("1", marked);

            d.Next();                                            // High with a fix: Mark as Manual is not offered (AI-480: a High row with nothing to fix gets it)
            Assert.False(d.Card.CanMarkManual);
            marked = null;
            d.MarkManual();
            Assert.Null(marked);
        }

        [Fact]
        public void A_marked_manual_issue_shows_the_tag_and_reopen_instead_of_ignore_and_apply()
        {
            var (d, r, _) = New(Issue("1", "A4", fixable: true, st: IssueStatus.ManualFixNeeded));
            d.Open("1");
            var c = d.Card;
            Assert.True(c.IsMarkedManual);
            Assert.Equal("Manual · you'll fix it in Revit", c.ManualTag);
            Assert.Equal(("Medium", "Med"), (c.SvLabel, c.SvKind));   // still an open fail
            Assert.True(c.CanReopen);
            Assert.False(c.CanMarkManual || c.CanIgnore || c.CanFix || c.HasApply);
            Assert.Equal("Reopen", d.ReopenLabel);

            List<string> fix = null, ign = null;
            string reopened = null;
            r.FixRequested += ids => fix = ids;
            r.IgnoreRequested += ids => ign = ids;
            r.ReopenRequested += id => reopened = id;
            d.HandleKey(PanelKey.F);
            d.HandleKey(PanelKey.A);
            Assert.Null(fix);
            Assert.Null(ign);
            d.Reopen();
            Assert.Equal("1", reopened);
        }

        [Fact]
        public void An_ignored_issue_offers_reopen_only()
        {
            var (d, r, core) = New(Issue("1", "A6", st: IssueStatus.Ignored, fixable: true));
            core.SetTab(PanelTab.Ignore);
            d.Open("1");
            Assert.Equal("Issue 1 of 1 ignored", d.IdxLabel);
            var c = d.Card;
            Assert.True(c.CanReopen);
            Assert.False(c.CanMarkManual || c.CanIgnore || c.HasApply);
            string reopened = null;
            r.ReopenRequested += id => reopened = id;
            d.Reopen();
            Assert.Equal("1", reopened);
        }

        [Fact]
        public void A_resolved_issue_offers_no_reopen()
        {
            var (d, r, core) = New(Issue("1", "A5", st: IssueStatus.Fixed, fixable: true));
            core.SetTab(PanelTab.Resolved);
            d.Open("1");
            Assert.Equal("Issue 1 of 1 resolved", d.IdxLabel);
            Assert.False(d.Card.CanReopen || d.Card.CanMarkManual || d.Card.HasApply);
            string reopened = null;
            r.ReopenRequested += id => reopened = id;
            d.Reopen();
            Assert.Null(reopened);
        }

        [Fact]
        public void An_ai_fixer_candidate_without_a_value_claims_no_proposal()
        {
            var (d, _, _) = New(AiCandidate("1", "D"));
            d.Open("1");
            var c = d.Card;
            Assert.True(c.IsAiCanPropose);
            Assert.False(c.IsAiProposal);
            Assert.False(c.HasApply);                            // nothing to apply yet
            Assert.False(c.CanFix);
            Assert.Equal("AI fixer can propose", d.AiCanTitle);
            Assert.Equal("No ready-made fix yet. Ask the AI fixer for one, or enter the value yourself; you see it before anything is written.", d.AiCanText);
        }

        [Fact]
        public void An_ai_fixer_proposal_shows_its_value_reason_and_apply_ai_fix()
        {
            var (d, r, core) = New(AiCandidate("1", "D"));
            var item = core.Issues.Single();
            item.Prop = "jkrMT_ST_Putih";
            item.AiReason = new Text("Padanan terdekat dalam senarai bahan JKR.", "Closest match in the JKR material list.");
            d.Open("1");
            var c = d.Card;
            Assert.True(c.IsAiProposal);
            Assert.False(c.IsAiCanPropose);
            Assert.False(c.CanFix);                              // not the ready-made fix block
            Assert.Equal("jkrMT_ST_Putih", c.Prop);
            Assert.Equal("Closest match in the JKR material list.", c.AiReason);
            Assert.True(c.HasAiReason);
            Assert.True(c.HasApply);
            Assert.Equal("Apply AI fix", c.ApplyLabel);
            Assert.False(c.HasSteps);
            Assert.Equal("Proposed by the AI fixer", d.AiProposedLabel);
            Assert.StartsWith("This is the AI fixer's best match, not a ready-made rule.", d.AiCaution);

            List<string> fix = null;
            r.FixRequested += ids => fix = ids;
            d.HandleKey(PanelKey.F);
            Assert.Equal(new[] { "1" }, fix);
        }

        [Fact]
        public void Detail_copy_follows_clear_sheet_in_both_languages()
        {
            var (d, _, core) = New(Issue("1", "A5", fixable: true));
            d.Open("1");
            Assert.Equal("You see every change again before anything is written.", d.SeeAgainNote);
            Assert.Equal("Source: Borang A5, x", d.SourceLine("Borang A5 · x"));
            Assert.Equal("This item is not counted in the compliance % and not counted as a failure. It prints as “manual review” on the Borang.", d.ManualNote);
            Assert.Equal("Mark as Manual", d.MarkManualLabel);
            Assert.Equal("Source: Borang A5, x", d.SourceLine("Borang A5 · x"));
        }

        // ── AI-435: the AI-fixer row ──

        [Fact]
        public void An_ai_fixer_card_always_has_the_fixer_row_with_the_manual_input_and_no_suggestion_yet()
        {
            var (d, _, _) = New(AiCandidate("1", "D"));
            d.Open("1");
            Assert.True(d.Card.IsFixerRow);
            Assert.NotNull(d.Fixer);
            Assert.Equal(FixerAiState.NotAsked, d.Fixer.AiState);
            Assert.Equal("No suggestion yet.", d.Fixer.AiLine);
            Assert.True(d.Fixer.IsFreeText);                     // manual input is there with no suggestion
        }

        [Fact]
        public void A_ready_made_fix_or_a_person_item_has_no_fixer_row()
        {
            var (d, _, _) = New(Issue("1", "A5", fixable: true), Issue("2", "A6", human: true));
            d.Open("1");
            Assert.False(d.Card.IsFixerRow);
            Assert.Null(d.Fixer);
            d.Open("2");
            Assert.Null(d.Fixer);
        }

        [Fact]
        public void With_a_suggestion_the_row_shows_it_with_its_reason_and_the_manual_input_stays()
        {
            var i = AiCandidate("1", "D");
            i.AiAsked = true;
            i.AiProposal = new AiSuggestion { Action = "set_parameter", Value = "(kt) Konkrit", Reason = "closest valid material code" };
            var (d, _, _) = New(i);
            d.Open("1");
            Assert.Equal(FixerAiState.Suggested, d.Fixer.AiState);
            Assert.Equal("(kt) Konkrit", d.Fixer.AiValue);
            Assert.Equal("closest valid material code", d.Fixer.AiReason);
            Assert.True(d.Fixer.CanAccept);
            Assert.True(d.Card.IsAiProposal && d.Card.HasApply);  // Apply AI fix (F) accepts it
            d.Fixer.ManualValue = "(bb02) Batu Bata";
            Assert.True(d.Fixer.IsFreeText);
        }

        [Fact]
        public void Asked_with_nothing_back_says_so_explicitly()
        {
            var i = AiCandidate("1", "D");
            i.AiAsked = true;
            var (d, _, _) = New(i);
            d.Open("1");
            Assert.Equal(FixerAiState.NoSuggestion, d.Fixer.AiState);
            Assert.Equal("The AI fixer has no suggestion for this item.", d.Fixer.AiLine);
        }

        [Fact]
        public void The_reference_set_reaches_the_row_as_a_dropdown()
        {
            var i = AiCandidate("1", "D");
            i.ReferenceValues = new[] { "(kt) Konkrit", "(bb02) Batu Bata" };
            var (d, _, _) = New(i);
            d.Open("1");
            Assert.True(d.Fixer.HasOptions);
        }

        [Fact]
        public void Ask_AI_and_a_valid_manual_value_go_to_the_pane_and_an_invalid_one_does_not()
        {
            var (d, r, _) = New(AiCandidate("1", "D"));
            d.RuleOf = id => "Naming convention";
            d.CategoryOf = id => "Doors";
            d.Open("1");
            string asked = null; (string id, string v)? written = null;
            r.AskAiRequested += id => asked = id;
            r.ManualFixRequested += (id, v) => written = (id, v);
            d.AskAi();
            Assert.Equal("1", asked);
            Assert.True(d.IsBusy);                               // AI-443: busy until the host answers
            r.EndBusy();                                         // the host's answer
            d.Fixer.ManualValue = "Basic Door";                  // fails the rule's own check
            d.WriteManual();
            Assert.Null(written);
            d.Fixer.ManualValue = "jkrAR_dor-k_(ky) Pintu Kayu";
            d.WriteManual();
            Assert.Equal(("1", "jkrAR_dor-k_(ky) Pintu Kayu"), written);
        }

        [Fact]
        public void What_was_typed_survives_a_refresh_of_the_same_card()
        {
            var (d, r, _) = New(AiCandidate("1", "D"));
            d.Open("1");
            d.Fixer.ManualValue = "jkrAR_dor";
            r.Load(new[] { AiCandidate("1", "D") }, AuditSummary.Build(Enumerable.Empty<Bim010Cell>()));
            Assert.Equal("jkrAR_dor", d.Fixer.ManualValue);
        }

        [Fact]
        public void The_ai_proposal_never_makes_a_row_auto_fixable_so_no_bulk_path_can_write_it()
        {
            var i = AiCandidate("1", "D");
            i.AiProposal = new AiSuggestion { Action = "rename_type", Value = "jkrAR_x" };
            Assert.False(i.AutoFixable);
            Assert.True(i.AiFixCandidate);                      // still an AI-fixer row after it was asked
            Assert.Equal("", i.FixAction);                      // the Fix* fields are untouched until accepted
            i.UseAiSuggestion();
            Assert.True(i.FixProposedByAi);
            Assert.Equal("jkrAR_x", i.FixValue);
            Assert.False(i.AutoFixable);                        // and still not auto-fixable once loaded for one write
        }

        [Fact]
        public void The_detail_shows_the_busy_text_and_progress_of_the_results_screen()
        {
            var (d, r, _) = New(Issue("1", "A4", fixable: true));
            d.Open("1");
            Assert.False(d.IsBusy);
            r.BeginBusy("AI looking for a suggestion…", 0, "1");
            Assert.True(d.IsBusy);
            Assert.Equal("AI looking for a suggestion…", d.BusyText);
            Assert.False(d.CanAct);
            r.EndBusy();
            Assert.False(d.IsBusy);
            Assert.True(d.CanAct);
        }
    }
}
