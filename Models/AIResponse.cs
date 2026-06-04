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

        /// <summary>Reviewer agent's semantic verification of the
        /// final reply + tool_calls. May be null when reviewer is
        /// disabled server-side (VIBE_TOOL_REVIEW=false).</summary>
        [JsonProperty("reviewer_verdict")]
        public ReviewerVerdict ReviewerVerdict { get; set; }

        /// <summary>Hybrid routing: when set, the backend mapped the prompt to a
        /// deterministic vetted Tier-1 tool (its BackendName, e.g. "rename_elements").
        /// The addin runs that tool with <see cref="VettedArgs"/> instead of Code.</summary>
        [JsonProperty("vetted_tool")]
        public string VettedTool { get; set; }

        /// <summary>Arguments for <see cref="VettedTool"/>, keyed by the catalog
        /// field ids the vetted synth reads (view / category / find / replace / etc.).</summary>
        [JsonProperty("vetted_args")]
        public Dictionary<string, object> VettedArgs { get; set; }
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
    /// to /generate (tool mode) responses when VIBE_TOOL_REVIEW=true on
    /// the backend. Logging-only by default.</summary>
    public class ReviewerVerdict
    {
        [JsonProperty("verified")] public bool Verified { get; set; }
        [JsonProperty("issues")] public List<string> Issues { get; set; } = new List<string>();
        [JsonProperty("suggestions")] public List<string> Suggestions { get; set; } = new List<string>();
        [JsonProperty("remediated")] public bool Remediated { get; set; }
    }
}
