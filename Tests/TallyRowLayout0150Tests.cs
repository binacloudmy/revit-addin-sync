#nullable enable
// r13 (0.0.150), live c6399f4d on 0.0.149 ("Select all the fire doors on PTBR_Aras 01.", frame V9-C2-05): the door-type rows put
// "1 HOUR · BILIK SUIS 1, BILIK TCR 1 · ×2" in the never-wrapping right column and the type name wrapped one character per line.
// In the usual pane (~330-420 px) the facts now go under the name and the right column keeps the count; the name has a minimum width
// and its code breaks at "_", ")" and "-".
using System.IO;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TallyRowLayout0150Tests
    {
        private const ReceiptLanguage En = ReceiptLanguage.En;

        private static ItemRow Row(string title, int count, params (string Key, string Value)[] cells)
        {
            var row = new ItemRow { Kind = ResultItemKinds.Row, Title = title, Count = count };
            foreach (var c in cells) row.Cells.Add(c);
            return row;
        }

        private static ItemRow FireDoor() => Row("jkrAR20_dor_PTa_Aa21a2_in_(PTa2)-3 Fire door: (PTa2) 1700x 2400 @FD1", 2,
            ("Type Mark", "FD1"), ("Fire Rating", "1 HOUR"), ("Room", "BILIK SUIS 1, BILIK TCR 1"));

        [Theory]
        [InlineData(330)]
        [InlineData(380)]
        [InlineData(420)]
        [InlineData(0)]     // width not known yet: narrow
        public void TheLiveFireDoorRow_InTheUsualPane_PutsItsFactsUnderTheName(double width)
        {
            var row = FireDoor();
            Assert.True(TallyText.FactsUnder(width, row, En));
            Assert.Equal("2", TallyText.CountOnly(row));
            Assert.Equal("Type Mark FD1 · 1 HOUR · BILIK SUIS 1, BILIK TCR 1", TallyText.FactsLine(row, En));
        }

        [Fact]
        public void AWidePane_KeepsTheFactsOnTheRight()
        {
            var row = FireDoor();
            Assert.False(TallyText.FactsUnder(900, row, En));
            Assert.Equal("FD1 · 1 HOUR · BILIK SUIS 1, BILIK TCR 1 · ×2", TallyText.RightText(row, En));   // unchanged wide text
        }

        [Fact]
        public void TheNameNeverGetsLessThanItsMinimum_WhenTheFactsStayRight()
        {
            var row = FireDoor();
            for (var w = 200; w <= 1200; w += 10)
                if (!TallyText.FactsUnder(w, row, En))
                    Assert.True(w - 24 - TallyText.RightText(row, En).Length * TallyText.RightCharWidth - 12 >= TallyText.MinTitleWidth);
        }

        [Fact]
        public void ShortFacts_StayOnTheRight_InTheUsualPane()   // A10: a room's area is its value, never a bare "1"
        {
            var room = Row("DE02 Dewan Utama", 1, ("Area", "512.88 m2"));
            Assert.False(TallyText.FactsUnder(330, room, En));
            Assert.Equal("512.88 m2", TallyText.RightText(room, En));
            var doors = Row("FD2 – (PTa1) 1000 x 2400", 2, ("Fire Rating", "1 HOUR"));   // ce75668c: "1 HOUR · ×2" fits
            Assert.False(TallyText.FactsUnder(330, doors, En));
        }

        [Fact]
        public void APlainTally_HasNoFacts_AndNeverStacks()
        {
            var walls = Row("Generic - 200mm", 12);
            Assert.False(TallyText.FactsUnder(0, walls, En));
            Assert.Equal("", TallyText.FactsLine(walls, En));
            Assert.Equal("12", TallyText.CountOnly(walls));
        }

        [Fact]
        public void ARowOfOneThing_UnderTheName_HasNoBareOneOnTheRight()
        {
            var one = Row("jkrAR20_dor_PTa_Aa21a2_in_(PTa2)-3 Fire door", 1, ("Type Mark", "FD1"), ("Fire Rating", "1 HOUR"), ("Room", "BILIK SUIS 1, BILIK TCR 1"));
            Assert.True(TallyText.FactsUnder(380, one, En));
            Assert.Equal("", TallyText.CountOnly(one));
        }

        [Fact]
        public void ACode_BreaksAtItsParts_NotPerCharacter()
        {
            var b = TallyText.Breakable("jkrAR20_dor_PTa_Aa21a2_in_(PTa2)-3");
            Assert.Equal("jkrAR20_dor_PTa_Aa21a2_in_(PTa2)-3", b.Replace("​", ""));
            Assert.Equal("jkrAR20_​dor_​PTa_​Aa21a2_​in_​(PTa2)​-​3", b);
            Assert.Equal("ABC", TallyText.Breakable("ABC"));
            Assert.Equal("a_", TallyText.Breakable("a_"));    // nothing after the last character
            Assert.Equal("", TallyText.Breakable(""));
        }

        [Fact]
        public void TheTallyRow_KeepsItsClick_Bar_AndMinimumWidth_AndSwitchesOnWidthOnly()
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            var s = File.ReadAllText(Path.Combine(dir!.FullName, "UI/Copilot/Controls/TallyRows.cs"));
            Assert.Contains("MinWidth = TallyText.MinTitleWidth", s);
            Assert.Contains("new Run(TallyText.Breakable(code))", s);
            Assert.Contains("TallyText.FactsLine(row, lang)", s);
            Assert.Contains("TallyText.FactsUnder(width, row, lang)", s);
            Assert.Contains("if (e.WidthChanged) Fit(e.NewSize.Width);", s);
            Assert.Contains("ItemsCard.Count(TallyText.RightText(row, lang), 12.5)", s);
            Assert.Contains("if (row.Button == ItemButton.Select) actions.Rows?.Link(line, Highlights.MarkFocus.ForItemRow(row));", s);
        }
    }
}
