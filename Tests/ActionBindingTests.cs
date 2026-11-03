// ActionBinding (AI-203): a pending action runs only in the document and
// view it was planned against. Pure logic — the Revit capture and the
// drainer refusal are covered by the live rows X-02 / X-03 / X-04.

using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ActionBindingTests
    {
        private static ActionBinding B(string fp, long? view, string title = "MRSM 1", string viewName = "Ground Floor") =>
            new ActionBinding { DocumentFingerprint = fp, DocumentTitle = title, ViewId = view, ViewName = viewName };

        [Fact]
        public void SameDocumentAndView_IsOk()
        {
            Assert.Equal(BindingVerdict.Ok,
                ActionBinding.Check(B("aaaa", 10), B("aaaa", 10), BindingScope.DocumentAndView));
        }

        [Fact]
        public void OtherDocument_IsDocumentChanged_ForBothScopes()
        {
            Assert.Equal(BindingVerdict.DocumentChanged,
                ActionBinding.Check(B("aaaa", 10), B("bbbb", 10), BindingScope.Document));
            Assert.Equal(BindingVerdict.DocumentChanged,
                ActionBinding.Check(B("aaaa", 10), B("bbbb", 99), BindingScope.DocumentAndView));
        }

        [Fact]
        public void OtherView_IsViewChanged_OnlyWhenViewIsInScope()
        {
            Assert.Equal(BindingVerdict.ViewChanged,
                ActionBinding.Check(B("aaaa", 10), B("aaaa", 11), BindingScope.DocumentAndView));
            Assert.Equal(BindingVerdict.Ok,
                ActionBinding.Check(B("aaaa", 10), B("aaaa", 11), BindingScope.Document));
        }

        [Fact]
        public void NoScope_NeverRefuses()
        {
            Assert.Equal(BindingVerdict.Ok,
                ActionBinding.Check(B("aaaa", 10), B("bbbb", 11), BindingScope.None));
        }

        [Fact]
        public void MissingSide_NeverRefuses()
        {
            Assert.Equal(BindingVerdict.Ok, ActionBinding.Check(null, B("bbbb", 11), BindingScope.DocumentAndView));
            Assert.Equal(BindingVerdict.Ok, ActionBinding.Check(B("aaaa", 10), null, BindingScope.DocumentAndView));
            Assert.Equal(BindingVerdict.Ok,
                ActionBinding.Check(B("", null), B("bbbb", 11), BindingScope.DocumentAndView));
            Assert.Equal(BindingVerdict.Ok,
                ActionBinding.Check(B("aaaa", 10), B("aaaa", null), BindingScope.DocumentAndView));
        }

        [Fact]
        public void Copy_IsIndependent()
        {
            var a = B("aaaa", 10);
            var c = a.Copy();
            c.ViewId = 11;
            Assert.Equal(10, a.ViewId);
        }

        [Fact]
        public void DocumentMessage_NamesBothModels_AndSaysNothingWritten()
        {
            var msg = ActionBinding.DocumentChangedMessage(B("aaaa", 1, "MRSM 1"), B("bbbb", 2, "MRSM 2"));
            Assert.Contains("dokumen telah bertukar", msg);
            Assert.Contains("datang daripada model lain", msg);
            Assert.Contains("\"MRSM 1\"", msg);
            Assert.Contains("Tiada apa yang ditulis dalam \"MRSM 2\"", msg);
            Assert.Contains("mulakan chat baru untuk \"MRSM 2\"", msg);
        }

        [Fact]
        public void Refusal_View_IsFailedResultTheAgentCanReplanFrom()
        {
            var call = new PendingToolCall { ToolCallId = "tc-1", Tool = "tag_all_in_view", Mutate = true };
            var r = ActionBinding.Refusal(call, BindingVerdict.ViewChanged,
                B("aaaa", 1, viewName: "Ground Floor"), B("aaaa", 2, viewName: "Level 1"));
            Assert.Equal("tc-1", r.ToolCallId);
            Assert.False(r.Ok);
            Assert.Contains("pandangan telah bertukar", r.Error);
            Assert.Contains("\"Ground Floor\"", r.Error);
            Assert.Contains("\"Level 1\"", r.Error);
            Assert.Contains("Tiada apa yang ditulis", r.Error);
        }

        [Fact]
        public void Refusal_Document_IsFailedResult()
        {
            var call = new PendingToolCall { ToolCallId = "tc-2", Tool = "set_parameter", Mutate = true };
            var r = ActionBinding.Refusal(call, BindingVerdict.DocumentChanged, B("aaaa", 1), B("bbbb", 1, "Other"));
            Assert.False(r.Ok);
            Assert.StartsWith("ditolak:", r.Error);
            Assert.Contains("dokumen telah bertukar", r.Error);
        }

        [Fact]
        public void CodeCardMessage_ViewChanged_SaysNothingRun()
        {
            var msg = ActionBinding.CodeCardMessage(BindingVerdict.ViewChanged, B("aaaa", 1), B("aaaa", 2, viewName: "3D"));
            Assert.Contains("pandangan telah bertukar", msg);
            Assert.Contains("Tiada apa yang dijalankan", msg);
        }

        [Fact]
        public void Messages_HandleMissingNames()
        {
            var msg = ActionBinding.DocumentChangedMessage(new ActionBinding(), null);
            Assert.Contains("model lain", msg);
            Assert.DoesNotContain("\"\"", msg);
        }
    }
}
