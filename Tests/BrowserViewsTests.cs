using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class BrowserViewsTests
    {
        [Theory]
        [InlineData("ProjectBrowser")] [InlineData("SystemBrowser")] [InlineData("Internal")] [InlineData("Undefined")]
        [InlineData("DrawingSheet")] [InlineData("Report")] [InlineData("CostReport")] [InlineData("SomethingNew")]
        public void The_browser_does_not_list_these(string type) => Assert.Null(BrowserViews.Place(type));

        [Fact]
        public void Views_keep_the_browsers_group_order()
        {
            var order = new[] { "FloorPlan", "CeilingPlan", "EngineeringPlan", "AreaPlan", "ThreeD", "Elevation", "Section", "Detail", "DraftingView", "Walkthrough", "Rendering" };
            for (int i = 1; i < order.Length; i++)
                Assert.True(BrowserViews.Place(order[i - 1]).Value.Rank < BrowserViews.Place(order[i]).Value.Rank, order[i]);
            Assert.All(order, t => Assert.Equal("Views", BrowserViews.Place(t).Value.Branch));
        }

        [Fact]
        public void Legends_and_schedules_are_their_own_branches()
        {
            Assert.Equal("Legends", BrowserViews.Place("Legend").Value.Branch);
            foreach (var t in new[] { "Schedule", "ColumnSchedule", "PanelSchedule" })
                Assert.Equal("Schedules/Quantities", BrowserViews.Place(t).Value.Branch);
        }
    }
}
