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
            Assert.Equal(label, DesignFileRules.DescribeViewerState(state, null, null));
        }

        [Fact]
        public void Viewer_state_wins_over_an_old_Autodesk_urn()
        {
            Assert.Equal("not converted", DesignFileRules.DescribeViewerState("notConverted", "dXJuOmFkc2s=", null));
        }

        [Fact]
        public void An_older_server_falls_back_to_the_urn()
        {
            Assert.Equal("viewable", DesignFileRules.DescribeViewerState(null, "dXJuOmFkc2s=", null));
            Assert.Equal("not converted", DesignFileRules.DescribeViewerState(null, null, null));
            Assert.Equal("not converted", DesignFileRules.DescribeViewerState("", "", null));
        }

        // The Civil 3D add-in (ViewerStateLabel) keeps this rule; the two must agree.
        [Theory]
        [InlineData(null, null, "Completed", "viewable")]
        [InlineData("", null, "completed", "viewable")]
        [InlineData(null, "", "COMPLETED", "viewable")]
        [InlineData(null, "dXJuOmFkc2s=", "Failed", "viewable")]
        [InlineData(null, null, "Failed", "not converted")]
        [InlineData(null, null, "Pending", "not converted")]
        [InlineData(null, null, null, "not converted")]
        public void An_older_server_without_a_state_keeps_the_urn_and_xkt_rule(
            string state, string urn, string xkt, string label)
        {
            Assert.Equal(label, DesignFileRules.DescribeViewerState(state, urn, xkt));
        }

        [Theory]
        [InlineData("failed", "conversion failed")]
        [InlineData("notConverted", "not converted")]
        public void A_state_wins_over_a_completed_xkt_status(string state, string label)
        {
            Assert.Equal(label, DesignFileRules.DescribeViewerState(state, null, "Completed"));
        }

        [Fact]
        public void The_xkt_status_is_read_from_the_version_row()
        {
            var v = Newtonsoft.Json.JsonConvert.DeserializeObject<DesignVersion>(
                "{\"designId\":1,\"xktConversionStatus\":\"Completed\"}");
            Assert.Equal("viewable", DesignFileRules.DescribeViewerState(v.OdaViewerState, v.UrnInBase64, v.XktConversionStatus));
        }

        [Fact]
        public void An_unknown_state_says_nothing()
        {
            Assert.Equal("", DesignFileRules.DescribeViewerState("somethingNew", "dXJuOmFkc2s=", null));
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
        [InlineData(null, "UPPER.DWG", "dwg")]
        [InlineData(null, "Tower 1.5", "5")]
        [InlineData("  ", "Plan.dwg", "dwg")]
        [InlineData(".", "Plan.dwg", null)]
        public void Extension_comes_from_the_server_else_the_name(string fileExtension, string name, string expected)
        {
            Assert.Equal(expected, DesignFileRules.ExtensionOf(fileExtension, name));
        }

        [Theory]
        [InlineData("rvt", true)]
        [InlineData("rfa", true)]
        [InlineData("rte", true)]
        [InlineData("RVT", true)]
        [InlineData(null, true)]
        [InlineData("dwg", false)]
        [InlineData("DWG", false)]
        [InlineData("nwc", false)]
        [InlineData("nwd", false)]
        [InlineData("ifc", false)]
        // Not a known file type: part of a name like "Tower 1.5", so not refused.
        [InlineData("5", true)]
        [InlineData("final", true)]
        public void Only_known_non_Revit_files_are_refused(string extension, bool revit)
        {
            Assert.Equal(revit, DesignFileRules.IsRevitFile(extension));
        }

        [Theory]
        [InlineData("rvt", ".rvt")]
        [InlineData("rfa", ".rfa")]
        [InlineData("rte", ".rte")]
        [InlineData("RFA", ".rfa")]
        [InlineData(null, ".rvt")]
        // Whitelist: anything else, including server text with path characters, saves as .rvt.
        [InlineData("dwg", ".rvt")]
        [InlineData("5", ".rvt")]
        [InlineData("", ".rvt")]
        [InlineData("rvt\\..\\x", ".rvt")]
        [InlineData("r<v>t", ".rvt")]
        [InlineData("rvt|", ".rvt")]
        [InlineData("a\"b", ".rvt")]
        [InlineData("rvt\u0001", ".rvt")]
        public void Saved_under_a_whitelisted_extension(string extension, string expected)
        {
            Assert.Equal(expected, DesignFileRules.SaveExtension(extension));
        }

        [Theory]
        [InlineData("Tower.rvt", "Tower")]
        [InlineData("Tower.RVT", "Tower")]
        [InlineData("Family.rfa", "Family")]
        [InlineData("Plan.dwg", "Plan")]
        [InlineData("1.0 Plan & Profile REV 5C (1).dwg", "1.0 Plan & Profile REV 5C (1)")]
        [InlineData("bad|name<1>.nwc", "bad|name<1>")]
        [InlineData("a\"b?.rvt", "a\"b?")]
        [InlineData("tab\tname\u0001.rvt", "tab\tname\u0001")]
        [InlineData("no-extension", "no-extension")]
        [InlineData("Tower 1.5", "Tower 1.5")]
        [InlineData("Tower 1.5.rvt", "Tower 1.5")]
        [InlineData("trailing.", "trailing")]
        [InlineData("spaces .rvt", "spaces")]
        [InlineData(".env", ".env")]
        [InlineData("   ", "model")]
        [InlineData("", "model")]
        [InlineData(null, "model")]
        [InlineData(".rvt", ".rvt")]
        public void Stem_is_read_by_hand_and_never_throws(string name, string expected)
        {
            Assert.Equal(expected, DesignFileRules.StemOf(name));
        }

        [Theory]
        [InlineData("Tower 1.5", null, true, ".rvt")]
        [InlineData("Tower 1.5", "5", true, ".rvt")]
        [InlineData("Tower.RVT", null, true, ".rvt")]
        [InlineData("Family.rfa", null, true, ".rfa")]
        [InlineData("Plan.dwg", null, false, ".rvt")]
        [InlineData("bad|name<1>.nwc", null, false, ".rvt")]
        [InlineData("trailing.", null, true, ".rvt")]
        [InlineData("no-extension", null, true, ".rvt")]
        public void Name_to_download_decision_end_to_end(string name, string fileExtension, bool revit, string saveExtension)
        {
            string ext = DesignFileRules.ExtensionOf(fileExtension, name);
            Assert.Equal(revit, DesignFileRules.IsRevitFile(ext));
            Assert.Equal(saveExtension, DesignFileRules.SaveExtension(ext));
        }
    }
}
