// Wire DTOs that outlived the old tool-calling loop (ToolLoopService /
// ToolLoopRunner, deleted with the protocol-3 rebuild, lean-harness spec §17).
// What is left is still referenced: FollowupAction (follow-up chips on the
// chat model), ToolResultEvent (stream-v2 tool cards and their tests) and
// PendingToolCall (pinned by the revision/receipt shape tests). Protocol 3's
// own wire types live in BinaVibe/Copilot/Contracts.

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitWebAppSync.Services
{
    // ─── Follow-up action chips (2026-08-02 offer_actions spec) ─────────────
    // {label, prompt}: Label is the pill text (already ≤32 chars, server-
    // truncated); Prompt is the full standalone request sent verbatim when
    // the pill is tapped. Shared verbatim from wire DTO through to the UI
    // model (ChatRouter.RouteResult / CopilotModels.ChatMessage) — same
    // pattern the pre-existing List<string> Followups used before this spec.
    public sealed class FollowupAction
    {
        public string Label { get; set; } = "";
        public string Prompt { get; set; } = "";
    }

    public sealed class PendingToolCall
    {
        [JsonPropertyName("tool_call_id")] public string ToolCallId { get; set; } = "";
        [JsonPropertyName("tool")] public string Tool { get; set; } = "";
        [JsonPropertyName("args")] public JsonElement Args { get; set; }
        [JsonPropertyName("idempotency_key")] public string IdempotencyKey { get; set; } = "";
        // True when this call MODIFIES the model (backend flags it from
        // MUTATE_TOOL_NAMES — pending batches mix reads and mutates in cloud
        // mode). Gates the Ya/Tidak confirmation card. Missing on older
        // backends → false → no gate, today's auto-execute behavior.
        [JsonPropertyName("mutate")] public bool Mutate { get; set; }
        // Action Mode addendum (2026-08-02): whether THIS call must be
        // approved even in Auto mode (serialize_pending's per-call flag —
        // always-confirm set: delete_elements, delete_unused_views,
        // purge_unused, workset mutations, execute_revit_batch; everything
        // else in MUTATE_TOOL_NAMES is auto-eligible). Default true —
        // fail-safe: a missing field (older backend) means "ask", never a
        // silent auto-run.
        [JsonPropertyName("requires_confirmation")] public bool RequiresConfirmation { get; set; } = true;
        // Document revision contract (bina-ai spec §8.4, R1 Task 16) — additive.
        // The backend stamps the revision it planned this MUTATION against; the
        // drainer compares it with the live document BEFORE opening a
        // transaction and answers stale_document on mismatch. Null from older
        // backends → no check (legacy behaviour).
        [JsonPropertyName("expected_revision")] public long? ExpectedRevision { get; set; }
        [JsonPropertyName("document_fingerprint")] public string DocumentFingerprint { get; set; }
        // Operation identity (spec §8.3): job = the run; operation = this
        // leg's approved mutation pack = one receipt = one Undo group.
        [JsonPropertyName("job_id")] public string JobId { get; set; }
        [JsonPropertyName("operation_id")] public string OperationId { get; set; }
    }

    // ─── Stream v2 tool_result frame (copilot-stream-v2-hermes-parity spec) ─
    // One per tool execution in the pre-protocol-3 stream; kept because saved
    // turns and the stream parser still carry it, rendered by ToolResultCard.
    public sealed class ToolResultEvent
    {
        [JsonPropertyName("tool_call_id")] public string ToolCallId { get; set; } = "";
        [JsonPropertyName("tool")] public string Tool { get; set; } = "";
        [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
        [JsonPropertyName("duration_ms")] public int? DurationMs { get; set; }
        [JsonPropertyName("args_digest")] public string ArgsDigest { get; set; } = "";
        [JsonPropertyName("result_digest")] public string ResultDigest { get; set; } = "";
        // Per-leg segment id (V2.1) — groups the card with the narrative leg
        // that announced the call. Null/empty from producers that don't tag.
        [JsonPropertyName("segment")] public string Segment { get; set; }

        // Wire digests arrive pre-clamped (backend hard-caps at 2KB); the SAME
        // budget applies to locally synthesized digests so both producers stay
        // within the frame-size acceptance bound.
        public const int DigestBudget = 2048;

        /// <summary>Clamp a digest string to the 2KB budget with an honest
        /// "…truncated" tail — never a silent cut.</summary>
        public static string Digest(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= DigestBudget ? s : s.Substring(0, DigestBudget) + "…truncated";
        }

        /// <summary>Header duration text ("0.4s"); empty when unknown.</summary>
        public string DurationLabel =>
            DurationMs == null ? "" : (DurationMs.Value / 1000.0).ToString("0.0") + "s";
    }
}
