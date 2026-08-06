// Panel phase-balance and slot-fit math — pure, Revit-free, VA and slot
// numbers only. No Document, no ElectricalSystem, no PanelScheduleView.
//
// Split from PanelTools.cs so it can be linked into Tests/Tests.csproj — the
// parts that can be wrong in an interesting way (which phase a slot belongs
// to, whether a 2-pole circuit fits, whether a proposed move actually
// improves anything) are testable without a live panel. Same reason
// SocketLayout.cs was split out of SocketCandidates.cs.
//
// SLOT/PHASE CONVENTION (Revit panelboard numbering): slots run 1,2 across the
// first row, 3,4 the second, and so on — so a slot's phase is
// ((slot - 1) / 2) % phases. A multi-pole breaker occupies every OTHER slot
// from its start (1-pole: {n}; 2-pole: {n, n+2}; 3-pole: {n, n+2, n+4}),
// which is exactly what makes it straddle consecutive phases.
//
// This file proposes; it never claims a proposal is code-compliant.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One circuit as far as balancing is concerned.</summary>
    public sealed class CircuitLoad
    {
        public long Id;
        public string Name = "";
        /// <summary>1-based panel slot the breaker starts at. 0 = unassigned.</summary>
        public int StartSlot;
        /// <summary>1, 2 or 3.</summary>
        public int Poles = 1;
        /// <summary>Apparent load in volt-amperes.</summary>
        public double LoadVa;
        /// <summary>Grouped or locked in the panel schedule — never proposed
        /// for a move, because Revit will refuse it.</summary>
        public bool Locked;
    }

    public sealed class PanelSpec
    {
        /// <summary>Total breaker slots (ways) on the board.</summary>
        public int TotalSlots = 42;
        /// <summary>1 for single-phase, 3 for three-phase. Anything else is
        /// treated as 1 — a panel cannot balance across phases it lacks.</summary>
        public int PhaseCount = 3;
    }

    public sealed class SlotMove
    {
        public long CircuitId;
        public string Name = "";
        public int FromSlot;
        public int ToSlot;
        public int FromPhase;
        public int ToPhase;
    }

    public sealed class BalancePlan
    {
        public List<SlotMove> Moves = new();
        public double[] BeforeVa = Array.Empty<double>();
        public double[] AfterVa = Array.Empty<double>();
        public double BeforeImbalancePct;
        public double AfterImbalancePct;
        /// <summary>Circuits deliberately left alone: {id, reason}.</summary>
        public List<(long Id, string Reason)> Skipped = new();
        /// <summary>Set when no move could improve the spread. The caller must
        /// say this out loud rather than reporting "0 moves" as success.</summary>
        public string? Note;
    }

    public static class PanelLoad
    {
        public const string PhaseLabels = "ABC";

        // ─── slot geometry ──────────────────────────────────────────────

        /// <summary>0-based phase index of a slot. Returns -1 for a slot
        /// outside the board or an unassigned (0) slot.</summary>
        public static int PhaseOfSlot(int slot, int phaseCount)
        {
            var phases = phaseCount == 3 ? 3 : 1;
            if (slot < 1) return -1;
            return ((slot - 1) / 2) % phases;
        }

        public static string PhaseLabel(int phaseIndex) =>
            phaseIndex >= 0 && phaseIndex < PhaseLabels.Length
                ? PhaseLabels[phaseIndex].ToString()
                : "?";

        /// <summary>Slots a breaker occupies from its start slot.</summary>
        public static List<int> SlotsFor(int startSlot, int poles)
        {
            var slots = new List<int>();
            if (startSlot < 1) return slots;
            var n = poles < 1 ? 1 : poles;
            for (int i = 0; i < n; i++) slots.Add(startSlot + (i * 2));
            return slots;
        }

        /// <summary>Every slot taken on the board, ignoring one circuit (pass
        /// its id when testing a move of that circuit).</summary>
        public static HashSet<int> OccupiedSlots(IEnumerable<CircuitLoad> circuits, long? ignoreId = null)
        {
            var taken = new HashSet<int>();
            foreach (var c in circuits)
            {
                if (ignoreId.HasValue && c.Id == ignoreId.Value) continue;
                foreach (var s in SlotsFor(c.StartSlot, c.Poles)) taken.Add(s);
            }
            return taken;
        }

        /// <summary>Can a breaker of this pole count start here? Checks the
        /// board's extent and every slot it would occupy.</summary>
        public static bool CanPlace(int startSlot, int poles, ISet<int> occupied, PanelSpec spec)
        {
            var slots = SlotsFor(startSlot, poles);
            if (slots.Count == 0) return false;
            foreach (var s in slots)
            {
                if (s < 1 || s > spec.TotalSlots) return false;
                if (occupied.Contains(s)) return false;
            }
            return true;
        }

        /// <summary>Start slots where a breaker of this pole count would fit,
        /// in ascending order.</summary>
        public static List<int> FreeStarts(int poles, ISet<int> occupied, PanelSpec spec)
        {
            var starts = new List<int>();
            for (int s = 1; s <= spec.TotalSlots; s++)
                if (CanPlace(s, poles, occupied, spec)) starts.Add(s);
            return starts;
        }

        // ─── load distribution ──────────────────────────────────────────

        /// <summary>Per-phase VA. A multi-pole circuit's load is split evenly
        /// across the phases it straddles — the same assumption Revit's own
        /// balanced-load figure makes, and it is an assumption, not a
        /// measurement.</summary>
        public static double[] PhaseLoads(IEnumerable<CircuitLoad> circuits, PanelSpec spec)
        {
            var phases = spec.PhaseCount == 3 ? 3 : 1;
            var totals = new double[phases];
            foreach (var c in circuits)
            {
                var slots = SlotsFor(c.StartSlot, c.Poles);
                var touched = slots
                    .Select(s => PhaseOfSlot(s, phases))
                    .Where(p => p >= 0)
                    .Distinct()
                    .ToList();
                if (touched.Count == 0) continue;
                var share = c.LoadVa / touched.Count;
                foreach (var p in touched) totals[p] += share;
            }
            return totals;
        }

        /// <summary>Spread as a percentage of the heaviest phase:
        /// (max - min) / max * 100. Zero when every phase is empty. Returns 0
        /// for a single-phase board, where the concept does not apply.</summary>
        public static double ImbalancePct(double[] phaseLoads)
        {
            if (phaseLoads.Length < 2) return 0;
            var max = phaseLoads.Max();
            var min = phaseLoads.Min();
            if (max <= 0) return 0;
            return (max - min) / max * 100.0;
        }

        /// <summary>Sum of squared deviations from the mean — the objective the
        /// greedy search minimises.
        ///
        /// It is NOT ImbalancePct, deliberately. With everything on phase A,
        /// (max-min)/max is 100% and STAYS 100% after the first good move
        /// (3000/0/0 -> 2000/1000/0 is still max-min == max), so a greedy step
        /// that insists on improving the reported percentage finds nothing to
        /// do and the tool reports "already balanced" on a board that is not.
        /// Dispersion falls on every genuine move, so the search keeps going;
        /// the percentage is what gets REPORTED, not what gets optimised.</summary>
        public static double Dispersion(double[] phaseLoads)
        {
            if (phaseLoads.Length == 0) return 0;
            var mean = phaseLoads.Average();
            return phaseLoads.Sum(v => (v - mean) * (v - mean));
        }

        // ─── the plan ───────────────────────────────────────────────────

        /// <summary>Greedy rebalance: repeatedly move the single change that
        /// most reduces the spread, until nothing helps or the move budget is
        /// spent.
        ///
        /// ONLY 1-POLE CIRCUITS ARE MOVED. A 3-pole breaker touches all three
        /// phases wherever it sits, so moving it changes nothing; a 2-pole one
        /// does change the split, but its slot constraints interact with every
        /// other breaker's and a greedy step routinely makes things worse.
        /// Both are reported in Skipped rather than silently ignored.</summary>
        public static BalancePlan Plan(IReadOnlyList<CircuitLoad> circuits, PanelSpec spec, int maxMoves = 12)
        {
            var phases = spec.PhaseCount == 3 ? 3 : 1;
            var working = circuits.Select(c => new CircuitLoad
            {
                Id = c.Id, Name = c.Name, StartSlot = c.StartSlot,
                Poles = c.Poles, LoadVa = c.LoadVa, Locked = c.Locked,
            }).ToList();

            var plan = new BalancePlan
            {
                BeforeVa = PhaseLoads(working, spec),
            };
            plan.BeforeImbalancePct = ImbalancePct(plan.BeforeVa);

            foreach (var c in circuits)
            {
                if (c.Locked) plan.Skipped.Add((c.Id, "locked or grouped in the panel schedule"));
                else if (c.StartSlot < 1) plan.Skipped.Add((c.Id, "not assigned to a slot"));
                else if (c.Poles != 1) plan.Skipped.Add((c.Id, $"{c.Poles}-pole breaker — not moved by this tool"));
            }

            if (phases < 2)
            {
                plan.AfterVa = plan.BeforeVa;
                plan.AfterImbalancePct = plan.BeforeImbalancePct;
                plan.Note = "single-phase board — there is nothing to balance across";
                return plan;
            }

            var movable = working
                .Where(c => !c.Locked && c.Poles == 1 && c.StartSlot >= 1)
                .ToList();

            if (movable.Count == 0)
            {
                plan.AfterVa = plan.BeforeVa;
                plan.AfterImbalancePct = plan.BeforeImbalancePct;
                plan.Note = "no movable 1-pole circuits on this panel";
                return plan;
            }

            var current = Dispersion(plan.BeforeVa);

            for (int step = 0; step < maxMoves; step++)
            {
                CircuitLoad? bestCircuit = null;
                var bestSlot = 0;
                var bestScore = current;

                foreach (var c in movable)
                {
                    var occupied = OccupiedSlots(working, ignoreId: c.Id);
                    var originalSlot = c.StartSlot;
                    var originalPhase = PhaseOfSlot(originalSlot, phases);

                    foreach (var slot in FreeStarts(1, occupied, spec))
                    {
                        // Only a phase change can alter the balance.
                        if (PhaseOfSlot(slot, phases) == originalPhase) continue;

                        c.StartSlot = slot;
                        var score = Dispersion(PhaseLoads(working, spec));
                        c.StartSlot = originalSlot;

                        // Strict improvement, with a tolerance, so float noise
                        // cannot produce an endless shuffle of equal states.
                        if (score < bestScore - 1e-9)
                        {
                            bestScore = score;
                            bestCircuit = c;
                            bestSlot = slot;
                        }
                    }
                }

                if (bestCircuit == null) break;

                var from = bestCircuit.StartSlot;
                plan.Moves.Add(new SlotMove
                {
                    CircuitId = bestCircuit.Id,
                    Name = bestCircuit.Name,
                    FromSlot = from,
                    ToSlot = bestSlot,
                    FromPhase = PhaseOfSlot(from, phases),
                    ToPhase = PhaseOfSlot(bestSlot, phases),
                });
                bestCircuit.StartSlot = bestSlot;
                current = bestScore;
            }

            plan.AfterVa = PhaseLoads(working, spec);
            plan.AfterImbalancePct = ImbalancePct(plan.AfterVa);

            // Guard, not an optimisation: the search minimises dispersion while
            // the drafter is shown the percentage, and the two can in principle
            // disagree. Handing back a board that reads WORSE than the one the
            // drafter has is the one outcome this tool must never produce, so
            // an unimproved plan is discarded rather than explained away.
            if (plan.AfterImbalancePct > plan.BeforeImbalancePct + 1e-9)
            {
                plan.Moves.Clear();
                plan.AfterVa = plan.BeforeVa;
                plan.AfterImbalancePct = plan.BeforeImbalancePct;
                plan.Note = "no slot move improved the phase spread — left as is";
                return plan;
            }

            if (plan.Moves.Count == 0)
                plan.Note = "already as balanced as slot moves can make it";
            return plan;
        }
    }
}
