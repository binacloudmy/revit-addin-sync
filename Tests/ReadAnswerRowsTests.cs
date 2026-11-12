#nullable enable
// A grouped read answer ("count all walls grouped by type") is a card of tally rows under the server's headline, not markdown bullets.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ReadAnswerRowsTests
    {
        private const string Long = "jkrAR_wll_DFd_ce01_20_UMM_in_(DFd311a)-3 @ce Cat Emulsi dan 20mm Lepaan Simen";

        private static ItemRow Row(string title, int? count, string kind = "element", params long[] ids) =>
            new ItemRow { Kind = kind, Title = title, Count = count, Ids = ids.ToList() };

        private static ItemSection Section(params ItemRow[] rows) => new ItemSection { Rows = rows.ToList() };

        // ---- which sections are a tally ----

        [Fact]
        public void NamesAndCounts_AreATally()
        {
            Assert.True(TallyText.IsTally(Section(Row("A", 5), Row("B", 3))));
            Assert.True(TallyText.IsTally(Section(Row("A", 5, ""), Row("B", 3, ""))));       // kind absent
            Assert.True(TallyText.IsTally(Section(Row("A", 5, "row"), Row("B", 3, "row"))));
        }

        [Fact]
        public void ASheetListAnExcelListAndNotDoneRows_AreNot()
        {
            Assert.False(TallyText.IsTally(Section(Row("S1", 1, "sheet"), Row("S2", 1, "sheet"))));
            Assert.False(TallyText.IsTally(Section(Row("V1", 4, "view"), Row("V2", 4, "view"))));
            var withScale = Row("A", 5); withScale.Cells.Add(("scale", "1:100"));
            Assert.False(TallyText.IsTally(Section(withScale, Row("B", 3))));
            var excel = Row("A", 5, "row"); excel.Cells.Add(("row", "3"));
            Assert.False(TallyText.IsTally(Section(excel, Row("B", 3, "row"))));
            var notDone = Row("A", 5, "row"); notDone.Cells.Add(("state", "not done"));
            Assert.False(TallyText.IsTally(Section(notDone, Row("B", 3, "row"))));
        }

        [Fact]
        public void OneRowOrAMissingCount_IsNotATally()
        {
            Assert.False(TallyText.IsTally(Section(Row("A", 5))));
            Assert.False(TallyText.IsTally(Section(Row("A", 5), Row("B", null))));
            Assert.False(TallyText.IsTally(new ItemSection()));
        }

        // ---- the type name ----

        [Theory]
        [InlineData(Long, "jkrAR_wll_DFd_ce01_20_UMM_in_(DFd311a)-3", "@ce Cat Emulsi dan 20mm Lepaan Simen")]
        [InlineData("PTh300a 750 x 2100", "PTh300a", "750 x 2100")]
        [InlineData("Wall_Ext_200", "Wall_Ext_200", "")]
        [InlineData("Generic - 200mm", "", "Generic - 200mm")]
        [InlineData("Basic Wall : Generic", "", "Basic Wall : Generic")]
        [InlineData("Doors", "", "Doors")]
        [InlineData("  ", "", "")]
        [InlineData(null, "", "")]
        public void SplitTitle_CodeInMono_WordsMuted(string? title, string code, string description) =>
            Assert.Equal((code, description), TallyText.SplitTitle(title));

        [Fact]
        public void Share_IsTheRowsPartOfTheSection()
        {
            Assert.Equal(0.5, TallyText.Share(5, 10));
            Assert.Equal(1.0, TallyText.Share(12, 10));      // never past the whole
            Assert.Equal(0, TallyText.Share(5, 0));
            Assert.Equal(0, TallyText.Share(0, 10));
            Assert.Equal("50%", TallyText.SharePercent(5, 10));
            Assert.Equal("<1%", TallyText.SharePercent(1, 1000));
            Assert.Equal("0%", TallyText.SharePercent(0, 10));
            Assert.Equal("Select", TallyText.SelectLabel(ReceiptLanguage.En));
            Assert.Equal("Pilih", TallyText.SelectLabel(ReceiptLanguage.Ms));
        }

        // ---- the server's result becomes the card ----

        private static TurnResultDto Server(params ResultItemDto[] items) => new TurnResultDto
        {
            Reply = "42 walls in 3 types.", Kind = "counts", Items = items.ToList(),
        };

        private static ResultItemDto Item(string title, int count, params long[] ids) => new ResultItemDto
        {
            Kind = "element", Title = title, Count = count, Ids = ids.Select(i => JsonDocument.Parse(i.ToString()).RootElement.Clone()).ToList(),
        };

        [Fact]
        public void AGroupedReadResult_IsACardWithTheServersReplyAsItsHeadline_AndProseHidden()
        {
            var card = new ResultCardModel().WithServerResult(Server(Item(Long, 574, 1, 2), Item("Generic - 200mm", 30), Item("PTh300a", 12)));
            Assert.Equal(ResultKind.Created, card.Kind);
            Assert.Equal("42 walls in 3 types.", card.Headline);
            Assert.True(card.ReplacesProse);
            var section = Assert.Single(card.Items!.Sections);
            Assert.True(TallyText.IsTally(section));
            Assert.Equal(616, section.Total);
        }

        // ---- the views ----

        private static string Src(string rel)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, rel));
        }

        [Fact]
        public void TheItemsCard_DrawsATallySection_WithTheTallyRow_AndKeepsTheFold()
        {
            var s = Src("UI/Copilot/Controls/ItemsCard.cs");
            Assert.Contains("TallyText.IsTally(section)", s);
            Assert.Contains("TallyRows.Row(unit.First, sectionTotal, lang, actions, buttons, Top())", s);
            Assert.Contains("FoldToggle(section, units.Count, behindFold, lang)", s);   // 8 rows, then "Show all N"
        }

        [Fact]
        public void TheTallyRow_HasMonoCode_MutedDescription_ShareBar_AndAGhostSelectOnlyWithIds()
        {
            var s = Src("UI/Copilot/Controls/TallyRows.cs");
            Assert.Contains("Cp.FontMono", s);
            Assert.Contains("Cp.Muted", s);
            Assert.Contains("TextWrapping = TextWrapping.Wrap", s);
            Assert.Contains("Height = 3", s);                         // the share bar
            Assert.Contains("if (row.Ids.Count > 0)", s);              // Select only with ids
            Assert.Contains("CardKit.Ghost(TallyText.SelectLabel(lang)", s);
            Assert.Contains("select.Visibility = Visibility.Collapsed;", s);   // until Revit vouches for the ids
            Assert.DoesNotContain("\"#", s);
            Assert.DoesNotContain("CardKit.Chip(", s);
        }

        [Fact]
        public void MarkdownBullets_HaveAHangingIndent_SoAWrappedLineDoesNotGlueToTheLeftEdge()
        {
            var s = Src("Helpers/MarkdownRenderer.cs");
            Assert.Contains("TextIndent = -markerWidth", s);
            Assert.Contains("new InlineUIContainer(cell)", s);
            Assert.Contains("Margin = new Thickness(8 + level * 14 + markerWidth, 2, 0, 2)", s);
        }
    }
}
