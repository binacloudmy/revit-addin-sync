// ConfirmGate + wire-contract tests for the mutate-confirmation (Ya/Tidak)
// flow. Pure logic — no WPF, no Revit. The rejected-result shape here is the
// client half of the contract locked by bina-ai's tests/test_mutate_confirm.py
// (ok=true + status "rejected" → backend keeps tool_call_error=false so the
// agent acknowledges instead of retrying).

using System.Collections.Generic;
using System.Text.Json;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ConfirmGateTests
    {
        private static PendingToolCall Call(string tool, bool mutate) =>
            new PendingToolCall { ToolCallId = "tc-" + tool, Tool = tool, Mutate = mutate };

        [Fact]
        public void RequiresConfirmation_TrueWhenAnyMutate()
        {
            var batch = new List<PendingToolCall> { Call("list_levels", false), Call("create_wall", true) };
            Assert.True(ConfirmGate.RequiresConfirmation(batch));
        }

        [Fact]
        public void RequiresConfirmation_FalseWhenAllReads()
        {
            var batch = new List<PendingToolCall> { Call("list_levels", false), Call("list_views", false) };
            Assert.False(ConfirmGate.RequiresConfirmation(batch));
        }

        [Fact]
        public void RequiresConfirmation_FalseOnNullOrEmpty()
        {
            Assert.False(ConfirmGate.RequiresConfirmation(null));
            Assert.False(ConfirmGate.RequiresConfirmation(new List<PendingToolCall>()));
        }

        [Fact]
        public void Rejected_BuildsOkTrueWithStatusRejected()
        {
            var res = ConfirmGate.Rejected(Call("delete_elements", true));

            Assert.Equal("tc-delete_elements", res.ToolCallId);
            Assert.True(res.Ok);   // a decline is a user decision, NOT a tool failure
            var dict = Assert.IsType<Dictionary<string, object>>(res.Result);
            Assert.Equal("rejected", dict["status"]);
            Assert.False(string.IsNullOrWhiteSpace((string)dict["reason"]));
        }

        [Fact]
        public void PendingToolCall_DeserializesMutateFlag()
        {
            const string json = @"{
                ""status"": ""awaiting_revit"",
                ""run_id"": ""r1"",
                ""pending_tool_calls"": [
                    { ""tool_call_id"": ""a"", ""tool"": ""create_wall"", ""args"": {}, ""mutate"": true },
                    { ""tool_call_id"": ""b"", ""tool"": ""list_levels"", ""args"": {}, ""mutate"": false },
                    { ""tool_call_id"": ""c"", ""tool"": ""old_backend_tool"", ""args"": {} }
                ]
            }";
            var turn = JsonSerializer.Deserialize<ToolTurn>(json);

            Assert.True(turn.AwaitingRevit);
            Assert.True(turn.Pending[0].Mutate);
            Assert.False(turn.Pending[1].Mutate);
            // Missing flag (older backend) → false → gate never trips.
            Assert.False(turn.Pending[2].Mutate);
        }

        [Fact]
        public void MixedBatch_GatesWholeBatch_AndRejectsEveryCall()
        {
            var batch = new List<PendingToolCall> { Call("list_levels", false), Call("set_parameter", true) };
            Assert.True(ConfirmGate.RequiresConfirmation(batch));

            // Tidak → every pending id gets a rejected result (resume requires a
            // result per id; reads riding the batch are declined with it).
            var results = new List<ToolResultDto>();
            foreach (var c in batch) results.Add(ConfirmGate.Rejected(c));
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.True(r.Ok));
            Assert.Equal(new[] { "tc-list_levels", "tc-set_parameter" },
                         new[] { results[0].ToolCallId, results[1].ToolCallId });
        }
    }
}
