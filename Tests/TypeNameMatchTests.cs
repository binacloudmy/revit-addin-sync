#nullable enable
// AI-328: a bulk type change through set_parameters ("Type" = "<name>") must
// match the target type among the types the element can take (its category),
// never across the document, and refuse ambiguity instead of guessing.
// Revit-free: RevitParamValues.ResolveType builds the candidates from
// Element.GetValidTypes() and hands them to TypeNameMatch.Pick.

using System.Linq;
using BinaVibe.Copilot.Writes;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class TypeNameMatchTests
    {
        // Door types the door can take: JKR families all name their one type "Normal".
        private static readonly TypeCandidate[] Doors =
        {
            new TypeCandidate(101, "0915 x 2134mm", "Single-Flush"),
            new TypeCandidate(102, "1830 x 2134mm", "Double-Flush"),
            new TypeCandidate(103, "Normal", "JKR_D1"),
            new TypeCandidate(104, "Normal", "JKR_D2"),
            new TypeCandidate(105, "Standard", "Sliding"),
        };

        [Fact]
        public void Unique_type_name_is_found()
        {
            var m = TypeNameMatch.Pick("1830 x 2134mm", Doors);
            Assert.Equal(TypeMatchKind.Found, m.Kind);
            Assert.Equal(102, m.Id);
        }

        [Fact]
        public void Name_is_matched_case_insensitively_and_trimmed()
        {
            var m = TypeNameMatch.Pick("  standard ", Doors);
            Assert.Equal(TypeMatchKind.Found, m.Kind);
            Assert.Equal(105, m.Id);
        }

        [Fact]
        public void Name_shared_by_two_families_is_ambiguous_and_lists_both()
        {
            var m = TypeNameMatch.Pick("Normal", Doors);
            Assert.Equal(TypeMatchKind.Ambiguous, m.Kind);
            Assert.Equal(new[] { "JKR_D1: Normal", "JKR_D2: Normal" }, m.Labels.ToArray());
        }

        [Theory]
        [InlineData("JKR_D2: Normal", 104)]
        [InlineData("jkr_d2:Normal", 104)]
        [InlineData("Single-Flush: 0915 x 2134mm", 101)]
        public void Family_and_type_form_resolves_the_ambiguity(string given, long id)
        {
            var m = TypeNameMatch.Pick(given, Doors);
            Assert.Equal(TypeMatchKind.Found, m.Kind);
            Assert.Equal(id, m.Id);
        }

        [Fact]
        public void A_type_from_another_category_is_not_a_candidate()
        {
            // "Generic - 200mm" is a wall type; the candidates are the door's valid types only, so it is
            // not found (main resolved it across the whole document).
            var m = TypeNameMatch.Pick("Generic - 200mm", Doors);
            Assert.Equal(TypeMatchKind.NotFound, m.Kind);
        }

        [Fact]
        public void Not_found_offers_similar_names_from_the_category()
        {
            var m = TypeNameMatch.Pick("2134mm", Doors);
            Assert.Equal(TypeMatchKind.NotFound, m.Kind);
            Assert.Contains("Double-Flush: 1830 x 2134mm", m.Labels);
            Assert.Contains("Single-Flush: 0915 x 2134mm", m.Labels);
        }

        [Fact]
        public void Type_name_holding_a_colon_matches_whole()
        {
            var types = new[] { new TypeCandidate(1, "Type: A", "Fam"), new TypeCandidate(2, "A", "Type") };
            // The full "Family: Type" label is matched before splitting at the first colon, so a type
            // name that holds a colon still resolves.
            var m = TypeNameMatch.Pick("Fam: Type: A", types);
            Assert.Equal(TypeMatchKind.Found, m.Kind);
            Assert.Equal(1, m.Id);
        }

        [Fact]
        public void System_type_without_family_matches_by_name()
        {
            var walls = new[] { new TypeCandidate(7, "Generic - 200mm", null), new TypeCandidate(8, "Generic - 150mm", "") };
            var m = TypeNameMatch.Pick("Generic - 150mm", walls);
            Assert.Equal(TypeMatchKind.Found, m.Kind);
            Assert.Equal(8, m.Id);
            Assert.Equal("Generic - 150mm", walls[1].Label);
        }

        [Fact]
        public void Empty_name_is_not_found()
        {
            Assert.Equal(TypeMatchKind.NotFound, TypeNameMatch.Pick("  ", Doors).Kind);
        }

        [Theory]
        [InlineData("1830 x 2134mm")]
        [InlineData("Generic - 200mm")]
        [InlineData("JKR_D2: Normal")]
        [InlineData("Normal")]
        public void Type_names_reach_the_matcher_as_names(string raw)
        {
            // set_parameters parses the value against the parameter kind first: an ElementId parameter
            // must keep these as names, not numbers.
            var v = ValueParser.Parse(raw, ValueKind.ElementId, null);
            Assert.True(v.Ok, v.Error);
            Assert.StartsWith("elementname|", v.Canonical);
            Assert.Equal(raw, v.Text);
        }
    }
}
