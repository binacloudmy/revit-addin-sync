#nullable enable
// #5 of the handover list: "Undone" showed 10-25 s after undoing a type change and the undo job logged no timing. The job now writes one
// "stages undo job" line (Idling wait, Revit's own undo, the wait to end) and the pane one "undo click" line. Source pins: the job needs Revit.
using System.IO;
using BinaVibe.Copilot.Guard;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class UndoTiming0121Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheUndoJob_MarksItsStages_AndWritesOneLineWhenItEnds()
        {
            var s = Src("BinaVibe/Copilot/Undo/UndoService.cs");
            Assert.Contains("job.Timer = StageTimer.Begin(", s);
            Assert.Contains("job.Timer?.Mark(\"until posting '\" + name + \"' (Idling and checks)\");", s);
            Assert.Contains("job.Timer?.Mark(\"Revit undid '\" + job.InFlight + \"' (post to its DocumentChanged)\");", s);
            Assert.Contains("job.Timer?.Mark(\"Revit redid '\" + job.InFlight + \"' (post to its DocumentChanged)\");", s);
            var finish = s.Substring(s.IndexOf("private static void Finish(Job job, Envelope result)", System.StringComparison.Ordinal));
            Assert.Contains("job.Timer?.Dispose();", finish);
        }

        [Fact]
        public void ThePaneLogsTheClickToTheUndoneLine()
        {
            var s = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("\" click: clear marks \" + clearedMs", s);
        }

        [Fact]
        public void TheStages_GiveOneLine_WithEveryStageNamedInOrder()
        {
            // The same marks UndoService makes, on the real timer (Line() does not touch the shared log sink).
            var t = StageTimer.Begin("undo job (1 step(s))");
            t.Mark("until posting 'BINA: Change type' (Idling and checks)");
            t.Mark("Revit undid 'BINA: Change type' (post to its DocumentChanged)");
            t.Mark("until the job ended (done)");
            var line = t.Line();
            Assert.StartsWith("stages undo job (1 step(s)): until posting", line);
            Assert.True(line.IndexOf("Revit undid", System.StringComparison.Ordinal) > line.IndexOf("until posting", System.StringComparison.Ordinal));
            Assert.True(line.IndexOf("until the job ended", System.StringComparison.Ordinal) > line.IndexOf("Revit undid", System.StringComparison.Ordinal));
            Assert.Contains(" ms, total ", line);
            Assert.Single(line.Split('\n'));
        }

        [Fact]
        public void ThePanesLine_SplitsClearMarks_Job_AndPane_AndCoversEveryOutcome()
        {
            var s = Src("UI/Copilot/CopilotViewModel.cs");
            Assert.Contains("var jobMs = clock.ElapsedMilliseconds - clearedMs;", s);
            Assert.Contains("ms, pane line \" + (clock.ElapsedMilliseconds - clearedMs - jobMs)", s);
            // after the try/catch, so a failed undo is timed too
            Assert.True(s.IndexOf("var jobMs =", System.StringComparison.Ordinal) > s.IndexOf("Could not undo: ", System.StringComparison.Ordinal));
        }
    }
}
