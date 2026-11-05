#nullable enable
// Chat pane render rules that are pure logic (no WPF): streaming hold-back of
// unfinished tables / code blocks, markdown line rules, evidence dedupe, step
// text, and the live look-image stash. One test per behaviour.

using System.Collections.Generic;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.Helpers;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class PaneRenderTests
    {
        [Fact]
        public void Streaming_holds_back_an_open_table()
        {
            var s = MarkdownBlocks.StableForStreaming("Found:\n\n| L | N |\n|---|---|\n| L1 | 2 |\n", out var held);
            Assert.Equal("Found:", s);
            Assert.Equal(HeldBlock.Table, held);
        }

        [Fact]
        public void Streaming_draws_a_table_once_prose_follows_it()
        {
            var text = "Intro\n| A | B |\n|---|---|\n| 1 | 2 |\n\nDone";
            Assert.Equal(text, MarkdownBlocks.StableForStreaming(text, out var held));
            Assert.Equal(HeldBlock.None, held);
        }

        [Fact]
        public void Streaming_holds_back_an_unclosed_code_fence()
        {
            Assert.Equal("Run:", MarkdownBlocks.StableForStreaming("Run:\n```csharp\nvar x = 1;", out var held));
            Assert.Equal(HeldBlock.Code, held);
        }

        [Fact]
        public void Cells_keep_pipes_inside_code()
        {
            Assert.Equal(new[] { "`a|b`", "z" }, MarkdownBlocks.SplitCells("| `a|b` | z |"));
        }

        [Fact]
        public void Headings_lists_and_nesting()
        {
            Assert.Equal(4, MarkdownBlocks.HeadingLevel("#### Deep", out var h));
            Assert.Equal("Deep", h);
            Assert.True(MarkdownBlocks.TryBullet("    - **Bold** item", out var level, out var text));
            Assert.Equal(2, level);
            Assert.Equal("**Bold** item", text);
            Assert.True(MarkdownBlocks.TryNumbered("2. second", out _, out var n, out _));
            Assert.Equal("2", n);
        }

        [Fact]
        public void Only_the_last_message_holding_an_evidence_draws_it()
        {
            var ev = new object();
            var list = new List<object?> { ev, null, ev };
            Assert.False(EvidenceDedupe.IsLastHolder(list, 0));
            Assert.True(EvidenceDedupe.IsLastHolder(list, 2));
        }

        [Fact]
        public void Identical_tables_export_once()
        {
            ResultTable T(string cell) => new ResultTable
            {
                Title = "Doors", Columns = new List<string> { "Level" }, Rows = new List<List<string>> { new List<string> { cell } },
            };
            Assert.Equal(2, EvidenceDedupe.DistinctTables(new[] { T("L1"), T("L1"), T("L2") }).Count);
        }

        [Fact]
        public void Step_raw_text_is_never_the_headline()
        {
            var raw = "System.NullReferenceException: Object reference not set.";
            Assert.Equal("Something went wrong in this turn", StepText.Title("error", raw, "error:3"));
            Assert.Equal(raw, StepText.Raw("error", raw, ""));
            Assert.Equal("Found 12 doors", StepText.Raw("tool", "Finding elements", "Found 12 doors"));
        }

        [Fact]
        public void Look_stash_keeps_one_turns_images_once()
        {
            Envelope Img(string b64) => new Envelope { Image = new ImageDto { Base64 = b64, Width = 10, Height = 10 } };
            LookImageStash.Clear();
            LookImageStash.AddFrom("t1", new[] { Img("A"), Img("A"), Img("B") });
            Assert.Equal(2, LookImageStash.Count);
            LookImageStash.AddFrom("t2", new[] { Img("C") });
            Assert.Single(LookImageStash.Current());
            LookImageStash.Clear();
        }
    }
}
