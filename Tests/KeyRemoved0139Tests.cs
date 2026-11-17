#nullable enable
// The user (18 Nov) asked for the floating key over the Revit canvas to go: the dark pill with "1 door changed . Not visible in this view" and
// Select all, Hide, Undo and an X. Its actions are on the result card: Hide marks / Show in model and Go to view (the marks header), Undo and Undo last
// part (the action row), and, new, Select all and Zoom to all beside the marks title. Not on the card any more: the stepper of the findings
// ("1 of 3", previous/next) and the X (Dismiss: Hide marks does the same). Source pins: the overlay draws nothing, the card has the buttons.
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class KeyRemoved0139Tests
    {
        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheOverlay_NoLongerShowsTheKey()
        {
            var s = Src("UI/Copilot/Highlights/HighlightOverlay.cs");
            var body = s.Substring(s.IndexOf("public bool ShowResult(", System.StringComparison.Ordinal));
            body = body.Substring(0, body.IndexOf("public void ClearResult()", System.StringComparison.Ordinal));
            Assert.Contains("_win?.ClearResult(); _win?.Hide();", body);
            Assert.DoesNotContain("RenderResult", body);
            Assert.DoesNotContain("_win.Show()", body);
            Assert.DoesNotContain("PlaceKey", body);
        }

        [Fact]
        public void TheCard_CarriesWhatTheKeyOffered()
        {
            var card = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.Contains("CardKit.Ghost(\"Select all\", actions.SelectAll, compact: true)", card);
            Assert.Contains("CardKit.Ghost(\"Zoom to all\", actions.ZoomAll, compact: true)", card);
            Assert.Contains("Undo last part", card);                       // the action row's undo stays
            Assert.Contains("MarksButton.Build(actions, ghost: true)", card);   // Hide marks / Show in model stays
            Assert.Contains("ZoomAll = () => Vm?.ZoomAllInModel(m),", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
            Assert.Contains("void ZoomAllMarks();", Src("UI/Copilot/Ports/IRevitActions.cs"));
            Assert.Contains("public void ZoomAllMarks() => _inModel()?.ZoomToAll();", Src("UI/Copilot/Ports/RevitActions.cs"));
        }
    }
}
