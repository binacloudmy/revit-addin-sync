// WireSizing — mechanical lookup over a caller-supplied table. The table
// itself is a required wire arg (the standard lives in the backend recipe);
// these tests pin the lookup, the drop arithmetic and the self-healable
// parse errors.

using System;
using System.Collections.Generic;
using BinaVibe.Mcp.Tools.Electrical;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class WireSizingTests
    {
        private static List<SizingRow> Table() => new()
        {
            new SizingRow { MaxCurrentA = 20, WireCsaMm2 = 2.5, ConduitDiameterMm = 20, MvPerAM = 18 },
            new SizingRow { MaxCurrentA = 10, WireCsaMm2 = 1.5, ConduitDiameterMm = 20, MvPerAM = 29 },
            new SizingRow { MaxCurrentA = 32, WireCsaMm2 = 6.0, ConduitDiameterMm = 25, MvPerAM = 7.3 },
        };

        [Fact]
        public void Picks_the_smallest_adequate_row_regardless_of_table_order()
        {
            var row = WireSizing.Pick(9.5, Table());
            Assert.NotNull(row);
            Assert.Equal(1.5, row!.WireCsaMm2);

            row = WireSizing.Pick(10.5, Table());
            Assert.Equal(2.5, row!.WireCsaMm2);
        }

        [Fact]
        public void Exact_boundary_current_still_fits_the_row()
        {
            Assert.Equal(1.5, WireSizing.Pick(10.0, Table())!.WireCsaMm2);
        }

        [Fact]
        public void No_adequate_row_returns_null_never_rounds_up()
        {
            Assert.Null(WireSizing.Pick(40, Table()));
            Assert.Null(WireSizing.Pick(5, new List<SizingRow>()));
        }

        [Fact]
        public void Calc_amps_single_and_three_phase()
        {
            Assert.Equal(10.0, WireSizing.CalcAmps(2300, 230, threePhase: false), 3);
            Assert.Equal(10.0, WireSizing.CalcAmps(6900, 230, threePhase: true), 3);
        }

        [Fact]
        public void Voltage_drop_follows_the_mv_per_a_m_convention()
        {
            // 18 mV/A/m x 10 A x 25 m = 4.5 V on 230 V = 1.95652%
            var pct = WireSizing.VoltageDropPct(10, 18, 25000, 230);
            Assert.Equal(4.5 / 230.0 * 100.0, pct, 6);
        }

        [Fact]
        public void Three_phase_drop_applies_the_sqrt3_over_2_factor()
        {
            var single = WireSizing.VoltageDropPct(10, 18, 25000, 230, threePhase: false);
            var three = WireSizing.VoltageDropPct(10, 18, 25000, 230, threePhase: true);
            Assert.Equal(single * Math.Sqrt(3) / 2.0, three, 6);
        }

        [Fact]
        public void Parse_names_the_missing_key_and_row_index()
        {
            var rows = new List<IReadOnlyDictionary<string, double>>
            {
                new Dictionary<string, double>
                {
                    ["max_current_a"] = 20, ["wire_csa_mm2"] = 2.5,
                    ["conduit_diameter_mm"] = 20, ["mv_per_a_m"] = 18,
                },
                new Dictionary<string, double>
                {
                    ["max_current_a"] = 32, ["wire_csa_mm2"] = 6.0,
                    ["conduit_diameter_mm"] = 25,
                    // mv_per_a_m missing
                },
            };
            var ex = Assert.Throws<ArgumentException>(() => WireSizing.ParseTable(rows));
            Assert.Contains("row 1", ex.Message);
            Assert.Contains("mv_per_a_m", ex.Message);
        }

        [Fact]
        public void Parse_rejects_an_empty_table()
        {
            Assert.Throws<ArgumentException>(
                () => WireSizing.ParseTable(new List<IReadOnlyDictionary<string, double>>()));
        }

        [Fact]
        public void Parse_rejects_non_positive_values()
        {
            var rows = new List<IReadOnlyDictionary<string, double>>
            {
                new Dictionary<string, double>
                {
                    ["max_current_a"] = 0, ["wire_csa_mm2"] = 2.5,
                    ["conduit_diameter_mm"] = 20, ["mv_per_a_m"] = 18,
                },
            };
            var ex = Assert.Throws<ArgumentException>(() => WireSizing.ParseTable(rows));
            Assert.Contains("must be > 0", ex.Message);
        }
    }
}
