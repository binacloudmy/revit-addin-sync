#nullable enable
// The model is told when the drafter undid its writes (live test 0.0.93: its next turn called its own
// earlier write "did not persist"). Revit-free.
using BinaVibe.Copilot.Undo;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UndoNotesTests
    {
        private static UndoChange Undone(string turn, string name, bool byPane = false) =>
            new UndoChange { Kind = UndoChangeKind.Undone, TurnId = turn, Name = name, ByPane = byPane };
        private static UndoChange Redone(string turn, string name) =>
            new UndoChange { Kind = UndoChangeKind.Redone, TurnId = turn, Name = name };

        [Fact]
        public void NothingUndone_NoNote() => Assert.Null(new UndoNotes().Take());

        [Fact]
        public void AnUndo_ProducesOneNote_AndTheNoteIsTakenOnce()
        {
            var n = new UndoNotes();
            n.Record(Undone("t1", "BINA: Set Mark (3)"));

            var note = n.Take();

            Assert.NotNull(note);
            Assert.StartsWith("[BINA add-in note:", note);
            Assert.Contains("the user undid 1 of your earlier change", note);
            Assert.Contains("BINA: Set Mark (3)", note);
            Assert.Contains("did not persist", note);   // told not to say it
            Assert.EndsWith("]", note);
            Assert.Null(n.Take());
        }

        [Fact]
        public void UndoThenRedoBeforeTheNextTurn_SaysNothing()
        {
            var n = new UndoNotes();
            n.Record(Undone("t1", "BINA: Set Mark (3)"));
            n.Record(Redone("t1", "BINA: Set Mark (3)"));

            Assert.Null(n.Take());
        }

        [Fact]
        public void RedoAfterTheModelWasTold_TellsItTheChangeIsBack()
        {
            var n = new UndoNotes();
            n.Record(Undone("t1", "BINA: Set Mark (3)"));
            Assert.NotNull(n.Take());

            n.Record(Redone("t1", "BINA: Set Mark (3)"));
            var note = n.Take();

            Assert.NotNull(note);
            Assert.Contains("redid 1 change", note);
            Assert.Contains("back in the model", note);
        }

        [Fact]
        public void SeveralTurns_AreListedTogether_AndNamesAreCapped()
        {
            var n = new UndoNotes();
            n.Record(Undone("t1", "BINA: " + new string('x', 200)));
            n.Record(Undone("t2", "BINA: Set Comments (2)"));

            var note = n.Take()!;

            Assert.Contains("BINA: Set Comments (2)", note);
            Assert.DoesNotContain(new string('x', 120), note);
            Assert.Equal(2, CountOf(note, "the user undid"));
        }

        [Fact]
        public void OtherChanges_AreIgnored()
        {
            var n = new UndoNotes();
            n.Record(new UndoChange { Kind = UndoChangeKind.Other });
            n.Record(null);
            n.Record(Undone("", "BINA: orphan"));

            Assert.Null(n.Take());
        }

        [Fact]
        public void ANewChat_StartsOver()
        {
            var n = new UndoNotes();
            n.Record(Undone("t1", "BINA: Set Mark (3)"));
            n.Reset();

            Assert.Null(n.Take());
        }

        [Fact]
        public void ThePanesOwnUndo_IsToldToo()
        {
            var n = new UndoNotes();
            n.Record(Undone("t1", "BINA: Set Mark (3)", byPane: true));

            Assert.NotNull(n.Take());
        }

        private static int CountOf(string s, string part)
        {
            var c = 0; var i = 0;
            while ((i = s.IndexOf(part, i, System.StringComparison.Ordinal)) >= 0) { c++; i += part.Length; }
            return c;
        }
    }
}
