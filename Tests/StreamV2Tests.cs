// Stream v2 — Hermes-parity rendering (docs/copilot-stream-v2-hermes-parity-spec.md).
// Pure-surface tests: the SSE parser additions (T2), the TurnBlocks reducer
// (T1) including the legacy feature-detect, the T3 headline-suppression rule,
// and the ToolResultEvent digest budget shared by both producers (T4/T6).

using System.Collections.Generic;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

public class StreamV2ParserTests
{
    [Fact]
    public void ReplyPartial_WithoutSegment_ParsesLegacy()
    {
        var c = AIServiceStreamExtensions.ParseEvent("reply_partial", "{\"delta\":\"hello \"}");
        Assert.Equal(StreamChunkKind.Reply, c.Kind);
        Assert.Equal("hello ", c.Delta);
        Assert.Equal("", c.Segment);
    }

    [Fact]
    public void ReplyPartial_WithSegment_CarriesTheLegId()
    {
        var c = AIServiceStreamExtensions.ParseEvent("reply_partial",
            "{\"delta\":\"hi\",\"segment\":\"leg-ab12cd-2\"}");
        Assert.Equal("leg-ab12cd-2", c.Segment);
    }

    [Fact]
    public void ToolResult_ParsesTypedEvent()
    {
        var raw = "{\"tool_call_id\":\"tc1\",\"tool\":\"set_section_box\",\"ok\":true," +
                  "\"duration_ms\":448,\"args_digest\":\"{}\",\"result_digest\":\"{\\\"ok\\\":true}\"," +
                  "\"segment\":\"leg-ab12cd-2\"}";
        var c = AIServiceStreamExtensions.ParseEvent("tool_result", raw);
        Assert.Equal(StreamChunkKind.ToolResult, c.Kind);
        Assert.NotNull(c.ToolResult);
        Assert.Equal("set_section_box", c.ToolResult.Tool);
        Assert.Equal("tc1", c.ToolResult.ToolCallId);
        Assert.True(c.ToolResult.Ok);
        Assert.Equal(448, c.ToolResult.DurationMs);
        Assert.Equal("leg-ab12cd-2", c.ToolResult.Segment);
        Assert.Equal("0.4s", c.ToolResult.DurationLabel);
    }

    [Fact]
    public void ToolResult_Garbage_FallsToUnknown_NeverThrows()
    {
        var c = AIServiceStreamExtensions.ParseEvent("tool_result", "not json");
        Assert.Equal(StreamChunkKind.Unknown, c.Kind);
    }
}

public class ToolResultEventDigestTests
{
    [Fact]
    public void Digest_PassesShortStringsThrough()
    {
        Assert.Equal("", ToolResultEvent.Digest(null));
        Assert.Equal("{\"a\":1}", ToolResultEvent.Digest("{\"a\":1}"));
    }

    [Fact]
    public void Digest_HardCapsAt2KbWithHonestTail()
    {
        var s = new string('x', 10_000);
        var d = ToolResultEvent.Digest(s);
        Assert.Equal(ToolResultEvent.DigestBudget + "…truncated".Length, d.Length);
        Assert.EndsWith("…truncated", d);
    }
}

public class TurnBlocksTests
{
    [Fact]
    public void LegacyDeltas_NeverActivate_NeverAccumulate()
    {
        var t = new TurnBlocks();
        Assert.False(t.ApplyReply("hello", null));
        Assert.False(t.ApplyReply("world", ""));
        Assert.False(t.Active);
        Assert.Empty(t.Blocks);
    }

    [Fact]
    public void FirstSegmentedDelta_IsTheFeatureDetect()
    {
        var t = new TurnBlocks();
        Assert.True(t.ApplyReply("I'll scope ", "leg-1"));
        Assert.True(t.Active);
        Assert.Single(t.Blocks);
        Assert.Equal(TurnBlockKind.Narrative, t.Blocks[0].Kind);
    }

    [Fact]
    public void SameSegment_Appends_NewSegment_OpensNewBlock()
    {
        var t = new TurnBlocks();
        t.ApplyReply("one ", "leg-1");
        t.ApplyReply("two", "leg-1");
        t.ApplyReply("three", "leg-2");
        Assert.Equal(2, t.Blocks.Count);
        Assert.Equal("one two", t.Blocks[0].Text);
        Assert.Equal("three", t.Blocks[1].Text);
    }

