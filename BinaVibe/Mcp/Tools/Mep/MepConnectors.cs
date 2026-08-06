// Connector primitives shared by every MEP tool — discipline-agnostic.
//
// Promoted out of MutatorsMepRouting.cs so the system/circuit tools do not
// re-derive the same connector-manager switch, nearest-connector search and
// physical-ref BFS that routing already got right. MutatorsMepRouting keeps
// thin forwarders that re-wrap failures into its own RouteFailure, so its
// `catch (RouteFailure rf)` arms and `failed_corner_index` reporting keep
// working unchanged.
//
// BEHAVIOUR IS FROZEN. This file is a move, not a rewrite: ConnectorSummary's
// keys, the "duct"/"pipe" domain labels and the traversal ORDER are contract
// for list_connectors / trace_connections / route_* and must not drift here.
// Generalisations (electrical node kinds, richer summaries) belong in the
// Layer 0 tool files that consume this, not in the primitives.
//
// UNITS: the Revit API side is FEET. Every value that leaves this file named
// *_mm has been multiplied by MmPerFoot; tolerances taken as arguments are in
// FEET (callers convert), matching ArgsHelp.GetLengthMm's return convention.
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal static class MepConnectors
    {
        public const double MmPerFoot = 304.8;

        // ─── discovery ──────────────────────────────────────────────────

        /// <summary>The only supported element shapes that carry connectors:
        /// an MEP curve (duct/pipe/conduit/tray) or a family instance with an
        /// MEPModel (equipment, terminal, fixture, fitting).</summary>
        public static ConnectorManager? GetConnectorManager(Element el) => el switch
        {
            MEPCurve mc => mc.ConnectorManager,
            FamilyInstance fi => fi.MEPModel?.ConnectorManager,
            _ => null,
        };

        /// <summary>All connectors on an element. Throws when the element has
        /// no connector manager at all — callers that must tolerate that use
        /// <see cref="TryConnectorsOf"/>.</summary>
        public static List<Connector> ConnectorsOf(Element el)
        {
            var cm = GetConnectorManager(el)
                ?? throw new InvalidOperationException(
                    $"element {el.Id.Value} has no MEP connectors (not a duct/pipe/tray or MEP family instance)");
            var list = new List<Connector>();
            foreach (Connector c in cm.Connectors) list.Add(c);
            return list;
        }

        /// <summary>Connectors, or an empty list for an element that has none.
        /// For graph walks, where a connector-less neighbour is data, not an
        /// error.</summary>
        public static List<Connector> TryConnectorsOf(Element? el)
        {
            var list = new List<Connector>();
            if (el == null) return list;
            var cm = GetConnectorManager(el);
            if (cm == null) return list;
            foreach (Connector c in cm.Connectors) list.Add(c);
            return list;
        }

        /// <summary>Unconnected connectors, optionally narrowed to one domain.
        /// Pass <see cref="Domain.DomainUndefined"/> to accept any.</summary>
        public static List<Connector> FreeConnectors(Element? el, Domain domain = Domain.DomainUndefined)
        {
            var free = new List<Connector>();
            foreach (var c in TryConnectorsOf(el))
            {
                if (c.IsConnected) continue;
                if (domain != Domain.DomainUndefined && c.Domain != domain) continue;
                free.Add(c);
            }
            return free;
        }

        /// <summary>Closest connector to a point, in FEET. Returns null when
        /// the closest one is beyond <paramref name="toleranceFt"/> — pass
        /// double.MaxValue for "nearest, whatever the distance".</summary>
        public static Connector? NearestConnector(IEnumerable<Connector> conns, XYZ point,
            double toleranceFt, bool freeOnly)
        {
            Connector? best = null;
            var bestDist = double.MaxValue;
            foreach (var c in conns)
            {
                if (freeOnly && c.IsConnected) continue;
                double d;
                try { d = c.Origin.DistanceTo(point); }
                catch { continue; }   // a logical connector has no readable Origin
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best != null && bestDist <= toleranceFt ? best : null;
        }

        /// <summary>Connector origin in mm, or null when it cannot be read.
        /// A panel's LOGICAL connector throws on Origin — that is not an
        /// error, it is what distinguishes it from a physical one.</summary>
        public static List<double>? OriginMm(Connector c)
        {
            try
            {
                var o = c.Origin;
                return new List<double> { o.X * MmPerFoot, o.Y * MmPerFoot, o.Z * MmPerFoot };
            }
            catch { return null; }
        }

        // ─── reporting ──────────────────────────────────────────────────

        /// <summary>Open-connector tally over a set of elements — the
        /// self-verify field every routing/connection tool returns.</summary>
        public static Dictionary<string, object?> ComputeOpenConnectors(IEnumerable<Element?> elements)
        {
            var origins = new List<List<double>>();
            var unlocatable = 0;
            foreach (var el in elements)
            {
                if (el == null) continue;
                var cm = GetConnectorManager(el);
                if (cm == null) continue;
                foreach (Connector c in cm.Connectors)
                {
                    if (c.IsConnected) continue;
                    var o = OriginMm(c);
                    if (o == null) { unlocatable++; continue; }
                    origins.Add(o);
                }
            }
            var row = new Dictionary<string, object?>
            {
                ["count"] = origins.Count,
                ["origins_mm"] = origins,
            };
            if (unlocatable > 0) row["without_origin"] = unlocatable;
            return row;
        }

        /// <summary>One connector as a flat dict. KEYS ARE CONTRACT — this is
        /// what list_connectors has returned since 2026-08-01.</summary>
        public static Dictionary<string, object?> ConnectorSummary(Connector c, int index)
        {
            var size = new Dictionary<string, object?>();
            try
            {
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
            }
            catch { /* an electrical connector has no profile — leave size empty */ }

            List<double>? direction = null;
            try
            {
                var z = c.CoordinateSystem.BasisZ;
                direction = new List<double> { z.X, z.Y, z.Z };
            }
            catch { }

            string shape;
            try { shape = c.Shape.ToString().ToLowerInvariant(); }
            catch { shape = "none"; }

            return new Dictionary<string, object?>
            {
                ["index"] = index,
                ["origin_mm"] = OriginMm(c),
                ["direction"] = direction,
                ["domain"] = MepDomains.ConnectorDomainLabel(c.Domain),
                ["shape"] = shape,
                ["size_mm"] = size,
                ["system_type"] = MepDomains.ConnectorSystemTypeName(c),
                ["is_connected"] = c.IsConnected,
            };
        }

        // ─── traversal ──────────────────────────────────────────────────

        /// <summary>Result of a physical-connector walk. Nodes are in BFS
        /// discovery order with the seed first; edges are de-duplicated
        /// (low id, high id) pairs.</summary>
        public sealed class Walk
        {
            public List<Element> Nodes { get; } = new();
            public List<(long A, long B)> Edges { get; } = new();
            public bool Truncated { get; set; }
        }

        /// <summary>BFS out from one element over PHYSICAL connector refs.
        /// Logical refs are skipped: an electrical circuit's logical link
        /// would otherwise make every device on a panel look physically
        /// joined. Capped at <paramref name="maxDepth"/> hops and
        /// <paramref name="maxNodes"/> elements; check
        /// <see cref="Walk.Truncated"/> before claiming completeness.</summary>
        public static Walk Traverse(Element start, int maxDepth, int maxNodes)
            => Traverse(new[] { start }, maxDepth, maxNodes);

        /// <summary>Multi-seed walk — used to test whether a system's members
        /// form one physical network.</summary>
        public static Walk Traverse(IEnumerable<Element> seeds, int maxDepth, int maxNodes)
        {
            var walk = new Walk();
            var visited = new HashSet<long>();
            var edgeKeys = new HashSet<(long, long)>();
            var queue = new Queue<(Element el, int depth)>();

            foreach (var s in seeds)
            {
                if (s == null) continue;
                if (!visited.Add(s.Id.Value)) continue;
                walk.Nodes.Add(s);
                queue.Enqueue((s, 0));
            }

            while (queue.Count > 0)
            {
                var (el, depth) = queue.Dequeue();
                if (depth >= maxDepth) { walk.Truncated = true; continue; }

                foreach (var c in TryConnectorsOf(el))
                {
                    if (!c.IsConnected) continue;
                    ConnectorSet refs;
                    try { refs = c.AllRefs; }
                    catch { continue; }

                    foreach (Connector r in refs)
                    {
                        if (r.Owner == null) continue;
                        if (r.Owner.Id.Value == el.Id.Value) continue;
                        if ((r.ConnectorType & ConnectorType.Physical) == 0) continue;

                        var otherId = r.Owner.Id.Value;
                        var key = el.Id.Value < otherId ? (el.Id.Value, otherId) : (otherId, el.Id.Value);
                        if (edgeKeys.Add(key)) walk.Edges.Add(key);

                        if (visited.Contains(otherId)) continue;
                        if (walk.Nodes.Count >= maxNodes) { walk.Truncated = true; continue; }

                        visited.Add(otherId);
                        walk.Nodes.Add(r.Owner);
                        queue.Enqueue((r.Owner, depth + 1));
                    }
                }
            }

            return walk;
        }
    }
}
