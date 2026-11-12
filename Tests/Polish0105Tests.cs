#nullable enable
// Live test of 0.0.104: toast over the card, read-back count, rule names, orphan lines, duplicate checks, headline length.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class Polish0105Tests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            return dir?.FullName ?? "/Users/ashraf/development/bina/lh/addin-jumble";
        }
        private static string Src(string rel) => File.ReadAllText(Path.Combine(Root(), rel));

        // 1 the toast
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(null, true)]
        public void TheToast_ShowsOnlyWhileThePaneIsNotOpen(bool? paneOpen, bool shown) => Assert.Equal(shown, ToastPolicy.Show(paneOpen));

        [Fact]
        public void TheOverlayWindow_AsksThePolicy_BeforeShowingTheToast()
        {
            var s = Src("UI/Copilot/Highlights/HighlightOverlayWindow.xaml.cs");
            Assert.Contains("ToastPolicy.Show(RevitWebAppSync.App.CopilotPaneOpen.Open)", s);
            Assert.DoesNotContain("Toast.Visibility = Visibility.Visible;", s);
        }

        // 2 the read-back chip counts elements
        [Fact]
        public void ReadBack_CountsElementsLikeTheHeadline()
        {
            var card = new ResultCardModel { Kind = ResultKind.Changes };
            card.Groups.Add(new TypeGroup { TypeName = "A", Instances = 3, Rows = { new ChangeRow { Param = "Comments", After = "x" }, new ChangeRow { Param = "Mark", After = "y" } } });
            Assert.Equal((3, 3), card.ReadBack());
            card.Mismatches = 1;
            Assert.Equal((2, 3), card.ReadBack());
            card.Mismatches = 9;
            Assert.Equal((0, 3), card.ReadBack());
        }

        [Fact]
        public void ReadBack_FallsBackToTheRowsWhenNoInstanceCountIsKnown()
        {
            var card = new ResultCardModel { Kind = ResultKind.Changes };
            card.Groups.Add(new TypeGroup { TypeName = "A", Instances = 0, Rows = { new ChangeRow { Param = "Comments", After = "x" } } });
            Assert.Equal((1, 1), card.ReadBack());
        }

        // 3 rule names
        [Theory]
        [InlineData("Revit Review Warnings (Document.GetWarnings, grouped by failure definition); BINA model-health practice", "Revit Review Warnings")]
        [InlineData("BIM: Piawaian JKR (Edisi Kedua 2021) ... pdf p.98", "BIM: Piawaian JKR")]
        [InlineData("Rooms placed", "Rooms placed")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void ShortName_IsTheFirstClause(string? name, string expected) => Assert.Equal(expected, RulesBlockText.ShortName(name));

        [Fact]
        public void ShortName_CutsALongClauseAtAWord_WithAnEllipsis()
        {
            var name = "Every habitable room must have a window that opens to the outside air and daylight for ventilation";
            var s = RulesBlockText.ShortName(name, 60);
            Assert.True(s.Length <= 61);
            Assert.EndsWith("…", s);
            Assert.StartsWith("Every habitable room must have a window", s);
            Assert.DoesNotContain("ventilat", s);
            // never cut inside a word: what is kept before the "…" is a prefix of the sentence that ends at a space
            Assert.Equal(' ', name[s.Length - 1]);
        }

        [Fact]
        public void TheRuleRow_ShowsTheShortName_AndKeepsTheWholeTextAsTheTooltip()
        {
            var s = Src("UI/Copilot/Controls/RulesBlock.cs");
            Assert.Contains("RulesBlockText.ShortName(r.Name)", s);
            Assert.Contains("name.ToolTip = r.Name", s);
        }

        // 4 orphan lines
        [Fact]
        public void TheRulesHeader_CarriesHighlightInModel_AndNoBareButtonOrDetailLineRemains()
        {
            var rules = Src("UI/Copilot/Controls/RulesBlock.cs");
            Assert.Contains("actions.ShowInModel != null", rules);
            Assert.Contains("CardKit.Ghost(ms ? \"Serlahkan dalam model\" : \"Highlight in model\"", rules);
            Assert.Contains("actions.ShowInModel != null && actions.RulesBlock == null", Src("UI/Copilot/Controls/FindingsBody.cs"));
            Assert.Contains("!(card.Kind == ResultKind.Findings && actions.RulesBlock != null)", Src("UI/Copilot/Controls/ResultCardView.cs"));
        }

        // 5 duplicate checks
        [Theory]
        [InlineData("MH-02 140 overlapping walls", false, true)]
        [InlineData("Look at MH-02: 140 overlapping walls", false, true)]
        [InlineData("mh-02 again", false, true)]
        [InlineData("MH-02 140 overlapping walls", true, false)]      // it has a button: it adds something
        [InlineData("MH-020 is another rule", false, false)]           // whole id only
        [InlineData("Two doors have no fire rating", false, false)]
        public void RestatesRule_OnlyAButtonlessCheckThatNamesAListedRuleId(string text, bool hasActions, bool restates) =>
            Assert.Equal(restates, RulesBlockText.RestatesRule(text, hasActions, new[] { "MH-01", "MH-02" }));

        [Fact]
        public void RestatesRule_NoRuleIds_NothingIsDropped()
        {
            Assert.False(RulesBlockText.RestatesRule("MH-02 x", false, null));
            Assert.False(RulesBlockText.RestatesRule("MH-02 x", false, new string[0]));
            Assert.Contains("RulesBlockText.RestatesRule(c.Text, c.Actions.Count > 0, actions.RuleIds)", Src("UI/Copilot/Controls/ChecksCard.cs"));
        }

        // 6 headline length
        [Fact]
        public void Clamp_KeepsAShortHeadline_AndCutsALongOneAtAWord()
        {
            Assert.Equal("3 changes applied", CardText.Clamp("3 changes applied"));
            var long300 = string.Join(" ", Enumerable.Repeat("filled", 60));
            var c = CardText.Clamp(long300, 160);
            Assert.True(c.Length <= 161);
            Assert.EndsWith("…", c);
            Assert.Equal("", CardText.Clamp(null));
        }

        [Fact]
        public void TheHeadline_IsThreeLinesAtMost_WithTheWholeTextAsATooltip()
        {
            var s = Src("UI/Copilot/Controls/ResultCardView.cs");
            Assert.Contains("CardText.Clamp(card.Headline)", s);
            Assert.Contains("text.MaxHeight = 66", s);
            Assert.Contains("text.ToolTip = card.Headline", s);
        }
    }
}
