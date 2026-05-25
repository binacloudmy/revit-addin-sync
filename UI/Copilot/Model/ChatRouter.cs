using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Discriminator for a routed chat decision (PRD revit_copilot_v2).</summary>
    public enum RouteResultKind
    {
        VettedTool,         // Handled locally — C# synthesized from a vetted recipe.
        NeedsAI,            // Delegated to bina-ai /generate for codegen.
        Clarify,            // Local interpreter wants a follow-up question.
        NotAuthenticated,   // No access token — caller must sign in.
        Failed,             // Backend unreachable or unrecoverable error.
    }

    /// <summary>
    /// Normalized outcome of a chat route. After PRD V2 the addin decides Vetted vs NeedsAI
    /// LOCALLY (QueryInterpreter regex); the backend is only reached for the NeedsAI path,
    /// and even then only for codegen. The bag-of-fields keeps the viewmodel consumer
    /// unchanged — what shifted is who populates them.
    /// </summary>
    public class RouteResult
    {
        public RouteResultKind Kind;

        // Common / consumer-side
        public bool NotAuthenticated;
        public bool NeedsClarification;
        public string ClarifyingQuestion;
        public string Intent;            // proposal title
        public string ToolId;            // catalog tool used for visuals
        public List<string> Plan;        // plan steps (English)
        public string Code;              // runnable C# (synthesized or AI-generated)
        public string Reply;             // optional natural-language reply

        // PRD V2 — discriminator payload
        public string ToolName;                          // vetted tool name (e.g. "open_view")
        public Dictionary<string, object> ToolParams;    // bound vetted params
        public string AIPrompt;                          // pass-through for NeedsAI

        public static RouteResult VettedTool(string name, Dictionary<string, object> p,
                                             string code, string intent, string toolId)
            => new RouteResult
            {
                Kind = RouteResultKind.VettedTool,
                ToolName = name, ToolParams = p,
                Code = code, Intent = intent, ToolId = toolId,
            };

        public static RouteResult NeedsAI(string prompt, string fallbackToolId)
            => new RouteResult
            {
                Kind = RouteResultKind.NeedsAI,
                AIPrompt = prompt, ToolId = fallbackToolId,
            };

        public static RouteResult Clarify(string question)
            => new RouteResult
            {
                Kind = RouteResultKind.Clarify,
                NeedsClarification = true,
                ClarifyingQuestion = question,
            };

        public static RouteResult NotAuthed()
            => new RouteResult { Kind = RouteResultKind.NotAuthenticated, NotAuthenticated = true };

        public static RouteResult Failed(string reply = null)
            => new RouteResult { Kind = RouteResultKind.Failed, Reply = reply };
    }

    /// <summary>
    /// Routes a chat message to a proposal. The Revit-aware implementation runs
    /// QueryInterpreter locally and only reaches the backend for the NeedsAI path.
    /// </summary>
    public interface IChatRouter
    {
        Task<RouteResult> RouteAsync(string message, string fallbackToolId, CancellationToken ct = default);
    }
}
