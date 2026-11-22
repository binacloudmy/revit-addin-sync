using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-473, catalog side (WPF-free): the spec's severity per check. The spec has no
    /// severity column; its only severity word is KRITIKAL in a ✗ cell (C5.1.1).</summary>
    public class SpecSeverityCatalogTests
    {
        [Fact]
        public void A_check_is_critical_exactly_when_its_not_comply_cell_says_KRITIKAL()
        {
            foreach (var c in SpecCatalog.Checks)
                Assert.True(c.NotComply.En.Contains("CRITICAL") == (c.Severity == "critical"), c.Id + " severity vs text");
        }

        [Fact]
        public void C5_1_1_is_the_critical_check_and_every_severity_is_a_known_word()
        {
            Assert.Equal("critical", SpecCatalog.Find("C5.1.1").Severity);
            foreach (var c in SpecCatalog.Checks)
                Assert.Contains(c.Severity, new[] { "critical", "major", "minor" });
        }
    }
}
