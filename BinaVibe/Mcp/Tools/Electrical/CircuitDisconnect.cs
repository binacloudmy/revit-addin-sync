// remove_from_circuit — free devices from the circuits they are on. MUTATE:
// the addin's ConfirmGate shows a Ya/Tidak card before this runs.
//
// The rules live in CircuitDisconnectPlan (Revit-free, unit-tested); this file
// is the Revit half. Panel and Circuit Number are read-only parameters driven
// by the system assignment, so RemoveFromCircuit is the only way off a circuit.
//
// TWO NON-GOALS, neither of which may be "finished":
//
//   * Conduit SURVIVES. No Revit API links an ElectricalSystem to the Conduit
//     create_circuit_routes drew, so it cannot be found from here — reported as
//     conduit_note pointing at delete_elements. Fixing this means persistence
//     (a RouteCommit-written circuit_id -> conduit_ids record), not more API
//     archaeology.
//
//   * DisconnectPanel() is deliberately not called. It leaves a panel-less
//     system, which is the state validate_panel_schedule reports as
//     orphaned_circuit AND still blocks suggest_circuits — GetElectricalSystems
//     keeps returning a PowerCircuit, so the devices stay already_circuited.
//     The real want behind it, "move this circuit to another board", is
//     SelectPanel on the new panel and needs no disconnect at all.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class CircuitDisconnect
    {
        public static Dictionary<string, object?> Run(Document doc, JsonElement args)
        {
            var deviceIds = ArgsHelp.GetLongList(args, "device_ids");
            var circuitIds = ArgsHelp.GetLongList(args, "circuit_ids");
            bool deleteEmpty = ArgsHelp.GetBool(args, "delete_empty_circuits") ?? true;
            bool deleteWires = ArgsHelp.GetBool(args, "delete_wires") ?? true;

            // A no-arg call must never mean "every circuit in the model".
            if (deviceIds.Count == 0 && circuitIds.Count == 0)
                return ToolResult.Fail("pass device_ids (free these devices from whatever circuit holds " +
                    "them) and/or circuit_ids (remove these circuits entirely). This " +
                    "tool deletes circuits, so it will not act on an empty request — " +
                    "call list_circuits first if you need the ids.");

            // (long) casts, not inference: ElementId.Value is int on the net48
            // target and long on net10, and the planner speaks long.
            var live = CircuitInventory.Collect(doc);
            var byId = live.ToDictionary(s => (long)s.Id.Value, s => s);
            var memberMap = live
                .Select(s => (CircuitId: (long)s.Id.Value,
                              MemberIds: (IReadOnlyList<long>)CircuitInventory.MemberIds(
                                  s, ElecReads.SafeBaseEquipment(s))))
                .ToList();

            var plan = CircuitDisconnectPlanner.Build(deviceIds, circuitIds, memberMap);

            var circuitRows = new List<object>();
            var deviceRows = new List<object>();
            int freed = 0, deleted = 0, modified = 0;

            TxGuard.ForEachInGroup(doc, "BinaVibe: remove_from_circuit", plan.Actions,
                action =>
                {
                    var row = ApplyOne(doc, byId[action.CircuitId], action, deleteEmpty, deleteWires);
                    circuitRows.Add(row);
                    freed += action.MembersToRemove.Count;
                    if (Equals(row["action"], "deleted")) deleted++; else modified++;
                    foreach (var d in action.MembersToRemove)
                        deviceRows.Add(DeviceRow(d, "freed", action.CircuitId, null));
                },
                (action, ex) =>
                {
                    circuitRows.Add(new Dictionary<string, object?>
                    {
                        ["circuit_id"] = action.CircuitId,
                        ["action"] = "failed",
                        ["reason"] = ex.Message,
                    });
                    // RemoveFromCircuit is all-or-nothing per call, so a failure
                    // tells us nothing about WHICH device Revit objected to. Name
                    // them individually rather than let the agent assume the
                    // whole set is unfixable.
                    foreach (var d in action.MembersToRemove)
                        deviceRows.Add(DeviceRow(d, "failed", action.CircuitId, ex.Message));
                });

            foreach (var miss in plan.MissedDevices)
                deviceRows.Add(DeviceRow(miss.DeviceId, "not_circuited", null,
                    "this device is on no power circuit — nothing to remove"));

            // Membership changed and circuit ids may be gone, so any held
            // socket/circuit/route plan now describes a model that no longer
            // exists (RoutePlan holds circuit ids directly).
            if (deleted > 0 || modified > 0) ElecPlanCaches.DropAll();

            return new Dictionary<string, object?>
            {
                // Same rule as create_circuits: a run where nothing happened
                // must not read as done to a loop branching on ok.
                ["ok"] = freed > 0 || deleted > 0,
                ["freed_device_count"] = freed,
                ["circuits_deleted"] = deleted,
                ["circuits_modified"] = modified,
                ["circuits"] = circuitRows,
                ["devices"] = deviceRows,
                ["unknown_circuit_ids"] = plan.UnknownCircuitIds.Cast<object>().ToList(),
                ["plans_invalidated"] = deleted > 0 || modified > 0,
                ["error"] = freed > 0 || deleted > 0 ? null
                    : "nothing was removed — see devices[] for why each requested id was a no-op",
            };
        }

        private static Dictionary<string, object?> ApplyOne(
            Document doc, ElectricalSystem sys, CircuitAction action,
            bool deleteEmpty, bool deleteWires)
        {
            long circuitId = action.CircuitId;
            var panel = ElecReads.SafeBaseEquipment(sys);
            long? panelId = panel?.Id.Value;
            string panelName = panel?.Name ?? "";
            string circuitNumber = ElecReads.SafeCircuitNumber(sys);

            // Read before mutating — after the members change, the path mode
            // and length no longer describe what the drafter is approving.
            bool wasRouted = ElecReads.SafePathMode(sys) == ElectricalCircuitPathMode.Custom;

            // A circuit named in circuit_ids is deleted even when the caller
            // asked to keep empties: delete_empty_circuits governs the circuit
            // that EMPTIES OUT as a side effect, not one the caller named.
            bool namedWhole = action.Kind == DisconnectKind.DeleteWhole &&
                              action.RemainingCount == 0;
            bool deleteIt = action.Kind == DisconnectKind.DeleteWhole &&
                            (deleteEmpty || namedWhole);

            using var tx = new Transaction(doc, "BinaVibe: remove from circuit");
            TxGuard.StartSwallowing(tx);
            int wiresDeleted = 0;
            bool pathReset = false;
            string appliedAction;
            try
            {
                // Wires first, while the circuit still knows its members. All of
                // them go, even on a partial removal: create_circuit_routes draws
                // one Wire per hop of the daisy chain, so a wire left behind is
                // drawn to a device that is no longer on this circuit, and no
                // tool redraws a partial chain. Re-run suggest_circuit_routes.
                if (deleteWires)
                    wiresDeleted = DeleteWiresOf(doc, sys);

                if (deleteIt)
                {
                    doc.Delete(sys.Id);
                    appliedAction = "deleted";
                }
                else if (action.Kind == DisconnectKind.DeleteWhole)
                {
                    // deleteEmpty:false on a side-effect empty. Honour it, but
                    // the caller is choosing to leave a circuit that still
                    // holds a breaker slot — say so in the row, not in a log.
                    RemoveMembers(doc, sys, action.MembersToRemove);
                    appliedAction = "emptied";
                }
                else
                {
                    RemoveMembers(doc, sys, action.MembersToRemove);
                    appliedAction = "members_removed";

                    // The stored custom path still routes through the device we
                    // just removed, so sys.Length — and every voltage drop
                    // check_circuit_loads derives from it — is now a wrong
                    // number presented as a verified one. Setting the mode AWAY
                    // from Custom is always legal (setting it TO Custom is what
                    // needs an existing custom path).
                    if (wasRouted)
                    {
                        try
                        {
                            sys.CircuitPathMode = ElectricalCircuitPathMode.FarthestDevice;
                            pathReset = true;
                        }
                        catch { /* reported as pathReset:false, not fatal */ }
                    }
                }

                TxGuard.CommitOrThrow(tx);
            }
            catch
            {
                TxGuard.SafeRollBack(tx);
                throw;
            }

            // PAST THE COMMIT LINE — see CircuitCommit's header. Nothing below
            // may throw: the removal already happened, and a throw here would
            // file a completed removal under failed.
            var row = new Dictionary<string, object?>
            {
                ["circuit_id"] = circuitId,
                ["circuit_number"] = circuitNumber,
                ["panel_id"] = panelId,
                ["panel_name"] = panelName,
                ["action"] = appliedAction,
                ["removed_device_ids"] = action.MembersToRemove.Cast<object>().ToList(),
                ["remaining_device_count"] = action.RemainingCount,
                ["wires_deleted"] = wiresDeleted,
                ["was_routed"] = wasRouted,
                ["circuit_path_reset"] = pathReset,
            };
            if (appliedAction == "emptied")
                row["note"] = "delete_empty_circuits was false, so this circuit still exists with " +
                              "no devices — it keeps its breaker slot and validate_panel_schedule " +
                              "will report it as orphaned/empty";
            if (wasRouted)
                row["conduit_note"] = "this circuit was routed. Its WIRES were removed, but the " +
                                      "CONDUIT and fittings are independent geometry that no " +
                                      "Revit API links to a circuit — they are still in the model. " +
                                      "Delete them with delete_elements using the conduit_ids that " +
                                      "create_circuit_routes returned, then re-run " +
                                      "suggest_circuit_routes / create_circuit_routes for the new " +
                                      "circuiting before trusting check_circuit_loads again.";
            return row;
        }

        /// <summary>RemoveFromCircuit takes the legacy ElementSet, and is
        /// documented all-or-nothing: on failure nothing is removed.</summary>
        private static void RemoveMembers(Document doc, ElectricalSystem sys, List<long> ids)
        {
            var set = new ElementSet();
            foreach (var id in ids)
            {
                var el = doc.GetElement(ElemIds.From(id));
                if (el != null) set.Insert(el);
            }
            if (set.Size == 0)
                throw new InvalidOperationException(
                    "none of the requested devices still exist in the model");
            sys.RemoveFromCircuit(set);
        }

        /// <summary>Delete every Wire that belongs to this circuit ONLY. A wire
        /// may belong to more than one system (the API says so explicitly); one
        /// shared with a circuit we are not touching is left alone rather than
        /// silently erased out from under it.</summary>
        private static int DeleteWiresOf(Document doc, ElectricalSystem sys)
        {
            long circuitId = sys.Id.Value;
            var doomed = new List<ElementId>();
            foreach (var wire in new FilteredElementCollector(doc)
                                     .OfClass(typeof(Wire)).Cast<Wire>())
            {
                try
                {
                    var systems = wire.GetMEPSystems();
                    if (systems == null || systems.Count == 0) continue;
                    if (!systems.Any(s => s.Value == circuitId)) continue;
                    if (systems.Any(s => s.Value != circuitId)) continue;   // shared
                    doomed.Add(wire.Id);
                }
                catch { }
            }
            if (doomed.Count == 0) return 0;
            try
            {
                // Revit may already have removed some of them as a side effect
                // of deleting the system; Delete returns what it actually took.
                var removed = doc.Delete(doomed);
                return removed?.Count ?? doomed.Count;
            }
            catch { return 0; }
        }

        private static Dictionary<string, object?> DeviceRow(
            long id, string status, long? circuitId, string? reason) => new()
            {
                ["id"] = id,
                ["status"] = status,
                ["circuit_id"] = circuitId,
                ["reason"] = reason,
            };
    }
}
