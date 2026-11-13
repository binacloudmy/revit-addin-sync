#nullable enable
using System;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>AI-330: FittingWatch reads every MEP curve and fitting in the document, so the engine takes
    /// that snapshot only before a call that can change a curve's type. Revit-free: decided from the tool
    /// name and the plan the call executes.</summary>
    public static class FittingWatchPolicy
    {
        /// <summary>False only for a set_parameters plan that writes no element reference. An element's type
        /// is held in ElementId parameters ("Type", "Family and Type" in any Revit language), and
        /// set_parameters writes exactly the plan's intended values, so a plan of text, numbers and yes/no
        /// cannot re-type a curve. Every other tool is watched: run_revit_code runs arbitrary code, and a
        /// tool this does not know may do anything.</summary>
        public static bool ShouldWatch(string? tool, WritePlan? plan)
        {
            if (!string.Equals(tool, ToolNames.SetParameters, StringComparison.Ordinal)) return true;
            if (plan?.IntendedValues == null) return true;
            return plan.IntendedValues.Values.Any(canonical =>
            {
                var v = ParsedValue.FromCanonical(canonical);
                return !v.Ok || v.Kind == ValueKind.ElementId;
            });
        }
    }
}
