// The decision half of remove_from_circuit: given what the caller asked for
// and what the model currently holds, decide what happens to each circuit.
//
// Revit-free on purpose (System only), so Tests.csproj can source-link it —
// CircuitDisconnect itself needs a live Document and can only be exercised in
// UAT. Everything that is a RULE lives here; everything that is an API call
// lives there.
//
// THE ONE RULE WORTH READING: a circuit that loses ALL of its members is
// DELETED, never emptied. RemoveFromCircuit is documented all-or-nothing and a
// zero-terminal ElectricalSystem is an undefined state that still occupies a
// breaker slot — and a panel whose slots are held by invisible empty circuits
// rejects the next SelectPanel with "panel is full", which is the wording that
// starts the swap-the-DB-box loop this whole feature exists to stop.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal enum DisconnectKind
    {
        /// <summary>Some members go, some stay: RemoveFromCircuit.</summary>
        RemoveMembers,
        /// <summary>Nothing would be left, or the caller named the circuit
        /// itself: doc.Delete on the ElectricalSystem.</summary>
        DeleteWhole,
    }

    internal sealed class CircuitAction
    {
        public long CircuitId;
        public DisconnectKind Kind;
        public List<long> MembersToRemove = new();
        public int RemainingCount;
    }

    /// <summary>Why a requested device id produced no work.</summary>
    internal sealed class DeviceMiss
    {
        public long DeviceId;
        public string Reason = "";     // not_circuited
    }

    internal sealed class DisconnectPlan
    {
        public List<CircuitAction> Actions = new();
        public List<DeviceMiss> MissedDevices = new();
        /// <summary>circuit_ids the caller named that the model does not hold.</summary>
        public List<long> UnknownCircuitIds = new();
    }

    internal static class CircuitDisconnectPlanner
    {
        /// <summary>Decide per circuit. <paramref name="live"/> is every power
        /// circuit that could be touched, with its CURRENT member device ids
        /// (panel excluded). Both request lists may be empty; the union of what
        /// they select is what gets acted on.</summary>
        public static DisconnectPlan Build(
            IReadOnlyList<long> requestedDeviceIds,
            IReadOnlyList<long> requestedCircuitIds,
            IReadOnlyList<(long CircuitId, IReadOnlyList<long> MemberIds)> live)
        {
            var plan = new DisconnectPlan();

            // Duplicates in either list must not double-count a device or turn
            // a partial removal into a whole-circuit delete.
            var wantDevices = new HashSet<long>(requestedDeviceIds ?? Array.Empty<long>());
            var wantCircuits = new HashSet<long>(requestedCircuitIds ?? Array.Empty<long>());
            var liveById = live
                .GroupBy(c => c.CircuitId)
                .ToDictionary(g => g.Key, g => g.First().MemberIds);

            foreach (var id in wantCircuits.OrderBy(x => x))
                if (!liveById.ContainsKey(id))
                    plan.UnknownCircuitIds.Add(id);

            // Ordered by circuit id so two runs on the same model produce the
            // same result rows and the same commit order.
            foreach (var circuitId in liveById.Keys.OrderBy(x => x))
            {
                var members = liveById[circuitId];
                bool namedWhole = wantCircuits.Contains(circuitId);
                var hit = members.Where(m => wantDevices.Contains(m)).Distinct().OrderBy(x => x).ToList();

                if (!namedWhole && hit.Count == 0) continue;   // untouched

                // Union wins: naming the circuit AND a subset of its devices
                // means the caller asked for the circuit, so the subset is not
                // a narrowing instruction.
                bool takesEverything = namedWhole || hit.Count >= members.Count;

                plan.Actions.Add(new CircuitAction
                {
                    CircuitId = circuitId,
                    Kind = takesEverything ? DisconnectKind.DeleteWhole : DisconnectKind.RemoveMembers,
                    MembersToRemove = takesEverything
                        ? members.Distinct().OrderBy(x => x).ToList()
                        : hit,
                    RemainingCount = takesEverything ? 0 : members.Distinct().Count() - hit.Count,
                });
            }

            // A device the caller named that no live circuit holds. Reported,
            // never an error: "these were already free" is a fine answer, and
            // ok:false here would make the agent retry a no-op.
            var touched = new HashSet<long>(plan.Actions.SelectMany(a => a.MembersToRemove));
            foreach (var id in wantDevices.OrderBy(x => x))
                if (!touched.Contains(id))
                    plan.MissedDevices.Add(new DeviceMiss { DeviceId = id, Reason = "not_circuited" });

            return plan;
        }
    }
}
