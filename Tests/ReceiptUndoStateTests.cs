// AI-202: pane [Undo] is offered only while the batch's undo group is on top
// of Revit's undo list, and says why in one line when it is not.
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ReceiptUndoStateTests
    {
        private static ReceiptUndoState Armed(string id = "r1")
        {
            var s = new ReceiptUndoState();
            s.Arm(id);
            return s;
        }

        [Fact]
        public void FreshBatch_CanUndo()
        {
            var s = Armed();
            Assert.True(s.CanUndo("r1"));
            Assert.Null(s.WhyNot("r1"));
        }

        [Fact]
        public void AfterRestart_NothingArmed_ExplainsRestart()
        {
            var s = new ReceiptUndoState();
            Assert.False(s.CanUndo("r1"));
            Assert.Contains("dimulakan semula", s.WhyNot("r1"));
        }

        [Fact]
        public void OlderReceipt_IsRefused_NewestIsNot()
        {
            var s = Armed("old");
            s.Arm("new");
            Assert.Contains("terkini", s.WhyNot("old"));
            Assert.True(s.CanUndo("new"));
        }

        [Fact]
        public void ManualEditAfterBatch_ClosesUndo()   // X-11
        {
            var s = Armed();
            s.OnCommitted();
            Assert.Equal(UndoStatus.Superseded, s.Status);
            Assert.Contains("Ctrl+Z", s.WhyNot("r1"));
        }

        [Fact]
        public void PaneUndo_ThenRevitConfirms_IsUndone_AndCannotRepeat()   // A-06
        {
            var s = Armed();
            s.OnPosted();
            Assert.False(s.CanUndo("r1"));
            s.OnUndone();
            Assert.Equal(UndoStatus.Undone, s.Status);
            Assert.Contains("sudah di-undo", s.WhyNot("r1"));
        }

        [Fact]
        public void CtrlZ_ThenRedo_MakesUndoLiveAgain()
        {
            var s = Armed();
            s.OnUndone();
            Assert.False(s.CanUndo("r1"));
            s.OnRedone();
            Assert.True(s.CanUndo("r1"));
        }

        [Fact]
        public void TwoUndos_ThenRedo_StaysClosed()
        {
            var s = Armed();
            s.OnUndone();
            s.OnUndone();   // went below our entry
            s.OnRedone();
            Assert.False(s.CanUndo("r1"));
        }

        [Fact]
        public void CommitAfterUndo_ClearsRedo_StaysClosed()
        {
            var s = Armed();
            s.OnUndone();
            s.OnCommitted();
            s.OnRedone();
            Assert.Equal(UndoStatus.Superseded, s.Status);
        }

        [Fact]
        public void OtherDocumentActive_IsSoft()
        {
            var s = Armed();
            s.SetOtherDocumentActive(true);
            Assert.Contains("Dokumen lain", s.WhyNot("r1"));
            s.SetOtherDocumentActive(false);
            Assert.True(s.CanUndo("r1"));
        }

        [Fact]
        public void DocumentClosed_IsFinal()
        {
            var s = Armed();
            s.OnDocumentClosed();
            Assert.Contains("ditutup", s.WhyNot("r1"));
        }

        [Fact]
        public void UngroupedBatch_IsRefused()
        {
            var s = new ReceiptUndoState();
            s.MarkUnavailable("r1");
            Assert.False(s.CanUndo("r1"));
            Assert.Contains("Ctrl+Z", s.WhyNot("r1"));
        }

        [Fact]
        public void EmptyReceiptId_IsRefused()
        {
            var s = Armed();
            Assert.False(s.CanUndo(""));
            Assert.False(s.CanUndo(null));
        }

        [Theory]
        [InlineData(new string[0], "BinaVibe: perubahan Copilot")]
        [InlineData(new[] { "BinaVibe: set_parameter Fire Rating" }, "BinaVibe: set_parameter Fire Rating")]
        [InlineData(new[] { "BinaVibe: a", "BinaVibe: a", "BinaVibe: b", "BinaVibe: c" }, "BinaVibe: a (+2 lagi)")]
        public void GroupName_IsReadableInRevitUndoList(string[] tx, string expected)
        {
            Assert.Equal(expected, ReceiptUndoState.GroupName(tx));
        }

        [Fact]
        public void BatchCanGroup_RefusesSaveLinkAndDwgTools()
        {
            Assert.True(ReceiptUndoState.BatchCanGroup(new[] { "set_parameter", "get_element_parameters" }));
            Assert.False(ReceiptUndoState.BatchCanGroup(new[] { "set_parameter", "save_document_as" }));
            Assert.False(ReceiptUndoState.BatchCanGroup(new[] { "load_family" }));
            Assert.False(ReceiptUndoState.BatchCanGroup(new[] { "get_dwg_summary" }));
            Assert.True(ReceiptUndoState.BatchCanGroup(null));
        }
    }
}
