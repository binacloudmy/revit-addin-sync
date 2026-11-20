using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using RevitWebAppSync.Services.JkrSpec;
using RevitWebAppSync.UI.Jkr.Export;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// JKR Round 2, stream S: AI-414 (C5.1.1 severity + capped notes), AI-418 (C1.5.2 count vs list),
    /// AI-420 (C4.2.2 capped view). Results come from the REAL evaluators over fixture models
    /// (SpecChecks.Rules), then SpecReport / SpecBorang / IssueMapper; no Revit.
    /// </summary>
    [Collection("PaneLanguage")]
    public class SpecNotesSeverityTests
    {
        private static List<SpecResult> Run(string id, SpecModel m)
        {
            var list = SpecChecks.Rules[id]((m ?? new SpecModel()).Normalize()).ToList();
            foreach (var r in list) { r.Source = SpecReport.Model; if (string.IsNullOrEmpty(r.CheckId)) r.CheckId = id; }
            return list;
        }

        private static string RowText(Bim010Summary s, string key) => string.Join(" | ", s.Rows[key].Notes);

        private static SpecModel SixUnloadedLinks()
        {
            var m = new SpecModel { Discipline = "AR" };
            foreach (var link in new[] { "jkrST22_4a", "jkrME22_4a_ACMV", "jkrME22_3b_SP", "jkrME22_3b_FP", "jkrME22_3b_CW", "jkrEL22_3b" })
                m.Links.Add(new SpecLink { Name = link + ".rvt", Status = "Unloaded" });
            return m;
        }

        private static SpecModel SixLevelsTwoWith3D()
        {
            var m = new SpecModel { Discipline = "AR" };
            var names = new[] { "Tanah", "Pentas", "Langkan", "Aras Bumbung", "Aras 05", "Aras 06" };
            for (var i = 0; i < names.Length; i++)
                m.Levels.Add(new SpecLevel { Id = i + 1, Name = names[i], Elevation = i * 4000, IsStory = true });
            m.Views.Add(new SpecView { Id = 90, Name = "Dokumen 3D Aras 05", Kind = "ThreeD", OnSheet = true, SheetNumber = "A-301" });
            m.Views.Add(new SpecView { Id = 91, Name = "Dokumen 3D Aras 06", Kind = "ThreeD", OnSheet = true, SheetNumber = "A-301" });
            return m;
        }

        // ---------- AI-414 ----------

        [Fact]
        public void AI414_C511_six_unloaded_links_the_Borang_remark_lists_EL_and_says_how_many_more()
        {
            var list = Run("C5.1.1", SixUnloadedLinks());
            var report = SpecReport.Build(list);
            var c = report.For("C5.1.1");
            Assert.Equal(6, c.AllNotes.Count);                               // the pane keeps all six per-item notes
            Assert.DoesNotContain(c.AllNotes, n => n.Contains("link tidak sah"));   // ...and never the summary
            var s = SpecBorang.Build(report, list);
            var text = RowText(s, "C5.1");
            Assert.Contains("jkrEL22_3b", text);                             // ONE summary note lists every link (EL included)
            Assert.StartsWith("CRITICAL: 6 link problem(s)", text);          // first
            Assert.Contains("+4 more", s.Rows["C5.1"].Notes);               // 3 shown (summary + 2) of 6 per-item: +4, never silent
        }

        [Fact]
        public void AI414_a_capped_note_list_ends_with_plus_N_more_in_EN_and_lagi_in_BM()
        {
            var list = Enumerable.Range(1, 5).Select(i => { var r = SpecChecks.Fail("C4.2.1", "Sheet S" + i + ": tiada title block", i, "S" + i); r.Source = SpecReport.Model; return r; }).ToList();
            var report = SpecReport.Build(list);
            var c = report.For("C4.2.1");
            Assert.Equal(3, c.Notes.Count);
            Assert.Equal(2, c.NotesHidden);
            Assert.Contains("+2 more", SpecBorang.Build(report, list).Rows["C4.2"].Notes);
            c.NotesEn = new List<string>();                                  // no English twin: the BM list is used
            Assert.Contains("+2 lagi", SpecBorang.Build(report, list).Rows["C4.2"].Notes);
        }

        [Fact]
        public void An_uncapped_list_gets_no_marker()
        {
            var list = Enumerable.Range(1, 3).Select(i => { var r = SpecChecks.Fail("C4.2.1", "Sheet S" + i + ": tiada title block", i, "S" + i); r.Source = SpecReport.Model; return r; }).ToList();
            var text = RowText(SpecBorang.Build(SpecReport.Build(list), list), "C4.2");
            Assert.DoesNotContain("more", text);
            Assert.DoesNotContain("lagi", text);
        }

        // ---------- AI-414: severity ----------

        [Fact]
        public void AI414_C511_SPEC_issue_priority_is_High_because_the_spec_says_KRITIKAL()
        {
            var list = Run("C5.1.1", SixUnloadedLinks());
            var issues = IssueMapper.FromModelResults(list, PanelStrings.En);
            Assert.Equal(6, issues.Count);
            Assert.All(issues, i => Assert.Equal(IssuePriority.High, i.Priority));
            Assert.All(issues, i => Assert.Equal(JkrTierMap.Label(IssuePriority.High), IssueExportRows.Cells(i)[3]));
            Assert.Equal(6, SpecReport.Build(list).For("C5.1.1").Failed);      // severity changes no pass / fail
        }

        [Fact]
        public void AI414_a_missing_link_discipline_is_critical_too_and_other_spec_fails_stay_Medium()
        {
            var noLinks = Run("C5.1.1", new SpecModel { Discipline = "AR" });
            Assert.Equal(3, noLinks.Count(r => r.Status == "fail" && r.Critical));
            var other = Run("C4.2.1", new SpecModel { Discipline = "AR", Sheets = { new SpecSheet { Id = 1, Number = "A-1", Name = "x", TitleBlock = "" } } });
            var issue = Assert.Single(IssueMapper.FromModelResults(other, PanelStrings.En));
            Assert.False(other.Single(r => r.Status == "fail").Critical);
            Assert.Equal(IssuePriority.Medium, issue.Priority);
        }

        [Fact]
        public void AI414_the_merge_of_two_results_for_one_issue_keeps_the_highest_priority()
        {
            var plain = SpecChecks.Fail("C5.1.1", "link ST", key: "ST"); plain.Source = SpecReport.Model;
            var crit = SpecChecks.Fail("C5.1.1", "KRITIKAL: link ST", key: "ST").Critical(); crit.Source = SpecReport.Model;
            Assert.Equal(plain.IssueId, crit.IssueId);
            Assert.Equal(IssuePriority.High, Assert.Single(IssueMapper.FromModelResults(new[] { plain, crit }, PanelStrings.En)).Priority);
            Assert.Equal(IssuePriority.High, Assert.Single(IssueMapper.FromModelResults(new[] { crit, plain }, PanelStrings.En)).Priority);
        }

        // ---------- AI-418 ----------

        [Fact]
        public void AI418_C152_6_levels_2_with_3D_says_2_of_6_and_the_Borang_names_all_4_missing()
        {
            var list = Run("C1.5.2", SixLevelsTwoWith3D());
            Assert.Equal(4, list.Count(r => r.Status == "fail"));
            var report = SpecReport.Build(list);
            Assert.Equal(4, report.For("C1.5.2").Failed);
            Assert.Equal(4, report.For("C1.5.2").AllNotes.Count);          // pane: all four per-item notes
            var text = RowText(SpecBorang.Build(report, list), "C1.5");
            Assert.Contains("2/6", text);                                   // the count is right
            Assert.Contains("(no Dokumen 3D view: Tanah, Pentas, Langkan, Aras Bumbung)", text);   // says what is missing, exactly
            var bm = report.For("C1.5.2").SummaryNote;
            Assert.Contains("(tiada 3D view dalam set Dokumen: Tanah, Pentas, Langkan, Aras Bumbung)", bm);
            foreach (var lv in new[] { "Tanah", "Pentas", "Langkan", "Aras Bumbung" })   // and the list matches it
                Assert.True(text.Contains(lv), "Borang remark misses '" + lv + "': " + text);
            Assert.EndsWith("+2 more", text);                                // 2 of the 4 per-item notes beyond the summary
        }

        [Fact]
        public void AI418_C152_with_no_cap_needed_no_summary_and_no_marker()
        {
            var m = SixLevelsTwoWith3D();
            m.Levels.RemoveRange(0, 2);                                     // 4 levels, 2 missing: 2 notes fit the cap
            var list = Run("C1.5.2", m);
            var report = SpecReport.Build(list);
            Assert.Equal(2, report.For("C1.5.2").Notes.Count);
            Assert.Equal(0, report.For("C1.5.2").NotesHidden);
            var text = RowText(SpecBorang.Build(report, list), "C1.5");
            Assert.DoesNotContain("more", text);
            Assert.DoesNotContain("no Dokumen 3D view:", text);
        }

        // ---------- AI-420 ----------

        [Fact]
        public void AI420_C422_5_sheets_x_2_fields_Author_all_10_issues_exist_and_the_Borang_shows_3_plus_7_more()
        {
            var m = new SpecModel { Discipline = "AR" };
            for (var sh = 1; sh <= 5; sh++)
                m.Sheets.Add(new SpecSheet
                {
                    Id = sh * 100, Number = "A-" + sh, Name = "Sheet S" + sh, TitleBlock = "jkrAR_TitleBlock_A1 : A1",
                    Params = new Dictionary<string, string>
                    {
                        ["Project Name"] = "Ibu Pejabat JKR", ["Sheet Name"] = "Sheet S" + sh,
                        ["Drawn By"] = "Author", ["Sheet Issue Date"] = "Author",
                    },
                });
            var list = Run("C4.2.2", m);
            var fails = list.Where(r => r.Status == "fail").ToList();
            Assert.Equal(10, fails.Count);
            // Pane: one issue per sheet x field (no dedupe).
            Assert.Equal(10, IssueMapper.FromModelResults(list, PanelStrings.En).Count);
            var report = SpecReport.Build(list);
            var c = report.For("C4.2.2");
            Assert.Equal(10, c.AllNotes.Count);
            Assert.Equal(3, c.Notes.Count);
            var notes = SpecBorang.Build(report, list).Rows["C4.2"].Notes;
            Assert.Equal(3, notes.Count(n => n.StartsWith("Sheet ")));      // the cap is unchanged: 3 shown ...
            Assert.Contains("+7 more", notes);                              // ... and the 7 left out are named, not silent
        }

        // ---------- the PDF remark (BorangDoc.RemarkFor over the real summary + issues) ----------

        private static string PdfRemark(List<SpecResult> list, string row)
        {
            var summary = SpecBorang.Build(SpecReport.Build(list), list);
            var issues = IssueMapper.FromModelResults(list, PanelStrings.En);
            var (text, _) = BorangDoc.RemarkFor(row, summary.Rows[row].Mark, summary, issues);
            return text;
        }

        [Fact]
        public void PDF_C511_six_unloaded_links_the_remark_names_every_link_through_the_summary()
        {
            var text = PdfRemark(Run("C5.1.1", SixUnloadedLinks()), "C5.1");
            foreach (var link in new[] { "jkrST22_4a", "jkrME22_4a_ACMV", "jkrME22_3b_SP", "jkrME22_3b_FP", "jkrME22_3b_CW", "jkrEL22_3b" })
                Assert.Contains(link, text);
            Assert.StartsWith("CRITICAL: 6 link problem(s)", text);          // the summary is first
            // Summary + the six issues, each now saying which link: 7 items, the cap shows 5 and says how many more.
            Assert.Contains("+2 more", text);
            Assert.Contains("ST link 'jkrST22_4a.rvt' Unloaded", text);
        }

        [Fact]
        public void PDF_C152_four_missing_levels_all_four_are_named()
        {
            var text = PdfRemark(Run("C1.5.2", SixLevelsTwoWith3D()), "C1.5");
            foreach (var lv in new[] { "Tanah", "Pentas", "Langkan", "Aras Bumbung" })
                Assert.Contains(lv, text);
            Assert.StartsWith("3D view for every level: 2/6 ✗ (no Dokumen 3D view: Tanah, Pentas, Langkan, Aras Bumbung)", text);
        }

        [Fact]
        public void PDF_a_check_without_a_summary_is_unchanged_three_issue_texts_no_marker()
        {
            var m = new SpecModel { Discipline = "AR" };
            for (var i = 1; i <= 3; i++) m.Sheets.Add(new SpecSheet { Id = i, Number = "A-" + i, Name = "S" + i, TitleBlock = "" });
            var text = PdfRemark(Run("C4.2.1", m), "C4.2");
            Assert.DoesNotContain("more", text);
        }

        [Fact]
        public void PDF_the_no_issue_fallback_shows_the_summary_exactly_once()
        {
            var list = Run("C1.5.2", SixLevelsTwoWith3D());
            var summary = SpecBorang.Build(SpecReport.Build(list), list);
            var (text, _) = BorangDoc.RemarkFor("C1.5", summary.Rows["C1.5"].Mark, summary, new List<IssueVm>());   // no live issues
            Assert.Contains("no Dokumen 3D view: Tanah, Pentas, Langkan, Aras Bumbung", text);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, "no Dokumen 3D view:").Count);
        }

        // ---------- one issue per element: the texts name their own element ----------

        private static SpecModel SheetsWithAuthor(int n)
        {
            var m = new SpecModel { Discipline = "AR" };
            for (var sh = 1; sh <= n; sh++)
                m.Sheets.Add(new SpecSheet
                {
                    Id = sh * 100, Number = "A-" + sh, Name = "Pelan " + sh, TitleBlock = "jkrAR_TitleBlock_A1 : A1",
                    Params = new Dictionary<string, string>
                    {
                        ["Project Name"] = "Ibu Pejabat JKR", ["Sheet Name"] = "Pelan " + sh, ["Drawn By"] = "Author", ["Sheet Issue Date"] = "16.11.2026",
                    },
                });
            return m;
        }

        [Fact]
        public void AI420_PDF_C422_three_sheets_with_Author_the_remark_names_each_sheet()
        {
            var text = PdfRemark(Run("C4.2.2", SheetsWithAuthor(3)), "C4.2");
            foreach (var sh in new[] { "A-1 Pelan 1", "A-2 Pelan 2", "A-3 Pelan 3" })
                Assert.Contains("Sheet " + sh, text);
            Assert.DoesNotContain("more", text);
        }

        [Fact]
        public void AI420_PDF_C422_more_than_five_sheets_list_five_and_say_how_many_more()
        {
            var text = PdfRemark(Run("C4.2.2", SheetsWithAuthor(8)), "C4.2");
            Assert.Contains("Sheet A-5 Pelan 5", text);
            Assert.DoesNotContain("Sheet A-6 Pelan 6", text);
            Assert.Contains("+3 more", text);
        }

        [Fact]
        public void AI414_xlsx_C511_six_links_six_distinct_Actual_values_each_naming_its_link()
        {
            var issues = IssueMapper.FromModelResults(Run("C5.1.1", SixUnloadedLinks()), PanelStrings.En);
            var actuals = issues.Select(i => IssueExportRows.Cells(i)[6]).ToList();
            Assert.Equal(6, actuals.Distinct().Count());
            Assert.Contains(actuals, a => a == "link EL 'jkrEL22_3b.rvt' tidak dimuatkan (Unloaded)" || a == "EL link 'jkrEL22_3b.rvt' not loaded (Unloaded)");
            Assert.All(actuals, a => Assert.DoesNotMatch(@"^['‘’\s]", a));       // Excel strips a leading apostrophe
            var none = IssueMapper.FromModelResults(Run("C5.1.1", new SpecModel { Discipline = "AR" }), PanelStrings.En)
                .Select(i => IssueExportRows.Cells(i)[6]).ToList();
            Assert.Equal(3, none.Distinct().Count());                         // ST / ME / EL: no link of each
        }

        [Fact]
        public void PDF_a_repeated_title_with_parentheses_keeps_the_state_suffix_last()
        {
            IssueVm One(string id, string own) => new IssueVm
            {
                Id = id, CheckId = "C4.2.2", Domain = "spec", Status = IssueStatus.Ignored,
                Title = "C4.2.2 · Title block (fields)", TitleEn = "C4.2.2 · Title block (fields)",
                Description = own, DescriptionEn = own,
            };
            var issues = new List<IssueVm> { One("a", "Sheet A-1: Drawn By is Author"), One("b", "Sheet A-2: Drawn By is Author") };
            var summary = new Bim010Summary { SpecBased = true };
            summary.Rows["C4.2"] = new Bim010RowMark { Key = "C4.2", Mark = "n", FailIssueIds = { "a", "b" } };
            var (text, _) = BorangDoc.RemarkFor("C4.2", "n", summary, issues);
            Assert.Contains("C4.2.2 · Title block (fields): Sheet A-1: Drawn By is Author (ignored by user)", text);
            Assert.Contains("C4.2.2 · Title block (fields): Sheet A-2: Drawn By is Author (ignored by user)", text);
        }

        [Fact]
        public void AI414_C511_Actual_never_starts_with_a_quote_in_BM_or_EN_and_names_discipline_and_link()
        {
            foreach (var r in Run("C5.1.1", SixUnloadedLinks()))
            {
                Assert.DoesNotMatch(@"^['‘’]", r.Actual);
                Assert.DoesNotMatch(@"^['‘’]", r.ActualEn);
                Assert.Matches(@"^link (ST|ME|EL) '", r.Actual);
                Assert.Matches(@"^(ST|ME|EL) link '", r.ActualEn);
                Assert.Contains(".rvt' ", r.Actual);
            }
            var st = Run("C5.1.1", SixUnloadedLinks()).First();
            Assert.Equal("link ST 'jkrST22_4a.rvt' tidak dimuatkan (Unloaded)", st.Actual);
            Assert.Equal("ST link 'jkrST22_4a.rvt' not loaded (Unloaded)", st.ActualEn);
        }
    }
}
