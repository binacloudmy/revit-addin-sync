#nullable enable
// Chat pane render rules that are pure logic (no WPF): the streaming hold-back
// of unfinished tables / code blocks, the markdown line rules the renderer
// uses, evidence dedupe, step text, and the live look-image stash.

using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Helpers;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class StreamingHoldBackTests
    {
        [Fact]
        public void Plain_prose_is_drawn_as_it_arrives()
        {
            var s = MarkdownBlocks.StableForStreaming("Hello **wor", out var held);
            Assert.Equal("Hello **wor", s);
            Assert.Equal(HeldBlock.None, held);
        }

        [Fact]
        public void A_table_still_arriving_is_held_back()
        {
            var text = "Found 3 doors:\n\n| Level | Count |\n|---|---|\n| L1 | 2 |\n| L2 |";
            var s = MarkdownBlocks.StableForStreaming(text, out var held);
            Assert.Equal("Found 3 doors:", s);
            Assert.Equal(HeldBlock.Table, held);
        }

        [Fact]
        public void A_table_whose_last_row_ended_with_a_newline_is_still_open()
        {
            var text = "Intro\n| A | B |\n|---|---|\n| 1 | 2 |\n";
            MarkdownBlocks.StableForStreaming(text, out var held);
            Assert.Equal(HeldBlock.Table, held);
        }

        [Fact]
        public void A_table_followed_by_prose_is_complete()
        {
            var text = "Intro\n| A | B |\n|---|---|\n| 1 | 2 |\n\nThat is all";
            var s = MarkdownBlocks.StableForStreaming(text, out var held);
            Assert.Equal(text, s);
            Assert.Equal(HeldBlock.None, held);
        }

        [Fact]
        public void The_separator_row_alone_never_flashes()
        {
            var s = MarkdownBlocks.StableForStreaming("Intro\n| A | B |\n|---", out var held);
            Assert.Equal("Intro", s);
            Assert.Equal(HeldBlock.Table, held);
        }

        [Fact]
        public void A_partial_first_row_is_held()
        {
            var s = MarkdownBlocks.StableForStreaming("Intro\n| Na", out var held);
            Assert.Equal("Intro", s);
            Assert.Equal(HeldBlock.Table, held);
        }

        [Fact]
        public void A_table_without_outer_pipes_is_held_until_done()
        {
            var text = "Intro\nLevel | Count\n--- | ---\nL1 | 2";
            var s = MarkdownBlocks.StableForStreaming(text, out var held);
            Assert.Equal("Intro", s);
            Assert.Equal(HeldBlock.Table, held);
        }

        [Fact]
        public void An_unclosed_code_fence_is_held_back()
        {
            var text = "Run this:\n```csharp\nvar x = 1;\n";
            var s = MarkdownBlocks.StableForStreaming(text, out var held);
            Assert.Equal("Run this:", s);
            Assert.Equal(HeldBlock.Code, held);
        }

        [Fact]
        public void A_closed_code_fence_is_drawn_and_pipes_inside_code_do_not_count()
        {
            var text = "Run:\n```\na | b\n```\nDone";
            var s = MarkdownBlocks.StableForStreaming(text, out var held);
            Assert.Equal(text, s);
            Assert.Equal(HeldBlock.None, held);
        }

        [Fact]
        public void Crlf_and_empty_input()
        {
            Assert.Equal("", MarkdownBlocks.StableForStreaming(null, out var h1));
            Assert.Equal(HeldBlock.None, h1);
            Assert.Equal("a\nb", MarkdownBlocks.StableForStreaming("a\r\nb", out _));
        }
    }

    public class MarkdownBlocksTests
    {
        [Fact]
        public void Cells_keep_pipes_inside_code_and_escaped_pipes()
        {
            Assert.Equal(new[] { "`a|b`", "x|y", "z" }, MarkdownBlocks.SplitCells("| `a|b` | x\\|y | z |"));
            Assert.Equal(new[] { "L1", "2" }, MarkdownBlocks.SplitCells("L1 | 2"));
        }

        [Fact]
        public void Table_start_with_and_without_outer_pipes()
        {
            Assert.True(MarkdownBlocks.TableStartsAt(new[] { "| a | b |" }, 0));
            Assert.True(MarkdownBlocks.TableStartsAt(new[] { "a | b", "--- | ---" }, 0));
            Assert.False(MarkdownBlocks.TableStartsAt(new[] { "this | that is prose", "more prose" }, 0));
        }

        [Theory]
        [InlineData("# Title", 1, "Title")]
        [InlineData("#### Deep", 4, "Deep")]
        [InlineData("## Closed ##", 2, "Closed")]
        [InlineData("#NoSpace", 0, "")]
        public void Headings(string line, int level, string text)
        {
            Assert.Equal(level, MarkdownBlocks.HeadingLevel(line, out var t));
            Assert.Equal(text, t);
        }

        [Fact]
        public void Lists_rules_and_separators()
        {
            Assert.True(MarkdownBlocks.TryBullet("- **Bold** item", out var l0, out var t0));
            Assert.Equal(0, l0); Assert.Equal("**Bold** item", t0);
            Assert.True(MarkdownBlocks.TryBullet("    + nested", out var l2, out _));
            Assert.Equal(2, l2);
            Assert.True(MarkdownBlocks.TryNumbered("2) second", out _, out var n, out var nt));
            Assert.Equal("2", n); Assert.Equal("second", nt);
            Assert.True(MarkdownBlocks.IsRule("---"));
            Assert.True(MarkdownBlocks.IsRule("* * *"));
            Assert.False(MarkdownBlocks.TryBullet("---", out _, out _));
            Assert.False(MarkdownBlocks.TryBullet("***", out _, out _));
            Assert.True(MarkdownBlocks.IsSeparatorRow("|:---|---:|"));
            Assert.False(MarkdownBlocks.IsSeparatorRow("---"));
        }
    }

    public class EvidenceDedupeTests
    {
        [Fact]
        public void Only_the_last_message_holding_an_evidence_draws_it()
        {
            var ev = new object();
            var other = new object();
            var list = new List<object?> { ev, null, other, ev };
            Assert.False(EvidenceDedupe.IsLastHolder(list, 0));
            Assert.False(EvidenceDedupe.IsLastHolder(list, 1));
            Assert.True(EvidenceDedupe.IsLastHolder(list, 2));
            Assert.True(EvidenceDedupe.IsLastHolder(list, 3));
        }

        [Fact]
        public void Identical_tables_export_once()
        {
            ResultTable T(string title, string cell) => new ResultTable
            {
                Title = title, Columns = new List<string> { "Level" }, Rows = new List<List<string>> { new List<string> { cell } },
            };
            var distinct = EvidenceDedupe.DistinctTables(new[] { T("Doors", "L1"), T("Doors", "L1"), T("Doors", "L2") });
            Assert.Equal(2, distinct.Count);
        }

        [Fact]
        public void The_same_image_and_the_same_line_show_once()
        {
            var a = new ImageDto { Base64 = "AAA" };
            Assert.Single(EvidenceDedupe.DistinctImages(new[] { a, new ImageDto { Base64 = "AAA" }, new ImageDto { Base64 = "" } }));
            Assert.Equal(new[] { "x", "y" }, EvidenceDedupe.DistinctBy(new[] { "x", "y", "x" }, s => s));
        }
    }

    public class StepTextTests
    {
        [Fact]
        public void An_error_step_never_shows_its_raw_message_as_the_headline()
        {
            var raw = "System.NullReferenceException: Object reference not set to an instance of an object.";
            Assert.Equal("Something went wrong in this turn", StepText.Title("error", raw, "error:3"));
            Assert.Equal(raw, StepText.Raw("error", raw, ""));
        }

        [Fact]
        public void A_tool_step_keeps_its_label_and_hides_the_summary_behind_details()
        {
            Assert.Equal("Finding elements", StepText.Title("tool", "Finding elements", "call:1"));
            Assert.Equal("", StepText.Short("tool", StepState.Done));
            Assert.Equal("This step didn't work.", StepText.Short("tool", StepState.Error));
            Assert.Equal("Found 12 doors on Level 1", StepText.Raw("tool", "Finding elements", "Found 12 doors on Level 1"));
            Assert.Equal("", StepText.Raw("tool", "Same", "Same"));
        }
    }

    [Collection("LookImageStash")]
    public class LookImageStashTests
    {
        private static Envelope Img(string b64) => new Envelope { Image = new ImageDto { Base64 = b64, Width = 10, Height = 10 } };

        [Fact]
        public void Keeps_one_turns_images_once_and_resets_on_a_new_turn()
        {
            LookImageStash.Clear();
            LookImageStash.AddFrom("t1", new[] { Img("A"), new Envelope(), Img("A"), Img("B") });
            Assert.Equal(2, LookImageStash.Current().Count);
            LookImageStash.AddFrom("t2", new[] { Img("C") });
            var now = LookImageStash.Current();
            Assert.Single(now);
            Assert.Equal("C", now[0].Base64);
            LookImageStash.Clear();
            Assert.Empty(LookImageStash.Current());
        }

        [Fact]
        public void Caps_images_per_turn()
        {
            LookImageStash.Clear();
            LookImageStash.AddFrom("t", new[] { Img("1"), Img("2"), Img("3"), Img("4") });
            Assert.Equal(LookImageStash.MaxPerTurn, LookImageStash.Count);
            LookImageStash.Clear();
        }
    }
}
