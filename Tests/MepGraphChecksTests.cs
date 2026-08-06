// MEP connector-graph integrity checks. Testable at all because
// MepGraphModel.cs / MepGraphChecks.cs are System-only — the Revit half that
// walks a live ConnectorManager lives in MepGraphTools.cs and is not linked
// here.
//
// The case worth protecting: an ELECTRICAL circuit legitimately claims devices
// that are not physically joined to each other, while a DUCT system claiming
// the same shape is broken. One bool on the DTO decides which, and these tests
// pin both sides of it.
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Mcp.Tools.Mep;
using Xunit;

namespace RevitWebAppSync.Tests
{
    public class MepGraphChecksTests
    {
        // ─── builders ───────────────────────────────────────────────────

        private static GraphNode Node(long id, string kind = "curve", string domain = "mechanical") =>
            new() { Id = id, Kind = kind, Domain = domain };

        private static GraphPort Port(long owner, int index, bool connected,
                                      double? x = 0, double? y = 0, double? z = 0) =>
            new() { OwnerId = owner, Index = index, IsConnected = connected, XMm = x, YMm = y, ZMm = z };

        private static GraphSystem Sys(long id, bool requiresPhysical, params long[] members) => new()
        {
            Id = id,
            Name = $"SYS{id}",
            Domain = requiresPhysical ? "mechanical" : "electrical",
            RequiresPhysicalConnection = requiresPhysical,
            MemberIds = members.ToList(),
        };

        private static MepGraph Graph(IEnumerable<GraphNode> nodes,
                                      IEnumerable<(long, long)> edges,
                                      params GraphSystem[] systems) => new()
        {
            Nodes = nodes.ToList(),
            Edges = edges.Select(e => new GraphEdge(e.Item1, e.Item2)).ToList(),
            Systems = systems.ToList(),
        };

        // ─── ConnectedComponents ────────────────────────────────────────

        [Fact]
        public void ConnectedComponents_puts_a_chain_in_one_component()
        {
            var g = Graph(new[] { Node(1), Node(2), Node(3) }, new[] { (1L, 2L), (2L, 3L) });
            var comp = MepGraphChecks.ConnectedComponents(g);
            Assert.Equal(comp[1], comp[2]);
            Assert.Equal(comp[2], comp[3]);
            Assert.Single(comp.Values.Distinct());
        }

        [Fact]
        public void ConnectedComponents_treats_an_isolated_node_as_its_own_component()
        {
            var g = Graph(new[] { Node(1), Node(2), Node(9) }, new[] { (1L, 2L) });
            var comp = MepGraphChecks.ConnectedComponents(g);
            Assert.NotEqual(comp[1], comp[9]);
            Assert.Equal(2, comp.Values.Distinct().Count());
        }

        [Fact]
        public void ConnectedComponents_is_order_independent_for_a_ring()
        {
            var g = Graph(new[] { Node(1), Node(2), Node(3), Node(4) },
                          new[] { (3L, 4L), (1L, 2L), (2L, 3L), (4L, 1L) });
            var comp = MepGraphChecks.ConnectedComponents(g);
            Assert.Single(comp.Values.Distinct());
        }

        [Fact]
        public void ComponentsOf_renumbers_from_zero_within_the_requested_set()
        {
            var g = Graph(new[] { Node(1), Node(2), Node(7), Node(8) },
                          new[] { (1L, 2L), (7L, 8L) });
            var comp = MepGraphChecks.ComponentsOf(g, new long[] { 7, 8 });
            Assert.Equal(2, comp.Count);
            Assert.Equal(comp[7], comp[8]);
            Assert.Equal(0, comp[7]);
        }

        // ─── the electrical / mechanical asymmetry ──────────────────────

        [Fact]
        public void A_duct_system_claiming_an_unjoined_member_is_an_error()
        {
            var g = Graph(new[] { Node(1), Node(2), Node(3) },
                          new[] { (1L, 2L) },
                          Sys(100, requiresPhysical: true, 1, 2, 3));

            var findings = MepGraphChecks.CheckUnconnectedMembers(g).ToList();
            var f = Assert.Single(findings);
            Assert.Equal("unconnected_member", f.Code);
            Assert.Equal("error", f.Severity);
            Assert.Equal(3L, Assert.Single(f.ElementIds));
            Assert.Equal(100L, f.SystemId);
        }

