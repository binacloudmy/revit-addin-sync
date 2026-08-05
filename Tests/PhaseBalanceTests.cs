// PhaseBalance — panel choice and phase proposal, the other half of
// suggest_circuits' decision core (grouping is in CircuitGroupingTests.cs).
//
// The proposed phase is a PROPOSAL: Revit hands out real slots at commit time
// and CircuitCommit reads the actual slot back. What is pinned here is the
// decision — capacity arithmetic, the unknown-mains fallback, and the exact
// infeasible reason CircuitCommit gates its allow_infeasible refusal on.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class PhaseBalanceTests
    {
        private static CircuitGroup Circuit(int index, double va)
            => new() { Index = index, TotalVa = va, LoadClass = "receptacle" };

        // ─── argument guards ───

        [Fact]
        public void Assign_refuses_an_empty_panel_list()
        {
            var ex = Assert.Throws<ArgumentException>(
                () => PhaseBalance.Assign(new[] { Circuit(0, 250) }, new List<PanelInfo>(), 230));
            Assert.Contains("at least one panel required", ex.Message);
        }

        [Fact]
        public void Assign_refuses_a_null_panel_list()
        {
            Assert.Throws<ArgumentException>(
                () => PhaseBalance.Assign(new[] { Circuit(0, 250) }, null, 230));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-230)]
        public void Assign_refuses_a_non_positive_voltage(double voltageV)
        {
            var panels = new List<PanelInfo> { new() { Id = 10, Phases = 1, MainsA = 63 } };
            var ex = Assert.Throws<ArgumentException>(
                () => PhaseBalance.Assign(new[] { Circuit(0, 250) }, panels, voltageV));
            Assert.Contains("voltage_v must be > 0", ex.Message);
        }

        [Fact]
        public void No_circuits_yields_no_assignments()
        {
            var panels = new List<PanelInfo> { new() { Id = 10, Phases = 1, MainsA = 63 } };
            Assert.Empty(PhaseBalance.Assign(new CircuitGroup[0], panels, 230));
        }

        // ─── capacity arithmetic ───

        [Fact]
        public void SpareVa_is_null_when_the_mains_rating_is_unset()
        {
            Assert.Null(PhaseBalance.SpareVa(new PanelInfo { Id = 10, MainsA = null }, 0, 230));
        }

        [Fact]
        public void SpareVa_on_three_phase_counts_the_mains_rating_on_every_leg()
        {
            var three = new PanelInfo { Id = 10, Phases = 3, MainsA = 100 };
            var one = new PanelInfo { Id = 11, Phases = 1, MainsA = 100 };

            Assert.Equal(100 * 230 * 3, PhaseBalance.SpareVa(three, 0, 230));
            Assert.Equal(100 * 230, PhaseBalance.SpareVa(one, 0, 230));
        }

        [Fact]
        public void SpareVa_subtracts_existing_and_newly_assigned_load()
        {
            var p = new PanelInfo { Id = 10, Phases = 1, MainsA = 100, ConnectedVa = 3000 };
            Assert.Equal(100 * 230 - 3000 - 500, PhaseBalance.SpareVa(p, 500, 230));
        }

        // ─── panel choice ───

        [Fact]
        public void Panel_with_most_spare_capacity_wins()
        {
            var panels = new List<PanelInfo>
            {
                new() { Id = 10, Phases = 1, MainsA = 63, ConnectedVa = 13000 },
                new() { Id = 20, Phases = 1, MainsA = 63, ConnectedVa = 0 },
            };
            var res = PhaseBalance.Assign(new[] { Circuit(0, 1000) }, panels, 230);

            Assert.Equal(20, res.Single().PanelId);
        }

        [Fact]
        public void Circuits_are_placed_largest_first_so_the_big_loads_get_the_room()
        {
            // Only panel 20 can hold the 20 kVA circuit. Taken in index order the
            // two 6 kVA circuits would land on panel 20 first (it has the most
            // spare), leaving 11000 VA — and the big one would go infeasible.
            var panels = new List<PanelInfo>
            {
                new() { Id = 10, Phases = 1, MainsA = 60 },    // 13800 VA
                new() { Id = 20, Phases = 1, MainsA = 100 },   // 23000 VA
            };
            var res = PhaseBalance.Assign(
                new[] { Circuit(0, 6000), Circuit(1, 6000), Circuit(2, 20000) }, panels, 230);

            Assert.Equal(20, res.Single(a => a.CircuitIndex == 2).PanelId);
            Assert.All(res, a => Assert.True(a.Feasible));
        }

        [Fact]
        public void Overflow_is_reported_infeasible_not_silently_assigned()
        {
            var panels = new List<PanelInfo>
            {
                new() { Id = 10, Phases = 1, MainsA = 6, ConnectedVa = 1000 },  // 1380 VA cap
            };
            var res = PhaseBalance.Assign(new[] { Circuit(0, 2000) }, panels, 230);

            var a = res.Single();
            Assert.False(a.Feasible);
            Assert.Contains("no panel has spare capacity", a.Reason);
            Assert.Equal(10, a.PanelId);   // still lands somewhere for reporting
        }

        [Fact]
        public void The_infeasible_reason_names_the_rounded_load()
        {
            // CircuitCommit surfaces this string verbatim when it refuses a plan
            // holding infeasible circuits, so the shape is part of the contract.
            var panels = new List<PanelInfo> { new() { Id = 10, Phases = 1, MainsA = 6 } };
            var a = PhaseBalance.Assign(new[] { Circuit(0, 20000.4) }, panels, 230).Single();

            Assert.Equal("no panel has spare capacity for 20000 VA", a.Reason);
        }

        [Fact]
        public void Unknown_mains_rating_is_flagged_not_guessed()
        {
            var panels = new List<PanelInfo>
            {
                new() { Id = 10, Phases = 1, MainsA = 6, ConnectedVa = 1300 },
                new() { Id = 20, Phases = 1, MainsA = null },
            };
            var res = PhaseBalance.Assign(new[] { Circuit(0, 2000) }, panels, 230);

            var a = res.Single();
            Assert.Equal(20, a.PanelId);
            Assert.True(a.Feasible);
            Assert.Contains("capacity unknown", a.Reason);
        }

        [Fact]
        public void An_unknown_mains_panel_is_the_fallback_only_never_the_first_choice()
        {
            var panels = new List<PanelInfo>
            {
                new() { Id = 10, Phases = 1, MainsA = null },
                new() { Id = 20, Phases = 1, MainsA = 100 },
            };
            var a = PhaseBalance.Assign(new[] { Circuit(0, 6000) }, panels, 230).Single();

            Assert.Equal(20, a.PanelId);
            Assert.True(string.IsNullOrEmpty(a.Reason));
        }

        // ─── phase choice ───

        [Fact]
        public void Single_phase_panel_always_proposes_phase_zero()
        {
            var panels = new List<PanelInfo> { new() { Id = 10, Phases = 1, MainsA = 63 } };
            var res = PhaseBalance.Assign(new[] { Circuit(0, 1000), Circuit(1, 500) }, panels, 230);

            Assert.All(res, a => Assert.Equal(0, a.ProposedPhase));
            Assert.All(res, a => Assert.True(a.Feasible));
        }

        [Fact]
        public void Three_phase_panel_spreads_circuits_across_lightest_phases()
        {
            var panels = new List<PanelInfo>
            {
                new() { Id = 10, Phases = 3, MainsA = 63, PhaseVa = new double[3] },
            };
            var res = PhaseBalance.Assign(
                new[] { Circuit(0, 1000), Circuit(1, 1000), Circuit(2, 1000) }, panels, 230);

            Assert.Equal(new[] { 0, 1, 2 }, res.Select(a => a.ProposedPhase).OrderBy(p => p));
        }

        [Fact]
        public void Existing_per_phase_load_decides_which_leg_is_lightest()
        {
            var panels = new List<PanelInfo>
            {
                new() { Id = 10, Phases = 3, MainsA = 63, PhaseVa = new double[] { 4000, 1000, 4000 } },
            };
            var a = PhaseBalance.Assign(new[] { Circuit(0, 500) }, panels, 230).Single();

            Assert.Equal(1, a.ProposedPhase);
        }

        [Fact]
        public void A_tie_between_phases_settles_on_the_lowest_index()
        {
            // The 1e-9 epsilon: a later phase must be STRICTLY lighter to win, so
            // an exact tie never bounces the proposal between equal legs.
            var panels = new List<PanelInfo>
            {
                new() { Id = 10, Phases = 3, MainsA = 63, PhaseVa = new double[] { 2000, 2000, 2000 } },
            };
            var a = PhaseBalance.Assign(new[] { Circuit(0, 500) }, panels, 230).Single();

            Assert.Equal(0, a.ProposedPhase);
        }

        [Fact]
        public void A_short_PhaseVa_array_reads_the_missing_legs_as_empty()
        {
            // The Revit-side collection can report fewer legs than the panel
            // claims; the missing ones must not be treated as absent phases.
            var panels = new List<PanelInfo>
            {
                new() { Id = 10, Phases = 3, MainsA = 63, PhaseVa = new double[] { 5000 } },
            };
            var a = PhaseBalance.Assign(new[] { Circuit(0, 500) }, panels, 230).Single();

            Assert.Equal(1, a.ProposedPhase);
        }

        // ─── output order ───

        [Fact]
        public void Results_come_back_in_circuit_index_order()
        {
            var panels = new List<PanelInfo> { new() { Id = 10, Phases = 1, MainsA = 63 } };
            var res = PhaseBalance.Assign(
                new[] { Circuit(0, 100), Circuit(1, 9000), Circuit(2, 500) }, panels, 230);

            Assert.Equal(new[] { 0, 1, 2 }, res.Select(a => a.CircuitIndex));
        }
    }
}
