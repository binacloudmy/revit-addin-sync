// MepGraph.Walk — the Revit-free BFS under trace_mep_connections.
// The rules that matter: cycles terminate, caps set truncated (never throw),
// a no-connectors element is distinct from an open-connector one, and an
// unresolvable root never kills the walk.
using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Mep;
using Xunit;

namespace Tests
{
    public class MepGraphTests
    {
        /// <summary>Build a resolve callback from a simple adjacency spec.
        /// spec[id] = (category, neighborIds). Bidirectional edges are the
        /// caller's job — list both directions like Revit's AllRefs does.</summary>
        private static Func<long, MepNode?> Net(
            Dictionary<long, (string cat, long[] adj)> spec,
            Dictionary<long, int>? openCounts = null,
            HashSet<long>? noConnectors = null)
        {
            return id =>
            {
                if (!spec.TryGetValue(id, out var s)) return null;
                var node = new MepNode
                {
                    Id = id,
                    Category = s.cat,
                    Name = $"el-{id}",
                    ConnectedCount = s.adj.Length,
                    UnconnectedCount = openCounts != null && openCounts.TryGetValue(id, out var u) ? u : 0,
                    HasConnectors = noConnectors == null || !noConnectors.Contains(id),
                };
                if (node.HasConnectors)
                    node.Neighbors = s.adj.Select(a => new MepAdj
                    {
                        OtherId = a,
                        Domain = "cable_tray_conduit",
                        SystemName = "C1",
                        SystemType = "ElectricalSystem",
                    }).ToList();
                return node;
            };
        }

        private static Dictionary<long, (string, long[])> Chain(params long[] ids)
        {
            var spec = new Dictionary<long, (string, long[])>();
            for (int i = 0; i < ids.Length; i++)
            {
                var adj = new List<long>();
                if (i > 0) adj.Add(ids[i - 1]);
                if (i < ids.Length - 1) adj.Add(ids[i + 1]);
                spec[ids[i]] = ("Conduits", adj.ToArray());
            }
            return spec;
        }

        [Fact]
        public void LinearChain_IsFullyWalked()
        {
            var g = MepGraph.Walk(new long[] { 1 }, Net(Chain(1, 2, 3, 4, 5)), 10, null);
            Assert.Equal(5, g.Nodes.Count);
            Assert.Equal(4, g.Edges.Count);
            Assert.False(g.Truncated);
        }

        [Fact]
        public void TeeBranch_WalksBothArms()
        {
            var spec = Chain(1, 2, 3);
            spec[2] = ("Conduits", new long[] { 1, 3, 10 });
            spec[10] = ("Conduits", new long[] { 2, 11 });
            spec[11] = ("Conduits", new long[] { 10 });
            var g = MepGraph.Walk(new long[] { 1 }, Net(spec), 10, null);
            Assert.Equal(5, g.Nodes.Count);
            Assert.Equal(4, g.Edges.Count);
        }

        [Fact]
        public void Cycle_Terminates_WithDedupedEdges()
        {
            // Ring 1-2-3-1: three nodes, three edges, no infinite loop.
            var spec = new Dictionary<long, (string, long[])>
            {
                [1] = ("Conduits", new long[] { 2, 3 }),
                [2] = ("Conduits", new long[] { 1, 3 }),
                [3] = ("Conduits", new long[] { 1, 2 }),
            };
            var g = MepGraph.Walk(new long[] { 1 }, Net(spec), 10, null);
            Assert.Equal(3, g.Nodes.Count);
            Assert.Equal(3, g.Edges.Count);
            Assert.False(g.Truncated);
        }

        [Fact]
        public void MaxDepth_Stops_AndSetsTruncated()
        {
            var g = MepGraph.Walk(new long[] { 1 }, Net(Chain(1, 2, 3, 4, 5)), 2, null);
            Assert.Equal(3, g.Nodes.Count);   // depth 0,1,2
            Assert.True(g.Truncated);
            Assert.Contains("max_depth", g.TruncationReason);
        }

        [Fact]
        public void NodeCap_Stops_AndSetsTruncatedWithReason()
        {
            var ids = Enumerable.Range(1, 50).Select(i => (long)i).ToArray();
            var g = MepGraph.Walk(new long[] { 1 }, Net(Chain(ids)), 25, null, nodeCap: 10);
            Assert.Equal(10, g.Nodes.Count);
            Assert.True(g.Truncated);
            Assert.Contains("node cap", g.TruncationReason);
        }

        [Fact]
        public void NoConnectorsNode_IsIsolated_AndListedDistinctly()
        {
            var spec = Chain(1, 2);
            spec[99] = ("Electrical Fixtures", new long[] { });
            var g = MepGraph.Walk(new long[] { 1, 99 },
                Net(spec, noConnectors: new HashSet<long> { 99 }), 10, null);
            Assert.Contains(99, g.NoConnectorIds);
            Assert.DoesNotContain(99, g.OpenEnds);   // no connectors != open connector
            var n = g.Nodes.First(x => x.Id == 99);
            Assert.False(n.HasConnectors);
            Assert.Empty(n.Neighbors);
        }

        [Fact]
        public void OpenConnector_IsAnOpenEnd_NotANoConnector()
        {
            var g = MepGraph.Walk(new long[] { 1 },
                Net(Chain(1, 2), openCounts: new Dictionary<long, int> { [2] = 1 }), 10, null);
            Assert.Contains(2, g.OpenEnds);
            Assert.DoesNotContain(2, g.NoConnectorIds);
        }

        [Fact]
        public void Panel_IsDetectedByCategory()
        {
            var spec = Chain(1, 2, 3);
            spec[3] = ("Electrical Equipment", new long[] { 2 });
            var g = MepGraph.Walk(new long[] { 1 }, Net(spec), 10, null);
            Assert.Equal(new[] { 3L }, g.Panels.ToArray());
        }

        [Fact]
        public void DomainFilter_DropsForeignEdges()
        {
            var spec = Chain(1, 2, 3);
            var baseNet = Net(spec);
            // Wrap: make edge 2-3 piping; a cable_tray_conduit filter must stop at 2.
            Func<long, MepNode?> resolve = id =>
            {
                var n = baseNet(id);
                if (n == null) return null;
                foreach (var a in n.Neighbors)
                    if ((n.Id == 2 && a.OtherId == 3) || (n.Id == 3 && a.OtherId == 2))
                        a.Domain = "piping";
                return n;
            };
            var g = MepGraph.Walk(new long[] { 1 }, resolve, 10, "cable_tray_conduit");
            Assert.Equal(2, g.Nodes.Count);           // 3 never reached
            Assert.Single(g.Edges);
            Assert.All(g.Edges, e => Assert.Equal("cable_tray_conduit", e.Domain));
        }

        [Fact]
        public void UnresolvableRoot_IsSkipped_WalkContinues()
        {
            var g = MepGraph.Walk(new long[] { 777, 1 }, Net(Chain(1, 2)), 10, null);
            Assert.Equal(2, g.Nodes.Count);
            Assert.DoesNotContain(g.Nodes, n => n.Id == 777);
        }

        [Fact]
        public void DuplicateRoots_DoNotDuplicateNodes()
        {
            var g = MepGraph.Walk(new long[] { 1, 1, 2 }, Net(Chain(1, 2)), 10, null);
            Assert.Equal(2, g.Nodes.Count);
            Assert.Single(g.Edges);
        }
    }
}
