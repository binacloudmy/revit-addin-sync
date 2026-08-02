using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum ReasoningState { Running, Done }

    /// <summary>
    /// One row in the streaming "reasoning" timeline (docs/design/copilot-reasoning
    /// README + 2026-08-02 spec) — distinct from <see cref="ProgressStep"/>: that
    /// trail carries terse one-line tool/status labels ("Running create_wall…");
    /// this one carries the backend's WORKING NARRATIVE — a short mono label plus
    /// a growing multi-sentence body, appended via `text_delta` on the wire
    /// `reasoning` SSE event ({step_id, label, text_delta, state}).
    ///
    /// INotifyPropertyChanged so a bound row can live-update its Text/State without
    /// the host control rebuilding the whole timeline — same contract as ProgressStep.
    /// </summary>
    public sealed class ReasoningStep : INotifyPropertyChanged
    {
        public string StepId { get; init; } = "";

        private string _label = "";
        public string Label { get => _label; set { _label = value; Raise(nameof(Label)); } }

        private string _text = "";
        public string Text { get => _text; set { _text = value; Raise(nameof(Text)); } }

        private ReasoningState _state = ReasoningState.Running;
        public ReasoningState State { get => _state; set { _state = value; Raise(nameof(State)); } }

        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Pure reducer: applies one parsed `reasoning` SSE event onto a step
    /// collection. A new step_id appends a row; an existing step_id APPENDS
    /// text_delta to the row's Text (the wire event is a delta stream, not a
    /// replacing snapshot — unlike ProgressReducer's tool/status events) and
    /// updates Label/State when supplied. No UI, no I/O — directly unit-testable.
    /// </summary>
    public static class ReasoningReducer
    {
        public static void Apply(ObservableCollection<ReasoningStep> steps, string stepId,
                                  string label, string textDelta, ReasoningState state)
        {
            if (steps == null || string.IsNullOrEmpty(stepId)) return;
            ReasoningStep existing = null;
            foreach (var s in steps) { if (s.StepId == stepId) { existing = s; break; } }
            if (existing == null)
            {
                steps.Add(new ReasoningStep
                {
                    StepId = stepId,
                    Label = label ?? "",
                    Text = textDelta ?? "",
                    State = state,
                });
                return;
            }
            if (!string.IsNullOrEmpty(label)) existing.Label = label;
            if (!string.IsNullOrEmpty(textDelta)) existing.Text += textDelta;
            existing.State = state;
        }

        /// <summary>On turn completion, flip any row still Running to Done — the
        /// same "no genuinely-running row survives a finished turn" rule
        /// ProgressReducer.CompleteRunning applies to the tool trail.</summary>
        public static void CompleteRunning(ObservableCollection<ReasoningStep> steps)
        {
            if (steps == null) return;
            foreach (var s in steps)
                if (s.State == ReasoningState.Running) s.State = ReasoningState.Done;
        }

        public static ReasoningState StateFrom(string wireState) =>
            string.Equals(wireState, "done", StringComparison.OrdinalIgnoreCase)
                ? ReasoningState.Done : ReasoningState.Running;
    }

    /// <summary>Small pure helpers for the reasoning timeline's chrome text —
    /// English chrome per the 2026-08-02 spec's operator override (drafter
    /// content stays in whatever language the turn used; the UI shell is
    /// English: "Thinking… 8s · 5 steps").</summary>
    public static class ReasoningTrail
    {
        public static string ElapsedLabel(double seconds, bool streaming) =>
            streaming ? $"Thinking…  {seconds:0.0}s" : $"Thinking {Math.Max(1, (int)Math.Round(seconds))}s";

        public static string StepBadge(int count) => count == 1 ? "1 step" : $"{count} steps";

        /// <summary>Whole-turn elapsed seconds: first step's start to now. Computed
        /// from wall-clock timestamps (no persisted end time on ReasoningStep — the
        /// backend's `reasoning` events are running/done state flips, not spans),
        /// same approach as ProgressTrail.TotalElapsedText. Called both live
        /// (ticking display) and once at turn completion (persisted stamp).</summary>
        public static double TotalElapsedSeconds(IReadOnlyList<ReasoningStep> steps)
        {
            if (steps == null || steps.Count == 0) return 0;
            var start = steps[0].StartedUtc;
            var secs = (DateTime.UtcNow - start).TotalSeconds;
            return secs < 0 ? 0 : secs;
        }

        /// <summary>The step currently streaming (last Running row), or null when
        /// the trail is idle/settled.</summary>
        public static ReasoningStep Current(IReadOnlyList<ReasoningStep> steps)
        {
            if (steps == null) return null;
            for (int i = steps.Count - 1; i >= 0; i--)
                if (steps[i]?.State == ReasoningState.Running) return steps[i];
            return null;
        }
    }
}
