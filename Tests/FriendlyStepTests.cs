using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class FriendlyStepTests
    {
        [Theory]
        [InlineData("parse_request", "Understanding your request")]
        [InlineData("understand", "Understanding your request")]
        [InlineData("retrieve_context", "Looking through the model")]
        [InlineData("SEARCH_MODEL", "Looking through the model")]
        [InlineData("read_model", "Looking through the model")]
        [InlineData("plan", "Planning the approach")]
        [InlineData("reason", "Reasoning it through")]
        [InlineData("generate", "Putting together a response")]
        [InlineData("compose", "Putting together a response")]
        [InlineData("build_command", "Preparing the command")]
        [InlineData("validate", "Double-checking the result")]
        [InlineData("verify", "Double-checking the result")]
        [InlineData("thinking", "Thinking")]
        public void Label_MapsKnownKeys(string raw, string label) =>
            Assert.Equal(label, FriendlyStep.Label(raw));

        [Theory]
        [InlineData("optimize_layout", "Optimize layout")]
        [InlineData("resolveRefs", "Resolve refs")]
        [InlineData("warm-up", "Warm up")]
        public void Label_HumanisesUnknown(string raw, string label) =>
            Assert.Equal(label, FriendlyStep.Label(raw));

        [Fact]
        public void Label_Empty_ReturnsEmpty()
        {
            Assert.Equal("", FriendlyStep.Label(null));
            Assert.Equal("", FriendlyStep.Label("  "));
        }
    }
}
