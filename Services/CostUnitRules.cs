using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Unit normalization + compatibility rules for cost matching.
    /// Line-for-line C# twin of bina-ai app/services/cost_units.py
    /// (normalize_unit / unit_compatible) — any rule change there must land
    /// here in the same batch. Guards AutoMatch from applying a rate whose
    /// unit cannot price the element (e.g. an m² rate onto an m³ quantity).
    /// </summary>
    public static class CostUnitRules
    {
        // Canonical tokens: m2, m3, m, unit, kg, tonne.
        private static readonly Dictionary<string, string> UnitAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "m2", "m2" }, { "m²", "m2" }, { "sqm", "m2" }, { "sq.m", "m2" }, { "sq m", "m2" },
            { "m3", "m3" }, { "m³", "m3" }, { "cum", "m3" }, { "cu.m", "m3" }, { "cu m", "m3" },
            { "m", "m" }, { "lm", "m" }, { "lin.m", "m" }, { "lin m", "m" }, { "mtr", "m" },
            { "unit", "unit" }, { "no", "unit" }, { "no.", "unit" }, { "nos", "unit" },
            { "each", "unit" }, { "ea", "unit" }, { "pcs", "unit" }, { "pc", "unit" }, { "bil", "unit" },
            { "kg", "kg" },
            { "tonne", "tonne" }, { "ton", "tonne" }, { "t", "tonne" }, { "mt", "tonne" },
        };

        /// <summary>Canonical token for a raw unit string, or null if unknown.</summary>
        public static string Normalize(string unit)
        {
            if (string.IsNullOrWhiteSpace(unit)) return null;
            return UnitAliases.TryGetValue(unit.Trim(), out var canonical) ? canonical : null;
        }

        /// <summary>
        /// True when a rate in rateUnit may price an item measured in itemUnit.
        /// Identical canonical units are compatible. m2&lt;-&gt;m3 only with an
        /// explicit thickness. Unknown units fall back to raw string equality.
        /// </summary>
        public static bool Compatible(string itemUnit, string rateUnit, double? thicknessMm = null)
        {
            string a = Normalize(itemUnit), b = Normalize(rateUnit);
            if (a == null || b == null)
                return !string.IsNullOrWhiteSpace(itemUnit) && !string.IsNullOrWhiteSpace(rateUnit)
                    && string.Equals(itemUnit.Trim(), rateUnit.Trim(), StringComparison.OrdinalIgnoreCase);
            if (a == b) return true;
            if ((a == "m2" && b == "m3") || (a == "m3" && b == "m2"))
                return thicknessMm.HasValue && thicknessMm.Value > 0;
            return false;
        }
    }
}
