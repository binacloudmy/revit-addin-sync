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
            Assert.Null(new JkrLocateResult(JkrLocateKind.Shown, 1, "Wall").MessageIn("en"));
        }

        [Fact]
        public void A_type_issue_selects_its_instances()
        {
            Assert.Equal("This issue is on a type. Selected its 12 placed instances.",
                         new JkrLocateResult(JkrLocateKind.ShownTypeInstances, 12).MessageIn("en"));
            Assert.Equal("This issue is on a type. Selected its 1 placed instance.",
                         new JkrLocateResult(JkrLocateKind.ShownTypeInstances, 1).MessageIn("en"));
        }

        [Fact]
        public void A_type_without_instances_says_so()
        {
            Assert.Equal("This issue is on the type 'Generic - 200mm', which has no placed instances to show.",
                         new JkrLocateResult(JkrLocateKind.TypeNotPlaced, 0, "Generic - 200mm").MessageIn("en"));
        }

        [Fact]
        public void Hidden_in_the_view_names_the_view()
        {
            Assert.Equal("Selected, but it is hidden in the view '01 Utara (07d_Dokumen)' (hidden element or category).",
                         new JkrLocateResult(JkrLocateKind.HiddenInView, 1, "01 Utara (07d_Dokumen)").MessageIn("en"));
        }

        [Fact]
        public void Not_shown_passes_on_revits_reason()
        {
            Assert.Equal("Selected, but Revit could not show it: No good view",
                         new JkrLocateResult(JkrLocateKind.NotShown, 1, "No good view").MessageIn("en"));
            Assert.Equal("Selected, but Revit could not find a view that shows it.",
                         new JkrLocateResult(JkrLocateKind.NotShown, 1, null).MessageIn("en"));
        }

        [Theory]
        [InlineData(JkrLocateKind.Missing, "Element no longer exists in the model — try re-scanning.")]
        [InlineData(JkrLocateKind.NoDocument, "No active Revit document — open a model and try again.")]
        [InlineData(JkrLocateKind.NoElement, "This issue has no linked element to locate.")]
        public void Other_cases_keep_their_messages(JkrLocateKind kind, string expected)
        {
            Assert.Equal(expected, new JkrLocateResult(kind).MessageIn("en"));
        }

        [Fact]
        public void A_view_issue_opens_the_view_and_says_which_of_many()
        {
            Assert.Equal("Opened the view 'Aras 01 (01w_WIP)'.",
                         new JkrLocateResult(JkrLocateKind.OpenedView, 1, "Aras 01 (01w_WIP)").MessageIn("en"));
            Assert.Equal("This issue lists 24 views. Opened the first: 'Aras 01 (01w_WIP)'.",
                         new JkrLocateResult(JkrLocateKind.OpenedView, 24, "Aras 01 (01w_WIP)").MessageIn("en"));
        }

        [Fact]
        public void A_view_template_explains_where_to_find_it()
        {
            Assert.StartsWith("'AR - Plan' is a view template, which can't be opened.",
                              new JkrLocateResult(JkrLocateKind.ViewTemplate, 1, "AR - Plan").MessageIn("en"));
        }

        [Fact]
        public void An_element_without_geometry_is_selected_and_says_so()
        {
            Assert.Equal("'Project Information' has no geometry to show in a view. It is selected.",
                         new JkrLocateResult(JkrLocateKind.NoGeometry, 1, "Project Information").MessageIn("en"));
        }
    }
}
