using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class MentionTokenTests
    {
        // ── The crash case ──────────────────────────────────────────────────
        // InsertMention sets Editor.Text, which resets the caret to 0 and fires
        // TextChanged BEFORE the caller restores the caret. With text starting
        // "@" the old inline code computed Substring(1, -1) and threw
        // ArgumentOutOfRangeException out of the click handler — crashing Revit.

        [Fact]
        public void CaretZero_TextStartsWithAt_NoTokenAndNoThrow()
        {
            int at = MentionToken.Find("@Aras 01 tail", 0, out var query);
            Assert.Equal(-1, at);
            Assert.Null(query);
        }

        [Fact]
        public void CaretZero_EmptyText_NoToken()
        {
            Assert.Equal(-1, MentionToken.Find("", 0, out _));
            Assert.Equal(-1, MentionToken.Find(null, 0, out _));
        }

        // ── Space-in-query (the "Aras 01 found none" bug) ──────────────────

        [Fact]
        public void QueryWithSpace_IsAValidToken()
        {
            int at = MentionToken.Find("@Aras 01", 8, out var query);
            Assert.Equal(0, at);
            Assert.Equal("Aras 01", query);
        }

        [Fact]
        public void SpacedQuery_MatchesSpacedItem()
        {
            Assert.True(MentionToken.Matches("Aras 01", "Aras 01"));
            Assert.True(MentionToken.Matches("Aras 01", "aras 0"));
            Assert.False(MentionToken.Matches("Aras Tanah", "Aras 01"));
        }

        [Fact]
        public void TrailingSpaceAfterFullItem_NoLongerMatches()
        {
            // Right after InsertMention the text is "@Aras 01 " — the query
            // still parses, but matches nothing, so the picker stays closed.
            int at = MentionToken.Find("@Aras 01 ", 9, out var query);
            Assert.Equal(0, at);
            Assert.Equal("Aras 01 ", query);
            Assert.False(MentionToken.Matches("Aras 01", query));
        }

        // ── Existing behavior kept ──────────────────────────────────────────

        [Fact]
        public void PlainQuery_StillWorks()
        {
            int at = MentionToken.Find("see @Aras", 9, out var query);
            Assert.Equal(4, at);
            Assert.Equal("Aras", query);
        }

        [Fact]
        public void EmptyQueryRightAfterAt_OpensWithAllItems()
        {
            int at = MentionToken.Find("@", 1, out var query);
            Assert.Equal(0, at);
            Assert.Equal("", query);
        }

        [Fact]
        public void NoAtBeforeCaret_NoToken()
        {
            Assert.Equal(-1, MentionToken.Find("hello", 3, out _));
        }

        [Fact]
        public void SpaceImmediatelyAfterAt_Dismisses()
        {
            Assert.Equal(-1, MentionToken.Find("@ Aras", 6, out _));
        }

        [Fact]
        public void NewlineInQuery_Dismisses()
        {
            Assert.Equal(-1, MentionToken.Find("@a\nb", 4, out _));
        }

        [Fact]
        public void CaretPastTextLength_Clamps()
        {
            int at = MentionToken.Find("@Aras", 99, out var query);
            Assert.Equal(0, at);
            Assert.Equal("Aras", query);
        }

        [Fact]
        public void ProseLongerThanCap_Dismisses()
        {
            var text = "@" + new string('x', 41);
            Assert.Equal(-1, MentionToken.Find(text, text.Length, out _));
        }
    }
}
