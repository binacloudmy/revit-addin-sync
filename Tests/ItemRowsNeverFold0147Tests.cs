#nullable enable
// r13 (0.0.147), live turn ce75668c: the stored result had 3 rows (FD2 1 HOUR, FD3 2 HOUR, FD1 1 HOUR) but the card drew a
// "1 HOUR x2" row and only the FD3 row: the not-done fold (rows that repeat one message) took the shared Fire Rating value as
// a message. Only a not-done row (kind "row" with a "state") folds now; every stored row is drawn.
using System.Linq;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ItemRowsNeverFold0147Tests
    {
        private const ReceiptLanguage En = ReceiptLanguage.En;

        // The turn_end result of ce75668c as stored (not_searched trimmed to one link).
        private const string Stored = "{\"kind\":\"selection\",\"lang\":\"en\","
            + "\"items\":[{\"kind\":\"row\",\"cells\":{\"Fire Rating\":\"1 HOUR\"},\"count\":2,\"title\":\"FD2 – (PTa1) 1000 x 2400\"},"
            + "{\"kind\":\"row\",\"cells\":{\"Fire Rating\":\"2 HOUR\"},\"count\":2,\"title\":\"FD3 – (PTa1) 700 x 1800\"},"
            + "{\"kind\":\"row\",\"cells\":{\"Fire Rating\":\"1 HOUR\"},\"count\":2,\"title\":\"FD1 – (PTa2) 1700x 2400\"}],"
            + "\"reply\":\"Selected 6 fire doors on PTBR_Aras 01: 2 x FD2, 2 x FD3 and 2 x FD1, all with a Fire Rating.\","
            + "\"checks\":[{\"text\":\"Only the host model was searched; all 6 links are unloaded, so linked fire doors were not checked.\",\"severity\":\"info\"}],"
            + "\"counts\":{\"elements\":6},\"not_searched\":[\"link: jkrST22_4a_(BEsm_19-001)_A-PTBR_w01_(S)_230706 (unloaded)\"]}";

        [Fact]
        public void TheLiveFireDoors_DrawAllThreeRows_NoneFolded()
        {
            var result = CopilotJson.Deserialize<TurnResultDto>(Stored)!;
            var items = ResultItems.From(result)!;
            var section = Assert.Single(items.Sections);
            Assert.Equal(3, section.Rows.Count);
            var units = ItemsBody.Units(section, En);
            Assert.Equal(3, units.Count);
            Assert.All(units, u => Assert.False(u.IsFolded));
            Assert.Equal(new[] { "FD2", "FD3", "FD1" }, units.Select(u => u.First.Title.Substring(0, 3)).ToArray());
            Assert.Equal(6, section.Total);
            Assert.Equal("1 HOUR · ×2", TallyText.RightText(units[0].First, En));          // each row keeps its own value
            Assert.Equal("1 HOUR · ×2", TallyText.RightText(units[2].First, En));
        }

        [Fact]
        public void ASharedCellValue_IsNeverAFoldMessage()
        {
            var a = new ItemRow { Kind = "row", Title = "Room A", Count = 1 }; a.Cells.Add(("Area", "12 m2"));
            var b = new ItemRow { Kind = "row", Title = "Room B", Count = 1 }; b.Cells.Add(("Area", "12 m2"));
            Assert.Equal("", ItemsBody.FoldMessage(a, En));
            var units = ItemsBody.Units(new ItemSection { Rows = { a, b } }, En);
            Assert.Equal(2, units.Count);
        }

        [Fact]
        public void NotDoneRows_StillFold_AndEveryRowIsInExactlyOneUnit()
        {
            var section = new ItemSection();
            for (var i = 0; i < 5; i++)
            {
                var r = new ItemRow { Kind = "row", Title = "Level " + i };
                r.Cells.Add(("state", "Already in model"));
                section.Rows.Add(r);
            }
            var other = new ItemRow { Kind = "row", Title = "Level 9", Count = 1 }; other.Cells.Add(("Fire Rating", "Already in model"));
            section.Rows.Add(other);
            var units = ItemsBody.Units(section, En);
            Assert.Equal(2, units.Count);
            Assert.True(units[0].IsFolded);
            Assert.Equal(5, units[0].Count);
            Assert.Same(other, units[1].First);
            Assert.Equal(section.Rows.Count, units.Sum(u => u.Members.Count));
        }
    }
}