    [Fact]
    public void UntaggedToolResult_BeforeAnySegment_IsDropped_LegacySafety()
    {
        // ToolLoopRunner's local synthesis carries no segment — against an old
        // backend nothing ever activates, so the pane stays legacy.
        var t = new TurnBlocks();
        Assert.False(t.ApplyToolResult(new ToolResultEvent { Tool = "count_by" }));
        Assert.Empty(t.Blocks);
        Assert.False(t.Active);
    }

    [Fact]
    public void SegmentTaggedToolResult_ActivatesV2_EvenBeforeNarrative()
    {
        // A tool can complete before the first narrative leg streams; the
        // wire tool_result frame is itself a v2-only marker.
        var t = new TurnBlocks();
        Assert.True(t.ApplyToolResult(new ToolResultEvent { Tool = "count_by", Segment = "leg-x-1" }));
        Assert.True(t.Active);
        Assert.Single(t.Blocks);
    }

    [Fact]
    public void LegTail_ArrivingAfterToolCard_GluesToItsOwnLeg()
    {
        // The reply gate's 2-char holdback flushes a leg's tail AFTER the tool
        // events that followed it — the tail must land in leg-1's block, not a
        // stray fragment after the card.
        var t = new TurnBlocks();
        t.ApplyReply("I'll scope the vie", "leg-1");
        t.ApplyToolResult(new ToolResultEvent { Tool = "set_section_box", Ok = true });
        t.ApplyReply("w.", "leg-1");
        t.ApplyReply("Done.", "leg-2");
        Assert.Equal(3, t.Blocks.Count);
        Assert.Equal("I'll scope the view.", t.Blocks[0].Text);
        Assert.Equal(TurnBlockKind.ToolCard, t.Blocks[1].Kind);
        Assert.Equal("Done.", t.Blocks[2].Text);
    }

    [Fact]
    public void OrderedInterleave_NarrativeCardNarrative()
    {
        var t = new TurnBlocks();
        t.ApplyReply("Creating the 3D view first. ", "leg-1");
        t.ApplyToolResult(new ToolResultEvent { Tool = "create_3d_view", Ok = true, DurationMs = 500 });
        t.ApplyReply("Now the section box. ", "leg-2");
        t.ApplyToolResult(new ToolResultEvent { Tool = "set_section_box", Ok = true });
        t.ApplyReply("Done.", "leg-3");
        Assert.Equal(new[]
        {
            TurnBlockKind.Narrative, TurnBlockKind.ToolCard,
            TurnBlockKind.Narrative, TurnBlockKind.ToolCard,
            TurnBlockKind.Narrative,
        }, System.Linq.Enumerable.Select(t.Blocks, b => b.Kind));
    }

    [Fact]
    public void HeadlineSuppression_OnlyAfterCompletion_OnlyWhenActive_OnlyArrowRows()
    {
        var t = new TurnBlocks();
        // Inactive: nothing suppressed even after a completion — but the
        // pending slot IS consumed, so it can't go stale into the v2 phase.
        t.NoteToolCompletion();
        Assert.False(t.ShouldSuppressReasoning("n-aaaa1111", "Count by on doors → 5 rows"));

        t.ApplyReply("x", "leg-1");   // v2 engages
        // No completion pending -> the opening "Reading the request → …" row passes.
        Assert.False(t.ShouldSuppressReasoning("n-bbbb2222", "Reading the request → berapa pintu"));

        t.NoteToolCompletion();
        // Phase rows always pass.
        Assert.False(t.ShouldSuppressReasoning("n-phase-gather", ""));
        // The tool headline is consumed exactly once.
        Assert.True(t.ShouldSuppressReasoning("n-cccc3333", "Count by on doors → 5 rows"));
        Assert.False(t.ShouldSuppressReasoning("n-dddd4444", "Working notes → still fine"));
    }

    [Fact]
    public void ConfirmContinuity_ReseedsActive_AndRecordsDecision()
    {
        var prior = new List<TurnBlock>
        {
            new TurnBlock { Kind = TurnBlockKind.Narrative, SegmentId = "leg-1", Text = "Building walls. " },
        };
        var t = TurnBlocks.From(prior);
        Assert.True(t.Active);   // resumed stream keeps appending, same thread
        t.ApplyConfirm(true, "3 tindakan diluluskan");
        t.ApplyToolResult(new ToolResultEvent { Tool = "create_wall", Ok = true });
        Assert.Equal(3, t.Blocks.Count);
        Assert.Equal(TurnBlockKind.ConfirmCard, t.Blocks[1].Kind);
        Assert.True(t.Blocks[1].Approved);
    }

    [Fact]
    public void From_Null_IsInactiveEmpty()
    {
        var t = TurnBlocks.From(null);
        Assert.False(t.Active);
        Assert.Empty(t.Blocks);
    }
}
