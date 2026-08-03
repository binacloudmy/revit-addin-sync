// trace_mep_connections — typed walk over the MEP connector graph. READ-ONLY.
// No Transaction is ever opened here (suggest_socket_points rationale: a read
// must never fire the pane's Ya/Tidak card).
//
// This tool exists because connectivity questions used to go to generated C#,
// which failed to compile four times in one production session, and the
// fallback proxy (get_model_warnings) only ever saw OPEN connectors — an
// element with NO connectors at all raises no warning and was invisible.
// Here that element comes back with no_connectors: true, explicitly.
//
// Connectors never span Revit links, so the result carries a constant
// scope: "host_model_only" — stated, not discovered the hard way.
//
// The BFS, caps and classification live in MepGraph.cs (Revit-free, tested);
// this file only materializes nodes from the live Document.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal static class TraceMep
    {
        private static readonly string[] Domains =
            { "electrical", "piping", "hvac", "cable_tray_conduit" };

        public static Dictionary<string, object?> Run(Document doc, JsonElement args)
        {
            var ids = new List<long>();
            if (args.TryGetProperty("element_ids", out var idArr) && idArr.ValueKind == JsonValueKind.Array)
                foreach (var e in idArr.EnumerateArray())
                    if (e.TryGetInt64(out var v)) ids.Add(v);
            if (ids.Count == 0)
                throw new ArgumentException("element_ids required");

            int maxDepth = (int)(ArgsHelp.GetLong(args, "max_depth") ?? 10);
            var domain = ArgsHelp.GetString(args, "domain")?.Trim().ToLowerInvariant();
            if (domain != null && !Domains.Contains(domain))
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"unknown domain '{domain}'",
                    ["supported"] = Domains.Cast<object>().ToList(),
                };

            var skipped = new List<long>();
            MepNode? Resolve(long id)
            {
                Element? el = null;
                try { el = doc.GetElement(ElemIds.From(id)); } catch { }
                if (el == null) { skipped.Add(id); return null; }
                return ToNode(doc, el);
            }

            var graph = MepGraph.Walk(ids, Resolve, maxDepth, domain);

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["nodes"] = graph.Nodes.Select(n => (object?)new Dictionary<string, object?>
                {
                    ["id"] = n.Id,
                    ["category"] = n.Category,
                    ["name"] = n.Name,
                    ["level"] = n.Level,
                    ["connected_count"] = n.ConnectedCount,
                    ["unconnected_count"] = n.UnconnectedCount,
                    ["no_connectors"] = !n.HasConnectors,
                }).ToList(),
                ["edges"] = graph.Edges.Select(e => (object?)new Dictionary<string, object?>
                {
                    ["from_id"] = e.FromId,
                    ["to_id"] = e.ToId,
                    ["domain"] = e.Domain,
                    ["system_type"] = e.SystemType,
                    ["system_name"] = e.SystemName,
                }).ToList(),
                ["roots"] = ids.Cast<object?>().ToList(),
                ["panels"] = graph.Panels.Cast<object?>().ToList(),
                ["open_ends"] = graph.OpenEnds.Select(id => (object?)new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["unconnected_count"] = graph.Nodes.First(n => n.Id == id).UnconnectedCount,
                }).ToList(),
                ["no_connector_ids"] = graph.NoConnectorIds.Cast<object?>().ToList(),
                ["node_count"] = graph.Nodes.Count,
                ["edge_count"] = graph.Edges.Count,
                ["truncated"] = graph.Truncated,
                ["scope"] = "host_model_only",
            };
            if (graph.TruncationReason != null) result["truncation_reason"] = graph.TruncationReason;
            if (skipped.Count > 0) result["skipped_ids"] = skipped.Cast<object?>().ToList();
            return result;
        }

        // ── element -> node (the only Revit-bound part) ─────────────────

        private static MepNode ToNode(Document doc, Element el)
        {
            var node = new MepNode
            {
                Id = el.Id.Value,
                Category = el.Category?.Name ?? "",
                Name = el.Name ?? "",
                Level = (doc.GetElement(el.LevelId) as Level)?.Name,
            };

            ConnectorManager? cm = null;
            if (el is MEPCurve mc) cm = mc.ConnectorManager;
            else if (el is FamilyInstance fi) cm = fi.MEPModel?.ConnectorManager;

            if (cm == null || cm.Connectors == null || cm.Connectors.Size == 0)
            {
                // The warnings-proxy blind spot, made visible: this element
                // cannot participate in a system at all.
                node.HasConnectors = false;
                return node;
            }

            foreach (Connector c in cm.Connectors)
            {
                bool connected;
                try { connected = c.IsConnected; } catch { connected = false; }
                if (connected) node.ConnectedCount++; else node.UnconnectedCount++;
                if (!connected) continue;

                string? sysName = null, sysType = null;
                try
                {
                    var sys = c.MEPSystem;
                    if (sys != null) { sysName = sys.Name; sysType = sys.GetType().Name; }
                }
                catch { }

                foreach (Connector r in c.AllRefs)
                {
                    var owner = r.Owner;
                    if (owner == null) continue;
                    // AllRefs includes the owning MEPSystem's logical
                    // connector — a ghost node the drafter cannot see. Skip
                    // it; the system rides on the edge as system_name/type.
                    if (owner is MEPSystem) continue;
                    if (owner.Id.Value == el.Id.Value) continue;

                    node.Neighbors.Add(new MepAdj
                    {
                        OtherId = owner.Id.Value,
                        Domain = DomainName(c),
                        SystemName = sysName,
                        SystemType = sysType,
                    });
                }
            }
            return node;
        }

        private static string DomainName(Connector c)
        {
            try
            {
                return c.Domain switch
                {
                    Autodesk.Revit.DB.Domain.DomainElectrical => "electrical",
                    Autodesk.Revit.DB.Domain.DomainPiping => "piping",
                    Autodesk.Revit.DB.Domain.DomainHvac => "hvac",
                    Autodesk.Revit.DB.Domain.DomainCableTrayConduit => "cable_tray_conduit",
                    _ => "undefined",
                };
            }
            catch { return "undefined"; }
        }
    }
}
