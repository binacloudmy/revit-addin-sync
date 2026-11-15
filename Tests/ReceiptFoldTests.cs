// Turn-receipt fold (AI-208). The receipt must reach the backend even when
// the last write of the pack failed: a write can commit and THEN throw
// (S-06: dimension_grids placed 2 dimensions, then a post-commit
// Regenerate() threw), and that is exactly when the drafter needs to hear
// what Revit changed.

using System.Collections.Generic;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ReceiptFoldTests
    {
        private static PendingToolCall Call(bool mutate) => new PendingToolCall { Tool = "dimension_grids", Mutate = mutate };

        private static Dictionary<string, object> Receipt() => new Dictionary<string, object>
        {
            ["added"] = 2,
            ["by_category"] = new Dictionary<string, int> { ["Dimensions"] = 2 },
        };

        [Fact]
        public void FailedLastWrite_WithNoResult_StillCarriesTheReceipt()
        {
            var pending = new List<PendingToolCall> { Call(mutate: true) };
            var results = new List<ToolResultDto>
            {
                new ToolResultDto { ToolCallId = "c1", Ok = false, Error = "Modification of the document is forbidden." },
            };
            var receipt = Receipt();

            Assert.Equal(0, ReceiptShape.FoldIntoLastMutate(pending, results, receipt));
            var body = Assert.IsAssignableFrom<IDictionary<string, object>>(results[0].Result);
            Assert.Same(receipt, body["turn_receipt"]);
            Assert.False(results[0].Ok);
            Assert.Equal("Modification of the document is forbidden.", results[0].Error);
        }

        [Fact]
        public void OkWrite_KeepsItsResult_AndGainsTheReceipt()
        {
            var pending = new List<PendingToolCall> { Call(mutate: true) };
            var result = new Dictionary<string, object> { ["ok"] = true, ["created"] = 2 };
            var results = new List<ToolResultDto> { new ToolResultDto { ToolCallId = "c1", Ok = true, Result = result } };

            Assert.Equal(0, ReceiptShape.FoldIntoLastMutate(pending, results, Receipt()));
            Assert.Same(result, results[0].Result);
            Assert.Equal(2, result["created"]);
            Assert.True(result.ContainsKey("turn_receipt"));
        }

        [Fact]
        public void Receipt_GoesOnTheLastWrite_NotOnATrailingRead()
        {
            var pending = new List<PendingToolCall> { Call(true), Call(true), Call(false) };
            var results = new List<ToolResultDto>
            {
                new ToolResultDto { Ok = true, Result = new Dictionary<string, object>() },
                new ToolResultDto { Ok = false, Error = "threw" },
                new ToolResultDto { Ok = true, Result = new Dictionary<string, object>() },
            };

            Assert.Equal(1, ReceiptShape.FoldIntoLastMutate(pending, results, Receipt()));
            Assert.False(((IDictionary<string, object>)results[0].Result).ContainsKey("turn_receipt"));
            Assert.False(((IDictionary<string, object>)results[2].Result).ContainsKey("turn_receipt"));
        }

        [Fact]
        public void NoWrite_OrNonDictionaryResult_IsLeftAlone()
        {
            var reads = new List<PendingToolCall> { Call(false) };
            var readResults = new List<ToolResultDto> { new ToolResultDto { Ok = true } };
            Assert.Equal(-1, ReceiptShape.FoldIntoLastMutate(reads, readResults, Receipt()));
            Assert.Null(readResults[0].Result);

            var writes = new List<PendingToolCall> { Call(true) };
            var stringResult = new List<ToolResultDto> { new ToolResultDto { Ok = true, Result = "done" } };
            Assert.Equal(-1, ReceiptShape.FoldIntoLastMutate(writes, stringResult, Receipt()));
            Assert.Equal("done", stringResult[0].Result);
        }

        [Fact]
        public void PackStatus_IsPartialWhenAnyWriteFailed()
        {
            var pending = new List<PendingToolCall> { Call(true), Call(false) };
            Assert.Equal("completed", ReceiptShape.PackStatus(pending, new List<ToolResultDto>
            {
                new ToolResultDto { Ok = true }, new ToolResultDto { Ok = false },   // failed read only
            }));
            Assert.Equal("partial", ReceiptShape.PackStatus(pending, new List<ToolResultDto>
            {
                new ToolResultDto { Ok = false }, new ToolResultDto { Ok = true },
            }));
        }
    }
}
