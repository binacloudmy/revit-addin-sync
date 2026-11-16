// The name rule of the parameter pull (ClickUp 14zefhy2nny): Bina stores the name the user typed,
// Revit compares parameter names without regard to case. Comparing exactly would miss the model's
// own parameter, and the pull would bind a second shared parameter whose name already exists.
// Revit-free.

using RevitWebAppSync.Services;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class ParameterNameRulesTests
    {
        [Theory]
        [InlineData("Mark", "Mark", true)]
        [InlineData("Mark", "mark", true)]
        [InlineData("Mark", "MARK", true)]
        [InlineData("  Fire Rating  ", "fire rating", true)]
        [InlineData("Mark", "Marks", false)]
        [InlineData("Fire Rating", "Fire  Rating", false)] // a different name, not just spacing
        [InlineData("", "Mark", false)]
        [InlineData(null, "Mark", false)]
        [InlineData(null, null, true)] // both empty: the caller rejects blank names anyway
        public void Matches_is_trimmed_and_case_insensitive(string left, string right, bool expected) =>
            Assert.Equal(expected, ParameterNameRules.Matches(left, right));

        [Theory]
        [InlineData("Mark", "Mark")]
        [InlineData("  Mark  ", "Mark")]
        [InlineData(null, "")]
        public void Normalize_trims_and_never_returns_null(string input, string expected) =>
            Assert.Equal(expected, ParameterNameRules.Normalize(input));

        [Theory]
        [InlineData("Mark", true)]
        [InlineData("  Mark ", true)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void IsUsable_rejects_blank_names(string name, bool expected) =>
            Assert.Equal(expected, ParameterNameRules.IsUsable(name));

        // ---- which parameter a row writes to (Danial's review of #213) ----

        private static ParameterCandidate Inst(string name, bool readOnly = false) => new ParameterCandidate(name, readOnly, false);
        private static ParameterCandidate Type(string name, bool readOnly = false) => new ParameterCandidate(name, readOnly, true);

        private static string Picked(string wanted, params ParameterCandidate[] candidates)
        {
            var pick = ParameterNameRules.Pick(wanted, candidates);
            if (pick.Ambiguous) return "ambiguous: " + string.Join(", ", pick.AmbiguousNames);
            if (pick.Index is int i) return (candidates[i].IsOnType ? "type " : "instance ") + candidates[i].Name;
            return "none";
        }

        [Fact]
        public void A_types_exact_name_wins_over_an_instance_look_alike() =>
            // develop wrote to the type here; the first version of #213 wrote to the instance "fire rating".
            Assert.Equal("type Fire Rating", Picked("Fire Rating", Inst("fire rating"), Type("Fire Rating")));

        [Fact]
        public void The_exact_name_on_the_instance_wins_over_the_type() =>
            Assert.Equal("instance Mark", Picked("Mark", Type("Mark"), Inst("Mark")));

        [Fact]
        public void A_read_only_exact_instance_name_falls_to_a_writable_exact_type_name() =>
            Assert.Equal("type Fire Rating", Picked("Fire Rating", Inst("Fire Rating", readOnly: true), Type("Fire Rating")));

        [Fact]
        public void A_read_only_exact_name_is_reported_rather_than_writing_to_a_look_alike() =>
            Assert.Equal("instance Area", Picked("Area", Inst("Area", readOnly: true), Inst("area")));

        [Fact]
        public void Without_an_exact_name_the_case_insensitive_one_is_found() =>
            Assert.Equal("instance Mark", Picked(" mark ", Inst("Mark"), Inst("Comments")));

        [Fact]
        public void Two_writable_look_alikes_are_ambiguous_not_a_coin_toss() =>
            Assert.Equal("ambiguous: Mark, mark", Picked("MARK", Inst("Mark"), Inst("mark")));

        [Fact]
        public void Among_look_alikes_the_writable_one_is_kept() =>
            Assert.Equal("instance mark", Picked("MARK", Inst("Mark", readOnly: true), Inst("mark")));

        [Fact]
        public void A_writable_instance_look_alike_wins_over_a_type_one() =>
            Assert.Equal("instance mark", Picked("MARK", Type("Mark"), Inst("mark")));

        [Fact]
        public void The_choice_does_not_depend_on_the_order_Revit_lists_parameters_in()
        {
            var a = new[] { Inst("fire rating"), Type("Fire Rating"), Inst("Comments") };
            var b = new[] { Inst("Comments"), Type("Fire Rating"), Inst("fire rating") };
            Assert.Equal(Picked("Fire Rating", a), Picked("Fire Rating", b));
            Assert.Equal(Picked("FIRE RATING", Inst("Fire Rating"), Inst("fire rating")),
                         Picked("FIRE RATING", Inst("fire rating"), Inst("Fire Rating")));
        }

        [Fact]
        public void No_match_and_blank_names_pick_nothing()
        {
            Assert.Equal("none", Picked("Fire Rating", Inst("Mark")));
            Assert.Equal("none", Picked("  ", Inst("Mark")));
            Assert.Equal("none", Picked("Mark"));
        }

        // ---- which shared parameter definition a row binds ----

        private static string PickedDefinition(string wanted, params DefinitionCandidate[] candidates)
        {
            var pick = ParameterNameRules.PickDefinition(wanted, candidates);
            if (pick.Ambiguous) return "ambiguous: " + string.Join(", ", pick.AmbiguousNames);
            return pick.Index is int i ? candidates[i].Name + (candidates[i].IsBound ? " (bound)" : "") : "none";
        }

        [Theory]
        [InlineData("fire rating")]
        [InlineData(" Fire Rating ")]
        [InlineData("FIRE RATING")]
        public void A_row_finds_the_definition_already_bound_whatever_its_case(string row) =>
            // Danial's example: "Fire Rating" bound to Walls; a row on a Door must widen it, not create a second one.
            Assert.Equal("Fire Rating (bound)", PickedDefinition(row, new DefinitionCandidate("Fire Rating", true)));

        [Fact]
        public void The_bound_definition_wins_over_an_unbound_exact_duplicate() =>
            Assert.Equal("Fire Rating (bound)",
                PickedDefinition("fire rating", new DefinitionCandidate("fire rating", false), new DefinitionCandidate("Fire Rating", true)));

        [Fact]
        public void Among_unbound_definitions_the_exact_name_wins() =>
            Assert.Equal("Mark", PickedDefinition("Mark", new DefinitionCandidate("mark", false), new DefinitionCandidate("Mark", false)));

        [Fact]
        public void Two_bound_look_alikes_are_ambiguous() =>
            Assert.Equal("ambiguous: Mark, mark",
                PickedDefinition("MARK", new DefinitionCandidate("Mark", true), new DefinitionCandidate("mark", true)));

        [Fact]
        public void No_definition_means_the_caller_creates_one_under_the_trimmed_name()
        {
            Assert.Equal("none", PickedDefinition(" Fire Rating ", new DefinitionCandidate("Mark", true)));
            Assert.Equal("Fire Rating", ParameterNameRules.Normalize(" Fire Rating "));
        }
    }
}
