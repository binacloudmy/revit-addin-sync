#nullable enable
// Live test of 0.0.114: ask-form option buttons cut long labels; the isolate turn took 40 s and the first click after a turn queued 4 s with no
// way to see why. Option labels wrap; every stage of a write and every long Revit-thread job leaves a line in the click log; the snapshot
// refresh steps aside for queued work of ours.
using System;
using System.Collections.Generic;
using System.IO;
using BinaVibe.Copilot.Guard;
using Xunit;

namespace RevitAddinSync.Tests
{
    [Collection("ClickTrace")]
    public class WrapAndTrace0114Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        // ---- wrapping labels ----

        [Fact]
        public void TheKitsButtonTemplate_IsAGrid_SoALabelCanWrap()
        {
            var kit = Src("UI/Copilot/Controls/CardKit.cs");
            Assert.Contains("<Grid HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'>", kit);
            Assert.Contains("<ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/>", kit);
            Assert.DoesNotContain("<StackPanel Orientation='Horizontal' HorizontalAlignment='Center' VerticalAlignment='Center'>", kit);
            Assert.Contains("x:Name='spin'", kit);
            Assert.Contains("public static Button WrapLabel(Button button, string label)", kit);
            Assert.Contains("TextWrapping = TextWrapping.Wrap", kit);
        }

        [Fact]
        public void TheAskCardsOptionButtonsAndCheckBoxes_Wrap()
        {
            var ask = Src("UI/Copilot/Controls/AskCard.cs");
            Assert.Contains("CardKit.WrapLabel(CardKit.Secondary(o.Label,", ask);
            Assert.Contains("Content = new TextBlock { Text = o.Label, TextWrapping = TextWrapping.Wrap }", ask);
        }

        // ---- the stage timer ----

        [Fact]
        public void AStageTimer_WritesOneLine_WithEveryStageAndTheTotal()
        {
            var lines = new List<string>();
            var old = StageTimer.Sink;
            StageTimer.Sink = lines.Add;
            try
            {
                using (var st = StageTimer.Begin("preview batch"))
                {
                    System.Threading.Thread.Sleep(20);
                    st.Mark("plan");
                    System.Threading.Thread.Sleep(20);
                    st.Mark("run call 0 (run_revit_code)");
                }
                var line = Assert.Single(lines);
                Assert.StartsWith("stages preview batch: plan ", line);
                Assert.Contains(", run call 0 (run_revit_code) ", line);
                Assert.Contains(", total ", line);
                Assert.EndsWith(" ms", line);
            }
            finally { StageTimer.Sink = old; }
        }

        [Fact]
        public void AFastTimer_BelowItsMinimum_WritesNothing_AndASinkThatThrowsBreaksNothing()
        {
            var lines = new List<string>();
            var old = StageTimer.Sink;
            StageTimer.Sink = lines.Add;
            try
            {
                using (StageTimer.Begin("idling snapshot", 5000)) { }
                Assert.Empty(lines);
                StageTimer.Sink = _ => throw new InvalidOperationException("disk full");
                using (StageTimer.Begin("x")) { }
            }
            finally { StageTimer.Sink = old; }
        }

        [Fact]
        public void TheWriteEnginesStages_AreMarked()
        {
            var e = Src("BinaVibe/Copilot/Writes/RevitWriteEngine.cs");
            foreach (var m in new[] { "StageTimer.Begin(\"preview batch\")", "StageTimer.Begin(\"commit batch\")", "st.Mark(\"categories of the targets\")",
                                       "st.Mark(\"value fingerprints\")", "st.Mark(\"thumbnail\")", "st.Mark(\"rollback\")", "st.Mark(\"assimilate\")" })
                Assert.Contains(m, e);
            var host = Src("BinaVibe/Copilot/Code/RevitCodeHost.cs");
            Assert.Contains("st.Mark(\"ids before (\" + before.Count + \" elements)\")", host);
            Assert.Contains("st.Mark(\"code (\" + run.Ticks + \" steps)\")", host);
        }

        // ---- the snapshot refresh steps aside ----

        [Fact]
        public void TheIdlingRefresh_StepsAsideForQueuedWork_AndALongJobIsLogged()
        {
            var snap = Src("BinaVibe/Copilot/Read/SnapshotService.cs");
            Assert.Contains("if (BinaVibe.Copilot.Writes.RevitThread.HasQueuedWork) return;", snap);
            var thread = Src("BinaVibe/Copilot/Writes/RevitThread.cs");
            Assert.Contains("public static bool HasQueuedWork => !Queue.IsEmpty;", thread);
            Assert.Contains("revit thread job ", thread);
        }
    }
}
