#nullable enable
// The words and numbers of the live "Working" card (design board Running.dc.html): which rows it lists, which of
// its five bar segments are lit, what the right-hand column says, and what the Stop row promises. Pure (no WPF),
// so a test pins each rule.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>One value of the "What I will write" card. <see cref="Derived"/>: the model gave no value, so the
    /// row is the amber "I will build one" row.</summary>
    public sealed record PreviewRecord(string Key, string Value, bool Derived);

    public enum SegmentState { Done, Current, Pending }

    /// <summary>One row of the card: a status icon, a label (backtick text is mono), a right-hand outcome.</summary>
    public sealed class RunRow
    {
        public StepState State { get; init; }
        public string Text { get; init; } = "";
        public string Outcome { get; init; } = "";
        public string Note { get; init; } = "";
        /// <summary>How many consecutive identical rows this one stands for.</summary>
        public int Times { get; init; } = 1;
    }

    public static class RunningCard
    {
        public const int SegmentCount = 5;

        /// <summary>The five bar segments from the turn's phase percentage (there is no server total, so no "of 5"):
        /// floor(pct / 20) are done, the next one pulses while the turn runs, the rest wait.</summary>
        public static IReadOnlyList<SegmentState> Segments(double pct, bool running)
        {
            var done = (int)Math.Floor(Math.Max(0, Math.Min(100, pct)) / (100.0 / SegmentCount));
            if (done > SegmentCount) done = SegmentCount;
            var list = new SegmentState[SegmentCount];
            for (var i = 0; i < SegmentCount; i++)
                list[i] = i < done ? SegmentState.Done : i == done && running ? SegmentState.Current : SegmentState.Pending;
            return list;
        }

        /// <summary>The rows the card lists: no developer line, no "Thinking" row, no write-preview row (that is its
        /// own card), and runs of the same label folded into one row.</summary>
        public static IReadOnlyList<RunRow> Rows(IEnumerable<ProgressStep>? steps)
        {
            var rows = new List<RunRow>();
            foreach (var s in ProgressTrail.UserVisible(steps))
            {
                if (ProgressTrail.IsWaitStep(s) || s.Phase == WritePreviewText.Phase) continue;
                var text = StepText.Title(s.Phase, s.Label, s.StepId);
                if (text.Length == 0) continue;
                var last = rows.Count > 0 ? rows[rows.Count - 1] : null;
                if (last != null && string.Equals(last.Text, text, StringComparison.OrdinalIgnoreCase))
                {
                    var times = last.Times + 1;
                    var state = last.State == StepState.Running || s.State == StepState.Running ? StepState.Running
                        : last.State == StepState.Error || s.State == StepState.Error ? StepState.Error : StepState.Done;
                    // A real outcome wins; "×N" is only the stand-in for a fold that counted nothing.
                    var real = s.Outcome.Length > 0 ? s.Outcome : last.Outcome.StartsWith("×", StringComparison.Ordinal) ? "" : last.Outcome;
                    rows[rows.Count - 1] = new RunRow
                    {
                        State = state, Text = last.Text, Times = times,
                        Outcome = real.Length > 0 ? real : "×" + times.ToString(CultureInfo.InvariantCulture),
                        Note = state == StepState.Running ? (s.Note.Length > 0 ? s.Note : last.Note) : "",
                    };
                    continue;
                }
                rows.Add(new RunRow
                {
                    State = s.State, Text = text, Outcome = s.Outcome,
                    Note = s.State == StepState.Running ? s.Note : "",
                });
            }
            return rows;
        }

        /// <summary>A label cut at its backticks: (text, isMono) runs. "Found the `Adrino` row" gives three runs.</summary>
        public static IReadOnlyList<(string Text, bool Mono)> LabelParts(string? label)
        {
            var parts = new List<(string, bool)>();
            var text = label ?? "";
            var mono = false;
            foreach (var piece in text.Split('`'))
            {
                if (piece.Length > 0) parts.Add((piece, mono));
                mono = !mono;
            }
            // An odd number of backticks leaves the last run open: show the text as it came, not half mono.
            if (text.Count(c => c == '`') % 2 == 1) return new List<(string, bool)> { (text.Replace("`", ""), false) };
            return parts;
        }

        /// <summary>The right-hand outcome of a finished tool call: its count with the noun of the tool, "" when the
        /// call counted nothing.</summary>
        public static string OutcomeText(string tool, int? count, bool ms)
        {
            if (count is not > 0) return "";
            var n = count.Value.ToString("N0", CultureInfo.InvariantCulture);
            var noun = tool switch
            {
                BinaVibe.Copilot.Contracts.ToolNames.FindElements or BinaVibe.Copilot.Contracts.ToolNames.Inspect => ms ? "elemen" : count == 1 ? "element" : "elements",
                BinaVibe.Copilot.Contracts.ToolNames.Check => ms ? "peraturan" : count == 1 ? "rule" : "rules",
                BinaVibe.Copilot.Contracts.ToolNames.SetParameters => ms ? "nilai" : count == 1 ? "value" : "values",
                _ => "",
            };
            return noun.Length == 0 ? n : n + " " + noun;
        }

        /// <summary>The line under the active tool row, before the call has counted anything.</summary>
        public static string ActiveNote(string tool, string? viewName, bool ms)
        {
            var inView = string.IsNullOrWhiteSpace(viewName) ? "" : (ms ? " dalam " : " in ") + "{" + viewName!.Trim() + "}";
            return tool switch
            {
                BinaVibe.Copilot.Contracts.ToolNames.FindElements => (ms ? "Menyaring model" : "Looking through the model") + inView,
                BinaVibe.Copilot.Contracts.ToolNames.ModelOverview => (ms ? "Mengira kategori dan aras" : "Counting categories and levels") + inView,
                BinaVibe.Copilot.Contracts.ToolNames.Inspect => ms ? "Membaca parameter elemen" : "Reading the parameters of the elements",
                BinaVibe.Copilot.Contracts.ToolNames.Check => ms ? "Menjalankan peraturan semakan" : "Running the check rules",
                BinaVibe.Copilot.Contracts.ToolNames.Look => ms ? "Mengambil imej paparan" : "Capturing the view" + inView,
                _ => "",
            };
        }

        /// <summary>The line next to the Stop button. While the values are shown and not yet written it promises that
        /// nothing changed; a read-only turn says it only reads; once a write began, nothing is promised.</summary>
        public static string StopHelper(IEnumerable<ProgressStep>? steps, bool ms)
        {
            var all = ProgressTrail.UserVisible(steps).ToList();
            var preview = all.FirstOrDefault(x => x.Phase == WritePreviewText.Phase);
            if (preview != null) return preview.State == StepState.Running ? WritePreviewText.NothingWritten(ms) : "";
            if (all.Any(x => x.Phase == "preview")) return "";
            return ms ? "Hanya membaca. Berhenti tidak mengubah apa-apa." : "Only reading. Stopping changes nothing.";
        }

        /// <summary>The values to show in the "What I will write" card (the latest write step that has any), or empty.</summary>
        public static IReadOnlyList<PreviewRecord> PreviewRecords(IEnumerable<ProgressStep>? steps)
        {
            var step = ProgressTrail.UserVisible(steps).LastOrDefault(x => x.Phase == WritePreviewText.Phase && x.Preview is { Count: > 0 });
            return step?.Preview ?? Array.Empty<PreviewRecord>();
        }

        /// <summary>True while the preview card is still on screen: the write step exists and has not finished.</summary>
        public static bool PreviewOpen(IEnumerable<ProgressStep>? steps) =>
            ProgressTrail.UserVisible(steps).Any(x => x.Phase == WritePreviewText.Phase && x.State == StepState.Running);
    }
}
