using System.IO;
using RevitWebAppSync.Services;
using Xunit;

namespace Tests
{
    public class AiServiceUrlTests
    {
        [Fact]
        public void Build_uses_agents_prefix()
        {
            Assert.Equal(
                "http://x/agents/revit-ai/route",
                AiUrl.Build("http://x", "route"));
        }

        [Fact]
        public void Build_keeps_subpath()
        {
            Assert.Equal(
                "https://h/agents/revit-ai/commands/abc",
                AiUrl.Build("https://h", "commands/abc"));
        }

        [Fact]
        public void AIService_source_has_no_old_api_prefix()
        {
            var here = Path.GetDirectoryName(
                typeof(AiServiceUrlTests).Assembly.Location);
            var src = Path.GetFullPath(Path.Combine(
                here, "..", "..", "..", "..", "Services", "AIService.cs"));
            var text = File.ReadAllText(src);
            Assert.DoesNotContain("/api/revit-ai", text);
            Assert.Contains("/agents/revit-ai", text);
        }
    }
}
