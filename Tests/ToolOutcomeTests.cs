// AI-55: a tool that fails with {ok:false} and no "error" key must not crash
// while the drainer records the ledger reason. ToolOutcome is the pure seam
// split out of McpExternalEventHandler for this coverage.

using System.Collections.Generic;
using BinaVibe.Mcp;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ToolOutcomeTests
    {
        [Fact]
        public void Failure_WithoutErrorKey_FallsBackWithoutThrowing()
        {
            var result = new Dictionary<string, object?> { ["ok"] = false };
            Assert.True(ToolOutcome.IsFailure(result));
            Assert.Equal("failed", ToolOutcome.FailureReason(result));   // was KeyNotFoundException
        }

        [Fact]
        public void Failure_WithErrorKey_UsesTheToolReason()
        {
            var result = new Dictionary<string, object?> { ["ok"] = false, ["error"] = "active view is not a plan view" };
            Assert.True(ToolOutcome.IsFailure(result));
            Assert.Equal("active view is not a plan view", ToolOutcome.FailureReason(result));
        }

        [Fact]
        public void AmbiguousRefusal_IsFailure_AndFallsBack()
        {
            var result = new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["ambiguous"] = true,
                ["note"] = "3 views match — ask the user",
            };
            Assert.True(ToolOutcome.IsFailure(result));
            Assert.Equal("failed", ToolOutcome.FailureReason(result));
        }

        [Fact]
        public void Success_IsNotFailure()
        {
            Assert.False(ToolOutcome.IsFailure(null));
            Assert.False(ToolOutcome.IsFailure(new Dictionary<string, object?> { ["ok"] = true }));
            Assert.False(ToolOutcome.IsFailure(new Dictionary<string, object?> { ["elements"] = new List<object>() }));   // no "ok" => success
        }
    }
}
