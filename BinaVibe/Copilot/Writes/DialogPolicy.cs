#nullable enable
// Revit dialogs during our write callbacks (spec §7.10; plan §5 W3 Task 5).
// Revit-free.
//
// Known dialog ids get a safe answer (DialogBoxShowingEventArgs.OverrideResult)
// and are reported in the envelope's `dialogs`. Any other dialog is left for
// the drafter to answer — the call waits, it cannot be aborted — and the
// pane shows progress{revit_busy}. Dialogs raised outside our callbacks (the
// drafter's own work) are never touched.
//
// Result codes are Win32 / TaskDialogResult values (IDOK = 1, IDCANCEL = 2,
// IDYES = 6, IDNO = 7). Add an id only with the answer that keeps the model
// honest: our failure preprocessor reports warnings itself, and a change
// Revit would otherwise roll back must be allowed to roll back.

using System;
using System.Collections.Generic;

namespace BinaVibe.Copilot.Writes
{
    public enum DialogAction
    {
        /// <summary>Answer with <see cref="DialogDecision.ResultCode"/>.</summary>
        Answer,
        /// <summary>Leave it to the drafter; tell the pane Revit is busy.</summary>
        Busy,
        /// <summary>Not raised by our callback: do nothing.</summary>
        Ignore,
    }

    public sealed class DialogDecision
    {
        public DialogAction Action { get; internal set; }
        public int ResultCode { get; internal set; }
        /// <summary>The answer's label for DialogDto.answered_with; null when not answered.</summary>
        public string? AnsweredWith { get; internal set; }
        /// <summary>What the dialog said, cut to <see cref="DialogPolicy.MaxTextChars"/>, for the
        /// envelope or the revit_busy progress line.</summary>
        public string Text { get; internal set; } = "";
    }

    public static class DialogPolicy
    {
        public const int MaxTextChars = 200;

        public sealed class SafeAnswer
        {
            public SafeAnswer(int resultCode, string label, string why)
            {
                ResultCode = resultCode;
                Label = label;
                Why = why;
            }

            public int ResultCode { get; }
            public string Label { get; }
            public string Why { get; }
        }

        /// <summary>Dialog id -> safe answer.</summary>
        public static IReadOnlyDictionary<string, SafeAnswer> Known { get; } = new Dictionary<string, SafeAnswer>(StringComparer.Ordinal)
        {
            // The warnings list after a transaction: OK keeps the change; the warnings
            // themselves are already in the envelope (FailureLog).
            ["Dialog_Revit_DocWarnDialog"] = new SafeAnswer(1, "OK", "warnings acknowledged; reported in the envelope"),
            // "Duplicate Types" on paste/copy: OK keeps the destination model's types.
            ["Dialog_Revit_PasteSimilarSymbolsPaste"] = new SafeAnswer(1, "OK", "keeps the types already in the model"),
        };

        public static DialogDecision Decide(string? dialogId, string? message, bool inOurCallback)
        {
            var text = Cut(message);
            if (!inOurCallback) return new DialogDecision { Action = DialogAction.Ignore, Text = text };

            if (!string.IsNullOrEmpty(dialogId) && Known.TryGetValue(dialogId!, out var answer))
                return new DialogDecision
                {
                    Action = DialogAction.Answer,
                    ResultCode = answer.ResultCode,
                    AnsweredWith = answer.Label,
                    Text = text.Length > 0 ? text : dialogId!,
                };

            var id = string.IsNullOrEmpty(dialogId) ? "a message box" : "'" + dialogId + "'";
            return new DialogDecision
            {
                Action = DialogAction.Busy,
                Text = "Revit is showing " + id + (text.Length > 0 ? ": " + text : "") + ". The drafter must answer it in Revit before the change can continue.",
            };
        }

        private static string Cut(string? s)
        {
            var t = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return t.Length <= MaxTextChars ? t : t.Substring(0, MaxTextChars - 1) + "…";
        }
    }
}
