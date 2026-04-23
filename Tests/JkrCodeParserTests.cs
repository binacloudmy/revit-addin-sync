using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class JkrCodeParserTests
    {
        // ── Standard codes in parentheses ────────────────

        [Theory]
        [InlineData("jkrAR_wll-b_(DBb300a) Batu Bata", "DBb300a")]
        [InlineData("jkrAR_dor-k_(PTa001a) Pintu Kayu", "PTa001a")]
        [InlineData("jkrAR_flr-t_(LFh301a) Jubin", "LFh301a")]
        [InlineData("jkrME_pip-s_(LSw952a) Pipe", "LSw952a")]
        public void Parse_TypeNameWithCode_ExtractsCode(string typeName, string expected)
        {
            Assert.Equal(expected, JkrCodeParser.Parse(null, null, typeName));
        }

        // ── Priority: type > family > element ────────────

        [Fact]
        public void Parse_TypeTakesPriority()
        {
            var result = JkrCodeParser.Parse(
                elementName: "element_(AA11)",
                familyName: "family_(BB22)",
                typeName: "type_(CC33)");
            Assert.Equal("CC33", result);
        }

        [Fact]
        public void Parse_FamilyFallback()
        {
            var result = JkrCodeParser.Parse(
                elementName: "element_(AA11)",
                familyName: "family_(BB22)",
                typeName: "no code here");
            Assert.Equal("BB22", result);
        }

        [Fact]
        public void Parse_ElementFallback()
        {
            var result = JkrCodeParser.Parse(
                elementName: "element_(AA11)",
                familyName: null,
                typeName: null);
            Assert.Equal("AA11", result);
        }

        // ── No code found ────────────────────────────────

        [Theory]
        [InlineData("Basic Wall 200mm")]
        [InlineData("")]
        [InlineData(null)]
        public void Parse_NoCode_ReturnsNull(string name)
        {
            Assert.Null(JkrCodeParser.Parse(name));
        }

        // ── Underscore pattern ───────────────────────────

        [Fact]
        public void Parse_UnderscorePattern()
        {
            var result = JkrCodeParser.Parse(null, null, "jkrAR_wll-b_(DBb300a)");
            Assert.Equal("DBb300a", result);
        }

        // ── Broad fallback ───────────────────────────────

        [Fact]
        public void Parse_BroadPattern()
        {
            // Broader pattern matches 2-4 alpha + 2-4 digits
            var result = JkrCodeParser.Parse(null, null, "some name (ABCD1234)");
            Assert.Equal("ABCD1234", result);
        }
    }
}
