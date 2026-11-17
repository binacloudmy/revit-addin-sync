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
    }
}
