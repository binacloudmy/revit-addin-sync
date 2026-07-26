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
    }
}
