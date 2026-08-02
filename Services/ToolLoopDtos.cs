// Wire DTOs for the tunnel-free tool-calling loop (System.Text.Json so `args`
// deserialises straight to a JsonElement, which is exactly what McpJob.Args /
// ToolRegistry want). Split out of ToolLoopService.cs so the pure shapes can be
// compiled into the test project without dragging the HttpClient/BinaConfig
// dependencies along (Tests.csproj cherry-picks source files).

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitWebAppSync.Services
{
    public sealed class ToolTurn
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("run_id")] public string RunId { get; set; } = "";
        [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
        [JsonPropertyName("reply")] public string Reply { get; set; } = "";
        // A one-tap "next step" offer the model surfaced in its reply (e.g. "Nak
        // saya lanjutkan?"), extracted server-side from a trailing "Tindakan:"
        // line. Empty on older backends / turns with no offer — treat null/empty
        // as "no offer", never render buttons.
        [JsonPropertyName("tindakan")] public string Tindakan { get; set; } = "";
        // When the tool agent fell back to codegen (no tool fit), the done turn
        // carries the C# to run; empty when it answered in prose / via tools.
        [JsonPropertyName("code")] public string Code { get; set; } = "";
        [JsonPropertyName("is_query")] public bool IsQuery { get; set; } = true;
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("success")] public bool Success { get; set; } = true;
        [JsonPropertyName("pending_tool_calls")] public List<PendingToolCall> Pending { get; set; } = new();
        // Tools the agent ran SERVER-SIDE this turn (list_views, find_elements_by_filter,
        // …). These never come back as pending (they don't execute in Revit), so without
        // this the trace would be empty for any read/codegen request. Drives the step chips.
        [JsonPropertyName("tool_calls")] public List<ServerToolCall> ToolCalls { get; set; } = new();
        // Clarify requirements when the agent paused to ask the user (HITL).
        [JsonPropertyName("clarify")] public List<ClarifyRequirement> Clarify { get; set; } = new();
        // Done-frame follow-up chips (0-3, model-derived text) — 2026-08-02
        // copilot-reasoning-ui spec. Empty/absent on older backends.
        [JsonPropertyName("followups")] public List<string> Followups { get; set; } = new();
        // Optional structured result breakdown for the result card's proportion
        // bars — populated only when the turn's tool results carried one
        // (count_by / color legend / route_* open_connectors). Null otherwise;
        // the pane falls back to the plain answer.
        [JsonPropertyName("result_summary")] public ResultSummaryDto ResultSummary { get; set; }
        // Action Mode addendum (2026-08-02): codegen C# always requires
        // confirmation — arbitrary code can delete anything, so Auto mode
        // never fast-tracks it. Hardcoded true server-side today; default
        // true here too (fail-safe — same "missing = ask" rule as
        // PendingToolCall.RequiresConfirmation) so an older/not-yet-updated
        // backend that omits the field still gates codegen.
        [JsonPropertyName("code_requires_confirmation")] public bool CodeRequiresConfirmation { get; set; } = true;

        public bool AwaitingRevit =>
            Status == "awaiting_revit" && Pending != null && Pending.Count > 0;

        public bool AwaitingUserInput =>
            Status == "awaiting_user_input" && Clarify != null && Clarify.Count > 0;
    }

    public sealed class ServerToolCall
    {
        [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    }

    // ─── Clarify (HITL get_user_input pause) wire DTOs ──────────────────────
    // Backend pauses with status "awaiting_user_input" + clarify requirements;
    // the pane asks the user, answers POST back via /tool/resume-input keyed by
    // requirement_id. Field shape mirrors agno's UserInputField.
    public sealed class ClarifyField
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("field_type")] public string FieldType { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("value")] public object Value { get; set; }
    }

    public sealed class ClarifyRequirement
    {
        [JsonPropertyName("requirement_id")] public string RequirementId { get; set; } = "";
        [JsonPropertyName("tool_call_id")] public string ToolCallId { get; set; }
        [JsonPropertyName("fields")] public List<ClarifyField> Fields { get; set; } = new();
    }

    public sealed class ClarifyAnswerDto
    {
        [JsonPropertyName("requirement_id")] public string RequirementId { get; set; } = "";
        [JsonPropertyName("values")] public Dictionary<string, object> Values { get; set; } = new();
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
    }

    public sealed class ToolResultDto
    {
        [JsonPropertyName("tool_call_id")] public string ToolCallId { get; set; } = "";
        [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
        [JsonPropertyName("result")] public object Result { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
    }

    // ─── Result summary (done-frame proportion-bar breakdown) ───────────────
    // 2026-08-02 copilot-reasoning-ui spec: color_hint is a system CLASS
    // ("supply"/"return"/"exhaust"/"none"), never a hex — the pane maps it to
    // the Cp.System.* tokens so the palette stays client-owned.
    public sealed class ResultSummaryRowDto
    {
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("color_hint")] public string ColorHint { get; set; } = "";
    }

    public sealed class ResultSummaryDto
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("rows")] public List<ResultSummaryRowDto> Rows { get; set; } = new();
    }
}
