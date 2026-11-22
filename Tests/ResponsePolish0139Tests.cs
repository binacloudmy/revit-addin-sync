#nullable enable
// Response-card polish (UX spec 18 Nov): presentation only; each pin names the control it touches.
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ResponsePolish0139Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheFooterIcons_HaveWords_AndTheFooterRepeatsNoTime()
        {
            var chat = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.Contains("\"Helpful\"", chat);
            Assert.Contains("\"Not helpful\"", chat);
            Assert.Contains("\"Copy answer\"", chat);
            Assert.Contains("AutomationProperties.SetName(btn, words)", chat);
            Assert.DoesNotContain("ProgressTrail.TotalElapsedText(m.Steps),\n                    FontSize = 10.5", chat.Replace("\r\n", "\n"));
        }

        [Fact]
        public void NextIsOnItsOwnLine_AndDontAnswerIsGhost_AndAQuestionHasNoExport()
        {
            var checks = Src("UI/Copilot/Controls/ChecksCard.cs");
            Assert.Contains("var block = new StackPanel", checks);
            Assert.Contains("block.Children.Add(label);", checks);
            Assert.Contains("chip.Margin = new Thickness(0, 0, 8, 8);", checks);
            Assert.Contains("CardKit.Ghost(_m.DeclineLabel", Src("UI/Copilot/Controls/AskCard.cs"));
            Assert.Contains("if (m.Kind == CpMsgKind.Clarify) evActions.Export = null;", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
        }
    }
}
