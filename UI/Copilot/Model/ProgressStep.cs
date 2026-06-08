using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum StepState { Running, Done, Error }

    /// <summary>
    /// One row in the copilot's live progress trail (spinner -> checkmark).
    /// Implements INotifyPropertyChanged so the bound card live-updates as
    /// running->done events arrive without re-creating the whole chat message.
    /// </summary>
    public sealed class ProgressStep : INotifyPropertyChanged
    {
        public string StepId { get; init; } = "";
        public string Phase { get; init; } = "";

        private string _label = "";
        public string Label { get => _label; set { _label = value; Raise(nameof(Label)); } }

        private string _detail = "";
        public string Detail { get => _detail; set { _detail = value; Raise(nameof(Detail)); } }

        private StepState _state = StepState.Running;
        public StepState State { get => _state; set { _state = value; Raise(nameof(State)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>
    /// Pure reducer: applies one parsed progress event to a step collection.
    /// A new step_id appends a row; an existing step_id updates it in place
    /// (so a running->done pair collapses onto one row). No UI, no I/O —
    /// directly unit-testable.
    /// </summary>
    public static class ProgressReducer
    {
        public static void Apply(ObservableCollection<ProgressStep> steps, string stepId,
                                 string phase, string label, string detail, StepState state)
        {
            if (steps == null) return;
            ProgressStep existing = null;
            foreach (var s in steps)
            {
                if (s.StepId == stepId) { existing = s; break; }
            }
            if (existing == null)
            {
                steps.Add(new ProgressStep
                {
                    StepId = stepId,
                    Phase = phase,
                    Label = label,
                    Detail = detail,
                    State = state,
                });
            }
            else
            {
                if (!string.IsNullOrEmpty(label)) existing.Label = label;
                if (!string.IsNullOrEmpty(detail)) existing.Detail = detail;
                existing.State = state;
            }
        }
    }

    /// <summary>
    /// Renders a step collection into the plain-text trail shown in the chat's
    /// "thinking" bubble (▶ running, ✓ done, ✗ error), collapsing to a
    /// "Done — N steps" summary when the run finishes. Pure + testable; the
    /// addin keeps no XAML card for this — it reuses the existing bubble.
    /// </summary>
    public static class ProgressTrail
    {
        public static StepState StateFrom(string wireState)
        {
            if (string.Equals(wireState, "done", StringComparison.OrdinalIgnoreCase)) return StepState.Done;
            if (string.Equals(wireState, "error", StringComparison.OrdinalIgnoreCase)) return StepState.Error;
            return StepState.Running;
        }

        public static string Glyph(StepState st) =>
            st == StepState.Done ? "✓" : st == StepState.Error ? "✗" : "▶";

        public static string Render(IEnumerable<ProgressStep> steps, bool collapsed = false)
        {
            var list = steps as IList<ProgressStep> ?? new List<ProgressStep>(steps ?? new List<ProgressStep>());
            if (collapsed)
                return list.Count == 1 ? "Done — 1 step" : $"Done — {list.Count} steps";
            var sb = new StringBuilder();
            foreach (var s in list)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(Glyph(s.State)).Append(' ')
                  .Append(string.IsNullOrEmpty(s.Label) ? s.StepId : s.Label);
            }
            return sb.ToString();
        }
    }
}
