#nullable enable
// Result pattern Task 8: the panel renders the result card and a quiet footer.
// WPF is not loaded in the harness, so these are source-text assertions
// (the pattern UpdateGatePaneTests uses).

using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ResultCardRenderTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }

        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        [Fact]
        public void EvidenceView_RendersTheResultCard()
        {
            var s = Src("UI/Copilot/Controls/TurnEvidenceView.cs");
            Assert.Contains("ResultCardView.Build(", s);
            Assert.Contains("ResultCardModel? card", s);
        }

        [Fact]
        public void CardView_ShowsCommitWarnings_OnChangesAndCreated()
        {
            // AI-332: overlap and duplicate warnings show on the card of a write that succeeded.
            var s = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.Contains("private static void CommitWarnings(", s);
            Assert.Contains("card.Warnings.Count > 0) line = \"Cp.AmberLn\"", s);
            var calls = System.Text.RegularExpressions.Regex.Matches(s, @"CommitWarnings\(root, card, lang, actions\);").Count;
            Assert.Equal(2, calls);
        }

        [Fact]
        public void ChatView_HasNoRatingBanner_AndNoActionCount()
        {
            var s = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            Assert.DoesNotContain("var nudge = BuildRatingNudge(", s);
            Assert.DoesNotContain("\" actions\"", s);
            Assert.DoesNotContain("Text = \"Useful?\"", s);
        }

        [Fact]
        public void Overlay_HasToastCalloutsAndLeaders()
        {
            var x = Src("UI/Copilot/Highlights/HighlightOverlayWindow.xaml");
            Assert.Contains("x:Name=\"UndoBtn\"", x);
            Assert.Contains("x:Name=\"CalloutLeft\"", x);
            Assert.Contains("x:Name=\"LeaderCanvas\"", x);
            var c = Src("UI/Copilot/Highlights/InModelResult.cs");
            Assert.Contains("IsPerspective", c);
            Assert.Contains("SettleMs = 150", c);
            Assert.Contains("FadeSeconds = 8", c);
        }
    }
}
