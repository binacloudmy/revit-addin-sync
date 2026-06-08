using System.Collections.ObjectModel;
using System.ComponentModel;

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
}
