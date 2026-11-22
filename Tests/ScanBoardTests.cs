using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-506: same input, same board. Pure merge / de-dupe / tally over rows (no WPF, no Revit).</summary>
    public class ScanBoardTests
    {
        private const string Param = "Kod_Komponen_jkr_stt";

        // A fixed fixture: two types, 3 doors of type 1 and 2 of type 2, one spec Db.1 row per type, and the
        // backend's "Missing Type Parameter" row per element and parameter, plus level rows and a manual row.
        private static List<SpecDedupe.Row> Backend()
        {
            var rows = new List<SpecDedupe.Row>();
            foreach (var e in new long[] { 101, 102, 103, 201, 202 })
                foreach (var p in new[] { Param, "Sistem_jkr_stt" })
                    rows.Add(new SpecDedupe.Row
                    {
                        Id = "L-" + e + p, CheckId = "Db.1", Source = SpecDedupe.FromBackend, Tier = 1, Param = p, ElementId = e,
                        Bucket = ScanBoard.Open, Extra = false,
                    });
            for (var k = 0; k < 6; k++)   // six level renames: fixable, no spec id = extra
                rows.Add(new SpecDedupe.Row { Id = "lvl" + k, CheckId = "", Source = SpecDedupe.FromBackend, Tier = 0, Fixable = true, Bucket = ScanBoard.Open, Extra = true, FixKey = "r:" + k });
            return rows;
        }

        private static List<SpecDedupe.Row> Audit() => new List<SpecDedupe.Row>
        {
            new SpecDedupe.Row { Id = "b1", CheckId = "A4.1", Source = SpecDedupe.FromAudit, Bucket = ScanBoard.Open },
        };

        private static List<SpecDedupe.Row> Model() => new List<SpecDedupe.Row>
        {
            new SpecDedupe.Row { Id = "s-type1", CheckId = "Db.1", Source = SpecDedupe.FromModel, Bucket = ScanBoard.Open,
                Params = SpecDedupe.ParamsOfActual("tiada: " + Param + ", Sistem_jkr_stt"), ElementIds = new long[] { 101, 102, 103 } },
            new SpecDedupe.Row { Id = "s-type2", CheckId = "Db.1", Source = SpecDedupe.FromModel, Bucket = ScanBoard.Open,
                Params = SpecDedupe.ParamsOfActual("kosong: " + Param + "; tiada: Sistem_jkr_stt"), ElementIds = new long[] { 201, 202 } },
            new SpecDedupe.Row { Id = "s-a41", CheckId = "A4.1", Source = SpecDedupe.FromModel, Bucket = ScanBoard.Open },
            new SpecDedupe.Row { Id = "s-human", CheckId = "C1.6.3", Source = SpecDedupe.FromModel, Bucket = ScanBoard.Manual },
        };

        private static string Sig(ScanBoard.Tally t) => $"{t.Open}/{t.Manual}/{t.Ignored}/{t.Resolved}/{t.Extra}/{t.Fixable}";

        [Fact]
        public void Three_scans_of_the_same_input_give_identical_rows_and_numbers()
        {
            var sigs = new List<string>(); var ids = new List<string>();
            for (var run = 0; run < 3; run++)
            {
                var kept = ScanBoard.Merge(Backend(), Audit(), Model());
                sigs.Add(Sig(ScanBoard.Count(kept)));
                ids.Add(string.Join(",", kept.Select(r => r.Id + ":" + r.Tier)));
            }
            Assert.Single(sigs.Distinct());
            Assert.Single(ids.Distinct());
        }

        [Fact]
        public void The_order_inside_a_source_does_not_change_the_numbers()
        {
            var a = Sig(ScanBoard.Count(ScanBoard.Merge(Backend(), Audit(), Model())));
            var b = Sig(ScanBoard.Count(ScanBoard.Merge(Enumerable.Reverse(Backend()), Audit(), Enumerable.Reverse(Model()))));
            Assert.Equal(a, b);
        }

        [Fact]
        public void A_db1_spec_row_and_the_backend_rows_for_the_same_parameter_show_once()
        {
            var kept = ScanBoard.Merge(Backend(), Audit(), Model());
            // 10 backend parameter rows (5 elements x 2 parameters) are all covered by the two spec rows: none stay.
            Assert.DoesNotContain(kept, r => r.Id.StartsWith("L-"));
            Assert.Equal(2, kept.Count(r => r.CheckId == "Db.1"));
        }

        [Fact]
        public void A_backend_row_no_spec_row_covers_stays_and_so_does_a_fixable_one()
        {
            var backend = Backend();
            backend.Add(new SpecDedupe.Row { Id = "L-other-param", CheckId = "Db.1", Source = SpecDedupe.FromBackend, Param = "Coping_jkr_stt", ElementId = 101, Bucket = ScanBoard.Open });   // another parameter
            backend.Add(new SpecDedupe.Row { Id = "L-other-el", CheckId = "Db.1", Source = SpecDedupe.FromBackend, Param = Param, ElementId = 999, Bucket = ScanBoard.Open });             // another element
            backend.Add(new SpecDedupe.Row { Id = "L-fixable", CheckId = "Db.1", Source = SpecDedupe.FromBackend, Param = Param, ElementId = 101, Fixable = true, Bucket = ScanBoard.Open }); // AI-479: a fixable row stays
            var ids = ScanBoard.Merge(backend, Audit(), Model()).Select(r => r.Id).ToList();
            Assert.Contains("L-other-param", ids);
            Assert.Contains("L-other-el", ids);
            Assert.Contains("L-fixable", ids);
        }

        [Fact]
        public void Parameter_names_match_across_spellings_and_the_rule_text_names_the_parameter()
        {
            Assert.Equal(SpecDedupe.ParamKey("Kod_Komponen_jkr_stt"), SpecDedupe.ParamKey("Kod Komponen Jkr Stt"));
            Assert.Equal("Kod_Komponen_jkr_stt", SpecDedupe.ParamOfRule("missing type parameter: Kod_Komponen_jkr_stt", ""));
            Assert.Equal("LOD_jkr_sit", SpecDedupe.ParamOfRule("Something else", "LOD_jkr_sit"));
            Assert.Equal(new[] { "a_b", "c" }, SpecDedupe.ParamsOfActual("kosong: A_b; tiada: C").ToArray());
        }

        [Fact]
        public void Board_fix_all_and_the_export_reconcile_and_the_extra_group_is_labelled()
        {
            var t = ScanBoard.Count(ScanBoard.Merge(Backend(), Audit(), Model()));
            Assert.Equal(t.Open + t.Manual + t.Ignored + t.Resolved, t.Total);
            Assert.Equal(1, t.Manual);
            Assert.Equal(6, t.Extra);       // the level rows: part of Open, outside the %
            Assert.Equal(6, t.Fixable);     // Fix all counts them, and nothing else
            Assert.True(t.Fixable <= t.Open && t.Extra <= t.Open);
            Assert.Contains("= " + t.Total + " in the file", ScanBoard.Line(t));
            Assert.Contains("6 open are extra, outside the %", ScanBoard.Line(t));
            Assert.Contains("manual review", ScanBoard.Line(t));
        }

        [Fact]
        public void Without_the_dedupe_the_same_problem_counted_twice_would_show_more_rows()
        {
            // The AI-506 report: 997 in the file against 943 on the board came from counting each problem per source.
            var all = Backend().Concat(Audit()).Concat(Model()).Count();
            var kept = ScanBoard.Merge(Backend(), Audit(), Model()).Count;
            Assert.True(kept < all);
        }

        [Fact]
        public void A_run_with_a_missing_source_is_partial_and_counts_the_missing_ones()
        {
            var full = new SpecReport();
            Assert.False(full.IsPartial);
            Assert.Equal(0, full.MissingCount);

            var backendDown = new SpecReport { BackendError = "timeout" };
            Assert.True(backendDown.IsPartial);
            Assert.Equal(1, backendDown.MissingCount);   // counted once, even if MissingSources also names it

            var two = new SpecReport { BackendError = "timeout", MissingSources = new List<string> { "backend", "BIM010" } };
            Assert.Equal(2, two.MissingCount);

            var auditOnly = new SpecReport { MissingSources = new List<string> { "BIM010" } };
            Assert.True(auditOnly.IsPartial);
        }
    }
}
