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
            // OFF by default, and deliberately so. The no-dive path omits the
            // per-device drops, so Revit's circuit length comes out SHORTER
            // than the conductor really runs — and check_circuit_loads cannot
            // tell the shapes apart: it reads CircuitPathMode == Custom and
            // nothing else (ElecValidation.cs), so a fallback path would make
            // every voltage drop optimistic with no way to notice. Opt in when
            // an approximate routed length beats none.
            bool allowFlatPath = ArgsHelp.GetBool(args, "allow_flat_circuit_path") ?? false;
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
                                              connectConduits, setCircuitPath, allowFlatPath));
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
            catch { TxGuard.SafeRollBack(group); throw; }

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
            bool createWires, bool connectConduits, bool setCircuitPath,
            bool allowFlatPath)
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
                        // Joints are found by STATION, not by leg adjacency. On
                        // the trunk a device station has THREE conduits meeting
                        // — the run in, the run out, and the branch drop — and
                        // pairing legs by index would try to elbow two of them
                        // and silently leave the third hanging. Grouping by
                        // shared endpoint gets the arity right, and the arity is
                        // what decides elbow vs tee.
                        foreach (var station in JointStations(r.Legs, conduits))
                        {
                            try
                            {
                                fittingIds.Add(Join(doc, station));
                            }
                            catch (Exception ex)
                            {
                                unconnected.Add(new Dictionary<string, object?>
                                {
                                    ["at_mm"] = station.AtMm.Select(v => (object)Math.Round(v)).ToList(),
                                    ["conduits_meeting"] = station.Connectors.Count,
                                    ["reason"] = ex.Message,
                                });
                            }
                        }
                        fittingIds.RemoveAll(id => id == 0);
                    }
                }

                var wireIds = new List<long>();
                string? wireSkip = wiresSkippedReason;
                Dictionary<string, object?>? wireDebug = null;
                int hopsAttempted = 0;
                if (createWires && wireSkip == null && wireType != null && wireView != null)
                {
                    // One Wire per hop: panel->dev0, dev0->dev1, ... Each hop
                    // names its own two ends; they are NEVER derived from the
                    // loop index, because a hop that produced no legs is absent
                    // from this list while DeviceIds still holds the full chain.
                    foreach (var hop in r.Hops)
                    {
                        hopsAttempted++;

                        // A Wire is drawn in a PLAN view, so only XY matters.
                        // Passing every leg endpoint fed it the rise and drop
                        // twice over — consecutive duplicate points, which
                        // Wire.Create rejects. Collapse to distinct XY stations
                        // first; the Z is the view's, not the route's.
                        var stations = new List<(double XMm, double YMm)>();
                        for (int i = hop.StartLegIndex; i <= hop.EndLegIndex; i++)
                        {
                            var leg = r.Legs[i];
                            if (stations.Count == 0) stations.Add((leg.FromXMm, leg.FromYMm));
                            stations.Add((leg.ToXMm, leg.ToYMm));
                        }
                        var distinct = WirePath.DistinctStations(stations);

                        // Connectors are resolved BEFORE the vertex list, not
                        // after: the list Revit wants is defined relative to
                        // them. The home run starts at THIS circuit's connector
                        // on the panel — a distribution board carries one per
                        // circuit, so "the panel's first electrical connector"
                        // is the right one only by luck, and Wire.Create
                        // accepts a mismatched connector without complaint.
                        var startConn = hop.FromDeviceId == 0
                            ? SafeBaseConnector(sys, panel)
                            : DeviceConnector(doc, hop.FromDeviceId);
                        var endConn = DeviceConnector(doc, hop.ToDeviceId);

                        // Revit builds [startConnector] + vertexPoints +
                        // [endConnector] and rejects the result if any pair is
                        // coincident in X and Y. Our stations START on the
                        // start connector and END on the end connector, so
                        // every hop of every circuit handed it a duplicate at
                        // both ends — 0 wires on every UAT run to date. Pass
                        // the INTERIOR stations only.
                        var interior = WirePath.InteriorStations(
                            distinct, ConnXyMm(startConn), ConnXyMm(endConn));
                        if (!WirePath.IsDrawable(interior, ConnXyMm(startConn), ConnXyMm(endConn)))
                        {
                            // Panel and device share a plan position — there is
                            // no line to draw. Not a failure, and it must not
                            // abort the remaining hops.
                            continue;
                        }
                        var verts = interior
                            .Select(s => new XYZ(s.XMm / MmPerFoot, s.YMm / MmPerFoot, 0.0))
                            .ToList();

                        try
                        {
                            var wire = Wire.Create(doc, wireType.Id, wireView.Id,
                                                   WiringType.Chamfer, verts, startConn, endConn);
                            wireIds.Add(wire.Id.Value);
                        }
                        catch (Exception ex)
                        {
                            // Stop, but do not pretend the earlier hops are not
                            // there: the transaction still commits, so this
                            // circuit is left PARTLY wired. The counts below say
                            // so — one bare reason string used to imply the
                            // whole circuit had been skipped.
                            wireSkip = "wire_create_failed after " + wireIds.Count + " of " +
                                       r.Hops.Count + " hop(s): " + ex.Message;
                            // The geometry Revit rejected, so the next run
                            // diagnoses this instead of guessing. Wire.Create
                            // failing on EVERY hop of circuits with different
                            // shapes (UAT 2026-08-04) points at the vertex list
                            // versus the two end connectors, not at any one
                            // coordinate — and that is only decidable with the
                            // numbers in hand.
                            wireDebug = new Dictionary<string, object?>
                            {
                                ["hop_from_device_id"] = hop.FromDeviceId,
                                ["hop_to_device_id"] = hop.ToDeviceId,
                                ["stations_mm"] = distinct
                                    .Select(s => (object)new List<object>
                                    {
                                        Math.Round(s.XMm, 1), Math.Round(s.YMm, 1),
                                    }).ToList(),
                                // What was actually passed, after trimming the
                                // stations that sit on the connectors.
                                ["interior_vertices_mm"] = interior
                                    .Select(s => (object)new List<object>
                                    {
                                        Math.Round(s.XMm, 1), Math.Round(s.YMm, 1),
                                    }).ToList(),
                                ["start_connector_mm"] = ConnOriginMm(startConn),
                                ["end_connector_mm"] = ConnOriginMm(endConn),
                                ["start_connector_found"] = startConn != null,
                                ["end_connector_found"] = endConn != null,
                            };
                            break;
                        }
                    }
                }

                bool pathSet = false;
                string? pathError = null;
                string? pathShape = null;
                List<object>? pathNodes = null;
                List<object>? pathFlatNodes = null;
                if (setCircuitPath)
                {
                    try
                    {
                        // The ELECTRICAL path through the devices, not the
                        // conduit trunk — since the trunk stays up and takes one
                        // drop per device, the leg list is no longer a single
                        // polyline and walking it would hand Revit a jump from
                        // device height back to routing height.
                        var pathVerts = r.PathVerticesMm
                            .Select(p => new XYZ(p.X / MmPerFoot, p.Y / MmPerFoot, p.Z / MmPerFoot))
                            .ToList();
                        if (pathVerts.Count < 2)
                            throw new InvalidOperationException(
                                "route has no circuit-path polyline (re-run suggest_circuit_routes)");

                        // NO `CircuitPathMode = Custom` before this. The setter
                        // throws "the circuit path does not have customized
                        // path, so CircuitPathMode cannot be set as Custom" on
                        // any circuit still in default mode — i.e. always, on a
                        // freshly created one. SetCircuitPath is documented to
                        // switch the mode to Custom ITSELF on success. The
                        // assignment was pure loss: it threw every time, the
                        // catch below swallowed it, and no circuit has ever
                        // carried its routed length into voltage drop.
                        sys.SetCircuitPath(pathVerts);
                        pathSet = true;
                        pathShape = "dive";
                    }
                    catch (Exception ex)
                    {
                        pathError = ex.Message;   // reported, never fatal — the
                        // conduits/wires above are still worth keeping

                        // Second shape, no dive-and-return. The dive path
                        // revisits an identical point three nodes later, and
                        // by round 6 of UAT every segment was axis-aligned and
                        // Revit still refused — the doubling back is the last
                        // condition left in its message. Trying rather than
                        // arguing: whichever shape lands is reported, so the
                        // next reader knows which one Revit takes instead of
                        // inferring it.
                        if (!allowFlatPath)
                        {
                            pathError += "  |  a no-dive path shape is available and NOT tried: " +
                                         "it omits the per-device drops, so Revit's circuit " +
                                         "length would come out shorter than the conductor runs " +
                                         "and check_circuit_loads cannot tell the two shapes " +
                                         "apart. Pass allow_flat_circuit_path=true to accept an " +
                                         "approximate routed length. Nodes are in " +
                                         "circuit_path_flat_nodes_mm";
                        }
                        else
                        {
                            try
                            {
                                var flat = r.PathVerticesFlatMm
                                    .Select(p => new XYZ(p.X / MmPerFoot, p.Y / MmPerFoot, p.Z / MmPerFoot))
                                    .ToList();
                                if (flat.Count >= 2)
                                {
                                    sys.SetCircuitPath(flat);
                                    pathSet = true;
                                    pathShape = "flat";
                                    // The drops are not in this path, so its
                                    // length is SHORTER than the conduit run.
                                    pathError += "  |  fell back to the no-dive path shape: " +
                                                 "circuit length now EXCLUDES the per-device " +
                                                 "drops, so voltage drop is computed on a " +
                                                 "shorter run than the conduit";
                                }
                            }
                            catch (Exception ex2)
                            {
                                pathError += "  |  no-dive shape also refused: " + ex2.Message;
                            }
                        }

                        // Revit's rejection lists FIVE conditions at once (first
                        // node must be the panel's circuit connector, adjacent
                        // nodes not too close, every segment horizontal or
                        // vertical, …) and never says which one failed. Without
                        // the nodes it handed back, the next round is guesswork
                        // — the same trap the wire failure took three rounds to
                        // escape.
                        // Report the shape that FAILED. When the fallback took,
                        // that is the dive path; when neither took, this is
                        // still the dive path and the flat one goes out beside
                        // it, because then both are evidence.
                        pathNodes = NodeRows(r.PathVerticesMm);
                        if (!pathSet) pathFlatNodes = NodeRows(r.PathVerticesFlatMm);
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
                    // Attempted vs wired: a wire failure mid-chain leaves the
                    // circuit partly wired and still commits, so the bare
                    // reason string alone reads as "no wires were made".
                    ["hops_total"] = r.Hops.Count,
                    ["hops_attempted"] = hopsAttempted,
                    ["hops_wired"] = wireIds.Count,
                    ["wires_partial"] = wireSkip != null && wireIds.Count > 0,
                    ["total_length_mm"] = Math.Round(r.TotalLengthMm),
                    ["wire_csa_mm2"] = r.WireCsaMm2,
                    ["conduit_diameter_mm"] = r.ConduitDiameterMm,
                    ["circuit_path_set"] = pathSet,
                };
                if (wireSkip != null) row["wires_skipped_reason"] = wireSkip;
                if (wireDebug != null) row["wire_failure_geometry"] = wireDebug;
                if (pathError != null) row["circuit_path_error"] = pathError;
                if (pathShape != null) row["circuit_path_shape"] = pathShape;
                if (pathNodes != null) row["circuit_path_nodes_mm"] = pathNodes;
                if (pathFlatNodes != null) row["circuit_path_flat_nodes_mm"] = pathFlatNodes;
                return row;
            }
            catch { TxGuard.SafeRollBack(tx); throw; }
        }

        // ─── joints ─────────────────────────────────────────────────────

        /// <summary>One point where two or more conduit ends meet.</summary>
        private sealed class JointStation
        {
            public double[] AtMm = new double[3];
            public List<Connector> Connectors = new();
            /// <summary>Connector belonging to the branch drop, when this is a
            /// trunk station a device hangs off. NewTeeFitting wants the branch
            /// as its THIRD argument, so it cannot be found by position later.</summary>
            public Connector? Branch;
        }

        /// <summary>Group conduit endpoints by shared position. Endpoints that
        /// are ends of the whole run (the panel connector, each device) are
        /// left out — one conduit at a station is nothing to join.</summary>
        private static List<JointStation> JointStations(
            IReadOnlyList<RouteLeg> legs, IReadOnlyList<Conduit> conduits)
        {
            var stations = new List<JointStation>();

            void Add(double[] at, Conduit conduit, bool isBranch)
            {
                var st = stations.FirstOrDefault(s =>
                    Math.Abs(s.AtMm[0] - at[0]) <= JointTolMm &&
                    Math.Abs(s.AtMm[1] - at[1]) <= JointTolMm &&
                    Math.Abs(s.AtMm[2] - at[2]) <= JointTolMm);
                if (st == null)
                {
                    st = new JointStation { AtMm = at };
                    stations.Add(st);
                }
                var conn = ConnectorNear(conduit, at);
                if (conn == null) return;
                st.Connectors.Add(conn);
                if (isBranch) st.Branch = conn;
            }

            for (int i = 0; i < legs.Count && i < conduits.Count; i++)
            {
                var leg = legs[i];
                bool branch = leg.DropsToDeviceId != 0;
                // Only a branch drop's TOP end is a joint; its bottom lands on
                // the device.
                Add(new[] { leg.FromXMm, leg.FromYMm, leg.FromZMm }, conduits[i], branch);
                if (!branch)
                    Add(new[] { leg.ToXMm, leg.ToYMm, leg.ToZMm }, conduits[i], false);
            }

            return stations.Where(s => s.Connectors.Count >= 2).ToList();
        }

        /// <summary>Fit one station. Returns the fitting's id, or 0 when the
        /// ends were connected directly (collinear, or an elbow the conduit
        /// type cannot serve) — connected without a fitting is a real outcome,
        /// and reporting it as a failed joint is what made a working run look
        /// broken.</summary>
        private static long Join(Document doc, JointStation station)
        {
            var conns = station.Connectors;

            if (conns.Count >= 3)
            {
                // Trunk station with a branch drop. The branch MUST be the
                // third argument; Revit reads it as the tee's leg.
                var branch = station.Branch
                    ?? throw new InvalidOperationException(
                        conns.Count + " conduits meet here but none is a branch drop");
                var run = conns.Where(c => !ReferenceEquals(c, branch)).Take(2).ToList();
                if (run.Count < 2)
                    throw new InvalidOperationException("tee needs two run ends plus the branch");
                if (conns.Count > 3)
                    throw new InvalidOperationException(
                        conns.Count + " conduits meet at one point — Revit fits at most a tee; " +
                        "review this station by hand");

                // A tee is straight-through plus a branch. Two runs that TURN
                // here need a fitting that both turns and branches, and Revit
                // has none — no conduit type will supply one, so saying
                // "routing preferences lack a tee" would be a wrong diagnosis.
                // RouteAssembly keeps the trunk on one axis through a device
                // station precisely to prevent this; reaching it means an
                // obstruction probe overrode that choice.
                if (!IsCollinear(run[0], run[1]))
                {
                    // No fitting both turns and branches, but the two run ends
                    // DO turn — that is an ordinary elbow. Fitting them keeps
                    // the trunk continuous and leaves only the branch open,
                    // instead of abandoning all three ends. Previously this
                    // threw before touching anything, so one corner severed the
                    // whole run (UAT 2026-08-05).
                    string salvage;
                    try
                    {
                        var corner = doc.Create.NewElbowFitting(run[0], run[1]);
                        salvage = corner != null
                            ? "the two run ends were elbowed together, so the trunk is continuous"
                            : "the two run ends were joined, so the trunk is continuous";
                    }
                    catch
                    {
                        try
                        {
                            run[0].ConnectTo(run[1]);
                            salvage = "the two run ends were joined directly, so the trunk is continuous";
                        }
                        catch { salvage = "the two run ends could not be joined either"; }
                    }

                    throw new InvalidOperationException(
                        "the trunk TURNS at this branch station, so no single fitting can serve " +
                        "it (a tee runs straight through) — " + salvage + ", and only the BRANCH " +
                        "drop is left open. The corner was forced here by the obstruction probe; " +
                        "re-run suggest_circuit_routes with probe_obstacles off, or have the " +
                        "drafter place a junction box at this point");
                }

                try
                {
                    var tee = doc.Create.NewTeeFitting(run[0], run[1], branch);
                    return tee?.Id.Value ?? 0L;
                }
                catch (Exception ex)
                {
                    // The elbow path below has always fallen back to a direct
                    // ConnectTo when the conduit type carries no fitting; the
                    // tee path had no such fallback, so ONE missing tee left the
                    // trunk itself severed (UAT 2026-08-05, Revit's bare
                    // "failed to insert tee." at a station whose two run ends
                    // were collinear — a genuinely absent tee in the type's
                    // routing preferences, not the turn-and-branch case above).
                    //
                    // Salvage what a fitting-less run can still be: join the two
                    // run ends so the trunk stays continuous. The BRANCH cannot
                    // be joined — a connector takes one partner — so this is
                    // still reported as an open joint, now saying exactly what
                    // is open and what is not.
                    try { run[0].ConnectTo(run[1]); }
                    catch
                    {
                        throw new InvalidOperationException(
                            "no tee and the run ends would not connect either: " + ex.Message);
                    }
                    throw new InvalidOperationException(
                        "no tee fitting for this conduit type, so the trunk was joined " +
                        "through and the BRANCH drop is left open at this point. Add a tee " +
                        "to the conduit type's routing preferences (or pass conduit_type_name " +
                        "for a type that has one), then re-run. Revit said: " + ex.Message);
                }
            }

            var a = conns[0];
            var b = conns[1];

            // NewElbowFitting refuses anything outside roughly 2-95 degrees, so
            // a straight continuation has to be connected, not elbowed. That
            // rejection is what produced 8 "failed fittings" on a 9-device
            // circuit in UAT 2026-08-04 — one per device junction, when the
            // route still dropped onto a device and rose straight back off it.
            if (IsCollinear(a, b))
            {
                a.ConnectTo(b);
                return 0L;
            }

            try
            {
                var elbow = doc.Create.NewElbowFitting(a, b);
                return elbow?.Id.Value ?? 0L;
            }
            catch (Exception ex)
            {
                // A conduit type whose routing preferences carry no elbow of
                // this size still leaves a physically continuous run if the
                // ends are simply joined. Better a connected run with a noted
                // missing fitting than an open one.
                try
                {
                    a.ConnectTo(b);
                    return 0L;
                }
                catch
                {
                    throw new InvalidOperationException(
                        "no elbow and no direct connection: " + ex.Message);
                }
            }
        }

        /// <summary>Two conduit ends pointing along the same line. Connector
        /// basis Z is the direction the connector faces, so two ends that meet
        /// head-on face opposite ways — hence the absolute value.</summary>
        private static bool IsCollinear(Connector a, Connector b)
        {
            try
            {
                var da = a.CoordinateSystem.BasisZ.Normalize();
                var db = b.CoordinateSystem.BasisZ.Normalize();
                return Math.Abs(da.DotProduct(db)) > 0.999;   // within ~2.5 degrees
            }
            catch { return false; }
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

        /// <summary>The connector this circuit occupies on its panel, or null.</summary>
        /// <summary>The board-side connector for this circuit's home run.
        /// Delegates to PanelConnectors so the wire and the circuit path start
        /// can never pick differently — see that file for why
        /// BaseEquipmentConnector alone is not enough (it is logical on a
        /// panel: no Origin, and Wire.Create refuses it with "cannot be
        /// connected to a wire, as it is not an electrical connector").</summary>
        private static Connector? SafeBaseConnector(ElectricalSystem sys, FamilyInstance panel)
            => PanelConnectors.ForCircuit(sys, panel).Connector;

        private static List<object> NodeRows(IEnumerable<Pt3Mm> pts)
            => pts.Select(p => (object)new List<object>
            {
                Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(p.Z, 1),
            }).ToList();

        /// <summary>A connector's PLAN position, which is the only part of it a
        /// Wire sees. Null when there is no connector or its origin cannot be
        /// read — the vertex trimming then leaves the stations alone, because
        /// Revit is not contributing a point for it either.</summary>
        private static (double XMm, double YMm)? ConnXyMm(Connector? c)
        {
            if (c == null) return null;
            try { return (c.Origin.X * MmPerFoot, c.Origin.Y * MmPerFoot); }
            catch { return null; }
        }

        private static object? ConnOriginMm(Connector? c)
        {
            if (c == null) return null;
            try
            {
                return new List<object>
                {
                    Math.Round(c.Origin.X * MmPerFoot, 1),
                    Math.Round(c.Origin.Y * MmPerFoot, 1),
                    Math.Round(c.Origin.Z * MmPerFoot, 1),
                };
            }
            catch { return null; }
        }

        private static Connector? DeviceConnector(Document doc, long deviceId)
            => doc.GetElement(ElemIds.From(deviceId)) is FamilyInstance fi
                ? FirstElectricalConnector(fi)
                : null;

        /// <summary>First electrical connector, or null — Wire.Create accepts
        /// null end connectors, so an unconnectable end degrades to a loose
        /// wire end rather than failing the hop.</summary>
        private static Connector? FirstElectricalConnector(FamilyInstance fi)
            => PanelConnectors.FirstWireCapable(fi);
    }
}
