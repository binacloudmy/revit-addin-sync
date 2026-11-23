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
    }
}
