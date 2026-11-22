using System.Linq;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-493 (IssueVm / WPF half; not compiled on the Mac harness): the IssueVm overload fills the facts.</summary>
    public class JkrCopilotPromptIssueTests
    {
        private static IssueVm Row()
        {
            var r = new IssueVm { SpecCheckId = "Da.1", Title = "Generic name", RevitCategory = "Doors", Category = "Naming", Required = "JKR code", Actual = "Pintu 1", ActualEn = "Door 1" };
            r.AddElements(new long[] { 11, 12 }, "Doors · Door 1");
            return r;
        }

        [Fact]
        public void IssueVm_overload_uses_ids_labels_category_and_the_pane_language_for_Actual()
        {
            var en = Assert.Single(JkrCopilotPrompt.Build(Row(), false));
            Assert.Contains("Actual: Door 1", en);
            Assert.Contains("Category: Doors", en);
            Assert.Contains("- 11 — Doors · Door 1", en);
            Assert.DoesNotContain("- 12 ", en);   // Da.1 is type-level: ids 11 and 12 share one type, so one id is listed
            var bm = Assert.Single(JkrCopilotPrompt.Build(Row(), true));
            Assert.Contains("Sebenar: Pintu 1", bm);
        }

        [Fact]
        public void IssueVm_with_no_ids_gives_no_prompt()
        {
            var r = Row(); r.ElementIds.Clear();
            Assert.Empty(JkrCopilotPrompt.Build(r, false));
        }

        [Fact]
        public void FactsOf_and_Build_agree_so_the_confirm_lists_what_the_prompt_sends()
        {
            var facts = JkrCopilotPrompt.FactsOf(Row(), false);
            Assert.Equal(new long[] { 11, 12 }, facts.ElementIds);
            Assert.Equal(new long[] { 11 }, JkrCopilotPrompt.TargetIds(facts));
            var confirm = JkrCopilotConfirm.Build(facts, false);
            Assert.Equal("Let Copilot change 1 type (and all their instances) for check Da.1?", confirm.Heading);
            Assert.Contains("11 — Doors · Door 1", confirm.Details);
            Assert.DoesNotContain("12 —", confirm.Details);
        }
    }
}
