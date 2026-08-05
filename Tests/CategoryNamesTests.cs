// CategoryNames — every decision behind the category resolver that
// filter_elements, list_family_types, find_elements_by_filter and count_by all
// share. (CategoryResolve itself is one Enum.TryParse loop over this, and
// cannot be tested here: the Revit API is a reference-only package, so a test
// type mentioning BuiltInCategory would make xUnit skip the whole assembly.)
//
// Two claims are pinned. First, "Electrical Circuits" must produce
// OST_ElectricalCircuit: Revit's UI shows the plural, the enum member is
// singular, and until the fallback existed filter_elements threw "unknown
// category" — leaving the agent no way at all to reach a circuit's element id
// (UAT 2026-08-04). Second, and more important, the NON-REGRESSION set: the
// singular fallback is LAST precisely so nothing that already resolved can
// reach it, and this is where that claim is checked instead of assumed.

using System.Linq;
using BinaVibe.Mcp.Tools;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class CategoryNamesTests
    {
        [Theory]
        [InlineData("Electrical Circuits")]
        [InlineData("electrical circuits")]
        [InlineData("Electrical Circuit")]
        [InlineData("circuits")]
        [InlineData("Power Circuits")]
        [InlineData("litar")]
        [InlineData("OST_ElectricalCircuit")]
        public void Every_way_a_circuit_gets_named_offers_the_singular_member(string input)
        {
            Assert.Contains("OST_ElectricalCircuit", CategoryNames.Candidates(input));
        }

        [Fact]
        public void The_plural_spelling_offers_the_singular_only_after_the_plural_fails()
        {
            // Order is the whole safety argument: OST_ElectricalCircuits is
            // tried first and simply does not exist, so nothing else changes.
            var c = CategoryNames.Candidates("Electrical Circuits");
            Assert.True(c.IndexOf("OST_ElectricalCircuits") < c.IndexOf("OST_ElectricalCircuit"));
        }

        [Theory]
        // The names that resolved BEFORE the aliases and the singular fallback
        // were added. Each must still offer the same member FIRST.
        [InlineData("Walls", "OST_Walls")]
        [InlineData("walls", "OST_Walls")]
        [InlineData("OST_Walls", "OST_Walls")]
        [InlineData("Doors", "OST_Doors")]
        [InlineData("Windows", "OST_Windows")]
        [InlineData("Floors", "OST_Floors")]
        [InlineData("Rooms", "OST_Rooms")]
        [InlineData("Levels", "OST_Levels")]
        [InlineData("Grids", "OST_Grids")]
        [InlineData("Plumbing Fixtures", "OST_PlumbingFixtures")]
        [InlineData("Electrical Fixtures", "OST_ElectricalFixtures")]
        [InlineData("Electrical Equipment", "OST_ElectricalEquipment")]
        [InlineData("Lighting Fixtures", "OST_LightingFixtures")]
        [InlineData("Structural Columns", "OST_StructuralColumns")]
        public void Names_that_already_worked_still_lead_with_the_same_member(
            string input, string expected)
        {
            // Case-insensitively: the resolver parses with ignoreCase, so
            // "walls" has always produced the candidate "OST_walls".
            Assert.Equal(expected, CategoryNames.Candidates(input).First(),
                         ignoreCase: true);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Blank_input_offers_nothing(string input)
        {
            // No candidates -> Resolve returns null -> the caller throws
            // "unknown category". Falling through to an unfiltered collector is
            // the behaviour this resolver exists to prevent.
            Assert.Empty(CategoryNames.Candidates(input));
        }

        [Fact]
        public void Junk_offers_only_names_that_do_not_exist()
        {
            // "tandas" produces OST_tandas and — because it ends in "s" — the
            // stripped OST_tanda. Neither is a BuiltInCategory, so Resolve
            // still returns null and the caller still fails loudly. This is
            // the residual risk of the fallback, bounded: it can only reach
            // names that already resolved to nothing.
            Assert.Equal(new[] { "OST_tandas", "OST_tanda" },
                         CategoryNames.Candidates("tandas"));
        }

        [Fact]
        public void The_singular_fallback_never_strips_a_short_word_to_nothing()
        {
            // "s" -> OST_s; stripping it would leave the bare prefix "OST_".
            Assert.DoesNotContain("OST_", CategoryNames.Candidates("s"));
        }
    }
}
