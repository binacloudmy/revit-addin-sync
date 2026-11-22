using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// The Result screen's "Aspects" view: the same 61 spec checks grouped by what they
    /// test (N naming, P parameters and information, Q quality and integrity, G design
    /// geometry) instead of by Borang section. The mapping is a draft (spec Q1), so these
    /// tests pin its shape — every check in exactly one aspect — and that an aspect row is
    /// computed exactly as a section row is, never the per-aspect counts.
    /// </summary>
    public class SpecAspectsTests
    {
        private static readonly SpecAspect[] All = { SpecAspect.Naming, SpecAspect.Parameters, SpecAspect.Quality, SpecAspect.Geometry };

        // ── the mapping ──

        [Fact]
        public void Every_spec_check_is_in_the_mapping_table_and_nothing_else_is()
        {
            var catalog = SpecCatalog.Checks.Select(c => c.Id).ToList();
            var table = SpecAspects.Table.Keys.ToList();
            Assert.Empty(catalog.Except(table));   // unmapped check: add it to SpecAspects.Table
            Assert.Empty(table.Except(catalog));   // stale id: the catalog no longer has it
            Assert.Equal(catalog.Count, table.Count);
        }

        [Fact]
        public void Every_spec_check_maps_to_exactly_one_aspect()
        {
            foreach (var c in SpecCatalog.Checks)
                Assert.Single(All, a => SpecAspects.Of(c.Id) == a);
            var counts = All.Select(a => SpecCatalog.Checks.Count(c => SpecAspects.Of(c.Id) == a)).ToList();
            Assert.Equal(SpecCatalog.Checks.Count, counts.Sum());
            Assert.All(counts, n => Assert.True(n > 0));
        }

        [Fact]
        public void An_unknown_id_falls_back_to_quality_at_runtime()
        {
            Assert.False(SpecAspects.IsMapped("Z9.9"));
            Assert.Equal(SpecAspect.Quality, SpecAspects.Of("Z9.9"));
            Assert.Equal(SpecAspect.Quality, SpecAspects.Of(null));
        }

        [Fact]
        public void Aspects_have_letters_and_names_in_BM_and_EN()
        {
            Assert.Equal(new[] { "N", "P", "Q", "G" }, All.Select(SpecAspects.Letter));
            Assert.Equal(new[] { "Naming", "Parameters and information", "Quality and integrity", "Design geometry" },
                All.Select(a => SpecAspects.Name(a)));
        }

        // ── rows: computed as section rows are ──

        private static List<SpecCheckResult> In(SpecReport r, Func<SpecCheck, bool> member) =>
            SpecCatalog.Checks.Where(member).Select(c => r.For(c.Id)).ToList();

        /// <summary>The section-row arithmetic, written out (AI-474): % = (AI checks in state Pass +
        /// List and Human checks) / checks that apply (not-applicable left out); one tick per check in
        /// catalog order; fails = checks that fail; manual = checks a person decides.</summary>
        private static void AssertRowOf(GroupRow row, List<SpecCheckResult> checks)
        {
            var applies = checks.Where(c => c.State != SpecState.NotApplicable).ToList();
            int p = applies.Count(c => c.Check.Class != SpecCellClass.Ai || c.State == SpecState.Pass), t = applies.Count;
            int? pct = t == 0 ? (int?)null : (int)Math.Round(100.0 * p / t, MidpointRounding.AwayFromZero);
            Assert.Equal(checks.Count, row.Checks);
            Assert.Equal(pct, row.Pct);
            Assert.Equal(pct.HasValue ? pct + "%" : "—", row.PctLabel);
            Assert.Equal(checks.Select(c => SpecChecklist.TickOf(c.State)), row.Ticks);
            Assert.Equal(checks.Count(c => c.State == SpecState.Fail), row.Fails);
            Assert.Equal(checks.Count(c => c.State == SpecState.NeedsPerson), row.Manual);
        }

        [Theory]
        [InlineData("jkr_run_failing.json")]
        [InlineData("jkr_run_compliant.json")]
        public void Aspect_rows_are_computed_exactly_as_section_rows(string run)
        {
            var r = SpecChecklistTests.Report(run);

            var secs = SpecChecklist.SectionRows(r);
            Assert.Equal(PanelCore.Secs, secs.Select(s => s.Key));
            foreach (var s in secs)
            {
                AssertRowOf(s, In(r, c => c.Section == s.Key));
                Assert.Equal(SpecChecklist.Pct(r, s.Key), s.Pct);   // the tiles' % is the same
            }

            var asp = SpecAspects.Rows(r);
            Assert.Equal(new[] { "N", "P", "Q", "G" }, asp.Select(a => a.Letter));
            foreach (var (row, a) in asp.Zip(All, (row, a) => (row, a)))
            {
                Assert.Equal(SpecAspects.Name(a), row.Name);
                AssertRowOf(row, In(r, c => SpecAspects.Of(c.Id) == a));
            }
            Assert.Equal(SpecCatalog.Checks.Count, asp.Sum(a => a.Checks));
            Assert.Equal(secs.Sum(s => s.Fails), asp.Sum(a => a.Fails));
            Assert.Equal(secs.Sum(s => s.Manual), asp.Sum(a => a.Manual));
        }

        [Fact]
        public void Tick_codes_follow_the_design()
        {
            Assert.Equal("f", SpecChecklist.TickOf(SpecState.Fail));
            Assert.Equal("p", SpecChecklist.TickOf(SpecState.Pass));
            Assert.Equal("h", SpecChecklist.TickOf(SpecState.Partial));
            Assert.Equal("m", SpecChecklist.TickOf(SpecState.NeedsPerson));
            Assert.Equal("l", SpecChecklist.TickOf(SpecState.Listed));
            Assert.Equal("n", SpecChecklist.TickOf(SpecState.NotApplicable));
            Assert.Equal("x", SpecChecklist.TickOf(SpecState.NotChecked));
        }

        [Fact]
        public void A_row_says_n_fail_or_when_none_fail_n_manual()
        {
            var failing = SpecAspects.Rows(SpecChecklistTests.Report("jkr_run_failing.json"));
            foreach (var row in failing)
            {
                if (row.Fails > 0) { Assert.Equal(row.Fails + " fail", row.CountLabel); Assert.Equal("fail", row.CountTone); }
                else if (row.Manual > 0) { Assert.Equal(row.Manual + " manual", row.CountLabel); Assert.Equal("person", row.CountTone); }
                else Assert.Equal("", row.CountLabel);
            }
            Assert.Contains(failing, row => row.Fails > 0);
            var bm = SpecAspects.Rows(SpecChecklistTests.Report("jkr_run_failing.json")).First(x => x.Fails > 0);
        }

        // ── the Result screen toggle ──

        [Fact]
        public void The_result_screen_shows_sections_first_and_toggles_to_aspects()
        {
            var (r, core) = SpecChecklistTests.Results("jkr_run_failing.json");
            Assert.False(r.ShowAspects);
            Assert.Equal(PanelCore.Secs, r.GroupRows.Select(g => g.Letter));
            Assert.Equal("Borang sections", r.SectionsToggleLabel);
            Assert.Equal("Aspects", r.AspectsToggleLabel);

            var raised = new List<string>();
            r.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            r.ShowAspects = true;
            Assert.NotEmpty(raised);
            Assert.Equal(new[] { "N", "P", "Q", "G" }, r.GroupRows.Select(g => g.Letter));
            Assert.Equal(SpecCatalog.Checks.Count, r.GroupRows.Sum(g => g.Checks));

            Assert.Equal("Aspek", r.AspectsToggleLabel);
            Assert.Equal("Penamaan", r.GroupRows[0].Name);
        }

        [Fact]
        public void Toggling_aspects_keeps_the_selection_and_filters()
        {
            var (r, core) = SpecChecklistTests.Results("jkr_run_failing.json");
            r.SelectSection("C");
            r.SetStateFilter("fail");
            core.SetSel(1);
            var checklist = r.Checklist.Select(s => s.Key).ToList();

            r.ShowAspects = true;
            Assert.Equal("C", core.Cat);
            Assert.Equal("fail", r.StateFilter);
            Assert.Equal(1, core.Sel);
            Assert.True(r.ShowChecklist);
            Assert.Equal(checklist, r.Checklist.Select(s => s.Key));

            r.ShowAspects = false;
            Assert.Equal("C", core.Cat);
            Assert.Equal("fail", r.StateFilter);
            Assert.Equal(1, core.Sel);
            Assert.Equal(PanelCore.Secs, r.GroupRows.Select(g => g.Letter));
        }

        [Fact]
        public void Without_a_spec_report_there_are_no_group_rows()
        {
            var core = new PanelCore(new List<PanelItem>(), null, new ManualClock());
            var r = new ResultsScreenModel(core, new StartScreenModel(core));
            Assert.Empty(r.GroupRows);
            r.ShowAspects = true;
            Assert.Empty(r.GroupRows);
        }
    }
}
