using System.Collections.Generic;
using System.Threading.Tasks;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Normalized outcome of a chat route — backend or offline fallback.</summary>
    public class RouteResult
    {
        public bool NeedsClarification;
        public bool NotAuthenticated;    // no access token — user must sign in
        public string ClarifyingQuestion;
        public string Intent;            // backend intent — drives the proposal title
        public string ToolId;            // catalog tool used for visuals
        public List<string> Plan;        // plan steps (English)
        public string Code;              // runnable C# (backend action or catalog sample)
        public string Reply;             // optional natural-language reply
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
