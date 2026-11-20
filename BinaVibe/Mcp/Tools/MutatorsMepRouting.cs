// MEP routing tools — connector-level polyline routing with real fittings.
// Spec: docs/superpowers/specs/2026-08-01-mep-routing-tools-design.md.
// list_connectors is a read; route_duct/route_pipe/tap_branch each run in
// ONE Transaction — any failure (missing type, unsupported elbow angle,
// failed snap) rolls the whole run back and reports {ok:false, error,
// failed_corner_index}. Straight-segment create_duct/create_pipe stay in
// MutatorsMep.cs; this file is additive.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using BinaVibe.Mcp.Tools.Electrical;

namespace BinaVibe.Mcp.Tools
{
    internal static class MutatorsMepRouting
    {
        private const double MmPerFoot = 304.8;
        private const double SnapToleranceMm = 50.0;

        private sealed class RouteFailure : Exception
        {
            public int CornerIndex { get; }
            public RouteFailure(string message, int cornerIndex) : base(message) => CornerIndex = cornerIndex;
        }

        public static Dictionary<string, object?> ListConnectors(Document doc, JsonElement args)
        {
            var elementId = ArgsHelp.GetLong(args, "element_id")
                ?? throw new InvalidOperationException("element_id required");
            var el = doc.GetElement(ElemIds.From(elementId))
                ?? throw new InvalidOperationException($"element {elementId} not found");
            var conns = ConnectorsOf(el);
            var list = new List<Dictionary<string, object?>>();
            for (int i = 0; i < conns.Count; i++)
                list.Add(ConnectorSummary(conns[i], i));
            return new Dictionary<string, object?> { ["ok"] = true, ["connectors"] = list, ["count"] = list.Count };
        }

        // Read-only — no Transaction. BFS over PHYSICAL connector refs (same
        // ConnectorsOf/AllRefs/ConnectorType.Physical pattern as
        // CapturePendingLinks), capped at max_depth and 100 nodes.
        public static Dictionary<string, object?> TraceConnections(Document doc, JsonElement args)
        {
            var elementId = ArgsHelp.GetLong(args, "element_id")
                ?? throw new InvalidOperationException("element_id required");
            var maxDepth = (int)(ArgsHelp.GetLong(args, "max_depth") ?? 10);
            const int maxNodes = 100;

            var start = doc.GetElement(ElemIds.From(elementId))
                ?? throw new InvalidOperationException($"element {elementId} not found");

            var visited = new HashSet<long> { elementId };
            var nodes = new List<Dictionary<string, object?>> { DescribeNode(start) };
            var edgeKeys = new HashSet<(long, long)>();
            var edges = new List<List<long>>();
            var truncated = false;

            var queue = new Queue<(Element el, int depth)>();
            queue.Enqueue((start, 0));

            while (queue.Count > 0)
            {
                var (el, depth) = queue.Dequeue();
                if (depth >= maxDepth) { truncated = true; continue; }

                List<Connector> conns;
                try { conns = ConnectorsOf(el); }
                catch (RouteFailure) { continue; }

                foreach (var c in conns)
                {
                    if (!c.IsConnected) continue;
                    foreach (Connector r in c.AllRefs)
                    {
                        if (r.Owner == null) continue;
                        if (r.Owner.Id.Value == el.Id.Value) continue;
                        if ((r.ConnectorType & ConnectorType.Physical) == 0) continue;

                        var otherId = r.Owner.Id.Value;
                        var key = el.Id.Value < otherId ? (el.Id.Value, otherId) : (otherId, el.Id.Value);
                        if (edgeKeys.Add(key))
                            edges.Add(new List<long> { key.Item1, key.Item2 });

                        if (visited.Contains(otherId)) continue;
                        if (nodes.Count >= maxNodes) { truncated = true; continue; }

                        visited.Add(otherId);
                        nodes.Add(DescribeNode(r.Owner));
                        queue.Enqueue((r.Owner, depth + 1));
                    }
                }
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["nodes"] = nodes,
                ["edges"] = edges,
                ["count"] = nodes.Count,
                ["truncated"] = truncated,
            };
        }

        public static Dictionary<string, object?> RouteDuct(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var pts = ArgsHelp.GetPointListMm(args, "points_mm");
                if (pts.Count < 2)
                    throw new RouteFailure("points_mm needs at least 2 points", -1);
                var level = ResolveLevel(doc, args);
                var typeName = ArgsHelp.GetString(args, "duct_type_name");
                var ductType = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>()
                    .FirstOrDefault(d => typeName != null
                        ? string.Equals(d.Name, typeName, StringComparison.OrdinalIgnoreCase)
                        : d.Shape == ConnectorProfileType.Rectangular)
                    ?? throw new RouteFailure(typeName != null
                        ? $"duct type '{typeName}' not found (use list_family_types(\"OST_DuctCurves\"))"
                        : "no rectangular duct types in project", -1);
                var systemTypeName = ArgsHelp.GetString(args, "system_type");
                var systemType = new FilteredElementCollector(doc).OfClass(typeof(MEPSystemType)).Cast<MEPSystemType>()
                    .FirstOrDefault(m => systemTypeName != null
                        ? string.Equals(m.Name, systemTypeName, StringComparison.OrdinalIgnoreCase)
                        : m.SystemClassification == MEPSystemClassification.SupplyAir)
                    ?? throw new RouteFailure(systemTypeName != null
                        ? $"MEP system type '{systemTypeName}' not found"
                        : "no supply-air MEP system type in project", -1);
                var widthFt = ArgsHelp.GetLengthMm(args, "width_mm");
                var heightFt = ArgsHelp.GetLengthMm(args, "height_mm");
                var connectStartTo = ArgsHelp.GetLong(args, "connect_start_to");
                var connectEndTo = ArgsHelp.GetLong(args, "connect_end_to");

                tx = new Transaction(doc, "BinaVibe: route duct");
                TxGuard.StartSwallowing(tx);

                var snapStart = ResolveSnap(doc, connectStartTo, pts[0]);
                if (snapStart != null) pts[0] = snapStart.Origin;
                var snapEnd = ResolveSnap(doc, connectEndTo, pts[pts.Count - 1]);
                if (snapEnd != null) pts[pts.Count - 1] = snapEnd.Origin;

                var segments = new List<MEPCurve>();
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Duct seg;
                    try { seg = Duct.Create(doc, systemType.Id, ductType.Id, level.Id, pts[i], pts[i + 1]); }
                    catch (Exception ex) { throw new RouteFailure($"leg {i} creation failed: {ex.Message}", i); }
                    if (widthFt.HasValue) seg.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.Set(widthFt.Value);
                    if (heightFt.HasValue) seg.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.Set(heightFt.Value);
                    segments.Add(seg);
                }

