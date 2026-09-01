using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;
using Xunit;

namespace Tests
{
    public class SavedCommandsDraftTests
    {
        [Fact]
        public void FromReply_seeds_name_template_tools()
        {
            var d = SavedCommandDraft.FromReply("Bina dinding dari CAD di Level 2, guna 150mm brick",
                new[] { "list_levels", "create_walls_batch", "list_levels" }, "run-1");
            Assert.Equal("Bina dinding dari CAD di Level", d.Name);
            Assert.Equal("Bina dinding dari CAD di Level 2, guna 150mm brick", d.Template);
            Assert.Equal(new[] { "list_levels", "create_walls_batch" }, d.ToolsCalled);
            Assert.Equal("run-1", d.SourceRunId);
            Assert.Empty(d.Inputs);
        }

        [Fact]
        public void MarkInput_replaces_selection_with_hole_and_adds_input()
        {
            var d = SavedCommandDraft.FromReply("walls on Level 2 please", new string[0], null);
            var start = d.Template.IndexOf("Level 2");
            Assert.True(d.MarkInput(start, "Level 2".Length, "level", out var err), err);
            Assert.Equal("walls on {level} please", d.Template);
            Assert.Single(d.Inputs);
            Assert.Equal("Level 2", d.Inputs[0].Label);
            Assert.True(d.Inputs[0].Required);
        }

        [Fact]
        public void MarkInput_rejects_bad_name_overlap_and_cap()
        {
            var d = SavedCommandDraft.FromReply("a b c d e f g h i j", new string[0], null);
            Assert.False(d.MarkInput(0, 1, "Bad Name", out var e1)); Assert.Contains("snake_case", e1);
            Assert.True(d.MarkInput(0, 1, "a", out _));
            Assert.False(d.MarkInput(0, 3, "x", out var e2)); Assert.Contains("overlaps", e2);
            for (int i = 0; i < 7; i++)
            {
                var tok = ((char)('b' + i)).ToString();
                Assert.True(d.MarkInput(d.Template.IndexOf(" " + tok) + 1, 1, tok, out _));
            }
            Assert.False(d.MarkInput(d.Template.LastIndexOf('j'), 1, "j", out var e3)); Assert.Contains("8", e3);
        }

        [Fact]
        public void UnmarkInput_restores_label_text()
        {
            var d = SavedCommandDraft.FromReply("walls on Level 2", new string[0], null);
            d.MarkInput(9, 7, "level", out _);
            d.UnmarkInput("level");
            Assert.Equal("walls on Level 2", d.Template);
            Assert.Empty(d.Inputs);
        }

        [Fact]
        public void SuggestInputName_is_snake_ascii_short()
        {
            Assert.Equal("level_2", SavedCommandDraft.SuggestInputName("Level 2"));
            Assert.Equal("brick_150mm", SavedCommandDraft.SuggestInputName("Brick 150mm!"));
            Assert.Equal("x", SavedCommandDraft.SuggestInputName("###"));
        }

        [Fact]
        public void SuggestSlug_matches_backend_kebab_rule()
        {
            Assert.Equal("my-walls-from-cad", SavedCommandDraft.SuggestSlug("Walls from CAD"));
            Assert.Equal("my-command", SavedCommandDraft.SuggestSlug("###"));
        }

        [Fact]
        public void ToRequest_maps_everything()
        {
            var d = SavedCommandDraft.FromReply("walls on Level 2", new[] { "list_levels" }, "run-9");
            d.MarkInput(9, 7, "level", out _);
            d.Name = "Walls from CAD";
            var r = d.ToRequest();
            Assert.Equal("Walls from CAD", r.NameEn);
            Assert.Equal("walls on {level}", r.PromptTemplate);
            Assert.Equal("level", r.Args[0].Name); Assert.Equal("Level 2", r.Args[0].LabelEn);
            Assert.Equal(new[] { "list_levels" }, r.ToolsCalled);
            Assert.Equal("run-9", r.SourceRunId);
        }
    }
}
