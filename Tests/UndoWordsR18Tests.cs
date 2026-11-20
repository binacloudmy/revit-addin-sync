#nullable enable
// r18 (live Round L): the same Undo read three ways in the pane: the card's banner "Undone. The model is back to how it was.", the
// thread line after Revit's own Undo "You undid: Revit code" (L9b, L20a), and the thread line after the card's Undo button
// "Undid 1 BINA change(s) in Revit: BINA: Revit code." Every undo line now starts "Undone" ("Redone" for a redo).
using BinaVibe.Copilot.Undo;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UndoWordsR18Tests
    {
        [Fact]
        public void RevitsOwnUndo_AndTheCardsButton_SayTheSameLine()
        {
            var qat = UndoNotices.TextOf(UndoChangeKind.Undone, new[] { UndoNotices.Strip("BINA: Revit code") });
            var button = UndoWords.LineOfEntries(false, new[] { "BINA: Revit code" });
            Assert.Equal("Undone: Revit code", qat);
            Assert.Equal(qat, button);
        }

        [Fact]
        public void SeveralEntries_AreNamedOnce_AndRedoSaysRedone()
        {
            Assert.Equal("Undone: Revit code", UndoWords.LineOfEntries(false, new[] { "BINA: Revit code", "BINA: Revit code" }));
            Assert.Equal("Redone: Set Mark and Tag doors", UndoWords.LineOfEntries(true, new[] { "BINA: Set Mark", "BINA: Tag doors" }));
            Assert.Equal("Undone: a Copilot change", UndoWords.Line(false, null));
        }

        [Fact]
        public void TheCardsOwnWords_AlreadyStartUndone()
        {
            Assert.StartsWith("Undone", UndoBanner.For(null, ReceiptLanguage.En).Title);
            Assert.StartsWith("Undone", ReceiptTemplates.UndoStatusLine(UndoBlock.AlreadyUndone, ReceiptLanguage.En));
        }
    }
}
