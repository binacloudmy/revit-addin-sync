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
    }
}
