#nullable enable
// UX review Q8 (18 Nov): the header chip said "Needs input" while the card under it said "Needs your OK": two words for one state. The chip's
// colours still key on the state "Needs input"; its words follow the card.
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class StatusChipWords0138Tests
    {
        [Fact]
        public void NeedsInput_ReadsAsTheCardDoes()
        {
            Assert.Equal("Needs your OK", TurnStatusText.Of("Needs input", TurnStatusText.NeedsOk));
            Assert.Equal("Needs your answer", TurnStatusText.Of("Needs input", TurnStatusText.NeedsAnswer));
            Assert.Equal("Needs your answer", TurnStatusText.Of("Needs input", ""));
        }

        [Fact]
        public void EveryOtherState_IsItsOwnWords()
        {
            foreach (var s in new[] { "Ready", "Running", "Done", "Stopped", "Failed" })
                Assert.Equal(s, TurnStatusText.Of(s, TurnStatusText.NeedsOk));
            Assert.Equal("", TurnStatusText.Of(null, "x"));
        }

        [Fact]
        public void TheChip_ShowsTheWords_ButKeysItsColoursOnTheState()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Screens/ChatView.xaml"));
            Assert.Contains("Text=\"{Binding RunStatusText, Mode=OneWay}\"", xaml);
            Assert.Contains("<DataTrigger Binding=\"{Binding RunStatus}\" Value=\"Needs input\">", xaml);
            var vm = File.ReadAllText(Path.Combine(dir.FullName, "UI/Copilot/CopilotViewModel.cs"));
            Assert.Contains("rr.Ask.Wide != null ? Model.TurnStatusText.NeedsOk : Model.TurnStatusText.NeedsAnswer", vm);
        }
    }
}
