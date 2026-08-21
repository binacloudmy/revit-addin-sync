// AuditNaming.Suggest — the conservative rename transform.
//
// The contract the checkers rely on: a suggestion is offered ONLY when a
// deterministic whitespace/separator transform reaches the required segment
// count, and null ("no confident transform") is a real, honest answer. These
// tests pin both sides so a checker can quote a suggestion verbatim without
// second-guessing it.

using BinaVibe.Mcp.Tools.Audit;
using Xunit;

namespace Tests
{
    public class AuditNamingTests
    {
        [Fact]
        public void Whitespace_becomes_separator()
        {
            Assert.Equal("Ground-Floor-Slab", AuditNaming.Suggest("Ground Floor Slab", 3));
        }

        [Fact]
        public void Two_segment_target_from_whitespace()
        {
            Assert.Equal("Basic-Wall", AuditNaming.Suggest("Basic Wall", 2));
        }

        [Fact]
        public void Already_conforming_returns_null()
        {
            // Nothing to change and it already meets the count — no suggestion.
            Assert.Null(AuditNaming.Suggest("PRJ-ARC-L01", 3));
        }

        [Fact]
        public void Single_word_too_few_segments_returns_null()
        {
            Assert.Null(AuditNaming.Suggest("Wall", 2));
        }

        [Fact]
        public void Collapses_repeated_separators_and_whitespace()
        {
            Assert.Equal("BLOCK-A-TYPE1", AuditNaming.Suggest("BLOCK  A __ TYPE1", 3));
        }

        [Fact]
        public void Trims_leading_and_trailing_separators()
        {
            Assert.Equal("A-B-C", AuditNaming.Suggest("  A - B - C  ", 3));
        }

        [Fact]
        public void Result_is_idempotent()
        {
            var once = AuditNaming.Suggest("Ground Floor Slab", 3);
            Assert.NotNull(once);
            // Feeding the suggestion back in yields no further change.
            Assert.Null(AuditNaming.Suggest(once!, 3));
        }

        [Fact]
        public void Empty_or_whitespace_returns_null()
        {
            Assert.Null(AuditNaming.Suggest("", 2));
            Assert.Null(AuditNaming.Suggest("   ", 2));
        }

        // ── live borang (BIM 010 run) cases ───────────────────────────────

        [Fact]
        public void Live_unit_and_font_name()
        {
            Assert.Equal("600mm-Arial", AuditNaming.Suggest("600mm Arial", 2));
        }

        [Fact]
        public void Live_symbol_token_after_space_gets_clean_separator()
        {
            // "@T1" keeps its symbol; the space before it becomes one separator
            // with no stray "- " and nothing glued.
            Assert.Equal("(TKk400a)-2400-x-2400-s300-@T1",
                AuditNaming.Suggest("(TKk400a) 2400 x 2400 s300 @T1", 2));
        }

        [Fact]
        public void Live_single_token_names_stay_null()
        {
            // Nothing deterministic to split — "no confident suggestion" is the
            // honest answer, never an invented segment.
            Assert.Null(AuditNaming.Suggest("DN100", 2));
            Assert.Null(AuditNaming.Suggest("Socket", 2));
            Assert.Null(AuditNaming.Suggest("(TKk400a)", 2));
            Assert.Null(AuditNaming.Suggest("Arial600mm", 2));
        }

        [Fact]
        public void Invisible_format_chars_count_as_whitespace()
        {
            // Zero-width space / NBSP pasted into a name render as a stray gap
            // in the PDF; they are boundaries, so they become the separator and
            // never survive into the suggestion.
            Assert.Equal("s300-@T1", AuditNaming.Suggest("s300 \u200B@T1", 2));
            Assert.Equal("s300-@T1", AuditNaming.Suggest("s300-\u200B@T1", 2));
            Assert.Equal("s300-@T1", AuditNaming.Suggest("s300\u00A0@T1", 2));
            Assert.Equal("A-B", AuditNaming.Suggest("\uFEFFA B", 2));
        }

        [Fact]
        public void Suggestion_never_contains_whitespace_or_edge_separators()
        {
            foreach (var name in new[]
            {
                "(TKk400a) 2400 x 2400 s300 @T1", " - A  B - ", "A\t_ B", "A\u200B B",
            })
            {
                var s = AuditNaming.Suggest(name, 2);
                Assert.NotNull(s);
                Assert.DoesNotContain(' ', s!);
                Assert.DoesNotContain("- ", s);
                Assert.DoesNotContain(" -", s);
                Assert.False(s.StartsWith('-') || s.EndsWith('-'), s);
            }
        }

        [Fact]
        public void No_case_change_no_character_stripping()
        {
            Assert.Equal("(TKk400a)-2400mm-@T1", AuditNaming.Suggest("(TKk400a) 2400mm @T1", 2));
        }
    }
}
