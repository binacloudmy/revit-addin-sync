#nullable enable
// The sheet-outline preview row (boards SheetGen / SheetsExcel): which item gets one, the label inside the outline, the text
// beside it, and the numbers the view draws it with. The percentage is only shown when the item carries one.
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SheetPreviewTests
    {
        private const ReceiptLanguage En = ReceiptLanguage.En;
        private const ReceiptLanguage Ms = ReceiptLanguage.Ms;

        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        private static ItemRow Item(string kind, string title, params (string Key, string Value)[] cells)
        {
            var r = new ItemRow { Kind = kind, Title = title };
            r.Cells.AddRange(cells);
            return r;
        }

        [Fact]
        public void ASheetWithATitleAndAScale_GetsAPreview_WithNumberScaleAndFill()
        {
            var p = SheetPreview.From(Item("sheet", " Level 1 Lighting Plan ", ("number", "EL-101"), ("scale", "1:100"), ("fill", "14%")));
            Assert.NotNull(p);
            Assert.Equal("Level 1 Lighting Plan", p!.Label);
            Assert.Equal("EL-101", p.Number);
            Assert.Equal("1:100", p.Scale);
            Assert.Equal("14%", p.Fill);
            Assert.Equal(new[] { "EL-101", "1:100 · fills 14% of the drawing area" }, p.Lines(En));
            Assert.Equal(new[] { "EL-101", "1:100 · memenuhi 14% kawasan lukisan" }, p.Lines(Ms));
        }

        [Fact]
        public void WithoutAPercentage_TheFillTextIsNotInvented()
        {
            var p = SheetPreview.From(Item("sheet", "Plan", ("number", "EL-1"), ("scale", "1:50")))!;
            Assert.Equal("", p.Fill);
            Assert.Equal("1:50", p.DetailLine(En));
            Assert.DoesNotContain("fills", string.Join(" ", p.Lines(En)));
        }

        [Fact]
        public void WithoutANumber_OnlyTheDetailLineShows()
        {
            var p = SheetPreview.From(Item("sheet", "Plan", ("scale", "1:20")))!;
            Assert.Equal(new[] { "1:20" }, p.Lines(En));
        }

        [Theory]
        [InlineData("view", "Plan", "1:100")]      // only sheets
        [InlineData("tag", "Plan", "1:100")]
        [InlineData("row", "Plan", "1:100")]
        [InlineData("sheet", "Plan", "")]          // no scale: nothing to show, no placeholder
        [InlineData("sheet", "   ", "1:100")]      // no name for the inside of the outline
        public void AnythingElse_GetsNoPreview(string kind, string title, string scale)
        {
            var row = Item(kind, title);
            if (scale.Length > 0) row.Cells.Add(("scale", scale));
            Assert.Null(SheetPreview.From(row));
        }

        [Fact]
        public void NoItem_GetsNoPreview() => Assert.Null(SheetPreview.From(null));

        [Theory]
        [InlineData("14%", "14%")]
        [InlineData(" 72 % ", "72%")]
        [InlineData("14,5%", "14,5%")]
        [InlineData("100%", "100%")]
        [InlineData("140%", "")]
        [InlineData("14", "")]
        [InlineData("about 14%", "")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void OnlyARealPercentage_IsAFill(string? raw, string expected) =>
            Assert.Equal(expected, SheetPreview.FillPercent(raw));

        [Fact]
        public void ThePreviewIsOfTheFirstSheetThatHasWhatItNeeds()
        {
            var items = new ResultItems();
            var s = new ItemSection { Name = "Plans" };
            s.Rows.Add(Item("sheet", "No scale yet", ("number", "EL-0")));
            s.Rows.Add(Item("sheet", "Second", ("number", "EL-2"), ("scale", "1:100")));
            s.Rows.Add(Item("sheet", "Third", ("number", "EL-3"), ("scale", "1:50")));
            items.Sections.Add(s);
            Assert.Equal("EL-2", SheetPreview.First(items)!.Number);
            Assert.Null(SheetPreview.First(new ResultItems()));
            Assert.Null(SheetPreview.First(null));
        }

        [Fact]
        public void TheOutline_IsAboutOnePointFourOneToOne_At64Wide()
        {
            Assert.Equal(64, SheetPreview.OutlineWidth);
            Assert.InRange(SheetPreview.Aspect, 1.41, 1.42);
            Assert.Equal(45.2, SheetPreview.OutlineHeight, 1);
        }

        [Fact]
        public void TheView_DrawsTheOutlineWithTheBoardsTokens_AndTheCardCallsIt()
        {
            var v = Src("UI/Copilot/Controls/SheetPreviewRow.cs");
            Assert.Contains("SheetPreview.OutlineWidth", v);
            Assert.Contains("SheetPreview.OutlineHeight", v);
            Assert.Contains("\"Cp.Sunken\"", v);
            Assert.Contains("\"Cp.Hair2\"", v);
            Assert.Contains("\"Cp.Muted\"", v);
            Assert.Contains("9.5", v);
            Assert.Contains("mono: true", v);
            Assert.Contains("TextTrimming.CharacterEllipsis", v);
            Assert.Contains("new CornerRadius(4)", v);

            var card = Src("UI/Copilot/Controls/ItemsCard.cs");
            Assert.Contains("SheetPreview.First(items)", card);
            Assert.Contains("SheetPreviewRow.Build(preview, lang, Top())", card);
        }
    }
}
