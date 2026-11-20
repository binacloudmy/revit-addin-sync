#nullable enable
// ReferenceDumpFacts: the scale guess of a CAD import's transform and the conduit connector
// check of dump_electrical_reference. Invented values only.

using System.Collections.Generic;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace Tests
{
    public class ReferenceDumpFactsTests
    {
        [Fact]
        public void Scale_guess_is_unit_when_the_basis_has_length_one()
        {
            Assert.Equal("unit", ReferenceDumpFacts.ScaleGuess(1, 1, 1));
            Assert.Equal("unit", ReferenceDumpFacts.ScaleGuess(1 + 1e-9, 1 - 1e-9, 1));
        }

        [Fact]
        public void Scale_guess_is_the_length_when_the_scale_is_uniform()
        {
            // A drawing in metres carried into feet: 3.28084 per unit.
            Assert.Equal(3.280839895, ReferenceDumpFacts.ScaleGuess(3.280839895, 3.280839895, 3.280839895));
            Assert.Equal(0.001, ReferenceDumpFacts.ScaleGuess(0.001, 0.001, 0.001));
        }

        [Fact]
        public void Scale_guess_lists_the_lengths_when_they_differ()
        {
            var guess = Assert.IsType<List<object?>>(ReferenceDumpFacts.ScaleGuess(2, 2, 1));
            Assert.Equal(new List<object?> { 2.0, 2.0, 1.0 }, guess);
        }

        [Fact]
        public void Transform_facts_give_origin_in_mm_and_basis_lengths()
        {
            // Rotated 90 degrees about Z, no scale, origin 1 ft east and 2 ft north.
            Dictionary<string, object?> facts = ReferenceDumpFacts.TransformFacts(
                new[] { 1.0, 2.0, 0.0 }, new[] { 0.0, 1.0, 0.0 }, new[] { -1.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 1.0 });

            Assert.Equal(new List<object?> { 304.8, 609.6, 0.0 }, facts["origin_mm"]);
            Assert.Equal(1.0, facts["basis_length_x"]);
            Assert.Equal(1.0, facts["basis_length_y"]);
            Assert.Equal(1.0, facts["basis_length_z"]);
            Assert.Equal("unit", facts["scale_guess"]);

            Dictionary<string, object?> scaled = ReferenceDumpFacts.TransformFacts(
                new[] { 0.0, 0.0, 0.0 }, new[] { 0.6, 0.8, 0.0 }, new[] { -0.8, 0.6, 0.0 }, new[] { 0.0, 0.0, 1.0 });
            Assert.Equal("unit", scaled["scale_guess"]);
        }

        [Fact]
        public void Conduit_connector_is_the_cable_tray_conduit_domain()
        {
            Assert.True(ReferenceDumpFacts.HasConduitConnector(new[] { "DomainElectrical", "DomainCableTrayConduit" }));
            Assert.False(ReferenceDumpFacts.HasConduitConnector(new[] { "DomainElectrical" }));
            Assert.False(ReferenceDumpFacts.HasConduitConnector(new string[0]));
        }
    }
}
