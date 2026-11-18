#nullable enable
// 0.0.140 (0.0.139 dark pass): an ask card and the delete gate wear the "Copilot" mark row like every other answer; the gate says "Needs your OK" once (the
// turn's status chip; the in-card amber chip is gone, as the ask card has none). Presentation only: options, buttons, order and Cancel default are untouched.
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskGateConsistency0140Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void AskAndGateRows_WearTheCopilotMark()
        {
            Assert.Contains("if (m.Kind == CpMsgKind.AiReply || m.Kind == CpMsgKind.Clarify) aiRow.Children.Add(Controls.RunningCardView.CopilotLabel());", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
        }

        [Fact]
        public void TheGate_HasNoSecondNeedsYourOkChip()
        {
            var card = Src("UI/Copilot/Controls/WideWriteCard.cs");
            Assert.DoesNotContain("var chip = Eyebrow(m.Eyebrow);", card);
            Assert.Contains("Cp.Muted", card);                              // the title and detail are still drawn
            Assert.Contains("NeedsOk", Src("UI/Copilot/Model/TurnStatusText.cs"));   // the status chip still says it
        }
    }
}
