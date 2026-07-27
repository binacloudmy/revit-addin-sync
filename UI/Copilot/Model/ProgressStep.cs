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
        public StepState State { get => _state; set { _state = value; Raise(nameof(State)); Raise(nameof(ElapsedText)); } }

        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

        private DateTime? _endedUtc = null;
        public DateTime? EndedUtc { get => _endedUtc; set { _endedUtc = value; Raise(nameof(EndedUtc)); Raise(nameof(ElapsedText)); } }

        public string ElapsedText
        {
            get
            {
                if (EndedUtc == null) return "";
                var s = (EndedUtc.Value - StartedUtc).TotalSeconds;
                return s < 0.05 ? "0.0s" : s.ToString("0.0") + "s";
            }
        }

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
                var newStep = new ProgressStep
                {
                    StepId = stepId,
                    Phase = phase,
                    Label = label,
                    Detail = detail,
                    State = state,
                };
                if (state == StepState.Done || state == StepState.Error)
                {
                    newStep.EndedUtc = DateTime.UtcNow;
                }
                steps.Add(newStep);
            }
            else
            {
                if (!string.IsNullOrEmpty(label)) existing.Label = label;
                if (!string.IsNullOrEmpty(detail)) existing.Detail = detail;
                if (state == StepState.Running && existing.State != StepState.Running)
                {
                    // Row re-opened (the writing phase runs once per tool-loop
                    // round) — restart its clock so the elapsed time reflects
                    // the live leg, not the first bracket.
                    existing.StartedUtc = DateTime.UtcNow;
                    existing.EndedUtc = null;
                }
                if ((state == StepState.Done || state == StepState.Error) && existing.EndedUtc == null)
                {
                    existing.EndedUtc = DateTime.UtcNow;
                }
                existing.State = state;
            }
        }

        /// <summary>On SUCCESSFUL completion, flip any row still marked Running to
        /// Done. A finished run has no genuinely-running step — this closes phase
        /// brackets whose backend "done" frame never landed in the snapshot (e.g.
        /// the awaiting-Revit multi-turn path), so the persisted final trail shows
        /// all ✓ instead of leaving phases stuck on ▶. Error rows are left as-is.</summary>
        public static void CompleteRunning(ObservableCollection<ProgressStep> steps)
        {
            if (steps == null) return;
            foreach (var s in steps)
            {
                if (s.State == StepState.Running)
                {
                    if (s.EndedUtc == null) s.EndedUtc = DateTime.UtcNow;
                    s.State = StepState.Done;
                }
            }
        }

        /// <summary>Move the row with this step id to the end of the trail (no-op if
        /// absent). Keeps the "Checking the result" review phase LAST: the backend
        /// emits it in stream turn 1, but on the awaiting-Revit path the real tool
        /// rows are appended later (resume round), which would otherwise leave
        /// review sitting before them.</summary>
        public static void MoveStepToEnd(ObservableCollection<ProgressStep> steps, string stepId)
        {
            if (steps == null || string.IsNullOrEmpty(stepId)) return;
            ProgressStep found = null;
            foreach (var s in steps) { if (s.StepId == stepId) { found = s; break; } }
            if (found == null) return;
            steps.Remove(found);
            steps.Add(found);
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

        /// <summary>Display text for one trail row: the rich backend label, or
        /// the raw step id when no label was supplied. Pure — unit-testable.</summary>
        public static string RowText(ProgressStep s) =>
            s == null ? "" : (string.IsNullOrEmpty(s.Label) ? s.StepId : s.Label);

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

        /// <summary>Summary text for a completed run: count of steps and total elapsed time.
        /// Returns empty string if steps is null or empty. Pure — unit-testable.</summary>
        public static string Summary(IReadOnlyList<ProgressStep> steps)
        {
            if (steps == null || steps.Count == 0) return "";
            var start = steps[0].StartedUtc;
            DateTime end = start;
            foreach (var s in steps)
            {
                if (s.EndedUtc != null && s.EndedUtc.Value > end)
                    end = s.EndedUtc.Value;
            }
            // If any step is still running, use current time if it's later than the max end
            foreach (var s in steps)
            {
                if (s.EndedUtc == null)
                {
                    var now = DateTime.UtcNow;
                    if (now > end)
                        end = now;
                    break;
                }
            }
            var total = (end - start).TotalSeconds;
            // Deliberately WORDLESS. This used to read "✓ 6 langkah · 8.9s",
            // which put a Malay noun on top of an English answer once the reply
            // started mirroring the user's language (UAT 2026-07-27: "how many
            // pipes in this model?" → English answer under a "6 langkah" pill).
            // Translating the chrome would mean the client guessing the reply's
            // language; removing the noun costs nothing and reads the same in
            // both. What the steps WERE is far more useful than the word
            // "steps" — see Preview below, which the chip renders beside this.
            return "✓ " + steps.Count + " · " + total.ToString("0.#") + "s";
        }

        /// <summary>The one step worth showing WHILE the turn runs: the running
        /// one, else the last with a label. Null when there is nothing to show.
        ///
        /// The live trail used to stack every step as it arrived, so a six-step
        /// turn grew a six-line block above the answer — and because two rows
        /// could hold State=Running at once, two spinners turned simultaneously
        /// (UAT 2026-07-27). One line at a time says the same thing without the
        /// pile-up; the full sequence is still there afterwards, behind the
        /// completed reply's chip.</summary>
        public static ProgressStep Current(IReadOnlyList<ProgressStep> steps)
        {
            if (steps == null || steps.Count == 0) return null;
            ProgressStep fallback = null;
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                var s = steps[i];
                if (s == null) continue;
                // Last running step wins: with several marked Running, the most
                // recent is the one actually in flight.
                if (s.State == StepState.Running && !string.IsNullOrWhiteSpace(s.Label))
                    return s;
                if (fallback == null && !string.IsNullOrWhiteSpace(s.Label))
                    fallback = s;
            }
            return fallback;
        }

        /// <summary>Whole-turn elapsed seconds, formatted like "2s" — what the
        /// live line shows instead of a per-step time. Per-step durations came
        /// out as "0.0s" on every status row, because those steps are progress
        /// markers that open and close in the same tick rather than units of
        /// work; nine rows of "0.0s" told the drafter nothing.</summary>
        public static string TotalElapsedText(IReadOnlyList<ProgressStep> steps)
        {
            if (steps == null || steps.Count == 0) return "";
            var start = steps[0].StartedUtc;
            var end = DateTime.UtcNow;
            foreach (var s in steps)
                if (s.EndedUtc != null && s.EndedUtc.Value > end) end = s.EndedUtc.Value;
            var secs = (end - start).TotalSeconds;
            if (secs < 0) secs = 0;
            return secs < 10 ? secs.ToString("0.#") + "s" : ((int)secs) + "s";
        }

        /// <summary>Short hint at WHAT ran, for the collapsed chip: the last
        /// meaningful step's label plus "+N" for the rest. Empty when there is
        /// nothing worth showing. Pure — unit-testable.
        ///
        /// The collapsed state used to carry only a count and a duration, so the
        /// only way to learn whether the copilot had read the right things was to
        /// expand it on every turn. The label text comes from ToolLabels, which
        /// is already human-phrased ("Finding MEP elements").</summary>
        public static string Preview(IReadOnlyList<ProgressStep> steps)
        {
            if (steps == null || steps.Count == 0) return "";
            // Prefer a running step (that is what the user is waiting on), else
            // the last one that carries a real label.
            ProgressStep pick = null;
            foreach (var s in steps)
            {
                if (s.State == StepState.Running && !string.IsNullOrWhiteSpace(s.Label)) { pick = s; break; }
                if (!string.IsNullOrWhiteSpace(s.Label)) pick = s;
            }
            if (pick == null) return "";
            var extra = steps.Count - 1;
            return extra > 0 ? pick.Label + "  +" + extra : pick.Label;
        }
    }
}
