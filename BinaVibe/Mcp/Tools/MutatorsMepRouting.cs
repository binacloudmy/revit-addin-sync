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
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

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

                tx = new Transaction(doc, "BINA: route duct");
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

                tx = new Transaction(doc, "BINA: route pipe");
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

                tx = new Transaction(doc, "BINA: tap branch");
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

                var newIds = segments.Select(s => s.Id.Value).ToList();
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

        private static ConnectorManager? GetConnectorManager(Element el) => el switch
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
    }
}
