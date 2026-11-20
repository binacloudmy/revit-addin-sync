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
            Assert.DoesNotContain("missing:", text);
        }
    }
}
