#nullable enable
// #3 of the handover list: the key-dropping hook in ViewActivation was never seen to act. Decision: keep it (it is the only thing between a
// letter typed in the pane during the view's hold and Revit's canvas, where letters start commands and Delete deletes the selection) and make
// its action provable without restarting Revit. Source pins: ViewActivation needs Revit's UI assemblies.
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class KeyHookProof0121Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheTestHold_CanBeSwitchedOnByAFile_WithoutRestartingRevit()
        {
            var s = Src("BinaVibe/Copilot/Writes/ViewActivation.cs");
            Assert.Contains("view-hold-test.txt", s);
            Assert.Contains("_holdTest = HoldTestNow();", s);                      // read at every activation
            Assert.Contains("s > 0 && s <= 10 ? s : 0", s);                        // 0 to 10 seconds, anything else is the real hold
            Assert.Contains("if (!TestFileIsFresh(System.IO.File.GetLastWriteTimeUtc(path), DateTime.UtcNow)) return 0;", s);   // a forgotten file expires
            Assert.Contains("public const int HoldTestFileMinutes = 30;", s);
            Assert.Contains("BINA_VIEW_HOLD_TEST_SECONDS", s);                     // the environment variable still works
        }

        [Fact]
        public void TheLog_CountsKeyPresses_NotKeyMessages_AndSaysWhenItWasATestHold()
        {
            var s = Src("BinaVibe/Copilot/Writes/ViewActivation.cs");
            Assert.Contains("(((long)l >> 31) & 1) == 0) _dropped++", s);          // bit 31 of lParam: the key's release
            Assert.Contains("key press(es) dropped", s);
            Assert.Contains("(test hold ", s);
        }

        [Fact]
        public void TheRealHold_IsStillAtMostASecond_AndTheHookIsStillInstalledBeforeTheView()
        {
            var s = Src("BinaVibe/Copilot/Writes/ViewActivation.cs");
            Assert.Contains("public const double HoldAtMostSeconds = 1.0;", s);
            Assert.Contains("if (had != IntPtr.Zero) DropKeys(true);   // before the view can get one", s);
        }
    }
}
