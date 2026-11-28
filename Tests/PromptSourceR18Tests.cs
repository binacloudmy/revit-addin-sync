#nullable enable
// r18 (coordinator, from the schedule fixer; live L3 aa736b6e "Source: Your prompt" in a Malay chat): "Your prompt" was hard-coded in
// SourcePanel and ChangesBody. One wording now, in the card's language, as CreatedSummary and ResultCardModel already had. The read-back
// prefix "Checked:" is untouched (the backend writes it and the green tick row keys on it).
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class PromptSourceR18Tests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "";
        }

        [Fact]
        public void PromptSource_FollowsTheCardsLanguage()
        {
            Assert.Equal("Your prompt", CreatedSummary.PromptSource(ReceiptLanguage.En));
            Assert.Equal("Prompt anda", CreatedSummary.PromptSource(ReceiptLanguage.Ms));
            Assert.Equal("Checked:", ChecksCardText.VerifiedPrefix);
        }

        [Fact]
        public void NoHardCodedYourPrompt_InTheSourceRowsAndPanel()
        {
            foreach (var f in new[] { "SourcePanel.cs", "ChangesBody.cs" })
            {
                var src = File.ReadAllText(Path.Combine(Root(), "UI", "Copilot", "Controls", f));
                Assert.DoesNotContain("\"Your prompt\"", src);
                Assert.Contains("CreatedSummary.PromptSource(lang)", src);
            }
        }
    }
}
