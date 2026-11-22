#nullable enable
using System.Linq;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Jkr.ViewModels;
using Xunit;

namespace RevitAddinSync.Tests
{
    /// <summary>AI-510: a type is renamed through set_parameters "Type Name" (Element.Name), never refused as read-only. The pure rules
    /// (which names count, empty, forbidden characters, duplicates) and the link between the writer and the confirmed_write
    /// field of Da.1 / Dc.1.</summary>
    public class TypeRenameTests
    {
        [Theory]
        [InlineData("Type Name", true)]
        [InlineData("type name", true)]
        [InlineData("  Type Name ", true)]
        [InlineData("Name", true)]
        [InlineData("Family Name", false)]
        [InlineData("Material", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Rename_param_names(string? param, bool expected) => Assert.Equal(expected, TypeRename.IsRenameParam(param));

        [Fact]
        public void A_new_name_is_accepted_and_trimmed()
        {
            Assert.Null(TypeRename.Check("  (TGc002) 1200 x 3500 a ", "Monolithic Stair", new[] { "Other" }, out var name));
            Assert.Equal("(TGc002) 1200 x 3500 a", name);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void An_empty_name_is_refused(string? given)
        {
            var v = TypeRename.Check(given, "Old", new string[0], out _);
            Assert.NotNull(v);
            Assert.NotEqual("same", v);
            Assert.Contains("empty", v);
        }

        [Theory]
        [InlineData("A:B")]
        [InlineData("A{B}")]
        [InlineData("A[1]")]
        [InlineData("A|B")]
        [InlineData("A;B")]
        [InlineData("A<B>")]
        [InlineData("A?")]
        [InlineData("A\\B")]
        public void A_name_with_a_character_revit_refuses_is_refused_before_the_write(string given)
        {
            var v = TypeRename.Check(given, "Old", new string[0], out _);
            Assert.NotNull(v);
            Assert.Contains("cannot contain", v);
        }

        [Fact]
        public void A_name_a_sibling_already_holds_is_a_duplicate_even_with_other_case()
        {
            var v = TypeRename.Check("(TGc002) 1200 x 3500 a", "Old", new[] { "(tgc002) 1200 x 3500 A" }, out _);
            Assert.NotNull(v);
            Assert.Contains("already exists", v);
        }

        [Fact]
        public void The_name_the_type_already_has_is_unchanged_not_a_duplicate()
        {
            Assert.Equal("same", TypeRename.Check("Old", "Old", new[] { "Other" }, out _));
        }

        [Fact]
        public void A_case_only_change_of_its_own_name_is_allowed()
        {
            // siblings never include the type itself
            Assert.Null(TypeRename.Check("OLD", "Old", new[] { "Other" }, out var name));
            Assert.Equal("OLD", name);
        }

        [Theory]
        [InlineData("Da.1")]
        [InlineData("Dc.1")]
        public void The_confirmed_params_of_a_rename_family_are_what_the_writer_accepts(string id)
        {
            var facts = new CopilotPromptFacts
            {
                CheckId = id, Category = "Stairs",
                ElementIds = new long[] { 11, 12, 13 }.ToList(),
                ElementLabels = new long[] { 11, 12, 13 }.ToDictionary(i => i, i => i == 13 ? "Stairs · B" : "Stairs · A"),
            };
            var dto = JkrCopilotConfirmedWrite.ForPart(facts, 0)!;
            Assert.True(dto.TypeLevel);
            Assert.Equal(3, dto.Instances);
            Assert.Equal(new long[] { 11, 13 }, dto.Ids);                 // one instance id per type, as the prompt lists them
            Assert.All(dto.Params, p => Assert.True(TypeRename.IsRenameParam(p)));
            foreach (var bm in new[] { false, true })
            {
                var how = JkrCopilotPrompt.VariantFor(id).How(bm);
                Assert.Contains("\"Type Name\"", how);
                Assert.Contains("type_param true", how);
                Assert.DoesNotContain("run_revit_code", how);          // the prompt never steers a rename to code
            }
        }
    }
}
