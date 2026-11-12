#nullable enable
// The rows of the Created card, the Findings card and the items list (boards SheetGen, SheetsExcel, SmartTags):
// which part of a row goes in which column, the group header's name and count, the "Show all N" fold, the "+ N more" line,
// the Excel table's column titles, in English and Malay; and source-text checks that the views draw the boards' numbers.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ItemsBodyTests
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

        private static ItemRow Sheet(string title, params (string Key, string Value)[] cells)
        {
            var r = new ItemRow { Kind = "sheet", Title = title, Ids = new List<long> { 1 } };
            r.Cells.AddRange(cells);
            return r;
        }

        private static ItemSection Section(string name, params ItemRow[] rows) => new ItemSection { Name = name, Rows = rows.ToList() };

        // ---- which part of a row goes where ----

        [Fact]
        public void ASheetRow_HasItsNumberAsLead_ItsScaleAsTrail_AndTheRestUnderTheName()
        {
            var row = Sheet("Level 1 Lighting Plan", ("number", "EL-101"), ("scale", "1:100"), ("fill", "72%"));

            var p = ItemsBody.Parts(row, En);
            Assert.Equal("EL-101", p.Lead);
            Assert.Equal("Level 1 Lighting Plan", p.Title);
            Assert.Equal("1:100", p.Trail);
            Assert.Equal("fills 72%", p.Detail);
            Assert.Equal("memenuhi 72%", ItemsBody.Parts(row, Ms).Detail);
        }

        [Fact]
        public void AnExcelRow_HasItsRowNumberAsTrail_AndANotDoneRowKeepsItsState()
        {
            var done = Sheet("Cover Sheet", ("number", "EL-000"), ("row", "2"));
            var notDone = new ItemRow { Kind = "row", Title = "Level 1 Lighting Layout" };
            notDone.Cells.AddRange(new[] { ("number", "EL-005"), ("row", "7"), ("state", "Already in model") });

            Assert.Equal(("EL-000", "row 2", ""), (ItemsBody.Parts(done, En).Lead, ItemsBody.Parts(done, En).Trail, ItemsBody.Parts(done, En).Detail));
            var p = ItemsBody.Parts(notDone, En);
            Assert.Equal(("EL-005", "row 7", "Already in model"), (p.Lead, p.Trail, p.Detail));
            Assert.Equal("baris 7", ItemsBody.Parts(notDone, Ms).Trail);
        }

        [Fact]
        public void AScaleWins_OverTheExcelRow_ForTheTrail_AndTheRowMovesUnderTheName()
        {
            var row = Sheet("X", ("number", "EL-1"), ("scale", "1:50"), ("row", "4"));

            var p = ItemsBody.Parts(row, En);
            Assert.Equal("1:50", p.Trail);
            Assert.Equal("row 4", p.Detail);
        }

        [Fact]
        public void ARowWithNoCells_HasOnlyATitle()
        {
            var p = ItemsBody.Parts(new ItemRow { Kind = "view", Title = "Level 1 - Lighting" }, En);
            Assert.Equal(("", "Level 1 - Lighting", "", ""), (p.Lead, p.Title, p.Trail, p.Detail));
        }

        [Fact]
        public void TheExcelTable_IsDrawnWhenAnyRowNamesItsExcelRow()
        {
            Assert.True(ItemsBody.IsExcelTable(Section("", Sheet("a", ("row", "2")), Sheet("b"))));
            Assert.False(ItemsBody.IsExcelTable(Section("", Sheet("a", ("number", "EL-1")))));
            Assert.Equal(new[] { "Sheet", "Name", "Excel" }, ItemsBody.TableHeaders(En));
            Assert.Equal(new[] { "Sheet", "Nama", "Excel" }, ItemsBody.TableHeaders(Ms));
        }

        // ---- group header: name left, count right ----

        [Fact]
        public void ASectionsCount_NamesItsUnit_WhenItsRowsAreOfOneKind()
        {
            var two = Section("Lighting plans", Sheet("a"), Sheet("b"));
            Assert.Equal("Lighting plans", ItemsBody.SectionName(two, En));
            Assert.Equal("2 sheets", ItemsBody.SectionCount(two, En));
            Assert.Equal("1 sheet", ItemsBody.SectionCount(Section("", Sheet("a")), En));
            Assert.Equal("2 sheet", ItemsBody.SectionCount(two, Ms));             // Malay has no plural
            Assert.Equal("Sheets", ItemsBody.SectionName(Section("", Sheet("a")), En));   // no group name: what the rows are
            Assert.Equal("Sheet", ItemsBody.SectionName(Section("", Sheet("a")), Ms));
        }

        [Fact]
        public void ASectionsCount_IsAPlainNumber_WhenItsRowsAreMixed()
        {
            var view = new ItemRow { Kind = "view", Title = "v" };
            Assert.Equal("2", ItemsBody.SectionCount(Section("Lighting plans", Sheet("a"), view), En));
        }

        [Fact]
        public void ACountedSection_SaysTagsOrElements()
        {
            var view = new ItemRow { Kind = "view", Title = "Level 1 - Lighting", Count = 82 };
            var tag = new ItemRow { Kind = "tag", Title = "Downlight", Count = 38 };
            var element = new ItemRow { Kind = "element", Title = "No Type Mark", Count = 11 };
            Assert.Equal("82 tags", ItemsBody.SectionCount(Section("", view), En));
            Assert.Equal("38 tags", ItemsBody.SectionCount(Section("L1", tag), En));
            Assert.Equal("11 elements", ItemsBody.SectionCount(Section("L2", element), En));
            Assert.Equal("11 elemen", ItemsBody.SectionCount(Section("L2", element), Ms));
            Assert.Equal("1 tag", ItemsBody.SectionCount(Section("L1", new ItemRow { Kind = "tag", Title = "t", Count = 1 }), En));
        }

        [Fact]
        public void TheUnits_AreRightForEveryKind()
        {
            Assert.Equal("rows", ItemsBody.Unit("row", 3, En));
            Assert.Equal("baris", ItemsBody.Unit("row", 3, Ms));
            Assert.Equal("items", ItemsBody.Unit(null, 2, En));
            Assert.Equal("item", ItemsBody.Unit("something", 2, Ms));
            Assert.Equal("view", ItemsBody.Unit("view", 1, En));
        }

        // ---- the fold ----

        [Theory]
        [InlineData(3, 3, 3, false)]
        [InlineData(8, 8, 8, false)]
        [InlineData(9, 9, 8, true)]
        [InlineData(24, 24, 8, true)]
        [InlineData(45, 30, 8, true)]
        public void RowsFoldAfterEight_AndNeverDrawPastThirty(int total, int drawn, int folded, bool fold)
        {
            Assert.Equal(drawn, ItemsBody.DrawnRows(total));
            Assert.Equal(folded, ItemsBody.FoldedRows(total));
            Assert.Equal(fold, ItemsBody.HasFold(total));
        }

        [Fact]
        public void TheFoldLines_AreWordedInBothLanguages()
        {
            var rows = Enumerable.Range(0, 24).Select(i => Sheet("s" + i)).ToArray();
            var s = Section("", rows);
            Assert.Equal("Show all 24 sheets", ItemsBody.ShowAllLabel(s, En));
            Assert.Equal("Tunjuk semua 24 sheet", ItemsBody.ShowAllLabel(s, Ms));
            Assert.Equal("Show fewer", ItemsBody.ShowFewerLabel(En));
            Assert.Equal("Tunjuk kurang", ItemsBody.ShowFewerLabel(Ms));
            Assert.Equal("Show all 30 sheets", ItemsBody.ShowAllLabel(Section("", Enumerable.Range(0, 40).Select(i => Sheet("s" + i)).ToArray()), En));
            Assert.Equal("+ 10 more", ItemsBody.MoreLine(10, En));
            Assert.Equal("+ 10 lagi", ItemsBody.MoreLine(10, Ms));
        }

        [Fact]
        public void TheListsOfTheCreatedAndFindingsCards_AreCappedAtTwelve() =>
            Assert.Equal(12, ItemsBody.MaxListRows);

        // ---- findings rows ----

        [Fact]
        public void AFindingRow_HasARedDotWhenFixable_AnAmberOneWhenNot_AndACountOnlyWithIds()
        {
            var fix = new Finding { Title = "No circuit", Fixable = true, Ids = new List<long> { 900, 901 } };
            var look = new Finding { Title = "Outside room", Fixable = false };
            Assert.Equal("Cp.Red", ItemsBody.FindingDotToken(fix));
            Assert.Equal("Cp.Amber", ItemsBody.FindingDotToken(look));
            Assert.Equal("2", ItemsBody.FindingCount(fix));
            Assert.Equal("", ItemsBody.FindingCount(look));
        }

        // ---- the views draw the boards' numbers ----

        [Fact]
        public void TheItemsList_DrawsTheBoardsBandsRowsTableAndFold()
        {
            var s = Src("UI/Copilot/Controls/ItemsCard.cs");
            Assert.Contains("\"Cp.Raised\"", s);                                                            // group bands
            Assert.Contains("\"Cp.Line\"", s);                                                              // hairlines
            Assert.Contains("new Thickness(14, 8, 14, 8)", s);                                              // band padding 8 14
            Assert.Contains("new Thickness(14, 6, 14, 6), table ? 36 : row.Count != null ? 42 : 40", s);    // row padding and heights
            Assert.Contains("hasLead ? 72 : 0", s);                                                         // number column 62 + gap 10
            Assert.Contains("new GridLength(44)", s);                                                       // Excel column
            Assert.Contains("Height = 24, CornerRadius = new CornerRadius(6)", s);                          // mapping chips
            Assert.Contains("\"Cp.PanelBg\"", s);
            Assert.Contains("\"Lajur digunakan\"", s);
            Assert.Contains("\"Columns used\"", s);
            Assert.Contains("FoldedRows(", s);
            Assert.Contains("CardKit.Ghost(all", s);                                                        // "Show all N"
            Assert.Contains("compact: true", s);                                                            // row buttons h30
            Assert.Contains("mono: true", s);
            Assert.DoesNotContain("CardKit.Chip(", s);
        }

        [Fact]
        public void TheCreatedCard_DrawsCategoryRowsAFootnoteAndTheButtons()
        {
            var s = Src("UI/Copilot/Controls/CreatedCard.cs");
            Assert.Contains("ItemsBody.MaxListRows", s);
            Assert.Contains("new Thickness(14, 6, 14, 6), 40", s);
            Assert.Contains("\"Cp.Sunken\"", s);                                                            // the footnote row
            Assert.Contains("CardKit.Icons.Eye", s);
            Assert.Contains("CardKit.Icons.Copy", s);
            Assert.Contains("\"Tunjuk semuanya\" : \"Show them\"", s);
            Assert.Contains("\"Salin senarai\" : \"Copy list\"", s);
            Assert.Contains("\"Serlahkan dalam model\" : \"Highlight in model\"", s);
            Assert.Contains("ItemsBody.MoreLine(", s);
        }

        [Fact]
        public void TheFindingsBody_DrawsADotATitleACountAndAShowButton()
        {
            var s = Src("UI/Copilot/Controls/FindingsBody.cs");
            Assert.Contains("Width = 8, Height = 8", s);
            Assert.Contains("ItemsBody.FindingDotToken(f)", s);
            Assert.Contains("ItemsCard.Count(", s);
            Assert.Contains("\"Tunjuk\" : \"Show\"", s);
            Assert.Contains("ReceiptTemplates.HighlightLabel(lang)", s);
            Assert.Contains("actions.ShowElements(ids)", s);
            Assert.Contains("actions.Highlight(h)", s);
            Assert.Contains("compact: true", s);
            Assert.DoesNotContain("CardKit.Chip(", s);
        }

        [Fact]
        public void TheCardView_HandsItsCreatedAndFindingsBodiesToTheNewViews()
        {
            var v = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.Contains("CreatedCard.TryBuild(root, card, lang, actions)", v);
            Assert.Contains("CreatedCard.BuildElements(root, card, lang, actions)", v);
            Assert.Contains("FindingsBody.Build(root, card, lang, actions)", v);
        }

        [Fact]
        public void TheNewViews_UseNoColourLiterals()
        {
            var hex = new Regex("\"#[0-9a-fA-F]{3,8}\"");
            foreach (var f in new[] { "ItemsCard", "CreatedCard", "FindingsBody" })
                Assert.False(hex.IsMatch(Src("UI/Copilot/Controls/" + f + ".cs")), f + " has a hex colour literal");
        }
    }
}
