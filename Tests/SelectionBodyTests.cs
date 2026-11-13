#nullable enable
// The selection / isolate card body (Selection board): the bar's segments, the legend and header words in both
// languages, the footnote, and the numbers and tokens of the drawing in SelectionBody.cs.
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class SelectionBodyTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        // ---- the bar ----

        [Fact]
        public void TheBar_HasAnInViewAndANotVisibleSegment_WithTheirCounts()
        {
            var s = SelectionBarText.Segments(52, 50, isolated: true, ReceiptLanguage.En);

            Assert.Equal(2, s.Count);
            Assert.Equal(SelectionSegmentKind.InView, s[0].Kind);
            Assert.Equal(50, s[0].Count);
            Assert.Equal("Isolated and selected", s[0].Label);
            Assert.Equal(SelectionSegmentKind.NotVisible, s[1].Kind);
            Assert.Equal(2, s[1].Count);
            Assert.Equal("Not visible in this view", s[1].Label);
        }

        [Fact]
        public void AllVisible_IsOneSegment()
        {
            var s = SelectionBarText.Segments(52, 52, isolated: false, ReceiptLanguage.En);

            Assert.Single(s);
            Assert.Equal(52, s[0].Count);
            Assert.Equal("Selected in view", s[0].Label);
        }

        [Fact]
        public void VisibleInViewZero_MeansNotKnown_SoEverySelectedElementCountsAsInView()
        {
            var s = SelectionBarText.Segments(9, 0, isolated: false, ReceiptLanguage.En);

            Assert.Single(s);
            Assert.Equal(9, s[0].Count);
        }

        [Fact]
        public void VisibleMoreThanSelected_IsClamped()
        {
            var s = SelectionBarText.Segments(5, 8, isolated: false, ReceiptLanguage.En);

            Assert.Single(s);
            Assert.Equal(5, s[0].Count);
        }

        [Fact]
        public void TheRestSegment_ComesLast_AndOnlyWithACount()
        {
            Assert.DoesNotContain(SelectionBarText.Segments(52, 52, true, ReceiptLanguage.En), x => x.Kind == SelectionSegmentKind.Rest);

            var s = SelectionBarText.Segments(52, 50, true, ReceiptLanguage.En, rest: 181);

            Assert.Equal(new[] { 50, 2, 181 }, s.Select(x => x.Count).ToArray());
            Assert.Equal(SelectionSegmentKind.Rest, s.Last().Kind);
            Assert.Equal("Hidden by the isolate", s.Last().Label);
        }

        [Fact]
        public void NothingSelected_NoSelectionSegments_AndNothingAtAllWithoutARest()
        {
            Assert.Empty(SelectionBarText.Segments(0, 0, true, ReceiptLanguage.En));
            Assert.Empty(SelectionBarText.Segments(-3, 2, false, ReceiptLanguage.En));
        }

        [Fact]
        public void TheLegend_ReadsInMalay()
        {
            var s = SelectionBarText.Segments(10, 7, true, ReceiptLanguage.Ms, rest: 4);

            Assert.Equal("Diasingkan dan dipilih", s[0].Label);
            Assert.Equal("Tidak kelihatan dalam view ini", s[1].Label);
            Assert.Equal("Disembunyikan oleh isolate", s[2].Label);
            Assert.Equal("Dipilih dalam view", SelectionBarText.InViewLabel(false, ReceiptLanguage.Ms));
        }

        // ---- the header, the count, the footnote ----

        [Fact]
        public void TheHeader_SaysHowManyTypes_WithAMiddleDot()
        {
            Assert.Equal("Selected · 2 types", SelectionBarText.TypesHeader(2, ReceiptLanguage.En));
            Assert.Equal("Selected · 1 type", SelectionBarText.TypesHeader(1, ReceiptLanguage.En));
            Assert.Equal("Dipilih · 3 jenis", SelectionBarText.TypesHeader(3, ReceiptLanguage.Ms));
            Assert.Equal("52 elements", SelectionBarText.TypesTotal(52, ReceiptLanguage.En));
            Assert.Equal("1 element", SelectionBarText.TypesTotal(1, ReceiptLanguage.En));
            Assert.Equal("52 elemen", SelectionBarText.TypesTotal(52, ReceiptLanguage.Ms));
        }

        [Fact]
        public void TheNotVisibleLine_KeepsTheOldWords_AndIsEmptyWhenAllAreVisibleOrUnknown()
        {
            var card = new ResultCardModel { Kind = ResultKind.Selection, Selected = 52, VisibleInView = 50 };

            Assert.Equal("2 of the selected elements are not visible in this view.", SelectionBarText.NotVisibleLine(card, ReceiptLanguage.En));
            Assert.Equal("2 daripada yang dipilih tidak kelihatan dalam view ini.", SelectionBarText.NotVisibleLine(card, ReceiptLanguage.Ms));
            Assert.Equal(SelectionLines.For(card, ReceiptLanguage.En)[1], SelectionBarText.NotVisibleLine(card, ReceiptLanguage.En));
            card.VisibleInView = 52;
            Assert.Equal("", SelectionBarText.NotVisibleLine(card, ReceiptLanguage.En));
            card.VisibleInView = 0;
            Assert.Equal("", SelectionBarText.NotVisibleLine(card, ReceiptLanguage.En));
        }

        [Fact]
        public void TheFootnote_HasTheNotVisibleLine_TheIsolatedLine_AndTheZoomHintWhenARowCanBeClicked()
        {
            var card = new ResultCardModel { Kind = ResultKind.Selection, Selected = 52, VisibleInView = 50, Isolated = true };

            var notes = SelectionBarText.Footnote(card, ReceiptLanguage.En, rowsClickable: true);

            Assert.Equal(3, notes.Count);
            Assert.StartsWith("2 of the selected", notes[0]);
            Assert.StartsWith("This view is isolated", notes[1]);
            Assert.Equal("Click a type to zoom to it.", notes[2]);
            Assert.Equal(2, SelectionBarText.Footnote(card, ReceiptLanguage.En, rowsClickable: false).Count);
            Assert.StartsWith("View ini diasingkan", SelectionBarText.Footnote(card, ReceiptLanguage.Ms, false)[1]);
        }

        [Fact]
        public void TheTypeRows_CountInstances_ElseIds_AndSkipTypesThatWerePutBack()
        {
            var a = new TypeGroup { TypeName = "Downlight 1x13W", IsType = true, Instances = 38 };
            var b = new TypeGroup { TypeName = "Downlight 1x8W", IsType = false, Ids = { 1, 2, 3 } };
            var card = new ResultCardModel { Kind = ResultKind.Selection };
            card.Groups.Add(a); card.Groups.Add(b);

            Assert.Equal(38, SelectionBarText.RowCount(a));
            Assert.Equal(3, SelectionBarText.RowCount(b));
            Assert.Equal(2, SelectionBarText.Types(card).Count);
        }

        // ---- the drawing (source text) ----

        [Fact]
        public void TheView_UsesTheBoardsNumbers()
        {
            var v = Src("UI/Copilot/Controls/SelectionBody.cs");

            Assert.Contains("BarHeight = 10", v);
            Assert.Contains("BarGap = 2", v);
            Assert.Contains("RowMinHeight = 54", v);
            Assert.Contains("MinWidth = seg.Kind == SelectionSegmentKind.NotVisible ? 5 : 0", v);   // the amber segment is never thinner than 5
            Assert.Contains("GridUnitType.Star", v);                                                 // segment widths are the counts
            Assert.Contains("CardKit.Text(seg.Label, 12, \"Cp.Run.Text2\")", v);                      // legend 12px
        }

        [Fact]
        public void TheView_UsesTokensForTheThreeSegments_AndTheRowsAndFootnote()
        {
            var v = Src("UI/Copilot/Controls/SelectionBody.cs");

            Assert.Contains("\"Cp.Run.Info\"", v);     // in view: the board's blue (Cp.Blue is lime in dark)
            Assert.Contains("\"Cp.Amber\"", v);        // not visible
            Assert.Contains("\"Cp.Run.Raised\"", v);   // the rest, grey
            Assert.Contains("\"Cp.Raised\"", v);       // the header strip
            Assert.Contains("\"Cp.Sunken\"", v);       // the footnote
            Assert.DoesNotContain("\"Cp.Blue\"", v);
            Assert.Contains("mono: true", v);
        }

        [Fact]
        public void TheView_KeepsTheButtonsAndTheResetBehaviour_AsCardKitButtons()
        {
            var v = Src("UI/Copilot/Controls/SelectionBody.cs");

            // Result marks design, rule 3: a selection draws no marks (Revit's own selection is its mark), so no marks button.
            Assert.DoesNotContain("Highlight in model", v);
            Assert.DoesNotContain("MarksButton", v);
            Assert.Contains("CardKit.Secondary(SelectionLines.ResetLabel(lang)", v);
            Assert.Contains("SelectionLines.ResetFailure(why, lang)", v);
            Assert.Contains("reset.ToolTip = full;", v);
            Assert.DoesNotContain("CardKit.Chip", v);
            Assert.DoesNotContain("FlatButton", v);
            Assert.DoesNotContain("Storyboard", v);
        }

        [Fact]
        public void TheCardView_OnlyCallsIntoTheBodyClass()
        {
            var v = Src("UI/Copilot/Controls/ResultCardView.cs");

            Assert.Contains("SelectionBody.Selection(root, card, lang, actions)", v);
            Assert.Contains("SelectionBody.IsolatedBlock(root, card, lang, actions)", v);
            Assert.DoesNotContain("private static void AddResetIsolate", v);
        }
    }
}
