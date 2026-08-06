// connect_elements, reconnect_element, disconnect_elements — Layer 0.
//
// These operate on the CONNECTOR GRAPH, one level below systems. Revit's model
// is bottom-up: elements carry connectors, connectors join in pairs, and a
// system is a label wrapped around an already-joined subgraph. So for duct and
// pipe work these tools come FIRST and create_mep_system comes after. (An
// electrical circuit is the exception — it is a logical label and needs no
// prior connection; see IMepSystemDriver's header.)
//
// DISCONNECT DELETES NOTHING. Breaking a join leaves both elements, both
// connectors and any system membership alone; that separation is the whole
// point of having a disconnect tool rather than telling the agent to delete
// and re-create.
//
// Each tool owns ONE transaction and reports the resulting open connectors, so
// a caller can see immediately whether the join landed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal static class MepConnectionTools
    {
        // ─── connect_elements ───────────────────────────────────────────

        /// <summary>Join two elements. Connector indices are optional — omitted,
        /// the best compatible free pair is chosen the same way
        /// find_compatible_connector ranks them.</summary>
        public static Dictionary<string, object?> ConnectElements(Document doc, JsonElement args)
        {
            var idA = ArgsHelp.GetLong(args, "element_id_a")
                ?? throw new ArgumentException("missing element_id_a");
            var idB = ArgsHelp.GetLong(args, "element_id_b")
                ?? throw new ArgumentException("missing element_id_b");

            var a = doc.GetElement(ElemIds.From(idA))
                ?? throw new ArgumentException($"element {idA} not found");
            var b = doc.GetElement(ElemIds.From(idB))
                ?? throw new ArgumentException($"element {idB} not found");

            var wantA = (int?)ArgsHelp.GetLong(args, "connector_index_a");
            var wantB = (int?)ArgsHelp.GetLong(args, "connector_index_b");

            Transaction? tx = null;
            try
            {
                var consA = MepConnectors.TryConnectorsOf(a);
                var consB = MepConnectors.TryConnectorsOf(b);

                var ca = Pick(consA, wantA, idA, "element_id_a");
                var cb = Pick(consB, wantB, idB, "element_id_b");

                if (ca == null || cb == null)
                {
                    var chosen = ChooseBestPair(consA, consB);
                    if (chosen == null)
                        return MepTx.Blocked("no_compatible_pair",
                            $"no free connector pair of the same domain between {idA} and {idB} — "
                            + "run find_compatible_connector to see why, or disconnect_elements first");
                    ca ??= consA[chosen.Value.A];
                    cb ??= consB[chosen.Value.B];
                }

                if (ca.Domain != cb.Domain)
                    return MepTx.Failure(
                        $"connector domains differ ({MepDomains.ConnectorDomainLabel(ca.Domain)} vs "
                        + $"{MepDomains.ConnectorDomainLabel(cb.Domain)}) — these cannot be joined");

                if (ca.IsConnectedTo(cb))
                    return MepTx.Blocked("already_connected",
                        $"elements {idA} and {idB} are already joined at these connectors");

                tx = new Transaction(doc, "BINA: connect_elements");
                TxGuard.StartSwallowing(tx);

                ca.ConnectTo(cb);

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["element_id_a"] = idA,
                    ["element_id_b"] = idB,
                    ["connector_index_a"] = consA.IndexOf(ca),
                    ["connector_index_b"] = consB.IndexOf(cb),
                    ["domain"] = MepDomains.ConnectorDomainLabel(ca.Domain),
                    ["open_connectors"] = MepConnectors.ComputeOpenConnectors(new[] { a, b }),
                };
                TxGuard.CommitOrThrow(tx);
                return result;
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message);
            }
        }

        // ─── reconnect_element ──────────────────────────────────────────

        /// <summary>Move one end of a join: element A's connector leaves
        /// whatever it is attached to and joins element B instead. Two steps in
        /// ONE transaction, so a failure to attach cannot leave the model with
        /// the old link already broken.</summary>
        public static Dictionary<string, object?> ReconnectElement(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id")
                ?? throw new ArgumentException("missing element_id");
            var newId = ArgsHelp.GetLong(args, "new_target_id")
                ?? throw new ArgumentException("missing new_target_id");

            var el = doc.GetElement(ElemIds.From(id))
                ?? throw new ArgumentException($"element {id} not found");
            var target = doc.GetElement(ElemIds.From(newId))
                ?? throw new ArgumentException($"element {newId} not found");

            var connIndex = (int?)ArgsHelp.GetLong(args, "connector_index");
            var targetIndex = (int?)ArgsHelp.GetLong(args, "new_connector_index");

            Transaction? tx = null;
            try
            {
                var cons = MepConnectors.TryConnectorsOf(el);
                if (cons.Count == 0)
                    return MepTx.Blocked("no_connectors", $"element {id} has no MEP connectors");

                // Default to the connector that is actually attached to
                // something — reconnect means "move an existing join", so an
                // unconnected connector is almost never what the caller meant.
                var source = connIndex.HasValue
                    ? Pick(cons, connIndex, id, "element_id")
                    : cons.FirstOrDefault(c => c.IsConnected) ?? cons[0];
                if (source == null)
                    return MepTx.Failure($"connector_index out of range on element {id}");

                var targetCons = MepConnectors.TryConnectorsOf(target);
                var dest = targetIndex.HasValue
                    ? Pick(targetCons, targetIndex, newId, "new_target_id")
                    : MepConnectors.NearestConnector(
                        targetCons.Where(c => !c.IsConnected && c.Domain == source.Domain),
                        SafeOrigin(source) ?? XYZ.Zero, double.MaxValue, freeOnly: true);

                if (dest == null)
                    return MepTx.Blocked("no_free_target_connector",
                        $"element {newId} has no free {MepDomains.ConnectorDomainLabel(source.Domain)} "
                        + "connector to receive this one");

                // Capture the old partners BEFORE breaking anything so the
                // result can say what was detached, not just what was attached.
                var previous = PhysicalPartners(source);

                tx = new Transaction(doc, "BINA: reconnect_element");
                TxGuard.StartSwallowing(tx);

                foreach (var partner in previous)
                {
                    try { source.DisconnectFrom(partner.Conn); }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"could not detach element {id} from {partner.OwnerId}: {ex.Message}");
                    }
                }
                source.ConnectTo(dest);

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["element_id"] = id,
                    ["connector_index"] = cons.IndexOf(source),
                    ["detached_from"] = previous.Select(p => p.OwnerId).ToList(),
                    ["new_target_id"] = newId,
                    ["new_connector_index"] = targetCons.IndexOf(dest),
                    ["open_connectors"] = MepConnectors.ComputeOpenConnectors(new[] { el, target }),
                };
                TxGuard.CommitOrThrow(tx);
                return result;
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message);
            }
        }

        // ─── disconnect_elements ────────────────────────────────────────

        /// <summary>Break the join between two elements. Neither element is
        /// deleted, neither connector is removed, and system membership is left
        /// exactly as it was — a disconnected member shows up in
        /// is_system_graph_valid rather than being silently tidied away.</summary>
        public static Dictionary<string, object?> DisconnectElements(Document doc, JsonElement args)
        {
            var idA = ArgsHelp.GetLong(args, "element_id_a")
                ?? throw new ArgumentException("missing element_id_a");
            var idB = ArgsHelp.GetLong(args, "element_id_b")
                ?? throw new ArgumentException("missing element_id_b");

            var a = doc.GetElement(ElemIds.From(idA))
                ?? throw new ArgumentException($"element {idA} not found");
            var b = doc.GetElement(ElemIds.From(idB))
                ?? throw new ArgumentException($"element {idB} not found");

            Transaction? tx = null;
            try
            {
                var consA = MepConnectors.TryConnectorsOf(a);
                var consB = MepConnectors.TryConnectorsOf(b);

                var joins = new List<(Connector A, Connector B, int IndexA, int IndexB)>();
                for (int i = 0; i < consA.Count; i++)
                    for (int j = 0; j < consB.Count; j++)
                        if (SafeIsConnectedTo(consA[i], consB[j]))
                            joins.Add((consA[i], consB[j], i, j));

                if (joins.Count == 0)
                    return MepTx.Blocked("not_connected",
                        $"elements {idA} and {idB} are not joined at any connector");

                tx = new Transaction(doc, "BINA: disconnect_elements");
                TxGuard.StartSwallowing(tx);

                var broken = new List<Dictionary<string, object?>>();
                foreach (var j in joins)
                {
                    j.A.DisconnectFrom(j.B);
                    broken.Add(new Dictionary<string, object?>
                    {
                        ["connector_index_a"] = j.IndexA,
                        ["connector_index_b"] = j.IndexB,
                    });
                }

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["element_id_a"] = idA,
                    ["element_id_b"] = idB,
                    ["disconnected"] = broken,
                    ["count"] = broken.Count,
                    ["elements_deleted"] = 0,   // stated, not implied — this tool never deletes
                    ["open_connectors"] = MepConnectors.ComputeOpenConnectors(new[] { a, b }),
                };
                TxGuard.CommitOrThrow(tx);
                return result;
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message);
            }
        }

        // ─── helpers ────────────────────────────────────────────────────

        private static Connector? Pick(List<Connector> cons, int? index, long elementId, string argName)
        {
            if (index == null) return null;
            if (index.Value < 0 || index.Value >= cons.Count)
                throw new ArgumentException(
                    $"connector index {index} is out of range for {argName}={elementId} "
                    + $"(it has {cons.Count} connector(s) — use get_element_mep_info)");
            return cons[index.Value];
        }

        /// <summary>Best free, same-domain pair by shape/size then distance —
        /// the same ordering find_compatible_connector reports, kept here so
        /// connect_elements without indices picks what that tool would show
        /// as `best`.</summary>
        private static (int A, int B)? ChooseBestPair(List<Connector> consA, List<Connector> consB)
        {
            (int A, int B)? best = null;
            var bestKey = (int.MaxValue, double.MaxValue);

            for (int i = 0; i < consA.Count; i++)
            {
                if (consA[i].IsConnected) continue;
                for (int j = 0; j < consB.Count; j++)
                {
                    if (consB[j].IsConnected) continue;
                    if (consA[i].Domain != consB[j].Domain) continue;

                    var shapeMatch = SafeShapeMatch(consA[i], consB[j]);
                    var dist = SafeDistance(consA[i], consB[j]);
                    var key = (shapeMatch ? 0 : 1, dist);
                    if (key.CompareTo(bestKey) >= 0) continue;
                    bestKey = key;
                    best = (i, j);
                }
            }
            return best;
        }

        private static bool SafeShapeMatch(Connector a, Connector b)
        {
            try { return a.Shape == b.Shape; }
            catch { return false; }
        }

        private static double SafeDistance(Connector a, Connector b)
        {
            try { return a.Origin.DistanceTo(b.Origin); }
            catch { return double.MaxValue; }
        }

        private static XYZ? SafeOrigin(Connector c)
        {
            try { return c.Origin; }
            catch { return null; }
        }

        private static bool SafeIsConnectedTo(Connector a, Connector b)
        {
            try { return a.IsConnectedTo(b); }
            catch { return false; }
        }

        private static List<(Connector Conn, long OwnerId)> PhysicalPartners(Connector c)
        {
            var partners = new List<(Connector, long)>();
            if (!c.IsConnected) return partners;
            ConnectorSet refs;
            try { refs = c.AllRefs; }
            catch { return partners; }

            foreach (Connector r in refs)
            {
                if (r.Owner == null) continue;
                if (c.Owner != null && r.Owner.Id.Value == c.Owner.Id.Value) continue;
                if ((r.ConnectorType & ConnectorType.Physical) == 0) continue;
                partners.Add((r, r.Owner.Id.Value));
            }
            return partners;
        }
    }
}
