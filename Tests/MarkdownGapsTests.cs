#nullable enable
// Chat markdown syntax the renderer used to print literally: "1)" lists, "+" and
// dot bullets, horizontal rules, ~~strike~~, __bold__ and list continuation lines.
// Pure parsing only (MarkdownBlocks, no WPF). The existing "-" / "1." behaviour is
// pinned here too, so a change to the shared patterns cannot break it unnoticed.

using System.Linq;
using RevitWebAppSync.Helpers;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class MarkdownGapsTests
    {
        // "Kind:text" per token, joined with "|", so one assert shows the whole split.
        private static string Split(string line) =>
            string.Join("|", MarkdownBlocks.TokenizeInline(line).Select(t => t.Kind.ToString() + ":" + t.Text));

        // ---- 1. "1)" ordered lists ------------------------------------------

        [Fact]
        public void Numbered_list_accepts_a_closing_parenthesis()
        {
            Assert.True(MarkdownBlocks.TryNumbered("1) first", out var level, out var number, out var text));
            Assert.Equal(0, level);
            Assert.Equal("1", number);
            Assert.Equal("first", text);
        }

        [Fact]
        public void Numbered_parenthesis_list_keeps_nesting_and_needs_a_space()
        {
            Assert.True(MarkdownBlocks.TryNumbered("  12) nested", out var level, out var number, out var text));
            Assert.Equal(1, level);
            Assert.Equal("12", number);
            Assert.Equal("nested", text);
            Assert.False(MarkdownBlocks.TryNumbered("1)no space", out _, out _, out _));
        }

        // ---- 2. "+" and dot bullets ----------------------------------------

        [Theory]
        [InlineData("+ plus item")]
        [InlineData("• dot item")]
        [InlineData("· middle dot item")]
        public void Plus_and_dot_characters_start_a_bullet(string line)
        {
            Assert.True(MarkdownBlocks.TryBullet(line, out var level, out var text));
            Assert.Equal(0, level);
            Assert.EndsWith("item", text);
            Assert.DoesNotContain("•", text);
            Assert.DoesNotContain("·", text);
        }

        [Fact]
        public void Dot_bullet_keeps_its_indent_level_and_text()
        {
            Assert.True(MarkdownBlocks.TryBullet("    • deep", out var level, out var text));
            Assert.Equal(2, level);
            Assert.Equal("deep", text);
        }

        [Theory]
        [InlineData("+no space")]
        [InlineData("·no space")]
        [InlineData("•no space")]
        [InlineData("**bold** start")]
        [InlineData("plain text")]
        public void Marker_without_a_following_space_is_not_a_bullet(string line)
        {
            Assert.False(MarkdownBlocks.TryBullet(line, out _, out _));
        }

        // ---- 3. horizontal rules ------------------------------------------

        [Theory]
        [InlineData("---")]
        [InlineData("***")]
        [InlineData("___")]
        [InlineData("-----")]
        [InlineData("  ---  ")]
        [InlineData("- - -")]
        public void Three_or_more_dashes_stars_or_underscores_are_a_rule(string line)
        {
            Assert.True(MarkdownBlocks.IsHorizontalRule(line));
        }

        [Theory]
        [InlineData("")]
        [InlineData("--")]
        [InlineData("- item")]
        [InlineData("***bold***")]
        [InlineData("--- text")]
        [InlineData("-*-")]
        [InlineData("plain")]
        public void Other_lines_are_not_a_rule(string line)
        {
            Assert.False(MarkdownBlocks.IsHorizontalRule(line));
        }

        [Fact]
        public void A_rule_is_not_read_as_a_bullet()
        {
            Assert.False(MarkdownBlocks.TryBullet("---", out _, out _));
            Assert.False(MarkdownBlocks.TryBullet("***", out _, out _));
        }

        // ---- 4. ~~strike~~ -------------------------------------------------

        [Fact]
        public void Double_tildes_strike_through()
        {
            Assert.Equal("Text:a |Strike:gone|Text: b", Split("a ~~gone~~ b"));
        }

        [Fact]
        public void Strike_can_hold_other_spans()
        {
            var tokens = MarkdownBlocks.TokenizeInline("~~old **name**~~");
            Assert.Single(tokens);
            Assert.Equal(InlineKind.Strike, tokens[0].Kind);
            Assert.Equal("old **name**", tokens[0].Text);
        }

        [Fact]
        public void Tildes_hugging_spaces_stay_literal()
        {
            Assert.Equal("Text:~~ not struck ~~", Split("~~ not struck ~~"));
        }

        // ---- 5. __bold__ ---------------------------------------------------

        [Fact]
        public void Double_underscores_are_bold_like_double_stars()
        {
            Assert.Equal("Bold:Total|Text: doors", Split("__Total__ doors"));
            Assert.Equal("Bold:Total|Text: doors", Split("**Total** doors"));
            Assert.Equal("Text:see |Bold:this|Text:.", Split("see __this__."));
        }

        [Theory]
        [InlineData("use snake_case_name here")]
        [InlineData("wall_type__v2 and door_family")]
        [InlineData("a__b__c")]
        [InlineData("__ not bold __")]
        public void Underscores_inside_words_stay_literal(string line)
        {
            var tokens = MarkdownBlocks.TokenizeInline(line);
            Assert.Single(tokens);
            Assert.Equal(InlineKind.Text, tokens[0].Kind);
            Assert.Equal(line, tokens[0].Text);
        }

        // ---- 6. list continuation lines -----------------------------------

        [Theory]
        [InlineData("  more text of the item", "more text of the item")]
        [InlineData("   three spaces", "three spaces")]
        [InlineData("\ttabbed", "tabbed")]
        [InlineData("  **Note:** still the item", "**Note:** still the item")]
        [InlineData("  2024 was the year", "2024 was the year")]
        public void Indented_text_after_a_list_item_continues_it(string line, string expected)
        {
            Assert.True(MarkdownBlocks.TryListContinuation(line, out var text));
            Assert.Equal(expected, text);
        }

        [Theory]
        [InlineData("Normal paragraph")]
        [InlineData(" one space only")]
        [InlineData("")]
        [InlineData("    ")]
        [InlineData("  - nested bullet")]
        [InlineData("  + plus bullet")]
        [InlineData("  • dot bullet")]
        [InlineData("  3. nested number")]
        [InlineData("  3) nested number")]
        [InlineData("  ## heading")]
        [InlineData("  > quote")]
        [InlineData("  ---")]
        [InlineData("  | a | b |")]
        [InlineData("  ```")]
        public void Other_lines_do_not_continue_a_list_item(string line)
        {
            Assert.False(MarkdownBlocks.TryListContinuation(line, out var text));
            Assert.Equal("", text);
        }

        [Fact]
        public void A_list_item_then_a_normal_paragraph_still_splits()
        {
            Assert.True(MarkdownBlocks.TryBullet("- item", out _, out _));
            // Not indented: it is a new paragraph, not more of the item.
            Assert.False(MarkdownBlocks.TryListContinuation("Next paragraph", out _));
            Assert.False(MarkdownBlocks.TryBullet("Next paragraph", out _, out _));
            Assert.False(MarkdownBlocks.TryNumbered("Next paragraph", out _, out _, out _));
        }

        // ---- existing behaviour stays --------------------------------------

        [Fact]
        public void Dash_and_star_bullets_are_unchanged()
        {
            Assert.True(MarkdownBlocks.TryBullet("- first", out var l1, out var t1));
            Assert.Equal(0, l1);
            Assert.Equal("first", t1);
            Assert.True(MarkdownBlocks.TryBullet("* second", out var l2, out var t2));
            Assert.Equal(0, l2);
            Assert.Equal("second", t2);
            Assert.True(MarkdownBlocks.TryBullet("  - nested", out var l3, out var t3));
            Assert.Equal(1, l3);
            Assert.Equal("nested", t3);
            Assert.False(MarkdownBlocks.TryBullet("-no space", out _, out _));
        }

        [Fact]
        public void Dot_numbered_list_is_unchanged()
        {
            Assert.True(MarkdownBlocks.TryNumbered("1. one", out var l1, out var n1, out var t1));
            Assert.Equal(0, l1);
            Assert.Equal("1", n1);
            Assert.Equal("one", t1);
            Assert.True(MarkdownBlocks.TryNumbered("  10. ten", out var l2, out var n2, out var t2));
            Assert.Equal(1, l2);
            Assert.Equal("10", n2);
            Assert.Equal("ten", t2);
            Assert.False(MarkdownBlocks.TryNumbered("3.14 is pi", out _, out _, out _));
        }

        [Fact]
        public void Existing_inline_spans_still_split_the_same_way()
        {
            Assert.Equal("Text:a |Bold:b|Text: c", Split("a **b** c"));
            Assert.Equal("Text:a |Italic:b|Text: c", Split("a *b* c"));
            Assert.Equal("Text:a |Code:b|Text: c", Split("a `b` c"));
            Assert.Equal("Code:a*b*c", Split("`a*b*c`"));
            Assert.Equal("Text:2 * 3 * 4", Split("2 * 3 * 4"));

            var link = MarkdownBlocks.TokenizeInline("[go](bina://select/123456)");
            Assert.Single(link);
            Assert.Equal(InlineKind.Link, link[0].Kind);
            Assert.Equal("go", link[0].Text);
            Assert.Equal("bina://select/123456", link[0].Url);

            var image = MarkdownBlocks.TokenizeInline("![pic](http://x/y.png)");
            Assert.Single(image);
            Assert.Equal(InlineKind.Image, image[0].Kind);
            Assert.Equal("pic", image[0].Text);
            Assert.Equal("http://x/y.png", image[0].Url);
        }
    }
}
