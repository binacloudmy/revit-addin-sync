#nullable enable
// Final retest 17 Nov (R2, the round-2 value edit): after "Change" and a typed value the card showed it, but the line under the card kept the old
// value; after "Undo last part" Revit held the old value, a Redo appeared, and the card still showed the edit. The card model now keeps the edits it
// made as parts of the turn; Undo last part puts the earlier value back, Redo the edit, and the pane draws the card again (Revision is part of the
// retained card's key).
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CardFollowsUndo0129Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        private static (ResultCardModel Card, ChangeRow Row) Card()
        {
            var row = new ChangeRow { Param = "Mark", Before = "", After = "ROUND2-1" };
            var card = new ResultCardModel { Kind = ResultKind.Changes };
            return (card, row);
        }

        [Fact]
        public void AnEdit_MovesTheRow_AndTheRevision()
        {
            var (card, row) = Card();
            Assert.Equal(0, card.Revision);
            card.NoteEdit(row, row.After, "ROUND2-EDIT", ReceiptLanguage.En);
            Assert.Equal("ROUND2-EDIT", row.After);
            Assert.Equal(1, card.Revision);
        }

        [Fact]
        public void UndoLastPart_PutsTheEarlierValueBack_AndRedoTheEdit()
        {
            var (card, row) = Card();
            card.NoteEdit(row, row.After, "ROUND2-EDIT", ReceiptLanguage.En);

            Assert.True(card.UndoLastPart(ReceiptLanguage.En));
            Assert.Equal("ROUND2-1", row.After);
            Assert.Equal(2, card.Revision);

            Assert.True(card.RedoPart(ReceiptLanguage.En));
            Assert.Equal("ROUND2-EDIT", row.After);
            Assert.Equal(3, card.Revision);
        }

        [Fact]
        public void TwoEdits_UndoGoesOneAtATime_InOrder()
        {
            var (card, row) = Card();
            card.NoteEdit(row, row.After, "A", ReceiptLanguage.En);
            card.NoteEdit(row, row.After, "B", ReceiptLanguage.En);
            card.UndoLastPart(ReceiptLanguage.En);
            Assert.Equal("A", row.After);
            card.UndoLastPart(ReceiptLanguage.En);
            Assert.Equal("ROUND2-1", row.After);
            card.RedoPart(ReceiptLanguage.En);
            Assert.Equal("A", row.After);
        }

        [Fact]
        public void APartTheCardDoesNotTrack_IsUndoneWithoutTouchingARow()
        {
            var (card, row) = Card();
            card.NoteEdit(row, row.After, "A", ReceiptLanguage.En);
            card.NoteOtherPart();
            Assert.False(card.UndoLastPart(ReceiptLanguage.En));   // the untracked batch: nothing on the card to put back
            Assert.Equal("A", row.After);
        }

        [Fact]
        public void NothingToUndoOrRedo_ChangesNothing()
        {
            var (card, row) = Card();
            Assert.False(card.UndoLastPart(ReceiptLanguage.En));
            Assert.False(card.RedoPart(ReceiptLanguage.En));
            Assert.Equal("ROUND2-1", row.After);
            Assert.Equal(0, card.Revision);
        }

        [Fact]
        public void ANewEdit_AfterAnUndo_DropsTheRedo()
        {
            var (card, row) = Card();
            card.NoteEdit(row, row.After, "A", ReceiptLanguage.En);
            card.UndoLastPart(ReceiptLanguage.En);
            card.NoteEdit(row, row.After, "C", ReceiptLanguage.En);
            Assert.False(card.RedoPart(ReceiptLanguage.En));
            Assert.Equal("C", row.After);
        }

        [Fact]
        public void TheWiring_EditCallsNoteEdit_TheKeyHoldsTheRevision_AndTheUndoPathFollows()
        {
            Assert.Contains("card.NoteEdit(row, row.After, norm, lang);", Src("UI/Copilot/Controls/ChangesBody.cs"));
            Assert.Contains("{card.Groups.Count(g => g.LeftAsIs)}|{card.Revision}", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
            var vm = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("m.ResultCard.RedoPart(m.Lang) : lastBatchOnly && m.ResultCard.UndoLastPart(m.Lang)", vm);
        }

        // The live shape (final retest 17 Nov, H_R2 screenshots): the server's item row under the card reads "900 x 2325 door, Mark 349" and
        // "349 · ROUND2-1"; after the edit the card showed ROUND2-EDIT and that line did not move.
        private static ResultCardModel CardWithItems(string cellValue, out ChangeRow row)
        {
            row = new ChangeRow { Param = "Comments", Before = "", After = "ROUND2-1" };
            var card = new ResultCardModel { Kind = ResultKind.Changes };
            card.Items = ResultItems.From(new BinaVibe.Copilot.Contracts.TurnResultDto
            {
                Items = new System.Collections.Generic.List<BinaVibe.Copilot.Contracts.ResultItemDto>
                {
                    new BinaVibe.Copilot.Contracts.ResultItemDto
                    {
                        Kind = BinaVibe.Copilot.Contracts.ResultItemKinds.Element, Title = "900 x 2325 door, Mark 349",
                        Cells = new System.Collections.Generic.Dictionary<string, string> { ["Comments"] = cellValue },
                    },
                },
            });
            return card;
        }

        [Fact]
        public void TheLineUnderTheCard_FollowsTheEdit_TheUndo_AndTheRedo()
        {
            var card = CardWithItems("349 · ROUND2-1", out var row);
            var cell = () => card.Items!.Sections[0].Rows[0].Cells[0].Value;

            card.NoteEdit(row, row.After, "ROUND2-EDIT", ReceiptLanguage.En);
            Assert.Equal("349 · ROUND2-EDIT", cell());

            card.UndoLastPart(ReceiptLanguage.En);
            Assert.Equal("349 · ROUND2-1", cell());

            card.RedoPart(ReceiptLanguage.En);
            Assert.Equal("349 · ROUND2-EDIT", cell());
            Assert.Equal("900 x 2325 door, Mark 349", card.Items!.Sections[0].Rows[0].Title);   // the door's own text is not the value: untouched
        }

        [Fact]
        public void ACellThatIsTheValue_BecomesTheNewValue()
        {
            var card = CardWithItems("ROUND2-1", out var row);
            card.NoteEdit(row, row.After, "ROUND2-EDIT", ReceiptLanguage.En);
            Assert.Equal("ROUND2-EDIT", card.Items!.Sections[0].Rows[0].Cells[0].Value);
        }

        [Fact]
        public void TheValueInsideAnotherWord_IsNotSwapped()
        {
            Assert.Equal("ROUND2-10 · X", ResultCardModel.SwapValue("ROUND2-10 · X", "ROUND2-1", "A"));
            Assert.Equal("A · X", ResultCardModel.SwapValue("ROUND2-1 · X", "ROUND2-1", "A"));
            Assert.Equal("", ResultCardModel.SwapValue(null, "a", "b"));
        }

        // Control run c0693d65 (17 Nov): the fact line under the card reads "<written> · <earlier value>" ("ROUND2-1 · 240V", the row says "was 240V").
        // Only the written value follows the edit; the earlier value stays what it was, also when the edit is back to that very value.
        [Fact]
        public void TheEarlierValueInTheFactLine_IsNotTouched_WhenTheEditIsBackToIt()
        {
            var row = new ChangeRow { Param = "Comments", Before = "240V", After = "ROUND2-1" };
            var card = new ResultCardModel { Kind = ResultKind.Changes };
            card.Items = ResultItems.From(new BinaVibe.Copilot.Contracts.TurnResultDto
            {
                Items = new System.Collections.Generic.List<BinaVibe.Copilot.Contracts.ResultItemDto>
                {
                    new BinaVibe.Copilot.Contracts.ResultItemDto
                    {
                        Kind = BinaVibe.Copilot.Contracts.ResultItemKinds.Element, Title = "(PT2p600a) 900 x 2325 a, Mark 349",
                        Cells = new System.Collections.Generic.Dictionary<string, string> { ["Comments"] = "ROUND2-1", ["was"] = "240V" },
                    },
                },
            });
            string Line() => string.Join(" | ", System.Linq.Enumerable.Select(card.Items!.Sections[0].Rows[0].Cells, c => c.Value));

            card.NoteEdit(row, row.After, "240V", ReceiptLanguage.En);
            Assert.Equal("240V | 240V", Line());            // the written value is 240V now; "was 240V" stays

            card.UndoLastPart(ReceiptLanguage.En);
            Assert.Equal("ROUND2-1 | 240V", Line());        // old code: "ROUND2-1 | ROUND2-1"

            card.RedoPart(ReceiptLanguage.En);
            Assert.Equal("240V | 240V", Line());
        }

        [Fact]
        public void SwapValue_FirstOnly_ReplacesOnlyTheFirstStandingValue()
        {
            Assert.Equal("A · 240V", ResultCardModel.SwapValue("240V · 240V", "240V", "A", firstOnly: true));
            Assert.Equal("A · A", ResultCardModel.SwapValue("240V · 240V", "240V", "A"));
        }
    }
}
