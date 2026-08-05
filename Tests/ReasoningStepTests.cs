using System.Collections.ObjectModel;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    // 2026-08-02 copilot-reasoning-ui spec — same style as ProgressReducerTests,
    // covering ReasoningReducer's delta-append contract (distinct from
    // ProgressReducer: a `reasoning` event's text_delta is APPENDED to the
    // existing row, never replaces it).
    public class ReasoningStepTests
    {
        [Fact]
        public void New_step_id_appends_a_row()
        {
            var steps = new ObservableCollection<ReasoningStep>();
            ReasoningReducer.Apply(steps, "s1", "Understanding request", "User wants ", ReasoningState.Running);
            Assert.Single(steps);
            Assert.Equal("Understanding request", steps[0].Label);
            Assert.Equal("User wants ", steps[0].Text);
            Assert.Equal(ReasoningState.Running, steps[0].State);
        }

        [Fact]
        public void Same_step_id_appends_text_delta_not_replaces()
        {
            var steps = new ObservableCollection<ReasoningStep>();
            ReasoningReducer.Apply(steps, "s1", "Inspecting model", "Scanning ducts", ReasoningState.Running);
            ReasoningReducer.Apply(steps, "s1", "Inspecting model", "... found 319.", ReasoningState.Running);
            Assert.Single(steps);
            Assert.Equal("Scanning ducts... found 319.", steps[0].Text);
        }

        [Fact]
        public void Distinct_ids_append_separate_rows()
        {
            var steps = new ObservableCollection<ReasoningStep>();
            ReasoningReducer.Apply(steps, "s1", "Understanding request", "text", ReasoningState.Running);
            ReasoningReducer.Apply(steps, "s2", "Inspecting model", "text", ReasoningState.Running);
            Assert.Equal(2, steps.Count);
        }

        [Fact]
        public void Empty_delta_does_not_blank_existing_text()
        {
            var steps = new ObservableCollection<ReasoningStep>();
            ReasoningReducer.Apply(steps, "s1", "Working", "hello", ReasoningState.Running);
            ReasoningReducer.Apply(steps, "s1", "", "", ReasoningState.Done);
            Assert.Equal("hello", steps[0].Text);
            Assert.Equal("Working", steps[0].Label);
            Assert.Equal(ReasoningState.Done, steps[0].State);
        }

        [Fact]
        public void CompleteRunning_flips_running_rows_to_done()
        {
            var steps = new ObservableCollection<ReasoningStep>();
            ReasoningReducer.Apply(steps, "s1", "A", "x", ReasoningState.Running);
            ReasoningReducer.Apply(steps, "s2", "B", "y", ReasoningState.Done);
            ReasoningReducer.CompleteRunning(steps);
            Assert.Equal(ReasoningState.Done, steps[0].State);
            Assert.Equal(ReasoningState.Done, steps[1].State);
        }

        [Fact]
        public void StateFrom_maps_wire_strings_case_insensitively()
        {
            Assert.Equal(ReasoningState.Done, ReasoningReducer.StateFrom("done"));
            Assert.Equal(ReasoningState.Done, ReasoningReducer.StateFrom("DONE"));
            Assert.Equal(ReasoningState.Running, ReasoningReducer.StateFrom("running"));
            Assert.Equal(ReasoningState.Running, ReasoningReducer.StateFrom(null));
        }

        [Fact]
        public void StepBadge_pluralises_correctly()
        {
            Assert.Equal("1 step", ReasoningTrail.StepBadge(1));
            Assert.Equal("5 steps", ReasoningTrail.StepBadge(5));
            Assert.Equal("0 steps", ReasoningTrail.StepBadge(0));
        }

        [Fact]
        public void ElapsedLabel_uses_english_chrome_streaming_vs_done()
        {
            var streaming = ReasoningTrail.ElapsedLabel(2.34, streaming: true);
            var done = ReasoningTrail.ElapsedLabel(8.0, streaming: false);
            Assert.Contains("Thinking…", streaming);
            Assert.Contains("2.3", streaming);
            Assert.Equal("Thinking 8s", done);
        }

        [Fact]
        public void Current_returns_last_running_step_or_null()
        {
            var steps = new ObservableCollection<ReasoningStep>();
            Assert.Null(ReasoningTrail.Current(steps));

            ReasoningReducer.Apply(steps, "s1", "A", "x", ReasoningState.Done);
            Assert.Null(ReasoningTrail.Current(steps));

            ReasoningReducer.Apply(steps, "s2", "B", "y", ReasoningState.Running);
            Assert.Equal("s2", ReasoningTrail.Current(steps).StepId);
        }

        // 2026-08-02 live-blank-rows defect fix — ReasoningTimelineView's body
        // rebuild is gated on RenderKey so it doesn't stutter its own caret
        // animation on every 15ms delta tick. The OLD key was
        // `streaming|count|lastRow.Text.Length` — these tests pin down exactly
        // the class of update that key missed: anything that isn't a brand-new
        // row AND isn't a length change on whichever row happens to be last.
        // The backend reuses a small, fixed set of step_ids ("gather"/"run"/
        // "gate:compile"/...), so a row frequently stops being "last" the
        // moment a later step_id is first seen — after that, its own updates
        // must still change the key, which is what these assert.

        [Fact]
        public void RenderKey_changes_when_a_non_last_rows_label_changes()
        {
            var steps = new ObservableCollection<ReasoningStep>();
            ReasoningReducer.Apply(steps, "gather", "Collecting information", "", ReasoningState.Running);
            ReasoningReducer.Apply(steps, "run", "Generating answer", "", ReasoningState.Running);
            var before = ReasoningTrail.RenderKey(true, steps);

            // "gather" is no longer the last row ("run" is) — a label-only
            // update on it must still change the key.
            ReasoningReducer.Apply(steps, "gather", "Route duct → 3 segments", "", ReasoningState.Running);
            var after = ReasoningTrail.RenderKey(true, steps);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void RenderKey_changes_when_a_non_last_rows_text_grows()
        {
            var steps = new ObservableCollection<ReasoningStep>();
            ReasoningReducer.Apply(steps, "gather", "Collecting information", "", ReasoningState.Running);
            ReasoningReducer.Apply(steps, "run", "Generating answer", "", ReasoningState.Running);
            var before = ReasoningTrail.RenderKey(true, steps);

            ReasoningReducer.Apply(steps, "gather", "", "route_duct → 3 segments, 1 fitting", ReasoningState.Running);
            var after = ReasoningTrail.RenderKey(true, steps);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void RenderKey_changes_when_a_non_last_rows_state_flips_to_done()
        {
            var steps = new ObservableCollection<ReasoningStep>();
            ReasoningReducer.Apply(steps, "gather", "Collecting information", "", ReasoningState.Running);
            ReasoningReducer.Apply(steps, "run", "Generating answer", "", ReasoningState.Running);
            var before = ReasoningTrail.RenderKey(true, steps);

            ReasoningReducer.Apply(steps, "gather", "Collecting information", "", ReasoningState.Done);
            var after = ReasoningTrail.RenderKey(true, steps);

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void RenderKey_is_stable_when_nothing_visible_changed()
        {
            var steps = new ObservableCollection<ReasoningStep>();
            ReasoningReducer.Apply(steps, "s1", "Working", "hello", ReasoningState.Running);
            var a = ReasoningTrail.RenderKey(true, steps);
            var b = ReasoningTrail.RenderKey(true, steps);
            Assert.Equal(a, b);
        }
    }
}
