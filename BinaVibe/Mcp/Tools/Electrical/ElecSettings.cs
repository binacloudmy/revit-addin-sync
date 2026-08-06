// list_electrical_settings (read) + set_distribution_system (mutate).
// set_connector_electrical_data lives in ElecSettings.ConnectorData.cs.
//
// WHY THESE EXIST: a circuit's voltage comes from the DEVICE connectors, and a
// panel only accepts a circuit whose voltage falls inside a voltage definition
// used by its distribution system. A socket family with a 0 V connector makes a
// circuit NO panel can take, and Revit rejects it with "The panel and circuit
// do not match" — wording that reads like a panel problem and sent the agent
// into a place/delete/replace loop over DB boxes.
//
// set_distribution_system writes an ElementId-valued parameter, which the
// generic set_parameter (string/double/int) cannot.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.Electrical.ElecReads;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static partial class ElecSettings
    {
        // ─── list_electrical_settings ───────────────────────────────────
        public static Dictionary<string, object?> List(Document doc, JsonElement args)
        {
            var voltageTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(VoltageType)).Cast<VoltageType>()
                .OrderBy(v => v.Id.Value)
                .ToList();

            // VoltageType exposes its values as properties ALREADY IN VOLTS
            // ("the unit is volt" — API docs), so no unit conversion here.
            var voltageRows = voltageTypes.Select(v => (object)new Dictionary<string, object?>
            {
                ["id"] = v.Id.Value,
                ["name"] = v.Name,
                ["voltage_v"] = Round1(SafeVolts(() => v.ActualValue)),
                ["min_v"] = Round1(SafeVolts(() => v.MinValue)),
                ["max_v"] = Round1(SafeVolts(() => v.MaxValue)),
            }).ToList();

            var distRows = new FilteredElementCollector(doc)
                .OfClass(typeof(DistributionSysType)).Cast<DistributionSysType>()
                .OrderBy(d => d.Id.Value)
                .Select(d => (object)new Dictionary<string, object?>
                {
                    ["id"] = d.Id.Value,
                    ["name"] = d.Name,
                    ["phases"] = SafePhases(d),
                    ["phase_config"] = SafeConfig(d),
                    ["wires"] = SafeWires(d),
                    ["voltage_line_to_ground_v"] = Round1(
                        SafeVolts(() => d.VoltageLineToGround?.ActualValue)),
                    ["voltage_line_to_line_v"] = Round1(
                        SafeVolts(() => d.VoltageLineToLine?.ActualValue)),
                }).ToList();

            // The panel's OWN connector goes out beside the system it was given: an
            // unassigned system and a connector Revit cannot match both read as
            // "the panel and circuit do not match" from outside.
            //
            // connector_voltage_v / connector_poles are the pair
            // IsValidDistributionSystem compares, and set_connector_electrical_data
            // writes both. panel_phases / panel_wires are DERIVED and no tool can
            // author them — named apart deliberately, because as connector data they
            // read as a fixable defect.
            var panelRows = CircuitCandidates.FindPanels(doc)
                .Select(p =>
                {
                    var fi = doc.GetElement(ElemIds.From(p.Info.Id)) as FamilyInstance;
                    return (object)new Dictionary<string, object?>
                    {
                        ["id"] = p.Info.Id,
                        ["name"] = p.Info.Name,
                        ["distribution_system"] = string.IsNullOrEmpty(p.DistSystem) ? null : p.DistSystem,
                        ["usable"] = p.Usable,
                        ["reason"] = p.Usable ? null : p.SkipReason,
                        ["connector_voltage_v"] = Round1(ReadVolts(fi, BuiltInParameter.RBS_ELEC_VOLTAGE)),
                        ["connector_poles"] = ReadInt(fi, BuiltInParameter.RBS_ELEC_NUMBER_OF_POLES),
                        ["panel_phases_derived"] = ReadInt(fi, BuiltInParameter.RBS_ELEC_PANEL_NUMPHASES_PARAM),
                        ["panel_wires_derived"] = ReadInt(fi, BuiltInParameter.RBS_ELEC_PANEL_NUMWIRES_PARAM),
                    };
                }).ToList();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["voltage_types"] = voltageRows,
                ["distribution_systems"] = distRows,
                ["panels"] = panelRows,
                ["voltage_type_count"] = voltageRows.Count,
                ["distribution_system_count"] = distRows.Count,
            };
        }

        // ─── set_distribution_system ────────────────────────────────────
        // The panel's distribution system is an ElementId-valued parameter,
        // which set_parameter (string/double/int only) cannot write — hence a
        // typed tool rather than a generic call.
        public static Dictionary<string, object?> SetDistributionSystem(Document doc, JsonElement args)
        {
            var panelId = ArgsHelp.GetLong(args, "panel_id")
                ?? throw new ArgumentException("missing panel_id");
            var systemName = ArgsHelp.GetString(args, "distribution_system");
            var systemId = ArgsHelp.GetLong(args, "distribution_system_id");
            if (systemName == null && !systemId.HasValue)
                throw new ArgumentException(
                    "pass distribution_system (name) or distribution_system_id — " +
                    "call list_electrical_settings for what this project defines");

            var panel = doc.GetElement(ElemIds.From(panelId)) as FamilyInstance
                ?? throw new ArgumentException("panel " + panelId + " not found");
            if (panel.MEPModel is not ElectricalEquipment equipment)
                return ToolResult.Fail("element " + panelId + " is not electrical equipment " +
                    "(category " + (panel.Category?.Name ?? "?") + ") — " +
                    "only a panel/switchboard carries a distribution system");

            var systems = new FilteredElementCollector(doc)
                .OfClass(typeof(DistributionSysType)).Cast<DistributionSysType>()
                .ToList();
            var target = systemId.HasValue
                ? systems.FirstOrDefault(s => s.Id.Value == systemId.Value)
                : systems.FirstOrDefault(s =>
                    string.Equals(s.Name, systemName, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return ToolResult.Fail("distribution system '" +
                    (systemName ?? systemId!.Value.ToString()) +
                    "' not found in this project",
                    new Dictionary<string, object?>
                    {
                        ["available"] = systems.OrderBy(s => s.Id.Value)
                            .Select(s => (object)s.Name).ToList(),
                    });

            // Pre-check: Revit compares the system's voltages against the
            // panel family's own connector, so an unmatched pair is refused.
            // Saying WHY beats relaying a bare ArgumentException.
            bool valid;
            try { valid = equipment.IsValidDistributionSystem(target); }
            catch { valid = true; }   // unavailable check must not block the attempt
            if (!valid)
                return ToolResult.Fail("'" + target.Name + "' cannot be assigned to panel " + panelId +
                    " — its voltage/phase does not match the panel family's " +
                    "electrical connector. Either pick a distribution system whose " +
                    "voltage matches, or fix the panel family's connector first " +
                    "with set_connector_electrical_data (a 0 V connector matches " +
                    "nothing).",
                    new Dictionary<string, object?>
                    {
                        ["distribution_system"] = target.Name,
                                            ["voltage_line_to_ground_v"] = Round1(
                                            SafeVolts(() => target.VoltageLineToGround?.ActualValue)),
                                            ["voltage_line_to_line_v"] = Round1(
                                            SafeVolts(() => target.VoltageLineToLine?.ActualValue)),
                    });

            using (var tx = new Transaction(doc, "BinaVibe: set distribution system"))
            {
                TxGuard.StartSwallowing(tx);
                try
                {
                    equipment.DistributionSystem = target;
                    TxGuard.CommitOrThrow(tx);
                }
                catch { TxGuard.SafeRollBack(tx); throw; }
            }

            // Read back rather than assume.
            var after = (doc.GetElement(ElemIds.From(panelId)) as FamilyInstance)
                ?.MEPModel as ElectricalEquipment;
            var applied = after?.DistributionSystem;
            bool ok = applied != null && applied.Id.Value == target.Id.Value;

            // A cached circuit plan carries the panel's phase count and its
            // assignment, both derived from the distribution system that was in
            // place when the plan was made. Changing it makes that plan stale
            // while its plan_id still resolves, so the natural chain — propose,
            // notice the system is wrong, fix it, commit — committed against
            // stale phases. Same contract as set_connector_electrical_data.
            if (ok) ElecPlanCaches.DropAll();

            return new Dictionary<string, object?>
            {
                ["ok"] = ok,
                ["panel_id"] = panelId,
                ["distribution_system"] = applied?.Name,
                ["distribution_system_id"] = applied?.Id.Value,
                ["phases"] = applied != null ? SafePhases(applied) : 1,
                ["voltage_line_to_ground_v"] = Round1(
                    SafeVolts(() => applied?.VoltageLineToGround?.ActualValue)),
                ["plans_invalidated"] = ok,
                ["error"] = ok ? null
                    : "Revit did not keep the assignment — the panel family's connector " +
                      "voltage most likely does not match this distribution system. Fix the " +
                      "connector with set_connector_electrical_data first.",
            };
        }
    }
}
