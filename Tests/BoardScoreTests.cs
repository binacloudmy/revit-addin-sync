using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-479: one problem is one row with one tier (SpecDedupe), and a Fix all says what
    /// moved on the board and on the score (FixImpact). WPF-free, so these run on the Mac harness.
    /// </summary>
    public class BoardScoreTests
    {
        private static SpecDedupe.Row R(string check, int source, int tier = 1, bool fixable = false) =>
            new SpecDedupe.Row { CheckId = check, Source = source, Tier = tier, Fixable = fixable };

        // ── why the three suspected duplicates really collide: every source maps to the same check id ──

        [Fact]
        public void Backend_audit_and_model_rows_of_A2_1_A4_1_B1_1_all_name_the_same_check()
        {
            Assert.Equal("A2.1", SpecReport.MapBackend("Project Naming", "File naming convention", 0)?.Id);
            Assert.Equal("A2.1", SpecReport.MapAudit("file_naming", "A2", "File name", false)?.Id);
            Assert.Equal("A4.1", SpecReport.MapBackend("Project Base Point", "Project Base Point surveyed", 0)?.Id);
            Assert.Equal("A4.1", SpecReport.MapAudit("base_point", "A4", "Base point", false)?.Id);
            Assert.Equal("B1.1", SpecReport.MapBackend("Project Information", "Project Address populated", 0)?.Id);
            Assert.Equal("B1.1", SpecReport.MapAudit("project_info", "B1", "Project information", false)?.Id);
            foreach (var id in new[] { "A2.1", "A4.1", "B1.1" }) Assert.True(SpecDedupe.IsOneProblem(id));
            Assert.False(SpecDedupe.IsOneProblem("Da.1"));
            Assert.False(SpecDedupe.IsOneProblem(""));
        }

        // ── de-dupe ──

        [Fact]
        public void Three_sources_for_one_check_keep_the_spec_evaluator_row_only()
        {
            var o = SpecDedupe.Resolve(new[] { R("A4.1", SpecDedupe.FromBackend), R("A4.1", SpecDedupe.FromAudit), R("A4.1", SpecDedupe.FromModel) });
            Assert.Equal(new[] { false, false, true }, o.Keep);
            Assert.Equal(2, o.Dropped);
        }

        [Fact]
        public void Without_a_spec_row_the_backend_row_beats_the_checker_row()
        {
            var o = SpecDedupe.Resolve(new[] { R("A2.1", SpecDedupe.FromAudit), R("A2.1", SpecDedupe.FromBackend) });
            Assert.Equal(new[] { false, true }, o.Keep);
        }

        [Fact]
        public void One_source_alone_is_untouched_whatever_its_row_count()
        {
            var o = SpecDedupe.Resolve(new[] { R("B1.1", SpecDedupe.FromModel), R("B1.1", SpecDedupe.FromModel), R("B1.1", SpecDedupe.FromModel) });
            Assert.All(o.Keep, k => Assert.True(k));
            Assert.Equal(0, o.Dropped);
        }

        [Fact]
        public void All_kept_rows_of_a_check_take_the_most_severe_tier_even_from_a_dropped_duplicate()
        {
            // legacy Project Naming is High (Must Fix); the spec row alone would be Medium (ignorable)
            var o = SpecDedupe.Resolve(new[] { R("A2.1", SpecDedupe.FromBackend, tier: 0), R("A2.1", SpecDedupe.FromModel, tier: 1) });
            Assert.Equal(new[] { false, true }, o.Keep);
            Assert.Equal(0, o.Tier[1]);
        }

        [Fact]
        public void A_fixable_row_is_never_dropped()
        {
            var o = SpecDedupe.Resolve(new[] { R("A2.1", SpecDedupe.FromBackend, fixable: true), R("A2.1", SpecDedupe.FromModel) });
            Assert.Equal(new[] { true, true }, o.Keep);
        }

        [Fact]
        public void Other_checks_and_rows_with_no_spec_id_pass_through_with_their_own_tier()
        {
            var o = SpecDedupe.Resolve(new[]
            {
                R("Da.1", SpecDedupe.FromBackend, tier: 2), R("Da.1", SpecDedupe.FromModel, tier: 0),
                R("", SpecDedupe.FromBackend, tier: 0), R("", SpecDedupe.FromModel, tier: 2),
            });
            Assert.All(o.Keep, k => Assert.True(k));
            Assert.Equal(new[] { 2, 0, 0, 2 }, o.Tier);
        }

        [Fact]
        public void Checks_are_grouped_apart_and_null_input_is_safe()
        {
            var o = SpecDedupe.Resolve(new[]
            {
                R("A2.1", SpecDedupe.FromBackend, tier: 0), R("A2.1", SpecDedupe.FromModel, tier: 1),
                R("A4.1", SpecDedupe.FromBackend, tier: 1), R("A4.1", SpecDedupe.FromModel, tier: 1),
            });
            Assert.Equal(new[] { false, true, false, true }, o.Keep);
            Assert.Equal(new[] { 0, 0, 1, 1 }, o.Tier);
            Assert.Empty(SpecDedupe.Resolve(null).Keep);
        }

        // ── extra families: no spec id from the backend mapper ──

        [Theory]
        [InlineData("Grids", "Grid naming convention", 0)]
        [InlineData("Levels", "Level naming convention", 12)]
        [InlineData("Component Parameter", "JKR code assigned", 7)]
        public void Families_with_no_spec_id_map_to_no_check(string category, string rule, long element)
        {
            Assert.Null(SpecReport.MapBackend(category, rule, element));
        }

        [Fact]
        public void A_missing_parameter_rule_on_an_element_maps_to_Db_1_so_it_is_in_the_percent()
        {
            // The autofix map lists Kod_Jenis as "no spec id"; the code maps it to Db.1.
            Assert.Equal("Db.1", SpecReport.MapBackend("Component Parameter", "Missing type parameter: Kod_Jenis_jkr_stt", 42)?.Id);
        }

        // ── fix impact ──

        private static SpecReport Report(bool a41Pass)
        {
            var results = new List<SpecResult>
            {
                new SpecResult { Source = SpecReport.Model, CheckId = "A4.1", Status = a41Pass ? "pass" : "fail", IssueId = "x" },
            };
            return SpecReport.Build(results);
        }

        [Fact]
        public void Take_reads_the_open_rows_and_the_score()
        {
            var s = FixImpact.Take(10, 3, Report(true));
            Assert.Equal(10, s.Open);
            Assert.Equal(3, s.OpenExtra);
            Assert.True(s.Total > 0);
            Assert.NotNull(s.Pct);
            Assert.Equal(SpecChecklist.Score(Report(true)).Passed, s.Passed);
        }

        [Fact]
        public void Take_without_a_report_has_no_score_and_clamps_extra_to_open()
        {
            var s = FixImpact.Take(2, 5, null);
            Assert.Equal(2, s.OpenExtra);
            Assert.Equal(0, s.Total);
            Assert.Null(s.Pct);
        }

        [Fact]
        public void Moved_counts_board_extra_and_score_separately()
        {
            var before = new FixImpact.Snapshot { Open = 12, OpenExtra = 4, Passed = 30, Total = 61, Pct = 49 };
            var after = new FixImpact.Snapshot { Open = 5, OpenExtra = 0, Passed = 32, Total = 61, Pct = 52 };
            Assert.Equal(7, FixImpact.BoardMoved(before, after));
            Assert.Equal(4, FixImpact.ExtraMoved(before, after));
            Assert.Equal(2, FixImpact.ScoreMoved(before, after));
        }

        [Fact]
        public void A_rescan_that_finds_new_rows_never_reports_a_negative_board_move()
        {
            var before = new FixImpact.Snapshot { Open = 3, OpenExtra = 1 };
            var after = new FixImpact.Snapshot { Open = 9, OpenExtra = 4 };
            Assert.Equal(0, FixImpact.BoardMoved(before, after));
            Assert.Equal(0, FixImpact.ExtraMoved(before, after));
        }

        [Fact]
        public void Text_says_what_moved_on_the_board_and_on_the_score_in_english()
        {
            var before = new FixImpact.Snapshot { Open = 12, OpenExtra = 4, Passed = 30, Total = 61, Pct = 49 };
            var after = new FixImpact.Snapshot { Open = 5, OpenExtra = 0, Passed = 32, Total = 61, Pct = 52 };
            Assert.Equal("Board 12 → 5 open (7 fixed, 4 of them extra, outside the %) · Score 49% → 52% (+2 checks)",
                FixImpact.Text(before, after, PanelStrings.En));
        }

        [Fact]
        public void Text_in_bahasa_melayu()
        {
            var before = new FixImpact.Snapshot { Open = 12, OpenExtra = 4, Passed = 30, Total = 61, Pct = 49 };
            var after = new FixImpact.Snapshot { Open = 5, OpenExtra = 0, Passed = 32, Total = 61, Pct = 52 };
            Assert.Equal("Papan 12 → 5 terbuka (7 dibaiki, 4 daripadanya tambahan, di luar %) · Skor 49% → 52% (+2 semakan)",
                FixImpact.Text(before, after, PanelStrings.Ms));
        }

        [Fact]
        public void Only_extra_rows_fixed_says_why_the_percent_did_not_move()
        {
            var before = new FixImpact.Snapshot { Open = 6, OpenExtra = 6, Passed = 30, Total = 61, Pct = 49 };
            var after = new FixImpact.Snapshot { Open = 0, OpenExtra = 0, Passed = 30, Total = 61, Pct = 49 };
            Assert.Equal("Board 6 → 0 open (6 fixed, 6 of them extra, outside the %) · Score 49% (unchanged: extra fixes are not in the %)",
                FixImpact.Text(before, after, PanelStrings.En));
        }

        [Fact]
        public void Spec_fixes_that_do_not_pass_yet_say_so()
        {
            var before = new FixImpact.Snapshot { Open = 4, Passed = 30, Total = 61, Pct = 49 };
            var after = new FixImpact.Snapshot { Open = 2, Passed = 30, Total = 61, Pct = 49 };
            Assert.Contains("unchanged: those checks do not pass yet", FixImpact.Text(before, after, PanelStrings.En));
        }

        [Fact]
        public void Text_without_a_score_is_the_board_part_only_and_nulls_are_empty()
        {
            var before = new FixImpact.Snapshot { Open = 4 };
            var after = new FixImpact.Snapshot { Open = 1 };
            Assert.Equal("Board 4 → 1 open (3 fixed)", FixImpact.Text(before, after, PanelStrings.En));
            Assert.Equal("", FixImpact.Text(null, after));
        }

        [Fact]
        public void The_extra_tag_is_written_in_both_languages()
        {
            Assert.Equal("Extra · outside the %", PaneText.ExtraTag(PanelStrings.En));
            Assert.Equal("Tambahan · di luar %", PaneText.ExtraTag(PanelStrings.Ms));
        }
    }
}
