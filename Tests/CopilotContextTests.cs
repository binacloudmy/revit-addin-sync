using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class CopilotContextTests
    {
        [Fact]
        public void ContextLabel_WithCommand_JoinsAllSegments()
        {
            CopilotContext.RevitVersion = "Revit 2024.2";
            var s = CopilotContext.ContextLabel("Create Walls");
            Assert.StartsWith("Auto-attached · Create Walls · Copilot ", s);
            Assert.EndsWith(" · Revit 2024.2", s);
        }

        [Fact]
        public void ContextLabel_NoCommand_OmitsSegment()
        {
            CopilotContext.RevitVersion = "Revit 2024.2";
            var s = CopilotContext.ContextLabel();
            Assert.StartsWith("Auto-attached · Copilot ", s);
            Assert.DoesNotContain("· ·", s);
        }

        [Fact]
        public void ShortLabel_HasBothVersions()
        {
            CopilotContext.RevitVersion = "Revit 2024.2";
            Assert.Contains("Copilot ", CopilotContext.ShortLabel);
            Assert.Contains("Revit 2024.2", CopilotContext.ShortLabel);
        }
    }
}
