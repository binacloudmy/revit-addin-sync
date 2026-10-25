#nullable enable
// W3 Task 5 (spec §7.6). The add-in's model of Revit's undo stack per
// document: one "BINA: …" entry per committed batch; the turn's Undo is
// allowed only while its entries are on top and nothing foreign happened;
// each posted Undo must undo exactly the expected name, else Redo and stop.
//
// Revit-free: compiles in Tests.csproj and runs in the macOS scratch harness.

using BinaVibe.Copilot.Undo;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UndoModelTests
    {
        private const string Doc = "project:aaaa";

        private static UndoModel TwoBatches()
        {
            var m = new UndoModel();
            m.Pushed(Doc, "t1", "BINA: Set Mark (2)");
            m.Pushed(Doc, "t1", "BINA: Set Comments (2)");
            return m;
        }

        [Fact]
        public void PlansTheTurnsEntriesNewestFirst()
        {
            var m = TwoBatches();
            Assert.True(m.CanUndoTurn("t1", out var doc, out var reason), reason);
            Assert.Equal(Doc, doc);
            Assert.Equal(new[] { "BINA: Set Comments (2)", "BINA: Set Mark (2)" }, m.PlanUndo("t1"));
        }

        [Fact]
        public void OurUndoOfTheExpectedNameAdvancesUntilTheTurnIsUndone()
        {
            var m = TwoBatches();
            m.ExpectUndo(Doc, "BINA: Set Comments (2)");
            Assert.Equal(UndoObservation.Expected, m.Undone(Doc, new[] { "BINA: Set Comments (2)" }));
            Assert.Equal(new[] { "BINA: Set Mark (2)" }, m.PlanUndo("t1"));

            m.ExpectUndo(Doc, "BINA: Set Mark (2)");
            Assert.Equal(UndoObservation.Expected, m.Undone(Doc, new[] { "BINA: Set Mark (2)" }));
            Assert.False(m.CanUndoTurn("t1", out _, out var reason));
            Assert.Contains("nothing", reason);
        }

        [Fact]
        public void AnUndoOfAnotherNameIsAMismatchThatDisables()
        {
            var m = TwoBatches();
            m.ExpectUndo(Doc, "BINA: Set Comments (2)");
            Assert.Equal(UndoObservation.Mismatch, m.Undone(Doc, new[] { "Move" }));
            Assert.False(m.CanUndoTurn("t1", out _, out _));
        }

        [Fact]
        public void AnUndoThatSweptInAnotherNameIsAMismatch()
        {
            var m = TwoBatches();
            m.ExpectUndo(Doc, "BINA: Set Comments (2)");
            Assert.Equal(UndoObservation.Mismatch, m.Undone(Doc, new[] { "BINA: Set Comments (2)", "Move" }));
            m.ExpectUndo(Doc, "BINA: Set Comments (2)");
            Assert.Equal(UndoObservation.Mismatch, m.Undone(Doc, new string[0]));
        }

        [Fact]
        public void TheLegacyNewestEntryUndoAcceptsOnlyOurNames()
        {
            var m = new UndoModel();
            m.ExpectUndoOfOurs(Doc);
            Assert.Equal(UndoObservation.Expected, m.Undone(Doc, new[] { "BinaVibe: batch build" }));
            m.ExpectUndoOfOurs(Doc);
            Assert.Equal(UndoObservation.Mismatch, m.Undone(Doc, new[] { "BINA: Set Mark (2)", "Move" }));
        }

        [Fact]
        public void ForeignChangeDisablesEarlierEntriesOnly()
        {
            var m = TwoBatches();
            m.ForeignChange(Doc, new[] { "Move" });
            Assert.False(m.CanUndoTurn("t1", out _, out var reason));
            Assert.Contains("Revit's Undo list", reason);

            // A later BINA batch sits on top of the foreign change and can still be undone.
            m.Pushed(Doc, "t2", "BINA: Revit code: add walls");
            Assert.True(m.CanUndoTurn("t2", out _, out _));
        }

        [Fact]
        public void DrafterUndoOrRedoOfOurEntriesDisables()
        {
            var undo = TwoBatches();
            Assert.Equal(UndoObservation.Unexpected, undo.Undone(Doc, new[] { "BINA: Set Comments (2)" }));
            Assert.False(undo.CanUndoTurn("t1", out _, out _));

            var redo = TwoBatches();
            redo.Redone(Doc, new[] { "BINA: Set Comments (2)" });
            Assert.False(redo.CanUndoTurn("t1", out _, out _));
        }

        [Fact]
        public void OurOwnRedoAfterAMismatchIsNotTheDrafters()
        {
            var m = TwoBatches();
            m.ExpectUndo(Doc, "BINA: Set Comments (2)");
            m.Undone(Doc, new[] { "Move" });
            m.ExpectRedo(Doc);
            Assert.Equal(UndoObservation.Expected, m.Redone(Doc, new[] { "Move" }));
            Assert.False(m.CanUndoTurn("t1", out _, out _));   // still disabled: we stopped
        }

        [Fact]
        public void SyncReloadOrCloseDisables()
        {
            var m = TwoBatches();
            m.Disable(Doc, "the model was synchronized with central");
            Assert.False(m.CanUndoTurn("t1", out _, out var reason));
            Assert.Contains("synchronized", reason);

            var closed = TwoBatches();
            closed.Forget(Doc);
            Assert.False(closed.CanUndoTurn("t1", out _, out _));
        }

        [Fact]
        public void ALaterTurnOnTopBlocksTheEarlierTurn()
        {
            var m = TwoBatches();
            m.Pushed(Doc, "t2", "BINA: Set Mark (5)");
            Assert.False(m.CanUndoTurn("t1", out _, out var reason));
            Assert.Contains("later", reason);
            Assert.True(m.CanUndoTurn("t2", out _, out _));
        }

        [Fact]
        public void DocumentsAreIndependent()
        {
            var m = TwoBatches();
            m.ForeignChange("project:bbbb", new[] { "Move" });
            Assert.True(m.CanUndoTurn("t1", out _, out _));
        }

        [Fact]
        public void AnUndoInFlightBlocksASecondOne()
        {
            var m = TwoBatches();
            m.ExpectUndo(Doc, "BINA: Set Comments (2)");
            Assert.False(m.CanUndoTurn("t1", out _, out var reason));
            Assert.Contains("progress", reason);
        }

        [Fact]
        public void UnknownTurnCannotUndo()
        {
            Assert.False(new UndoModel().CanUndoTurn("nope", out var doc, out var reason));
            Assert.Null(doc);
            Assert.False(string.IsNullOrEmpty(reason));
        }
    }
}
