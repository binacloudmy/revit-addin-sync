#nullable enable
// UX review Q7 (18 Nov): the composer hint was cut off ("paste or dro…") and "Reasoning" / "Free" were unexplained. Text and tooltips only.
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ComposerHint0138Tests
    {
        [Fact]
        public void TheHint_IsShortEnoughToFit_AndTheFullSentenceIsItsTooltip()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Controls/PromptBar.xaml"));
            Assert.Contains("<Run Text=\" to mention · paste or drop images\"/>", xaml);
            Assert.DoesNotContain("to pick a type, level, view or the selection · paste or drop images\"/>", xaml);
            Assert.Contains("ToolTip=\"Type / for tools. Type @ to pick a type, level, view or the selection. Paste or drop images to attach them.\"", xaml);
            Assert.Contains("ToolTip=\"Reasoning: show or hide the step-by-step timeline for the next turn\"", xaml);
            Assert.Contains("ToolTip=\"Your plan and usage (Free is the plan this account is on)\"", xaml);
        }
    }
}
