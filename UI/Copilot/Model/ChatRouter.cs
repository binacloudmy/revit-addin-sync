using System.Collections.Generic;
using System.Threading.Tasks;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>Outcome of one copilot turn leg (protocol 3). Everything the pane
    /// renders about what HAPPENED comes from <see cref="Evidence"/> — the
    /// envelopes the add-in itself returned — never from <see cref="Reply"/>.</summary>
    public class RouteResult
    {
        /// <summary>The turn could not run; <see cref="FailureKind"/> picks the card.</summary>
        public bool Failed;
        public RouteFailure FailureKind;
        /// <summary>The turn parked on the assistant's question (<see cref="Ask"/>).</summary>
        public bool NeedsClarification;
        public string ClarifyingQuestion;
        public AskCardModel Ask;
        public string ToolId;
        /// <summary>Distinct add-in tools the turn ran.</summary>
        public List<string> ToolsUsed;
        /// <summary>Exactly the model's text (reply_delta), nothing appended.</summary>
        public string Reply;
        /// <summary>A pane-owned note shown apart from the reply (never merged into it).</summary>
        public string PaneNote;
        public bool IsQuery;
        /// <summary>The step list (progress frames, tool calls, the turn budget).</summary>
        public IReadOnlyList<ProgressStep> Steps;
        /// <summary>The drafter pressed Stop or the cloud cancelled the turn.</summary>
        public bool Interrupted;
        /// <summary>Client-generated id of the turn (the receipt's Undo is per turn).</summary>
        public string TurnId;
        /// <summary>Evidence strip, receipt headline, caution, export tables.</summary>
        public TurnEvidence Evidence;
        /// <summary>Drafter's language for pane-rendered text.</summary>
        public ReceiptLanguage Language;
    }

    public enum RouteFailure { None, SignIn, UpdateRequired, TurnActive, Unreachable, Refused, Other }

    /// <summary>Routes a chat message to the copilot (CopilotTurnRouter on protocol 3).</summary>
    public interface IChatRouter
    {
        Task<RouteResult> RouteAsync(string message, string fallbackToolId);
    }
}
