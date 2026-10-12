using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    /// <summary>
    /// The Download dialog's and rollback picker's viewer-state label, and which
    /// files Revit may download (14zefhy1rfc).
    /// </summary>
    public class DesignFileRulesTests
    {
        [Theory]
        [InlineData("ready", "viewable")]
        [InlineData("partial", "partly viewable")]
        [InlineData("converting", "converting")]
        [InlineData("needsGlb", "preparing viewer")]
        [InlineData("failed", "conversion failed")]
        [InlineData("notConverted", "not converted")]
        public void Viewer_state_names_the_new_converter_state(string state, string label)
        {
            Assert.Equal(label, DesignFileRules.DescribeViewerState(state, null));
        }

        [Fact]
        public void Viewer_state_wins_over_an_old_Autodesk_urn()
        {
            Assert.Equal("not converted", DesignFileRules.DescribeViewerState("notConverted", "dXJuOmFkc2s="));
        }

        [Fact]
        public void An_older_server_falls_back_to_the_urn()
        {
            Assert.Equal("viewable", DesignFileRules.DescribeViewerState(null, "dXJuOmFkc2s="));
            Assert.Equal("not converted", DesignFileRules.DescribeViewerState(null, null));
            Assert.Equal("not converted", DesignFileRules.DescribeViewerState("", ""));
        }

        [Fact]
        public void An_unknown_state_says_nothing()
        {
            Assert.Equal("", DesignFileRules.DescribeViewerState("somethingNew", "dXJuOmFkc2s="));
        }

        [Theory]
        [InlineData("dwg", "anything.rvt", "dwg")]
        [InlineData("DWG", null, "dwg")]
        [InlineData(".ifc", null, "ifc")]
        [InlineData(null, "1.0 Plan & Profile REV 5C (1).dwg", "dwg")]
        [InlineData("", "Tower.RVT", "rvt")]
        [InlineData(null, "no-extension", null)]
        [InlineData(null, ".env", null)]
        [InlineData(null, "trailing.", null)]
        [InlineData(null, null, null)]
        [InlineData(null, "bad|name<1>.nwc", "nwc")]
        public void Extension_comes_from_the_server_else_the_name(string fileExtension, string name, string expected)
        {
            Assert.Equal(expected, DesignFileRules.ExtensionOf(fileExtension, name));
        }

        [Theory]
        [InlineData("rvt", true)]
        [InlineData("rfa", true)]
        [InlineData("rte", true)]
        [InlineData(null, true)]
        [InlineData("dwg", false)]
        [InlineData("nwc", false)]
        [InlineData("nwd", false)]
        [InlineData("ifc", false)]
        public void Only_Revit_files_download_into_Revit(string extension, bool revit)
        {
            Assert.Equal(revit, DesignFileRules.IsRevitFile(extension));
        }

        [Theory]
        [InlineData("rvt", ".rvt")]
        [InlineData("rfa", ".rfa")]
        [InlineData(null, ".rvt")]
        public void Saved_under_its_own_extension(string extension, string expected)
        {
            Assert.Equal(expected, DesignFileRules.SaveExtension(extension));
        }
    }
}
