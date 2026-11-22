#nullable enable
// Live latency probe of 0.0.106: Zoom and Copy table froze Revit for seconds, buttons gave no feedback, Open as grid was a white
// window. The click trace, the one-call zoom and selection, the off-thread clipboard, the button guard, the dark grid.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    [Collection("ClickTrace")]
    public class Latency0107Tests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        // ---- the click trace ----

        [Fact]
        public void TheLine_SaysQueueAndRunSeparately()
        {
            var lines = new List<string>();
            var old = ClickTrace.Sink;
            ClickTrace.Sink = lines.Add;
            try
            {
                var op = ClickTrace.Begin("zoom-group", 3493);
                op.HandedOff();
                System.Threading.Thread.Sleep(30);
                op.Started();
                System.Threading.Thread.Sleep(30);
                ClickTrace.End(op, "ok");
                var line = Assert.Single(lines);
                Assert.StartsWith("click zoom-group n=3493: to-revit ", line);
                Assert.Contains(", queued ", line);
                Assert.Contains(", ran ", line);
                Assert.Contains(", total ", line);
                Assert.EndsWith("(ok)", line);
                Assert.True(op.QueueMs >= 20, "queue " + op.QueueMs);
                Assert.True(op.RunMs >= 20, "run " + op.RunMs);
                Assert.True(op.TotalMs >= op.QueueMs + op.RunMs);
            }
            finally { ClickTrace.Sink = old; }
        }

        [Fact]
        public void AnOpThatNeverStarted_ShowsDashes_NotNegativeNumbers()
        {
            var op = ClickTrace.Begin("x");
            op.Finished();
            var line = op.Line();
            Assert.Contains("to-revit -", line);
            Assert.Contains("queued -", line);
            Assert.Contains("ran -", line);
            Assert.DoesNotContain("n=", line);
        }

        [Fact]
        public void Ending_RaisesTheEvent_CountsOpened_AndASinkThatThrowsBreaksNothing()
        {
            var old = ClickTrace.Sink;
            ClickTrace.Sink = _ => throw new System.InvalidOperationException("disk full");
            ClickOp? seen = null;
            void On(ClickOp o) => seen = o;
            ClickTrace.Ended += On;
            try
            {
                var before = ClickTrace.Opened;
                var op = ClickTrace.Begin("y");
                Assert.Equal(before + 1, ClickTrace.Opened);
                ClickTrace.End(op);
                Assert.Same(op, seen);
                ClickTrace.End(null);   // nothing to end: fine
            }
            finally { ClickTrace.Ended -= On; ClickTrace.Sink = old; }
        }

        [Fact]
        public void TheLogIsUnderTheBinaLogsFolder()
        {
            var s = Src("UI/Copilot/Model/ClickTrace.cs");
            Assert.Contains("\"Bina\", \"RevitSync\", \"logs\", \"copilot-clicks.log\"", s);
        }

        // ---- Revit side ----

        [Fact]
        public void EveryInModelClick_IsTraced_AndNoneCallsRunAsyncDirectly()
        {
            var s = Src("UI/Copilot/Highlights/InModelResult.cs");
            Assert.Contains("TracedRevit.Run<object?>(label, count", s);
            Assert.DoesNotContain("RevitThread.RunAsync", s);
            // Result marks design: Before/After (rings-on/off) became Hide / Show again; Open opens the view that holds them.
            foreach (var label in new[] { "show-in-model", "select-group", "zoom-group", "type-properties", "marks-hide", "marks-show-again", "clear-marks", "open-view" })
                Assert.Contains("\"" + label + "\"", s);
        }

        [Fact]
        public void ZoomAndSelect_UseOneCall_NotShowElements()
        {
            var inModel = Src("UI/Copilot/Highlights/InModelResult.cs");
            Assert.DoesNotContain("uidoc.ShowElements(", inModel);
            Assert.Contains("BinaVibe.Mcp.Tools.ViewZoom.ZoomTo(uidoc, ids)", inModel);
            Assert.Contains("uidoc.Selection.SetElementIds(ids)", inModel);
            var insp = Src("BinaVibe/Mcp/Tools/Inspectors.cs");
            Assert.Contains("ViewZoom.ZoomTo(uidoc, ids)", insp);
            Assert.DoesNotContain("try { uidoc.ShowElements(ids); } catch { /* zoom best-effort */ }", insp);
            var zoom = Src("BinaVibe/Mcp/Tools/ViewZoom.cs");
            Assert.Contains("ZoomAndCenterRectangle", zoom);
            Assert.Contains("get_BoundingBox(view)", zoom);
        }

        [Fact]
        public void TheInstancesOfAType_AreALookupAndCached_NotAScanOfTheModelEveryClick()
        {
            var s = Src("UI/Copilot/Highlights/InModelResult.cs");
            Assert.Contains("GetDependentElements(notTypes)", s);
            Assert.Contains("_instanceCache", s);
            Assert.Contains("best.Count >= MaxMarks", s);             // the mark cap is a counter, not a Count() per element
            Assert.DoesNotContain("_marks.Count(m => m.Outcome ==", s);
        }

        // ---- the clipboard, the guard, the grid ----

        [Fact]
        public void NoResultButton_SetsTheClipboardOnTheUiThread()
        {
            foreach (var f in new[] { "ChangesBody", "CreatedCard", "ItemsCard", "ResultCardView" })
                Assert.DoesNotContain("Clipboard.SetText", Src("UI/Copilot/Controls/" + f + ".cs"));
            var kit = Src("UI/Copilot/Controls/ClipboardKit.cs");
            Assert.Contains("ApartmentState.STA", kit);
            Assert.Contains("Clipboard.SetDataObject(t, true);", kit);
            Assert.Contains("ClipboardKit.CopyAsync(() => ChangeTable.TabText(card), \"copy-table\"", Src("UI/Copilot/Controls/ChangesBody.cs"));
            Assert.Contains("ClipboardKit.CopyAsync(() => by.CopyText(), \"copy-list\"", Src("UI/Copilot/Controls/CreatedCard.cs"));
        }

        [Fact]
        public void TheButtons_SpinAndIgnoreRepeats_WhileWorkIsOutstanding()
        {
            var kit = Src("UI/Copilot/Controls/CardKit.cs");
            Assert.Contains("x:Name='spin'", kit);
            Assert.Contains("ClickGuard.Run(b, onClick)", kit);
            var guard = Src("UI/Copilot/Controls/ClickGuard.cs");
            Assert.Contains("if (Busy.Contains(button)) return;", guard);
            Assert.Contains("ClickTrace.Opened == before", guard);
            Assert.Contains("Later(button, 200", guard);      // the spinner shows after 200 ms, so a quick click never flickers
            Assert.Contains("Later(button, 20000", guard);    // and never for ever
            Assert.Contains("ClickTrace.Ended +=", guard);
        }

        [Fact]
        public void TheRuleHighlightAndShowThem_AreTraced()
        {
            Assert.Contains("ClickTrace.Begin(\"rule-highlight\")", Src("UI/Copilot/CopilotViewModel.cs"));
            Assert.Contains("Model.ClickTrace.Begin(\"select-elements\"", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
        }

        [Fact]
        public void TheGridWindow_IsDark_FromTheTokens()
        {
            var s = Src("UI/Copilot/Controls/GridWindow.cs");
            foreach (var token in new[] { "Cp.PanelBg", "Cp.Bg", "Cp.Raised", "Cp.Muted", "Cp.Ink", "Cp.Line", "Cp.Hover" })
                Assert.Contains("\"" + token + "\"", s);
            Assert.DoesNotContain("\"#", s);
            Assert.Contains("ColumnHeaderStyle = header", s);
            Assert.Contains("CellStyle = cell", s);
            Assert.Contains("new Thickness(12, 8, 12, 8)", s);      // the cell text carries the padding
            Assert.Contains("ElementStyle = text", s);
        }
    }

    [CollectionDefinition("ClickTrace", DisableParallelization = true)]
    public class ClickTraceCollection { }
}
