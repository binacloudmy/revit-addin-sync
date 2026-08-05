// suggest_circuit_routes — propose Manhattan conduit+wire routes per circuit.
//
// READ-ONLY. No Transaction is ever opened here; this tool proposes, the
// drafter reviews (including per-leg obstruction rows), and
// create_circuit_routes (Electrical/RouteCommit.cs) commits. INSPECT for the
// same confirm-fatigue reason as the other suggest_* tools.
//
// Path generation is pluggable: RoutePath.cs defines IRoutePathStrategy and
// the `strategy` arg selects an implementation ("manhattan" today). A future
// A* lands as a new class + a name here — nothing else changes.
//
// COLLISION POLICY (product decision): horizontal runs at the routing
// elevation are scanned with exactly check_corridor's arithmetic
// (CorridorCheck.ScanSegment); obstructions are REPORTED per leg, never
// auto-rerouted. Rises/drops down to devices are NOT scanned — a drop to a
// wall-mounted socket always grazes its own host wall, and that noise would
// bury real obstructions. The result says which legs were probed.
//
// THIS FILE IS A ft<->mm BOUNDARY: Revit reads in feet, everything handed to
// RoutePath/WireSizing is mm.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.Electrical.ElecReads;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class RoutePlanner
    {
        private const int DefaultMaxHitsPerLeg = 20;

        public static Dictionary<string, object?> Suggest(Document doc, JsonElement args)
        {
            // ── required standards args ───────────────────────────────────
            var clearanceMm = ArgsHelp.GetDouble(args, "clearance_mm");
            var routingHeightMm = ArgsHelp.GetDouble(args, "routing_height_mm");
            var tableRows = ReadSizingTable(args, out var tableError);

            var missing = new List<string>();
            if (!clearanceMm.HasValue) missing.Add("clearance_mm");
            if (!routingHeightMm.HasValue) missing.Add("routing_height_mm");
            if (tableRows == null && tableError == null) missing.Add("sizing_table");
            if (missing.Count > 0)
                return ToolResult.FailMissingArgs(
                    missing, "standards args", "electrical design standards",
                    "electrical_circuiting");
            if (tableError != null)
                return ToolResult.Fail(tableError);

            List<SizingRow> sizing;
            try { sizing = WireSizing.ParseTable(tableRows!); }
            catch (ArgumentException ex)
            {
                return ToolResult.Fail(ex.Message);
            }

            var strategyName = ArgsHelp.GetString(args, "strategy");
            var strategy = RouteStrategies.ByName(strategyName);
            if (strategy == null)
                return ToolResult.Fail("unknown strategy '" + strategyName + "'",
                    new Dictionary<string, object?>
                    {
                        ["supported"] = RouteStrategies.SupportedNames.Cast<object>().ToList(),
                    });

            bool includeLinks = ArgsHelp.GetBool(args, "include_links") ?? true;
            bool probeObstacles = ArgsHelp.GetBool(args, "probe_obstacles") ?? false;
            int maxHitsPerLeg = (int)(ArgsHelp.GetLong(args, "max_hits_per_leg") ?? DefaultMaxHitsPerLeg);
            var cats = ResolveScanCategories(args, out var unknownCats);
            if (unknownCats.Count > 0)
                return ToolResult.Fail("unknown categories: " + string.Join(", ", unknownCats),
                    new Dictionary<string, object?>
                    {
                        ["supported"] = CorridorCheck.Cats.Keys.Cast<object>().ToList(),
                    });

            // ── circuits ──────────────────────────────────────────────────
            var wanted = ArgsHelp.GetLongList(args, "circuit_ids");
            var systems = ResolveCircuits(doc, wanted, out var skippedCircuits);

            var plan = new RoutePlan
            {
                RoutingElevationMm = routingHeightMm!.Value,
                ParamsUsed = new Dictionary<string, object?>
                {
                    ["clearance_mm"] = clearanceMm!.Value,
                    ["routing_height_mm"] = routingHeightMm.Value,
                    ["strategy"] = strategyName ?? "manhattan",
                    ["include_links"] = includeLinks,
                    ["probe_obstacles"] = probeObstacles,
                    ["sizing_table_rows"] = sizing.Count,
                },
            };

            var linksUnloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyTruncated = false;
            int index = 0;

            foreach (var sys in systems)
            {
                var route = PlanOne(doc, sys, strategy, sizing,
                                    routingHeightMm.Value, clearanceMm.Value, cats,
                                    includeLinks, probeObstacles, maxHitsPerLeg,
                                    linksUnloaded, ref anyTruncated, out var skipReason);
                if (route == null)
                {
                    skippedCircuits.Add(new Dictionary<string, object?>
                    {
                        ["circuit_id"] = sys.Id.Value, ["reason"] = skipReason,
                    });
                    continue;
                }
                route.Index = index++;
                plan.Routes.Add(route);
            }

            if (plan.Routes.Count == 0)
                return ToolResult.Fail("no routable circuits — every candidate was skipped " +
                    "(see skipped_circuits)",
                    new Dictionary<string, object?>
                    {
                        ["skipped_circuits"] = skippedCircuits.Cast<object>().ToList(),
                    });

            var planId = RoutePlanCache.Store(plan, SocketCandidates.DocKey(doc));

            var routeRows = plan.Routes.Select(r => (object)new Dictionary<string, object?>
            {
                ["index"] = r.Index,
                ["circuit_id"] = r.CircuitId,
                ["circuit_number"] = r.CircuitNumber,
                ["panel_id"] = r.PanelId,
                ["device_ids"] = r.DeviceIds.Cast<object>().ToList(),
                ["total_length_mm"] = Math.Round(r.TotalLengthMm),
                ["calc_amps"] = Math.Round(r.CalcAmps, 2),
                ["wire_csa_mm2"] = r.WireCsaMm2,
                ["conduit_diameter_mm"] = r.ConduitDiameterMm,
                ["sizing_error"] = r.SizingError,
                ["obstructed_leg_count"] = r.ObstructedLegCount,
                ["legs"] = r.Legs.Select(l => (object)new Dictionary<string, object?>
                {
                    ["from_mm"] = new List<object> { Math.Round(l.FromXMm), Math.Round(l.FromYMm), Math.Round(l.FromZMm) },
                    ["to_mm"] = new List<object> { Math.Round(l.ToXMm), Math.Round(l.ToYMm), Math.Round(l.ToZMm) },
                    ["length_mm"] = Math.Round(l.LengthMm),
                    ["kind"] = l.Kind,
                    ["probed"] = l.Kind == "run",
                    ["obstructions"] = l.Obstructions.Cast<object>().ToList(),
                }).ToList(),
                ["notes"] = r.Notes.Cast<object>().ToList(),
            }).ToList();

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = planId,
                ["count"] = plan.Routes.Count,
                ["params_used"] = plan.ParamsUsed,
                ["routes"] = routeRows,
                ["skipped_circuits"] = skippedCircuits.Cast<object>().ToList(),
                ["obstructed_total"] = plan.Routes.Sum(r => r.ObstructedLegCount),
                ["truncated"] = anyTruncated,
            };
            if (linksUnloaded.Count > 0)
                result["links_unloaded"] = linksUnloaded.Cast<object>().ToList();
            return result;
        }

        // ─── one circuit ────────────────────────────────────────────────
        private static PlannedRoute? PlanOne(
            Document doc, ElectricalSystem sys, IRoutePathStrategy strategy,
            IReadOnlyList<SizingRow> sizing, double routingHeightMm, double clearanceMm,
            List<BuiltInCategory> cats, bool includeLinks, bool probeObstacles,
            int maxHitsPerLeg, HashSet<string> linksUnloaded, ref bool anyTruncated,
            out string skipReason)
        {
            skipReason = "";
            var panel = sys.BaseEquipment;
            if (panel == null) { skipReason = "orphaned_circuit_no_panel"; return null; }
            if (panel.Location is not LocationPoint panelLoc) { skipReason = "panel_has_no_point_location"; return null; }

            var devices = new List<ElecDevice>();
            foreach (Element el in sys.Elements)
            {
                if (el is FamilyInstance fi && fi.Location is LocationPoint lp)
                    devices.Add(new ElecDevice
                    {
                        Id = fi.Id.Value,
                        XMm = lp.Point.X * MmPerFoot,
                        YMm = lp.Point.Y * MmPerFoot,
                        ZMm = lp.Point.Z * MmPerFoot,
                    });
            }
            if (devices.Count == 0) { skipReason = "no_point_located_devices"; return null; }

            // Routing elevation: above the panel's level, so one number in the
            // recipe means the same thing on every storey.
            double levelElevMm = (doc.GetElement(panel.LevelId) as Level)?.Elevation * MmPerFoot ?? 0.0;
            double routeZMm = levelElevMm + routingHeightMm;

            var route = new PlannedRoute
            {
                CircuitId = sys.Id.Value,
                CircuitNumber = sys.CircuitNumber ?? "",
                PanelId = panel.Id.Value,
            };
            if (levelElevMm == 0.0 && panel.LevelId == ElementId.InvalidElementId)
                route.Notes.Add("panel has no level — routing elevation treated as absolute Z");

            // Chain order: same nearest-neighbor walk the circuiting proposal
            // used, so review and route agree.
            // The panel's electrical CONNECTOR, falling back to its origin.
            // SetCircuitPath refuses a path whose first node is the panel
            // INSTANCE ORIGIN ("should be the position of the connector ...
            // but not the origin of the panel instance"), so a route built off
            // the origin can never set a circuit path — which is why every
            // routed circuit still reported its straight-line length.
            var panelMm = PanelStartPoint(sys, panel, panelLoc, out var panelStartSource);
            // A board serving several circuits offers several physical
            // connectors and nothing ties one to this circuit — so say the pick
            // is a guess rather than let a wrong home-run pass as fact.
            var panelPick = PanelConnectors.ForCircuit(sys, panel);
            if (panelPick.Ambiguous)
                route.Notes.Add(
                    "panel " + panel.Id.Value + " has " + panelPick.PhysicalCount +
                    " physical electrical connectors and BaseEquipmentConnector is logical, " +
                    "so nothing identifies which one belongs to this circuit — the first was " +
                    "used for both the home-run wire and the circuit path start. Verify the " +
                    "home run lands on the right breaker");
            if (panelStartSource == "instance_origin")
                route.Notes.Add(
                    "panel start fell back to the instance ORIGIN — no circuit or electrical " +
                    "connector could be read on panel " + panel.Id.Value + ". SetCircuitPath " +
                    "refuses a path that starts at the origin, so circuit_path_set will be false " +
                    "and voltage drop will keep using the straight-line length");
            var chain = CircuitGrouping.ChainOrder(devices, panelMm.X, panelMm.Y);
            route.DeviceIds = chain.Select(d => d.Id).ToList();

            // Elbow-variant probe (X-first vs Y-first). Probe truncation is
            // irrelevant: any hit at all fails the variant, truncated or not.
            Func<Pt3Mm, Pt3Mm, bool>? probe = null;
            if (probeObstacles)
                probe = (a, b) => CorridorCheck.ScanSegment(
                    doc, a, b, clearanceMm, cats, includeLinks, maxHitsPerLeg).Rows.Count == 0;

            // ── trunk + one drop per device ───────────────────────────────
            // The topology lives in RouteAssembly (Revit-free, unit-tested):
            // rise once at the panel, run at routing elevation through each
            // device's XY, drop once onto each device. The strategy is asked
            // only for the HORIZONTAL travel between two points already at
            // routing elevation, which is why its own rise/drop never fires.
            var assembled = RouteAssembly.Build(
                panelMm, routeZMm,
                chain.Select(d => (d.Id, new Pt3Mm(d.XMm, d.YMm, d.ZMm))).ToList(),
                (a, b, preferAxis) =>
                {
                    var path = strategy.Plan(new RouteRequest
                    {
                        Start = a, End = b, RoutingElevationMm = routeZMm, IsClear = probe,
                        PreferFirstAxis = preferAxis,
                    });
                    route.Notes.AddRange(path.Notes);
                    return path.Ok ? path.Vertices : new List<Pt3Mm>();
                });
            route.Notes.AddRange(assembled.Notes);

            // The builder owns the chain position. A device whose runs
            // collapsed to nothing must advance it WITHOUT emitting a wire, or
            // every later wire connects the wrong pair of devices — silently,
            // because Wire.Create tolerates mismatched connectors.
            var hops = new RouteHopBuilder();
            foreach (var leg in assembled.Legs)
            {
                var a = leg.A;
                var b = leg.B;
                var rl = new RouteLeg
                {
                    FromXMm = a.X, FromYMm = a.Y, FromZMm = a.Z,
                    ToXMm = b.X, ToYMm = b.Y, ToZMm = b.Z,
                    LengthMm = Dist(a, b),
                    Kind = leg.Kind,
                    DropsToDeviceId = leg.DropsToDeviceId,
                };

                if (rl.Kind == "run")
                {
                    var scan = CorridorCheck.ScanSegment(doc, a, b, clearanceMm, cats,
                                                         includeLinks, maxHitsPerLeg);
                    rl.Obstructions = scan.Rows;
                    if (scan.Truncated) anyTruncated = true;
                    foreach (var lu in scan.LinksUnloaded) linksUnloaded.Add(lu);
                    if (scan.Rows.Count > 0) route.ObstructedLegCount++;
                }

                route.TotalLengthMm += rl.LengthMm;
                route.Legs.Add(rl);
            }
            foreach (var (deviceId, startLeg, endLeg) in assembled.HopRanges)
            {
                if (endLeg < startLeg) hops.SkipHop(deviceId);
                else hops.AddHop(deviceId, startLeg, endLeg);
            }
            route.Hops = hops.Hops;
            route.PathVerticesMm = assembled.PathVertices;
            route.PathVerticesFlatMm = assembled.PathVerticesFlat;

            if (route.Legs.Count == 0) { skipReason = "no_legs_generated"; return null; }

            // ── sizing from the circuit's own load + voltage ──────────────
            var loadParam = sys.get_Parameter(BuiltInParameter.RBS_ELEC_APPARENT_LOAD);
            var voltParam = sys.get_Parameter(BuiltInParameter.RBS_ELEC_VOLTAGE);
            double va = loadParam != null && loadParam.HasValue
                ? UnitUtils.ConvertFromInternalUnits(loadParam.AsDouble(), UnitTypeId.VoltAmperes)
                : 0.0;
            double volts = voltParam != null && voltParam.HasValue
                ? UnitUtils.ConvertFromInternalUnits(voltParam.AsDouble(), UnitTypeId.Volts)
                : 0.0;
            route.ThreePhase = SafePoles(sys) == 3;
            if (va <= 0 || volts <= 0)
            {
                route.SizingError = "no_load_or_voltage_on_circuit";
                route.Notes.Add("circuit reports no apparent load/voltage — wire not sized");
            }
            else
            {
                route.CalcAmps = WireSizing.CalcAmps(va, volts, route.ThreePhase);
                var row = WireSizing.Pick(route.CalcAmps, sizing);
                if (row == null)
                {
                    route.SizingError = "no_adequate_size";
                    route.Notes.Add("no sizing_table row covers " +
                                    Math.Round(route.CalcAmps, 1) + " A");
                }
                else
                {
                    route.WireCsaMm2 = row.WireCsaMm2;
                    route.ConduitDiameterMm = row.ConduitDiameterMm;
                    route.MvPerAM = row.MvPerAM;
                }
            }
            return route;
        }

        // ─── helpers ────────────────────────────────────────────────────
        private static List<ElectricalSystem> ResolveCircuits(
            Document doc, List<long> wanted, out List<Dictionary<string, object?>> skipped)
        {
            skipped = new List<Dictionary<string, object?>>();
            var systems = new List<ElectricalSystem>();
            if (wanted.Count > 0)
            {
                foreach (var id in wanted)
                {
                    if (doc.GetElement(ElemIds.From(id)) is ElectricalSystem sys &&
                        sys.SystemType == ElectricalSystemType.PowerCircuit)
                        systems.Add(sys);
                    else
                        skipped.Add(new Dictionary<string, object?>
                        {
                            ["circuit_id"] = id, ["reason"] = "not_a_power_circuit",
                        });
                }
            }
            else
            {
                systems.AddRange(new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem))
                    .Cast<ElectricalSystem>()
                    .Where(s => s.SystemType == ElectricalSystemType.PowerCircuit)
                    .OrderBy(s => s.Id.Value));
            }
            return systems;
        }

        private static List<BuiltInCategory> ResolveScanCategories(
            JsonElement args, out List<string> unknown)
        {
            unknown = new List<string>();
            var words = ArgsHelp.GetStringList(args, "categories");
            if (words.Count == 0) return CorridorCheck.Cats.Values.ToList();
            var found = new List<BuiltInCategory>();
            foreach (var w in words)
            {
                if (CorridorCheck.Cats.TryGetValue(w.Trim().ToLowerInvariant(), out var bic))
                    found.Add(bic);
                else unknown.Add(w);
            }
            return found;
        }

        private static List<IReadOnlyDictionary<string, double>>? ReadSizingTable(
            JsonElement args, out string? error)
        {
            error = null;
            if (args.ValueKind != JsonValueKind.Object ||
                !args.TryGetProperty("sizing_table", out var v))
                return null;
            if (v.ValueKind != JsonValueKind.Array)
            {
                error = "sizing_table must be an array of {max_current_a, wire_csa_mm2, " +
                        "conduit_diameter_mm, mv_per_a_m} rows";
                return null;
            }
            var rows = new List<IReadOnlyDictionary<string, double>>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = "sizing_table rows must be objects, not " + item.ValueKind;
                    return null;
                }
                var row = new Dictionary<string, double>();
                foreach (var p in item.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number &&
                        p.Value.TryGetDouble(out var d))
                        row[p.Name] = d;
                rows.Add(row);
            }
            return rows;
        }


        private static double Dist(Pt3Mm a, Pt3Mm b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Where the route leaves the panel: its electrical connector,
        /// or the instance origin when the family exposes none.
        ///
        /// SetCircuitPath is explicit that the first node "should be the
        /// position of the connector (the one connects to the circuit) of the
        /// panel, but not the origin of the panel instance" — a path built off
        /// the origin is rejected as invalid, which is half of why no routed
        /// circuit has ever carried its routed length.</summary>
        private static Pt3Mm PanelStartPoint(ElectricalSystem sys, FamilyInstance panel,
                                             LocationPoint panelLoc, out string source)
        {
            // ONE picker, shared with the home-run wire in RouteCommit. Picking
            // separately let the path start at one connector while the wire
            // attached to another — Revit accepts both silently, so the model
            // goes wrong with no error to read.
            var pick = PanelConnectors.ForCircuit(sys, panel);
            if (pick.Connector != null)
            {
                try
                {
                    var o = pick.Connector.Origin;
                    source = pick.Source;
                    return new Pt3Mm(o.X * MmPerFoot, o.Y * MmPerFoot, o.Z * MmPerFoot);
                }
                catch { /* fall through to the origin */ }
            }

            // Reported, never silent: a path from here is rejected outright,
            // and "circuit_path_set: false" with no reason is what sent the
            // last run guessing at panel connector resolution.
            source = "instance_origin";
            return new Pt3Mm(panelLoc.Point.X * MmPerFoot,
                             panelLoc.Point.Y * MmPerFoot,
                             panelLoc.Point.Z * MmPerFoot);
        }
    }
}
