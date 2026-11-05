#nullable enable
// What a step row in the activity card says. The router fills a tool step's
// Detail with the envelope's own summary (English, sometimes a .NET exception)
// and an error step's Label with the raw error message. The drafter gets a
// short plain line in their language; the raw text stays one click away under
// "Details" and in the debug log, never as the row's headline.
// Pure (no WPF) so Tests can link it.
using System;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class StepText
    {
        public const string ErrorPhase = "error";
        public const string ToolPhase = "tool";

        /// <summary>The row's headline.</summary>
        public static string Title(string? phase, string? label, string? stepId, StepState state, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            if (phase == ErrorPhase)
                return ms ? "Ada masalah semasa giliran ini" : "Something went wrong in this turn";
            if (!string.IsNullOrWhiteSpace(label)) return label!.Trim();
            if (phase == ToolPhase) return ms ? "Menjalankan alat" : "Running a tool";
            return stepId ?? "";
        }

        /// <summary>A short plain line under the headline, or "" for none.</summary>
        public static string Short(string? phase, StepState state, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            if (phase == ErrorPhase)
                return ms ? "Copilot akan cuba cara lain atau memberitahu anda." : "Copilot will try another way or tell you.";
            if (phase == ToolPhase && state == StepState.Error)
                return ms ? "Langkah ini tidak berjaya." : "This step didn't work.";
            return "";
        }

        /// <summary>The raw text kept behind "Details": the router's detail for a
        /// tool step, the raw message for an error step. "" when there is none or
        /// it says nothing the headline does not.</summary>
        public static string Raw(string? phase, string? label, string? detail)
        {
            var raw = phase == ErrorPhase ? (label ?? "") : (detail ?? "");
            raw = raw.Trim();
            if (raw.Length == 0) return "";
            if (phase != ErrorPhase && string.Equals(raw, (label ?? "").Trim(), StringComparison.Ordinal)) return "";
            return raw;
        }

        /// <summary>Label of the toggle that shows <see cref="Raw"/>.</summary>
        public static string DetailsToggle(bool open, ReceiptLanguage lang) =>
            lang == ReceiptLanguage.Ms ? (open ? "Sembunyi butiran" : "Butiran") : (open ? "Hide details" : "Details");

        /// <summary>Placeholder while the streaming view holds a block back.</summary>
        public static string Preparing(RevitWebAppSync.Helpers.HeldBlock held, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            switch (held)
            {
                case RevitWebAppSync.Helpers.HeldBlock.Table: return ms ? "Menyediakan jadual…" : "Preparing a table…";
                case RevitWebAppSync.Helpers.HeldBlock.Code: return ms ? "Menulis kod…" : "Writing code…";
                default: return "";
            }
        }
    }
}
