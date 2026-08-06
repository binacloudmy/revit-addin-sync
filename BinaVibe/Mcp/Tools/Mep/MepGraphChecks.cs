// MEP connector-graph integrity checks — pure, Revit-free, testable.
//
// Backs is_system_graph_valid. Every check is a separate public method so a
// test can pin one behaviour without constructing the whole report, and so
// create_mep_system can borrow ConnectedComponents alone as a pre-flight.
//
// FINDING CODES (stable; the agent branches on these):
//   open_connector          a connector on a non-exempt element is unconnected
//   unconnected_member      a system claims an element with no physical link
//                           to any other member of that same system
//   split_system            one system's members span >1 connected component
//   orphan_member           a system claims an id that is not in the graph
//   multi_claimed           an element is claimed by >1 system of one domain
//   empty_system            a system with no members
//   domain_mismatch         a member's domain differs from its system's
//   network_count_mismatch  our component count disagrees with Revit's
//
// SEVERITY: only `error` clears GraphReport.Ok. An open connector is a
// WARNING by default — a design in progress legitimately has open ends, and a
// validator that cries error on every unfinished run gets ignored.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Mep
{
    public static class MepGraphChecks
    {
        // ─── connected components ───────────────────────────────────────

        /// <summary>Union-find over the graph's edges. Every node gets a
        /// component index; an isolated node is its own component. Indices are
        /// assigned in Nodes order so results are deterministic and testable.</summary>
        public static Dictionary<long, int> ConnectedComponents(MepGraph g)
        {
            var parent = new Dictionary<long, long>();

            long Find(long x)
            {
                if (!parent.ContainsKey(x)) { parent[x] = x; return x; }
                var root = x;
                while (parent[root] != root) root = parent[root];
                while (parent[x] != root) { var next = parent[x]; parent[x] = root; x = next; }
                return root;
            }

            void Union(long a, long b)
            {
                var ra = Find(a);
                var rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

            foreach (var n in g.Nodes) Find(n.Id);
            // Edges may name ids outside Nodes (a neighbour past the node cap).
            // Find() registers them, so they still union correctly.
            foreach (var e in g.Edges) Union(e.A, e.B);

            var componentOfRoot = new Dictionary<long, int>();
            var result = new Dictionary<long, int>();
            foreach (var n in g.Nodes)
            {
                var root = Find(n.Id);
                if (!componentOfRoot.TryGetValue(root, out var idx))
                {
                    idx = componentOfRoot.Count;
                    componentOfRoot[root] = idx;
                }
                result[n.Id] = idx;
            }
            return result;
        }

        /// <summary>Component indices restricted to one id set, renumbered from
        /// zero. The pre-flight create_mep_system uses to answer "would these
        /// elements form one network?" without building a report.</summary>
        public static Dictionary<long, int> ComponentsOf(MepGraph g, IEnumerable<long> ids)
        {
            var wanted = new HashSet<long>(ids);
            var all = ConnectedComponents(g);
            var renumber = new Dictionary<int, int>();
            var result = new Dictionary<long, int>();
            foreach (var id in wanted)
            {
                if (!all.TryGetValue(id, out var c)) continue;
                if (!renumber.TryGetValue(c, out var idx))
                {
                    idx = renumber.Count;
                    renumber[c] = idx;
                }
                result[id] = idx;
            }
            return result;
        }

        // ─── the report ─────────────────────────────────────────────────

        public static GraphReport Validate(MepGraph g, GraphCheckOptions? opt = null)
        {
            opt ??= new GraphCheckOptions();
            var comp = ConnectedComponents(g);

            var report = new GraphReport
            {
                ComponentOfNode = comp,
                ComponentCount = comp.Count == 0 ? 0 : comp.Values.Distinct().Count(),
                Truncated = g.Truncated,
            };

            var all = new List<GraphFinding>();
            all.AddRange(CheckEmptySystems(g));
            all.AddRange(CheckOrphanMembers(g));
            all.AddRange(CheckDomainMismatch(g));
            all.AddRange(CheckMultiClaimed(g));
            all.AddRange(CheckUnconnectedMembers(g));
            all.AddRange(CheckSplitSystems(g, comp));
            all.AddRange(CheckNetworkCountAgreement(g, comp));
            if (opt.ReportOpenConnectors) all.AddRange(CheckOpenConnectors(g, opt));

            // Errors first — a truncated finding list must not drop them for a
            // pile of open-connector warnings.
            var ordered = all
                .OrderBy(f => f.Severity == "error" ? 0 : f.Severity == "warning" ? 1 : 2)
                .ToList();

            if (ordered.Count > opt.MaxFindings)
            {
                report.FindingsTruncated = true;
                ordered = ordered.Take(opt.MaxFindings).ToList();
            }

            report.Findings = ordered;
            report.Ok = !all.Any(f => f.Severity == "error");
            return report;
        }

        // ─── individual checks ──────────────────────────────────────────

        /// <summary>Unconnected connectors — "orphaned connectors" in the
        /// brief. Exempt kinds (terminals, equipment, devices) are skipped:
        /// a diffuser's single duct connector has nothing downstream by
        /// design.</summary>
        public static IEnumerable<GraphFinding> CheckOpenConnectors(MepGraph g, GraphCheckOptions opt)
        {
            var kindOf = g.Nodes.ToDictionary(n => n.Id, n => n.Kind);
            foreach (var p in g.Ports)
            {
                if (p.IsConnected) continue;
                if (kindOf.TryGetValue(p.OwnerId, out var kind)
                    && opt.OpenConnectorExemptKinds.Contains(kind)) continue;

                yield return new GraphFinding
                {
                    Code = "open_connector",
                    Severity = opt.TreatOpenConnectorAsError ? "error" : "warning",
                    Message = $"element {p.OwnerId} connector {p.Index} ({p.Domain}) is not connected",
                    ElementIds = new List<long> { p.OwnerId },
                    XMm = p.XMm, YMm = p.YMm, ZMm = p.ZMm,
                };
            }
        }

        /// <summary>A system claiming an element that has no physical link to
        /// any other member of the SAME system. Only meaningful where
        /// membership implies connection — an electrical circuit's members are
        /// deliberately unconnected, so RequiresPhysicalConnection gates it.</summary>
        public static IEnumerable<GraphFinding> CheckUnconnectedMembers(MepGraph g)
        {
            foreach (var sys in g.Systems)
            {
                if (!sys.RequiresPhysicalConnection) continue;
                if (sys.MemberIds.Count < 2) continue;

                var members = new HashSet<long>(sys.MemberIds);
                if (sys.BaseEquipmentId.HasValue) members.Add(sys.BaseEquipmentId.Value);

                var neighbours = Adjacency(g, members);
                foreach (var id in sys.MemberIds)
                {
                    if (g.Node(id) == null) continue;   // reported by CheckOrphanMembers
                    if (neighbours.TryGetValue(id, out var n) && n.Count > 0) continue;

                    yield return new GraphFinding
                    {
                        Code = "unconnected_member",
                        Severity = "error",
                        Message = $"system '{sys.Name}' ({sys.Id}) claims element {id}, "
                                + "but it has no physical connection to any other member",
                        SystemId = sys.Id,
                        ElementIds = new List<long> { id },
                    };
                }
            }
        }

        /// <summary>Members of one system spanning more than one connected
        /// component — the system looks whole in the browser and is two
        /// separate networks in the model.</summary>
        public static IEnumerable<GraphFinding> CheckSplitSystems(
            MepGraph g, IReadOnlyDictionary<long, int> comp)
        {
            foreach (var sys in g.Systems)
            {
                if (!sys.RequiresPhysicalConnection) continue;

                var groups = new Dictionary<int, List<long>>();
                foreach (var id in sys.MemberIds)
                {
                    if (!comp.TryGetValue(id, out var c)) continue;
                    if (!groups.TryGetValue(c, out var list)) groups[c] = list = new List<long>();
                    list.Add(id);
                }
                if (groups.Count < 2) continue;

                var ids = groups.Values.Select(v => v.OrderBy(x => x).First()).OrderBy(x => x).ToList();
                yield return new GraphFinding
                {
                    Code = "split_system",
                    Severity = "error",
                    Message = $"system '{sys.Name}' ({sys.Id}) spans {groups.Count} disconnected "
                            + $"networks; one representative element per network: {string.Join(", ", ids)}",
                    SystemId = sys.Id,
                    ElementIds = ids,
                };
            }
        }

        /// <summary>A system claiming an id the graph does not contain —
        /// typically a deleted element whose membership survived.</summary>
        public static IEnumerable<GraphFinding> CheckOrphanMembers(MepGraph g)
        {
            var known = new HashSet<long>(g.Nodes.Select(n => n.Id));
            foreach (var sys in g.Systems)
            {
                var missing = sys.MemberIds.Where(id => !known.Contains(id)).ToList();
                if (missing.Count == 0) continue;
                yield return new GraphFinding
                {
                    Code = "orphan_member",
                    Severity = "error",
                    Message = $"system '{sys.Name}' ({sys.Id}) claims {missing.Count} element(s) "
                            + $"that are not in the graph: {string.Join(", ", missing)}",
                    SystemId = sys.Id,
                    ElementIds = missing,
                };
            }
        }

        /// <summary>One element claimed by two systems of the same domain. Two
        /// domains claiming one element is normal (a fan coil is on a duct
        /// system AND a circuit); two circuits claiming one socket is not.</summary>
        public static IEnumerable<GraphFinding> CheckMultiClaimed(MepGraph g)
        {
            var domainOfSystem = g.Systems.ToDictionary(s => s.Id, s => s.Domain);
            foreach (var n in g.Nodes)
            {
                if (n.ClaimedBySystemIds.Count < 2) continue;
                var byDomain = n.ClaimedBySystemIds
                    .Where(domainOfSystem.ContainsKey)
                    .GroupBy(id => domainOfSystem[id]);
                foreach (var grp in byDomain)
                {
                    var sysIds = grp.OrderBy(x => x).ToList();
                    if (sysIds.Count < 2) continue;
                    yield return new GraphFinding
                    {
                        Code = "multi_claimed",
                        Severity = "error",
                        Message = $"element {n.Id} is claimed by {sysIds.Count} {grp.Key} systems: "
                                + string.Join(", ", sysIds),
                        ElementIds = new List<long> { n.Id },
                    };
                }
            }
        }

        public static IEnumerable<GraphFinding> CheckEmptySystems(MepGraph g)
        {
            foreach (var sys in g.Systems)
            {
                if (sys.MemberIds.Count > 0) continue;
                yield return new GraphFinding
                {
                    Code = "empty_system",
                    Severity = "warning",
                    Message = $"system '{sys.Name}' ({sys.Id}) has no members; "
                            + "an empty system still occupies its slot or browser entry",
                    SystemId = sys.Id,
                };
            }
        }

        /// <summary>A member whose own domain differs from its system's.</summary>
        public static IEnumerable<GraphFinding> CheckDomainMismatch(MepGraph g)
        {
            foreach (var sys in g.Systems)
            {
                foreach (var id in sys.MemberIds)
                {
                    var node = g.Node(id);
                    if (node == null) continue;
                    if (node.Domain == "unknown" || sys.Domain == "unknown") continue;
                    if (string.Equals(node.Domain, sys.Domain, StringComparison.OrdinalIgnoreCase)) continue;

                    yield return new GraphFinding
                    {
                        Code = "domain_mismatch",
                        Severity = "error",
                        Message = $"element {id} is {node.Domain} but system '{sys.Name}' ({sys.Id}) "
                                + $"is {sys.Domain}",
                        SystemId = sys.Id,
                        ElementIds = new List<long> { id },
                    };
                }
            }
        }

        /// <summary>Cross-check our component count for a system against
        /// Revit's own GetPhysicalNetworksNumber. Disagreement is either a bug
        /// here or a stale system in the model — both worth saying out loud.
        /// Skipped for truncated graphs, where a lower count is expected.</summary>
        public static IEnumerable<GraphFinding> CheckNetworkCountAgreement(
            MepGraph g, IReadOnlyDictionary<long, int> comp)
        {
            if (g.Truncated) yield break;

            foreach (var sys in g.Systems)
            {
                if (sys.RevitPhysicalNetworkCount is not int revitCount) continue;
                if (sys.MemberIds.Count == 0) continue;

                var ours = sys.MemberIds
                    .Where(comp.ContainsKey)
                    .Select(id => comp[id])
                    .Distinct()
                    .Count();
                if (ours == 0 || ours == revitCount) continue;

                yield return new GraphFinding
                {
                    Code = "network_count_mismatch",
                    Severity = "info",
                    Message = $"system '{sys.Name}' ({sys.Id}): Revit reports {revitCount} physical "
                            + $"network(s), the connector walk found {ours} — the system may be stale, "
                            + "or the walk was bounded",
                    SystemId = sys.Id,
                };
            }
        }

        // ─── helper ─────────────────────────────────────────────────────

        /// <summary>Adjacency restricted to one id set — connections that leave
        /// the set do not count, because the question is always "connected to
        /// the REST OF THIS SYSTEM".</summary>
        private static Dictionary<long, List<long>> Adjacency(MepGraph g, HashSet<long> within)
        {
            var adj = new Dictionary<long, List<long>>();
            foreach (var id in within) adj[id] = new List<long>();
            foreach (var e in g.Edges)
            {
                if (!within.Contains(e.A) || !within.Contains(e.B)) continue;
                adj[e.A].Add(e.B);
                adj[e.B].Add(e.A);
            }
            return adj;
        }
    }
}
