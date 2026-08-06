// Panel phase-balance and slot-fit math. Testable because PanelLoad.cs is
// VA-and-slot-numbers only — the PanelScheduleView half lives in PanelTools.cs
// and is not linked here.
//
// The numbers that matter: which phase a slot belongs to (Revit's panelboard
// numbering runs 1,2 across the first row, not down a column), that a
// multi-pole breaker skips a slot between poles, and that a proposed
// rebalance never makes the spread worse.
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class PanelLoadTests
    {
        private const double Eps = 1e-6;

        private static PanelSpec ThreePhase(int slots = 42) => new() { TotalSlots = slots, PhaseCount = 3 };
        private static PanelSpec SinglePhase(int slots = 24) => new() { TotalSlots = slots, PhaseCount = 1 };

        private static CircuitLoad C(long id, int slot, double va, int poles = 1, bool locked = false) =>
            new() { Id = id, Name = $"C{id}", StartSlot = slot, Poles = poles, LoadVa = va, Locked = locked };

        // ─── PhaseOfSlot ────────────────────────────────────────────────

        [Theory]
        [InlineData(1, 0)] [InlineData(2, 0)]
        [InlineData(3, 1)] [InlineData(4, 1)]
        [InlineData(5, 2)] [InlineData(6, 2)]
        [InlineData(7, 0)] [InlineData(8, 0)]
        public void PhaseOfSlot_follows_revit_panelboard_row_numbering(int slot, int expected)
        {
            Assert.Equal(expected, PanelLoad.PhaseOfSlot(slot, 3));
        }

        [Fact]
        public void PhaseOfSlot_collapses_to_one_phase_on_a_single_phase_board()
        {
            Assert.Equal(0, PanelLoad.PhaseOfSlot(1, 1));
            Assert.Equal(0, PanelLoad.PhaseOfSlot(5, 1));
        }

        [Fact]
        public void PhaseOfSlot_rejects_an_unassigned_slot()
        {
            Assert.Equal(-1, PanelLoad.PhaseOfSlot(0, 3));
        }

        // ─── SlotsFor / CanPlace ────────────────────────────────────────

        [Fact]
        public void SlotsFor_skips_a_slot_between_poles()
        {
            Assert.Equal(new[] { 3 }, PanelLoad.SlotsFor(3, 1));
            Assert.Equal(new[] { 3, 5 }, PanelLoad.SlotsFor(3, 2));
            Assert.Equal(new[] { 1, 3, 5 }, PanelLoad.SlotsFor(1, 3));
        }

        [Fact]
        public void A_three_pole_breaker_straddles_all_three_phases()
        {
            var phases = PanelLoad.SlotsFor(1, 3).Select(s => PanelLoad.PhaseOfSlot(s, 3)).ToList();
            Assert.Equal(new[] { 0, 1, 2 }, phases);
        }

        [Fact]
        public void CanPlace_refuses_a_breaker_that_runs_off_the_end_of_the_board()
        {
            var spec = ThreePhase(slots: 6);
            var occupied = PanelLoad.OccupiedSlots(new[] { C(1, 1, 100) });
            Assert.True(PanelLoad.CanPlace(2, 3, occupied, spec));    // 2,4,6 — last pole lands on the last slot
            Assert.False(PanelLoad.CanPlace(4, 3, occupied, spec));   // 4,6,8 — 8 is past the end
        }

        [Fact]
        public void CanPlace_refuses_an_occupied_slot()
        {
            var spec = ThreePhase();
            var occupied = PanelLoad.OccupiedSlots(new[] { C(1, 5, 100) });
            Assert.False(PanelLoad.CanPlace(5, 1, occupied, spec));
            Assert.True(PanelLoad.CanPlace(6, 1, occupied, spec));
        }

        [Fact]
        public void OccupiedSlots_can_ignore_the_circuit_being_moved()
        {
            var circuits = new[] { C(1, 5, 100), C(2, 7, 100) };
            Assert.Contains(5, PanelLoad.OccupiedSlots(circuits));
            Assert.DoesNotContain(5, PanelLoad.OccupiedSlots(circuits, ignoreId: 1));
        }

        [Fact]
        public void FreeStarts_leaves_out_starts_that_collide_on_the_second_pole()
        {
            var spec = ThreePhase(slots: 8);
            var occupied = PanelLoad.OccupiedSlots(new[] { C(1, 3, 100) });
            var starts = PanelLoad.FreeStarts(2, occupied, spec);
            Assert.DoesNotContain(1, starts);   // 1,3 — 3 is taken
            Assert.DoesNotContain(3, starts);
            Assert.Contains(2, starts);         // 2,4
        }

        // ─── PhaseLoads / ImbalancePct ──────────────────────────────────

        [Fact]
        public void PhaseLoads_bins_single_pole_circuits_by_slot()
        {
            var loads = PanelLoad.PhaseLoads(new[] { C(1, 1, 1000), C(2, 3, 500), C(3, 5, 250) }, ThreePhase());
            Assert.Equal(1000, loads[0], 6);
            Assert.Equal(500, loads[1], 6);
            Assert.Equal(250, loads[2], 6);
        }

        [Fact]
        public void PhaseLoads_splits_a_multi_pole_circuit_evenly_across_the_phases_it_touches()
        {
            var loads = PanelLoad.PhaseLoads(new[] { C(1, 1, 900, poles: 3) }, ThreePhase());
            Assert.Equal(300, loads[0], 6);
            Assert.Equal(300, loads[1], 6);
            Assert.Equal(300, loads[2], 6);
        }

        [Fact]
        public void PhaseLoads_ignores_an_unassigned_circuit()
        {
            var loads = PanelLoad.PhaseLoads(new[] { C(1, 0, 5000) }, ThreePhase());
            Assert.All(loads, v => Assert.Equal(0, v, 6));
        }

        [Fact]
        public void ImbalancePct_is_zero_for_an_even_board_and_for_an_empty_one()
        {
            Assert.Equal(0, PanelLoad.ImbalancePct(new[] { 500.0, 500.0, 500.0 }), 6);
            Assert.Equal(0, PanelLoad.ImbalancePct(new[] { 0.0, 0.0, 0.0 }), 6);
        }

        [Fact]
        public void ImbalancePct_is_measured_against_the_heaviest_phase()
        {
            Assert.Equal(50.0, PanelLoad.ImbalancePct(new[] { 1000.0, 500.0, 1000.0 }), 6);
        }

        // ─── Plan ───────────────────────────────────────────────────────

        [Fact]
        public void Plan_moves_load_off_the_heaviest_phase()
        {
            // Everything on phase A; B and C empty.
            var circuits = new[] { C(1, 1, 1000), C(2, 2, 1000), C(3, 7, 1000) };
            var plan = PanelLoad.Plan(circuits, ThreePhase());

            Assert.NotEmpty(plan.Moves);
            Assert.Equal(100.0, plan.BeforeImbalancePct, 6);
            Assert.True(plan.AfterImbalancePct < plan.BeforeImbalancePct);
            Assert.All(plan.Moves, m => Assert.NotEqual(m.FromPhase, m.ToPhase));
        }

        [Fact]
        public void Plan_pushes_past_the_percentage_plateau_to_a_fully_even_board()
        {
            // THE regression this guards: 3000/0/0 and 2000/1000/0 both read as
            // 100% imbalance, so a greedy step that optimises the REPORTED
            // percentage stops after zero moves and calls the board balanced.
            // The search minimises dispersion instead, which keeps falling.
            var circuits = new[] { C(1, 1, 1000), C(2, 2, 1000), C(3, 7, 1000) };
            var plan = PanelLoad.Plan(circuits, ThreePhase());

            Assert.Equal(0.0, plan.AfterImbalancePct, 6);
            Assert.Equal(2, plan.Moves.Count);
            Assert.All(plan.AfterVa, v => Assert.Equal(1000.0, v, 6));
        }

        [Fact]
        public void Dispersion_falls_on_a_move_the_percentage_cannot_see()
        {
            Assert.Equal(100.0, PanelLoad.ImbalancePct(new[] { 3000.0, 0.0, 0.0 }), 6);
            Assert.Equal(100.0, PanelLoad.ImbalancePct(new[] { 2000.0, 1000.0, 0.0 }), 6);
            Assert.True(PanelLoad.Dispersion(new[] { 2000.0, 1000.0, 0.0 })
                      < PanelLoad.Dispersion(new[] { 3000.0, 0.0, 0.0 }));
        }

        [Fact]
        public void Plan_never_makes_the_spread_worse()
        {
            var circuits = new[] { C(1, 1, 700), C(2, 3, 300), C(3, 5, 1200), C(4, 8, 450) };
            var plan = PanelLoad.Plan(circuits, ThreePhase());
            Assert.True(plan.AfterImbalancePct <= plan.BeforeImbalancePct + Eps);
        }

        [Fact]
        public void Plan_says_so_when_nothing_can_improve()
        {
            var circuits = new[] { C(1, 1, 500), C(2, 3, 500), C(3, 5, 500) };
            var plan = PanelLoad.Plan(circuits, ThreePhase());
            Assert.Empty(plan.Moves);
            Assert.Equal("already as balanced as slot moves can make it", plan.Note);
        }

        [Fact]
        public void Plan_refuses_to_pretend_a_single_phase_board_can_be_balanced()
        {
            var plan = PanelLoad.Plan(new[] { C(1, 1, 1000), C(2, 3, 10) }, SinglePhase());
            Assert.Empty(plan.Moves);
            Assert.Equal("single-phase board — there is nothing to balance across", plan.Note);
            Assert.Equal(0, plan.AfterImbalancePct, 6);
        }

        [Fact]
        public void Plan_skips_locked_multipole_and_unassigned_circuits_and_says_why()
        {
            var circuits = new[]
            {
                C(1, 1, 1000, locked: true),
                C(2, 3, 1000, poles: 2),
                C(3, 0, 1000),
            };
            var plan = PanelLoad.Plan(circuits, ThreePhase());

            Assert.Empty(plan.Moves);
            Assert.Equal(3, plan.Skipped.Count);
            Assert.Contains(plan.Skipped, s => s.Id == 1 && s.Reason.Contains("locked"));
            Assert.Contains(plan.Skipped, s => s.Id == 2 && s.Reason.Contains("2-pole"));
            Assert.Contains(plan.Skipped, s => s.Id == 3 && s.Reason.Contains("not assigned"));
            Assert.Equal("no movable 1-pole circuits on this panel", plan.Note);
        }

        [Fact]
        public void Plan_leaves_the_input_circuits_untouched()
        {
            var circuits = new[] { C(1, 1, 1000), C(2, 2, 1000), C(3, 7, 1000) };
            PanelLoad.Plan(circuits, ThreePhase());
            Assert.Equal(new[] { 1, 2, 7 }, circuits.Select(c => c.StartSlot));
        }

        [Fact]
        public void Plan_moves_never_collide_with_each_other()
        {
            var circuits = new[] { C(1, 1, 900), C(2, 2, 800), C(3, 7, 700), C(4, 8, 600) };
            var spec = ThreePhase(slots: 12);
            var plan = PanelLoad.Plan(circuits, spec);

            var final = circuits.ToDictionary(c => c.Id, c => c.StartSlot);
            foreach (var m in plan.Moves) final[m.CircuitId] = m.ToSlot;
            Assert.Equal(final.Count, final.Values.Distinct().Count());
            Assert.All(final.Values, s => Assert.InRange(s, 1, spec.TotalSlots));
        }
    }
}
