// MEP connector-graph model + walk — pure, Revit-free, ids and strings only.
//
// Split out of TraceMep.cs precisely so the BFS, the caps, the cycle
// handling and the open-end/panel classification are testable in
// Tests/Tests.csproj (explicit <Compile Include>, no globs — anything
// touching Autodesk.Revit.DB is untestable there). Same reason
// SocketLayout.cs was split out of SocketCandidates.cs.
//
// The Revit side (TraceMep.cs) supplies a resolve callback that materializes
// one node per element id on demand — the walk itself never sees a Document.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Mep
{
    /// <summary>One adjacency: the element on the other side of a connector
    /// pair, and the system it belongs to.</summary>
    internal sealed class MepAdj
    {
        public long OtherId;
        /// <summary>Revit Domain name, lowercased by the extractor:
        /// electrical | piping | hvac | cable_tray_conduit | undefined.</summary>
        public string Domain = "undefined";
        public string? SystemName;
        public string? SystemType;
    }

    /// <summary>One element in the graph.</summary>
    internal sealed class MepNode
    {
        public long Id;
        public string Category = "";
        public string Name = "";
        public string? Level;
        public int ConnectedCount;
        public int UnconnectedCount;
        /// <summary>False = the element has NO ConnectorManager / zero
        /// connectors. Distinct from open connectors: this element cannot
        /// participate in a system AT ALL until its family is fixed — and it
        /// is exactly what the get_model_warnings proxy could never see.</summary>
        public bool HasConnectors = true;
        public List<MepAdj> Neighbors = new();
    }

    internal sealed class MepEdge
    {
        public long FromId, ToId;
        public string Domain = "undefined";
        public string? SystemName;
        public string? SystemType;
    }

    internal sealed class MepGraphResult
    {
        public List<MepNode> Nodes = new();
        public List<MepEdge> Edges = new();
        public List<long> OpenEnds = new();
        public List<long> Panels = new();
        public List<long> NoConnectorIds = new();
        public bool Truncated;
        public string? TruncationReason;
    }

    internal static class MepGraph
    {
        public const int NodeCap = 500;
        public const int EdgeCap = 1000;
        public const int MaxDepthCeiling = 25;

        /// <summary>The category string the panel classification keys on —
        /// pinned here (and by tests) rather than scattered.</summary>
        public const string PanelCategory = "Electrical Equipment";

        /// <summary>BFS over the connector graph from the root ids.
        ///
        /// resolve(id) returns the node with its neighbors, or null for an id
        /// that does not exist (the walk continues from the other roots).
        /// domainFilter, when set, drops edges of other domains — but a node
        /// reached in-domain keeps its full connector counts.
        /// Cycles terminate on the visited set; edges are deduped on
        /// (min, max, domain).</summary>
        public static MepGraphResult Walk(
            IReadOnlyList<long> roots,
            Func<long, MepNode?> resolve,
            int maxDepth,
            string? domainFilter,
            int nodeCap = NodeCap,
            int edgeCap = EdgeCap)
        {
            maxDepth = Math.Min(Math.Max(1, maxDepth), MaxDepthCeiling);
            var result = new MepGraphResult();
            var visited = new Dictionary<long, MepNode>();
            var edgeKeys = new HashSet<(long, long, string)>();
            var queue = new Queue<(long id, int depth)>();

            foreach (var r in roots.Distinct())
                queue.Enqueue((r, 0));

            while (queue.Count > 0)
            {
                var (id, depth) = queue.Dequeue();
                if (visited.ContainsKey(id)) continue;
                if (visited.Count >= nodeCap)
                {
                    result.Truncated = true;
                    result.TruncationReason ??= $"node cap ({nodeCap})";
                    break;
                }

                var node = resolve(id);
                if (node == null) continue;   // unresolvable id — walk on
                visited[id] = node;

                foreach (var adj in node.Neighbors)
                {
                    if (domainFilter != null &&
                        !string.Equals(adj.Domain, domainFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var key = (Math.Min(id, adj.OtherId), Math.Max(id, adj.OtherId), adj.Domain);
                    if (edgeKeys.Add(key))
                    {
                        if (result.Edges.Count >= edgeCap)
                        {
                            result.Truncated = true;
                            result.TruncationReason ??= $"edge cap ({edgeCap})";
                        }
                        else
                        {
                            result.Edges.Add(new MepEdge
                            {
                                FromId = id,
                                ToId = adj.OtherId,
                                Domain = adj.Domain,
                                SystemName = adj.SystemName,
                                SystemType = adj.SystemType,
                            });
                        }
                    }

                    if (visited.ContainsKey(adj.OtherId)) continue;
                    if (depth + 1 > maxDepth)
                    {
                        result.Truncated = true;
                        result.TruncationReason ??= $"max_depth ({maxDepth})";
                        continue;
                    }
                    queue.Enqueue((adj.OtherId, depth + 1));
                }
            }

            foreach (var node in visited.Values)
            {
                result.Nodes.Add(node);
                if (!node.HasConnectors) result.NoConnectorIds.Add(node.Id);
                else if (node.UnconnectedCount > 0) result.OpenEnds.Add(node.Id);
                if (string.Equals(node.Category, PanelCategory, StringComparison.OrdinalIgnoreCase))
                    result.Panels.Add(node.Id);
            }
            return result;
        }
    }
}
