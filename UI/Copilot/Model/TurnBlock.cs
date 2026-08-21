using System;
using System.Collections.Generic;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Copilot.Model
{
    // ─── Stream v2 segmented turn body (copilot-stream-v2-hermes-parity spec, T1) ──
    // A v2 turn renders as an ORDERED list of blocks instead of one growing
    // text blob: narrative legs, tool cards, and the confirm decision record
    // interleave in arrival order (the thinking card stays a single block
    // pinned first, outside this list). Legacy turns (no `segment` on any
    // reply_partial) never populate blocks — the pane keeps today's rendering
    // byte-identical.

    public enum TurnBlockKind { Narrative, ToolCard, ConfirmCard }

    public sealed class TurnBlock
    {
        public TurnBlockKind Kind;
        // Narrative: the leg's segment id + its accumulated prose.
        public string SegmentId;
        public string Text = "";
        // ToolCard: the per-execution result frame (wire or locally synthesized).
        public ToolResultEvent ToolResult;
        // ConfirmCard: the resolved decision record ("3 tindakan diluluskan").
        public bool? Approved;
    }

    /// <summary>
    /// Pure per-turn accumulator for the v2 block list — the segment twin of
    /// ReasoningReducer. Fed by the SSE layer (reply deltas + tool_result
    /// frames) and by ToolLoopRunner's local executions; no UI, no I/O, so the
    /// append/new-block rules are unit-testable.
    ///
    /// v2 feature-detect: the FIRST reply delta carrying a segment id flips
    /// <see cref="Active"/> for the turn. Until then every delta is ignored
    /// here (the legacy one-bubble path renders it), so an old backend leaves
    /// this object empty and the pane byte-identical to today.
    /// </summary>
    public sealed class TurnBlocks
    {
        public List<TurnBlock> Blocks { get; } = new List<TurnBlock>();

        /// <summary>True once any reply_partial carried a segment id this turn.</summary>
        public bool Active { get; private set; }

        /// <summary>Segment id of the most recent tagged reply delta — lets the
        /// SSE layer detect a leg boundary BEFORE appending to the flat copy
        /// buffer (replySb), so copied text gets a paragraph break between legs
        /// instead of the glued "…rename.The audit is complete." (2026-08-20).</summary>
        public string CurrentSegment { get; private set; } = "";

        // T3 thinking-card dedupe: tool completions whose reasoning-strip
        // headline should be suppressed when v2 cards are rendering (the card
        // carries the same information richer). Incremented by the SSE layer
        // on each tool done/error event, consumed by ShouldSuppressReasoning.
        private int _pendingHeadlines;

        /// <summary>Apply one reply_partial delta. Returns true when the block
        /// list changed (caller pushes a snapshot to the pane).</summary>
        public bool ApplyReply(string delta, string segment)
        {
            if (string.IsNullOrEmpty(delta)) return false;
            if (string.IsNullOrEmpty(segment))
            {
                // Old backend — or a stray untagged frame after v2 already
                // engaged: glue it to the last narrative rather than lose it.
                if (!Active) return false;
                var last = LastNarrative(null);
                if (last == null) return false;
                last.Text += delta;
                return true;
            }
            Active = true;
            CurrentSegment = segment;
            // The gate's 2-char holdback means a leg's TAIL delta can arrive
            // AFTER the tool cards that followed it — append to the existing
            // narrative block for that segment wherever it sits, never a new
            // fragment block.
            var existing = LastNarrative(segment);
            if (existing != null) { existing.Text += delta; return true; }
            Blocks.Add(new TurnBlock { Kind = TurnBlockKind.Narrative, SegmentId = segment, Text = delta });
            return true;
        }

        /// <summary>Append a tool card. A segment-tagged wire frame is itself a
        /// v2-only marker, so it activates segmented rendering (a tool can
        /// complete BEFORE the first narrative leg streams). An untagged event
        /// (ToolLoopRunner's local synthesis) still needs the turn already
        /// active — against an old backend the pane must stay legacy, so those
        /// are dropped. Returns true when a block was added.</summary>
        public bool ApplyToolResult(ToolResultEvent ev)
        {
            if (ev == null) return false;
            if (!Active && string.IsNullOrEmpty(ev.Segment)) return false;
            Active = true;
            Blocks.Add(new TurnBlock { Kind = TurnBlockKind.ToolCard, ToolResult = ev });
            return true;
        }

        /// <summary>Append the confirm decision record (T5) — called at
        /// resolution time so the record carries the outcome, never a pending
        /// state.</summary>
        public void ApplyConfirm(bool approved, string label)
        {
            Blocks.Add(new TurnBlock { Kind = TurnBlockKind.ConfirmCard, Approved = approved, Text = label ?? "" });
        }

        /// <summary>Note a tool completion whose reasoning-strip headline the
        /// v2 tool card supersedes (T3 dedupe).</summary>
        public void NoteToolCompletion() => _pendingHeadlines++;

        /// <summary>True when this reasoning frame is the tool headline of a
        /// just-completed call and v2 cards are rendering — the strip drops it
        /// (phases and notes always pass: they carry no " → " headline or no
        /// completion is pending). The pending slot is consumed even while
        /// inactive so a pre-activation completion can never leave a stale
        /// counter that suppresses a later non-headline row.</summary>
        public bool ShouldSuppressReasoning(string stepId, string textDelta)
        {
            if (_pendingHeadlines <= 0) return false;
            if ((stepId ?? "").StartsWith("n-phase-")) return false;
            if ((textDelta ?? "").IndexOf(" → ", StringComparison.Ordinal) < 0) return false;
            _pendingHeadlines--;
            return Active;
        }

        /// <summary>Immutable snapshot for the UI callback / persisted message.</summary>
        public List<TurnBlock> Snapshot() => new List<TurnBlock>(Blocks);

        /// <summary>Reconcile a completed turn's block list against the FULL
        /// reply text (defect 2026-08-20, JKR audit turn): the block feed can
        /// die mid-leg — a cut resume stream falls back to the blocking
        /// /tool/resume, which carries no block frames — leaving the blocks a
        /// strict PREFIX of the real reply while the copy button (m.Text) has
        /// it all. Returns:
        ///  · the blocks unchanged when their narrative already covers the reply,
        ///  · blocks + one appended tail narrative when they are a clean prefix,
        ///  · null when the narrative diverges from the reply — the caller must
        ///    then drop segmented rendering for this message and show the full
        ///    text (correct content beats segmentation).
        /// Pure — unit-testable.</summary>
        public static IReadOnlyList<TurnBlock> WithReplyTail(IReadOnlyList<TurnBlock> blocks, string fullReply)
        {
            if (blocks == null || blocks.Count == 0) return blocks;
            if (string.IsNullOrEmpty(fullReply)) return blocks;
            var sb = new System.Text.StringBuilder();
            foreach (var b in blocks)
                if (b.Kind == TurnBlockKind.Narrative) sb.Append(b.Text);
            var narrative = sb.ToString();
            // Whitespace-insensitive prefix walk: the flat copy buffer inserts
            // paragraph breaks at leg boundaries that the per-block text never
            // held, so an exact StartsWith would false-negative every
            // multi-leg turn.
            int cut = MatchPrefixIgnoringWhitespace(fullReply, narrative);
            if (cut < 0) return null;
            var tail = fullReply.Substring(cut);
            if (tail.Trim().Length == 0) return blocks;
            var list = new List<TurnBlock>(blocks)
            {
                new TurnBlock { Kind = TurnBlockKind.Narrative, SegmentId = "reply-tail", Text = tail.TrimStart('\n', '\r') },
            };
            return list;
        }

        // Index in `full` just past the content of `prefix`, treating runs of
        // whitespace on either side as equal; -1 when prefix is not a
        // (whitespace-insensitive) prefix of full.
        private static int MatchPrefixIgnoringWhitespace(string full, string prefix)
        {
            int i = 0, j = 0;
            while (j < prefix.Length)
            {
                if (char.IsWhiteSpace(prefix[j])) { j++; continue; }
                while (i < full.Length && char.IsWhiteSpace(full[i])) i++;
                if (i >= full.Length || full[i] != prefix[j]) return -1;
                i++; j++;
            }
            return i;
        }

        /// <summary>Reconstitute state carried across a confirm pause (T5) —
        /// prior blocks re-seed the list and v2 stays engaged so the resumed
        /// stream keeps appending to the same visual thread.</summary>
        public static TurnBlocks From(IReadOnlyList<TurnBlock> prior)
        {
            var t = new TurnBlocks();
            if (prior != null && prior.Count > 0)
            {
                t.Blocks.AddRange(prior);
                t.Active = true;
            }
            return t;
        }

        private TurnBlock LastNarrative(string segment)
        {
            for (int i = Blocks.Count - 1; i >= 0; i--)
            {
                var b = Blocks[i];
                if (b.Kind != TurnBlockKind.Narrative) continue;
                if (segment == null || b.SegmentId == segment) return b;
                // Only the MOST RECENT narrative can match — an older leg with
                // the same id would mean the backend reused ids, which it
                // never does (per-stream nonce); stop at the first narrative.
                return null;
            }
            return null;
        }
    }
}
