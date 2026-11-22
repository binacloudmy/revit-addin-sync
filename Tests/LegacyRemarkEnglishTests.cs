using System;
using System.IO;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>
    /// AI-469 / AI-513: the legacy BIM010 checker (AuditCheckers.cs) writes its remarks in English, so the pane
    /// shows them as written (there is no BM template and no translation table any more). The checker's own
    /// templates are guarded here: a template changed back to BM, or lost, fails.
    /// </summary>
    public class LegacyRemarkEnglishTests
    {
        private static string Read(string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            Assert.True(dir != null, "repo root not found from " + AppContext.BaseDirectory);
            return File.ReadAllText(Path.Combine(dir.FullName, "BinaVibe", "Mcp", "Tools", "Audit", file)).Replace("\r\n", "\n");
        }

        [Fact]
        public void The_checker_writes_the_legacy_templates_in_English()
        {
            var src = Read("AuditCheckers.cs");
            foreach (var piece in new[]
            {
                "Rule: {rule}. Found 0 Area Plans in the model — generate the space / zone analysis ",
                "const string rule = \"at least one Area Plan for space / zone analysis\";",
                "No schedule name matches: ",
                "schedules in the model checked). ",
                "\"Generate those schedules.\"",
                "const string rule = \"every graphical view has a View Template\";",
                "views have no template (e.g. ",
                "from the architectural template.",
                "Create a template of that type first if wanted, then re-check.",
                "No {population} in the model, so the rule",
            })
                Assert.True(src.Contains(piece), "AuditCheckers.cs no longer contains: " + piece);
            Assert.Contains("\"The automatic check failed (\" + checker.Id + \") — check manually.\"", Read("AuditTools.cs"));
        }

        [Fact]
        public void The_template_availability_clause_is_English()
        {
            Assert.Contains("have no template", Read("AuditTemplateAvailability.cs"));
            Assert.Contains("can be fixed", Read("AuditTemplateAvailability.cs"));
        }
    }
}
