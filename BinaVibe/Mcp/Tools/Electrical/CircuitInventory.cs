// list_circuits — the power circuits a model already has.
//
// READ-ONLY. Registered as an INSPECT tool; no Transaction is opened here.
//
// WHY IT EXISTS. Until now nothing could hand the agent a circuit's element
// id. filter_elements threw "unknown category 'Electrical Circuits'" (the enum
// member is singular — fixed in CategoryResolve, but its row shape still has
// no panel, no members and no rating), trace_mep_connections follows physical
// connectors and a circuit assignment is logical, and validate_panel_schedule
// reports only counts. So when UAT 2026-08-04 asked to re-circuit ten sockets
// that were already on a circuit, the agent could see THAT they were circuited
// and never WHICH circuit — with no id, remove_from_circuit and delete_elements
// are both unreachable and the run degenerates into guessing.
//
// Circuits are collected by CLASS, not category (precedent: ElecValidation) —
// which is why this tool was never affected by the category bug.
//
// Every read is guarded. A circuit Revit considers incomplete throws from
// CircuitNumber, StartSlot, Length and PolesNumber rather than returning a
// blank, and an inventory that dies on the one broken circuit in the model is
// useless exactly when it is needed.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.Electrical.ElecReads;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class CircuitInventory
    {
        public static Dictionary<string, object?> List(Document doc, JsonElement args)
        {
            var wantCircuits = ArgsHelp.GetLongList(args, "circuit_ids");
            var wantDevices = ArgsHelp.GetLongList(args, "device_ids");
            long? panelId = ArgsHelp.GetLong(args, "panel_id");
            var levelFilter = ArgsHelp.GetString(args, "level");
            bool includeUnassigned = ArgsHelp.GetBool(args, "include_unassigned") ?? true;
            int maxCircuits = (int)(ArgsHelp.GetLong(args, "max_circuits") ?? 200);

            var all = Collect(doc);

            var rows = new List<object>();
            int matched = 0;
            foreach (var sys in all)
            {
                long id = sys.Id.Value;
                if (wantCircuits.Count > 0 && !wantCircuits.Contains(id)) continue;

                var panel = SafeBaseEquipment(sys);
                if (panel == null && !includeUnassigned) continue;
                if (panelId.HasValue && panel?.Id.Value != panelId.Value) continue;

                var memberIds = MemberIds(sys, panel);
                if (wantDevices.Count > 0 && !memberIds.Any(wantDevices.Contains)) continue;

                string? levelMatch = null;
                if (levelFilter != null)
                {
                    levelMatch = MatchLevel(doc, sys, panel, memberIds, levelFilter);
                    if (levelMatch == null) continue;
                }

                matched++;
                if (rows.Count >= maxCircuits) continue;
                rows.Add(Describe(doc, sys, panel, memberIds, levelMatch));
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["count"] = rows.Count,
                ["matched"] = matched,
                ["total_circuits_in_model"] = all.Count,
                ["circuits"] = rows,
                ["truncated"] = matched > rows.Count
                    ? "showing " + rows.Count + " of " + matched +
                      " matching circuits — narrow with panel_id, level or device_ids, " +
                      "or raise max_circuits"
                    : null,
            };
        }

        /// <summary>Every power circuit in the document, id-ordered.</summary>
        internal static List<ElectricalSystem> Collect(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                .Where(s => SafeSystemType(s) == ElectricalSystemType.PowerCircuit)
                .OrderBy(s => s.Id.Value)
                .ToList();

        /// <summary>Member device ids. sys.Elements is documented to exclude the
        /// base equipment, but the panel is filtered out defensively anyway —
        /// a member list that silently contains the board would make
        /// remove_from_circuit's "all members removed" rule fire one device
        /// early on every circuit.</summary>
        internal static List<long> MemberIds(ElectricalSystem sys, Element? panel)
        {
            var ids = new List<long>();
            try
            {
                foreach (Element el in sys.Elements)
                {
                    if (el == null) continue;
                    if (panel != null && el.Id.Value == panel.Id.Value) continue;
                    ids.Add(el.Id.Value);
                }
            }
            catch { }
            return ids.Distinct().OrderBy(x => x).ToList();
        }

        private static Dictionary<string, object?> Describe(
            Document doc, ElectricalSystem sys, Element? panel,
            List<long> memberIds, string? levelMatch)
        {
            var row = new Dictionary<string, object?>
            {
                ["circuit_id"] = sys.Id.Value,
                ["circuit_number"] = SafeCircuitNumber(sys),
                ["name"] = SafeName(sys),
                ["panel_id"] = panel?.Id.Value,
                ["panel_name"] = panel?.Name ?? "",
                ["device_ids"] = memberIds.Cast<object>().ToList(),
                ["device_count"] = memberIds.Count,
                ["is_empty"] = memberIds.Count == 0,
                ["rating_a"] = Round(ParamAs(
                    sys, BuiltInParameter.RBS_ELEC_CIRCUIT_RATING_PARAM, UnitTypeId.Amperes), 1),
                ["apparent_load_va"] = Round(ParamAs(
                    sys, BuiltInParameter.RBS_ELEC_APPARENT_LOAD, UnitTypeId.VoltAmperes), 0),
                ["voltage_v"] = Round(ParamAs(
                    sys, BuiltInParameter.RBS_ELEC_VOLTAGE, UnitTypeId.Volts), 1),
                ["poles"] = SafePoles(sys),
                ["start_slot"] = SafeStartSlot(sys),
                ["length_mm"] = Round(SafeLengthMm(sys), 0),
                // routed == a custom circuit path, which create_circuit_routes
                // sets with SetCircuitPath. check_circuit_loads refuses a
                // voltage-drop verdict without it.
                ["routed"] = SafePathMode(sys) == ElectricalCircuitPathMode.Custom,
            };
            if (panel == null)
                row["note"] = "orphaned: this circuit is assigned to no panel — " +
                              "validate_panel_schedule reports it as a defect";
            if (levelMatch != null)
                row["level_match"] = levelMatch;
            return row;
        }

        /// <summary>"panel" / "device" when the circuit belongs to that level,
        /// null when it does not. Level is read through
        /// CircuitCandidates.DeviceLevelName so a wall-hosted socket — which
        /// reports InvalidElementId for LevelId — resolves through its host,
        /// the way suggest_circuits does.</summary>
        private static string? MatchLevel(
            Document doc, ElectricalSystem sys, Element? panel,
            List<long> memberIds, string levelFilter)
        {
            if (panel != null &&
                string.Equals(CircuitCandidates.DeviceLevelName(doc, panel), levelFilter,
                              StringComparison.OrdinalIgnoreCase))
                return "panel";

            foreach (var id in memberIds)
            {
                var el = doc.GetElement(ElemIds.From(id));
                if (el == null) continue;
                if (string.Equals(CircuitCandidates.DeviceLevelName(doc, el), levelFilter,
                                  StringComparison.OrdinalIgnoreCase))
                    return "device";
            }
            return null;
        }

    }
}
