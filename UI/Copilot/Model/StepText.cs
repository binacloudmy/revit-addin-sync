#nullable enable
// What a step row in the activity card says. The router fills a tool step's
// Detail with the envelope's own summary (sometimes a .NET exception) and an
// error step's Label with the raw error message. The row shows a short plain
// line; the raw text stays behind a collapsed "Details" toggle.
// Pure (no WPF) so Tests can link it.
using System;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class StepText
    {
        /// <summary>The row's headline.</summary>
        public static string Title(string? phase, string? label, string? stepId) =>
            phase == "error" ? "Something went wrong in this turn"
            : !string.IsNullOrWhiteSpace(label) ? label!.Trim()
            : stepId ?? "";

        /// <summary>A short plain line under the headline, or "".</summary>
        public static string Short(string? phase, StepState state, string? detail = null) =>
            phase == WritePreviewText.Phase ? detail?.Trim() ?? ""
            : phase == "tool" && state == StepState.Error ? "This step didn't work." : "";

        /// <summary>The raw text kept behind "Details": the detail of a tool step,
        /// the message of an error step. "" when it adds nothing to the headline.</summary>
        public static string Raw(string? phase, string? label, string? detail)
        {
            if (phase == WritePreviewText.Phase) return "";   // shown whole under the headline
            var raw = (phase == "error" ? label : detail)?.Trim() ?? "";
            if (phase != "error" && string.Equals(raw, (label ?? "").Trim(), StringComparison.Ordinal)) return "";
            return raw;
        }
    }
}
