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
            Assert.Contains("- 12 — Doors · Door 1", en);
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
        public void A_create_type_row_with_no_ids_still_gives_one_prompt_AI498()
        {
            var r = new IssueVm { SpecCheckId = "C1.5.2", Title = "3D view", RevitCategory = "Views", Required = "3D view per level", Actual = "Level 3 has none" };
            var p = Assert.Single(JkrCopilotPrompt.Build(r, false));
            Assert.Contains("Create a 3D view for every level", p);
            Assert.Contains("Actual (what is missing): Level 3 has none", p);
        }
    }
}
