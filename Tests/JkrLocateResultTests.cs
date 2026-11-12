using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>"Locate in 3D" tells the user why when it can't simply show the
    /// element. Which case applies is decided in Revit (panel LocateInRevitAsync).</summary>
    public class JkrLocateResultTests
    {
        [Fact]
        public void Plain_success_says_nothing()
        {
            Assert.Null(new JkrLocateResult(JkrLocateKind.Shown, 1, "Wall").Message);
        }

        [Fact]
        public void A_type_issue_selects_its_instances()
        {
            Assert.Equal("This issue is on a type. Selected its 12 placed instances.",
                         new JkrLocateResult(JkrLocateKind.ShownTypeInstances, 12).Message);
            Assert.Equal("This issue is on a type. Selected its 1 placed instance.",
                         new JkrLocateResult(JkrLocateKind.ShownTypeInstances, 1).Message);
        }

        [Fact]
        public void A_type_without_instances_says_so()
        {
            Assert.Equal("This issue is on the type 'Generic - 200mm', which has no placed instances to show.",
                         new JkrLocateResult(JkrLocateKind.TypeNotPlaced, 0, "Generic - 200mm").Message);
        }

        [Fact]
        public void Hidden_in_the_view_names_the_view()
        {
            Assert.Equal("Selected, but it is hidden in the view '01 Utara (07d_Dokumen)' (hidden element or category).",
                         new JkrLocateResult(JkrLocateKind.HiddenInView, 1, "01 Utara (07d_Dokumen)").Message);
        }

        [Fact]
        public void Not_shown_passes_on_revits_reason()
        {
            Assert.Equal("Selected, but Revit could not show it: No good view",
                         new JkrLocateResult(JkrLocateKind.NotShown, 1, "No good view").Message);
            Assert.Equal("Selected, but Revit could not find a view that shows it.",
                         new JkrLocateResult(JkrLocateKind.NotShown, 1, null).Message);
        }

        [Theory]
        [InlineData(JkrLocateKind.Missing, "Element no longer exists in the model — try re-scanning.")]
        [InlineData(JkrLocateKind.NoDocument, "No active Revit document — open a model and try again.")]
        [InlineData(JkrLocateKind.NoElement, "This issue has no linked element to locate.")]
        public void Other_cases_keep_their_messages(JkrLocateKind kind, string expected)
        {
            Assert.Equal(expected, new JkrLocateResult(kind).Message);
        }
    }
}
