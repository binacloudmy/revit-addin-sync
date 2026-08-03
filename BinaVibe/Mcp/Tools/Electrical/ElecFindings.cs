// Validation finding model + pure rule evaluators — Revit-free.
//
// Shared by all three electrical validators (ElecValidation.cs gathers facts
// from the model and hands numbers here). Every threshold arrives from the
// caller; a rule whose numbers were not supplied is reported "skipped", never
// silently passed — silence must never be ambiguous.
//
// Wire shape per finding: {check, status, elements, reason, value, limit,
// unit} — structured pass/fail with reasons, so the agent can relay
// actionable feedback rather than a boolean.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One validation outcome.</summary>
    public sealed class Finding
    {
        public string Check = "";
        /// <summary>"pass" | "fail" | "warning" | "skipped"</summary>
        public string Status = "pass";
        public List<long> Elements = new();
        public string Reason = "";
        public double? Value;
        public double? Limit;
        public string Unit = "";

        public Dictionary<string, object?> ToDict() => new()
        {
            ["check"] = Check,
            ["status"] = Status,
            ["elements"] = Elements.Cast<object>().ToList(),
            ["reason"] = Reason,
            ["value"] = Value,
            ["limit"] = Limit,
            ["unit"] = Unit,
        };

        public static Finding Skipped(string check, string reason) => new()
        {
            Check = check, Status = "skipped", Reason = reason,
        };

        /// <summary>Roll counts for the wire: {pass, fail, warning, skipped}.</summary>
        public static Dictionary<string, object?> Counts(IReadOnlyList<Finding> findings) => new()
        {
            ["pass"] = findings.Count(f => f.Status == "pass"),
            ["fail"] = findings.Count(f => f.Status == "fail"),
            ["warning"] = findings.Count(f => f.Status == "warning"),
            ["skipped"] = findings.Count(f => f.Status == "skipped"),
        };
    }

    public static class ElecFindings
    {
        /// <summary>Circuit load vs breaker capacity. maxLoadRatio (e.g. 0.8)
        /// comes from the caller.</summary>
        public static Finding LoadVsBreaker(
            long circuitId, double loadVa, double voltageV, double breakerA,
            double maxLoadRatio, bool threePhase)
        {
            double capacityVa = threePhase
                ? breakerA * voltageV * 3.0
                : breakerA * voltageV;
            double limitVa = capacityVa * maxLoadRatio;
            var f = new Finding
            {
                Check = "load_vs_breaker",
                Elements = { circuitId },
                Value = Math.Round(loadVa),
                Limit = Math.Round(limitVa),
                Unit = "VA",
            };
            if (loadVa > limitVa)
            {
                f.Status = "fail";
                f.Reason = "connected load " + Math.Round(loadVa) + " VA exceeds " +
                           (maxLoadRatio * 100) + "% of breaker capacity (" +
                           breakerA + " A -> " + Math.Round(limitVa) + " VA)";
            }
            else
            {
                f.Reason = "load within " + (maxLoadRatio * 100) + "% of breaker capacity";
            }
            return f;
        }

        /// <summary>Voltage drop over a run vs the caller's limit.</summary>
        public static Finding VoltageDrop(
            long circuitId, double amps, double mvPerAM, double lengthMm,
            double voltageV, double limitPct, bool threePhase, string lengthSource)
        {
            double pct = WireSizing.VoltageDropPct(amps, mvPerAM, lengthMm, voltageV, threePhase);
            var f = new Finding
            {
                Check = "voltage_drop",
                Elements = { circuitId },
                Value = Math.Round(pct, 2),
                Limit = limitPct,
                Unit = "%",
            };
            string basis = " over " + Math.Round(lengthMm / 1000.0, 1) + " m (length source: " + lengthSource + ")";
            if (pct > limitPct)
            {
                f.Status = "fail";
                f.Reason = "voltage drop " + Math.Round(pct, 2) + "% exceeds " + limitPct + "%" + basis;
            }
            else
            {
                f.Reason = "voltage drop " + Math.Round(pct, 2) + "% within " + limitPct + "%" + basis;
            }
            return f;
        }

        /// <summary>Receptacle spacing along one wall run: any gap between
        /// consecutive stations (or run ends) beyond the max is a fail.
        /// Stations are mm from the run start; need not be pre-sorted.</summary>
        public static Finding ReceptacleSpacing(
            string runKey, IReadOnlyList<double> stationsMm, double wallLengthMm,
            double maxSpacingMm, IReadOnlyList<long>? elementIds = null)
        {
            var f = new Finding
            {
                Check = "receptacle_spacing",
                Limit = maxSpacingMm,
                Unit = "mm",
            };
            if (elementIds != null) f.Elements.AddRange(elementIds);

            if (stationsMm == null || stationsMm.Count == 0)
            {
                if (wallLengthMm > maxSpacingMm)
                {
                    f.Status = "fail";
                    f.Value = Math.Round(wallLengthMm);
                    f.Reason = "wall run " + runKey + " (" + Math.Round(wallLengthMm) +
                               " mm) has no receptacle at all";
                }
                else
                {
                    f.Reason = "run " + runKey + " shorter than max spacing — no receptacle required";
                }
                return f;
            }

            var sorted = stationsMm.OrderBy(s => s).ToList();
            double worst = sorted[0];                          // gap from run start
            for (int i = 1; i < sorted.Count; i++)
                worst = Math.Max(worst, sorted[i] - sorted[i - 1]);
            worst = Math.Max(worst, wallLengthMm - sorted[sorted.Count - 1]);

            f.Value = Math.Round(worst);
            if (worst > maxSpacingMm)
            {
                f.Status = "fail";
                f.Reason = "largest gap on run " + runKey + " is " + Math.Round(worst) +
                           " mm, over the " + maxSpacingMm + " mm maximum";
            }
            else
            {
                f.Reason = "spacing on run " + runKey + " within maximum";
            }
            return f;
        }

        /// <summary>Two circuits claiming the same breaker slot on one panel.
        /// Input rows: (circuitElementId, panelId, slotLabel). Slot labels are
        /// compared verbatim per panel.</summary>
        public static List<Finding> DoubleAssignedSlots(
            IReadOnlyList<(long CircuitId, long PanelId, string Slot)> rows)
        {
            var findings = new List<Finding>();
            foreach (var g in rows
                         .Where(r => !string.IsNullOrWhiteSpace(r.Slot))
                         .GroupBy(r => (r.PanelId, r.Slot))
                         .Where(g => g.Count() > 1)
                         .OrderBy(g => g.Key.PanelId).ThenBy(g => g.Key.Slot, StringComparer.Ordinal))
            {
                var f = new Finding
                {
                    Check = "double_assigned_slot",
                    Status = "fail",
                    Reason = "slot " + g.Key.Slot + " on panel " + g.Key.PanelId +
                             " is claimed by " + g.Count() + " circuits",
                };
                f.Elements.AddRange(g.Select(r => r.CircuitId).OrderBy(id => id));
                findings.Add(f);
            }
            return findings;
        }
    }
}
