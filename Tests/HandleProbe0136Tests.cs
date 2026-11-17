#nullable enable
// Handle chase (overnight soak, 17 Nov): a diagnostic that names the turn stage that gains handles. Pure line rule; the switch file keeps it off by default.
using System.IO;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class HandleProbe0136Tests
    {
        [Fact]
        public void ALine_OnlyWhenTheCountMoved_OrForced()
        {
            Assert.Null(HandleProbe.Line("reply delta", 12000, 12000, force: false));
            Assert.Equal("handles reply delta: 12007 (+7)", HandleProbe.Line("reply delta", 12007, 12000, force: false));
            Assert.Equal("handles turn x start: 12000 (+0)", HandleProbe.Line("turn x start", 12000, 12000, force: true));
            Assert.Equal("handles chat rebuild end: 11990 (-10)", HandleProbe.Line("chat rebuild end", 11990, 12000, force: false));
            Assert.Equal("handles first: 500 (+0)", HandleProbe.Line("first", 500, -1, force: true));   // no earlier mark: nothing to compare
        }

        [Fact]
        public void TheProbe_IsOffUnlessTheSwitchFileExists_AndTheStagesAreWired()
        {
            Assert.EndsWith(Path.Combine("RevitSync", "handle-probe.txt"), HandleProbe.SwitchPath());
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var router = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/CopilotTurnRouter.cs"));
            Assert.Contains("HandleProbe.Mark(\"before publish\")", router);
            Assert.Contains("HandleProbe.Mark(\"after publish (steps drawn)\")", router);
            Assert.Contains("HandleProbe.Mark(\"tools done ", router);
            var chat = File.ReadAllText(Path.Combine(dir.FullName, "UI/Copilot/Screens/ChatView.xaml.cs"));
            Assert.Contains("HandleProbe.Mark(\"chat rebuild start\")", chat);
        }
    }
}
