#nullable enable
// The decision card's focus rules inside Revit (live R4/R5) cannot run without WPF and Revit, so these pin the
// source the way ResultKit0103Tests does: the parts that made the keys arrive and the pane stay still must not be
// lost in a later edit. Only a live run proves the behaviour.

using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class AskCardSourceTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "";
        }

        private static string Card() => File.ReadAllText(Path.Combine(Root(), "UI", "Copilot", "Controls", "AskCard.cs"));

        [Fact]
        public void TheKeysGoThroughATextBoxSink()
        {
            var s = Card();
            Assert.Contains("private TextBox MakeSink()", s);
            Assert.Contains("PreviewTextInput", s);
            Assert.Contains("AskCardKeys.ForChar", s);
            Assert.Contains("AskCardKeys.ForEnter", s);
        }

        [Fact]
        public void CardButtonsTakeNoFocus_AndPickOnMouseDown()
        {
            var s = Card();
            Assert.Contains("Focusable = false,", s);
            Assert.Contains("ClickMode = ClickMode.Press,", s);
        }

        [Fact]
        public void ABackgroundClickNeverReachesThePaneScrollViewer_AndFocusNeverScrolls()
        {
            var s = Card();
            Assert.Contains("_outer.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnCardMouseDownBubble), false);", s);
            Assert.Contains("e.Handled = true;", s);
            Assert.Contains("RequestBringIntoViewEvent", s);
            Assert.Contains("ScrollToVerticalOffset(before.Value)", s);
        }
    }
}
