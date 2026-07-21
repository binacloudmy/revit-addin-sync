using System.Collections.Generic;
using RevitWebAppSync.Helpers;
using Xunit;

namespace RevitWebAppSync.Tests
{
    // TableCsv turns the rows MarkdownRenderer already parsed for a | table |
    // block into an RFC 4180 CSV string, and suggests a save-dialog filename
    // from the nearest markdown heading above the table.
    public class TableCsvTests
    {
        private static List<string[]> Rows(params string[][] rows) => new List<string[]>(rows);

        [Fact]
        public void Serialize_PlainTable_HeaderFirstCrlfRows()
        {
            var csv = TableCsv.Serialize(Rows(
                new[] { "Mark", "Level", "Width" },
                new[] { "D1", "Aras 1", "900" },
                new[] { "D2", "Aras 2", "1200" }));

            Assert.Equal("Mark,Level,Width\r\nD1,Aras 1,900\r\nD2,Aras 2,1200\r\n", csv);
        }

        [Fact]
        public void Serialize_FieldWithComma_IsQuoted()
        {
            var csv = TableCsv.Serialize(Rows(
                new[] { "Name" },
                new[] { "Door, Single-Flush" }));

            Assert.Equal("Name\r\n\"Door, Single-Flush\"\r\n", csv);
        }

        [Fact]
        public void Serialize_FieldWithQuote_QuoteDoubledAndFieldQuoted()
        {
            var csv = TableCsv.Serialize(Rows(
                new[] { "Name" },
                new[] { "36\" door" }));

            Assert.Equal("Name\r\n\"36\"\" door\"\r\n", csv);
        }

        [Fact]
        public void Serialize_FieldWithNewline_IsQuoted()
        {
            var csv = TableCsv.Serialize(Rows(
                new[] { "Note" },
                new[] { "line1\nline2" }));

            Assert.Equal("Note\r\n\"line1\nline2\"\r\n", csv);
        }

        [Fact]
        public void Serialize_InlineMarkdown_StrippedToVisibleText()
        {
            var csv = TableCsv.Serialize(Rows(
                new[] { "Col" },
                new[] { "**bold**" },
                new[] { "*italic*" },
                new[] { "`code`" },
                new[] { "[312456](bina://select/312456)" }));

            Assert.Equal("Col\r\nbold\r\nitalic\r\ncode\r\n312456\r\n", csv);
        }

        [Fact]
        public void Serialize_RaggedRows_PaddedToWidestRow()
        {
            var csv = TableCsv.Serialize(Rows(
                new[] { "A", "B", "C" },
                new[] { "1" }));

            Assert.Equal("A,B,C\r\n1,,\r\n", csv);
        }

        [Fact]
        public void Serialize_EmptyInput_ReturnsEmptyString()
        {
            Assert.Equal("", TableCsv.Serialize(Rows()));
        }

        [Fact]
        public void Serialize_UnicodePreserved()
        {
            var csv = TableCsv.Serialize(Rows(
                new[] { "Tujuan" },
                new[] { "Bilik Air — Lelaki ✓" }));

            Assert.Equal("Tujuan\r\nBilik Air — Lelaki ✓\r\n", csv);
        }

        [Fact]
        public void Serialize_NullCell_TreatedAsEmpty()
        {
            var csv = TableCsv.Serialize(Rows(
                new[] { "A", "B" },
                new[] { "1", null }));

            Assert.Equal("A,B\r\n1,\r\n", csv);
        }

        [Fact]
        public void SuggestFileName_HeadingSlugified()
        {
            Assert.Equal("jadual-pintu-aras-1", TableCsv.SuggestFileName("Jadual Pintu Aras 1"));
        }

        [Fact]
        public void SuggestFileName_MarkdownAndIllegalCharsStripped()
        {
            Assert.Equal("door-schedule-level-2", TableCsv.SuggestFileName("**Door Schedule**: Level/2?"));
        }

        [Fact]
        public void SuggestFileName_NoHeading_Fallback()
        {
            Assert.Equal("bina-schedule", TableCsv.SuggestFileName(null));
            Assert.Equal("bina-schedule", TableCsv.SuggestFileName("   "));
            Assert.Equal("bina-schedule", TableCsv.SuggestFileName("???"));
        }

        [Fact]
        public void SuggestFileName_CappedAt60Chars()
        {
            var longHeading = new string('a', 50) + " " + new string('b', 50);
            var name = TableCsv.SuggestFileName(longHeading);
            Assert.True(name.Length <= 60);
            Assert.False(name.EndsWith("-"));
        }

        [Fact]
        public void SuggestFileName_CollapsesDashRuns()
        {
            Assert.Equal("a-b", TableCsv.SuggestFileName("a --- b"));
        }
    }
}
