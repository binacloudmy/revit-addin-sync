// Panel assignment + phase balancing — pure, Revit-free.
//
// Split out of CircuitCandidates.cs (which needs a live Revit Document) so the
// capacity and balance rules are testable in Tests/Tests.csproj. Facts about
// panels (phase count, mains rating, existing per-phase load) are gathered
// Revit-side and handed in; this file only decides.
//
// The proposed phase is exactly that — a PROPOSAL. Revit assigns real slots at
// commit time; CircuitCommit.cs reads the actual slot back and reports both.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One candidate panel, facts gathered from the model.</summary>
    public sealed class PanelInfo
    {
        public long Id;
        public string Name = "";
        /// <summary>1 or 3.</summary>
        public int Phases = 1;
        /// <summary>Mains rating in amps; null when the parameter is unset —
        /// capacity is then reported "unknown", never guessed.</summary>
        public double? MainsA;
        /// <summary>Load already connected to the panel, VA.</summary>
        public double ConnectedVa;
        /// <summary>Existing per-phase load, VA. Length == Phases.</summary>
        public double[] PhaseVa = Array.Empty<double>();
    }

    /// <summary>Where one circuit should land.</summary>
    public sealed class CircuitAssignment
    {
        public int CircuitIndex;
        public long PanelId;
        /// <summary>0-based phase within the panel (always 0 on single-phase).</summary>
        public int ProposedPhase;
        public bool Feasible = true;
        public string Reason = "";
    }

    public static class PhaseBalance
    {
        /// <summary>Assign circuits to panels by available capacity, then to
        /// the lightest phase within the panel. Circuits are taken largest-VA
        /// first (ties by index) so the big loads land while there is still
        /// room to balance around them.
        ///
        /// A panel with unknown mains is used only when no panel with known
        /// spare capacity fits, and the assignment carries a reason saying the
        /// capacity was not verifiable.</summary>
        public static List<CircuitAssignment> Assign(
            IReadOnlyList<CircuitGroup> circuits,
            IReadOnlyList<PanelInfo> panels,
            double voltageV)
        {
            if (panels == null || panels.Count == 0)
                throw new ArgumentException("at least one panel required — the caller gates no_panel before this");
            if (!(voltageV > 0)) throw new ArgumentException("voltage_v must be > 0");

            // Running state per panel: VA assigned by this call, per phase.
            var extraVa = panels.ToDictionary(p => p.Id, _ => 0.0);
            var extraPhaseVa = panels.ToDictionary(
                p => p.Id, p => new double[Math.Max(1, p.Phases)]);

            var result = new List<CircuitAssignment>();
            foreach (var c in circuits
                         .OrderByDescending(c => c.TotalVa)
                         .ThenBy(c => c.Index))
            {
                var a = new CircuitAssignment { CircuitIndex = c.Index };

                PanelInfo? chosen = null;
                double bestSpare = double.MinValue;
                foreach (var p in panels.OrderBy(p => p.Id))
                {
                    double? spare = SpareVa(p, extraVa[p.Id], voltageV);
                    if (!spare.HasValue) continue;       // unknown capacity — fallback only
                    if (spare.Value >= c.TotalVa && spare.Value > bestSpare)
                    {
                        bestSpare = spare.Value;
                        chosen = p;
                    }
                }

                if (chosen == null)
                {
                    // No panel with verified room — take the first unknown-capacity
                    // panel if one exists, else the least-overloaded known one.
                    chosen = panels.OrderBy(p => p.Id).FirstOrDefault(p => !p.MainsA.HasValue);
                    if (chosen != null)
                    {
                        a.Feasible = true;
                        a.Reason = "panel capacity unknown (mains rating unset) — not verified";
                    }
                    else
                    {
                        chosen = panels
                            .OrderByDescending(p => SpareVa(p, extraVa[p.Id], voltageV) ?? double.MinValue)
                            .ThenBy(p => p.Id)
                            .First();
                        a.Feasible = false;
                        a.Reason = "no panel has spare capacity for " + Math.Round(c.TotalVa) + " VA";
                    }
                }

                a.PanelId = chosen.Id;
                a.ProposedPhase = LightestPhase(chosen, extraPhaseVa[chosen.Id]);
                extraVa[chosen.Id] += c.TotalVa;
                extraPhaseVa[chosen.Id][a.ProposedPhase] += c.TotalVa;
                result.Add(a);
            }
            return result.OrderBy(a => a.CircuitIndex).ToList();
        }

        /// <summary>Spare VA on a panel after existing + newly assigned load.
        /// Null when the mains rating is unknown.</summary>
        public static double? SpareVa(PanelInfo p, double assignedVa, double voltageV)
        {
            if (!p.MainsA.HasValue) return null;
            double capacity = p.Phases == 3
                ? p.MainsA.Value * voltageV * 3.0     // per-phase mains rating x 3 line-neutral legs
                : p.MainsA.Value * voltageV;
            return capacity - p.ConnectedVa - assignedVa;
        }

        private static int LightestPhase(PanelInfo p, double[] extraPhaseVa)
        {
            int phases = Math.Max(1, p.Phases);
            int best = 0;
            double bestVa = double.MaxValue;
            for (int i = 0; i < phases; i++)
            {
                double existing = i < p.PhaseVa.Length ? p.PhaseVa[i] : 0.0;
                double total = existing + extraPhaseVa[i];
                if (total < bestVa - 1e-9)
                {
                    bestVa = total;
                    best = i;
                }
            }
            return best;
        }
    }
}
