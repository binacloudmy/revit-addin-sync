#nullable enable
// r25 audit (NICE): "Send again" on the 409 card and "Try again" on the credit card re-sent only the text: the screenshots, files,
// @ mentions, the slash command (and its inputs) and the text the thread shows were dropped, because the retry payload was built from
// the display text alone. The pane now keeps what it really sent and re-sends it the way the sign-in path does. The view model is WPF,
// so this is pinned by its source.

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class R25SendAgainTests
    {
        private static string Vm() => File.ReadAllText(RevitWebAppSync.Tests.CodeTestSupport.RepoFile("UI/Copilot/CopilotViewModel.cs")).Replace("\r\n", "\n");

        [Fact]
        public void ThePaneKeepsTheWholeSendForARetry()
        {
            var vm = Vm();
            Assert.Contains("_lastSent = new PromptPayload { Text = text, ImagesBase64 = images, Files = files, DisplayText = displayText, Mentions = mentions };", vm);
            Assert.Contains("_lastSentSlash = slashChip; _lastSentArgs = commandArgs;", vm);
            Assert.Contains("CardForFailure(rr, _lastSent)", vm);
            Assert.DoesNotContain("new PromptPayload { Text = displayText }", vm);
        }

        [Fact]
        public void SendAgainAndTryAgain_ResendLikeTheSignInPath()
        {
            var vm = Vm();
            Assert.DoesNotContain("ChatSend(retry.Text, retry.ImagesBase64, retry.Files);", vm);
            Assert.Equal(2, Regex.Matches(vm, @"=> Resend\(retry, slash, args\)").Count);   // the 409 card and the credit card
            Assert.Contains("ChatSend(p.Text, p.ImagesBase64, p.Files, slash, args, displayText: p.DisplayText, mentions: p.Mentions);", vm);
        }

        // r25 verifier: the expired sign-in card and the Unreachable/HostBlocked "Try again" kept the prompt but not the slash command
        // or its inputs, so the retry sent plain text. They now keep what Resend uses, and both retries go through Resend.
        [Fact]
        public void SignInAndTryAgain_KeepTheSlashCommandAndItsInputs()
        {
            var vm = Vm();
            Assert.Equal(2, Regex.Matches(vm, @"_pendingAfterSignIn = retry; _pendingSlashAfterSignIn = slash; _pendingArgsAfterSignIn = args;").Count);
            Assert.Contains("_pendingSlashAfterSignIn = slashChip; _pendingArgsAfterSignIn = commandArgs;", vm);
            Assert.Equal(2, Regex.Matches(vm, @"Resend\(pending, slash, args\);").Count);   // after sign-in, and the Attention card's Try again
            Assert.DoesNotContain("ChatSend(pending.Text", vm);
            Assert.Equal(2, Regex.Matches(vm, @"_pendingAfterSignIn = null; _pendingSlashAfterSignIn = null; _pendingArgsAfterSignIn = null;").Count);
        }
    }
}
