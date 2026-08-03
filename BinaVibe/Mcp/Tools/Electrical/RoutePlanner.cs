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

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class RoutePlanner
    {
        private const double MmPerFoot = 304.8;
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
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "missing required standards args: " + string.Join(", ", missing) +
                                ". These are electrical design standards, not defaults the addin " +
                                "may assume — take the values from the electrical_circuiting " +
                                "recipe and pass them explicitly.",
                };
            if (tableError != null)
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = tableError };

            List<SizingRow> sizing;
            try { sizing = WireSizing.ParseTable(tableRows!); }
            catch (ArgumentException ex)
            {
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.Message };
            }

            var strategyName = ArgsHelp.GetString(args, "strategy");
            var strategy = RouteStrategies.ByName(strategyName);
            if (strategy == null)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "unknown strategy '" + strategyName + "'",
                    ["supported"] = RouteStrategies.SupportedNames.Cast<object>().ToList(),
                };

            bool includeLinks = ArgsHelp.GetBool(args, "include_links") ?? true;
            bool probeObstacles = ArgsHelp.GetBool(args, "probe_obstacles") ?? false;
            int maxHitsPerLeg = (int)(ArgsHelp.GetLong(args, "max_hits_per_leg") ?? DefaultMaxHitsPerLeg);
            var cats = ResolveScanCategories(args, out var unknownCats);
            if (unknownCats.Count > 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "unknown categories: " + string.Join(", ", unknownCats),
                    ["supported"] = CorridorCheck.Cats.Keys.Cast<object>().ToList(),
                };

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
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "no routable circuits — every candidate was skipped " +
                                "(see skipped_circuits)",
                    ["skipped_circuits"] = skippedCircuits.Cast<object>().ToList(),
                };

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
            var panelMm = new Pt3Mm(panelLoc.Point.X * MmPerFoot,
                                    panelLoc.Point.Y * MmPerFoot,
                                    panelLoc.Point.Z * MmPerFoot);
            var chain = CircuitGrouping.ChainOrder(devices, panelMm.X, panelMm.Y);
            route.DeviceIds = chain.Select(d => d.Id).ToList();

            // Elbow-variant probe (X-first vs Y-first). Probe truncation is
            // irrelevant: any hit at all fails the variant, truncated or not.
            Func<Pt3Mm, Pt3Mm, bool>? probe = null;
            if (probeObstacles)
                probe = (a, b) => CorridorCheck.ScanSegment(
                    doc, a, b, clearanceMm, cats, includeLinks, maxHitsPerLeg).Rows.Count == 0;

            // ── hops: panel -> dev0 -> dev1 -> ... ────────────────────────
            var from = panelMm;
            foreach (var dev in chain)
            {
                var to = new Pt3Mm(dev.XMm, dev.YMm, dev.ZMm);
                var path = strategy.Plan(new RouteRequest
                {
                    Start = from, End = to, RoutingElevationMm = routeZMm, IsClear = probe,
                });
                route.Notes.AddRange(path.Notes);
                if (!path.Ok || path.Vertices.Count < 2)
                {
                    from = to;
                    continue;   // coincident points — nothing to build for this hop
                }

                route.HopStartLegIndex.Add(route.Legs.Count);
                for (int i = 1; i < path.Vertices.Count; i++)
                {
                    var a = path.Vertices[i - 1];
                    var b = path.Vertices[i];
                    var leg = new RouteLeg
                    {
                        FromXMm = a.X, FromYMm = a.Y, FromZMm = a.Z,
                        ToXMm = b.X, ToYMm = b.Y, ToZMm = b.Z,
                        LengthMm = Dist(a, b),
                        Kind = Math.Abs(a.Z - b.Z) > 0.5
                            ? (b.Z > a.Z ? "rise" : "drop")
                            : "run",
                    };

                    if (leg.Kind == "run")
                    {
                        var scan = CorridorCheck.ScanSegment(doc, a, b, clearanceMm, cats,
                                                             includeLinks, maxHitsPerLeg);
                        leg.Obstructions = scan.Rows;
                        if (scan.Truncated) anyTruncated = true;
                        foreach (var lu in scan.LinksUnloaded) linksUnloaded.Add(lu);
                        if (scan.Rows.Count > 0) route.ObstructedLegCount++;
                    }

                    route.TotalLengthMm += leg.LengthMm;
                    route.Legs.Add(leg);
                }
                from = to;
            }

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

        private static int SafePoles(ElectricalSystem sys)
        {
            try { return sys.PolesNumber; }
            catch { return 1; }
        }

        private static double Dist(Pt3Mm a, Pt3Mm b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
