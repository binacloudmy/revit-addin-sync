#nullable enable
// Live test of 0.0.99: while a turn ran, the pane showed only "Thinking…" (no step card, no "Step N", no streamed text).
// Cause: ChatView.TryRenderLiveTail swapped the last row with `Children[i] = row`. A panel's indexer only fills an empty
// slot, so every tick threw "Specified index is already in use" (reproduced headless on the PC); the router swallows
// what a live callback throws, so nothing showed until the turn ended and the whole thread drew again.
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class LiveRow0100Tests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        private static string LiveTail()
        {
            var s = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            var from = s.IndexOf("private bool TryRenderLiveTail", System.StringComparison.Ordinal);
            Assert.True(from > 0);
            var to = s.IndexOf("// True while the view is pinned", from, System.StringComparison.Ordinal);
            Assert.True(to > from);
            return s.Substring(from, to - from);
        }

        [Fact]
        public void The_live_row_is_swapped_by_removing_then_inserting_never_by_the_indexer()
        {
            var body = LiveTail();
            Assert.Contains("Children.RemoveAt(i)", body);
            Assert.Contains("Children.Insert(i,", body);
            Assert.DoesNotContain("Children[i] =", body);
        }

        [Fact]
        public void A_row_that_cannot_be_drawn_in_place_falls_back_to_drawing_the_whole_thread()
        {
            var body = LiveTail();
            Assert.Contains("catch (System.Exception", body);
            Assert.Contains("return false;", body);
        }

        [Fact]
        public void No_view_swaps_a_drawn_child_through_a_panel_indexer()
        {
            foreach (var f in Directory.GetFiles(Path.Combine(Root(), "UI", "Copilot"), "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(f);
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, @"\.Children\[[^\]]+\]\s*=\s*[^=]"),
                    f + " assigns a panel child through the indexer");
            }
        }

        [Fact]
        public void A_turn_with_only_the_wait_step_still_has_live_steps_to_draw()
        {
            var steps = new ObservableCollection<ProgressStep>();
            ProgressReducer.Apply(steps, "thinking", "", "Thinking…", "", StepState.Running);
            var visible = ProgressTrail.UserVisible(steps);
            Assert.Single(visible);
            Assert.Equal("Thinking…", visible.Single().Label);
        }

        [Fact]
        public void The_live_card_branch_runs_before_the_plain_thinking_line()
        {
            var s = Src("UI/Copilot/Screens/ChatView.xaml.cs");
            var live = s.IndexOf("col.Children.Add(ActivityPanel(m))", System.StringComparison.Ordinal);
            var plain = s.IndexOf("col.Children.Add(ThinkingTrail(m.Text))", System.StringComparison.Ordinal);
            Assert.True(live > 0 && plain > live);
        }
    }
}
