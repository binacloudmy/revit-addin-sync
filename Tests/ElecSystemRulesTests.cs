// ElecSystemRules — the Revit-free half of create_distribution_system and the
// phase/wire guard on set_connector_electrical_data.
//
// Two things are pinned here and nowhere else. First, the phase/wire table:
// writing a pair Revit cannot serve reloads a family, changes every instance,
// and still matches no distribution system, so the refusal has to happen before
// any of that. Second, the Malaysian defaults — a JKR project circuited against
// a US 120/208 template yields wrong breaker, cable and voltage-drop numbers
// throughout, which is the failure these values exist to prevent (UAT
// 2026-08-04).

using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class ElecSystemRulesPhaseWireTests
    {
        [Theory]
        [InlineData(1, 2)]   // line + neutral
        [InlineData(1, 3)]   // split phase
        [InlineData(3, 3)]   // delta, no neutral
        [InlineData(3, 4)]   // wye with neutral
        public void Accepts_the_pairs_Revit_can_serve(int phases, int wires)
        {
            Assert.Null(ElecSystemRules.ValidatePhaseWire(phases, wires));
        }

        [Theory]
        [InlineData(1, 4)]
        [InlineData(3, 2)]
        public void Rejects_a_mismatched_pair(int phases, int wires)
        {
            var err = ElecSystemRules.ValidatePhaseWire(phases, wires);
            Assert.NotNull(err);
            Assert.Contains("cannot have", err);
        }

        [Fact]
        public void Rejects_a_phase_count_Revit_has_no_concept_of()
        {
            var err = ElecSystemRules.ValidatePhaseWire(2, 3);
            Assert.NotNull(err);
            Assert.Contains("must be 1 or 3", err);
        }

        [Fact]
        public void Rejects_an_out_of_range_wire_count()
        {
            var err = ElecSystemRules.ValidatePhaseWire(null, 5);
            Assert.NotNull(err);
            Assert.Contains("must be 2, 3 or 4", err);
        }

        // The connector tool passes whatever subset the caller sent. Half a pair
        // is not enough to judge, and must not block a voltage-only repair.
        [Theory]
        [InlineData(null, null)]
        [InlineData(3, null)]
        [InlineData(null, 4)]
        public void Passes_when_the_pair_is_incomplete(int? phases, int? wires)
        {
            Assert.Null(ElecSystemRules.ValidatePhaseWire(phases, wires));
        }

        [Fact]
        public void Default_wires_follow_the_config()
        {
            Assert.Equal(4, ElecSystemRules.DefaultWires(3, "wye"));
            Assert.Equal(3, ElecSystemRules.DefaultWires(3, "delta"));
            Assert.Equal(2, ElecSystemRules.DefaultWires(1, "undefined"));
        }
    }

    public class ElecSystemRulesDefaultsTests
    {
        [Fact]
        public void Three_phase_default_is_415_over_240_wye_four_wire()
        {
            var s = ElecSystemRules.MalaysianThreePhase();
            Assert.Equal("415/240 Wye", s.Name);
            Assert.Equal(3, s.Phases);
            Assert.Equal("wye", s.PhaseConfig);
            Assert.Equal(4, s.Wires);
            Assert.Equal(240.0, s.VoltageLineToGroundV);
            Assert.Equal(415.0, s.VoltageLineToLineV);
        }

        [Fact]
        public void Single_phase_default_is_240_two_wire_with_no_line_to_line()
        {
            var s = ElecSystemRules.MalaysianSinglePhase();
            Assert.Equal("240 V Single", s.Name);
            Assert.Equal(1, s.Phases);
            Assert.Equal(2, s.Wires);
            Assert.Equal(240.0, s.VoltageLineToGroundV);
            // A single-phase system has no line-to-line voltage. Passing one
            // anyway is how a 1-phase system ends up unassignable.
            Assert.Null(s.VoltageLineToLineV);
        }

        [Fact]
        public void Both_defaults_pass_their_own_phase_wire_check()
        {
            foreach (var s in new[] {
                         ElecSystemRules.MalaysianThreePhase(),
                         ElecSystemRules.MalaysianSinglePhase() })
                Assert.Null(ElecSystemRules.ValidatePhaseWire(s.Phases, s.Wires));
        }
    }

    public class ElecSystemRulesVoltageTests
    {
        [Fact]
        public void Malaysian_voltages_get_their_drawing_bands()
        {
            Assert.Equal((220.0, 250.0), ElecSystemRules.VoltageBand(240));
            Assert.Equal((400.0, 430.0), ElecSystemRules.VoltageBand(415));
        }

        [Fact]
        public void Other_voltages_get_a_symmetric_five_percent()
        {
            var band = ElecSystemRules.VoltageBand(120);
            Assert.Equal(114.0, band.Min);
            Assert.Equal(126.0, band.Max);
        }

        // The bands must not overlap, or which definition a connector binds to
        // is ambiguous and the panel match becomes luck.
        [Fact]
        public void The_two_malaysian_bands_do_not_overlap()
        {
            var lg = ElecSystemRules.VoltageBand(ElecSystemRules.MalaysianLineToGroundV);
            var ll = ElecSystemRules.VoltageBand(ElecSystemRules.MalaysianLineToLineV);
            Assert.True(lg.Max < ll.Min);
        }

        [Fact]
        public void Near_matches_within_a_volt_so_existing_definitions_are_reused()
        {
            Assert.True(ElecSystemRules.Near(240.0, 240.4));
            Assert.False(ElecSystemRules.Near(240.0, 415.0));
        }

        [Fact]
        public void Voltage_names_follow_the_template_convention()
        {
            Assert.Equal("240 V", ElecSystemRules.VoltageName(240));
            Assert.Equal("415 V", ElecSystemRules.VoltageName(415));
        }
    }

    public class ElecSystemRulesPhaseConfigTests
    {
        [Theory]
        [InlineData("wye", "wye")]
        [InlineData("Wye", "wye")]
        [InlineData("STAR", "wye")]
        [InlineData("delta", "delta")]
        [InlineData(" Undefined ", "undefined")]
        public void Normalises_the_spellings_an_agent_emits(string raw, string expected)
        {
            Assert.Equal(expected, ElecSystemRules.NormalisePhaseConfig(raw));
        }

        // Null, not a silent fallback: an unrecognised config must be echoed
        // back to the caller, not quietly turned into Undefined.
        [Theory]
        [InlineData("triangle")]
        [InlineData("")]
        [InlineData(null)]
        public void Returns_null_on_anything_unrecognised(string? raw)
        {
            Assert.Null(ElecSystemRules.NormalisePhaseConfig(raw));
        }
    }
}
