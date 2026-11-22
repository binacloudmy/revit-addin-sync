using System.Collections.Generic;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-513: the JKR pane is English-only; every check's Copilot prompt tells the engine to ask and reply in English. Revit- and WPF-free.</summary>
    public class JkrCopilotEnglishOnlyTests
    {
        [Theory]
        [InlineData("E.1")]
        [InlineData("Da.1")]
        [InlineData("C1.6.3")]
        public void Prompt_pins_english(string check)
        {
            var facts = new CopilotPromptFacts { CheckId = check, Title = "t", ElementIds = new List<long> { 101 } };
            var prompts = JkrCopilotPrompt.Build(facts);
            Assert.NotEmpty(prompts);
            foreach (var p in prompts)
                Assert.Contains("Write every ask_user question and your reply in English, even when view names are Malay.", p);
        }
    }
}
