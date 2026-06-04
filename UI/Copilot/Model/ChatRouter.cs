using System.Collections.Generic;
using System.Threading.Tasks;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Normalized outcome of a chat route — backend or offline fallback.</summary>
    public class RouteResult
    {
        public bool NeedsClarification;
        public string ClarifyingQuestion;
        public string ToolId;            // catalog tool used for visuals
        public List<string> PlanSteps;        // plan steps (English)
        public string Code;              // runnable C# (backend action or catalog sample)
        public string Reply;             // optional natural-language reply
        public bool IsQuery;             // pure read+report — auto-run, render as chat text not card
        public List<string> ToolCallTrace;  // tool names called by the agent (in order). Renders as a faint trace under the reply.
        public RevitWebAppSync.Models.ReviewerVerdict Verdict;

        // Hybrid routing: backend chose a deterministic vetted tool (BackendName)
        // + args instead of code. The VM runs the vetted synth (confirm if mutating).
        public string VettedTool;
        public IDictionary<string, object> VettedArgs;
    }

    /// <summary>
    /// Routes a chat message to a proposal. The Revit-aware implementation (in the panel)
    /// calls AIService.RouteAsync with live ModelContext; it falls back to the deterministic
    /// QueryInterpreter when the backend is unreachable.
    /// </summary>
    public interface IChatRouter
    {
        Task<RouteResult> RouteAsync(string message, string fallbackToolId);
    }
}
