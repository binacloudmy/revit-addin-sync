#nullable enable
// r18 (live Round L, L15-L17 on 0.0.155): the drafter undid the L15 schedule turn and the L17 edit turn in ONE step of Revit's
// Undo list (both entries are named "BINA: Revit code"). DocumentChanged reported both names, but the add-in's model popped only
// the newest entry, so the L15 entry stayed live with nothing above it and its card showed Undo enabled again. An undo (or redo)
// of several entries now moves as many entries as Revit named. Revit-free.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Undo;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UndoSweepR18Tests
    {
        private const string Doc = "project:aaaa";
        private const string Code = "BINA: Revit code";

        private static UndoModel TwoTurnsSameName()
        {
            var m = new UndoModel();
            m.Pushed(Doc, "L15", Code);   // the schedule
            m.Pushed(Doc, "L17", Code);   // the edit of it
            return m;
        }

        [Fact]
        public void BeforeTheUndo_TheOlderTurnIsUnderANewerOne()
        {
            var m = TwoTurnsSameName();
            Assert.Equal(UndoBlock.NewerChangeOnTop, m.WhyNot("L15"));
            Assert.Equal(UndoBlock.None, m.WhyNot("L17"));
        }

        [Fact]
        public void OneUndoOfBothEntries_LeavesBothTurnsUndone()
        {
            var m = TwoTurnsSameName();
            var changes = new List<UndoChange>();
            m.Changed += changes.Add;
            Assert.Equal(UndoObservation.Unexpected, m.Undone(Doc, new[] { Code, Code }));
            Assert.Equal(UndoBlock.AlreadyUndone, m.WhyNot("L15"));   // was None: the card offered Undo again
            Assert.Equal(UndoBlock.AlreadyUndone, m.WhyNot("L17"));
            Assert.False(m.CanUndoTurn("L15", out _, out _));
            // Each turn is told (its card redraws as undone; the thread's line names the entries).
            Assert.Equal(new[] { "L17", "L15" }, changes.Where(c => c.Kind == UndoChangeKind.Undone).Select(c => c.TurnId).ToArray());
            Assert.All(changes, c => Assert.False(c.ByPane));
        }

        [Fact]
        public void OneUndoOfTwoDifferentNames_PopsBothInAnyOrder()
        {
            var m = new UndoModel();
            m.Pushed(Doc, "t1", "BINA: Set Mark (2)");
            m.Pushed(Doc, "t2", "BINA: Tag doors");
            m.Undone(Doc, new[] { "BINA: Set Mark (2)", "BINA: Tag doors" });
            Assert.Equal(UndoBlock.AlreadyUndone, m.WhyNot("t1"));
            Assert.Equal(UndoBlock.AlreadyUndone, m.WhyNot("t2"));
        }

        [Fact]
        public void AMultiUndoThatSweptAForeignEditToo_PopsIt()
        {
            var m = new UndoModel();
            m.Pushed(Doc, "t1", Code);
            m.ForeignChange(Doc, new[] { "Move" });
            m.Undone(Doc, new[] { "Move", Code });
            Assert.Equal(UndoBlock.AlreadyUndone, m.WhyNot("t1"));
        }

        [Fact]
        public void NamesThatDoNotMatchTheTop_StillBlock()
        {
            var m = TwoTurnsSameName();
            m.Undone(Doc, new[] { Code, "BINA: Set Mark (2)" });
            Assert.Equal(UndoBlock.RevitUndoRedoUsed, m.WhyNot("L15"));
        }

        [Fact]
        public void ASingleUndo_StillPopsOnlyTheNewest()
        {
            var m = TwoTurnsSameName();
            m.Undone(Doc, new[] { Code });
            Assert.Equal(UndoBlock.AlreadyUndone, m.WhyNot("L17"));
            Assert.Equal(UndoBlock.None, m.WhyNot("L15"));   // now on top: its Undo is offered, and right
        }

        [Fact]
        public void OneRedoOfBothEntries_BringsBothBack()
        {
            var m = TwoTurnsSameName();
            m.Undone(Doc, new[] { Code, Code });
            Assert.Equal(UndoObservation.Unexpected, m.Redone(Doc, new[] { Code, Code }));
            Assert.Equal(UndoBlock.NewerChangeOnTop, m.WhyNot("L15"));
            Assert.Equal(UndoBlock.None, m.WhyNot("L17"));
        }

        [Fact]
        public void OurOwnPostedUndo_IsUnchanged()
        {
            var m = TwoTurnsSameName();
            m.ExpectUndo(Doc, Code);
            Assert.Equal(UndoObservation.Expected, m.Undone(Doc, new[] { Code }));
            Assert.Equal(UndoBlock.AlreadyUndone, m.WhyNot("L17"));
            Assert.Equal(UndoBlock.None, m.WhyNot("L15"));
        }
    }
}
