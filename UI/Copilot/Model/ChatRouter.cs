using System.Collections.Generic;
using System.Threading.Tasks;
using RevitWebAppSync.Services;

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
        public IReadOnlyList<ProgressStep> Steps;  // full phased trail (phases + tools); preferred over ToolCallTrace when present
        public string Tindakan;          // one-tap "next step" offer parsed from the reply; empty = no offer
        public RevitWebAppSync.Models.ReviewerVerdict Verdict;
        public bool Interrupted;         // user hit Stop — renders as the italic "Interrupted." line
        public bool NeedsActionConfirmation;  // pending MUTATE batch parked behind the Ya/Tidak card
        public List<string> ActionLabels;     // friendly per-action lines for the confirmation card
        // Streaming reasoning timeline (2026-08-02 spec) — persisted trail +
        // whole-turn elapsed seconds, snapshotted at completion.
        public List<ReasoningStep> ReasoningSteps;
        public double ReasoningElapsedSeconds;
        // Done-frame follow-up chips + optional structured result breakdown.
        public List<FollowupAction> Followups;
        public ResultSummaryModel ResultSummary;
        // Action Mode addendum (2026-08-02): true when EVERY pending call in
        // this confirmation batch has requires_confirmation == false — the
        // ONLY thing that makes Auto mode's programmatic-accept path safe.
        // Meaningless unless NeedsActionConfirmation is also true.
        public bool AutoApprovable;
        // Whether the codegen `Code` above needs an approval card before it
        // runs. Always true from a spec-compliant backend (arbitrary C# can
        // delete anything — Auto mode never fast-tracks it); defaults true
        // fail-safe. Meaningless unless Code is non-empty.
        public bool CodeRequiresConfirmation = true;
        // Structured ask_user questions (2026-08-18): options + multi_select
        // per question — the pane renders tappable option rows. ChoiceBatch is
        // the card-owned opaque HITL batch (RevitChatRouter.PendingHitl) the
        // submit passes back, same ownership pattern as PendingBatch below.
        public List<ChoiceRequirement> Choices;
        public object ChoiceBatch;
        // The parked MUTATE batch behind this confirmation card, as an opaque
        // handle (RevitChatRouter.PendingConfirm). The CARD owns its batch:
        // resolution passes this back so a Ya click still works after the
        // router's own parked field was cleared by a newer message or a router
        // swap — the 2026-08-18 UAT dead-end ("Tiada tindakan tertunda" on Ya,
        // batch silently auto-rejected). Meaningless unless
        // NeedsActionConfirmation is true.
        public object PendingBatch;
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
