using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-516 round 3. Shapes from the real LOD 300 dedupe dump, names replaced: a legacy "Missing Type parameter: X" row and a legacy
    /// "JKR code assigned" row (no spec id, extra) on the same element with the same write (29 pairs in the dump).
    /// </summary>
    public class ScanBoardSameWriteTests
    {
        private const string Param = "Kod_DAK_Komponen_jkr_stt";

        private static SpecDedupe.Row ParamRow(string id, long el, int tier = 1) => new SpecDedupe.Row
        {
            Id = id, CheckId = "Db.1", Source = SpecDedupe.FromBackend, Tier = tier, Fixable = true, Bucket = ScanBoard.Open,
            Param = Param, ParamRule = true, ParamKind = "type", ElementId = el, Category = "Ceilings", TypeName = "T-" + el,
            FixKey = "set_parameter|" + el + "|" + Param, FixValue = "A0401",
        };

        private static SpecDedupe.Row CodeRow(string id, long el, int tier = 1, string fixParam = Param) => new SpecDedupe.Row
        {
            Id = id, CheckId = "", Source = SpecDedupe.FromBackend, Tier = tier, Fixable = true, Bucket = ScanBoard.Open, Extra = true,
            Param = "A0401", ParamRule = false, ElementId = el, Category = "Ceilings", TypeName = "T-" + el,
            FixKey = "set_parameter|" + el + "|" + fixParam, FixValue = "A0401",
        };

        [Fact]
        public void Same_element_same_write_shows_once_and_fix_all_counts_one()
        {
            var kept = ScanBoard.Merge(new[] { ParamRow("p1", 10), CodeRow("c1", 10) }, null, null);
            Assert.Equal(new[] { "p1" }, kept.Select(r => r.Id).ToArray());
            var t = ScanBoard.Count(kept);
            Assert.Equal(1, t.Open); Assert.Equal(1, t.Fixable); Assert.Equal(0, t.Extra);
        }

        [Fact]
        public void Input_order_does_not_change_the_result()
        {
            var kept = ScanBoard.Merge(new[] { CodeRow("c1", 10), ParamRow("p1", 10) }, null, null);
            Assert.Equal(new[] { "p1" }, kept.Select(r => r.Id).ToArray());
        }

        [Fact]
        public void The_kept_row_takes_the_more_severe_tier()
        {
            var kept = ScanBoard.Merge(new[] { ParamRow("p1", 10, tier: 2), CodeRow("c1", 10, tier: 0) }, null, null);
            Assert.Equal(0, Assert.Single(kept).Tier);
        }

        [Fact]
        public void Another_element_or_another_parameter_or_a_different_write_stays()
        {
            var rows = new[] { ParamRow("p1", 10), CodeRow("c-other-el", 11), CodeRow("c-other-param", 10, fixParam: "Sistem_jkr_stt") };
            Assert.Equal(3, ScanBoard.Merge(rows, null, null).Count);
        }

        [Fact]
        public void A_row_with_a_spec_id_or_not_fixable_is_never_dropped()
        {
            var withSpec = CodeRow("c-spec", 10); withSpec.CheckId = "A6.2"; withSpec.Extra = false;
            var notFixable = CodeRow("c-nofix", 10); notFixable.Fixable = false;
            Assert.Equal(3, ScanBoard.Merge(new[] { ParamRow("p1", 10), withSpec, notFixable }, null, null).Count);
        }

        [Fact]
        public void Two_extra_rows_with_one_write_and_no_parameter_row_both_stay()
        {
            Assert.Equal(2, ScanBoard.Merge(new[] { CodeRow("c1", 10), CodeRow("c2", 10) }, null, null).Count);
        }

        [Fact]
        public void Same_write_with_a_different_value_stays()
        {
            var c = CodeRow("c1", 10); c.FixValue = "A0402";
            Assert.Equal(2, ScanBoard.Merge(new[] { ParamRow("p1", 10), c }, null, null).Count);
        }

        [Fact]
        public void A_missing_value_is_no_evidence_and_both_stay()
        {
            var c = CodeRow("c1", 10); c.FixValue = "";
            Assert.Equal(2, ScanBoard.Merge(new[] { ParamRow("p1", 10), c }, null, null).Count);
        }

        [Fact]
        public void A_kept_row_that_is_itself_extra_does_not_absorb_another()
        {
            var p = ParamRow("p1", 10); p.Extra = true;
            Assert.Equal(2, ScanBoard.Merge(new[] { p, CodeRow("c1", 10) }, null, null).Count);
        }
    }
}
