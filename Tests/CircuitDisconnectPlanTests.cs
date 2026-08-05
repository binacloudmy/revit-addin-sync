// CircuitDisconnectPlanner — the rules remove_from_circuit applies before it
// touches Revit.
//
// The rule worth pinning hardest is DeleteWhole: a circuit that loses every
// member is deleted, not emptied. An empty ElectricalSystem still holds its
// breaker slot, so a panel accumulating invisible empties starts rejecting new
// circuits as "full" — the wording that sent the agent swapping DB boxes in
// UAT 2026-08-04. CircuitDisconnect.cs itself needs a live Document and is
// UAT-only; everything decidable lives here.

using System.Collections.Generic;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class CircuitDisconnectPlanTests
    {
        private static List<(long, IReadOnlyList<long>)> Live(
            params (long CircuitId, long[] Members)[] rows)
        {
            var live = new List<(long, IReadOnlyList<long>)>();
            foreach (var r in rows) live.Add((r.CircuitId, r.Members));
            return live;
        }

        [Fact]
        public void Removing_some_members_keeps_the_circuit()
        {
            var plan = CircuitDisconnectPlanner.Build(
                new long[] { 11, 12 }, new long[0],
                Live((100, new long[] { 11, 12, 13, 14 })));

            var a = Assert.Single(plan.Actions);
            Assert.Equal(100, a.CircuitId);
            Assert.Equal(DisconnectKind.RemoveMembers, a.Kind);
            Assert.Equal(new long[] { 11, 12 }, a.MembersToRemove);
            Assert.Equal(2, a.RemainingCount);
        }

        [Fact]
        public void Removing_every_member_deletes_the_circuit()
        {
            var plan = CircuitDisconnectPlanner.Build(
                new long[] { 11, 12, 13 }, new long[0],
                Live((100, new long[] { 11, 12, 13 })));

            var a = Assert.Single(plan.Actions);
            Assert.Equal(DisconnectKind.DeleteWhole, a.Kind);
            Assert.Equal(0, a.RemainingCount);
        }

        [Fact]
        public void Naming_a_circuit_deletes_it_whole_even_with_a_device_subset()
        {
            // Union, not intersection: asking for the circuit AND one of its
            // devices is not a request to narrow down to that device.
            var plan = CircuitDisconnectPlanner.Build(
                new long[] { 11 }, new long[] { 100 },
                Live((100, new long[] { 11, 12, 13 })));

            var a = Assert.Single(plan.Actions);
            Assert.Equal(DisconnectKind.DeleteWhole, a.Kind);
            Assert.Equal(new long[] { 11, 12, 13 }, a.MembersToRemove);
            Assert.Equal(0, a.RemainingCount);
        }

        [Fact]
        public void Untouched_circuits_produce_no_action()
        {
            var plan = CircuitDisconnectPlanner.Build(
                new long[] { 11 }, new long[0],
                Live((100, new long[] { 11, 12 }),
                     (200, new long[] { 21, 22 })));

            Assert.Equal(100, Assert.Single(plan.Actions).CircuitId);
        }

        [Fact]
        public void A_device_on_no_circuit_is_reported_not_errored()
        {
            // "already free" is a fine answer; ok:false here would make the
            // agent retry a no-op.
            var plan = CircuitDisconnectPlanner.Build(
                new long[] { 11, 99 }, new long[0],
                Live((100, new long[] { 11, 12 })));

            var miss = Assert.Single(plan.MissedDevices);
            Assert.Equal(99, miss.DeviceId);
            Assert.Equal("not_circuited", miss.Reason);
        }

        [Fact]
        public void A_device_on_two_circuits_touches_both()
        {
            var plan = CircuitDisconnectPlanner.Build(
                new long[] { 11 }, new long[0],
                Live((100, new long[] { 11, 12 }),
                     (200, new long[] { 11, 21 })));

            Assert.Equal(2, plan.Actions.Count);
            Assert.Empty(plan.MissedDevices);
        }

        [Fact]
        public void Duplicate_ids_do_not_turn_a_partial_removal_into_a_delete()
        {
            // Counting hits without de-duplicating would see 3 >= 3 members.
            var plan = CircuitDisconnectPlanner.Build(
                new long[] { 11, 11, 11 }, new long[0],
                Live((100, new long[] { 11, 12, 13 })));

            var a = Assert.Single(plan.Actions);
            Assert.Equal(DisconnectKind.RemoveMembers, a.Kind);
            Assert.Equal(new long[] { 11 }, a.MembersToRemove);
            Assert.Equal(2, a.RemainingCount);
        }

        [Fact]
        public void An_unknown_circuit_id_is_reported_separately()
        {
            var plan = CircuitDisconnectPlanner.Build(
                new long[0], new long[] { 100, 999 },
                Live((100, new long[] { 11 })));

            Assert.Equal(new long[] { 999 }, plan.UnknownCircuitIds);
            Assert.Single(plan.Actions);
        }

        [Fact]
        public void Actions_and_members_come_back_id_ordered()
        {
            // Deterministic order = deterministic commit order = the same
            // result rows on a re-run of the same model.
            var plan = CircuitDisconnectPlanner.Build(
                new long[] { 22, 13, 11 }, new long[0],
                Live((300, new long[] { 22, 23 }),
                     (100, new long[] { 13, 11, 12 })));

            Assert.Equal(new long[] { 100, 300 },
                         new[] { plan.Actions[0].CircuitId, plan.Actions[1].CircuitId });
            Assert.Equal(new long[] { 11, 13 }, plan.Actions[0].MembersToRemove);
        }

        [Fact]
        public void An_empty_request_selects_nothing()
        {
            // CircuitDisconnect refuses this at the arg check, but the planner
            // must not treat "no filter" as "everything" either.
            var plan = CircuitDisconnectPlanner.Build(
                new long[0], new long[0],
                Live((100, new long[] { 11, 12 })));

            Assert.Empty(plan.Actions);
        }
    }
}
