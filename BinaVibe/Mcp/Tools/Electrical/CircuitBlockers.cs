// The two dead ends suggest_circuits can hit, and the DTOs they read.
//
// Both return ok:TRUE with a structured blocker. ok:false is the agent's
// self-heal-retry signal, and neither "no usable panel" nor "every device is
// already circuited" is fixable by retrying — UAT 2026-08-04 watched the agent
// place and delete distribution boards in a loop because the second said
// ok:false. A blocker carries the ids the next call needs.
//
// Revit-free on purpose: only the SHAPE lives here, so the rule above is
// unit-testable rather than UAT-testable.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One Electrical Equipment instance, classified usable (has a
    /// distribution system) or skipped-with-reason.</summary>
    internal sealed class PanelFacts
    {
        public PanelInfo Info = new();
        public double XMm, YMm, ZMm;
        public string DistSystem = "";
        public bool Usable;
        public string SkipReason = "";
    }

    /// <summary>A device that was skipped because it is ALREADY on a power
    /// circuit, carried with the circuit that owns it. Collection used to
    /// resolve this and throw it away, which is why the response could only say
    /// "already_circuited" and never which circuit.</summary>
    internal sealed class CircuitedDevice
    {
        public long DeviceId;
        public long CircuitId;
        public string CircuitNumber = "";
        public long? PanelId;
        public string PanelName = "";
    }

    internal static class CircuitBlockers
    {
        /// <summary>Rows for the panels that exist but cannot take a circuit.
        /// Each carries the fix, because an unusable panel is a SETTING and the
        /// agent's instinct is to replace the hardware.</summary>
        public static List<object> SkippedPanelRows(IEnumerable<PanelFacts> panels) =>
            panels
                .Where(p => !p.Usable)
                .Select(p => (object)new Dictionary<string, object?>
                {
                    ["id"] = p.Info.Id, ["name"] = p.Info.Name, ["reason"] = p.SkipReason,
                    // The one fact that stops the place-delete-replace churn:
                    // an unusable panel is a SETTING, not a placement mistake.
                    ["fix"] = "call set_distribution_system on this panel (use " +
                              "list_electrical_settings to pick one) — re-placing or swapping " +
                              "the panel will not help. If the assignment is refused for a " +
                              "voltage mismatch, the panel FAMILY's connector is wrong: fix it " +
                              "with set_connector_electrical_data",
                })
                .ToList();

        /// <summary>No panel in this model can take a circuit. ok:true — see
        /// the file header.</summary>
        public static Dictionary<string, object?> NoPanel(
            int panelsFound, List<object> skippedPanels) =>
            new()
            {
                ["ok"] = true,
                ["blocker"] = new Dictionary<string, object?>
                {
                    ["code"] = "no_panel",
                    ["detail"] = "no usable electrical panel (Electrical Equipment with a " +
                                 "distribution system) exists in this model. If panels EXIST " +
                                 "but are unusable, see skipped_panels: assign a distribution " +
                                 "system with set_distribution_system (call " +
                                 "list_electrical_settings first for what this project " +
                                 "defines). Only when the model has NO panel at all must a " +
                                 "drafter place one — and even then, placing more panels " +
                                 "never fixes an unusable one, so do not place, delete or " +
                                 "swap panels in a loop",
                    ["panels_found"] = panelsFound,
                    ["skipped_panels"] = skippedPanels,
                },
                ["circuits"] = new List<object>(),
                ["skipped_devices"] = new List<object>(),
                ["panels"] = new List<object>(),
                ["count"] = 0,
            };

        /// <summary>Every candidate was skipped. A drafter-actionable dead end,
        /// NOT a tool misuse — ok:true, see the file header. The branch is
        /// decided by WHY they were skipped, because the three causes have three
        /// different next steps.</summary>
        public static Dictionary<string, object?> NothingToGroup(
            List<object> skippedDevices, IReadOnlyList<CircuitedDevice> circuited)
        {
            var byReason = skippedDevices
                .OfType<Dictionary<string, object?>>()
                .GroupBy(r => (r.TryGetValue("reason", out var v) ? v?.ToString() : null) ?? "unknown")
                .OrderByDescending(g => g.Count())
                .ToList();

            string code;
            string detail;
            var blocker = new Dictionary<string, object?>();

            int alreadyCircuited = byReason.FirstOrDefault(g => g.Key == "already_circuited")?.Count() ?? 0;
            var levelKeys = byReason.Where(g => g.Key.StartsWith("level_mismatch")).ToList();
            int levelSkipped = levelKeys.Sum(g => g.Count());

            if (alreadyCircuited > 0 && alreadyCircuited == skippedDevices.Count)
            {
                code = "all_devices_already_circuited";
                detail = alreadyCircuited + " device(s) are already on power circuits, so there is " +
                         "nothing left to group. THIS IS OFTEN THE COMPLETE AND CORRECT ANSWER: " +
                         "say which circuits they are on (see existing_circuits) and stop. Only if " +
                         "the drafter explicitly wants them RE-circuited — a different grouping, " +
                         "a different panel or a different breaker — call remove_from_circuit with " +
                         "those device_ids, then re-run suggest_circuits. Do NOT retry this call " +
                         "unchanged, and do NOT place, delete or swap panels: neither frees a device.";
                blocker["existing_circuits"] = circuited
                    .GroupBy(c => c.CircuitId)
                    .OrderBy(g => g.Key)
                    .Select(g => (object)new Dictionary<string, object?>
                    {
                        ["circuit_id"] = g.Key,
                        ["circuit_number"] = g.First().CircuitNumber,
                        ["panel_id"] = g.First().PanelId,
                        ["panel_name"] = g.First().PanelName,
                        ["device_ids"] = g.Select(c => (object)c.DeviceId).OrderBy(x => (long)x).ToList(),
                        ["device_count"] = g.Count(),
                    })
                    .ToList();
                blocker["next_tool"] = "remove_from_circuit";
                blocker["next_args_hint"] = new Dictionary<string, object?>
                {
                    ["device_ids"] = circuited.Select(c => (object)c.DeviceId).OrderBy(x => (long)x).ToList(),
                };
            }
            else if (levelSkipped > 0 && levelSkipped == skippedDevices.Count)
            {
                code = "level_filter_excluded_everything";
                detail = "every candidate was excluded by the level filter — the levels actually " +
                         "found were: " +
                         string.Join(", ", levelKeys
                             .Select(g => g.Key.Substring("level_mismatch:".Length))
                             .Distinct()) +
                         ". Re-run with a level name that matches one of those, or omit level.";
                blocker["levels_found"] = levelKeys
                    .Select(g => (object)g.Key.Substring("level_mismatch:".Length))
                    .Distinct().ToList();
            }
            else
            {
                code = "no_circuitable_devices";
                detail = "no candidate could be circuited. Reasons: " +
                         string.Join(", ", byReason.Select(g => g.Key + " x" + g.Count())) +
                         ". connector_voltage_unset is fixed with set_connector_electrical_data; " +
                         "no_electrical_connector means the family has no power connector at all " +
                         "and only a drafter can add one in the Family Editor.";
            }

            blocker["code"] = code;
            blocker["detail"] = detail;
            blocker["skipped_count"] = skippedDevices.Count;

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["blocker"] = blocker,
                ["circuits"] = new List<object>(),
                ["count"] = 0,
                ["skipped_devices"] = skippedDevices,
                ["skipped_by_reason"] = byReason.ToDictionary(
                    g => g.Key, g => (object?)g.Count()),
            };
        }
    }
}
