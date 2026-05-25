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
                "http://x/agents/revit-ai/generate",
                AiUrl.Build("http://x", "generate"));
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
