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
        // Task 12: true when EVERY pending call this round is a read (server-side
        // INSPECT_TOOL_NAMES, app/services/revit_turn.py). ToolLoopRunner spends
        // this round against the separate, larger InspectRounds cap instead of
        // MaxRounds — verification (the post-build audit chain, a read-heavy
        // question) never counts toward the budget that exists to stop a runaway
        // MUTATE spiral. Default false (fail closed) so an older backend that
        // omits the field keeps today's behaviour: every round spends MaxRounds.
        [JsonPropertyName("reads_only")] public bool ReadsOnly { get; set; }
        // Tools the agent ran SERVER-SIDE this turn (list_views, find_elements_by_filter,
        // …). These never come back as pending (they don't execute in Revit), so without
        // this the trace would be empty for any read/codegen request. Drives the step chips.
        [JsonPropertyName("tool_calls")] public List<ServerToolCall> ToolCalls { get; set; } = new();
        // Clarify requirements when the agent paused to ask the user (HITL).
        [JsonPropertyName("clarify")] public List<ClarifyRequirement> Clarify { get; set; } = new();
        // Structured twin (2026-08-18): ask_user QUESTIONS with 2-4 options
        // each — rendered as tappable option rows, answered via the same
        // /tool/resume-input lane with `selections` keyed by question text.
        [JsonPropertyName("choices")] public List<ChoiceRequirement> Choices { get; set; } = new();
        // Done-frame follow-up chips (0-3) — 2026-08-02 offer_actions spec.
        // Wire shape is now list[{label, prompt}], but an older backend can
        // still send plain strings; FollowupActionListConverter tolerates
        // both (string s -> {Label=s, Prompt=s}) and skips anything it can't
        // parse rather than failing the whole done frame. Empty/absent on
        // older backends that predate follow-up chips entirely.
        [JsonPropertyName("followups")]
        [JsonConverter(typeof(FollowupActionListConverter))]
        public List<FollowupAction> Followups { get; set; } = new();
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

        // BOTH clarify shapes count: get_user_input fields (Clarify) AND
        // ask_user option questions (Choices). The old Clarify-only guard
        // silently swallowed every ask_user pause — the run parked backend-
        // side while the pane rendered the default "Done." (UAT 2026-08-18,
        // "buat rumah kedai" → model called ask_user twice → drafter saw
        // "Done." and an empty box).
        public bool AwaitingUserInput =>
            Status == "awaiting_user_input"
            && ((Clarify != null && Clarify.Count > 0)
                || (Choices != null && Choices.Count > 0));
    }

    public sealed class ServerToolCall
    {
        [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    }

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

    // Tolerant list converter: each item is EITHER a {label, prompt} object
    // OR a plain string (old-backend compat — string s becomes Label=Prompt=s,
    // per the 2026-08-02 spec's "addin compat both directions"). Any item
    // that is neither, or an object with no usable label/prompt, is skipped —
    // fail-safe, never throws, never blanks the rest of the list.
    public sealed class FollowupActionListConverter : JsonConverter<List<FollowupAction>>
    {
        public override List<FollowupAction> Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
        {
            var result = new List<FollowupAction>();
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                return result;
            }
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.String:
                    {
                        var s = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            result.Add(new FollowupAction { Label = s, Prompt = s });
                        break;
                    }
                    case JsonTokenType.StartObject:
                    {
                        using var doc = JsonDocument.ParseValue(ref reader);
                        var root = doc.RootElement;
                        string label = root.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                        string prompt = root.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(label) || !string.IsNullOrWhiteSpace(prompt))
                            result.Add(new FollowupAction { Label = label ?? prompt, Prompt = prompt ?? label });
                        break;
                    }
                    default:
                        // Junk item (number, bool, null, nested array) — skip
                        // and keep parsing the rest of the list.
                        reader.Skip();
                        break;
                }
            }
            return result;
        }

        public override void Write(Utf8JsonWriter writer, List<FollowupAction> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            if (value != null)
                foreach (var item in value)
                {
                    writer.WriteStartObject();
                    writer.WriteString("label", item?.Label ?? "");
                    writer.WriteString("prompt", item?.Prompt ?? "");
                    writer.WriteEndObject();
                }
            writer.WriteEndArray();
        }
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
        // ask_user answers: selected option labels (or free text via the
        // Lain-lain escape) keyed by QUESTION TEXT — provide_user_feedback's
        // contract backend-side.
        [JsonPropertyName("selections")] public Dictionary<string, List<string>> Selections { get; set; } = new();
    }

    // ─── ask_user (structured clarify) wire shapes ──────────────────────────
    public sealed class AskOptionDto
    {
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
    }

    public sealed class AskQuestionDto
    {
        [JsonPropertyName("question")] public string Question { get; set; } = "";
        [JsonPropertyName("header")] public string Header { get; set; } = "";
        [JsonPropertyName("multi_select")] public bool MultiSelect { get; set; }
        [JsonPropertyName("options")] public List<AskOptionDto> Options { get; set; } = new();
    }

    public sealed class ChoiceRequirement
    {
        [JsonPropertyName("requirement_id")] public string RequirementId { get; set; } = "";
        [JsonPropertyName("questions")] public List<AskQuestionDto> Questions { get; set; } = new();
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
