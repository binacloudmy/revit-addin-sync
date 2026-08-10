// Why a panel is refusing a circuit — pure, Revit-free, linked into Tests.
//
// THE FAILURE THIS EXISTS TO STOP. Revit's refusal to seat a circuit on a
// panel NAMES THE PANEL, whatever the real cause. An agent reads that as
// "wrong panel", creates another one, gets the same message, deletes it and
// tries again. Observed in UAT 2026-08-07: the copilot spawned and deleted a
// string of DBs while the actual cause sat on the circuit and the project
// settings. Both electrical recipes already said in prose "never place,
// delete or swap a panel to work around this" and it happened anyway — prose
// in a recipe does not survive a tool result that says otherwise.
//
// So the diagnosis belongs in the RESULT, next to the failure, naming the
// cause and the tool that fixes it. `DoNotCreatePanel` is emitted on every
// unhappy path precisely because the wrong reflex is so strong.
//
// Nothing here decides whether Revit will accept an assignment — no
// IsCircuitValidForPanel exists in 2023/2025/2027. It reports what is
// checkable, and PanelTools attaches it to whatever Revit actually did.
//
// UNITS: volts and VA. Slots are 1-based counts.
using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>Everything readable about a circuit/panel pair before the
    /// assignment is attempted. Nulls mean "could not be read", which is
    /// itself a finding — never silently treated as a passing value.</summary>
    public sealed class AssignmentFacts
    {
        public bool PanelExists = true;
        public bool PanelIsElectricalEquipment = true;
        /// <summary>Null or empty = the panel has no distribution system, and
        /// a panel without one accepts NO circuit at all.</summary>
        public string? DistributionSystem;
        public double? CircuitVoltageV;
        public double? SystemLineToGroundV;
        public double? SystemLineToLineV;
        public int? CircuitPoles;
        public int PanelPhaseCount = 1;
        public int TotalSlots;
        public int UsedSlots;
    }

    public sealed class AssignmentBlocker
    {
        public string Code = "";
        public string Detail = "";
        /// <summary>The tool that actually fixes it. Never "try another
        /// panel".</summary>
        public string Fix = "";
    }

    public static class PanelAssignmentDiagnosis
    {
        /// <summary>Circuit voltage is matched against the distribution
        /// system's line-to-ground and line-to-line values with this much
        /// slack. Deliberately loose: a false "mismatch" would block a
        /// legitimate assignment, which is worse than missing one. It exists
        /// to catch 240-on-a-415-only board and similar, not to police
        /// tolerances.</summary>
        public const double VoltageTolerance = 0.20;

        /// <summary>Blockers that make the assignment CERTAIN to fail, cheapest
        /// and most common cause first. An empty list does not promise success
        /// — see the file header.</summary>
        public static List<AssignmentBlocker> Hard(AssignmentFacts f)
        {
            var found = new List<AssignmentBlocker>();
            if (f == null) return found;

            if (!f.PanelExists)
            {
                Add(found, "panel_not_found",
                    "no element with that id exists in the model",
                    "resolve the real board's id with find_mep_elements (category Electrical "
                    + "Equipment), then retry with that id.");
                return found;   // nothing else is meaningful without a panel
            }

            if (!f.PanelIsElectricalEquipment)
            {
                Add(found, "panel_not_equipment",
                    "that element is not an Electrical Equipment family, so it cannot host circuits",
                    "pick the real board with find_mep_elements. A socket or a tag is not a panel.");
                return found;
            }

            if (string.IsNullOrWhiteSpace(f.DistributionSystem))
                Add(found, "panel_no_distribution_system",
                    "the panel has NO distribution system, and a panel without one accepts no circuit "
                    + "at all. This is a project setting, not a property of the panel family, and it is "
                    + "the single most common cause of this refusal.",
                    "list_electrical_settings to see what exists (count 0 means the model defines none "
                    + "— create_distribution_system), then set_distribution_system on this panel.");

            if (f.CircuitVoltageV == null)
                Add(found, "circuit_voltage_unreadable",
                    "the circuit reports no voltage. Revit builds circuit voltage from the DEVICE "
                    + "connectors, so this means the family carries none — create_circuit lets that "
                    + "through as 'unknown' and the panel assignment is where it surfaces.",
                    "get_connector_electrical_data on a member device, then "
                    + "set_connector_electrical_data on its family.");
            else if (Math.Abs(f.CircuitVoltageV.Value) < 1e-9)
                Add(found, "circuit_zero_voltage",
                    "the circuit is 0 V. No panel will accept it, and every panel will report the "
                    + "same thing.",
                    "get_connector_electrical_data on a member device, then "
                    + "set_connector_electrical_data on its family.");
            else if (HasSystemVoltage(f) && !VoltageFits(f))
                Add(found, "voltage_mismatch",
                    $"the circuit is {Round(f.CircuitVoltageV)} V at {f.CircuitPoles?.ToString() ?? "?"} "
                    + $"pole(s), but the panel's distribution system '{f.DistributionSystem}' runs at "
                    + $"{Describe(f.SystemLineToGroundV, f.SystemLineToLineV)}. A 1-pole breaker sits "
                    + "line-to-ground and a 2- or 3-pole breaker sits line-to-line, so the circuit "
                    + "voltage has to equal one of those two numbers.",
                    "read `resolution` on this result — it lists the distribution systems that WOULD "
                    + "seat this circuit, the pole count that would make it fit the current one, and "
                    + "the exact create_distribution_system call when the model has nothing suitable.");

            if (f.CircuitPoles is 3 && f.PanelPhaseCount == 1)
                Add(found, "pole_mismatch",
                    "a 3-pole circuit cannot sit on a single-phase panel",
                    "put it on a three-phase board, or correct the device poles with "
                    + "set_connector_electrical_data.");

            if (f.TotalSlots > 0 && f.UsedSlots >= f.TotalSlots)
                Add(found, "panel_full",
                    $"the panel has {f.UsedSlots} of {f.TotalSlots} slots used and no free way. Note "
                    + "an EMPTY circuit still holds its breaker slot, so a board can read full while "
                    + "feeding nothing.",
                    "free a slot with delete_circuit on a circuit that feeds nothing, or choose "
                    + "another EXISTING board. This is the one cause where a new panel is a genuine "
                    + "answer — offer it and let the drafter decide, rather than placing one.");

            return found;
        }

        /// <summary>Put in front of the agent on ANY failed assignment,
        /// including one whose cause could not be named.
        ///
        /// This deliberately does NOT say "do not create a panel". An earlier
        /// version did, and a prohibition with no alternative just leaves the
        /// agent stuck — it looped anyway and then handed the drafter manual
        /// UI steps. What breaks the loop is a next action, so this states the
        /// one fact the agent is missing (the error names the panel regardless
        /// of cause) and points at the computed resolution.</summary>
        public const string PanelNamedRegardlessAdvice =
            "Revit names the PANEL in this error whatever the real cause is, so a panel mismatch is "
            + "what it looks like even when the panel is fine. The cause is almost always the "
            + "distribution system on the panel or the voltage/poles on the device family. "
            + "`resolution` on this result says which distribution systems would seat this circuit, "
            + "what pole count would fit the one already assigned, and what to create if neither. "
            + "Act on that; a different panel inherits the same mismatch.";

        private static bool HasSystemVoltage(AssignmentFacts f) =>
            f.SystemLineToGroundV is > 0 || f.SystemLineToLineV is > 0;

        private static bool VoltageFits(AssignmentFacts f)
        {
            var v = f.CircuitVoltageV;
            if (v == null) return false;
            return Near(v.Value, f.SystemLineToGroundV) || Near(v.Value, f.SystemLineToLineV);
        }

        private static bool Near(double v, double? target)
        {
            if (target is not > 0) return false;
            return Math.Abs(v - target.Value) <= target.Value * VoltageTolerance;
        }

        private static string Describe(double? lineToGround, double? lineToLine)
        {
            var parts = new List<string>();
            if (lineToGround is > 0) parts.Add($"{Round(lineToGround)} V line-to-ground");
            if (lineToLine is > 0) parts.Add($"{Round(lineToLine)} V line-to-line");
            return parts.Count == 0 ? "an unreadable voltage" : string.Join(" / ", parts);
        }

        private static string Round(double? v) =>
            v == null ? "?" : Math.Round(v.Value, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static void Add(List<AssignmentBlocker> list, string code, string detail, string fix) =>
            list.Add(new AssignmentBlocker { Code = code, Detail = detail, Fix = fix });
    }
}
