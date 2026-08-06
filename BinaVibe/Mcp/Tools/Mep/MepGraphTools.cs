// is_system_graph_valid — the Revit adapter over MepGraphChecks.
//
// This file does ONE thing: turn a live document into the MepGraph DTO. Every
// judgement lives in MepGraphChecks.cs, which is Revit-free and unit-tested,
// so a disagreement about what counts as broken is settled in a test rather
// than in a live model.
//
// Read-only. No Transaction.
//
// SCOPE: one system (system_id), a whole discipline (domain), or a walk out
// from seed elements (element_ids). The walk is capped; `truncated` travels
// through to the report and the checks that would misfire on a partial graph
// (the Revit network-count oracle) skip themselves when it is set.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal static class MepGraphTools
    {
        private const int DefaultNodeCap = 400;
        private const int DefaultDepth = 50;

        public static Dictionary<string, object?> IsSystemGraphValid(Document doc, JsonElement args)
        {
            var nodeCap = (int)(ArgsHelp.GetLong(args, "max_nodes") ?? DefaultNodeCap);
            var sysId = ArgsHelp.GetLong(args, "system_id");
            var domainWord = ArgsHelp.GetString(args, "domain");
            var seedIds = ArgsHelp.GetLongList(args, "element_ids");

            MepGraph graph;
            string scope;

            if (sysId.HasValue)
            {
                if (doc.GetElement(ElemIds.From(sysId.Value)) is not MEPSystem sys)
                    return MepTx.Failure($"element {sysId} is not an MEP system");
                graph = BuildForSystem(doc, sys, nodeCap);
                scope = $"system {sysId}";
            }
            else if (seedIds.Count > 0)
            {
                var seeds = seedIds
                    .Select(id => doc.GetElement(ElemIds.From(id)))
                    .Where(e => e != null)
                    .ToList();
                if (seeds.Count == 0)
                    return MepTx.Failure($"none of the element_ids exist: {string.Join(", ", seedIds)}");
                graph = BuildForSeeds(doc, seeds!, nodeCap, DefaultDepth);
                scope = $"{seeds.Count} seed element(s)";
            }
            else if (!string.IsNullOrWhiteSpace(domainWord))
            {
                var kind = MepDomains.Parse(domainWord);
                if (kind == MepDomainKind.Unknown)
                    return MepTx.Failure(
                        $"unknown domain '{domainWord}' — use one of: "
                        + string.Join(", ", MepDomains.AcceptedWords));
                var driver = MepSystemDrivers.TryResolve(kind);
                if (driver == null)
                    return MepTx.Blocked("domain_not_supported",
                        $"'{MepDomains.ToWire(kind)}' systems are not implemented in this build yet");
                graph = BuildForDomain(doc, driver, nodeCap);
                scope = $"all {MepDomains.ToWire(kind)} systems";
            }
            else
            {
                return MepTx.Failure(
                    "is_system_graph_valid needs one of: system_id, element_ids, or domain");
            }

            var opt = new GraphCheckOptions
            {
                ReportOpenConnectors = ArgsHelp.GetBool(args, "report_open_connectors") ?? true,
                TreatOpenConnectorAsError = ArgsHelp.GetBool(args, "expect_closed") ?? false,
                MaxFindings = (int)(ArgsHelp.GetLong(args, "max_findings") ?? 200),
            };

            var report = MepGraphChecks.Validate(graph, opt);

            return new Dictionary<string, object?>
            {
                // ok:true means the CHECK ran. `valid` is the answer — a tool
                // that returns ok:false for "I found problems" makes the agent
                // self-heal and retry the query forever.
                ["ok"] = true,
                ["valid"] = report.Ok,
                ["scope"] = scope,
                ["node_count"] = graph.Nodes.Count,
                ["edge_count"] = graph.Edges.Count,
                ["system_count"] = graph.Systems.Count,
                ["component_count"] = report.ComponentCount,
                ["truncated"] = report.Truncated,
                ["findings_truncated"] = report.FindingsTruncated,
                ["finding_count"] = report.Findings.Count,
                ["by_code"] = report.Findings
                    .GroupBy(f => f.Code)
                    .ToDictionary(g => g.Key, g => (object?)g.Count()),
                ["findings"] = report.Findings.Select(FindingRow).ToList(),
            };
        }

        // ─── graph builders ─────────────────────────────────────────────

        internal static MepGraph BuildForSystem(Document doc, MEPSystem sys, int maxNodes)
        {
            var memberIds = MepSystemTools.MemberIds(sys);
            var seeds = memberIds
                .Select(id => doc.GetElement(ElemIds.From(id)))
                .Where(e => e != null)
                .ToList();

            var baseId = MepSystemTools.SafeBaseEquipmentId(sys);
            if (baseId.HasValue)
            {
                var b = doc.GetElement(ElemIds.From(baseId.Value));
                if (b != null) seeds.Add(b);
            }

            var graph = BuildForSeeds(doc, seeds!, maxNodes, DefaultDepth);
            AddSystem(graph, sys, memberIds, baseId);

            // A member the walk could not seed (deleted) must still appear as a
            // claim, or CheckOrphanMembers has nothing to find.
            return graph;
        }

        internal static MepGraph BuildForDomain(Document doc, IMepSystemDriver driver, int maxNodes)
        {
            var systems = driver.Collect(doc).OfType<MEPSystem>().ToList();
            var seeds = new List<Element>();
            foreach (var sys in systems)
            {
                foreach (var id in MepSystemTools.MemberIds(sys))
                {
                    var el = doc.GetElement(ElemIds.From(id));
                    if (el != null) seeds.Add(el);
                }
                var baseId = MepSystemTools.SafeBaseEquipmentId(sys);
                if (baseId.HasValue)
                {
                    var b = doc.GetElement(ElemIds.From(baseId.Value));
                    if (b != null) seeds.Add(b);
                }
            }

            var graph = BuildForSeeds(doc, seeds, maxNodes, DefaultDepth);
            foreach (var sys in systems)
                AddSystem(graph, sys, MepSystemTools.MemberIds(sys), MepSystemTools.SafeBaseEquipmentId(sys));
            return graph;
        }

        internal static MepGraph BuildForSeeds(
            Document doc, IReadOnlyList<Element> seeds, int maxNodes, int maxDepth)
        {
            var walk = MepConnectors.Traverse(seeds, maxDepth, maxNodes);
            var graph = new MepGraph { Truncated = walk.Truncated, NodeCap = maxNodes };

            foreach (var el in walk.Nodes)
            {
                var conns = MepConnectors.TryConnectorsOf(el);
                var domains = conns
                    .Select(c => MepDomains.FromConnectorDomain(c.Domain))
                    .Where(k => k != MepDomainKind.Unknown)
                    .Distinct()
                    .ToList();

                graph.Nodes.Add(new GraphNode
                {
                    Id = el.Id.Value,
                    Kind = MepElementInfo.ClassifyKind(el),
                    Category = el.Category?.Name,
                    TypeName = doc.GetElement(el.GetTypeId()) is ElementType et ? et.Name : null,
                    // One domain = that domain. Several (a fan coil carries duct
                    // AND electrical connectors) = "mixed", which the
                    // domain-mismatch check deliberately does not flag.
                    Domain = domains.Count == 1 ? MepDomains.ToWire(domains[0])
                           : domains.Count > 1 ? "mixed"
                           : "unknown",
                });

                for (int i = 0; i < conns.Count; i++)
                {
                    var origin = MepConnectors.OriginMm(conns[i]);
                    graph.Ports.Add(new GraphPort
                    {
                        OwnerId = el.Id.Value,
                        Index = i,
                        Domain = MepDomains.ConnectorDomainLabel(conns[i].Domain),
                        IsConnected = conns[i].IsConnected,
                        XMm = origin?[0], YMm = origin?[1], ZMm = origin?[2],
                        LinkedOwnerIds = PhysicalPartnerIds(conns[i], el.Id.Value),
                    });
                }
            }

            foreach (var e in walk.Edges) graph.Edges.Add(new GraphEdge(e.A, e.B));
            return graph;
        }

        // ─── helpers ────────────────────────────────────────────────────

        private static void AddSystem(MepGraph graph, MEPSystem sys, List<long> memberIds, long? baseId)
        {
            if (graph.Systems.Any(s => s.Id == sys.Id.Value)) return;

            var driver = MepSystemDrivers.ForSystem(sys);
            graph.Systems.Add(new GraphSystem
            {
                Id = sys.Id.Value,
                Name = MepElementInfo.SafeName(sys),
                Domain = MepDomains.ToWire(driver.Kind),
                // Stamped from the driver, which is what keeps MepGraphChecks
                // discipline-blind: a circuit's members need not touch, a duct
                // system's must.
                RequiresPhysicalConnection = driver.Capabilities.RequiresPhysicalConnection,
                BaseEquipmentId = baseId,
                MemberIds = memberIds,
                RevitPhysicalNetworkCount = MepSystemInspect.SafeNetworkCount(sys),
            });

            foreach (var id in memberIds)
            {
                var node = graph.Node(id);
                if (node != null && !node.ClaimedBySystemIds.Contains(sys.Id.Value))
                    node.ClaimedBySystemIds.Add(sys.Id.Value);
            }
        }

        private static List<long> PhysicalPartnerIds(Connector c, long selfId)
        {
            var ids = new List<long>();
            if (!c.IsConnected) return ids;
            ConnectorSet refs;
            try { refs = c.AllRefs; }
            catch { return ids; }

            foreach (Connector r in refs)
            {
                if (r.Owner == null) continue;
                if (r.Owner.Id.Value == selfId) continue;
                if ((r.ConnectorType & ConnectorType.Physical) == 0) continue;
                ids.Add(r.Owner.Id.Value);
            }
            return ids;
        }

        internal static Dictionary<string, object?> FindingRow(GraphFinding f)
        {
            var row = new Dictionary<string, object?>
            {
                ["code"] = f.Code,
                ["severity"] = f.Severity,
                ["message"] = f.Message,
                ["element_ids"] = f.ElementIds,
            };
            if (f.SystemId.HasValue) row["system_id"] = f.SystemId.Value;
            if (f.XMm.HasValue && f.YMm.HasValue && f.ZMm.HasValue)
                row["location_mm"] = new List<double> { f.XMm.Value, f.YMm.Value, f.ZMm.Value };
            return row;
        }
    }
}
