// ActionBinding — the document and view a pending action was planned against
// (AI-203). Pure, so the decision is unit-tested without Revit.
//
// The router captures one at the start of a turn and keeps the chat's first
// document for the whole session: an element list read in model A must never
// be written in model B (X-02, X-03). The view is re-taken after every batch
// the add-in runs, so only a view change made by the drafter while the model
// planned (or while a card was open) is caught (X-04).

using System;

namespace RevitWebAppSync.Services
{
    public enum BindingScope
    {
        None,
        Document,
        DocumentAndView,
    }

    public enum BindingVerdict
    {
        Ok,
        DocumentChanged,
        ViewChanged,
    }

    public sealed class ActionBinding
    {
        public string DocumentFingerprint { get; set; }
        public string DocumentTitle { get; set; }
        public long? ViewId { get; set; }
        public string ViewName { get; set; }

        public ActionBinding Copy() => (ActionBinding)MemberwiseClone();

        /// <summary>Compare the planned binding with the live one. A side
        /// with no value (no document open, older caller) is never refused.</summary>
        public static BindingVerdict Check(ActionBinding planned, ActionBinding live, BindingScope scope)
        {
            if (planned == null || live == null || scope == BindingScope.None) return BindingVerdict.Ok;
            if (!string.IsNullOrEmpty(planned.DocumentFingerprint)
                && !string.IsNullOrEmpty(live.DocumentFingerprint)
                && !string.Equals(planned.DocumentFingerprint, live.DocumentFingerprint, StringComparison.Ordinal))
                return BindingVerdict.DocumentChanged;
            if (scope == BindingScope.DocumentAndView
                && planned.ViewId.HasValue && live.ViewId.HasValue
                && planned.ViewId.Value != live.ViewId.Value)
                return BindingVerdict.ViewChanged;
            return BindingVerdict.Ok;
        }

        /// <summary>Drafter-facing stop line when the document changed.</summary>
        public static string DocumentChangedMessage(ActionBinding planned, ActionBinding live)
        {
            var from = Name(planned?.DocumentTitle, "another model");
            var to = Name(live?.DocumentTitle, "this model");
            return $"Stopped: the document changed. This chat was working in {from}, but the active document is now {to}. "
                 + $"Anything found in {from} (like an earlier list of elements) came from another model and can't be applied here. "
                 + $"Nothing was written in {to}. Switch back to {from}, or start a new chat for {to}.";
        }

        /// <summary>Model-facing refusal when the drafter switched views. ok=false
        /// on purpose: the agent should re-plan (a new card) for the active view.</summary>
        public static string ViewChangedError(ActionBinding planned, ActionBinding live)
        {
            var from = Name(planned?.ViewName, "the planned view");
            var to = Name(live?.ViewName, "another view");
            return $"refused: the view changed. This action was planned in view {from}, but the active view is now {to}. "
                 + "Nothing was written. Re-check the active view and plan again for it; do not reuse results that were tied to the old view.";
        }

        /// <summary>The result sent back for a call refused by the check.
        /// ok=false so the agent re-plans instead of reporting success.</summary>
        public static ToolResultDto Refusal(PendingToolCall call, BindingVerdict verdict,
                                            ActionBinding planned, ActionBinding live)
        {
            return new ToolResultDto
            {
                ToolCallId = call?.ToolCallId ?? "",
                Ok = false,
                Error = verdict == BindingVerdict.ViewChanged
                    ? ViewChangedError(planned, live)
                    : "refused: " + DocumentChangedMessage(planned, live),
            };
        }

        /// <summary>Drafter-facing line for a code card whose document or view changed.</summary>
        public static string CodeCardMessage(BindingVerdict verdict, ActionBinding planned, ActionBinding live)
        {
            if (verdict == BindingVerdict.DocumentChanged) return DocumentChangedMessage(planned, live);
            return $"Stopped: the view changed. This code was written for view {Name(planned?.ViewName, "the planned view")}, "
                 + $"but the active view is now {Name(live?.ViewName, "another view")}. Nothing was run. Ask again in this view.";
        }

        private static string Name(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : "\"" + value.Trim() + "\"";
    }
}
