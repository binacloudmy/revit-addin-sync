#nullable enable
// Hand test 17 Nov (turn 7cd54ec1): the user typed "Find the doors with Mark 349..." into the composer while the "Which view?" question was open; the
// router sent it as THE QUESTION'S ANSWER. The protocol stays; the composer now says what typing does while a question is open, and the card's own
// "Don't answer" is the way out.
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskTypedHint0121Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void AnOpenQuestion_SaysThatTypingAnswersIt_AndNamesTheWayOut()
        {
            var ask = new AskCardModel { Question = "Which view?", Language = ReceiptLanguage.En, DeclineLabel = "Don't answer" };
            Assert.Equal("This will answer the question above. Press \"Don't answer\" to ask something else.", AskCardModel.TypedAnswerHint(ask));
        }

        [Fact]
        public void InMalay_AndWithTheCardsOwnDeclineLabel()
        {
            var ask = new AskCardModel { Language = ReceiptLanguage.Ms, DeclineLabel = "Jangan jawab" };
            Assert.Equal("Ini akan menjawab soalan di atas. Tekan \"Jangan jawab\" untuk bertanya perkara lain.", AskCardModel.TypedAnswerHint(ask));
            Assert.Contains("\"Don't answer\"", AskCardModel.TypedAnswerHint(new AskCardModel { Language = ReceiptLanguage.En }));   // no label set: the default
        }

        [Fact]
        public void NoHint_WhenThereIsNoQuestion_ItIsAnswered_OrItTakesNoTypedAnswer()
        {
            Assert.Equal("", AskCardModel.TypedAnswerHint(null));
            Assert.Equal("", AskCardModel.TypedAnswerHint(new AskCardModel { AnsweredSummary = "Level 1" }));
            Assert.Equal("", AskCardModel.TypedAnswerHint(new AskCardModel { AsksForCredential = true }));   // typed text is refused there
        }

        [Fact]
        public void TheWiring_ComposerRow_ViewModelLine_AndTheChatViewCall()
        {
            Assert.Contains("x:Name=\"AskHint\"", Src("UI/Copilot/Controls/PromptBar.xaml"));
            Assert.Contains("public void SetAskHint(string text)", Src("UI/Copilot/Controls/PromptBar.xaml.cs"));
            Assert.Contains("public string AskHint => Model.AskCardModel.TypedAnswerHint(TurnRouter?.PendingAsk);", Src("UI/Copilot/CopilotViewModel.cs"));
            Assert.Contains("Prompt?.SetAskHint(Vm.AskHint);", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
        }

        [Fact]
        public void TheTurnStart_ReReadsTheViewWhereTheToolsCan()
        {
            Assert.Contains("_tools is ISnapshotRefresher fresh", Src("UI/Copilot/CopilotTurnRouter.cs"));
            Assert.Contains("RevitThread.RunAsync(app => SnapshotService.RefreshViewNow(app), ct)", Src("UI/Copilot/PaneToolRunner.cs"));
            Assert.Contains("_viewDirty = true;\n            Refresh(app, force: false);", Src("BinaVibe/Copilot/Read/SnapshotService.cs").Replace("\r\n", "\n"));
        }
    }
}
