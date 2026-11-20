using System.Linq;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>The Fix all result window's model: counts, grouping by reason,
    /// "show more", element labels, copy text and BM/EN.</summary>
    [Xunit.Collection("PaneLanguage")]
    public class FixResultModelTests
    {
        private const string Ms = PanelStrings.Ms;
        private const string En = PanelStrings.En;

        private static void Param(RenameResult r, long id, string param, string why) =>
            r.RecordFailure(RenameResult.MakeFixKey("set_parameter", id, param), id, PaneText.ParamWhat(param, id, En), why);

        /// <summary>The real run: 15 on one element without JKR parameters, 4 in model
        /// groups, 2 read-only, 1 unsupported type.</summary>
        private static RenameResult RealRun()
        {
            var r = new RenameResult();
            for (var i = 0; i < 15; i++)
                Param(r, 578862, "P" + i + "_jkr_stt", $"Parameter 'P{i}_jkr_stt' not found on element 578862. Element category does not accept bound parameters.");
            foreach (var id in new long[] { 901102, 901105, 930585, 941247 })
                Param(r, id, "LOD_jkr_sit", $"Element {id} is inside a model group — Revit blocks instance parameter edits outside group edit mode. Edit the group (or ungroup) and re-run Fix All.");
            Param(r, 1201804, "Kod_Jenis_jkr_stt", "Parameter 'Kod_Jenis_jkr_stt' is read-only");
            Param(r, 602392, "LOD_jkr_sit", "Parameter 'LOD_jkr_sit' is read-only");
            Param(r, 921291, "Bahan_jkr_stm", "Unsupported storage type for 'Bahan_jkr_stm'");
            return r;
        }

        [Fact]
        public void Failures_are_grouped_by_reason_largest_first_with_counts()
        {
            var m = new FixResultModel(RealRun(), 466, fixAll: true, lang: Ms);

            Assert.Equal(466, m.Applied);
            Assert.Equal(22, m.Failed);
            Assert.True(m.AnyWritten);
            Assert.Equal(new[] { 15, 4, 2, 1 }, m.Groups.Select(g => g.Count));
            Assert.Equal(FixResultModel.ReasonKind.Category, m.Groups[0].Kind);
            Assert.Equal(FixResultModel.ReasonKind.ModelGroup, m.Groups[1].Kind);
            Assert.Equal(FixResultModel.ReasonKind.ReadOnly, m.Groups[2].Kind);
            Assert.Equal(FixResultModel.ReasonKind.ValueType, m.Groups[3].Kind);
            Assert.Equal("466 perubahan ditulis, 22 tidak ditulis", m.Title);
        }

        [Fact]
        public void The_headline_counts_written_and_not_written_from_the_batch()
        {
            var m = new FixResultModel(RealRun(), 466, true, lang: En);
            Assert.Equal("466 changes written, 22 not written", m.Title);
            Assert.Equal("The written changes are one undo step. Ctrl+Z in Revit undoes them together.", m.UndoLine);
            Assert.Equal("The 22 changes not written stay in Open.", m.StayOpenLine);
            Assert.True(m.HasStayOpen);
        }

        [Fact]
        public void The_headline_reads_singular_for_one()
        {
            var r = new RenameResult();
            Param(r, 1, "A", "Parameter 'A' is read-only");
            var m = new FixResultModel(r, 1, true, lang: En);
            Assert.Equal("1 change written, 1 not written", m.Title);
            Assert.Equal("The 1 change not written stays in Open.", m.StayOpenLine);
        }

        [Fact]
        public void All_written_says_only_what_was_written()
        {
            var m = new FixResultModel(new RenameResult { ParamFixed = 5 }, 5, true, lang: En);
            Assert.Equal(0, m.Failed);
            Assert.Equal("5 changes written", m.Title);
            Assert.False(m.HasStayOpen);
            Assert.Empty(m.Groups);
            Assert.Equal("5 perubahan ditulis", new FixResultModel(new RenameResult { ParamFixed = 5 }, 5, true, lang: Ms).Title);
        }

        [Fact]
        public void Bm_lines()
        {
            var m = new FixResultModel(RealRun(), 466, true, lang: Ms);
            Assert.Equal("Perubahan yang ditulis ialah satu langkah undo. Ctrl+Z dalam Revit membatalkan kesemuanya sekali gus.", m.UndoLine);
            Assert.Equal("22 perubahan yang tidak ditulis kekal dalam Terbuka.", m.StayOpenLine);
            Assert.Equal("Salin senarai untuk pengurus BIM", m.CopyLabel);
            Assert.Equal("Selesai", m.DoneLabel);
            Assert.Equal("Cari", m.LocateLabel);
        }

        [Fact]
        public void Footer_in_english()
        {
            var m = new FixResultModel(RealRun(), 466, true, lang: En);
            Assert.Equal("Copy list for BIM manager", m.CopyLabel);
            Assert.Equal("Copied", m.CopiedLabel);
            Assert.Equal("Done", m.DoneLabel);
            Assert.Equal("Locate", m.LocateLabel);
        }

        [Fact]
        public void A_long_group_shows_five_rows_until_show_more()
        {
            var g = new FixResultModel(RealRun(), 466, true, lang: En).Groups[0];
            Assert.Equal(5, g.Visible.Count);
            Assert.True(g.HasMore);
            Assert.Equal("Show 10 more", g.MoreText);
            g.ShowingAll = true;
            Assert.Equal(15, g.Visible.Count);
            Assert.False(g.HasMore);
        }

        [Fact]
        public void Rows_name_the_parameter_and_the_element_with_its_label()
        {
            var m = new FixResultModel(RealRun(), 466, true, id => id == 578862 ? "Generic Models" : null, Ms);
            var first = m.Groups[0].Items[0];
            Assert.Equal("P0_jkr_stt", first.Param);
            Assert.Equal("[578862] Generic Models", first.Where);
            Assert.Equal(578862, first.ElementId);
            Assert.True(first.CanLocate);
            Assert.False(first.HasReason);
            Assert.Equal("[1201804]", m.Groups[2].Items[0].Where);
        }

        [Fact]
        public void A_failed_rename_shows_the_new_name()
        {
            var r = new RenameResult();
            r.RecordFailure("r:311", 311, PaneText.RenameWhat(311, "L01 +0.000", En),
                            "The name entered is already in use. Enter a unique name.", newName: "L01 +0.000");
            var m = new FixResultModel(r, 0, true, lang: En);
            Assert.Equal(FixResultModel.ReasonKind.NameInUse, m.Groups[0].Kind);
            Assert.Equal("→ L01 +0.000", m.Groups[0].Items[0].Param);
            Assert.Equal("Name already in use", m.Groups[0].Title);
        }

        [Fact]
        public void When_nothing_was_written_the_window_says_so()
        {
            var r = new RenameResult();
            Param(r, 1, "A", "Parameter 'A' is read-only");
            var m = new FixResultModel(r, 0, true, lang: En);
            Assert.True(m.NothingWritten);
            Assert.False(m.AnyWritten);
            Assert.Equal("1 change not written", m.Title);
            Assert.Equal("The model is unchanged.", m.UndoLine);
            var ms = new FixResultModel(r, 0, true, lang: Ms);
            Assert.Equal("1 perubahan tidak ditulis", ms.Title);
            Assert.Equal("Model tidak berubah.", ms.UndoLine);
        }

        [Fact]
        public void An_unknown_reason_goes_to_the_Revit_group_with_the_reason_on_the_row()
        {
            var r = new RenameResult();
            Param(r, 7, "Area", "Revit did not accept this change: Can't set Area.");
            var m = new FixResultModel(r, 3, false, lang: Ms);
            Assert.Equal(FixResultModel.ReasonKind.Other, m.Groups[0].Kind);
            Assert.Equal("Revit tidak menerima perubahan ini: Can't set Area.", m.Groups[0].Items[0].Reason);
        }

        [Fact]
        public void Copy_lists_every_failure_without_a_limit()
        {
            var text = new FixResultModel(RealRun(), 466, true, lang: En).CopyText();
            Assert.StartsWith("466 changes applied as one undo step. 22 fixes failed and were not written:", text);
            Assert.DoesNotContain("more", text);
            Assert.Contains("Bahan_jkr_stm on 921291", text);
        }

        [Fact]
        public void A_batch_retried_in_parts_keeps_every_failure_record()
        {
            var all = new RenameResult();
            var part = new RenameResult { ParamFixed = 2 };
            Param(part, 5, "A", "Parameter 'A' is read-only");
            all.Absorb(part);
            Param(all, 6, "B", "Parameter 'B' is read-only");
            Assert.Equal(new long[] { 5, 6 }, all.Failures.Select(f => f.ElementId));
            Assert.Equal("A", all.Failures[0].Parameter);
        }
    }
}
