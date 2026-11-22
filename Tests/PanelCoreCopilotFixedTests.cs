using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Jkr.Panel.Core;
using Xunit;

namespace RevitWebAppSync.Tests
{
    /// <summary>AI-507: a row fixed via Copilot moves to Selesai (not out of every tab); tab counts add up. Pure logic.</summary>
    public class PanelCoreCopilotFixedTests
    {
        private static PanelItem Item(string id, bool human = false) => new PanelItem
        {
            Id = id, Sec = "A", Row = "A1", V = human ? Verify.Human : Verify.Ai, Title = Text.Same("t" + id),
        };

        private static PanelCore Board(int ai, int human = 0, int manual = 0)
        {
            var items = new List<PanelItem>();
            for (var i = 0; i < ai; i++) items.Add(Item("r" + i));
            for (var i = 0; i < human; i++) items.Add(Item("h" + i, true));
            for (var i = 0; i < manual; i++) { var m = Item("m" + i); m.MarkedManual = true; items.Add(m); }
            return new PanelCore(items, null, new ManualClock());
        }

        private static int Sum(PanelCore c) => c.TabCounts().Values.Sum();

        /// <summary>What the add-in does for a Copilot row that re-passed: Fixed, verified, via Copilot.</summary>
        private static void CopilotFix(PanelCore c, string id)
        {
            var x = c.Issues.First(i => i.Id == id);
            x.Status = ItemStatus.Resolved; x.Passed = true; x.FixedViaCopilot = true;
        }

        [Fact]
        public void Copilot_fix_of_four_rows_takes_four_off_Terbuka_and_puts_four_in_Selesai()
        {
            var c = Board(ai: 10, human: 2, manual: 1);
            var before = c.TabCounts();
            for (var i = 0; i < 4; i++) CopilotFix(c, "r" + i);
            var after = c.TabCounts();
            Assert.Equal(before[PanelTab.Open] - 4, after[PanelTab.Open]);
            Assert.Equal(before[PanelTab.Resolved] + 4, after[PanelTab.Resolved]);
            Assert.Equal(before[PanelTab.Manual], after[PanelTab.Manual]);
            c.SetTab(PanelTab.Resolved);
            Assert.Equal(4, c.Filtered().Count);
            Assert.All(c.Filtered(), x => Assert.True(x.FixedViaCopilot));
        }

        [Fact]
        public void Tab_counts_sum_to_the_board_total_before_and_after_a_Copilot_fix()
        {
            var c = Board(ai: 10, human: 2, manual: 1);
            c.Issues.First(x => x.Id == "r9").Status = ItemStatus.Ignored;
            Assert.Equal(13, Sum(c));
            for (var i = 0; i < 4; i++) CopilotFix(c, "r" + i);
            Assert.Equal(13, Sum(c));
        }

        [Fact]
        public void A_marked_manual_row_is_in_the_Manual_list_and_not_the_Open_list()
        {
            var c = Board(ai: 3, manual: 2);
            var n = c.TabCounts();
            c.SetTab(PanelTab.Open);
            Assert.Equal(n[PanelTab.Open], c.Filtered().Count);
            c.SetTab(PanelTab.Manual);
            Assert.Equal(n[PanelTab.Manual], c.Filtered().Count);
        }

        [Fact]
        public void A_row_passed_by_a_scan_without_Copilot_still_drops_out_of_every_tab()
        {
            var c = Board(ai: 3);
            var x = c.Issues[0]; x.Status = ItemStatus.Resolved; x.Passed = true;
            Assert.Equal(2, Sum(c));
        }

        [Fact]
        public void A_Copilot_fixed_row_counts_as_a_passing_cell_in_the_score()
        {
            var c = Board(ai: 3);
            var pass = c.Stats()["A"].Pass;
            CopilotFix(c, "r0");
            Assert.Equal(pass + 1, c.Stats()["A"].Pass);
        }
    }
}
