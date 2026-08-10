// Which distribution system will actually seat this circuit — pure,
// Revit-free, linked into Tests.
//
// WHY THIS REPLACED A GUARDRAIL. The first attempt at the panel-assignment
// problem shipped a `do_not_create_panel` flag and prose telling the agent not
// to loop. That is not a fix: it forbids the wrong action without supplying
// the right one, so the agent is left with a refusal and no next step. It
// looped anyway, then told the drafter to go and do it by hand in the Revit
// UI.
//
// The actual question behind "panel and circuit do not match" is arithmetic,
// and Revit exposes everything needed to answer it:
//
//   * a 1-pole breaker sits LINE-TO-GROUND
//   * a 2- or 3-pole breaker sits LINE-TO-LINE
//
// So a 240 V 1-pole circuit cannot seat on a "120/240 Single" board, where a
// 1-pole slot is 120 V — but the SAME circuit at 2 poles seats fine at 240 V
// line-to-line, and the same circuit at 1 pole seats fine on a Malaysian
// 240/415 board where line-to-ground IS 240 V. That is the answer the agent
// needed, and none of it is a prohibition.
//
// This file computes: which existing systems work, what to change if none do,
// and the exact system to create when the model has nothing suitable. The
// caller turns that into a result the agent can act on.
//
// UNITS: volts throughout.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One distribution system as the matcher sees it. Min/Max come
    /// from Revit's own VoltageType range — that range IS the tolerance, so
    /// nothing here invents a percentage.</summary>
    public sealed class DistSysOption
    {
        public long Id;
        public string Name = "";
        public bool ThreePhase;
        public double? LineToGroundV;
        public double? LineToGroundMinV;
        public double? LineToGroundMaxV;
        public double? LineToLineV;
        public double? LineToLineMinV;
        public double? LineToLineMaxV;
        /// <summary>ElectricalEquipment.IsValidDistributionSystem for the
        /// target panel — Revit's own answer, not a guess.</summary>
        public bool PanelAccepts;
    }

    /// <summary>What the circuit needs seated, plus what the PANEL is
    /// physically able to accept.</summary>
    public sealed class CircuitDemand
    {
        public double? VoltageV;
        public int Poles = 1;

        /// <summary>Poles on the panel's own incoming connector. A three-phase
        /// distribution system needs a 3-pole panel connector, and that count
        /// is authored into the family — no tool can change it on a placed
        /// instance. Suggesting a three-phase system to a 1-pole board sends
        /// the drafter to the Family Editor for nothing, which is exactly what
        /// happened on 2026-08-07. Null = unknown, in which case no assumption
        /// is made either way.</summary>
        public int? PanelConnectorPoles;

        /// <summary>False when the panel connector is known to be 1-pole.</summary>
        public bool PanelCanTakeThreePhase => PanelConnectorPoles is not (1 or 2);

        /// <summary>Supply convention to PROPOSE when the circuit itself has no
        /// usable voltage — the caller's project default (Malaysian LV: 240 V,
        /// 1 pole). Null = no convention offered; the matcher then falls back
        /// to 240/1 itself. Values derived from these are always flagged
        /// is_proposal, never applied silently.</summary>
        public double? DefaultVoltageV;
        public int? DefaultPoles;
    }

    /// <summary>A system that would work, and how.</summary>
    public sealed class SystemFit
    {
        public long Id;
        public string Name = "";
        /// <summary>Pole count at which this circuit seats on this system.</summary>
        public int PolesRequired;
        /// <summary>True when it seats at the circuit's CURRENT pole count —
        /// i.e. assignable with no further change.</summary>
        public bool FitsAsIs;
        public double? SeatVoltageV;
    }

    /// <summary>The spec for a system that does not exist yet but would solve
    /// this. Maps 1:1 onto create_distribution_system's arguments.</summary>
    public sealed class SystemToCreate
    {
        public string Name = "";
        public string Phase = "single";
        public double? LineToGroundV;
        public double? LineToLineV;
    }

    /// <summary>The connector write that unblocks a voltage-less circuit. Maps
    /// 1:1 onto set_connector_electrical_data's arguments. The values come
    /// from a supply CONVENTION, not a measurement — IsProposal stays true so
    /// the caller shows them for confirmation (dry_run first) instead of
    /// applying them as fact.</summary>
    public sealed class ConnectorFix
    {
        public string Tool = "set_connector_electrical_data";
        public double VoltageV;
        public int Poles = 1;
        public string SystemType = "power_unbalanced";
        public bool IsProposal = true;
    }

    public sealed class MatchResult
    {
        /// <summary>Panel accepts it AND the circuit seats at its current pole
        /// count. Assign one of these and the job is done.</summary>
        public List<SystemFit> AssignableNow { get; } = new List<SystemFit>();
        /// <summary>Panel accepts it and the circuit would seat at a DIFFERENT
        /// pole count. The fix is the device family's poles, not the panel.</summary>
        public List<SystemFit> FitsWithPoleChange { get; } = new List<SystemFit>();
        /// <summary>Systems in the model the panel itself will not take.</summary>
        public List<string> PanelRejects { get; } = new List<string>();
        /// <summary>Set when nothing existing can work.</summary>
        public SystemToCreate? Create;
        /// <summary>Set when the circuit has no usable voltage: the exact
        /// set_connector_electrical_data call (a proposal from the supply
        /// convention) that makes matching possible at all.</summary>
        public ConnectorFix? FixConnector;
        public string Summary = "";
    }

    public static class DistributionSystemMatch
    {
        /// <summary>Used only when a VoltageType exposes no min/max range.
        /// Revit's own range is preferred wherever it is readable.</summary>
        public const double FallbackTolerance = 0.10;

        /// <summary>A 1-pole breaker sits line-to-ground; 2 and 3 poles sit
        /// line-to-line. This single rule is what the whole mismatch reduces
        /// to.</summary>
        public static bool IsLineToLine(int poles) => poles >= 2;

        // Standard three-phase nominal pairs, line-to-ground -> line-to-line.
        // These are CONVENTIONS, not arithmetic: 240 x sqrt(3) is 415.69, and
        // naming a system "240/416V" would be wrong everywhere it is quoted —
        // the Malaysian/IEC pairing is 240/415. sqrt(3) is only the fallback
        // for a voltage not on this list.
        private static readonly Dictionary<int, int> NominalThreePhasePairs =
            new Dictionary<int, int>
            {
                [110] = 190,
                [120] = 208,
                [127] = 220,
                [220] = 380,
                [230] = 400,
                [240] = 415,
                [277] = 480,
                [347] = 600,
            };

        /// <summary>Line-to-line partner for a nominal line-to-ground voltage.
        /// Table first, root 3 only when the value is not a standard one.</summary>
        public static double LineToLineFor(double lineToGroundV)
        {
            var key = (int)Math.Round(lineToGroundV, 0);
            if (NominalThreePhasePairs.TryGetValue(key, out var pair)) return pair;
            return Math.Round(lineToGroundV * Math.Sqrt(3), 0);
        }

        /// <summary>Line-to-ground partner for a nominal line-to-line voltage —
        /// the same table read backwards.</summary>
        public static double LineToGroundFor(double lineToLineV)
        {
            var key = (int)Math.Round(lineToLineV, 0);
            foreach (var kv in NominalThreePhasePairs)
                if (kv.Value == key) return kv.Key;
            return Math.Round(lineToLineV / Math.Sqrt(3), 0);
        }

        /// <summary>Pole count at which <paramref name="volts"/> seats on this
        /// system, or null when it seats at none.</summary>
        public static int? PolesFor(DistSysOption o, double volts)
        {
            if (o == null || volts <= 0) return null;
            if (InRange(volts, o.LineToGroundV, o.LineToGroundMinV, o.LineToGroundMaxV)) return 1;
            if (InRange(volts, o.LineToLineV, o.LineToLineMinV, o.LineToLineMaxV))
                return o.ThreePhase ? 3 : 2;
            return null;
        }

        public static MatchResult Solve(CircuitDemand demand, IEnumerable<DistSysOption> options)
        {
            var result = new MatchResult();
            var all = (options ?? Enumerable.Empty<DistSysOption>()).Where(o => o != null).ToList();
            var volts = demand?.VoltageV ?? 0;
            var poles = Math.Max(1, demand?.Poles ?? 1);
            // Unknown panel poles means no assumption: only a KNOWN 1- or
            // 2-pole panel connector rules three phase out.
            var canThreePhase = demand?.PanelCanTakeThreePhase ?? true;

            if (volts <= 0)
            {
                // The loop trigger this file exists to close: at exactly the
                // moment the JKR 0 V case bites, prose alone left the agent
                // with no computed next action (observed 2026-08-07). Return
                // the fix as arguments — the convention voltage, the connector
                // write, AND the system that seats it afterwards.
                var fixVolts = demand?.DefaultVoltageV is > 0 ? demand.DefaultVoltageV.Value : 240;
                var fixPoles = demand?.DefaultPoles is >= 1 and <= 3 ? demand.DefaultPoles.Value : 1;
                result.FixConnector = new ConnectorFix
                {
                    VoltageV = fixVolts,
                    Poles = fixPoles,
                    SystemType = fixPoles >= 3 ? "power_balanced" : "power_unbalanced",
                };
                result.Create = Suggest(fixVolts, fixPoles, canThreePhase);
                result.Summary =
                    "the circuit reports no usable voltage, so no distribution system can be "
                    + "matched to it — the panel is not the problem. The computed route: 1) "
                    + $"set_connector_electrical_data with the args in `fix_connector` ({Fmt(fixVolts)} V "
                    + $"{fixPoles}-pole is a supply CONVENTION — confirm it with the drafter, dry_run "
                    + "first), then 2) the system in `create` seats the circuit (skip creating it if "
                    + "an equivalent already exists).";
                result.Summary += SinglePhaseOnlyNote(canThreePhase, demand?.PanelConnectorPoles);
                return result;
            }

            foreach (var o in all)
            {
                if (!o.PanelAccepts) { result.PanelRejects.Add(o.Name); continue; }
                var needed = PolesFor(o, volts);
                if (needed == null) continue;

                var fit = new SystemFit
                {
                    Id = o.Id,
                    Name = o.Name,
                    PolesRequired = needed.Value,
                    FitsAsIs = needed.Value == poles,
                    SeatVoltageV = needed.Value == 1 ? o.LineToGroundV : o.LineToLineV,
                };
                if (fit.FitsAsIs) result.AssignableNow.Add(fit);
                else result.FitsWithPoleChange.Add(fit);
            }

            if (result.AssignableNow.Count > 0)
            {
                var names = string.Join(", ", result.AssignableNow.Select(f => $"'{f.Name}'"));
                result.Summary =
                    $"the panel takes {names} and a {Fmt(volts)} V {poles}-pole circuit seats on "
                    + "it as-is. set_distribution_system on the panel, then assign_panel.";
                return result;
            }

            if (result.FitsWithPoleChange.Count > 0)
            {
                var f = result.FitsWithPoleChange[0];
                result.Summary =
                    $"a {Fmt(volts)} V circuit DOES seat on '{f.Name}', but at {f.PolesRequired} poles, "
                    + $"not the {poles} it currently has. A 1-pole breaker sits line-to-ground and a "
                    + "2- or 3-pole breaker sits line-to-line, so this is a pole count question, not a "
                    + "panel question. Either set poles=" + f.PolesRequired + " on the device family "
                    + "with set_connector_electrical_data, or use a system whose line-to-ground "
                    + $"voltage is {Fmt(volts)} V — create_distribution_system can make one.";
                result.Create = Suggest(volts, poles, canThreePhase);
                result.Summary += SinglePhaseOnlyNote(canThreePhase, demand?.PanelConnectorPoles);
                return result;
            }

            result.Create = Suggest(volts, poles, canThreePhase);
            result.Summary = all.Count == 0
                ? "this model defines no distribution systems at all, which is why every circuit is "
                  + "refused. create_distribution_system, then set_distribution_system on the panel."
                : $"none of the {all.Count} distribution system(s) in this model can seat a "
                  + $"{Fmt(volts)} V {poles}-pole circuit on this panel. create_distribution_system "
                  + "with the spec in `create`, then set_distribution_system on the panel.";
            result.Summary += SinglePhaseOnlyNote(canThreePhase, demand?.PanelConnectorPoles);
            return result;
        }

        /// <summary>Said on EVERY path that offers a system to create, because
        /// the drafter needs it before being sent to the Family Editor to make
        /// a perfectly good panel fit a system it can never take.</summary>
        private static string SinglePhaseOnlyNote(bool canThreePhase, int? panelPoles)
        {
            if (canThreePhase) return "";
            return $" NOTE: this panel's own connector is {panelPoles}-pole, so it cannot take a "
                 + "THREE-PHASE distribution system at all — that pole count is authored into the "
                 + "family and no tool can change it on a placed instance. The spec in `create` is "
                 + "single-phase for that reason. Only re-author the family if the board is "
                 + "genuinely three-phase by design.";
        }

        /// <summary>Overload kept for callers with no panel information. Assumes
        /// the panel can take three phase — only safe when nothing is known.</summary>
        public static SystemToCreate Suggest(double volts, int poles) =>
            Suggest(volts, poles, true);

        /// <summary>A system that would seat this circuit at its CURRENT pole
        /// count, on THIS panel — so the drafter is never told to re-author a
        /// family they just fixed.
        ///
        /// <paramref name="panelCanTakeThreePhase"/> is the part that was
        /// missing. A three-phase system requires a 3-pole panel connector,
        /// authored into the family and unchangeable on a placed instance. A
        /// 1-pole board takes a SINGLE-PHASE system, where the circuit voltage
        /// is line-to-ground and there is no line-to-line at all.</summary>
        public static SystemToCreate Suggest(double volts, int poles, bool panelCanTakeThreePhase)
        {
            if (IsLineToLine(poles))
            {
                // 3-pole is line-to-line on a three-phase system; 2-pole is
                // line-to-line on a split single-phase one, where the two legs
                // are simply half.
                var ltg = poles >= 3 ? LineToGroundFor(volts) : Math.Round(volts / 2, 0);
                return new SystemToCreate
                {
                    Name = $"{Fmt(ltg)}/{Fmt(volts)}V {(poles >= 3 ? "Three" : "Single")} Phase",
                    Phase = poles >= 3 ? "three" : "single",
                    LineToGroundV = ltg,
                    LineToLineV = volts,
                };
            }

            // 1-pole: the circuit voltage IS line-to-ground.
            if (!panelCanTakeThreePhase)
                // A 1-pole board cannot take a three-phase system no matter how
                // convenient it would be. Single phase, line-to-ground only.
                return new SystemToCreate
                {
                    Name = $"{Fmt(volts)}V Single Phase",
                    Phase = "single",
                    LineToGroundV = volts,
                    LineToLineV = null,
                };

            // Otherwise offer the matching three-phase line-to-line alongside
            // it (240 -> 415 for Malaysia), because a three-phase board still
            // seats 1-pole loads and covers future three-phase ones.
            var ltl = LineToLineFor(volts);
            return new SystemToCreate
            {
                Name = $"{Fmt(volts)}/{Fmt(ltl)}V Three Phase",
                Phase = "three",
                LineToGroundV = volts,
                LineToLineV = ltl,
            };
        }

        private static bool InRange(double v, double? actual, double? min, double? max)
        {
            if (min is > 0 && max is > 0) return v >= min.Value && v <= max.Value;
            if (actual is not > 0) return false;
            return Math.Abs(v - actual.Value) <= actual.Value * FallbackTolerance;
        }

        private static string Fmt(double v) =>
            Math.Round(v, 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
