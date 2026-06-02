using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using BinaVibe.Mcp.Tools;

namespace Tests
{
    public class BatchRefResolverTests
    {
        [Fact]
        public void ResolveRefs_substitutes_prior_step_output()
        {
            var results = new List<Dictionary<string, object?>>
            {
                new() { ["created_id"] = 1234L },   // step 0 output
            };
            var args = JsonDocument.Parse("{\"host_wall_id\":\"$0.created_id\",\"x\":5}").RootElement;
            var resolved = BatchRefResolver.ResolveRefs(args, results);
            Assert.Equal("1234", resolved["host_wall_id"]?.ToString());
            Assert.Equal("5", resolved["x"]?.ToString());
        }

        [Fact]
        public void ResolveRefs_passes_through_when_no_refs()
        {
            var args = JsonDocument.Parse("{\"level\":\"Aras 01\"}").RootElement;
            var resolved = BatchRefResolver.ResolveRefs(args, new List<Dictionary<string, object?>>());
            Assert.Equal("Aras 01", resolved["level"]?.ToString());
        }

        [Fact]
        public void ResolveRefs_leaves_dangling_ref_as_literal()
        {
            // $5 references a step that doesn't exist → passed through unchanged.
            var args = JsonDocument.Parse("{\"id\":\"$5.created_id\"}").RootElement;
            var resolved = BatchRefResolver.ResolveRefs(args, new List<Dictionary<string, object?>>());
            Assert.Equal("$5.created_id", resolved["id"]?.ToString());
        }
    }
}
