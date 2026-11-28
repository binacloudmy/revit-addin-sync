#nullable enable
// r25 audit (SHOULD): a failed "Undo last part" or Redo still changed the card. The view model set moved = true whatever the undo
// job answered, and IRevitActions returned only the summary text. Now the port returns the envelope and the card follows only an
// undo or redo that went through (ok), or a partial one that undid at least one entry (UndoService marks those Partial).
// Revit-free; the view model wiring is pinned by its source (WPF cannot run on macOS).

using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Undo;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class R25UndoCardTests
    {
        [Fact]
        public void TheCardFollowsAnUndoThatWentThrough()
        {
            var ok = new Envelope { Ok = true, Status = EnvelopeStatus.Done, Phase = EnvelopePhase.Commit, Summary = "Undone: Set Comments", Count = 1 };
            Assert.True(UndoWords.CardFollows(ok));
        }

        [Fact]
        public void TheCardStaysWhenNothingWasUndone()
        {
            Assert.False(UndoWords.CardFollows(null));
            Assert.False(UndoWords.CardFollows(WriteEnvelopes.Failure(ErrorCodes.RevitBusy, "An undo is already in progress; wait for it to finish.", EnvelopePhase.Commit)));
            Assert.False(UndoWords.CardFollows(WriteEnvelopes.Failure(ErrorCodes.NotAllowed, "This turn cannot be undone from here. Nothing was undone.", EnvelopePhase.Commit)));
        }

        [Fact]
        public void TheCardFollowsAPartialUndoThatUndidSomething()
        {
            // UndoService.Failure: "N of M change(s) were undone before it stopped." with status partial when N > 0.
            var partial = WriteEnvelopes.Failure(ErrorCodes.NotAllowed, "Revit stopped the undo. 1 of 2 change(s) were undone before it stopped.", EnvelopePhase.Commit);
            partial.Status = EnvelopeStatus.Partial;
            Assert.True(UndoWords.CardFollows(partial));
        }

        [Fact]
        public void ThePaneMovesTheCardOnlyOnTheUndoEnvelope()
        {
            var vm = System.IO.File.ReadAllText(RevitWebAppSync.Tests.CodeTestSupport.RepoFile("UI/Copilot/CopilotViewModel.cs"));
            var port = System.IO.File.ReadAllText(RevitWebAppSync.Tests.CodeTestSupport.RepoFile("UI/Copilot/Ports/IRevitActions.cs"));
            var actions = System.IO.File.ReadAllText(RevitWebAppSync.Tests.CodeTestSupport.RepoFile("UI/Copilot/Ports/RevitActions.cs"));
            Assert.Contains("Task<BinaVibe.Copilot.Contracts.Envelope> UndoTurnAsync(string turnId, bool lastBatchOnly);", port);
            Assert.Contains("Task<BinaVibe.Copilot.Contracts.Envelope> RedoTurnAsync(string turnId);", port);
            Assert.DoesNotContain("?.Summary;", actions);
            Assert.Contains("moved = BinaVibe.Copilot.Undo.UndoWords.CardFollows(result);", vm);
            Assert.False(Regex.IsMatch(vm, @"\bmoved = true;"), "the card must not follow an undo that failed");
        }
    }
}
