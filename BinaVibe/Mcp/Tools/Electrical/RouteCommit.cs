// create_circuit_routes — the write half of routing. MUTATE: the addin's
// ConfirmGate shows a Ya/Tidak card before this runs.
//
// Takes a plan_id + indices, never coordinates. Circuits with obstructed legs
// are SKIPPED by default (skipping single legs would break daisy-chain
// continuity); include_obstructed=true builds them after review.
//
// One Transaction per circuit inside a TransactionGroup, so a half-routed
// circuit rolls back to nothing while the others survive. What it builds, all
// best-effort and individually reported: conduit + joint fittings
// (.Conduits.cs), wires (.Wires.cs), the circuit path (.CircuitPath.cs). All
// three run INSIDE CommitOne's transaction and open none of their own.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static partial class RouteCommit
    {
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
                return ToolResult.Fail($"no routes selected from plan {planId} " +
                    $"(plan holds {plan.Routes.Count} routes; indices are 0-based)");

            bool includeObstructed = ArgsHelp.GetBool(args, "include_obstructed") ?? false;
            bool createWires = ArgsHelp.GetBool(args, "create_wires") ?? true;
            bool createConduits = ArgsHelp.GetBool(args, "create_conduits") ?? true;
            bool connectConduits = ArgsHelp.GetBool(args, "connect_conduits") ?? true;
            bool setCircuitPath = ArgsHelp.GetBool(args, "set_circuit_path") ?? true;
            // OFF by default: the no-dive path omits the per-device drops, and
            // check_circuit_loads cannot tell the shapes apart (it reads only
            // CircuitPathMode == Custom), so every voltage drop would go
            // optimistic with no way to notice.
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
                    return ToolResult.Fail(conduitTypeName != null
                        ? $"conduit type '{conduitTypeName}' not found (use list_family_types(\"OST_Conduit\"))"
                        : "no conduit types in project");
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

            // One pass, in route order, so the skipped[] rows keep their order.
            var toCommit = new List<PlannedRoute>();
            foreach (var r in routes)
            {
                if (r.ObstructedLegCount > 0 && !includeObstructed)
                    skipped.Add(new Dictionary<string, object?>
                    {
                        ["index"] = r.Index,
                        ["circuit_id"] = r.CircuitId,
                        ["reason"] = "obstructed_legs",
                        ["obstructed_leg_count"] = r.ObstructedLegCount,
                    });
                else
                    toCommit.Add(r);
            }

            TxGuard.ForEachInGroup(doc, "BinaVibe: create_circuit_routes", toCommit,
                r => created.Add(CommitOne(doc, r, conduitType, wireType, wireView,
                                           wiresSkippedReason, createConduits, createWires,
                                           connectConduits, setCircuitPath, allowFlatPath)),
                (r, ex) => failed.Add(new Dictionary<string, object?>
                {
                    ["index"] = r.Index,
                    ["circuit_id"] = r.CircuitId,
                    ["reason"] = ex.Message,
                }));

            // ok:false when nothing was routed. This used to be an unconditional
            // true, so a run where every route failed — or every route was
            // skipped as obstructed — returned {ok:true, count:0} and an
            // unattended loop branching on ok read that as done. Mirrors
            // create_circuits, which carries the same pair of keys; the second
            // branch is this tool's own, because a route can be skipped before
            // anything is attempted and "all attempts failed" would then be a
            // lie that sends the agent looking at failed[] for an empty list.
            return new Dictionary<string, object?>
            {
                ["ok"] = created.Count > 0,
                ["plan_id"] = planId,
                ["count"] = created.Count,
                ["created"] = created,
                ["skipped"] = skipped,
                ["failed"] = failed,
                ["fittings_failed"] = created
                    .OfType<Dictionary<string, object?>>()
                    .Sum(r => ((List<object>)(r["unconnected_joints"] ?? new List<object>())).Count),
                ["error"] = created.Count > 0 ? null
                    : failed.Count > 0
                        ? "no circuit was routed — all " + failed.Count +
                          " attempt(s) failed; see failed[] for each reason"
                        : "no circuit was routed — all " + skipped.Count +
                          " selected route(s) were skipped before anything was attempted; " +
                          "see skipped[] (pass include_obstructed:true to build them anyway)",
            };
        }

        /// <summary>Everything one circuit needs, in one transaction: conduit,
        /// wire, circuit path. Each builder reports what it managed rather than
        /// throwing, because a circuit with conduit but no wire is a real and
        /// reportable outcome — only a failure to reach the model at all throws
        /// out of here and lands the route in failed[].</summary>
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
                var conduit = createConduits && conduitType != null
                    ? BuildConduits(doc, r, conduitType, levelId, connectConduits)
                    : new ConduitOutcome();

                var wire = createWires && wiresSkippedReason == null
                                       && wireType != null && wireView != null
                    ? BuildWires(doc, r, sys, panel, wireType, wireView, wiresSkippedReason)
                    : new WireOutcome { SkipReason = wiresSkippedReason };

                var path = setCircuitPath
                    ? SetPath(sys, r, allowFlatPath)
                    : new PathOutcome();

                // NOTHING may move below this line except the row build, and the
                // row build may not throw: past the commit the write has already
                // reached the model, so a throw here would file a committed
                // circuit under failed[] with its conduit and wires in place.
                TxGuard.CommitOrThrow(tx);

                var row = new Dictionary<string, object?>
                {
                    ["index"] = r.Index,
                    ["circuit_id"] = r.CircuitId,
                    ["circuit_number"] = r.CircuitNumber,
                    ["conduit_ids"] = conduit.ConduitIds.Cast<object>().ToList(),
                    ["fitting_ids"] = conduit.FittingIds.Cast<object>().ToList(),
                    ["unconnected_joints"] = conduit.UnconnectedJoints,
                    ["wire_ids"] = wire.WireIds.Cast<object>().ToList(),
                    // Attempted vs wired: a wire failure mid-chain leaves the
                    // circuit partly wired and still commits, so the bare
                    // reason string alone reads as "no wires were made".
                    ["hops_total"] = r.Hops.Count,
                    ["hops_attempted"] = wire.HopsAttempted,
                    ["hops_wired"] = wire.WireIds.Count,
                    ["wires_partial"] = wire.SkipReason != null && wire.WireIds.Count > 0,
                    ["total_length_mm"] = Math.Round(r.TotalLengthMm),
                    ["wire_csa_mm2"] = r.WireCsaMm2,
                    ["conduit_diameter_mm"] = r.ConduitDiameterMm,
                    ["circuit_path_set"] = path.Set,
                };
                if (wire.SkipReason != null) row["wires_skipped_reason"] = wire.SkipReason;
                if (wire.Debug != null) row["wire_failure_geometry"] = wire.Debug;
                if (path.Error != null) row["circuit_path_error"] = path.Error;
                if (path.Shape != null) row["circuit_path_shape"] = path.Shape;
                if (path.Nodes != null) row["circuit_path_nodes_mm"] = path.Nodes;
                if (path.FlatNodes != null) row["circuit_path_flat_nodes_mm"] = path.FlatNodes;
                return row;
            }
            catch { TxGuard.SafeRollBack(tx); throw; }
        }

        // ─── what each builder reports back ─────────────────────────────

        private sealed class ConduitOutcome
        {
            public List<long> ConduitIds = new();
            /// <summary>Fittings actually inserted — NOT joints. A joint that
            /// resolved by connecting two ends directly has no fitting.</summary>
            public List<long> FittingIds = new();
            public List<object> UnconnectedJoints = new();
        }

        private sealed class WireOutcome
        {
            public List<long> WireIds = new();
            public string? SkipReason;
            /// <summary>Stations and connector origins for a rejected hop, so
            /// the next run diagnoses the geometry instead of guessing.</summary>
            public Dictionary<string, object?>? Debug;
            /// <summary>Hops entered, including ones skipped as undrawable.</summary>
            public int HopsAttempted;
        }

        private sealed class PathOutcome
        {
            public bool Set;
            public string? Error;
            /// <summary>"dive" | "flat" — which shape Revit accepted.</summary>
            public string? Shape;
            public List<object>? Nodes;
            public List<object>? FlatNodes;
        }
    }
}
