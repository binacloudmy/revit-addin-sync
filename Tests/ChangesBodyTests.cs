#nullable enable
// The Changes card body of the design boards Main and Partial: the types as tiles, "Written as asked . N", the footnote,
// the ten-segment bar, the "Check N types" card. The words and counts are pure (ChangesBodyText); the view is pinned by
// source-text assertions (WPF is not loaded in the harness).
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class ChangesBodyTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        private const ReceiptLanguage En = ReceiptLanguage.En;
        private const ReceiptLanguage Ms = ReceiptLanguage.Ms;

        // ---- the bar ----

        [Theory]
        [InlineData(10, 0, 10, 0)]      // nothing put back: all green
        [InlineData(9, 1, 9, 1)]        // the Partial board: 9 written, 1 changed back
        [InlineData(5, 5, 5, 5)]
        [InlineData(1, 1, 5, 5)]
        [InlineData(0, 3, 0, 10)]       // everything put back: all amber
        [InlineData(0, 0, 0, 0)]        // nothing to count
        [InlineData(99, 1, 9, 1)]       // more than ten types: one amber segment is still drawn
        [InlineData(1, 99, 1, 9)]       // and one green segment
        [InlineData(30, 10, 7, 3)]      // 10 of 40 is 2.5 segments: rounded away from zero
        public void TheBarHasTenSegments_AndKeepsOneOfEachColourWhenBothExist(int held, int back, int green, int amber)
        {
            var (g, a) = ChangesBodyText.Segments(held, back);
            Assert.Equal((green, amber), (g, a));
            Assert.True(held + back == 0 || g + a == ChangesBodyText.BarSegments);
        }

        [Fact]
        public void TheBarShowsOnlyWhenATypeWasPutBack()
        {
            Assert.False(ChangesBodyText.ShowBar(3, 0));
            Assert.False(ChangesBodyText.ShowBar(0, 0));
            Assert.True(ChangesBodyText.ShowBar(10, 1));
            Assert.True(ChangesBodyText.ShowBar(1, 1));
        }

        [Fact]
        public void TheBarLines_SayWrittenChangedBackAndTheTotal_InBothLanguages()
        {
            Assert.Equal(("9 written", "1 changed back", "10 types"), ChangesBodyText.BarLines(9, 1, true, En));
            Assert.Equal(("9 ditulis", "1 dipulihkan balik", "10 jenis"), ChangesBodyText.BarLines(9, 1, true, Ms));
            Assert.Equal("2 groups", ChangesBodyText.BarLines(1, 1, false, En).Total);
            Assert.Equal("1 type", ChangesBodyText.BarLines(0, 1, true, En).Total);
        }

        // ---- the list, the tiles, the footnote ----

        [Fact]
        public void WrittenAsAskedAndTheTilesHeader_FollowTheBoard()
        {
            Assert.Equal("Written as asked · 4", ChangesBodyText.WrittenAsAsked(4, En));
            Assert.Equal("Ditulis seperti diminta · 4", ChangesBodyText.WrittenAsAsked(4, Ms));
            Assert.Equal("Types written", ChangesBodyText.TilesHeader(true, En));
            Assert.Equal("Elements written", ChangesBodyText.TilesHeader(false, En));
            Assert.Equal("Jenis ditulis", ChangesBodyText.TilesHeader(true, Ms));
        }

        [Fact]
        public void ATile_CountsElements_AndSaysSelectOrSelected()
        {
            Assert.Equal("38 elements", ChangesBodyText.CountLabel(38, En));
            Assert.Equal("1 element", ChangesBodyText.CountLabel(1, En));
            Assert.Equal("14 elemen", ChangesBodyText.CountLabel(14, Ms));
            Assert.Equal("Select", ChangesBodyText.SelectLabel(false, En));
            Assert.Equal("Selected", ChangesBodyText.SelectLabel(true, En));
            Assert.Equal("Pilih", ChangesBodyText.SelectLabel(false, Ms));
            Assert.Equal("38 selected in Revit.", ChangesBodyText.SelectLine(38, En));
            Assert.Equal("Click a type to show it in the model; its arrow selects it in Revit.", ChangesBodyText.SelectLine(null, En));
        }

        [Fact]
        public void ATilesName_SplitsAFamilyAndTypeName_AndKeepsAnyOtherNameWhole()
        {
            Assert.Equal(("Normal", (string?)"jkrEL_lit-fix_bc_w_alpr_1X28W : Normal"), ChangesBodyText.TileLabels("jkrEL_lit-fix_bc_w_alpr_1X28W : Normal"));
            Assert.Equal(("Downlight 1×13W", (string?)null), ChangesBodyText.TileLabels("Downlight 1×13W"));
            Assert.Equal(("", (string?)null), ChangesBodyText.TileLabels(null));
            Assert.Equal(("A :", (string?)null), ChangesBodyText.TileLabels("A :"));
        }

        // Live 13 Nov, F-t04b: "Family: Type" in a half-width tile read "LumCo_rcs- / dl_rd_LED_2X1 / 0W: Standard".
        private const string LongA = "LumCo_rcs-dl_rd_LED_2X10W: Standard";
        private const string LongB = "LumCo_rcs-dl_rd_LED_2X6W: Standard";

        [Fact]
        public void ATilesName_BreaksOnlyAtTheSeparatorsADrafterReads()
        {
            var lines = ChangesBodyText.TileLines(LongA, ChangesBodyText.HalfTileChars);
            Assert.Equal(LongA, string.Concat(ChangesBodyText.TilePieces(LongA)));           // nothing lost or added
            Assert.Equal(LongA.Replace(" ", ""), string.Concat(lines).Replace(" ", ""));
            // Every line but the last ends at a separator: never inside "2X10W".
            foreach (var l in lines.Take(lines.Count - 1)) Assert.True(l.EndsWith("_") || l.EndsWith("-") || l.EndsWith(":"), l);
            Assert.Contains(lines, l => l.Contains("2X10W"));
            Assert.Equal("Standard", lines.Last());                                            // the type's own name, whole
            Assert.All(lines, l => Assert.True(l.Length <= ChangesBodyText.HalfTileChars, l));
        }

        [Fact]
        public void ATilesTypeName_StaysWholeWhenItFitsALine()
        {
            var lines = ChangesBodyText.TileLines("Panel_Fam: Type A 600", 12);
            Assert.Equal("Type A 600", lines.Last());
            var text = ChangesBodyText.TileText("Panel_Fam: Type A 600");
            Assert.EndsWith("Type\u00A0A\u00A0600", text);                                   // no break inside the type's name
            Assert.Equal("Panel_Fam: Type A 600", text.Replace("\u200B", "").Replace('\u00A0', ' '));
            Assert.Contains("Panel_\u200BFam", text);                                        // a break WPF may take after "_"
            // A type name longer than a line still breaks, at its spaces.
            Assert.True(ChangesBodyText.TileLines("F: (DA30a) 760 x 2110 swing", 12).Count > 1);
        }

        [Fact]
        public void ATileWhoseNameNeedsMoreThanTwoLines_TakesTheWholeWidth()
        {
            Assert.True(ChangesBodyText.TileTakesFullWidth(LongA));
            Assert.False(ChangesBodyText.TileTakesFullWidth("Alpha: Standard"));
            Assert.True(ChangesBodyText.TileLines(LongA, ChangesBodyText.FullTileChars).Count <= 2);

            Assert.Equal(new[] { (0, 0, 3), (1, 0, 3) }, ChangesBodyText.TileLayout(new[] { LongA, LongB }));
            Assert.Equal(new[] { (0, 0, 1), (0, 2, 1) }, ChangesBodyText.TileLayout(new[] { "A: X", "B: Y" }));
            Assert.Equal(new[] { (0, 0, 3) }, ChangesBodyText.TileLayout(new[] { "A: X" }));                   // a lone tile
            // A short tile before a long one keeps its row; the long one gets the next row to itself; pairing goes on after.
            Assert.Equal(new[] { (0, 0, 1), (1, 0, 3), (2, 0, 1), (2, 2, 1) }, ChangesBodyText.TileLayout(new[] { "A: X", LongA, "B: Y", "C: Z" }));
        }

        [Fact]
        public void TheBeforeIsShownOnlyWhereARowReplacedAValue()
        {
            Assert.Equal("was 10 W", ChangesBodyText.Was("10 W", En));
            Assert.Equal("asalnya 10 W", ChangesBodyText.Was("10 W", Ms));
            Assert.Equal("", ChangesBodyText.Was("", En));
            Assert.Equal("", ChangesBodyText.Was(null, Ms));
            Assert.Equal("Change", ChangesBodyText.Change(En));
        }

        [Fact]
        public void TheFootnote_OnlyMentionsWhatTheCardCanDo()
        {
            var plain = ChangesBodyText.Footnote(false, false, En);
            Assert.Equal("Click a source to see where a value came from.", plain);

            var full = ChangesBodyText.Footnote(true, true, En);
            Assert.Contains("Click a value, or Change, to edit it.", full);
            Assert.Contains("Changed back", full);
            // Every type's values are listed: a tile no longer "shows its values"; what a tile does is said once, under the tiles.
            Assert.DoesNotContain("Click a type", full);
            Assert.Contains("Klik nilai, atau Ubah", ChangesBodyText.Footnote(true, false, Ms));
            Assert.Contains("Dipulihkan balik", ChangesBodyText.Footnote(false, true, Ms));
        }

        // Live 13 Nov, F-t04b: one type parameter written on two types of two families (the types named alike, 38 and 14
        // instances). The card showed "Written as asked · 1 / For <family A>: <type>" only; the second type's row was
        // reachable only through a tile click, which did nothing in Revit.
        [Fact]
        public void EveryWrittenType_GetsItsOwnList_OneUnderTheOther()
        {
            TypeGroup T(string family, long id, int instances) => new TypeGroup
            {
                TypeName = "Standard", Family = family, Label = family + ": Standard", IsType = true, Instances = instances, Ids = new List<long> { id },
                Rows = { new ChangeRow { Param = "Rated Current", Before = "0 A", After = "0.04 A", Ids = new List<long> { id } } },
            };
            var a = T("Alpha_dl_2X10W", 11, 38);
            var b = T("Alpha_dl_2X6W", 12, 14);
            var lists = ChangesBodyText.Lists(new[] { a, b });

            Assert.Equal(2, lists.Count);
            Assert.Same(a, lists[0].Group);
            Assert.Same(b, lists[1].Group);
            Assert.All(lists, l => Assert.Single(l.Written));
            Assert.All(lists, l => Assert.True(l.NamesType));
            Assert.Equal("For Alpha_dl_2X6W: Standard", ChangesBodyText.WrittenFor(lists[1].Group.Title, En));

            // One type: one list, and it needs no "For ...".
            var one = Assert.Single(ChangesBodyText.Lists(new[] { a }));
            Assert.False(one.NamesType);
        }

        [Fact]
        public void APutBackType_KeepsItsCheckCard_AndOnlyItsHeldRowsAreListed()
        {
            var some = Group((false, null), (true, "2"));
            var part = Assert.Single(ChangesBodyText.Lists(new[] { some }));
            Assert.True(part.CheckCard);
            Assert.Single(part.Written);
            Assert.False(part.Written[0].ChangedBack);
        }

        [Fact]
        public void TheView_DrawsEveryTypesList_NotOnlyTheClickedOne()
        {
            var s = Src("UI/Copilot/Controls/ChangesBody.cs");
            Assert.Contains("foreach (var part in ChangesBodyText.Lists(card.Groups))", s);
            Assert.Contains("WrittenList(part.Group, part.Written, part.NamesType)", s);
            Assert.DoesNotContain("var g = Current;", s);
        }

        // ---- the check card ----

        private static TypeGroup Group(params (bool Back, string? Intended)[] rows)
        {
            var g = new TypeGroup { TypeName = "T" };
            foreach (var (back, intended) in rows)
                g.Rows.Add(new ChangeRow { Param = "P", After = "1", ChangedBack = back, Intended = intended, Ids = new List<long> { 1 } });
            return g;
        }

        [Fact]
        public void TheTypesThatNeedALook_AreThoseWithAPutBackValueNotLeftAsIs()
        {
            var held = Group((false, null));
            var back = Group((true, "2"));
            var some = Group((false, null), (true, "2"));
            var left = Group((true, "2")); left.LeftAsIs = true;
            Assert.Equal(2, ChangesBodyText.NeedAttention(new[] { held, back, some, left }));
            Assert.Equal(0, ChangesBodyText.NeedAttention(new[] { held, left }));
        }

        [Fact]
        public void TheCheckCard_Words()
        {
            Assert.Equal("Check 1 type", ChangesBodyText.CheckTitle(1, En));
            Assert.Equal("Check 3 types", ChangesBodyText.CheckTitle(3, En));
            Assert.Equal("Semak 2 jenis", ChangesBodyText.CheckTitle(2, Ms));
            Assert.Equal("Values were written, then put back to what they were. This type holds its old values now.", ChangesBodyText.BackNote(true, En));
            Assert.DoesNotContain("struck through", ChangesBodyText.BackNote(false, En));
            Assert.Equal("Changed back", ChangesBodyText.ChipText(true, false, En));
            Assert.Equal("Some changed back", ChangesBodyText.ChipText(false, false, En));
            Assert.Equal("Left as is", ChangesBodyText.ChipText(true, true, En));
            Assert.Equal("Dipulihkan balik", ChangesBodyText.ChipText(true, false, Ms));
        }

        [Fact]
        public void TheCheckTable_HasThreeColumns_AndSaysBrochureOnlyForDocumentSources()
        {
            var plain = new[] { new ChangeRow { Param = "P" } };
            Assert.Equal(new[] { "Parameter", "Holds now", "Written" }, ChangesBodyText.CheckHeaders(plain, En));
            Assert.Equal(new[] { "Parameter", "Kini", "Ditulis" }, ChangesBodyText.CheckHeaders(plain, Ms));

            var doc = new[] { new ChangeRow { Param = "P", SourceInfo = new ResultSourceDto { Kind = ResultSourceKinds.Document } } };
            Assert.Equal("Brochure", ChangesBodyText.CheckHeaders(doc, En)[2]);
            Assert.Equal("Brosur", ChangesBodyText.CheckHeaders(doc, Ms)[2]);

            var mixed = doc.Concat(plain);
            Assert.Equal("Written", ChangesBodyText.CheckHeaders(mixed, En)[2]);
            Assert.Equal("Written", ChangesBodyText.CheckHeaders(new List<ChangeRow>(), En)[2]);
        }

        [Fact]
        public void TheCheckBanner_NoLongerPointsAtAnAfterColumn()
        {
            var (title, hint) = ChangesBodyText.CheckBanner(1, En);
            Assert.Equal("Check 1 value", title);
            Assert.Equal("Check 2 values", ChangesBodyText.CheckBanner(2, En).Title);
            Assert.Equal("Semak 2 nilai", ChangesBodyText.CheckBanner(2, Ms).Title);
            Assert.DoesNotContain("After", hint);
            Assert.Contains("Change", hint);
            Assert.Contains("Ubah", ChangesBodyText.CheckBanner(1, Ms).Hint);
        }

        // ---- the view, to the board's numbers ----

        [Fact]
        public void TheView_DrawsTheBoardsTilesRowsAndFootnote()
        {
            var s = Src("UI/Copilot/Controls/ChangesBody.cs");
            Assert.Contains("public static class ChangesBody", s);
            Assert.Contains("CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 11)", s);   // the tile
            Assert.Contains("new ColumnDefinition { Width = new GridLength(8) }", s);                       // 8px between the two columns
            Assert.Contains("Width = 12, Height = 12, StrokeThickness = 2", s);                              // the ring
            Assert.Contains("CardKit.Text(ChangesBodyText.TileText(title), 13, \"Cp.Ink\", FontWeights.SemiBold)", s);   // the tile's name
            Assert.Contains("CardKit.Text(mono, 10.5, \"Cp.Faint\", mono: true", s);                          // its mono line
            Assert.Contains("new GridLength(104)", s);                                                       // the parameter column
            Assert.Contains("CardKit.Text(\"\", 11.5, \"Cp.Muted\", mono: true", s);                           // the parameter, mono
            Assert.Contains("CardKit.Text(row.After!, 13, \"Cp.Ink\", FontWeights.Medium)", s);               // the value, 13/500
            Assert.Contains("Short(row.Source, 40), 11.5, \"Cp.Faint\"", s);                                  // the source line, 11.5
            Assert.Contains("TextDecorations = TextDecorations.Underline", s);                               // the underlined Change
            Assert.Contains("CardKit.Ghost(ChangesBodyText.Change(lang)", s);
            Assert.Contains("SetResourceReference(Border.BackgroundProperty, \"Cp.Sunken\")", s);              // the footnote strip
            Assert.Contains("12, \"Cp.Muted\", margin: new Thickness(8, 0, 0, 0)", s);                         // 12px muted footnote text
            Assert.Contains("ChangesBodyText.WrittenAsAsked(rows.Count, lang)", s);
        }

        [Fact]
        public void TheView_HasNoBeforeColumn_AndKeepsEveryAction()
        {
            var s = Src("UI/Copilot/Controls/ChangesBody.cs");
            Assert.DoesNotContain("\"Before\"", s);
            Assert.DoesNotContain("\"Sebelum\"", s);
            Assert.Contains("ChangesBodyText.Was(row.Before, lang)", s);                // a replaced value stays visible, as a "was ..." line
            Assert.DoesNotContain("MarksButton.Build", s);                              // step 2: Hide marks / Show in model is the card's section header
            foreach (var label in new[] { "\"Open as grid\"", "\"Copy table\"", "\"Write these values\"", "\"Leave as is\"", "\"Show in Revit\"", "\"Open Type Properties\"", "\"Keep all as written\"" })
                Assert.Contains(label, s);
            Assert.DoesNotContain("\"Zoom\"", s);                                       // step 2: a tile click zooms to its type
            Assert.Contains("actions.ShowGroup(Highlights.MarkFocus.ForTypeGroup(card.Groups[i]), select)", s);   // the tile shows, its arrow selects
            Assert.Contains("CardRows.Arrow(() => Pick(idx, select: true))", s);
            Assert.Contains("ValueInput.Normalize(row.After, box.Text, out var e)", s); // click-to-edit
            Assert.Contains("await actions.EditValue!(g, row, norm)", s);
            Assert.Contains("ValueInput.Normalize(row.After, row.Intended!, out var e)", s);   // Write these values
            Assert.Contains("g.LeftAsIs = true;", s);
            Assert.Contains("card.RefreshHeadline(lang);", s);
            Assert.Contains("g.CanWriteAgain", s);
            Assert.Contains("SourcePanel.Build(row, lang, actions", s);                 // the Source panel
        }

        [Fact]
        public void TheView_DrawsTheTenSegmentBar_AndTheCheckCard_ToThePartialBoard()
        {
            var s = Src("UI/Copilot/Controls/ChangesBody.cs");
            Assert.Contains("ChangesBodyText.BarSegments", s);
            Assert.Contains("Height = 8, CornerRadius = new CornerRadius(2)", s);       // a segment
            Assert.Contains("\"Cp.Green\"", s);
            Assert.Contains("\"Cp.AmberBg\"", s);                                        // the hatch's ground
            Assert.Contains("\"Cp.Amber\"", s);                                          // and its stripes
            Assert.Contains("CardKit.Card(attention ? \"Cp.AmberLn\" : \"Cp.Line\", \"Cp.Bg\"", s);   // the Check card
            Assert.Contains("new GridLength(1.3, GridUnitType.Star)", s);                // Parameter | Holds now | Written: 1.3 / 1 / 1
            Assert.Contains("ChangesBodyText.CheckHeaders(rows, lang)", s);
            Assert.Contains("ChangesBodyText.ChipText(g.ChangedBack, false, lang)", s); // the "Changed back" chip
            Assert.Contains("CardKit.Icons.Eye", s);
        }

        [Fact]
        public void TheView_UsesOnlyTokens_AndTheKitsButtons_AndNoStoryboard()
        {
            var s = Src("UI/Copilot/Controls/ChangesBody.cs");
            Assert.DoesNotMatch(new Regex("\"#[0-9a-fA-F]{3,8}\""), s);
            Assert.DoesNotContain("Color.FromRgb", s);
            Assert.DoesNotContain("Color.FromArgb", s);
            Assert.DoesNotContain("CardKit.Chip(", s);
            Assert.DoesNotContain("FlatButton", s);
            Assert.DoesNotContain("Storyboard", s);
            Assert.DoesNotContain("new Button", s);
            Assert.Contains("\"Cp.Run.Info\"", s);                                       // the selected tile's blue
            Assert.Contains("\"Cp.Hair2\"", s);
        }

        [Fact]
        public void TheResultCardView_OnlyCallsIntoTheBody_AndLostTheHelpersOnlyTheTabsUsed()
        {
            var v = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.Contains("ChangesBody.Build(root, card, lang, actions);", v);
            foreach (var gone in new[] { "private static FrameworkElement Table(", "private static FrameworkElement AfterCell(", "private static async void WriteAgain(",
                                         "private static void Paint(", "private static TypeGroup Current(", "internal static string TableText(", "private static void Header(" })
                Assert.DoesNotContain(gone, v);
            Assert.Contains("private static void Cell(", v);        // Created and Findings still use these two
            Assert.Contains("private static string Short(", v);
        }
    }
}