        [Fact]
        public void An_electrical_circuit_claiming_unjoined_members_is_fine()
        {
            // Same shape as the test above — only the flag differs. Sockets on
            // a circuit are not physically connected to each other and must not
            // be reported as a defect.
            var g = Graph(new[] { Node(1, "device", "electrical"),
                                  Node(2, "device", "electrical"),
                                  Node(3, "device", "electrical") },
                          System.Array.Empty<(long, long)>(),
                          Sys(100, requiresPhysical: false, 1, 2, 3));

            Assert.Empty(MepGraphChecks.CheckUnconnectedMembers(g));
            Assert.Empty(MepGraphChecks.CheckSplitSystems(g, MepGraphChecks.ConnectedComponents(g)));
        }

        [Fact]
        public void A_one_member_system_is_never_unconnected()
        {
            var g = Graph(new[] { Node(1) }, System.Array.Empty<(long, long)>(),
                          Sys(100, requiresPhysical: true, 1));
            Assert.Empty(MepGraphChecks.CheckUnconnectedMembers(g));
        }

        // ─── split systems ──────────────────────────────────────────────

        [Fact]
        public void CheckSplitSystems_reports_one_representative_per_network()
        {
            var g = Graph(new[] { Node(1), Node(2), Node(7), Node(8) },
                          new[] { (1L, 2L), (7L, 8L) },
                          Sys(100, requiresPhysical: true, 1, 2, 7, 8));

            var f = Assert.Single(MepGraphChecks.CheckSplitSystems(g, MepGraphChecks.ConnectedComponents(g)));
            Assert.Equal("split_system", f.Code);
            Assert.Equal("error", f.Severity);
            Assert.Equal(new List<long> { 1, 7 }, f.ElementIds);
            Assert.Contains("2 disconnected", f.Message);
        }

        // ─── orphan / multi-claim / empty / domain ──────────────────────

        [Fact]
        public void CheckOrphanMembers_names_the_ids_that_are_not_in_the_graph()
        {
            var g = Graph(new[] { Node(1) }, System.Array.Empty<(long, long)>(),
                          Sys(100, requiresPhysical: true, 1, 55));
            var f = Assert.Single(MepGraphChecks.CheckOrphanMembers(g));
            Assert.Equal("orphan_member", f.Code);
            Assert.Equal(new List<long> { 55 }, f.ElementIds);
        }

        [Fact]
        public void CheckMultiClaimed_ignores_two_systems_of_different_domains()
        {
            // A fan coil is legitimately on a duct system AND a circuit.
            var node = Node(1, "equipment", "mechanical");
            node.ClaimedBySystemIds = new List<long> { 100, 200 };
            var g = Graph(new[] { node }, System.Array.Empty<(long, long)>(),
                          Sys(100, requiresPhysical: true, 1),
                          Sys(200, requiresPhysical: false, 1));

            Assert.Empty(MepGraphChecks.CheckMultiClaimed(g));
        }

        [Fact]
        public void CheckMultiClaimed_flags_two_systems_of_the_same_domain()
        {
            var node = Node(1, "device", "electrical");
            node.ClaimedBySystemIds = new List<long> { 200, 201 };
            var g = Graph(new[] { node }, System.Array.Empty<(long, long)>(),
                          Sys(200, requiresPhysical: false, 1),
                          Sys(201, requiresPhysical: false, 1));

            var f = Assert.Single(MepGraphChecks.CheckMultiClaimed(g));
            Assert.Equal("multi_claimed", f.Code);
            Assert.Equal("error", f.Severity);
        }

        [Fact]
        public void CheckEmptySystems_is_a_warning_not_an_error()
        {
            var g = Graph(new[] { Node(1) }, System.Array.Empty<(long, long)>(),
                          Sys(100, requiresPhysical: true));
            var f = Assert.Single(MepGraphChecks.CheckEmptySystems(g));
            Assert.Equal("empty_system", f.Code);
            Assert.Equal("warning", f.Severity);
        }

        [Fact]
        public void CheckDomainMismatch_flags_a_pipe_member_on_a_duct_system()
        {
            var g = Graph(new[] { Node(1, "curve", "piping") }, System.Array.Empty<(long, long)>(),
                          Sys(100, requiresPhysical: true, 1));
            var f = Assert.Single(MepGraphChecks.CheckDomainMismatch(g));
            Assert.Equal("domain_mismatch", f.Code);
            Assert.Equal("error", f.Severity);
        }

        [Fact]
        public void CheckDomainMismatch_stays_quiet_when_either_side_is_unknown()
        {
            var g = Graph(new[] { Node(1, "curve", "unknown") }, System.Array.Empty<(long, long)>(),
                          Sys(100, requiresPhysical: true, 1));
            Assert.Empty(MepGraphChecks.CheckDomainMismatch(g));
        }

        // ─── open connectors ────────────────────────────────────────────

