// AI-200: Stop really stops. Pure parts only — the not-run result the
// backend gets for calls Stop kept from running, the drafter's line, and the
// job claim that decides "run whole" vs "never started".

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinaVibe.Mcp;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class StopReportTests
    {
        private static PendingToolCall Call(string id, bool mutate) =>
            new PendingToolCall { ToolCallId = id, Tool = mutate ? "create_wall" : "list_levels", Mutate = mutate };

        private static ToolResultDto Ok(string id) =>
            new ToolResultDto { ToolCallId = id, Ok = true, Result = new Dictionary<string, object> { ["ok"] = true } };

        [Fact]
        public void NotRun_IsAUserDecisionNotAnError()
        {
            var r = StopReport.NotRun(Call("tc1", true));
            Assert.Equal("tc1", r.ToolCallId);
            Assert.True(r.Ok);   // agent acknowledges, does not self-heal/retry
            var d = (Dictionary<string, object>)r.Result;
            Assert.Equal("rejected", d["status"]);
            Assert.Contains("NOT executed", (string)d["reason"]);
            Assert.True(StopReport.IsNotRun(r));
            Assert.False(StopReport.IsNotRun(Ok("tc1")));
        }

        [Fact]
        public void Complete_KeepsRealResults_FillsTheRestAsNotRun()
        {
            var pending = new List<PendingToolCall> { Call("a", true), Call("b", true), Call("c", false) };
            var all = StopReport.Complete(pending, new List<ToolResultDto> { Ok("a") });
            Assert.Equal(new[] { "a", "b", "c" }, all.Select(r => r.ToolCallId));
            Assert.False(StopReport.IsNotRun(all[0]));
            Assert.True(StopReport.IsNotRun(all[1]));
            Assert.True(StopReport.IsNotRun(all[2]));
            Assert.True(StopReport.AnyRan(all));
        }

        [Fact]
        public void StopAfterTheBatchRan_ReportKeepsTheResultThatLanded()
        {
            // X-01 on the P2.2 branch: the one grouped write finished and its
            // receipt was folded in; Stop then landed during the resume leg.
            // The stop report must carry that real result, receipt included,
            // not "rejected" and not nothing, so "did it work?" can read it back.
            var pending = new List<PendingToolCall> { Call("tc1", true) };
            var results = new List<ToolResultDto> { Ok("tc1") };
            var receipt = new Dictionary<string, object> { ["receipt_id"] = "r1", ["modified"] = 85 };
            Assert.Equal(0, ReceiptShape.FoldIntoLastMutate(pending, results, receipt));

            var all = StopReport.Complete(pending, results);
            Assert.Single(all);
            Assert.False(StopReport.IsNotRun(all[0]));
            Assert.True(all[0].Ok);
            var d = (Dictionary<string, object>)all[0].Result;
            Assert.Same(receipt, d["turn_receipt"]);
            Assert.True(StopReport.AnyRan(all));
            Assert.Contains("1 tindakan sudah siap", StopReport.Message(pending, all));
        }

        [Fact]
        public void Complete_WithNothingDone_RejectsWholeBatch()
        {
            var pending = new List<PendingToolCall> { Call("a", true), Call("b", false) };
            var all = StopReport.Complete(pending, null);
            Assert.Equal(2, all.Count);
            Assert.All(all, r => Assert.True(StopReport.IsNotRun(r)));
            Assert.False(StopReport.AnyRan(all));
        }

        [Fact]
        public void Message_NothingRan_SaysNoChange()
        {
            var pending = new List<PendingToolCall> { Call("a", true), Call("b", true) };
            var msg = StopReport.Message(pending, StopReport.Complete(pending, null));
            Assert.Equal("Dihentikan. 2 tindakan tidak dijalankan — tiada perubahan dibuat pada model.", msg);
        }

        [Fact]
        public void Message_ChangeLanded_SaysItStays()
        {
            var pending = new List<PendingToolCall> { Call("a", true), Call("b", true) };
            var msg = StopReport.Message(pending, StopReport.Complete(pending, new List<ToolResultDto> { Ok("a") }));
            Assert.Contains("1 tindakan sudah siap dalam Revit sebelum Stop", msg);
            Assert.Contains("1 tindakan lagi tidak dijalankan", msg);
        }

        [Fact]
        public void Message_FailedMutate_AsksToCheckModel()
        {
            var pending = new List<PendingToolCall> { Call("a", true) };
            var failed = new ToolResultDto { ToolCallId = "a", Ok = false, Error = "Revit did not finish create_wall within 45s" };
            var msg = StopReport.Message(pending, new List<ToolResultDto> { failed });
            Assert.Contains("1 tindakan gagal atau tidak sempat siap", msg);
            Assert.DoesNotContain("sudah siap", msg);
        }

        [Fact]
        public void Message_ReadsThatRan_AreNotChanges()
        {
            var pending = new List<PendingToolCall> { Call("a", false) };
            Assert.Equal("Dihentikan.", StopReport.Message(pending, new List<ToolResultDto> { Ok("a") }));
        }

        [Fact]
        public void Job_AbandonedBeforeStart_NeverStarts()
        {
            var job = new McpJob { Tool = "create_wall" };
            Assert.True(job.TryAbandon());
            Assert.True(job.Abandoned);
            Assert.False(job.TryStart());   // drainer skips it
        }

        [Fact]
        public void Job_StartedFirst_CannotBeAbandoned()
        {
            var job = new McpJob { Tool = "create_wall" };
            Assert.True(job.TryStart());
            Assert.False(job.TryAbandon());   // Stop waits for it instead
            Assert.False(job.Abandoned);
        }

        [Fact]
        public void Job_StartAndAbandonRace_ExactlyOneWins()
        {
            for (int i = 0; i < 2000; i++)
            {
                var job = new McpJob { Tool = "create_wall" };
                bool started = false, abandoned = false;
                Parallel.Invoke(() => started = job.TryStart(), () => abandoned = job.TryAbandon());
                Assert.True(started ^ abandoned);
            }
        }
    }
}
