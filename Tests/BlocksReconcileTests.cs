using System.Collections.Generic;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>TurnBlocks.WithReplyTail — the completed-turn reconcile added
    /// after the 2026-08-20 JKR-audit defect, where a dead block feed left the
    /// pane showing "I'll r" while copy held the whole answer.</summary>
    public class BlocksReconcileTests
    {
        private static List<TurnBlock> Blocks(params string[] narratives)
        {
            var list = new List<TurnBlock>();
            int i = 0;
            foreach (var n in narratives)
                list.Add(new TurnBlock { Kind = TurnBlockKind.Narrative, SegmentId = "leg-" + (++i), Text = n });
            return list;
        }

        [Fact]
        public void TruncatedPrefix_GetsTailBlock()
        {
            var blocks = Blocks("I'll r");
            var full = "I'll run the audit now.\n\n## JKR Naming Audit — Result\n381 names audited.";
            var rec = TurnBlocks.WithReplyTail(blocks, full);
            Assert.NotNull(rec);
            Assert.Equal(2, rec.Count);
            Assert.Equal("reply-tail", rec[1].SegmentId);
            Assert.StartsWith("un the audit now.", rec[1].Text);
            Assert.Contains("381 names audited.", rec[1].Text);
        }

        [Fact]
        public void FullCoverage_Unchanged()
        {
            var blocks = Blocks("First leg. ", "Second leg.");
            var rec = TurnBlocks.WithReplyTail(blocks, "First leg. Second leg.");
            Assert.Same(blocks, rec);
        }

        [Fact]
        public void LegBreakWhitespaceOnlyDifference_Unchanged()
        {
            // The copy buffer holds "\n\n" between legs; the blocks don't.
            var blocks = Blocks("First leg.", "Second leg.");
            var rec = TurnBlocks.WithReplyTail(blocks, "First leg.\n\nSecond leg.\n");
            Assert.Same(blocks, rec);
        }

        [Fact]
        public void DivergedNarrative_ReturnsNull_ForPlainTextFallback()
        {
            var blocks = Blocks("Something entirely different");
            Assert.Null(TurnBlocks.WithReplyTail(blocks, "The real reply text."));
        }

        [Fact]
        public void ToolCardsDoNotCountAsNarrative()
        {
            var blocks = new List<TurnBlock>
            {
                new TurnBlock { Kind = TurnBlockKind.ToolCard,
                    ToolResult = new RevitWebAppSync.Services.ToolResultEvent { Tool = "audit_family_names" } },
                new TurnBlock { Kind = TurnBlockKind.Narrative, SegmentId = "leg-1", Text = "I'll r" },
            };
            var rec = TurnBlocks.WithReplyTail(blocks, "I'll run it.");
            Assert.NotNull(rec);
            Assert.Equal(3, rec.Count);   // toolcard + prefix + tail
        }

        [Fact]
        public void EmptyInputs_PassThrough()
        {
            Assert.Null(TurnBlocks.WithReplyTail(null, "x"));
            var empty = new List<TurnBlock>();
            Assert.Same(empty, TurnBlocks.WithReplyTail(empty, "x"));
            var blocks = Blocks("a");
            Assert.Same(blocks, TurnBlocks.WithReplyTail(blocks, ""));
        }

        [Fact]
        public void CurrentSegment_TracksTaggedDeltas()
        {
            var t = new TurnBlocks();
            t.ApplyReply("Leg one ", "leg-1");
            Assert.Equal("leg-1", t.CurrentSegment);
            t.ApplyReply("Leg two", "leg-2");
            Assert.Equal("leg-2", t.CurrentSegment);
            t.ApplyReply("more", "");         // untagged glue keeps the segment
            Assert.Equal("leg-2", t.CurrentSegment);
        }
    }
}
