using System.Collections.Generic;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-535: the Da.1 ask hint travels from the issue row (a WPF type, so Windows CI only) into the prompt.</summary>
    public class Da1AskRowTests
    {
        [Fact]
        public void The_ask_hint_of_a_row_reaches_the_prompt_through_the_issue_row()
        {
            var vm = new IssueVm
            {
                SpecCheckId = "Da.1", RevitCategory = "Stairs", Category = "Borang D", RenameFrom = "Stair 1", CodeHint = "type-code prefix TGc",
                ElementIds = new List<long> { 7 }, ElementLabels = new Dictionary<long, string> { [7] = "Stairs · Stair 1" },
            };
            var p = Assert.Single(JkrCopilotPrompt.Build(vm));
            Assert.Contains("-> ASK: type-code prefix TGc", p);
        }

        [Fact]
        public void A_derived_name_reaches_the_prompt_through_the_issue_row_and_no_hint_means_no_ask()
        {
            var vm = new IssueVm
            {
                SpecCheckId = "Da.1", RevitCategory = "Stairs", Category = "Borang D", RenameFrom = "Stair 1", RenameTo = "(TGc002) Stair 1",
                RenameSource = "the type's own Kod_Komponen_jkr_stt parameter holds the JKR code TGc002",
                ElementIds = new List<long> { 7 }, ElementLabels = new Dictionary<long, string> { [7] = "Stairs · Stair 1" },
            };
            var p = Assert.Single(JkrCopilotPrompt.Build(vm));
            Assert.Contains("\"Stair 1\" -> \"(TGc002) Stair 1\"", p);
            Assert.DoesNotContain("\"Stair 1\" -> ASK", p);
        }
    }
}
