// r17 (21 Nov, laptop round Z): live e8083027 "Create a door schedule for this view only" showed "Ran: ... (added 6, deleted 0; ...)" under
// Steps > Details (the tester read a deletion on a create); live 646bdf7f, a Malay turn, said "1 Schedule removed." after Undo.
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class StepDetailText0155Tests
    {
        [Fact]
        public void A_create_s_step_line_never_says_deleted_0_and_a_real_count_stays()
        {
            Assert.Equal("Ran: Create a door schedule for this view only (added 6; edits to existing elements are listed under changes).",
                StepDetailText.Of("Ran: Create a door schedule for this view only (added 6, deleted 0; edits to existing elements are listed under changes)."));
            Assert.Equal("Preview: x (adds 1; edits to existing elements are listed under changes). Nothing has changed yet.",
                StepDetailText.Of("Preview: x (adds 1, deletes 0; edits to existing elements are listed under changes). Nothing has changed yet."));
            Assert.Equal("Ran: x (deleted 2; edits to existing elements are listed under changes).",
                StepDetailText.Of("Ran: x (added 0, deleted 2; edits to existing elements are listed under changes)."));
            Assert.Equal("Ran: x (added 3, deleted 1; edits).", StepDetailText.Of("Ran: x (added 3, deleted 1; edits)."));
            Assert.Equal("", StepDetailText.Of(null));
        }

        [Fact]
        public void A_malay_undo_notice_names_the_categories_in_malay()
        {
            Assert.Equal("1 jadual", StepDetailText.MalayNames("1 Schedule"));
            Assert.Equal("3 helaian · 1 aras · 2 Room Separation Lines", StepDetailText.MalayNames("3 Sheets · 1 Level · 2 Room Separation Lines"));
        }
    }
}
