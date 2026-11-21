#nullable enable
// r23 (0.0.171): (B) live P16 G3 7241e714: after a committed own-code write emptied the selection, the ribbon stayed on "Modify | Ducts"
// with greyed tools and Select by ID disabled. Every write request now ends by activating the active view as after a find, after the
// AI-456 give-back, only for a valid document with no transaction open, with no view or selection change. (A) Up recalls the last prompt
// only with the caret at index 0 of an empty box.
using System.IO;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class WriteEndRefreshR23Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel)).Replace("\r\n", "\n");
        }

        [Fact]
        public void TheRefresh_RunsOnlyForAValidDocument_WithNoTransactionOpen()
        {
            Assert.True(WriteEndRefresh.Applies(hasDocument: true, documentValid: true, transactionOpen: false));
            Assert.False(WriteEndRefresh.Applies(hasDocument: false, documentValid: false, transactionOpen: false));   // the model was closed
            Assert.False(WriteEndRefresh.Applies(hasDocument: true, documentValid: false, transactionOpen: false));    // the document went
            Assert.False(WriteEndRefresh.Applies(hasDocument: true, documentValid: true, transactionOpen: true));      // a change is still open
        }

        [Fact]
        public void OnlyARequestThatChangedTheSelection_Refreshes_AndOnlyOncePerTurn()
        {
            // 7241e714: the duct was selected when the request started and the commit emptied the selection: refresh.
            Assert.True(WriteEndRefresh.Applies(true, true, false, new long[] { 1382112 }, new long[0], "turn-g3", null));
            // A read pass, a refused preview or a re-preview that left the selection as it was: no keyboard taken.
            Assert.False(WriteEndRefresh.Applies(true, true, false, new long[] { 1382112 }, new long[] { 1382112 }, "turn-g3", null));
            Assert.False(WriteEndRefresh.Applies(true, true, false, new long[] { 1, 2 }, new long[] { 2, 1 }, "turn-g3", null));   // same set
            Assert.False(WriteEndRefresh.Applies(true, true, false, new long[0], new long[0], "turn-g3", null));
            // The same turn again (its commit after its preview, a later write of the turn): once only.
            Assert.False(WriteEndRefresh.Applies(true, true, false, new long[] { 1382112 }, new long[0], "turn-g3", "turn-g3"));
            Assert.True(WriteEndRefresh.Applies(true, true, false, new long[] { 1382112 }, new long[0], "turn-g4", "turn-g3"));      // the next turn
            // The document test still comes first.
            Assert.False(WriteEndRefresh.Applies(true, true, true, new long[] { 1 }, new long[0], "t", null));
        }

        [Fact]
        public void EveryWriteRequest_EndsWithTheRefresh_AfterTheGiveBack()
        {
            var engine = Src("BinaVibe/Copilot/Writes/RevitWriteEngine.cs");
            // The preview request (a Cancel at the card ends here) and the commit request, committed or not, both in their finally.
            Assert.Contains("finally { Code.RevitCodeHost.ForgetSelectionSwap(); RefreshCommandState(app, batch.TurnId, selectedAtStart); }", engine);
            var commit = engine.Substring(engine.IndexOf("public Task<CommitOutcome> CommitAsync(", System.StringComparison.Ordinal));
            commit = commit.Substring(0, commit.IndexOf("}, ct);", System.StringComparison.Ordinal));
            var give = commit.IndexOf("Code.RevitCodeHost.GiveBackSelection(track.Doc, track.UiDoc);", System.StringComparison.Ordinal);
            var refresh = commit.IndexOf("RefreshCommandState(app, batch.TurnId, selectedAtStart);", System.StringComparison.Ordinal);
            Assert.True(commit.IndexOf("var selectedAtStart = SelectedIds(app);", System.StringComparison.Ordinal) < commit.IndexOf("try", System.StringComparison.Ordinal));
            Assert.True(give > 0 && refresh > give);                                   // AI-456: the give-back first
            Assert.True(commit.IndexOf("finally", System.StringComparison.Ordinal) < give);
        }

        [Fact]
        public void TheRefresh_IsTheFindsViewActivation_AndTouchesNoViewAndNoSelection()
        {
            var engine = Src("BinaVibe/Copilot/Writes/RevitWriteEngine.cs");
            var at = engine.IndexOf("private static void RefreshCommandState(UIApplication app, string? turnId, IReadOnlyList<long>? selectedAtStart)", System.StringComparison.Ordinal);
            var body = engine.Substring(at, engine.IndexOf("\n        }\n", at, System.StringComparison.Ordinal) - at);
            Assert.Contains("selectedAtStart, SelectedIds(app!), turnId, _refreshedTurn)) return;", body);
            Assert.True(body.IndexOf("_refreshedTurn = turnId;", System.StringComparison.Ordinal) < body.IndexOf("ViewActivation.Show(app!)", System.StringComparison.Ordinal));
            Assert.Contains("ViewActivation.Show(app!)", body);
            Assert.Contains("catch (Exception)", body);
            Assert.DoesNotContain("Selection", body);
            Assert.DoesNotContain("RequestViewChange", body);
            Assert.DoesNotContain("ActiveView =", body);
            // The find uses the same call after it selects.
            Assert.Contains("if (selected > 0) ViewActivation.Show(uidoc.Application);", Src("BinaVibe/Copilot/Read/FindExecutor.cs"));
        }

        [Fact]
        public void UpRecalls_OnlyWithTheCaretAtTheStartOfAnEmptyBox()
        {
            Assert.True(PromptRecall.RecallsOnUp("", popupOpen: false, busy: false, caretIndex: 0));
            Assert.False(PromptRecall.RecallsOnUp("", popupOpen: false, busy: false, caretIndex: 1));
            Assert.False(PromptRecall.RecallsOnUp("draft", popupOpen: false, busy: false, caretIndex: 0));
            Assert.False(PromptRecall.RecallsOnUp("", popupOpen: true, busy: false, caretIndex: 0));
            Assert.False(PromptRecall.RecallsOnUp("", popupOpen: false, busy: true, caretIndex: 0));
            Assert.Contains("PromptRecall.RecallsOnUp(Editor.Text, Picker.IsOpen || _slashOpen, busy: false, caretIndex: Editor.CaretIndex)",
                Src("UI/Copilot/Controls/MentionInput.xaml.cs"));
        }
    }
}
