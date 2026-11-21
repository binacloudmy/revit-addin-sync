using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The Result screen as Clear Sheet draws it: the head (verdict, "n spec checks
    /// fail.", "41% compliant, 25 of 61 checks pass. …", "n checks wait for semak
    /// manual"), the Checklist / Issues tabs, the list of sections or aspects (a row
    /// opens its checks), "Other JKR rules", the split LOD / backend banner, and the
    /// checklist inside a section (state chips counted in that section, a check that
    /// opens to its card). Every number comes from the run's spec report.
    /// </summary>
    public class ResultsClearSheetTests
    {
        private static (ResultsScreenModel r, PanelCore core) Failing() => SpecChecklistTests.Results("jkr_run_failing.json");
        private static (ResultsScreenModel r, PanelCore core) Compliant() => SpecChecklistTests.Results("jkr_run_compliant.json");

        private static List<ChecklistCheck> Checks(ResultsScreenModel r) =>
            r.Checklist.SelectMany(s => s.Items).SelectMany(i => i.Checks).ToList();

        // ── head ──

        [Fact]
        public void The_head_says_the_verdict_the_fail_count_the_score_and_semak_manual_in_both_languages()
        {
            var (r, core) = Failing();
            var fails = r.Spec.FailChecks;
            var (pass, total) = SpecChecklist.Score(r.Spec);
            var pct = SpecChecklist.Pct(r.Spec);
            var person = r.Spec.NeedsPersonChecks;
            Assert.True(fails > 1 && person > 1 && total > 1);   // the fixture fails, plural wording

            Assert.Equal("fail", r.VerdictTone);
            Assert.Equal("Tidak patuh", r.VerdictHead);
            Assert.Equal(fails + " semakan spec gagal.", r.VerdictSub);
            Assert.Equal(pct + "% patuh", r.HeadPctStrong);
            Assert.Equal(", " + pass + " daripada " + total + " semakan lulus. Semakan Senarai dan manusia dikira lulus; semakan AI yang belum dinilai tidak.", r.HeadResultsRest);
            Assert.True(r.HasManualLink);
            Assert.Equal(person + " semakan menunggu semak manual", r.ManualLinkLabel);

            core.SetLang(PanelStrings.En);
            Assert.Equal("Does not comply", r.VerdictHead);
            Assert.Equal(fails + " spec checks fail.", r.VerdictSub);
            Assert.Equal(pct + "% compliant", r.HeadPctStrong);
            Assert.Equal(", " + pass + " of " + total + " checks pass. List and Human checks count as pass; unjudged AI checks do not.", r.HeadResultsRest);
            Assert.Equal(person + " checks wait for semak manual", r.ManualLinkLabel);
            Assert.Equal(pct + "% compliant · " + fails + " fail", r.VerdictShortLine);
        }

        [Fact]
        public void The_headline_card_is_pinned_on_the_overview_a_section_and_the_issue_tabs()
        {
            var (r, _) = Failing();
            Assert.True(r.ShowOverview);
            Assert.True(r.ShowHeadlineCard);          // checklist overview

            r.OpenGroup("D");
            Assert.True(r.IsFocused);
            Assert.True(r.ShowHeadlineCard);          // inside a section

            r.ShowIssues();
            Assert.True(r.ShowList);
            Assert.True(r.ShowHeadlineCard);          // issues tab, same card
        }

        [Fact]
        public void A_run_with_no_failure_says_it_is_not_a_full_pass_while_a_person_still_decides()
        {
            var (r, core) = Compliant();
            Assert.Equal(0, r.Spec.FailChecks);
            Assert.True(r.Spec.NeedsPersonChecks > 0);
            Assert.Equal("pass", r.VerdictTone);
            core.SetLang(PanelStrings.En);
            Assert.Equal("No failures in the checks covered", r.VerdictHead);
            Assert.Equal("Not a full pass yet: some checks are for a person to decide.", r.VerdictSub);
            core.SetLang(PanelStrings.Ms);
            Assert.Equal("Tiada kegagalan dalam semakan yang diliputi", r.VerdictHead);
            Assert.Equal("Belum lulus penuh: sesetengah semakan perlu diputuskan oleh manusia.", r.VerdictSub);
        }

        [Fact]
        public void One_failing_check_and_one_result_read_in_the_singular()
        {
            Assert.Equal("1 spec check fails.", SpecChecklist.Plural("vSubFail", PanelStrings.En, 1));
            Assert.Equal("1 semakan spec gagal.", SpecChecklist.Plural("vSubFail", PanelStrings.Ms, 1));
            Assert.Equal(", 1 of 1 check passes. List and Human checks count as pass; unjudged AI checks do not.", SpecChecklist.Plural("hRest", PanelStrings.En, 1, 1, 1));
        }

        [Fact]
        public void The_manual_link_opens_the_checks_a_person_decides()
        {
            var (r, _) = Failing();
            r.GoNeedsPerson();
            Assert.True(r.IsFocused);
            Assert.False(r.ShowOverview);
            Assert.Equal("person", r.StateFilter);
            Assert.Equal(r.Spec.NeedsPersonChecks, Checks(r).Count);
            Assert.Equal("Semua semakan", r.FocusName);
        }

        // ── tabs ──

        [Fact]
        public void The_tabs_are_Checklist_with_its_spec_checks_and_Issues_with_the_open_issues()
        {
            var (r, core) = Failing();
            Assert.Equal(r.Spec.Checks.Count, r.ChecklistTabN);
            Assert.Equal(core.TabCounts()[PanelTab.Open], r.IssuesTabN);
            Assert.Equal("Isu", r.IssuesTabLabel);
            core.SetLang(PanelStrings.En);
            Assert.Equal("Issues", r.IssuesTabLabel);
            Assert.True(r.ShowOverview);
            Assert.False(r.IssuesTabOn);

            r.ShowIssues();
            Assert.True(r.ShowList);
            Assert.True(r.IssuesTabOn);
            Assert.Equal(PanelTab.Open, core.Tab);
            r.ShowChecklistTab();
            Assert.True(r.ShowOverview);
        }

        [Fact]
        public void The_footer_exports_the_Borang()
        {
            var (r, core) = Failing();
            Assert.Equal("Eksport Borang", r.ExportLabel);
            Assert.Equal("Set semula", r.ResetLabel);
            core.SetLang(PanelStrings.En);
            Assert.Equal("Export Borang", r.ExportLabel);
            Assert.Equal("Reset", r.ResetLabel);
            Assert.Equal("Keyboard shortcuts", r.KeysAria);
            Assert.Equal(new[] { "f", "p", "h", "m", "l", "n", "x", "" },
                         new[] { "fail", "pass", "partial", "person", "listed", "na", "none", "all" }.Select(SpecChecklist.TickOfTone));
        }

        // ── the list: a row opens its checks ──

        [Fact]
        public void A_section_row_opens_that_section_with_its_head_ticks_and_state_chips()
        {
            var (r, core) = Failing();
            var row = r.GroupRows.Single(g => g.Key == "D");
            r.OpenGroup("D");
            Assert.True(r.IsFocused);
            Assert.Equal("D", core.Cat);                                    // also the Issues filter (item 10)
            Assert.Equal("D", r.FocusLetter);
            Assert.Equal(row.Name, r.FocusName);
            Assert.Equal(row.PctLabel, r.FocusPctLabel);
            Assert.Equal(row.Ticks, r.FocusTicks);
            Assert.Equal("Seksyen 4 daripada 5", r.FocusPosition);
            core.SetLang(PanelStrings.En);
            Assert.Equal("Section 4 of 5", r.FocusPosition);

            var inD = r.Spec.Checks.Where(c => c.Check.Section == "D").ToList();
            Assert.Equal(inD.Count, r.StateChips.Single(c => c.Key == SpecChecklist.AllStates).N);
            Assert.Equal(inD.Count(c => c.State == SpecState.Fail), r.StateChips.Single(c => c.Key == "fail").N);
            Assert.Equal(inD.Select(c => c.Check.Id), Checks(r).Select(c => c.Id));
            Assert.True(r.ShowFocusChips);

            r.OpenGroup("D");                                               // again: stays open
            Assert.Equal("D", core.Cat);
            r.CloseFocus();
            Assert.True(r.ShowOverview);
            Assert.Equal(PanelCore.AllSections, core.Cat);
            Assert.Equal(SpecChecklist.AllStates, r.StateFilter);
        }

        [Fact]
        public void A_section_links_to_its_issues_and_the_Issues_view_keeps_the_section_filter_until_the_pill_clears_it()
        {
            var (r, core) = Failing();
            Assert.False(r.HasSectionIssuesLink);                          // the list: no section open
            // A section with more than one open issue in this fixture.
            var sec = PanelCore.Secs.First(k => core.Issues.Count(x => x.Sec == k && !x.IsHuman && x.Status == ItemStatus.Open) > 1);
            r.OpenGroup(sec);
            var n = core.TabCounts()[PanelTab.Open];
            Assert.True(n > 1);
            Assert.True(r.HasSectionIssuesLink);
            Assert.Equal(n + " isu dalam seksyen ini", r.SectionIssuesLabel);
            core.SetLang(PanelStrings.En);
            Assert.Equal(n + " issues in this section", r.SectionIssuesLabel);
            Assert.Equal("1 issue in this section", SpecChecklist.Plural("secIssues", PanelStrings.En, 1));

            r.ShowSectionIssues();
            Assert.True(r.ShowList);
            Assert.Equal(PanelTab.Open, core.Tab);
            Assert.Equal(sec, core.Cat);                                    // the filter is kept
            Assert.True(r.HasCat);                                          // the section pill shows (item 9)
            Assert.Equal(n, r.IssuesTabN);                                  // counted in the section
            Assert.Equal("Issues · Section " + sec, r.IssuesTabLabel);
            core.SetLang(PanelStrings.Ms);
            Assert.Equal("Isu · Seksyen " + sec, r.IssuesTabLabel);

            r.ClearCat();                                                   // the pill clears it
            Assert.Equal(PanelCore.AllSections, core.Cat);
            Assert.False(r.HasCat);
            Assert.Equal("Isu", r.IssuesTabLabel);
            Assert.True(r.ShowList);
        }

        [Fact]
        public void No_issues_link_in_an_aspect_or_the_other_rules()
        {
            var (r, _) = Failing();
            r.ShowAspectsView();
            r.OpenGroup("N");
            Assert.False(r.HasSectionIssuesLink);
            r.CloseFocus();
            r.ShowSectionsView();
            r.OpenGroup(SpecChecklist.OtherKey);
            Assert.False(r.HasSectionIssuesLink);
        }

        [Fact]
        public void An_aspect_row_opens_the_checks_of_that_aspect_and_leaves_the_section_filter_alone()
        {
            var (r, core) = Failing();
            r.ShowAspectsView();
            var row = r.GroupRows.Single(g => g.Key == "P");
            r.OpenGroup("P");
            Assert.True(r.IsFocused);
            Assert.Equal(PanelCore.AllSections, core.Cat);
            Assert.Equal("P", r.FocusLetter);
            Assert.Equal(row.Name, r.FocusName);
            Assert.Equal(row.Ticks, r.FocusTicks);
            Assert.Equal("Aspek 2 daripada 4", r.FocusPosition);
            var ids = SpecCatalog.Checks.Where(c => SpecAspects.Of(c.Id) == SpecAspect.Parameters).Select(c => c.Id).ToList();
            Assert.Equal(ids, Checks(r).Select(c => c.Id));
            Assert.Equal(ids.Count, r.StateChips.Single(c => c.Key == SpecChecklist.AllStates).N);
            Assert.True(r.ShowSectionHeads);                                // the checks come from several sections

            r.SetStateFilter("fail");                                       // a chip filters within the aspect
            Assert.All(Checks(r), c => Assert.Equal("fail", c.Tone));
            r.CloseFocus();
            Assert.True(r.ShowOverview);
            Assert.True(r.ShowAspects);                                     // back to the same view
        }

        [Fact]
        public void The_other_rules_row_counts_its_fails_and_opens_those_rules()
        {
            var (r, core) = Failing();
            var failing = r.Spec.Others.Count(o => o.State == SpecState.Fail);
            Assert.True(r.HasOtherRow);
            Assert.Equal("Peraturan JKR lain", r.OtherRowName);
            Assert.Equal("bukan dalam spec Borang", r.OtherRowNote);
            Assert.Equal(failing + " gagal", r.OtherRowCount);
            core.SetLang(PanelStrings.En);
            Assert.Equal("Other JKR rules", r.OtherRowName);
            Assert.Equal("not in the Borang spec", r.OtherRowNote);
            Assert.Equal(failing + " fail", r.OtherRowCount);

            r.OpenGroup(SpecChecklist.OtherKey);
            Assert.True(r.IsFocused);
            Assert.False(r.ShowFocusChips);                                 // not spec checks: no state filter
            Assert.Equal("Other JKR rules", r.FocusName);
            Assert.Equal(PanelCore.AllSections, core.Cat);
            var sec = Assert.Single(r.Checklist);
            Assert.Equal(SpecChecklist.OtherKey, sec.Key);
            Assert.Equal(r.Spec.Others.Count, sec.Items.SelectMany(i => i.Checks).Count());
            Assert.Equal("", r.FocusPosition);
        }

        [Fact]
        public void Escape_in_a_section_goes_back_to_the_list()
        {
            var (r, _) = Failing();
            r.OpenGroup("C");
            Assert.Equal(KeyOutcome.Handled, r.HandleKey(PanelKey.Escape, typing: false));
            Assert.True(r.ShowOverview);
        }

        // ── checks inside a section ──

        [Fact]
        public void The_first_failing_check_of_a_section_opens_and_a_check_toggles()
        {
            var (r, _) = Failing();
            r.OpenGroup("D");
            var list = Checks(r);
            var first = list.First(c => c.Tone == "fail");
            Assert.True(first.IsOpen);
            Assert.All(list.Where(c => c.Id != first.Id), c => Assert.False(c.IsOpen));
            var other = list.First(c => c.Id != first.Id);
            r.ToggleCheck(first.Id);
            r.ToggleCheck(other.Id);
            Assert.False(Checks(r).Single(c => c.Id == first.Id).IsOpen);
            Assert.True(Checks(r).Single(c => c.Id == other.Id).IsOpen);
            r.CloseFocus();
            r.OpenGroup("D");                                               // a new visit starts fresh
            Assert.True(Checks(r).Single(c => c.Id == first.Id).IsOpen);
        }

        [Fact]
        public void Why_this_fails_is_closed_until_asked_for()
        {
            var (r, core) = Failing();
            core.SetLang(PanelStrings.En);
            r.OpenGroup("D");
            ChecklistCheck Da1() => Checks(r).Single(c => c.Id == "Da.1");
            Assert.False(Da1().ShowWhy);
            Assert.Equal("Why this fails", Da1().WhyToggleLabel);
            r.ToggleWhy("Da.1");
            Assert.True(Da1().ShowWhy);
            core.SetLang(PanelStrings.Ms);
            Assert.Equal("Kenapa ini gagal", Da1().WhyToggleLabel);
        }

        [Fact]
        public void Semak_manual_is_the_name_of_the_person_state()
        {
            Assert.Equal("Semak manual", SpecChecklist.StateLabel("person", PanelStrings.En));
            Assert.Equal("Semak manual", SpecChecklist.StateLabel("person", PanelStrings.Ms));
        }

        [Fact]
        public void The_legend_lists_the_states_the_run_has_and_aspects_say_what_they_are()
        {
            var (r, core) = Failing();
            core.SetLang(PanelStrings.En);
            var tones = r.Spec.Checks.Select(c => SpecChecklist.ToneOf(c.State)).Distinct().ToList();
            // Clear Sheet's legend order: Fails, Pass, Partly, Semak manual, Listed, Not applicable (Not checked).
            Assert.Equal(new[] { "fail", "pass", "partial", "person", "listed", "na", "none" }.Where(tones.Contains), r.Legend.Select(l => l.Key));
            Assert.Contains(r.Legend, l => l.Label == "Semak manual");
            Assert.False(r.ShowAspectsNote);
            r.ShowAspectsView();
            Assert.True(r.ShowAspectsNote);
            Assert.Equal("Aspects regroup the same 61 checks by what they test: naming, parameters and information, quality and integrity, and design geometry.",
                         r.AspectsNote);
        }

        // ── banners ──

        [Fact]
        public void A_changed_LOD_says_which_LOD_the_result_is_for()
        {
            var (r, core) = Failing();
            Assert.False(r.ShowLodBanner);
            core.SetLod("400");
            Assert.True(r.ShowLodBanner);
            Assert.True(r.ShowBanner);
            Assert.Equal("Keputusan ini untuk LOD 300.", r.LodBannerStrong);
            Assert.Equal("Imbas semula untuk menyemak model terhadap LOD 400.", r.LodBannerRest);
            core.SetLang(PanelStrings.En);
            Assert.Equal("This result is for LOD 300.", r.LodBannerStrong);
            Assert.Equal("Re-scan to check the model against LOD 400.", r.LodBannerRest);
        }

        [Fact]
        public void Backend_unavailable_names_the_checks_that_need_it()
        {
            Assert.Equal("B1.2 needs it, so it shows as not checked, never as passed.",
                         SpecChecklist.BackendNeedsLine(new[] { "B1.2" }, PanelStrings.En));
            Assert.Equal("A2.1, B1.1 need it, so they show as not checked, never as passed.",
                         SpecChecklist.BackendNeedsLine(new[] { "A2.1", "B1.1" }, PanelStrings.En));
            Assert.Equal("Checks that need it show as not checked, never as passed.",
                         SpecChecklist.BackendNeedsLine(new string[0], PanelStrings.En));
            Assert.Equal("B1.2 memerlukannya, jadi ia ditunjuk sebagai belum disemak, bukan sebagai lulus.",
                         SpecChecklist.BackendNeedsLine(new[] { "B1.2" }, PanelStrings.Ms));

            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var start = new StartScreenModel(core);
            core.SetLod("300"); core.BeginExternalRun(); core.FinishExternalRun();
            var r = new ResultsScreenModel(core, start);
            var spec = SpecReport.Build(new List<SpecResult>(), "timeout");
            r.Load(new List<RevitWebAppSync.UI.Jkr.ViewModels.IssueVm>(), null, spec);
            var ids = spec.Checks.Where(c => c.State == SpecState.NotChecked && SpecReport.BackendMappedChecks.Contains(c.Check.Id))
                                 .Select(c => c.Check.Id).ToList();
            Assert.NotEmpty(ids);
            Assert.True(r.ShowBackendBanner);
            Assert.True(r.ShowBanner);
            Assert.False(r.ShowBothBanners);
            Assert.Equal("Backend BINA tidak tersedia.", r.BackendBannerStrong);
            Assert.Equal(SpecChecklist.BackendNeedsLine(ids, PanelStrings.Ms), r.BackendBannerRest);
            core.SetLang(PanelStrings.En);
            Assert.Equal("BINA backend unavailable.", r.BackendBannerStrong);
            core.SetLod("400");
            Assert.True(r.ShowBothBanners);
        }
    }
}
