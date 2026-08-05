// Distribution-system rules — pure, Revit-free, so they are unit-testable.
//
// Split out of ElecSystemAuthoring.cs / ElecSettings.cs for the same reason
// ElecFindings and CircuitGrouping were split out of their tools: the decisions
// worth pinning (which phase/wire pairs Revit can serve, what a Malaysian
// project should get when the agent has to invent values, how a voltage band is
// derived) must not need a live Document to test. Tests.csproj links this file;
// it must stay free of Autodesk.Revit references.
//
// VOLTAGES ARE IN VOLTS throughout, matching VoltageType's own unit — the
// Revit-side caller does no conversion for these.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>A distribution system described without any Revit type, so the
    /// defaults table can be asserted in a unit test. The authoring tool maps
    /// PhaseConfig to ElectricalPhaseConfiguration and Phases to
    /// ElectricalPhase at the API boundary.</summary>
    public sealed class DistSystemSpec
    {
        public string Name = "";
        /// <summary>1 or 3.</summary>
        public int Phases;
        /// <summary>"wye" | "delta" | "undefined".</summary>
        public string PhaseConfig = "undefined";
        public int Wires;
        public double? VoltageLineToGroundV;
        /// <summary>Null for a single-phase system, which has no line-to-line
        /// voltage.</summary>
        public double? VoltageLineToLineV;
    }

    public static class ElecSystemRules
    {
        // Malaysian LV practice (TNB / MS IEC 60364): 240 V line-to-neutral,
        // 415 V line-to-line, TN-S. Same standards family the sizing rules in
        // WireSizing.cs already follow — a JKR project circuited against a US
        // 120/208 template produces breaker, cable and voltage-drop numbers
        // that are all wrong, which is what happened in UAT 2026-08-04.
        public const double MalaysianLineToGroundV = 240.0;
        public const double MalaysianLineToLineV = 415.0;

        public const string ThreePhaseSystemName = "415/240 Wye";
        public const string SinglePhaseSystemName = "240 V Single";

        /// <summary>The default a project gets when the agent must author a
        /// system and the caller named no values. Three-phase four-wire is the
        /// one that serves both a 415 V distribution board and its 240 V
        /// single-phase branch circuits, so it is the useful default.</summary>
        public static DistSystemSpec MalaysianThreePhase() => new DistSystemSpec
        {
            Name = ThreePhaseSystemName,
            Phases = 3,
            PhaseConfig = "wye",
            Wires = 4,
            VoltageLineToGroundV = MalaysianLineToGroundV,
            VoltageLineToLineV = MalaysianLineToLineV,
        };

        /// <summary>For a project with only single-phase boards.</summary>
        public static DistSystemSpec MalaysianSinglePhase() => new DistSystemSpec
        {
            Name = SinglePhaseSystemName,
            Phases = 1,
            PhaseConfig = "undefined",
            Wires = 2,
            VoltageLineToGroundV = MalaysianLineToGroundV,
            VoltageLineToLineV = null,
        };

        /// <summary>Phase/wire pairs Revit will serve. A single-phase system is
        /// 2-wire (line + neutral) or 3-wire (split phase); a three-phase one is
        /// 3-wire (delta, no neutral) or 4-wire (wye with neutral). Anything
        /// else matches no panel however its voltage is set.
        /// Returns null when the pair is acceptable, else the reason.</summary>
        public static string? ValidatePhaseWire(int? phases, int? wires)
        {
            if (!phases.HasValue && !wires.HasValue) return null;

            if (phases.HasValue && phases.Value != 1 && phases.Value != 3)
                return "number_of_phases must be 1 or 3 (got " + phases.Value +
                       ") — Revit has no other phase count";

            if (wires.HasValue && (wires.Value < 2 || wires.Value > 4))
                return "number_of_wires must be 2, 3 or 4 (got " + wires.Value + ")";

            if (!phases.HasValue || !wires.HasValue) return null;

            var allowed = AllowedWires(phases.Value);
            if (!allowed.Contains(wires.Value))
                return "a " + phases.Value + "-phase connector cannot have " + wires.Value +
                       " wires — allowed: " + string.Join(" or ", allowed) +
                       ". A pair Revit cannot serve matches no distribution system, " +
                       "so writing it would reload the family for nothing.";

            return null;
        }

        /// <summary>Wire counts valid for a phase count. Public so the error
        /// text and the test read from one place.</summary>
        public static IReadOnlyList<int> AllowedWires(int phases) =>
            phases == 3 ? new[] { 3, 4 } : new[] { 2, 3 };

        /// <summary>Wire count Revit expects for a phase/config pair, used when
        /// the caller named a config but no wire count.</summary>
        public static int DefaultWires(int phases, string? phaseConfig) =>
            phases == 3
                ? (IsDelta(phaseConfig) ? 3 : 4)
                : 2;

        public static bool IsDelta(string? phaseConfig) =>
            string.Equals(phaseConfig, "delta", StringComparison.OrdinalIgnoreCase);

        /// <summary>Accepts the spellings an agent is likely to emit and
        /// normalises to the three the authoring tool understands. Returns null
        /// on an unrecognised value so the caller can refuse with the input
        /// echoed back.</summary>
        public static string? NormalisePhaseConfig(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            switch (raw!.Trim().ToLowerInvariant())
            {
                case "wye":
                case "star":
                case "y":
                    return "wye";
                case "delta":
                case "d":
                    return "delta";
                case "undefined":
                case "none":
                case "single":
                    return "undefined";
                default:
                    return null;
            }
        }

        /// <summary>Min/max band for a voltage definition. Revit matches a
        /// connector's voltage to a definition by falling inside this band, so
        /// it must be wide enough to absorb supply tolerance and narrow enough
        /// that 240 and 415 never overlap.
        ///
        /// The two Malaysian voltages get the statutory-ish bands used on JKR
        /// drawings; anything else gets a symmetric 5%, which is the safe
        /// generic choice rather than a claim about that supply.</summary>
        public static (double Min, double Max) VoltageBand(double actualV)
        {
            if (Near(actualV, MalaysianLineToGroundV)) return (220.0, 250.0);
            if (Near(actualV, MalaysianLineToLineV)) return (400.0, 430.0);
            return (Math.Round(actualV * 0.95, 1), Math.Round(actualV * 1.05, 1));
        }

        /// <summary>Voltage definitions are matched by value, not name, so a
        /// project that already defines 240 V under some other name is reused
        /// rather than duplicated. One volt is well below the gap between any
        /// two real supply voltages.</summary>
        public static bool Near(double a, double b) => Math.Abs(a - b) < 1.0;

        /// <summary>Default name for a voltage definition the tool has to
        /// create. Matches the "240 V" convention already in JKR templates.</summary>
        public static string VoltageName(double actualV) =>
            actualV.ToString("0.#", CultureInfo.InvariantCulture) + " V";
    }
}
