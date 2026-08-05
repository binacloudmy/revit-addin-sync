// CircuitGrouping — the grouping half of suggest_circuits' decision core.
// Determinism matters as much as correctness here: the same model must always
// produce the same plan, or the drafter reviews one grouping and commits
// another. PhaseBalance, the other half, is in PhaseBalanceTests.cs.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class CircuitGroupingTests
    {
        private static ElecDevice Dev(long id, double x, double y, double va = 250,
                                      string loadClass = "receptacle")
            => new() { Id = id, XMm = x, YMm = y, Va = va, LoadClass = loadClass };

        private static GroupingOptions Opt(int maxDevices = 10, double maxVa = 2300,
                                           double? spanMm = null)
            => new(maxDevices, maxVa, spanMm);

        [Fact]
        public void Never_mixes_load_classes_in_one_circuit()
        {
            var devices = new List<ElecDevice>
            {
                Dev(1, 0, 0), Dev(2, 100, 0),
                Dev(3, 50, 0, 100, "lighting"), Dev(4, 150, 0, 100, "lighting"),
            };
            var groups = CircuitGrouping.Group(devices, 0, 0, Opt());

            Assert.All(groups, g =>
                Assert.Single(g.DevicesInChainOrder.Select(d => d.LoadClass).Distinct()));
            Assert.Equal(2, groups.Count);
        }

        [Fact]
        public void Splits_when_device_cap_is_hit()
        {
            var devices = Enumerable.Range(1, 7)
                .Select(i => Dev(i, i * 1000, 0)).ToList();
            var groups = CircuitGrouping.Group(devices, 0, 0, Opt(maxDevices: 3, maxVa: 99999));

            Assert.Equal(3, groups.Count);
            Assert.Equal(new[] { 3, 3, 1 }, groups.Select(g => g.DevicesInChainOrder.Count));
        }

        [Fact]
        public void Splits_when_va_cap_is_hit()
        {
            var devices = Enumerable.Range(1, 4)
                .Select(i => Dev(i, i * 1000, 0, va: 1000)).ToList();
            var groups = CircuitGrouping.Group(devices, 0, 0, Opt(maxDevices: 99, maxVa: 2500));

            Assert.Equal(2, groups.Count);
            Assert.All(groups, g => Assert.True(g.TotalVa <= 2500));
        }

        [Fact]
        public void Span_cap_closes_a_group_before_a_distant_device_joins()
        {
            // Two tight clusters 50 m apart; span cap keeps them separate even
            // though device and VA caps would allow one big circuit.
            var devices = new List<ElecDevice>
            {
                Dev(1, 0, 0), Dev(2, 500, 0),
                Dev(3, 50000, 0), Dev(4, 50500, 0),
            };
            var groups = CircuitGrouping.Group(devices, 0, 0, Opt(spanMm: 5000));

            Assert.Equal(2, groups.Count);
            Assert.Equal(new long[] { 1, 2 }, groups[0].DeviceIds.OrderBy(i => i));
            Assert.Equal(new long[] { 3, 4 }, groups[1].DeviceIds.OrderBy(i => i));
        }

        [Fact]
        public void Chain_starts_at_the_device_nearest_the_panel()
        {
            var devices = new List<ElecDevice>
            {
                Dev(1, 9000, 0), Dev(2, 1000, 0), Dev(3, 5000, 0),
            };
            var groups = CircuitGrouping.Group(devices, 0, 0, Opt());

            Assert.Single(groups);
            Assert.Equal(new long[] { 2, 3, 1 }, groups[0].DeviceIds.ToArray());
        }

        [Fact]
        public void Grouping_is_deterministic_under_input_reordering()
        {
            var a = new List<ElecDevice>
            {
                Dev(1, 0, 0), Dev(2, 1000, 0), Dev(3, 2000, 0), Dev(4, 40000, 0),
            };
            var b = new List<ElecDevice> { a[3], a[1], a[0], a[2] };

            var ga = CircuitGrouping.Group(a, 0, 0, Opt(spanMm: 10000));
            var gb = CircuitGrouping.Group(b, 0, 0, Opt(spanMm: 10000));

            Assert.Equal(
                ga.Select(g => string.Join(",", g.DeviceIds)),
                gb.Select(g => string.Join(",", g.DeviceIds)));
        }

        [Fact]
        public void Oversized_single_device_still_gets_a_circuit_with_a_note()
        {
            // A 3000 VA water heater against a 2300 VA cap: it cannot be left
            // uncircuited, so it goes alone and the note says why.
            var devices = new List<ElecDevice> { Dev(1, 0, 0, va: 3000) };
            var groups = CircuitGrouping.Group(devices, 0, 0, Opt(maxVa: 2300));

            Assert.Single(groups);
            Assert.Contains("exceeds max_va_per_circuit", groups[0].Notes.Single());
        }

        [Fact]
        public void Empty_input_yields_no_groups()
        {
            Assert.Empty(CircuitGrouping.Group(new List<ElecDevice>(), 0, 0, Opt()));
        }
    }
}
