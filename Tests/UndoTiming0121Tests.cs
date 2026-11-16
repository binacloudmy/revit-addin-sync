#nullable enable
// #5 of the handover list: "Undone" showed 10-25 s after undoing a type change and the undo job logged no timing. The job now writes one
// "stages undo job" line (Idling wait, Revit's own undo, the wait to end) and the pane one "undo click" line. Source pins: the job needs Revit.
using System.IO;
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
    }
}
