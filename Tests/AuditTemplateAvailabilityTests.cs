// AuditTemplateAvailability — a "no View Template" finding is only actionable
// when a template of that ViewType exists in the model. These pin the
// partition and the verdict against the live-model case that motivated it
// (jkrAR24: 86 templates, none of type Legend/DraftingView; E1.1 flagged 3
// such views, C1.1 flagged 50 of which 21 were Legend/DraftingView).
//
// The Revit side (collecting templates per ViewType from a Document) cannot
// run off Windows; only the string/enum-name partition is covered here.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Audit;
using Xunit;

namespace Tests
{
    public class AuditTemplateAvailabilityTests
    {
        // Live model: templates exist only for these types.
        private static readonly HashSet<string> LiveTypes = new()
        {
            "CeilingPlan", "Elevation", "FloorPlan", "Section", "ThreeD",
        };

        private static UntemplatedView V(long id, string name, string type) =>
            new() { Id = id, Name = name, ViewType = type };

        [Fact]
        public void E1_1_bomba_three_flagged_all_unactionable()
        {
            var without = new[]
            {
                V(1, "Petunjuk Bomba", "DraftingView"),
                V(2, "jkrAR_lgd_Kehendak Bomba", "Legend"),
                V(3, "jkrAR_lgd_Petunjuk Bomba", "Legend"),
            };
            var split = AuditTemplateAvailability.Split(without, LiveTypes);

            Assert.Empty(split.Actionable);
            Assert.Equal(3, split.Unactionable.Count);
            Assert.Equal("not_verifiable", AuditTemplateAvailability.Compliance(split));
            Assert.Equal("DraftingView ×1, Legend ×2", split.UnactionableTypesText);
        }

        [Fact]
        public void C1_1_fifty_flagged_splits_29_actionable_21_unactionable()
        {
            var without = new List<UntemplatedView>();
            for (int i = 0; i < 29; i++) without.Add(V(100 + i, $"Plan {i}", "FloorPlan"));
            for (int i = 0; i < 13; i++) without.Add(V(200 + i, $"Legend {i}", "Legend"));
            for (int i = 0; i < 8; i++) without.Add(V(300 + i, $"Drafting {i}", "DraftingView"));

            var split = AuditTemplateAvailability.Split(without, LiveTypes);

            Assert.Equal(29, split.Actionable.Count);
            Assert.Equal(21, split.Unactionable.Count);
            Assert.Equal("no", AuditTemplateAvailability.Compliance(split));
            Assert.Equal(
                new[] { ("Legend", 13), ("DraftingView", 8) },
                split.UnactionableByType.Select(kv => (kv.Key, kv.Value)).ToArray());

            var clause = AuditTemplateAvailability.ActionabilityClause(split);
            Assert.Contains("29 boleh tindakan", clause);
            Assert.Contains("21 tiada template jenis tersebut wujud", clause);
            Assert.Contains("Legend ×13, DraftingView ×8", clause);
        }

        [Fact]
        public void All_actionable_when_templates_exist_for_every_type()
        {
            var without = new[] { V(1, "L1", "FloorPlan"), V(2, "S1", "Section") };
            var split = AuditTemplateAvailability.Split(without, LiveTypes);

            Assert.Equal(2, split.Actionable.Count);
            Assert.Empty(split.Unactionable);
            Assert.Equal("no", AuditTemplateAvailability.Compliance(split));
            Assert.Equal("", AuditTemplateAvailability.ActionabilityClause(split));
        }

        [Fact]
        public void No_offenders_is_yes()
        {
            var split = AuditTemplateAvailability.Split(System.Array.Empty<UntemplatedView>(), LiveTypes);
            Assert.Equal("yes", AuditTemplateAvailability.Compliance(split));
        }

        [Fact]
        public void Model_with_zero_templates_makes_everything_unactionable()
        {
            var without = new[] { V(1, "L1", "FloorPlan") };
            var split = AuditTemplateAvailability.Split(without, new HashSet<string>());
            Assert.Empty(split.Actionable);
            Assert.Single(split.Unactionable);
            Assert.Equal("not_verifiable", AuditTemplateAvailability.Compliance(split));
        }
    }
}
