using Newtonsoft.Json;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    public class AIResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("explanation")]
        public string Explanation { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("tokensUsed")]
        public int? TokensUsed { get; set; }

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; }

        /// <summary>Server-detected: code is a pure read + SetResult report
        /// (no Transaction, no Selection.SetElementIds, no Delete/Set/Create).
        /// When true the Copilot pane auto-runs and renders the result as a
        /// chat-text bubble instead of the Save/Copy/Undo proposal card.</summary>
        [JsonProperty("is_query")]
        public bool IsQuery { get; set; }

        /// <summary>Tool-calling agent mode only (VIBE_AGENT_MODE=tool):
        /// the natural-language reply the agent emitted instead of (or
        /// alongside) a code fence. Pane renders this directly as the
        /// chat bubble — no card.</summary>
        [JsonProperty("reply")]
        public string Reply { get; set; }

        /// <summary>Tool-calling agent mode only: structured list of
        /// tools the agent called and the args it passed. Empty when
        /// the legacy codegen path was used. Each entry is
        /// {tool: string, args: object, result: object|null}.</summary>
        [JsonProperty("tool_calls")]
        public List<ToolCallRecord> ToolCalls { get; set; }

        /// <summary>Server-side path that produced this response:
        /// "tool" (PRD §12 Step 3 native tool calls) or "codegen"
        /// (legacy raw-C# emission).</summary>
        [JsonProperty("agent_mode")]
        public string AgentMode { get; set; }

        /// <summary>Plan-mode round-trip: matches the plan_id the
        /// addin sent in ExecutePlanRequest.</summary>
        [JsonProperty("plan_id")]
        public string PlanId { get; set; }

        /// <summary>Plan-mode (/execute-plan) gates that need user
        /// approval. Empty when nothing is gated. One ApprovalCardView
        /// per entry; on Approve, addin re-posts /execute-plan with
        /// the entry's gate_id added to approval_tokens.</summary>
        [JsonProperty("pending_approvals")]
        public List<PendingApproval> PendingApprovals { get; set; }

        /// <summary>Plan-mode round-trip: the tokens the addin sent
        /// in the request. Lets the addin accumulate across cycles
        /// without re-deriving from chat state.</summary>
        [JsonProperty("approval_tokens")]
        public List<string> ApprovalTokens { get; set; }

        /// <summary>Reviewer agent's semantic verification of the
        /// final reply + tool_calls. May be null when reviewer is
        /// disabled server-side (VIBE_TOOL_REVIEW=false).</summary>
        [JsonProperty("reviewer_verdict")]
        public ReviewerVerdict ReviewerVerdict { get; set; }
    }

    /// <summary>One tool the tool-calling agent invoked. Used by the
    /// Copilot pane to render an inline trace under the AI reply so
    /// the drafter can see what the AI looked up before answering.</summary>
    public class ToolCallRecord
    {
        [JsonProperty("tool")] public string Tool { get; set; }
        [JsonProperty("args")] public object Args { get; set; }
        [JsonProperty("result")] public object Result { get; set; }
    }

    /// <summary>Reviewer agent verdict (PRD §6.2 stage 7). Attached
    /// to /execute-plan and /generate (tool mode) responses when
    /// VIBE_TOOL_REVIEW=true on the backend. Logging-only by default;
    /// when ``remediated=true`` the orchestrator already ran a
    /// remediation round on the failure and the response reflects
    /// the remediated attempt.</summary>
    public class ReviewerVerdict
    {
        [JsonProperty("verified")] public bool Verified { get; set; }
        [JsonProperty("issues")] public List<string> Issues { get; set; } = new List<string>();
        [JsonProperty("suggestions")] public List<string> Suggestions { get; set; } = new List<string>();
        [JsonProperty("remediated")] public bool Remediated { get; set; }
    }

    /// <summary>One server-side gate awaiting drafter approval.
    /// Carries everything ApprovalCardView needs to render: the tool
    /// name, the args (so we can preview element_ids in Revit), and
    /// a human-readable reason for the gate.</summary>
    public class PendingApproval
    {
        [JsonProperty("gate_id")] public string GateId { get; set; }
        [JsonProperty("tool")] public string Tool { get; set; }
        [JsonProperty("args")] public Newtonsoft.Json.Linq.JObject Args { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
    }
}
