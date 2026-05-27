// Plan / PlanStep wire shape — mirrors app/agents/vibe/plan_schema.py.
// JSON property names match the backend's snake_case fields exactly.
// The Copilot pane's PlanCardView binds against PlanModel directly.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitWebAppSync.Models
{
    public class PlanStepModel
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("type")] public string Type { get; set; }                  // INSPECT | DECIDE | MUTATE | VERIFY
        [JsonProperty("goal")] public string Goal { get; set; }
        [JsonProperty("tool")] public string Tool { get; set; }                  // null for DECIDE
        [JsonProperty("tool_args_hint")] public object ToolArgsHint { get; set; }
        [JsonProperty("depends_on")] public List<int> DependsOn { get; set; } = new List<int>();
        [JsonProperty("requires_approval")] public bool RequiresApproval { get; set; }
        [JsonProperty("expected_outcome")] public string ExpectedOutcome { get; set; }
    }

    public class PlanModel
    {
        [JsonProperty("intent")] public string Intent { get; set; }
        [JsonProperty("ambiguities")] public List<string> Ambiguities { get; set; } = new List<string>();
        [JsonProperty("steps")] public List<PlanStepModel> Steps { get; set; } = new List<PlanStepModel>();
        [JsonProperty("rollback_strategy")] public string RollbackStrategy { get; set; } = "transaction_group";
        [JsonProperty("estimated_seconds")] public double EstimatedSeconds { get; set; }
    }

    /// <summary>Response shape from POST /agents/revit-ai/plan.</summary>
    public class PlanResponse
    {
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("plan")] public PlanModel Plan { get; set; }
        [JsonProperty("plan_id")] public string PlanId { get; set; }
        [JsonProperty("intent_summary")] public string IntentSummary { get; set; }
        [JsonProperty("prompt_version")] public string PromptVersion { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
    }
}
