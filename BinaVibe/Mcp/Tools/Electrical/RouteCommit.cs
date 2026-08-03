// create_circuit_routes — the write half of the routing workflow. MUTATE
// tool: the addin's ConfirmGate shows a Ya/Tidak card before this runs.
//
// Takes a plan_id + indices, never coordinates — legs come from the cached
// RoutePlan the drafter reviewed. Circuits with obstructed legs are SKIPPED
// by default (skipping single legs would break daisy-chain continuity);
// include_obstructed=true builds them anyway after review.
//
// Per circuit, one Transaction inside a TransactionGroup: a half-routed
// circuit rolls back to nothing while the other circuits survive.
// Per circuit it creates, best-effort and individually reported:
//   1. chained Conduit segments (one per leg),
//   2. elbow fittings at the joints (per-joint try/catch — a conduit type
//      whose routing preferences lack an elbow costs ONE joint, not the run),
//   3. Wire elements (one per hop, plan view required, connectors attached
//      where resolvable),
//   4. the circuit's own path (SetCircuitPath) so Revit's circuit length
//      reflects the ROUTED length — which is what voltage-drop checks use.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class RouteCommit
    {
        private const double MmPerFoot = 304.8;
        /// <summary>Connector-to-joint match tolerance. A conduit endpoint
        /// connector sits exactly at the leg endpoint; 5 mm absorbs rounding.</summary>
        private const double JointTolMm = 5.0;

        public static Dictionary<string, object?> Run(Document doc, JsonElement args)
        {
            var planId = ArgsHelp.GetString(args, "plan_id")
                ?? throw new ArgumentException("missing plan_id");
            var plan = RoutePlanCache.Get(planId, SocketCandidates.DocKey(doc));

            var wanted = ArgsHelp.GetLongList(args, "route_indices");
            var routes = wanted.Count == 0
                ? plan.Routes
                : plan.Routes.Where(r => wanted.Contains(r.Index)).ToList();
            if (routes.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"no routes selected from plan {planId} " +
                                $"(plan holds {plan.Routes.Count} routes; indices are 0-based)",
                };

            bool includeObstructed = ArgsHelp.GetBool(args, "include_obstructed") ?? false;
            bool createWires = ArgsHelp.GetBool(args, "create_wires") ?? true;
            bool createConduits = ArgsHelp.GetBool(args, "create_conduits") ?? true;
            bool connectConduits = ArgsHelp.GetBool(args, "connect_conduits") ?? true;
            bool setCircuitPath = ArgsHelp.GetBool(args, "set_circuit_path") ?? true;
            var conduitTypeName = ArgsHelp.GetString(args, "conduit_type_name");
            var wireTypeName = ArgsHelp.GetString(args, "wire_type_name");
            var viewName = ArgsHelp.GetString(args, "view");

            // ── shared resolutions, fail fast with guidance ───────────────
            ConduitType? conduitType = null;
            if (createConduits)
            {
                conduitType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ConduitType)).Cast<ConduitType>()
                    .OrderBy(c => c.Id.Value)
                    .FirstOrDefault(c => conduitTypeName == null ||
                        string.Equals(c.Name, conduitTypeName, StringComparison.OrdinalIgnoreCase));
                if (conduitType == null)
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = conduitTypeName != null
                            ? $"conduit type '{conduitTypeName}' not found (use list_family_types(\"OST_Conduit\"))"
                            : "no conduit types in project",
                    };
            }

            WireType? wireType = null;
            string? wiresSkippedReason = null;
            ViewPlan? wireView = null;
            if (createWires)
            {
                wireType = new FilteredElementCollector(doc)
                    .OfClass(typeof(WireType)).Cast<WireType>()
                    .OrderBy(w => w.Id.Value)
                    .FirstOrDefault(w => wireTypeName == null ||
                        string.Equals(w.Name, wireTypeName, StringComparison.OrdinalIgnoreCase));
                if (wireType == null)
                    wiresSkippedReason = wireTypeName != null
                        ? $"wire type '{wireTypeName}' not found"
                        : "no_wire_type";

                if (viewName != null)
                {
                    wireView = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                        .FirstOrDefault(v => !v.IsTemplate &&
                            string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase));
                    if (wireView == null)
                        wiresSkippedReason ??= $"view '{viewName}' is not a plan view";
                }
                else
                {
                    wireView = doc.ActiveView as ViewPlan;
                    if (wireView == null)
                        wiresSkippedReason ??= "no_plan_view";   // wires are plan-view-hosted
                }
            }

            var created = new List<object>();
            var failed = new List<object>();
            var skipped = new List<object>();

            using var group = new TransactionGroup(doc, "BinaVibe: create_circuit_routes");
            group.Start();
            try
            {
                foreach (var r in routes)
                {
                    if (r.ObstructedLegCount > 0 && !includeObstructed)
                    {
                        skipped.Add(new Dictionary<string, object?>
                        {
                            ["index"] = r.Index,
                            ["circuit_id"] = r.CircuitId,
                            ["reason"] = "obstructed_legs",
                            ["obstructed_leg_count"] = r.ObstructedLegCount,
                        });
                        continue;
                    }
                    try
                    {
                        created.Add(CommitOne(doc, r, conduitType, wireType, wireView,
                                              wiresSkippedReason, createConduits, createWires,
                                              connectConduits, setCircuitPath));
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new Dictionary<string, object?>
                        {
                            ["index"] = r.Index,
                            ["circuit_id"] = r.CircuitId,
                            ["reason"] = ex.Message,
                        });
                    }
                }
                group.Assimilate();
            }
            catch { group.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = planId,
                ["count"] = created.Count,
                ["created"] = created,
                ["skipped"] = skipped,
                ["failed"] = failed,
                ["fittings_failed"] = created
                    .OfType<Dictionary<string, object?>>()
                    .Sum(r => ((List<object>)(r["unconnected_joints"] ?? new List<object>())).Count),
            };
        }

        private static Dictionary<string, object?> CommitOne(
            Document doc, PlannedRoute r, ConduitType? conduitType, WireType? wireType,
            ViewPlan? wireView, string? wiresSkippedReason, bool createConduits,
            bool createWires, bool connectConduits, bool setCircuitPath)
        {
            var sys = doc.GetElement(ElemIds.From(r.CircuitId)) as ElectricalSystem
                ?? throw new InvalidOperationException(
                    "circuit " + r.CircuitId + " no longer exists — re-run suggest_circuit_routes");
            var panel = sys.BaseEquipment
                ?? throw new InvalidOperationException(
                    "circuit " + r.CircuitId + " lost its panel — re-run suggest_circuit_routes");

            var levelId = panel.LevelId != ElementId.InvalidElementId
                ? panel.LevelId
                : new FilteredElementCollector(doc).OfClass(typeof(Level))
                      .Cast<Level>().OrderBy(l => l.Elevation).First().Id;

            using var tx = new Transaction(doc, "BinaVibe: route circuit " + r.CircuitNumber);
            TxGuard.StartSwallowing(tx);
            try
            {
                var conduitIds = new List<long>();
                var fittingIds = new List<long>();
                var unconnected = new List<object>();
                var conduits = new List<Conduit>();

                if (createConduits && conduitType != null)
                {
                    foreach (var leg in r.Legs)
                    {
                        var a = new XYZ(leg.FromXMm / MmPerFoot, leg.FromYMm / MmPerFoot, leg.FromZMm / MmPerFoot);
                        var b = new XYZ(leg.ToXMm / MmPerFoot, leg.ToYMm / MmPerFoot, leg.ToZMm / MmPerFoot);
                        // Type first, level LAST — Conduit.Create's arg order
                        // differs from Duct/Pipe (MutatorsMep precedent).
                        var conduit = Conduit.Create(doc, conduitType.Id, a, b, levelId);
                        if (r.ConduitDiameterMm.HasValue)
                            conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)
                                ?.Set(r.ConduitDiameterMm.Value / MmPerFoot);
                        conduits.Add(conduit);
                        conduitIds.Add(conduit.Id.Value);
                    }

                    if (connectConduits)
                    {
                        // Joints = shared endpoints of consecutive legs.
                        for (int i = 1; i < conduits.Count; i++)
                        {
                            var jointMm = new[] { r.Legs[i].FromXMm, r.Legs[i].FromYMm, r.Legs[i].FromZMm };
                            try
                            {
                                var c1 = ConnectorNear(conduits[i - 1], jointMm);
                                var c2 = ConnectorNear(conduits[i], jointMm);
                                if (c1 == null || c2 == null)
                                    throw new InvalidOperationException("no connector at joint");
                                var fitting = doc.Create.NewElbowFitting(c1, c2);
                                if (fitting != null) fittingIds.Add(fitting.Id.Value);
                            }
                            catch (Exception ex)
                            {
                                unconnected.Add(new Dictionary<string, object?>
                                {
                                    ["at_mm"] = jointMm.Select(v => (object)Math.Round(v)).ToList(),
                                    ["reason"] = ex.Message,
                                });
                            }
                        }
                    }
                }

                var wireIds = new List<long>();
                string? wireSkip = wiresSkippedReason;
                if (createWires && wireSkip == null && wireType != null && wireView != null)
                {
                    // One Wire per hop: panel->dev0, dev0->dev1, ...
                    for (int h = 0; h < r.HopStartLegIndex.Count; h++)
                    {
                        int startLeg = r.HopStartLegIndex[h];
                        int endLeg = h + 1 < r.HopStartLegIndex.Count
                            ? r.HopStartLegIndex[h + 1] - 1
                            : r.Legs.Count - 1;

                        var verts = new List<XYZ>();
                        for (int i = startLeg; i <= endLeg; i++)
                        {
                            var leg = r.Legs[i];
                            if (verts.Count == 0)
                                verts.Add(new XYZ(leg.FromXMm / MmPerFoot, leg.FromYMm / MmPerFoot, leg.FromZMm / MmPerFoot));
                            verts.Add(new XYZ(leg.ToXMm / MmPerFoot, leg.ToYMm / MmPerFoot, leg.ToZMm / MmPerFoot));
                        }

                        var startConn = h == 0
                            ? FirstElectricalConnector(panel)
                            : DeviceConnector(doc, r.DeviceIds[h - 1]);
                        var endConn = DeviceConnector(doc, r.DeviceIds[h]);

                        try
                        {
                            var wire = Wire.Create(doc, wireType.Id, wireView.Id,
                                                   WiringType.Chamfer, verts, startConn, endConn);
                            wireIds.Add(wire.Id.Value);
                        }
                        catch (Exception ex)
                        {
                            wireSkip = "wire_create_failed: " + ex.Message;
                            break;
                        }
                    }
                }

                bool pathSet = false;
                string? pathError = null;
                if (setCircuitPath)
                {
                    try
                    {
                        var pathVerts = new List<XYZ>();
                        foreach (var leg in r.Legs)
                        {
                            if (pathVerts.Count == 0)
                                pathVerts.Add(new XYZ(leg.FromXMm / MmPerFoot, leg.FromYMm / MmPerFoot, leg.FromZMm / MmPerFoot));
                            pathVerts.Add(new XYZ(leg.ToXMm / MmPerFoot, leg.ToYMm / MmPerFoot, leg.ToZMm / MmPerFoot));
                        }
                        sys.CircuitPathMode = ElectricalCircuitPathMode.Custom;
                        sys.SetCircuitPath(pathVerts);
                        pathSet = true;
                    }
                    catch (Exception ex)
                    {
                        pathError = ex.Message;   // reported, never fatal — the
                        // conduits/wires above are still worth keeping
                    }
                }

                TxGuard.CommitOrThrow(tx);

                var row = new Dictionary<string, object?>
                {
                    ["index"] = r.Index,
                    ["circuit_id"] = r.CircuitId,
                    ["circuit_number"] = r.CircuitNumber,
                    ["conduit_ids"] = conduitIds.Cast<object>().ToList(),
                    ["fitting_ids"] = fittingIds.Cast<object>().ToList(),
                    ["unconnected_joints"] = unconnected,
                    ["wire_ids"] = wireIds.Cast<object>().ToList(),
                    ["total_length_mm"] = Math.Round(r.TotalLengthMm),
                    ["wire_csa_mm2"] = r.WireCsaMm2,
                    ["conduit_diameter_mm"] = r.ConduitDiameterMm,
                    ["circuit_path_set"] = pathSet,
                };
                if (wireSkip != null) row["wires_skipped_reason"] = wireSkip;
                if (pathError != null) row["circuit_path_error"] = pathError;
                return row;
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── connector helpers ──────────────────────────────────────────
        private static Connector? ConnectorNear(Conduit conduit, double[] jointMm)
        {
            Connector? best = null;
            double bestDist = double.MaxValue;
            foreach (Connector c in conduit.ConnectorManager.Connectors)
            {
                double dx = c.Origin.X * MmPerFoot - jointMm[0];
                double dy = c.Origin.Y * MmPerFoot - jointMm[1];
                double dz = c.Origin.Z * MmPerFoot - jointMm[2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return bestDist <= JointTolMm ? best : null;
        }

        private static Connector? DeviceConnector(Document doc, long deviceId)
            => doc.GetElement(ElemIds.From(deviceId)) is FamilyInstance fi
                ? FirstElectricalConnector(fi)
                : null;

        /// <summary>First electrical connector, or null — Wire.Create accepts
        /// null end connectors, so an unconnectable end degrades to a loose
        /// wire end rather than failing the hop.</summary>
        private static Connector? FirstElectricalConnector(FamilyInstance fi)
        {
            var cm = fi.MEPModel?.ConnectorManager;
            if (cm == null) return null;
            foreach (Connector c in cm.Connectors)
                if (c.Domain == Domain.DomainElectrical) return c;
            return null;
        }
    }
}
