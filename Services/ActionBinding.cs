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
            var from = Name(planned?.DocumentTitle, "model lain");
            var to = Name(live?.DocumentTitle, "model ini");
            return $"Saya berhenti: dokumen telah bertukar. Chat ini sedang bekerja dalam {from}, tetapi dokumen aktif sekarang ialah {to}. "
                 + $"Apa-apa yang ditemui dalam {from} (contohnya senarai elemen tadi) datang daripada model lain dan tidak boleh digunakan di sini. "
                 + $"Tiada apa yang ditulis dalam {to}. Tukar semula ke {from}, atau mulakan chat baru untuk {to}.";
        }

        /// <summary>Model-facing refusal when the drafter switched views. ok=false
        /// on purpose: the agent should re-plan (a new card) for the active view.</summary>
        public static string ViewChangedError(ActionBinding planned, ActionBinding live)
        {
            var from = Name(planned?.ViewName, "pandangan asal");
            var to = Name(live?.ViewName, "pandangan lain");
            return $"ditolak: pandangan telah bertukar. Tindakan ini dirancang dalam pandangan {from}, tetapi pandangan aktif sekarang ialah {to}. "
                 + "Tiada apa yang ditulis. Semak semula pandangan aktif dan rancang semula untuknya; jangan guna semula hasil yang terikat pada pandangan lama.";
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
                    : "ditolak: " + DocumentChangedMessage(planned, live),
            };
        }

        /// <summary>Drafter-facing line for a code card whose document or view changed.</summary>
        public static string CodeCardMessage(BindingVerdict verdict, ActionBinding planned, ActionBinding live)
        {
            if (verdict == BindingVerdict.DocumentChanged) return DocumentChangedMessage(planned, live);
            return $"Saya berhenti: pandangan telah bertukar. Kod ini ditulis untuk pandangan {Name(planned?.ViewName, "pandangan asal")}, "
                 + $"tetapi pandangan aktif sekarang ialah {Name(live?.ViewName, "pandangan lain")}. Tiada apa yang dijalankan. Minta semula dalam pandangan ini.";
        }

        private static string Name(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : "\"" + value.Trim() + "\"";
    }
}
