#nullable enable
// #4 of the handover list: the first stepper press in a large plan took ~5 s (Revit's redraw after the zoom) and the keys typed in
// that time were lost, because the select (ViewActivation.Show: the view keeps the keyboard and drops keys until Revit's idle calls)
// ran in the same job as the zoom. The zoom now runs alone and the select is a second job queued after it.
using Xunit;

namespace RevitAddinSync.Tests
{
    public class StepperKeyboard0121Tests
    {
        private static string Step()
        {
            var s = ResultMarksFocusTests.Src("UI", "Copilot", "Highlights", "InModelResult.cs");
            var step = s.Substring(s.IndexOf("public void Step(int delta)", System.StringComparison.Ordinal));
            return step.Substring(0, step.IndexOf("public void ZoomToAll()", System.StringComparison.Ordinal));
        }

        [Fact]
        public void TheZoomJob_DoesNotSelect_TheSelectIsASecondJob()
        {
            var step = Step();
            var zoom = step.IndexOf("BinaVibe.Mcp.Tools.ViewZoom.ZoomTo(uidoc, here)", System.StringComparison.Ordinal);
            var ret = step.IndexOf("return;", zoom, System.StringComparison.Ordinal);
            Assert.True(zoom > 0 && ret > zoom);
            Assert.DoesNotContain("Select(", step.Substring(zoom, ret - zoom));        // no select in the zoom's branch
            Assert.Contains("selectNext = eids;", step);
            Assert.Contains("Run(\"look-step-select\"", step);                          // its own job, queued after Revit's redraw
        }

        [Fact]
        public void TheSecondJob_IsSkippedForANewerPressOrANewResult()
        {
            var step = Step();
            Assert.Contains("if (generation != _generation || at != _lookAt) return;", step);
        }

        [Fact]
        public void APressForAnElementInAnotherView_StillSelectsInTheSameJob_AndRequestsTheViewChange()
        {
            var step = Step();
            var other = step.IndexOf("Select(app, eids, \"look-step\", generation);", System.StringComparison.Ordinal);
            Assert.True(other > 0);
            Assert.True(step.IndexOf("MostOwner(doc, eids, view.Id)", other, System.StringComparison.Ordinal) > other);   // select, then the view change
            Assert.Contains("uidoc.RequestViewChange(other)", step);
        }

        [Fact]
        public void TheSecondJob_RedrawsTheMarks_WhenItIsDone()
        {
            var step = Step();
            var second = step.Substring(step.IndexOf("Run(\"look-step-select\"", System.StringComparison.Ordinal));
            Assert.Contains("Select(a, eids, \"look-step\", generation);", second);
            Assert.Contains("Draw(a);", second);
            Assert.Contains("RaiseChanged();", second);
        }
    }
}
