#nullable enable
// "What I will write": the values a write request is about to set, in plain
// lines, shown in the running turn BEFORE the Revit preview. Informational only
// (writes auto-apply after the preview; Stop is the way out). Built from the
// model's own tool-call arguments, so it needs no confirm card. Pure (no WPF).
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class WritePreviewText
    {
        public const string Phase = "write_preview";

        /// <summary>How long the row stays up, with Stop live, before the write starts. The Revit
        /// preview that follows freezes the pane, so this is the only moment Stop can be pressed.</summary>
        public const int StopWindowMs = 1200;
        private const int MaxLines = 8;
        private const int MaxValueChars = 80;

        public static string Title(bool ms) => ms ? "Apa yang akan saya tulis" : "What I will write";

        public static string NothingWritten(bool ms) => ms
            ? "Belum ada yang ditulis. Berhenti sekarang tidak mengubah apa-apa."
            : "Nothing written yet. Stopping now changes nothing.";

        /// <summary>One line per call ("Model: Adrino LED 601/10/840"), at most 8, then "+N more".
        /// "" when no call has anything to list.</summary>
        public static string Lines(IReadOnlyList<ToolCallDto>? calls, bool ms)
        {
            var lines = new List<string>();
            if (calls != null)
                foreach (var c in calls)
                {
                    var line = Line(c, ms);
                    if (line.Length > 0) lines.Add(line);
                }
            var sb = new StringBuilder();
            for (var i = 0; i < lines.Count && i < MaxLines; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(lines[i]);
            }
            if (lines.Count > MaxLines)
                sb.Append('\n').Append(ms ? "+" + (lines.Count - MaxLines) + " lagi" : "+" + (lines.Count - MaxLines) + " more");
            return sb.ToString();
        }

        private static string Line(ToolCallDto c, bool ms)
        {
            if (c == null) return "";
            if (c.Tool == ToolNames.RunRevitCode)
                return ms ? "Perubahan daripada kod Revit (dipratonton dalam Revit)" : "Changes from Revit code (previewed in Revit)";
            if (c.Tool != ToolNames.SetParameters || c.Args.ValueKind != JsonValueKind.Object) return "";
            var args = c.Args;
            if (!args.TryGetProperty("param", out var p) || p.ValueKind != JsonValueKind.String) return "";
            var param = p.GetString()!.Trim();
            if (param.Length == 0) return "";
            if (args.TryGetProperty("table_rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                return param + ": " + rows.GetArrayLength().ToString(CultureInfo.InvariantCulture)
                       + (ms ? " nilai daripada jadual" : " values from the table");
            if (!args.TryGetProperty("value", out var v)) return "";
            var text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
            text = text.Trim();
            if (text.Length > MaxValueChars) text = text.Substring(0, MaxValueChars - 1) + "…";
            return param + ": " + text;
        }
    }
}
