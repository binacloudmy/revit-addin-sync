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
            Assert.Contains("SelectAfterRedraw(\"look-step-select\"", step);                // its own job, queued after Revit's redraw
        }

        [Fact]
        public void TheSecondJob_IsSkippedForANewerPressOrANewResult()
        {
            var step = Step();
            Assert.Contains("() => at == _lookAt);", step);                            // not for a newer press
            Assert.Contains("if (generation != _generation || !current()) return;", Helper());   // nor for a new result
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

        private static string Helper()
        {
            var s = ResultMarksFocusTests.Src("UI", "Copilot", "Highlights", "InModelResult.cs");
            var h = s.Substring(s.IndexOf("private void SelectAfterRedraw(", System.StringComparison.Ordinal));
            return h.Substring(0, h.IndexOf("/// <summary>The bar's \"Zoom to all\"", System.StringComparison.Ordinal));
        }

        [Fact]
        public void TheSecondJob_ChecksTheViewTheDocumentAndTheElementsAtItsStart_AndRedrawsWhenDone()
        {
            var h = Helper();
            Assert.Contains("view.Id.Value != viewId || DocKey(doc) != docKey", h);        // another view or document since the zoom: nothing selected
            Assert.Contains("eids.Where(e => doc.GetElement(e) != null).ToList()", h);     // deleted or undone since the zoom
            Assert.Contains("if (live.Count == 0) return;", h);
            Assert.Contains("Select(app, live, why, generation);", h);
            Assert.Contains("Draw(a);", h);
            Assert.Contains("RaiseChanged();", h);
        }

        [Fact]
        public void TheZoomJob_RemembersTheViewAndDocumentItZoomedIn()
        {
            var step = Step();
            Assert.Contains("zoomedView = view.Id.Value; zoomedDoc = DocKey(doc);", step);
        }

        private static string Focus()
        {
            var s = ResultMarksFocusTests.Src("UI", "Copilot", "Highlights", "InModelResult.cs");
            var f = s.Substring(s.IndexOf("public void Focus(MarkGroup target, bool select)", System.StringComparison.Ordinal));
            return f.Substring(0, f.IndexOf("public void SelectAll()", System.StringComparison.Ordinal));
        }

        [Fact]
        public void ARowClick_ZoomsInOneJob_AndSelectsInASecond()
        {
            var focus = Focus();
            var zoom = focus.IndexOf("BinaVibe.Mcp.Tools.ViewZoom.ZoomTo(uidoc, here)", System.StringComparison.Ordinal);
            var ret = focus.IndexOf("return;", zoom, System.StringComparison.Ordinal);
            Assert.True(zoom > 0 && ret > zoom);
            Assert.DoesNotContain("Select(app", focus.Substring(zoom, ret - zoom));      // no select in the zoom's branch
            Assert.Contains("if (select) { selectNext = eids; zoomedView = view.Id.Value; zoomedDoc = DocKey(doc); }", focus);
            Assert.Contains("SelectAfterRedraw(\"focus-select-2\", \"focus-select\", eids, zoomedView, zoomedDoc, generation, () => ReferenceEquals(_focusIds, mine));", focus);
        }

        [Fact]
        public void ARowClickForAnElementInAnotherView_StillSelectsInTheSameJob()
        {
            var focus = Focus();
            var other = focus.IndexOf("if (select) Select(app, eids, \"focus-select\", generation);", System.StringComparison.Ordinal);
            Assert.True(other > 0);
            Assert.True(focus.IndexOf("MostOwner(doc, eids, view.Id)", other, System.StringComparison.Ordinal) > other);
        }
    }
}
