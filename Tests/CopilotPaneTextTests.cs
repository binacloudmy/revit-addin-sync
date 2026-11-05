#nullable enable
// The pane's own words in one language, and the reply cut at a confirm card.

using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CopilotPaneTextTests
    {
        [Theory]
        [InlineData(413, "too large")]
        [InlineData(429, "busy")]
        [InlineData(502, "problem on its side")]
        [InlineData(404, "no longer has this turn")]
        [InlineData(422, "could not accept")]
        public void HttpFailure_IsShortAndBodyFree(int status, string fragment)
        {
            Assert.Contains(fragment, CopilotPaneText.HttpFailure(status, ReceiptLanguage.En));
            Assert.DoesNotContain("HTTP", CopilotPaneText.HttpFailure(status, ReceiptLanguage.Ms));
        }

        [Fact]
        public void EveryReason_HasBothLanguages_NeitherMixed()
        {
            foreach (var reason in new[]
            {
                TurnFailureReasons.HostNotAllowed, TurnFailureReasons.Unreachable, TurnFailureReasons.ResultsUndelivered,
                TurnFailureReasons.StreamLost, TurnFailureReasons.Cancelled, TurnFailureReasons.NotActive,
                TurnFailureReasons.ForeignToolRequest, TurnFailureReasons.BadFrame, TurnFailureReasons.UpdateRequired,
                TurnFailureReasons.TurnActive, TurnFailureReasons.SignedOut, TurnFailureReasons.Http,
            })
            {
                var en = CopilotPaneText.Failure(reason, 500, ReceiptLanguage.En);
                var ms = CopilotPaneText.Failure(reason, 500, ReceiptLanguage.Ms);
                Assert.False(string.IsNullOrWhiteSpace(en));
                Assert.False(string.IsNullOrWhiteSpace(ms));
                Assert.NotEqual(en, ms);
                Assert.DoesNotContain(" / ", en);
                Assert.DoesNotContain(" / ", ms);
            }
            Assert.Null(CopilotPaneText.Failure("something_new", null, ReceiptLanguage.En));
        }

        [Theory]
        [InlineData("thinking", "Reading the request", ReceiptLanguage.Ms, "Berfikir…")]
        [InlineData("thinking", "Reading the request", ReceiptLanguage.En, "Reading the request")]
        [InlineData("thinking", "", ReceiptLanguage.En, "Thinking…")]
        [InlineData("running", "Saya semak bilik di aras ini", ReceiptLanguage.Ms, "Saya semak bilik di aras ini")]
        [InlineData("running", "Saya semak bilik di aras ini", ReceiptLanguage.En, "Working…")]
        [InlineData("brand_new_kind", "Something", ReceiptLanguage.Ms, "Something")]
        [InlineData("brand_new_kind", "", ReceiptLanguage.Ms, "brand_new_kind")]
        public void ProgressLabel_KeepsTheCloudsTextOnlyInTheTurnsLanguage(string kind, string text, ReceiptLanguage lang, string expected)
        {
            Assert.Equal(expected, CopilotPaneText.ProgressLabel(kind, text, lang));
        }

        [Theory]
        [InlineData("Saya akan tukar mark.", 0, "Saya akan tukar mark.")]
        [InlineData("Saya akan tukar mark.\n\n4 pintu ditukar.", 21, "4 pintu ditukar.")]
        [InlineData("Saya akan tukar mark.", 21, "")]
        [InlineData("short", 50, "")]
        [InlineData(null, 3, "")]
        public void ReplySplit_After_ShowsOnlyWhatIsNew(string? full, int shown, string expected)
        {
            Assert.Equal(expected, ReplySplit.After(full, shown));
        }
    }
}
