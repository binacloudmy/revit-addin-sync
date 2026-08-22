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
        public void Live_jkr_coded_name_is_never_rewritten()
        {
            // "(TKk400a) 2400 x 2400 s300 @T1" is JKR's "(code) dimensions"
            // convention. Hyphenating its spaces would corrupt a compliant name,
            // so Suggest must stay silent no matter the segment target.
            Assert.Null(AuditNaming.Suggest("(TKk400a) 2400 x 2400 s300 @T1", 2));
            Assert.Null(AuditNaming.Suggest("(TKk400a) 2400 x 2400 s300 @T1", 3));
        }

        [Fact]
        public void Live_symbol_token_after_space_gets_clean_separator()
        {
            // Non-JKR name with a symbol token: "@T1" keeps its symbol; the
            // space before it becomes one separator with no stray "- ".
            Assert.Equal("Panel-2400-x-2400-s300-@T1",
                AuditNaming.Suggest("Panel 2400 x 2400 s300 @T1", 2));
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
                "Panel 2400 x 2400 s300 @T1", " - A  B - ", "A\t_ B", "A\u200B B",
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
            Assert.Equal("(tKk400a)-2400mm-@T1", AuditNaming.Suggest("(tKk400a) 2400mm @T1", 2));
        }

        // ── Section D: JKR-aware type naming (live model
        //    jkrAR24_5a_(BEde1A_p14-001)_A1_w-01_(S)_DS_220222a) ──────────────

        [Theory]
        [InlineData("(TKh281a) 600 x 1800  s900  @T3")]
        [InlineData("(TKh282a) 700 x 1200 s900  @T4")]
        [InlineData("(AKs002a) 3600 x 3600mm")]
        [InlineData("(LSc096a)")]
        [InlineData("(PT2p600a) 900 x 2325 a")]
        [InlineData("(TKk400b) 2100 x 2100 s300")]
        [InlineData("jkrAR_Wall_Ext_150")]
        [InlineData("jkrST-Column-300x300")]
        [InlineData("jkrME_Duct")]
        [InlineData("jkrAR24_5a_(BEde1A_p14-001)_A1")]
        public void Live_jkr_coded_type_names_are_jkr(string name)
        {
            Assert.True(AuditNaming.IsJkrName(name), name);
            Assert.True(AuditNaming.IsSectionDCompliant(name), name);
        }

        [Theory]
        [InlineData("UPVC")]
        [InlineData("Brick")]
        [InlineData("upvc")]
        [InlineData("Concrete")]
        public void Live_material_names_are_compliant_but_not_jkr(string name)
        {
            Assert.False(AuditNaming.IsJkrName(name), name);
            Assert.True(AuditNaming.IsMaterialName(name), name);
            Assert.True(AuditNaming.IsSectionDCompliant(name), name);
        }

        [Theory]
        [InlineData("Curtain Wall")]
        [InlineData("Generic")]
        [InlineData("Sloped Glazing")]
        [InlineData("Precast Stair")]
        [InlineData("Solid")]
        [InlineData("Glazed")]
        [InlineData("Empty")]
        [InlineData("600mm Arial")]
        [InlineData("Basic Wall")]
        [InlineData("Ground Floor Slab")]
        [InlineData("(Brick)")]          // parentheses alone are not a JKR code
        [InlineData("(123)")]            // no discipline letters
        [InlineData("(A1)")]             // too short to be a JKR code
        [InlineData("jkr")]              // prefix without discipline letters
        [InlineData("Jkrafter 200")]     // "jkr" buried in a word, not a prefix
        [InlineData("")]
        [InlineData("   ")]
        public void Revit_defaults_and_plain_names_are_not_compliant(string name)
        {
            Assert.False(AuditNaming.IsJkrName(name), name);
            Assert.False(AuditNaming.IsMaterialName(name), name);
            Assert.False(AuditNaming.IsSectionDCompliant(name), name);
        }

        [Fact]
        public void Jkr_code_survives_invisible_format_chars_and_nbsp()
        {
            Assert.True(AuditNaming.IsJkrName("﻿(TKh281a) 600 x 1800"));
            Assert.True(AuditNaming.IsJkrName("(TKh281a)​ 600"));
        }

        [Fact]
        public void Suggest_stays_silent_for_every_compliant_live_name()
        {
            foreach (var name in new[]
            {
                "(TKh281a) 600 x 1800  s900  @T3", "(TKh282a) 700 x 1200 s900  @T4",
                "(AKs002a) 3600 x 3600mm", "(LSc096a)", "(PT2p600a) 900 x 2325 a", "UPVC",
            })
                Assert.Null(AuditNaming.Suggest(name, 2));
        }

        [Fact]
        public void Suggest_still_fires_for_genuinely_non_jkr_names()
        {
            Assert.Equal("Curtain-Wall", AuditNaming.Suggest("Curtain Wall", 2));
            Assert.Equal("600mm-Arial", AuditNaming.Suggest("600mm Arial", 2));
            Assert.Null(AuditNaming.Suggest("Generic", 2));   // nothing to split — honest null
        }
    }
}
