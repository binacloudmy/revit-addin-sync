// Operation receipt shape (bina-ai spec §8.3/§8.5, R1 Task 17).
//
// ReceiptShape is the Revit-free assembly of the immutable receipt the
// epilogue produces: identity (receipt/operation/job/document), pre/post
// revision, changed element ids, transaction names, status and the Undo
// group. One approved mutation pack = one operation = one Undo group whose
// size is the number of BinaVibe transactions it committed.

using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ReceiptShapeTests
    {
        private static Dictionary<string, object> Build(string status = "completed") =>
            ReceiptShape.Build(
                operationId: "op-abc", jobId: "run-1",
                documentFingerprint: "fp", preRevision: 3, postRevision: 5,
                added: new long[] { 10, 11 }, modified: new long[] { 5, 10 }, deleted: 1,
                txNames: new[] { "BinaVibe: create_wall", "BinaVibe: place_door" },
                status: status);

        [Fact]
        public void Receipt_BelongsToExactlyOneDocumentAndOperation()
        {
            var r = Build();
            Assert.Equal("op-abc", r["operation_id"]);
            Assert.Equal("run-1", r["job_id"]);
            Assert.Equal("fp", r["document_fingerprint"]);
            Assert.False(string.IsNullOrEmpty((string)r["receipt_id"]));
            Assert.Equal(3L, r["pre_revision"]);
            Assert.Equal(5L, r["post_revision"]);
        }

        [Fact]
        public void Receipt_CarriesChangedIds_AddedWinsOverModified_AndKeepsLegacyCounts()
        {
            var r = Build();
            Assert.Equal(new long[] { 10, 11 }, ((IEnumerable<long>)r["added_ids"]).ToArray());
            Assert.Equal(new long[] { 5 }, ((IEnumerable<long>)r["modified_ids"]).ToArray());
            Assert.Equal(1, r["deleted"]);
            Assert.Equal(2, r["added"]);      // legacy count keys stay for old cards
            Assert.Equal(1, r["modified"]);
        }

        [Fact]
        public void Receipt_OneOperation_IsOneUndoGroup_SizedByItsTransactions()
        {
            var r = Build();
            var undo = (Dictionary<string, object>)r["undo_group"];
            Assert.Equal("op-abc", undo["operation_id"]);
            Assert.Equal(2, undo["tx_count"]);
            Assert.Equal(2, ReceiptShape.UndoSteps(txCount: 2, hadTint: false));
            Assert.Equal(3, ReceiptShape.UndoSteps(txCount: 2, hadTint: true));
            Assert.Equal(1, ReceiptShape.UndoSteps(txCount: 0, hadTint: false)); // legacy: at least one
        }

        [Fact]
        public void Receipt_IdListsAreCapped_ButCountsAreExact()
        {
            var many = Enumerable.Range(1, 2000).Select(i => (long)i).ToArray();
            var r = ReceiptShape.Build("op", "job", "fp", 0, 1, many, new long[0], 0, new[] { "BinaVibe: x" }, "completed");
            Assert.Equal(ReceiptShape.MaxIds, ((IEnumerable<long>)r["added_ids"]).Count());
            Assert.Equal(2000, r["added"]);
            Assert.Equal(true, r["ids_truncated"]);
        }

        [Fact]
        public void PendingToolCall_ParsesOperationIdentity()
        {
            var v2 = System.Text.Json.JsonSerializer.Deserialize<PendingToolCall>(
                "{\"tool_call_id\":\"c1\",\"tool\":\"create_wall\",\"args\":{},\"mutate\":true," +
                "\"job_id\":\"run-1\",\"operation_id\":\"op-abc\"}")!;
            Assert.Equal("run-1", v2.JobId);
            Assert.Equal("op-abc", v2.OperationId);
        }
    }
}
