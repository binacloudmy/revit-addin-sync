#nullable enable
// UX review Q1 (18 Nov, be dev's note): the backend streams the whole answer AND sends its first sentence as result.reply, so the headline and the
// first sentence of the body were the same words. The body now starts after the headline.
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class HeadlineOnce0139Tests
    {
        [Fact]
        public void TheBodyThatStartsWithTheHeadline_LosesItsFirstSentence()
        {
            var text = "Aras 01 has 60 Doors but 0 Door Tags (56 doors visible). Unloaded links were not searched. Do you want me to tag them?";
            var headline = "Aras 01 has 60 Doors but 0 Door Tags (56 doors visible).";
            Assert.Equal("Unloaded links were not searched. Do you want me to tag them?", ProseSplit.RestAfterHeadline(text, headline));
        }

        [Fact]
        public void AHeadlineCutAtAClause_StillMatches_AndEmphasisIsIgnored()
        {
            var text = "**1,458 walls** in the host model, grouped by level: Aras 01 828; Aras Tanah 368.";
            Assert.Equal("grouped by level: Aras 01 828; Aras Tanah 368.", ProseSplit.RestAfterHeadline(text, "1,458 walls in the host model,"));
        }

        [Fact]
        public void NoMatch_IsNull_AndAnIdenticalTextIsNothingMore()
        {
            Assert.Null(ProseSplit.RestAfterHeadline("Something else entirely.", "56 doors."));
            Assert.Null(ProseSplit.RestAfterHeadline("", "56 doors."));
            Assert.Null(ProseSplit.RestAfterHeadline("56 doors.", ""));
            Assert.Equal("", ProseSplit.RestAfterHeadline("56 doors in 9 types.", "56 doors in 9 types."));
        }

        [Fact]
        public void TheScopeLine_NamesWhatWasNotSearched_AtMostThree()
        {
            Assert.Equal("", ScopeNote.Line(null, ReceiptLanguage.En));
            Assert.Equal("Not searched: link: A (unloaded)", ScopeNote.Line(new[] { "link: A (unloaded)" }, ReceiptLanguage.En));
            Assert.Equal("Not searched: a; b; c and 2 more", ScopeNote.Line(new[] { "a", "b", "c", "d", "e" }, ReceiptLanguage.En));
            Assert.Equal("Tidak dicari: a; b; c dan 1 lagi", ScopeNote.Line(new[] { "a", "b", "c", "d" }, ReceiptLanguage.Ms));
        }

        [Fact]
        public void TheRestKeepsItsMarkdown_BoldCodeAndAList()
        {
            var headline = "56 doors in 9 types.";
            var bold = "56 doors in 9 types. The biggest is **(PTh300a) 20** and `PTt760b` has 12.";
            Assert.Equal("The biggest is **(PTh300a) 20** and `PTt760b` has 12.", ProseSplit.RestAfterHeadline(bold, headline));
            var list = "56 doors in 9 types.\n- (PTh300a) 20\n- (PTt760b) 12";
            Assert.Equal("- (PTh300a) 20\n- (PTt760b) 12", ProseSplit.RestAfterHeadline(list, headline));   // the list keeps its first "- "
        }

        [Fact]
        public void AnEmphasisSpanTheCutRunsThrough_IsReopenedOnTheRest()
        {
            var text = "**56 doors in 9 types, grouped by type** as follows.";
            Assert.Equal("as follows.", ProseSplit.RestAfterHeadline(text, "56 doors in 9 types, grouped by type"));   // the span closes with the headline
            var through = "**56 doors in 9 types, and the biggest group is (PTh300a)** with 20.";
            Assert.Equal("**the biggest group is (PTh300a)** with 20.", ProseSplit.RestAfterHeadline(through, "56 doors in 9 types, and"));
        }

        [Fact]
        public void TheNotSearchedList_IsParsedButNeverDrawn_OnTheAnswer()
        {
            // The user (18 Nov): "no need to show this part for the Copilot response": result.not_searched stays on the model (the contract is kept),
            // no control draws it: neither the card nor the prose answer, nor under "More detail".
            var dir = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RevitWebAppSync.csproj"))) dir = dir.Parent;
            string Src(string rel) => System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, rel));
            Assert.DoesNotContain("ScopeFootnote", Src("UI/Copilot/Controls/ResultCardView.cs"));
            Assert.DoesNotContain("ScopeFootnote", Src("UI/Copilot/Screens/ChatView.xaml.cs"));
            Assert.DoesNotContain("ScopeNote.Line", Src("UI/Copilot/Controls/ResultCardView.cs"));
            Assert.Contains("NotSearched = (result?.NotSearched", Src("UI/Copilot/Model/ResultCardModel.cs"));   // still read from the result
        }
    }
}
