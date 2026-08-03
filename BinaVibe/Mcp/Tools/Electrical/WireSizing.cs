// Wire sizing + voltage drop arithmetic — pure, Revit-free.
//
// Mechanical table lookup only. The sizing table itself arrives on the wire
// from the backend recipe (rows of {max_current_a, wire_csa_mm2,
// conduit_diameter_mm, mv_per_a_m}); no ampacity, CSA or resistivity value is
// baked into the addin.
//
// Voltage drop follows the BS 7671 / MS IEC 60364 mV/A/m convention: the
// tabulated millivolt-per-amp-per-metre value already accounts for the return
// path, so length is the ONE-WAY route length. Three-phase tabulated values
// differ from single-phase by a fixed sqrt(3)/2 factor; callers with a
// single-phase table pass threePhase=true to apply it.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One row of the caller-supplied sizing table.</summary>
    public sealed class SizingRow
    {
        public double MaxCurrentA;
        public double WireCsaMm2;
        public double ConduitDiameterMm;
        /// <summary>Voltage drop, millivolts per amp per metre (loop
        /// accounted for, per the tabulation convention).</summary>
        public double MvPerAM;
    }

    public static class WireSizing
    {
        /// <summary>Smallest row whose MaxCurrentA covers the demand. Null when
        /// no row is adequate — the caller reports no_adequate_size, never
        /// rounds up silently.</summary>
        public static SizingRow? Pick(double amps, IReadOnlyList<SizingRow> table)
        {
            if (table == null || table.Count == 0) return null;
            SizingRow? best = null;
            foreach (var row in table.OrderBy(r => r.MaxCurrentA).ThenBy(r => r.WireCsaMm2))
            {
                if (row.MaxCurrentA >= amps) { best = row; break; }
            }
            return best;
        }

        /// <summary>Design current for a circuit load. Single-phase I = VA/V;
        /// three-phase (balanced, line-neutral voltage) I = VA / (3 * V).</summary>
        public static double CalcAmps(double totalVa, double voltageV, bool threePhase)
        {
            if (!(voltageV > 0)) throw new ArgumentException("voltage_v must be > 0");
            return threePhase ? totalVa / (3.0 * voltageV) : totalVa / voltageV;
        }

        /// <summary>Voltage drop as a percentage of nominal voltage.
        /// drop_mV = mV/A/m x I x L(m); threePhase applies the standard
        /// sqrt(3)/2 conversion for single-phase-tabulated values.</summary>
        public static double VoltageDropPct(
            double amps, double mvPerAM, double lengthMm, double voltageV,
            bool threePhase = false)
        {
            if (!(voltageV > 0)) throw new ArgumentException("voltage_v must be > 0");
            double factor = threePhase ? Math.Sqrt(3.0) / 2.0 : 1.0;
            double dropV = mvPerAM * amps * (lengthMm / 1000.0) * factor / 1000.0;
            return dropV / voltageV * 100.0;
        }

        /// <summary>Parse caller-supplied rows. Throws with a self-healable
        /// message naming the exact missing key and row index, so the agent
        /// can fix the table without guessing.</summary>
        public static List<SizingRow> ParseTable(
            IReadOnlyList<IReadOnlyDictionary<string, double>> rows)
        {
            if (rows == null || rows.Count == 0)
                throw new ArgumentException("sizing_table must have at least one row");
            var outRows = new List<SizingRow>();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                outRows.Add(new SizingRow
                {
                    MaxCurrentA = Req(r, "max_current_a", i),
                    WireCsaMm2 = Req(r, "wire_csa_mm2", i),
                    ConduitDiameterMm = Req(r, "conduit_diameter_mm", i),
                    MvPerAM = Req(r, "mv_per_a_m", i),
                });
            }
            return outRows;
        }

        private static double Req(IReadOnlyDictionary<string, double> row, string key, int index)
        {
            if (!row.TryGetValue(key, out var v))
                throw new ArgumentException(
                    "sizing_table row " + index + " is missing '" + key +
                    "' — every row needs max_current_a, wire_csa_mm2, conduit_diameter_mm, mv_per_a_m");
            if (!(v > 0))
                throw new ArgumentException(
                    "sizing_table row " + index + " '" + key + "' must be > 0");
            return v;
        }
    }
}
