#nullable enable
// Round 2 of the gaps: the undone banner's words, the changes as a table (Copy table / Open as grid), "Leave as is".
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class Round2Tests
    {
        private static ProgressStep S(string id, StepState state, string phase = "tool") => new ProgressStep { StepId = id, Phase = phase, State = state };

        private static ResultCardModel TwoTypes()
        {
            var m = new ResultCardModel { Kind = ResultKind.Changes };
            foreach (var (name, n) in new[] { ("Downlight 1x13W", 38), ("Downlight 1x8W", 14) })
            {
                var g = new TypeGroup { TypeName = name, IsType = true, Instances = n };
                foreach (var p in new[] { "Model", "Manufacturer", "URL", "Description", "Type Comments" })
                    g.Rows.Add(new ChangeRow { Param = p, Before = "", After = p + "-v", Source = "brochure.pdf, page 8", Ids = new List<long> { 1 } });
                m.Groups.Add(g);
            }
            return m;
        }

        // ---- R1 ----

        [Fact]
        public void TheUndoneBanner_SaysTheModelIsBack_AndWhatWasRemoved()
        {
            var (title, detail) = UndoBanner.For(TwoTypes(), ReceiptLanguage.En);

            Assert.Equal("Undone. The model is back to how it was.", title);
            Assert.Equal("10 values put back to what they were on 2 types. Nothing else changed.", detail);
        }

        [Fact]
        public void TheUndoneBanner_ReadsInMalay_AndCountsInstanceWritesInElements()
        {
            var ms = UndoBanner.For(TwoTypes(), ReceiptLanguage.Ms);
            Assert.Equal("Dibuat asal. Model kembali seperti asal.", ms.Title);
            Assert.Contains("10 nilai dikembalikan seperti asal pada 2 jenis", ms.Detail);

            var inst = new ResultCardModel { Kind = ResultKind.Changes };
            var g = new TypeGroup { TypeName = "Doors", Instances = 3, Ids = new List<long> { 1, 2, 3 } };
            g.Rows.Add(new ChangeRow { Param = "Mark", Ids = new List<long> { 1, 2, 3 } });
            inst.Groups.Add(g);
            Assert.Equal("1 value put back to what it was on 3 elements. Nothing else changed.", UndoBanner.For(inst, ReceiptLanguage.En).Detail);
        }

        [Fact]
        public void TheUndoneBanner_ForAPutBackOnlyCardOrNoCard_ClaimsNothing()
        {
            // r13: the card lists nothing the Undo put back, so it does not say "Nothing else changed."
            Assert.Equal("", UndoBanner.For(null, ReceiptLanguage.En).Detail);
            Assert.Equal("", UndoBanner.For(new ResultCardModel { Kind = ResultKind.Selection }, ReceiptLanguage.En).Detail);
        }

        [Fact]
        public void TheUndoneBanner_DropsNothingElseChanged_WhenTheUndoAlsoBroughtDeletedElementsBack()
        {
            var changes = TwoTypes();
            changes.Deleted = 3;
            Assert.Equal("10 values put back to what they were on 2 types.", UndoBanner.For(changes, ReceiptLanguage.En).Detail);
            Assert.Equal("10 nilai dikembalikan seperti asal pada 2 jenis.", UndoBanner.For(changes, ReceiptLanguage.Ms).Detail);
            var made = new ResultCardModel { Kind = ResultKind.Created, Deleted = 1 };
            made.Created.Add(new CreatedItem { Id = 1 });
            Assert.Equal("1 removed.", UndoBanner.For(made, ReceiptLanguage.En).Detail);
            made.Deleted = 0;
            Assert.Equal("1 dibuang. Tiada yang lain berubah.", UndoBanner.For(made, ReceiptLanguage.Ms).Detail);
        }

        [Fact]
        public void TheWarningsNote_SaysApplied_NotSaved_InBothLanguages()
        {
            Assert.Equal("The change is applied in Revit. Check what Revit flagged below in the model.", ResultCardModel.CommitWarningsNote(ReceiptLanguage.En));
            Assert.StartsWith("Perubahan telah dibuat dalam Revit.", ResultCardModel.CommitWarningsNote(ReceiptLanguage.Ms));
            Assert.DoesNotContain("saved", ResultCardModel.CommitWarningsNote(ReceiptLanguage.En));
        }

        [Fact]
        public void DeletedBy_CountsTouchedOnce_PlusRevitsOwnDeletes()
        {
            var env = new Envelope { Changes = new ChangeReportDto
            {
                Touched = new ChangesDto { Deleted = new List<ElementRef> { new ElementRef(1, null), new ElementRef(2, null) } },
                TouchedByCategory = new SideEffectsDto { DeletedByCategory = new Dictionary<string, int> { ["Doors"] = 2 } },
                SideEffects = new SideEffectsDto { DeletedByCategory = new Dictionary<string, int> { ["Tags"] = 1 } },
            } };
            Assert.Equal(3, ResultCardModel.DeletedBy(new[] { env }));
            Assert.Equal(0, ResultCardModel.DeletedBy(new[] { new Envelope() }));
        }

        [Fact]
        public void TheUndoneBanner_ForACreatedCard_NamesWhatWasRemoved()
        {
            var m = new ResultCardModel { Kind = ResultKind.Created };
            m.Created.Add(new CreatedItem { Id = 1 }); m.Created.Add(new CreatedItem { Id = 2 });

            Assert.Equal("2 removed. Nothing else changed.", UndoBanner.For(m, ReceiptLanguage.En).Detail);
        }

        // ---- R2 ----

        [Fact]
        public void TheChangeTable_HasOneRowPerValue_UnderTheLanguagesHeader()
        {
            var m = TwoTypes();

            Assert.Equal(new[] { "Type", "Parameter", "Before", "After", "Source" }, ChangeTable.Columns(ReceiptLanguage.En));
            Assert.Equal("Jenis", ChangeTable.Columns(ReceiptLanguage.Ms)[0]);
            var rows = ChangeTable.Rows(m, ReceiptLanguage.En);
            Assert.Equal(10, rows.Count);
            Assert.Equal(new[] { "Downlight 1x13W", "Model", "empty", "Model-v", "brochure.pdf, page 8" }, rows[0]);   // an empty Before reads "empty", as on the card
        }

        [Fact]
        public void APutBackRow_ShowsTheWrittenValue_Marked_InTheTableAndTheCopiedText()
        {
            var m = TwoTypes();
            var r = m.Groups[0].Rows[0];
            r.ChangedBack = true; r.After = "old"; r.Intended = "Adrino";

            Assert.Equal("Adrino (put back)", ChangeTable.Rows(m, ReceiptLanguage.En)[0][3]);
            Assert.Equal("Adrino (dipulihkan)", ChangeTable.Rows(m, ReceiptLanguage.Ms)[0][3]);
            Assert.Contains("\tAdrino (put back)\t", ChangeTable.TabText(m));
            Assert.StartsWith("Type\tParameter\tBefore\tAfter\tSource\nDownlight 1x13W\tModel\t", ChangeTable.TabText(m));
        }

        [Fact]
        public void NoCard_IsAnEmptyTable() => Assert.Empty(ChangeTable.Rows(null, ReceiptLanguage.En));

        [Fact]
        public void LeaveAsIs_TakesTheWriteAgainOfferAway_ButKeepsTheRowsPutBack()
        {
            var g = new TypeGroup { TypeName = "Wall light", IsType = true };
            g.Rows.Add(new ChangeRow { Param = "Wattage", After = "T5", Intended = "10 W", ChangedBack = true, Ids = new List<long> { 5 } });
            Assert.True(g.CanWriteAgain);

            g.LeftAsIs = true;

            Assert.False(g.CanWriteAgain);
            Assert.True(g.AnyChangedBack);
            Assert.True(g.ChangedBack);
        }
    }
}
