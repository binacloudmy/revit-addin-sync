#nullable enable
// "List X with Y": when the card draws the same rows as the model's markdown table, the table is not shown a second time under "More detail". Any doubt keeps it.
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TableDedupe0140Tests
    {
        private static ResultItems Items(params (string Title, string Area)[] rows)
        {
            var s = new ItemSection();
            foreach (var r in rows)
                s.Rows.Add(new ItemRow { Kind = "row", Title = r.Title, Count = 1, Cells = new List<(string, string)> { ("Area", r.Area) } });
            var items = new ResultItems();
            items.Sections.Add(s);
            return items;
        }

        private const string Table = "| No. | Room | Area |\n|---|---|---|\n| 1 | Lobby | 40 |\n| 2 | **Hall** | 60 |\n| 3 | Store | 9 |";

        [Fact]
        public void TheSameRows_DropTheTable_KeepTheOtherText()
        {
            var items = Items(("Lobby", "40"), ("hall", "60"), ("Store ", "9"));
            var text = "Details below.\n\n" + Table + "\n\nTotal 109 m².";
            Assert.Equal("Details below.\n\nTotal 109 m².", TableDedupe.Strip(text, items));
            Assert.Equal("", TableDedupe.Strip(Table, items));
        }

        [Fact]
        public void AnyDoubt_KeepsTheTable()
        {
            var text = "Intro\n\n" + Table;
            Assert.Equal(text, TableDedupe.Strip(text, Items(("Lobby", "40"), ("Hall", "60"))));                         // fewer rows on the card
            Assert.Equal(text, TableDedupe.Strip(text, Items(("Lobby", "40"), ("Hall", "60"), ("Garage", "9"))));       // another title
            Assert.Equal(text, TableDedupe.Strip(text, null));
            var wide = "| No. | Room | Area | Level | Use | Owner |\n|---|---|---|---|---|---|\n| 1 | Lobby | 40 | 1 | a | b |\n| 2 | Hall | 60 | 1 | a | b |";
            Assert.Equal(wide, TableDedupe.Strip(wide, Items(("Lobby", "40"), ("Hall", "60"))));                         // columns the card cannot show
        }

        [Fact]
        public void ACardLongerThanItShows_KeepsTheTable()
        {
            var rows = Enumerable.Range(1, ItemsBody.MaxRowsPerSection + 1).Select(i => ("R" + i, "1")).ToArray();
            var md = "| Room | Area |\n|---|---|\n" + string.Join("\n", rows.Select(r => "| " + r.Item1 + " | 1 |"));
            Assert.Equal(md, TableDedupe.Strip(md, Items(rows)));
        }
    }
}
