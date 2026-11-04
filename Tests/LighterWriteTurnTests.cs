// AI-217 (P2.2): fewer view exports on data-only writes, and a shorter
// idle watchdog only while a modal dialog is open.

using System.Collections.Generic;
using System.Text.Json;
using BinaVibe.Mcp;
using RevitWebAppSync.Services;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LighterWriteTurnTests
    {
        private static PendingToolCall Call(string tool, string argsJson, bool mutate = true) => new PendingToolCall
        {
            Tool = tool,
            Args = JsonDocument.Parse(argsJson).RootElement.Clone(),
            Mutate = mutate,
        };

        // ── ReceiptCapture.NeedsImages ──────────────────────────────────────

        [Theory]
        [InlineData("set_parameter_by_filter", "{\"parameter\":\"Fire Rating\",\"value\":\"60\"}")]   // A-06
        [InlineData("set_parameter_by_filter", "{\"parameter\":\"Comments\",\"value\":\"Semakan STR\"}")] // S-04
        [InlineData("set_parameter_bulk", "{\"param\":\"Sheet Issue Date\",\"value\":\"30.10.2026\"}")]   // D-03
        [InlineData("set_parameter_where", "{\"parameter\":\"Kod_Komponen_Sumber_jkr_stt\"}")]          // J-02
        [InlineData("set_revision_on_sheets", "{}")]
        public void DataOnlyWrites_SkipImages(string tool, string args)
        {
            Assert.False(ReceiptCapture.NeedsImages(new List<PendingToolCall> { Call(tool, args) }));
        }

        [Theory]
        [InlineData("create_grid", "{}")]                                                   // S-05
        [InlineData("tag_all_in_view", "{}")]                                               // A-09
        [InlineData("create_dimensions", "{}")]
        [InlineData("set_type_parameter", "{\"parameter\":\"Comments\"}")]                  // a type edit is never assumed data-only
        [InlineData("set_parameter_by_filter", "{\"parameter\":\"Width\",\"value\":\"1000\"}")]
        [InlineData("set_parameter_by_filter", "{\"parameter\":\"Mark\",\"value\":\"D1\"}")] // tags show Mark
        [InlineData("set_parameter_by_filter", "{\"value\":\"x\"}")]                        // unknown parameter
        [InlineData("renumber_elements", "{}")]
        [InlineData("delete_elements", "{}")]
        [InlineData("set_workset_bulk", "{}")]                                              // views can colour by workset
        [InlineData("set_parameter_by_filter", "{\"parameter\":\"Keynote\",\"value\":\"1\"}")] // keynote tags show it
        public void VisibleOrUnknownWrites_KeepImages(string tool, string args)
        {
            Assert.True(ReceiptCapture.NeedsImages(new List<PendingToolCall> { Call(tool, args) }));
        }

        [Fact]
        public void OneVisibleWriteInTheBatch_KeepsImages()
        {
            var batch = new List<PendingToolCall>
            {
                Call("set_parameter_by_filter", "{\"parameter\":\"Comments\",\"value\":\"x\"}"),
                Call("create_grid", "{}"),
            };
            Assert.True(ReceiptCapture.NeedsImages(batch));
        }

        [Fact]
        public void ReadsAreIgnored()
        {
            var batch = new List<PendingToolCall>
            {
                Call("list_levels", "{}", mutate: false),
                Call("set_parameter_by_filter", "{\"parameter\":\"Comments\",\"value\":\"x\"}"),
            };
            Assert.False(ReceiptCapture.NeedsImages(batch));
            Assert.False(ReceiptCapture.NeedsImages(new List<PendingToolCall> { Call("create_grid", "{}", mutate: false) }));
            Assert.False(ReceiptCapture.NeedsImages(null));
        }

        [Fact]
        public void DataParameterNames_AreCaseInsensitive()
        {
            Assert.True(ReceiptCapture.IsDataParameter("comments"));
            Assert.True(ReceiptCapture.IsDataParameter(" Fire Rating "));
            Assert.True(ReceiptCapture.IsDataParameter("kod_aset"));
            Assert.False(ReceiptCapture.IsDataParameter("Sill Height"));
            Assert.False(ReceiptCapture.IsDataParameter(""));
            Assert.False(ReceiptCapture.IsDataParameter(null));
        }

        // ── ReceiptCapture.ReportedExport ───────────────────────────────────

        [Fact]
        public void ReportedExport_ReadsTheJobsOwnTiming()
        {
            var r = ReceiptCapture.ReportedExport(new Dictionary<string, object> { ["export_ms"] = 1234.5, ["exports"] = 1 });
            Assert.NotNull(r);
            Assert.Equal(1234.5, r.Value.ms);
            Assert.Equal(1, r.Value.count);
            Assert.Null(ReceiptCapture.ReportedExport(new Dictionary<string, object> { ["ok"] = true }));
            Assert.Null(ReceiptCapture.ReportedExport(null));
        }

        // ── McpJob.DropIfNotStarted / AllToolChildrenDropped ────────────────

        [Fact]
        public void DropIfNotStarted_StaysTrueAfterAnEarlierDrop()
        {
            // Review fix 1: the Stop callback drops a grouped child first; the
            // runner then asks again and must still hear "never ran".
            var job = new McpJob { Tool = "create_grid" };
            Assert.True(job.TryAbandon());
            Assert.False(job.TryAbandon());          // the old check, which misread this
            Assert.True(job.DropIfNotStarted());
            Assert.True(job.DropIfNotStarted());
            Assert.False(job.TryStart());
        }

        [Fact]
        public void DropIfNotStarted_IsFalseOnceStarted()
        {
            var job = new McpJob { Tool = "create_grid" };
            Assert.True(job.TryStart());
            Assert.False(job.DropIfNotStarted());
            Assert.False(job.Abandoned);
        }

        private static McpJob Group(out McpJob a, out McpJob b, out McpJob receipt)
        {
            a = new McpJob { Tool = "create_grid" };
            b = new McpJob { Tool = "create_grid" };
            receipt = new McpJob { Tool = "__turn_receipt" };
            return new McpJob { Tool = "__undo_group", Children = new List<McpJob> { a, b, receipt } };
        }

        [Fact]
        public void AllToolChildrenDropped_OnlyWhenEveryToolCallWasDropped()
        {
            var g = Group(out var a, out var b, out var receipt);
            Assert.False(McpJob.AllToolChildrenDropped(g));
            a.TryAbandon();
            Assert.False(McpJob.AllToolChildrenDropped(g));   // b may still run
            b.TryAbandon();
            Assert.True(McpJob.AllToolChildrenDropped(g));    // receipt has nothing to report
        }

        [Fact]
        public void AllToolChildrenDropped_FalseWhenOneRan()
        {
            var g = Group(out var a, out var b, out var receipt);
            Assert.True(a.TryStart());
            b.TryAbandon();
            Assert.False(McpJob.AllToolChildrenDropped(g));
            Assert.False(McpJob.AllToolChildrenDropped(new McpJob { Tool = "list_levels" }));
            Assert.False(McpJob.AllToolChildrenDropped(null));
        }

        // ── PumpGrace.Decide ────────────────────────────────────────────────

        [Fact]
        public void IdleSinceArm_IsServiced()
        {
            Assert.Equal(PumpVerdict.Serviced, PumpGrace.Decide(true, true, 10_000).verdict);
        }

        [Fact]
        public void DialogAfterQueueing_FailsAfterTwoSeconds()
        {
            Assert.Equal(PumpVerdict.Wait, PumpGrace.Decide(false, true, 1_000).verdict);
            Assert.Equal(PumpVerdict.Fail, PumpGrace.Decide(false, true, 2_000).verdict);
        }

        [Fact]
        public void NoDialog_KeepsTheSixSecondGrace()
        {
            var at2 = PumpGrace.Decide(false, false, 2_000);
            Assert.Equal(PumpVerdict.Wait, at2.verdict);
            Assert.Equal(4_000, at2.next.TotalMilliseconds, 0);
            Assert.Equal(PumpVerdict.Wait, PumpGrace.Decide(false, false, 5_900).verdict);
            Assert.Equal(PumpVerdict.Fail, PumpGrace.Decide(false, false, 6_000).verdict);
        }

        [Fact]
        public void DialogSeenBeforeQueueing_KeepsSixSeconds()
        {
            // Review fix 4: a dialog seen before the job was queued may already
            // be closed (Revit just busy), so it doesn't shorten the grace.
            Assert.Equal(PumpVerdict.Wait, PumpGrace.Decide(false, false, 3_000).verdict);
            Assert.Equal(PumpVerdict.Fail, PumpGrace.Decide(false, false, 6_000).verdict);
            Assert.True(PumpGrace.DialogLikely(8_000));   // message still points at the dialog
        }

        [Fact]
        public void FirstLook_IsAtTheShortGrace()
        {
            // Armed just now: look again at 2 s, where an open dialog fails fast.
            var first = PumpGrace.Decide(false, false, 0);
            Assert.Equal(PumpVerdict.Wait, first.verdict);
            Assert.Equal(2_000, first.next.TotalMilliseconds, 0);
        }

        [Fact]
        public void DialogMessage_OnlyWhenRecent()
        {
            Assert.True(PumpGrace.DialogLikely(5_000));
            Assert.False(PumpGrace.DialogLikely(60_000));
        }
    }
}