        [Fact]
        public void CheckOpenConnectors_exempts_terminals_and_equipment_by_default()
        {
            var g = Graph(new[] { Node(1, "curve"), Node(2, "terminal"), Node(3, "equipment") },
                          System.Array.Empty<(long, long)>());
            g.Ports.Add(Port(1, 0, connected: false, x: 100, y: 200, z: 2700));
            g.Ports.Add(Port(2, 0, connected: false));
            g.Ports.Add(Port(3, 0, connected: false));

            var f = Assert.Single(MepGraphChecks.CheckOpenConnectors(g, new GraphCheckOptions()));
            Assert.Equal(1L, Assert.Single(f.ElementIds));
            Assert.Equal("warning", f.Severity);
            Assert.Equal(2700, f.ZMm);
        }

        [Fact]
        public void CheckOpenConnectors_escalates_to_error_when_the_caller_asserts_a_closed_run()
        {
            var g = Graph(new[] { Node(1, "curve") }, System.Array.Empty<(long, long)>());
            g.Ports.Add(Port(1, 0, connected: false));

            var opt = new GraphCheckOptions { TreatOpenConnectorAsError = true };
            Assert.Equal("error", Assert.Single(MepGraphChecks.CheckOpenConnectors(g, opt)).Severity);
        }

        [Fact]
        public void CheckOpenConnectors_ignores_connected_ports()
        {
            var g = Graph(new[] { Node(1, "curve") }, System.Array.Empty<(long, long)>());
            g.Ports.Add(Port(1, 0, connected: true));
            Assert.Empty(MepGraphChecks.CheckOpenConnectors(g, new GraphCheckOptions()));
        }

        // ─── network count oracle ───────────────────────────────────────

        [Fact]
        public void CheckNetworkCountAgreement_reports_a_disagreement_as_info()
        {
            var sys = Sys(100, requiresPhysical: true, 1, 2);
            sys.RevitPhysicalNetworkCount = 2;
            var g = Graph(new[] { Node(1), Node(2) }, new[] { (1L, 2L) }, sys);

            var f = Assert.Single(MepGraphChecks.CheckNetworkCountAgreement(g, MepGraphChecks.ConnectedComponents(g)));
            Assert.Equal("network_count_mismatch", f.Code);
            Assert.Equal("info", f.Severity);
        }

        [Fact]
        public void CheckNetworkCountAgreement_is_skipped_on_a_truncated_walk()
        {
            var sys = Sys(100, requiresPhysical: true, 1, 2);
            sys.RevitPhysicalNetworkCount = 2;
            var g = Graph(new[] { Node(1), Node(2) }, new[] { (1L, 2L) }, sys);
            g.Truncated = true;

            Assert.Empty(MepGraphChecks.CheckNetworkCountAgreement(g, MepGraphChecks.ConnectedComponents(g)));
        }

        // ─── Validate ───────────────────────────────────────────────────

        [Fact]
        public void Validate_is_ok_when_only_warnings_fired()
        {
            var g = Graph(new[] { Node(1, "curve"), Node(2, "curve") }, new[] { (1L, 2L) },
                          Sys(100, requiresPhysical: true, 1, 2));
            g.Ports.Add(Port(1, 0, connected: false));

            var report = MepGraphChecks.Validate(g);
            Assert.True(report.Ok);
            Assert.Equal("open_connector", Assert.Single(report.Findings).Code);
            Assert.Equal(1, report.ComponentCount);
        }

        [Fact]
        public void Validate_is_not_ok_once_an_error_fires()
        {
            var g = Graph(new[] { Node(1), Node(2), Node(3) }, new[] { (1L, 2L) },
                          Sys(100, requiresPhysical: true, 1, 2, 3));
            var report = MepGraphChecks.Validate(g);
            Assert.False(report.Ok);
            Assert.Contains(report.Findings, f => f.Code == "unconnected_member");
        }

        [Fact]
        public void Validate_orders_errors_ahead_of_warnings_before_truncating()
        {
            var g = Graph(new[] { Node(1), Node(2), Node(3) }, new[] { (1L, 2L) },
                          Sys(100, requiresPhysical: true, 1, 2, 3));
            for (int i = 0; i < 10; i++) g.Ports.Add(Port(1, i, connected: false));

            var report = MepGraphChecks.Validate(g, new GraphCheckOptions { MaxFindings = 1 });
            Assert.True(report.FindingsTruncated);
            Assert.Equal("error", Assert.Single(report.Findings).Severity);
            Assert.False(report.Ok);   // Ok reflects ALL findings, not the kept ones
        }

        [Fact]
        public void Validate_carries_the_truncated_flag_through()
        {
            var g = Graph(new[] { Node(1) }, System.Array.Empty<(long, long)>());
            g.Truncated = true;
            Assert.True(MepGraphChecks.Validate(g).Truncated);
        }
    }
}