                var fittingIds = new List<long>();
                for (int corner = 1; corner < pts.Count - 1; corner++)
                    fittingIds.Add(CreateElbow(doc, segments[corner - 1], segments[corner], pts[corner], corner));

                var warnings = new List<string>();
                SnapEndpoint(segments[0], pts[0], snapStart, "start", warnings);
                SnapEndpoint(segments[segments.Count - 1], pts[pts.Count - 1], snapEnd, "end", warnings);

                var result = BuildResult(segments, fittingIds, warnings);
                TxGuard.CommitOrThrow(tx);
                return result;
            }
            catch (RouteFailure rf) { SafeRollback(tx); return Failure(rf.Message, rf.CornerIndex); }
            catch (Exception ex) { SafeRollback(tx); return Failure(ex.Message, -1); }
        }

        public static Dictionary<string, object?> RoutePipe(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var pts = ArgsHelp.GetPointListMm(args, "points_mm");
                if (pts.Count < 2)
                    throw new RouteFailure("points_mm needs at least 2 points", -1);
                var level = ResolveLevel(doc, args);
                var typeName = ArgsHelp.GetString(args, "pipe_type_name");
                var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>()
                    .FirstOrDefault(p => typeName == null
                        || string.Equals(p.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new RouteFailure(typeName != null
                        ? $"pipe type '{typeName}' not found (use list_family_types(\"OST_PipeCurves\"))"
                        : "no pipe types in project", -1);
                var systemTypeName = ArgsHelp.GetString(args, "system_type");
                var systemType = new FilteredElementCollector(doc).OfClass(typeof(MEPSystemType)).Cast<MEPSystemType>()
                    .FirstOrDefault(m => systemTypeName != null
                        ? string.Equals(m.Name, systemTypeName, StringComparison.OrdinalIgnoreCase)
                        : m.SystemClassification == MEPSystemClassification.DomesticColdWater
                          || m.SystemClassification == MEPSystemClassification.SupplyHydronic)
                    ?? throw new RouteFailure(systemTypeName != null
                        ? $"MEP system type '{systemTypeName}' not found"
                        : "no cold-water/hydronic MEP system type in project", -1);
                var diaFt = ArgsHelp.GetLengthMm(args, "diameter_mm");
                var connectStartTo = ArgsHelp.GetLong(args, "connect_start_to");
                var connectEndTo = ArgsHelp.GetLong(args, "connect_end_to");

                tx = new Transaction(doc, "BinaVibe: route pipe");
                TxGuard.StartSwallowing(tx);

                var snapStart = ResolveSnap(doc, connectStartTo, pts[0]);
                if (snapStart != null) pts[0] = snapStart.Origin;
                var snapEnd = ResolveSnap(doc, connectEndTo, pts[pts.Count - 1]);
                if (snapEnd != null) pts[pts.Count - 1] = snapEnd.Origin;

                var segments = new List<MEPCurve>();
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    Pipe seg;
                    try { seg = Pipe.Create(doc, systemType.Id, pipeType.Id, level.Id, pts[i], pts[i + 1]); }
                    catch (Exception ex) { throw new RouteFailure($"leg {i} creation failed: {ex.Message}", i); }
                    if (diaFt.HasValue) seg.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diaFt.Value);
                    segments.Add(seg);
                }

                var fittingIds = new List<long>();
                for (int corner = 1; corner < pts.Count - 1; corner++)
                    fittingIds.Add(CreateElbow(doc, segments[corner - 1], segments[corner], pts[corner], corner));

                var warnings = new List<string>();
                SnapEndpoint(segments[0], pts[0], snapStart, "start", warnings);
                SnapEndpoint(segments[segments.Count - 1], pts[pts.Count - 1], snapEnd, "end", warnings);

                var result = BuildResult(segments, fittingIds, warnings);
                TxGuard.CommitOrThrow(tx);
                return result;
            }
            catch (RouteFailure rf) { SafeRollback(tx); return Failure(rf.Message, rf.CornerIndex); }
            catch (Exception ex) { SafeRollback(tx); return Failure(ex.Message, -1); }
        }

        public static Dictionary<string, object?> TapBranch(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var mainId = ArgsHelp.GetLong(args, "main_element_id")
                    ?? throw new RouteFailure("main_element_id required", -1);
                var pts = ArgsHelp.GetPointListMm(args, "points_mm");
                if (pts.Count < 2)
                    throw new RouteFailure("points_mm needs the tap point plus at least one branch waypoint", -1);

                var mainEl = doc.GetElement(ElemIds.From(mainId))
                    ?? throw new RouteFailure($"main element {mainId} not found", -1);
                if (mainEl is not MEPCurve mainCurve)
                    throw new RouteFailure($"main element {mainId} is not a duct or pipe", -1);
                var mainConns = ConnectorsOf(mainEl);
                var domain = mainConns.FirstOrDefault()?.Domain
                    ?? throw new RouteFailure($"main element {mainId} has no connectors", -1);
                if (domain != Domain.DomainHvac && domain != Domain.DomainPiping)
                    throw new RouteFailure($"main element {mainId} is not a duct or pipe (domain {domain})", -1);
                var isDuct = domain == Domain.DomainHvac;

                var level = mainCurve.ReferenceLevel
                    ?? throw new RouteFailure($"cannot determine level from main element {mainId}", -1);
                var curveTypeId = mainEl.GetTypeId();
                var systemTypeId = mainEl.get_Parameter(isDuct
                        ? BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM
                        : BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId()
                    ?? throw new RouteFailure($"cannot determine system type from main element {mainId}", -1);
                var mainWidthFt = mainEl.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.AsDouble();
                var mainHeightFt = mainEl.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.AsDouble();
                var mainDiaFt = mainEl.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble();

                var widthFt = ArgsHelp.GetLengthMm(args, "width_mm") ?? mainWidthFt;
                var heightFt = ArgsHelp.GetLengthMm(args, "height_mm") ?? mainHeightFt;
                var diaFt = ArgsHelp.GetLengthMm(args, "diameter_mm") ?? mainDiaFt;

                var mainLine = (mainCurve.Location as LocationCurve)?.Curve as Line
                    ?? throw new RouteFailure($"main element {mainId} geometry is not a straight line", -1);
                var proj = mainLine.Project(pts[0])
                    ?? throw new RouteFailure($"tap point does not project onto main element {mainId}'s centerline", -1);
                if (proj.Distance * MmPerFoot > SnapToleranceMm)
                    throw new RouteFailure(
                        $"tap point is {proj.Distance * MmPerFoot:F0}mm off main element {mainId}'s centerline (tolerance {SnapToleranceMm:F0}mm)",
                        -1);
                pts[0] = proj.XYZPoint;

                tx = new Transaction(doc, "BinaVibe: tap branch");
                TxGuard.StartSwallowing(tx);

                var segments = new List<MEPCurve>();
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    MEPCurve seg;
                    try
                    {
                        seg = isDuct
                            ? Duct.Create(doc, systemTypeId, curveTypeId, level.Id, pts[i], pts[i + 1])
                            : Pipe.Create(doc, systemTypeId, curveTypeId, level.Id, pts[i], pts[i + 1]);
                    }
                    catch (Exception ex) { throw new RouteFailure($"branch leg {i} creation failed: {ex.Message}", i); }
                    if (widthFt.HasValue) seg.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.Set(widthFt.Value);
                    if (heightFt.HasValue) seg.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.Set(heightFt.Value);
                    if (diaFt.HasValue) seg.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diaFt.Value);
                    segments.Add(seg);
                }

                var fittingIds = new List<long>();
                for (int corner = 1; corner < pts.Count - 1; corner++)
                    fittingIds.Add(CreateElbow(doc, segments[corner - 1], segments[corner], pts[corner], corner));

                // (long) is load-bearing for net48: ElementId.Value is int on the
                // 2024- API, long on 2025+ — without the cast this infers
                // List<int> and SplitMainAndTee's List<long> parameter won't bind.
                var newIds = segments.Select(s => (long)s.Id.Value).ToList();
                var warnings = new List<string>();
                var branchConn = NearestConnector(ConnectorsOf(segments[0]), pts[0], double.MaxValue, freeOnly: false)
                    ?? throw new RouteFailure("no connector found at the branch's tap end", 0);

                try
                {
                    var takeoff = doc.Create.NewTakeoffFitting(branchConn, mainCurve);
                    fittingIds.Add(takeoff.Id.Value);
                }
                catch (Exception)
                {
                    try
                    {
                        var tee = SplitMainAndTee(doc, mainCurve, isDuct, curveTypeId, systemTypeId, level.Id,
                            pts[0], branchConn, mainWidthFt, mainHeightFt, mainDiaFt, newIds, warnings);
                        fittingIds.Add(tee.Id.Value);
                    }
                    catch (Exception ex2)
                    {
                        throw new RouteFailure(
                            $"takeoff and fallback tee fitting both failed at main element {mainId}: {ex2.Message}", 0);
                    }
                }

                var elements = segments.Cast<Element>().Concat(fittingIds.Select(id => doc.GetElement(ElemIds.From(id))));
                var totalLenFt = segments.Sum(s => s.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0);
                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["new_ids"] = newIds,
                    ["fitting_ids"] = fittingIds,
                    ["total_length_mm"] = totalLenFt * MmPerFoot,
                    ["open_connectors"] = ComputeOpenConnectors(elements),
                    ["warnings"] = warnings,
                };
                TxGuard.CommitOrThrow(tx);
                return result;
            }
            catch (RouteFailure rf) { SafeRollback(tx); return Failure(rf.Message, rf.CornerIndex); }
            catch (Exception ex) { SafeRollback(tx); return Failure(ex.Message, -1); }
        }

        // ─── route_conduit ──────────────────────────────────────────────
        // Two modes. points_mm: one run from the given points in ONE Transaction, with the same
        // result keys as route_pipe. plan_id: the runs plan_cad_electrical planned from a
        // drawing, in one TransactionGroup with one Transaction per run: a run that fails rolls
        // back alone and is listed, and the whole call is one undo step. Types and sizes are
        // checked before any transaction.

        private const double DeviceSearchMm = 150.0;
        private const double DeviceConnectorReachMm = 300.0;
        private const double ConnectorVerticalDeg = 30.0;

        private sealed class RunCurveSpec
        {
            /// <summary>"conduit" | "cable_tray"</summary>
            public string Kind = "conduit";
            public string Name = "";
            public ElementId TypeId = ElementId.InvalidElementId;
            public double? DiameterFt, WidthFt, HeightFt;
        }

        public static Dictionary<string, object?> RouteConduit(Document doc, JsonElement args)
        {
            bool hasPoints = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("points_mm", out var pv) &&
                             pv.ValueKind != JsonValueKind.Null;
            var planId = ArgsHelp.GetString(args, "plan_id");
            if (hasPoints && planId != null)
                return Failure("pass either points_mm (one run) or plan_id (a drawing plan's runs), not both", -1);
            if (!hasPoints && planId == null)
                return Failure("pass points_mm and level for one run, or plan_id from plan_cad_electrical", -1);
            return planId != null
                ? RoutePlanRuns(doc, planId, ArgsHelp.GetLongList(args, "run_indices"))
                : RouteConduitPoints(doc, args);
        }

        private static Dictionary<string, object?> RouteConduitPoints(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var pts = ArgsHelp.GetPointListMm(args, "points_mm");
                if (pts.Count < 2)
                    throw new RouteFailure("points_mm needs at least 2 points", -1);
                var level = ResolveLevel(doc, args);
                var conduitTypeName = ArgsHelp.GetString(args, "conduit_type_name");
                var trayTypeName = ArgsHelp.GetString(args, "tray_type_name");
                if (conduitTypeName != null && trayTypeName != null)
                    throw new RouteFailure("pass conduit_type_name (conduit) or tray_type_name (cable tray), not both", -1);
                var spec = trayTypeName != null
                    ? ResolveTraySpec(doc, trayTypeName, ArgsHelp.GetDouble(args, "width_mm"),
                                      ArgsHelp.GetDouble(args, "height_mm"), needsBends: pts.Count > 2)
                    : ResolveConduitSpec(doc, conduitTypeName, ArgsHelp.GetDouble(args, "diameter_mm"), needsBends: pts.Count > 2);
                var connectStartTo = ArgsHelp.GetLong(args, "connect_start_to");
                var connectEndTo = ArgsHelp.GetLong(args, "connect_end_to");

                tx = new Transaction(doc, "BinaVibe: route conduit");
                TxGuard.StartSwallowing(tx);

                var snapStart = ResolveSnap(doc, connectStartTo, pts[0]);
                if (snapStart != null) pts[0] = snapStart.Origin;
                var snapEnd = ResolveSnap(doc, connectEndTo, pts[pts.Count - 1]);
                if (snapEnd != null) pts[pts.Count - 1] = snapEnd.Origin;

                var (segments, fittingIds) = BuildRunLegs(doc, spec, level, pts);

                var warnings = new List<string>();
                SnapEndpoint(segments[0], pts[0], snapStart, "start", warnings);
                SnapEndpoint(segments[segments.Count - 1], pts[pts.Count - 1], snapEnd, "end", warnings);

                var result = BuildResult(segments, fittingIds, warnings);
                TxGuard.CommitOrThrow(tx);
                return result;
            }
            catch (RouteFailure rf) { SafeRollback(tx); return Failure(rf.Message, rf.CornerIndex); }
            catch (Exception ex) { SafeRollback(tx); return Failure(ex.Message, -1); }
        }

        /// <summary>route_conduit's plan_id mode, shared with place_cad_electrical. Its own
        /// TransactionGroup is assimilated when a run was built; started inside an outer group
        /// (place_cad_electrical), it folds into that group's single undo step.</summary>
        internal static Dictionary<string, object?> RoutePlanRuns(Document doc, string planId, List<long> wanted)
        {
            var plan = SocketPlanCache.Get(planId, SocketCandidates.DocKey(doc));
            var selected = SocketPlanSelect.SelectRuns(plan, wanted);
            if (selected.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["plan_id"] = planId,
                    ["error"] = plan.Runs.Count == 0
                        ? "this plan has no conduit runs; run plan_cad_electrical with run_height_mm"
                        : $"no runs selected from plan {planId} (plan holds {plan.Runs.Count} runs, " +
                          $"{plan.Runs.Count(r => r.Excluded)} of them excluded; run_indices are 0-based)",
                };

            var created = new List<object?>();
            var failed = new List<object?>();
            var skipped = new List<object?>();
            var allNew = new List<long>();
            var allFittings = new List<long>();
            var warnings = new List<string>();
            double totalFt = 0;

            // Each distinct type resolved once, before the group: a type error fails its runs.
            var specs = new Dictionary<string, RunCurveSpec>(StringComparer.Ordinal);
            var specErrors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var run in selected)
            {
                string key = SpecKey(run);
                if (specs.ContainsKey(key) || specErrors.ContainsKey(key)) continue;
                try
                {
                    bool bends = selected.Any(r => SpecKey(r) == key && r.PointsMm.Count > 2);
                    specs[key] = ResolveRunSpec(doc, run, bends);
                }
                catch (Exception ex) { specErrors[key] = ex.Message; }
            }

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var present = Electrical.CadElectricalResolver.ExistingDevices(doc);
            var todo = new List<PlannedRun>();
            foreach (var run in selected)
            {
                if (run.CreatedIds.Count > 0 && run.CreatedIds.All(id => doc.GetElement(ElemIds.From(id)) != null))
                {
                    skipped.Add(new Dictionary<string, object?> { ["index"] = run.Index, ["reason"] = "already_routed" });
                    continue;
                }
                if (specErrors.TryGetValue(SpecKey(run), out var typeError))
                {
                    failed.Add(FailedRun(run.Index, typeError, -1));
                    continue;
                }
                if (run.PointsMm.Count < 2)
                {
                    failed.Add(FailedRun(run.Index, "the run has fewer than 2 points", -1));
                    continue;
                }
                todo.Add(run);
            }

            using (var group = new TransactionGroup(doc, "BinaVibe: route_conduit"))
            {
                group.Start();
                try
                {
                    foreach (var run in todo)
                    {
                        var level = levels.FirstOrDefault(l => string.Equals(l.Name, run.LevelName, StringComparison.Ordinal))
                                    ?? levels.FirstOrDefault(l => string.Equals(l.Name, run.LevelName, StringComparison.OrdinalIgnoreCase));
                        if (level == null)
                        {
                            failed.Add(FailedRun(run.Index, $"level '{run.LevelName}' not found", -1));
                            continue;
                        }

                        var pts = run.PointsMm.Select(p => new XYZ(p[0] / MmPerFoot, p[1] / MmPerFoot, p[2] / MmPerFoot)).ToList();
                        var runWarnings = new List<string>();
                        var tx = new Transaction(doc, "BinaVibe: route conduit run");
                        try
                        {
                            TxGuard.StartSwallowing(tx);
                            var spec = specs[SpecKey(run)];
                            Connector? startConn = null, endConn = null;
                            if (spec.Kind == "conduit")
                            {
                                startConn = DeviceConnector(doc, plan, run.Start, pts, atStart: true, present, runWarnings);
                                endConn = DeviceConnector(doc, plan, run.End, pts, atStart: false, present, runWarnings);
                            }

                            var (segments, fittingIds) = BuildRunLegs(doc, spec, level, pts);
                            bool startConnected = Connect(segments[0], pts[0], startConn, "start", runWarnings);
                            bool endConnected = Connect(segments[segments.Count - 1], pts[pts.Count - 1], endConn, "end", runWarnings);

                            var newIds = segments.Select(s => (long)s.Id.Value).ToList();
                            double lengthFt = segments.Sum(s => s.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0);
                            TxGuard.CommitOrThrow(tx);

                            run.CreatedIds = newIds.Concat(fittingIds).ToList();
                            allNew.AddRange(newIds);
                            allFittings.AddRange(fittingIds);
                            totalFt += lengthFt;
                            warnings.AddRange(runWarnings.Select(w => $"run {run.Index}: {w}"));
                            created.Add(new Dictionary<string, object?>
                            {
                                ["index"] = run.Index,
                                ["new_ids"] = newIds,
                                ["fitting_ids"] = fittingIds,
                                ["length_mm"] = Math.Round(lengthFt * MmPerFoot, 1),
                                ["start"] = EndRow(run.Start, startConnected),
                                ["end"] = EndRow(run.End, endConnected),
                            });
                        }
                        catch (RouteFailure rf)
                        {
                            SafeRollback(tx);
                            failed.Add(FailedRun(run.Index, rf.Message, rf.CornerIndex));
                        }
                        catch (Exception ex)
                        {
                            SafeRollback(tx);
                            failed.Add(FailedRun(run.Index, ex.Message, -1));
                        }
                        finally
                        {
                            tx.Dispose();
                        }
                    }

                    if (created.Count > 0) group.Assimilate();
                    else group.RollBack();
                }
                catch
                {
                    if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                    throw;
                }
            }

            var elements = allNew.Concat(allFittings).Select(id => doc.GetElement(ElemIds.From(id))).Where(e => e != null);
            return new Dictionary<string, object?>
            {
                ["ok"] = created.Count > 0 || failed.Count == 0,
                ["plan_id"] = planId,
                ["runs_created"] = created.Count,
                ["runs_failed"] = failed.Count,
                ["created"] = created,
                ["failed"] = failed,
                ["skipped"] = skipped,
                ["new_ids"] = allNew,
                ["fitting_ids"] = allFittings,
                ["total_length_mm"] = Math.Round(totalFt * MmPerFoot, 1),
                ["open_connectors"] = ComputeOpenConnectors(elements!),
                ["warnings"] = warnings,
            };
        }

        private static string SpecKey(PlannedRun run) =>
            run.Kind + "|" + (run.TypeName ?? "") + "|" + (run.DiameterMm?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "") +
            "|" + (run.WidthMm?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "") +
            "|" + (run.HeightMm?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "");

        private static RunCurveSpec ResolveRunSpec(Document doc, PlannedRun run, bool needsBends)
        {
            if (run.Kind == "conduit") return ResolveConduitSpec(doc, run.TypeName, run.DiameterMm, needsBends);
            if (run.Kind == "cable_tray")
                return ResolveTraySpec(doc, run.TypeName ?? throw new RouteFailure("the trunking run names no cable tray type", -1),
                                       run.WidthMm, run.HeightMm, needsBends);
            throw new RouteFailure($"run {run.Index} is a {run.Kind} run, which route_conduit cannot build", -1);
        }

        /// <summary>
        /// A cable tray type (trunking) by "Family : Type", else by a unique bare type name. When
        /// the run bends, the type must be a "with fittings" type. Width and height must be
        /// positive. Throws RouteFailure with the fix.
        /// </summary>
        private static RunCurveSpec ResolveTraySpec(Document doc, string typeName, double? widthMm, double? heightMm, bool needsBends)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<CableTrayType>().ToList();
            var full = types.Where(t => string.Equals(t.FamilyName + " : " + t.Name, typeName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = full.Count > 0 ? full
                : types.Where(t => string.Equals(t.Name, typeName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                throw new RouteFailure($"cable tray type '{typeName}' not found (use list_family_types(\"OST_CableTray\"))", -1);
            if (matches.Count > 1)
                throw new RouteFailure($"cable tray type '{typeName}' matches " +
                    string.Join(", ", matches.Select(t => "'" + t.FamilyName + " : " + t.Name + "'")) +
                    ": name it as 'Family : Type'", -1);
            var type = matches[0];
            string name = type.FamilyName + " : " + type.Name;

            if (needsBends)
            {
                bool withFitting;
                try { withFitting = type.IsWithFitting; } catch { withFitting = false; }
                if (!withFitting)
                    throw new RouteFailure($"cable tray type '{name}' is a type without fittings, so bends cannot be built: " +
                                           "choose a Cable Tray with Fittings type", -1);
            }
            if (widthMm.HasValue && widthMm.Value <= 0) throw new RouteFailure("width_mm must be above 0", -1);
            if (heightMm.HasValue && heightMm.Value <= 0) throw new RouteFailure("height_mm must be above 0", -1);

            return new RunCurveSpec
            {
                Kind = "cable_tray", Name = name, TypeId = type.Id,
                WidthFt = widthMm / MmPerFoot, HeightFt = heightMm / MmPerFoot,
            };
        }

        private static Dictionary<string, object?> FailedRun(int index, string reason, int corner) => new()
        {
            ["index"] = index, ["reason"] = reason, ["failed_corner_index"] = corner,
        };

        private static Dictionary<string, object?> EndRow(RunEnd end, bool connected) => new()
        {
            ["kind"] = end.Kind, ["point_index"] = end.PointIndex, ["element_id"] = end.ElementId, ["connected"] = connected,
        };

        /// <summary>
        /// A conduit type by "Family : Type", else by a unique bare type name; with no name, the
        /// first type with fittings. When the run bends, the type must be a "with fittings" type
        /// with a Bend assigned. The diameter must be one of the type's trade sizes, where they
        /// can be read. Throws RouteFailure with the fix.
        /// </summary>
        private static RunCurveSpec ResolveConduitSpec(Document doc, string? typeName, double? diameterMm, bool needsBends)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<ConduitType>().ToList();
            ConduitType type;
            if (typeName == null)
            {
                type = types.FirstOrDefault(IsWithFitting) ?? types.FirstOrDefault()
                    ?? throw new RouteFailure("no conduit types in project", -1);
            }
            else
            {
                var full = types.Where(t => string.Equals(t.FamilyName + " : " + t.Name, typeName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                var matches = full.Count > 0 ? full
                    : types.Where(t => string.Equals(t.Name, typeName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0)
                    throw new RouteFailure($"conduit type '{typeName}' not found (use list_family_types(\"OST_Conduit\"))", -1);
                if (matches.Count > 1)
                    throw new RouteFailure($"conduit type '{typeName}' matches " +
                        string.Join(", ", matches.Select(t => "'" + t.FamilyName + " : " + t.Name + "'")) +
                        ": name it as 'Family : Type'", -1);
                type = matches[0];
            }
            string name = type.FamilyName + " : " + type.Name;

            if (needsBends)
            {
                if (!IsWithFitting(type))
                    throw new RouteFailure($"conduit type '{name}' is a type without fittings, so bends cannot be built: " +
                                           "choose a Conduit with Fittings type", -1);
                RequireBend(type, "conduit type", name);
            }

            double? diameterFt = null;
            if (diameterMm.HasValue)
            {
                double d = diameterMm.Value;
                if (d <= 0) throw new RouteFailure("diameter_mm must be above 0", -1);
                var sizes = Electrical.CadElectricalResolver.ConduitSizesMm(type, Electrical.CadElectricalResolver.ConduitSizeTable(doc));
                if (sizes.Count > 0 && !sizes.Any(s => Math.Abs(s - d) <= Electrical.CadElectricalMapping.SizeToleranceMm))
                    throw new RouteFailure($"{d} mm is not a size of conduit type '{name}'; its sizes are " +
                                           string.Join(", ", sizes.Select(s => Math.Round(s, 1))) + " mm", -1);
                diameterFt = d / MmPerFoot;
            }

            return new RunCurveSpec { Kind = "conduit", Name = name, TypeId = type.Id, DiameterFt = diameterFt };
        }

        private static bool IsWithFitting(ConduitType t)
        {
            try { return t.IsWithFitting; } catch { return false; }
        }

        /// <summary>A type whose Bend fitting is unset cannot build a corner: say so before any
        /// transaction. A type with no such parameter is left to the elbow call.</summary>
        private static void RequireBend(ElementType type, string what, string name)
        {
            Parameter? bend = null;
            try { bend = type.get_Parameter(BuiltInParameter.RBS_CURVETYPE_DEFAULT_BEND_PARAM); } catch { bend = null; }
            if (bend != null && bend.StorageType == StorageType.ElementId && bend.AsElementId() == ElementId.InvalidElementId)
                throw new RouteFailure($"{what} '{name}' has no Bend fitting assigned, so bends cannot be built: " +
                                       "set its Bend in the type properties (Routing Preferences) or choose another type", -1);
        }

        /// <summary>One segment per leg, sized, and an elbow at every corner. Throws
        /// RouteFailure with the leg or corner index.</summary>
        private static (List<MEPCurve> Segments, List<long> FittingIds) BuildRunLegs(Document doc, RunCurveSpec spec,
                                                                                     Level level, List<XYZ> pts)
        {
            var segments = new List<MEPCurve>();
            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (pts[i].DistanceTo(pts[i + 1]) < 1.0 / MmPerFoot)
                    throw new RouteFailure($"leg {i} has no length", i);
                MEPCurve seg;
                try { seg = CreateLeg(doc, spec, level, pts[i], pts[i + 1]); }
                catch (Exception ex) { throw new RouteFailure($"leg {i} creation failed: {ex.Message}", i); }
                segments.Add(seg);
            }

            var fittingIds = new List<long>();
            for (int corner = 1; corner < pts.Count - 1; corner++)
                fittingIds.Add(CreateElbow(doc, segments[corner - 1], segments[corner], pts[corner], corner));
            return (segments, fittingIds);
        }

        private static MEPCurve CreateLeg(Document doc, RunCurveSpec spec, Level level, XYZ a, XYZ b)
        {
            if (spec.Kind == "cable_tray")
            {
                var tray = CableTray.Create(doc, spec.TypeId, a, b, level.Id);
                if (spec.WidthFt.HasValue) tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM)?.Set(spec.WidthFt.Value);
                if (spec.HeightFt.HasValue) tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM)?.Set(spec.HeightFt.Value);
                return tray;
            }
            var conduit = Conduit.Create(doc, spec.TypeId, a, b, level.Id);
            if (spec.DiameterFt.HasValue) conduit.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM)?.Set(spec.DiameterFt.Value);
            return conduit;
        }

        /// <summary>
        /// The free conduit connector of a run end's device, with the run's last two points moved
        /// onto it, or null with a warning: device_not_placed (no device found), no_drop (the
        /// end has no vertical leg), no_conduit_connector, connector_not_vertical.
        /// </summary>
        private static Connector? DeviceConnector(Document doc, SocketPlan plan, RunEnd end, List<XYZ> pts, bool atStart,
                                                  List<Electrical.PresentDevice> present, List<string> warnings)
        {
            if (end.Kind != "device") return null;
            string label = atStart ? "start" : "end";

            Element? device = null;
            if (end.ElementId.HasValue) device = doc.GetElement(ElemIds.From(end.ElementId.Value));
            PlannedPoint? point = end.PointIndex.HasValue ? plan.Points.FirstOrDefault(p => p.Index == end.PointIndex.Value) : null;
            if (device == null && point?.PlacedId != null) device = doc.GetElement(ElemIds.From(point.PlacedId.Value));
            if (device == null && point != null)
            {
                Electrical.PresentDevice? near = present
                    .Where(d => Math.Sqrt(Sq(d.X - point.XMm) + Sq(d.Y - point.YMm) + Sq(d.Z - point.ZMm)) <= DeviceSearchMm)
                    .OrderBy(d => Sq(d.X - point.XMm) + Sq(d.Y - point.YMm) + Sq(d.Z - point.ZMm))
                    .FirstOrDefault();
                if (near != null && near.Id != 0) device = doc.GetElement(ElemIds.From(near.Id));
            }
            if (device == null)
            {
                warnings.Add($"{label}: device_not_placed (place the devices with place_socket_points first)");
                return null;
            }

            int last = atStart ? 0 : pts.Count - 1;
            int next = atStart ? 1 : pts.Count - 2;
            if (pts.Count < 2 || Math.Sqrt(Sq(pts[last].X - pts[next].X) + Sq(pts[last].Y - pts[next].Y)) * MmPerFoot > 1)
            {
                warnings.Add($"{label}: no_drop (device {device.Id.Value} is not reached by a vertical leg)");
                return null;
            }

            var found = FreeConduitConnector(device, pts[last], DeviceConnectorReachMm / MmPerFoot, out bool anyFree);
            if (found == null)
            {
                warnings.Add($"{label}: no_conduit_connector (device {device.Id.Value} has " +
                             (anyFree ? "no free conduit connector within 300 mm" : "no free conduit connector") + ")");
                return null;
            }
            if (Math.Abs(found.CoordinateSystem.BasisZ.Z) < Math.Cos(ConnectorVerticalDeg * Math.PI / 180))
            {
                warnings.Add($"{label}: connector_not_vertical (device {device.Id.Value})");
                return null;
            }

            var o = found.Origin;
            pts[last] = o;
            pts[next] = new XYZ(o.X, o.Y, pts[next].Z);
            return found;
        }

        /// <summary>The nearest free conduit-domain connector of a device within reach of the aimed
        /// point; <paramref name="anyFree"/> says whether it has one at all.</summary>
        private static Connector? FreeConduitConnector(Element device, XYZ aim, double toleranceFt, out bool anyFree)
        {
            anyFree = false;
            var cm = GetConnectorManager(device);
            if (cm == null) return null;
            Connector? best = null;
            double bestDist = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                if (c.Domain != Domain.DomainCableTrayConduit || c.IsConnected) continue;
                anyFree = true;
                double d = c.Origin.DistanceTo(aim);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best != null && bestDist <= toleranceFt ? best : null;
        }

        /// <summary>Connects the run's end to the device's connector; a refusal is a warning.</summary>
        private static bool Connect(MEPCurve segment, XYZ point, Connector? target, string label, List<string> warnings)
        {
            if (target == null) return false;
            var own = NearestConnector(ConnectorsOf(segment), point, double.MaxValue, freeOnly: true);
            if (own == null)
            {
                warnings.Add($"{label}: no free connector on the run's end to connect");
                return false;
            }
            try
            {
                own.ConnectTo(target);
                return true;
            }
            catch (Exception ex)
            {
                warnings.Add($"{label}: connect refused ({ex.Message})");
                return false;
            }
        }

        private static double Sq(double v) => v * v;

        // ─── shared helpers ─────────────────────────────────────────────

        private static Level ResolveLevel(Document doc, JsonElement args)
        {
            var levelName = ArgsHelp.GetString(args, "level")
                ?? throw new RouteFailure("level required", -1);
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new RouteFailure($"level '{levelName}' not found (use list_levels)", -1);
        }

        private static long CreateElbow(Document doc, MEPCurve prev, MEPCurve next, XYZ corner, int cornerIndex)
        {
            var connA = NearestConnector(ConnectorsOf(prev), corner, double.MaxValue, freeOnly: false)
                ?? throw new RouteFailure($"no connector found near corner {cornerIndex} on the preceding leg", cornerIndex);
            var connB = NearestConnector(ConnectorsOf(next), corner, double.MaxValue, freeOnly: false)
                ?? throw new RouteFailure($"no connector found near corner {cornerIndex} on the following leg", cornerIndex);
            try { return doc.Create.NewElbowFitting(connA, connB).Id.Value; }
            catch (Exception ex) { throw new RouteFailure($"elbow at corner {cornerIndex} failed: {ex.Message}", cornerIndex); }
        }

        private static Connector? ResolveSnap(Document doc, long? targetId, XYZ point)
        {
            if (targetId == null) return null;
            var el = doc.GetElement(ElemIds.From(targetId.Value))
                ?? throw new RouteFailure($"connect target element {targetId} not found", -1);
            var toleranceFt = SnapToleranceMm / MmPerFoot;
            return NearestConnector(ConnectorsOf(el), point, toleranceFt, freeOnly: true)
                ?? throw new RouteFailure(
                    $"no free connector on element {targetId} within {SnapToleranceMm:F0}mm of the aimed endpoint", -1);
        }

        private static void SnapEndpoint(MEPCurve segment, XYZ point, Connector? target, string label, List<string> warnings)
        {
            if (target == null) return;
            var own = NearestConnector(ConnectorsOf(segment), point, double.MaxValue, freeOnly: false)
                ?? throw new RouteFailure($"could not locate the {label} connector to snap", -1);
            try { own.ConnectTo(target); }
            catch (Exception ex) { throw new RouteFailure($"snap at {label} failed: {ex.Message}", -1); }
        }

        // A physical connection main had before the split (its far side, and
        // the partner connector on whatever was attached there — an elbow,
        // equipment, or an upstream/downstream segment). Captured before
        // doc.Delete(main) so the split can restore it on segA/segB.
        private readonly struct PendingLink
        {
            public readonly XYZ Origin;
            public readonly Connector Partner;
            public PendingLink(XYZ origin, Connector partner) { Origin = origin; Partner = partner; }
        }

        private static List<PendingLink> CapturePendingLinks(MEPCurve main)
        {
            var links = new List<PendingLink>();
            foreach (var c in ConnectorsOf(main))
            {
                if (!c.IsConnected) continue;
                foreach (Connector r in c.AllRefs)
                {
                    if (r.Owner != null && r.Owner.Id.Value == main.Id.Value) continue;
                    if ((r.ConnectorType & ConnectorType.Physical) == 0) continue;
                    links.Add(new PendingLink(c.Origin, r));
                }
            }
            return links;
        }

        private static FamilyInstance SplitMainAndTee(Document doc, MEPCurve main, bool isDuct, ElementId curveTypeId,
            ElementId systemTypeId, ElementId levelId, XYZ tapPoint, Connector branchConn,
            double? widthFt, double? heightFt, double? diaFt, List<long> newIds, List<string> warnings)
        {
            var line = ((LocationCurve)main.Location).Curve as Line
                ?? throw new RouteFailure("main element geometry is not a straight line", 0);
            var p0 = line.GetEndPoint(0);
            var p1 = line.GetEndPoint(1);
            var mainId = main.Id.Value;
            var pendingLinks = CapturePendingLinks(main);
            doc.Delete(main.Id);

            MEPCurve segA = isDuct
                ? Duct.Create(doc, systemTypeId, curveTypeId, levelId, p0, tapPoint)
                : Pipe.Create(doc, systemTypeId, curveTypeId, levelId, p0, tapPoint);
            MEPCurve segB = isDuct
                ? Duct.Create(doc, systemTypeId, curveTypeId, levelId, tapPoint, p1)
                : Pipe.Create(doc, systemTypeId, curveTypeId, levelId, tapPoint, p1);
            foreach (var seg in new[] { segA, segB })
            {
                if (widthFt.HasValue) seg.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.Set(widthFt.Value);
                if (heightFt.HasValue) seg.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.Set(heightFt.Value);
                if (diaFt.HasValue) seg.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diaFt.Value);
            }
            newIds.Add(segA.Id.Value);
            newIds.Add(segB.Id.Value);
            warnings.Add($"main element {mainId} was split into {segA.Id.Value}/{segB.Id.Value} to insert a tee fitting");

            var connA = NearestConnector(ConnectorsOf(segA), tapPoint, double.MaxValue, freeOnly: false)
                ?? throw new RouteFailure("no connector found on split main segment A", 0);
            var connB = NearestConnector(ConnectorsOf(segB), tapPoint, double.MaxValue, freeOnly: false)
                ?? throw new RouteFailure("no connector found on split main segment B", 0);
            var tee = doc.Create.NewTeeFitting(connA, connB, branchConn);

            foreach (var link in pendingLinks)
            {
                var partnerId = link.Partner.Owner?.Id.Value;
                var target = NearestConnector(ConnectorsOf(segA).Concat(ConnectorsOf(segB)), link.Origin,
                    double.MaxValue, freeOnly: true);
                if (target == null)
                {
                    warnings.Add($"could not find a free connector to restore main element {mainId}'s prior link to element {partnerId}");
                    continue;
                }
                try { target.ConnectTo(link.Partner); }
                catch (Exception) { warnings.Add($"failed to reconnect main element {mainId}'s prior link to element {partnerId}"); }
            }

            return tee;
        }

        // internal, not private: Inspectors.CountBy's "connectivity" group_by
        // (2026-08-02) reuses this same connector-manager resolver instead of
        // duplicating the element-shape switch.
        internal static ConnectorManager? GetConnectorManager(Element el) => el switch
        {
            MEPCurve mc => mc.ConnectorManager,
            FamilyInstance fi => fi.MEPModel?.ConnectorManager,
            _ => null,
        };

        private static List<Connector> ConnectorsOf(Element el)
        {
            var cm = GetConnectorManager(el)
                ?? throw new RouteFailure($"element {el.Id.Value} has no MEP connectors (not a duct/pipe/tray or MEP family instance)", -1);
            var list = new List<Connector>();
            foreach (Connector c in cm.Connectors) list.Add(c);
            return list;
        }

        private static Connector? NearestConnector(IEnumerable<Connector> conns, XYZ point, double toleranceFt, bool freeOnly)
        {
            Connector? best = null;
            var bestDist = double.MaxValue;
            foreach (var c in conns)
            {
                if (freeOnly && c.IsConnected) continue;
                var d = c.Origin.DistanceTo(point);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best != null && bestDist <= toleranceFt ? best : null;
        }

        private static void SafeRollback(Transaction? tx)
        {
            if (tx != null && tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
        }

        private static Dictionary<string, object?> Failure(string error, int cornerIndex) => new()
        {
            ["ok"] = false, ["error"] = error, ["failed_corner_index"] = cornerIndex,
        };

        private static Dictionary<string, object?> BuildResult(List<MEPCurve> segments, List<long> fittingIds, List<string> warnings)
        {
            var doc = segments[0].Document;
            var newIds = segments.Select(s => s.Id.Value).ToList();
            var totalLenFt = segments.Sum(s => s.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0);
            var elements = segments.Cast<Element>().Concat(fittingIds.Select(id => doc.GetElement(ElemIds.From(id))));
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["new_ids"] = newIds,
                ["fitting_ids"] = fittingIds,
                ["total_length_mm"] = totalLenFt * MmPerFoot,
                ["open_connectors"] = ComputeOpenConnectors(elements),
                ["warnings"] = warnings,
            };
        }

        private static Dictionary<string, object?> ComputeOpenConnectors(IEnumerable<Element> elements)
        {
            var origins = new List<List<double>>();
            foreach (var el in elements)
            {
                if (el == null) continue;
                var cm = GetConnectorManager(el);
                if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                    if (!c.IsConnected)
                        origins.Add(new List<double> { c.Origin.X * MmPerFoot, c.Origin.Y * MmPerFoot, c.Origin.Z * MmPerFoot });
            }
            return new Dictionary<string, object?> { ["count"] = origins.Count, ["origins_mm"] = origins };
        }

        private static Dictionary<string, object?> ConnectorSummary(Connector c, int index)
        {
            var size = new Dictionary<string, object?>();
            switch (c.Shape)
            {
                case ConnectorProfileType.Round:
                    size["diameter_mm"] = c.Radius * 2 * MmPerFoot;
                    break;
                case ConnectorProfileType.Rectangular:
                case ConnectorProfileType.Oval:
                    size["width_mm"] = c.Width * MmPerFoot;
                    size["height_mm"] = c.Height * MmPerFoot;
                    break;
            }
            string? systemType = null;
            try { systemType = c.MEPSystem?.Name; } catch { }
            if (systemType == null)
            {
                try
                {
                    systemType = c.Domain switch
                    {
                        Domain.DomainHvac => c.DuctSystemType.ToString(),
                        Domain.DomainPiping => c.PipeSystemType.ToString(),
                        Domain.DomainElectrical => c.ElectricalSystemType.ToString(),
                        _ => null,
                    };
                }
                catch { systemType = null; }
            }
            return new Dictionary<string, object?>
            {
                ["index"] = index,
                ["origin_mm"] = new List<double> { c.Origin.X * MmPerFoot, c.Origin.Y * MmPerFoot, c.Origin.Z * MmPerFoot },
                ["direction"] = new List<double> { c.CoordinateSystem.BasisZ.X, c.CoordinateSystem.BasisZ.Y, c.CoordinateSystem.BasisZ.Z },
                ["domain"] = DomainToString(c.Domain),
                ["shape"] = c.Shape.ToString().ToLowerInvariant(),
                ["size_mm"] = size,
                ["system_type"] = systemType,
                ["is_connected"] = c.IsConnected,
            };
        }

        private static string DomainToString(Domain d) => d switch
        {
            Domain.DomainHvac => "duct",
            Domain.DomainPiping => "pipe",
            Domain.DomainElectrical => "electrical",
            Domain.DomainCableTrayConduit => "cable_tray_conduit",
            _ => "undefined",
        };

        private static Dictionary<string, object?> DescribeNode(Element el)
        {
            var typeName = el.Document.GetElement(el.GetTypeId()) is ElementType et ? et.Name : null;
            return new Dictionary<string, object?>
            {
                ["id"] = el.Id.Value,
                ["kind"] = ClassifyNode(el),
                ["category"] = el.Category?.Name,
                ["type_name"] = typeName,
            };
        }

        private static string ClassifyNode(Element el)
        {
            if (el is MEPCurve) return "curve";
            if (el is FamilyInstance fi)
            {
                var catId = fi.Category?.Id.Value;
                if (catId == (long)BuiltInCategory.OST_DuctFitting || catId == (long)BuiltInCategory.OST_PipeFitting)
                    return "fitting";
                if (catId == (long)BuiltInCategory.OST_DuctTerminal)
                    return "terminal";
                if (fi.MEPModel != null)
                    return "equipment";
            }
            return "unknown";
        }
    }
}
