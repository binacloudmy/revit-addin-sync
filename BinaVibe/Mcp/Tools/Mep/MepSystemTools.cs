// create_mep_system, delete_mep_system, and the member add/remove core —
// Layer 0, discipline-agnostic.
//
// Every discipline-specific decision goes through IMepSystemDriver. There is
// no `if (electrical)` in this file, and adding Mechanical must not introduce
// one: the pre-flight branches on MepDriverCapabilities, which is DATA the
// driver supplies.
//
// TRANSACTION SPLIT: each tool entry point owns the Transaction and converts
// failure into {ok:false}. The *Core methods take no transaction and THROW.
// Layer 2 composites call only *Core — chaining entry points would attempt an
// illegal nested transaction and would sail straight past a failed step that
// RETURNED rather than threw.
//
// DELETING A SYSTEM DELETES THE LABEL, NOT THE MEMBERS. RevitAPI.xml on
// Document.Delete: it removes "any elements that are totally dependent" — a
// device is not totally dependent on its circuit (it exists uncircuited), a
// Wire is. Rather than assert what that covers, the tool returns Revit's own
// deleted-id set verbatim plus how many of the pre-delete members are in it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal static class MepSystemTools
    {
        /// <summary>Node cap for the connectivity pre-flight. Generous — this
        /// walk only runs for disciplines that require physical connection.</summary>
        private const int PreflightNodeCap = 500;

        // ─── create_mep_system ──────────────────────────────────────────

        public static Dictionary<string, object?> CreateMepSystem(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var driver = ResolveDriver(args);
                var ids = ArgsHelp.GetLongList(args, "element_ids");
                if (ids.Count == 0)
                    return MepTx.Failure("create_mep_system: element_ids is required and must not be empty");

                var members = new List<ElementId>();
                var missing = new List<long>();
                foreach (var id in ids)
                {
                    var el = doc.GetElement(ElemIds.From(id));
                    if (el == null) missing.Add(id);
                    else members.Add(el.Id);
                }
                if (missing.Count > 0)
                    return MepTx.Failure($"element(s) not found: {string.Join(", ", missing)}");

                var baseId = ArgsHelp.GetLong(args, "base_equipment_id");
                if (baseId.HasValue && doc.GetElement(ElemIds.From(baseId.Value)) == null)
                    return MepTx.Failure($"base_equipment_id {baseId} not found");

                var preflight = PreCheck(doc, driver, members);
                if (preflight != null) return preflight;

                var req = new MepSystemCreateRequest
                {
                    MemberIds = members,
                    BaseEquipmentId = baseId.HasValue ? ElemIds.From(baseId.Value) : null,
                    BaseConnectorIndex = (int?)ArgsHelp.GetLong(args, "base_connector_index"),
                    SystemTypeName = ArgsHelp.GetString(args, "system_type"),
                    SystemName = ArgsHelp.GetString(args, "name"),
                    RawArgs = args,
                };

                tx = new Transaction(doc, $"BINA: create_mep_system ({MepDomains.ToWire(driver.Kind)})");
                TxGuard.StartSwallowing(tx);

                var result = CreateCore(doc, driver, req);
                var row = Describe(result.System, driver);
                row["ok"] = true;
                row["warnings"] = result.Warnings;

                TxGuard.CommitOrThrow(tx);
                return row;
            }
            catch (Exception ex)
            {
                MepTx.SafeRollback(tx);
                return MepTx.Failure(ex.Message, ClassifyError(ex));
            }
        }

        /// <summary>No transaction, throws on failure. Layer 2 calls this.</summary>
        internal static MepSystemCreateResult CreateCore(
            Document doc, IMepSystemDriver driver, in MepSystemCreateRequest req)
        {
            var result = driver.Create(doc, req);

            // ElectricalSystem.Create is documented to return NULL on failure
            // rather than throw; without this check CommitOrThrow commits an
            // empty transaction and the tool reports a success that never
            // happened. Drivers throw, but the guard stays here too.
            if (result.System == null)
                throw new InvalidOperationException(
                    "Revit declined to create the system and gave no reason — "
                    + "check that the elements accept this system type "
                    + "(get_element_mep_info shows each element's domains)");

            if (!string.IsNullOrWhiteSpace(req.SystemName))
            {
                try { result.System.Name = req.SystemName; }
                catch (Exception ex)
                {
                    result.Warnings.Add($"could not set the system name: {ex.Message}");
                }
            }

            if (req.BaseEquipmentId != null)
                driver.SetBaseEquipment(doc, result.System, req.BaseEquipmentId, req.BaseConnectorIndex);

            return result;
        }

        // ─── delete_mep_system ──────────────────────────────────────────

        public static Dictionary<string, object?> DeleteMepSystem(Document doc, JsonElement args)
        {
            Transaction? tx = null;
            try
            {
                var sysId = ArgsHelp.GetLong(args, "system_id")
                    ?? throw new ArgumentException("missing system_id");
                if (doc.GetElement(ElemIds.From(sysId)) is not MEPSystem sys)
                    return MepTx.Failure($"element {sysId} is not an MEP system");

                var driver = MepSystemDrivers.ForSystem(sys);
                var deleteMembers = ArgsHelp.GetBool(args, "delete_members") ?? false;

                var memberIds = MemberIds(sys);
                var name = MepElementInfo.SafeName(sys);

                tx = new Transaction(doc, "BINA: delete_mep_system");
                TxGuard.StartSwallowing(tx);

                var deleted = new List<long>(driver.Delete(doc, sys, deleteMembers));

                if (deleteMembers)
                {
                    var survivors = memberIds
                        .Where(id => !deleted.Contains(id))
                        .Select(id => ElemIds.From(id))
                        .Where(eid => doc.GetElement(eid) != null)
                        .ToList();
                    if (survivors.Count > 0)
                    {
                        var extra = doc.Delete(survivors);
                        if (extra != null) deleted.AddRange(extra.Select(e => (long)e.Value));
                    }
                }

                var result = new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["system_id"] = sysId,
                    ["name"] = name,
                    ["domain"] = MepDomains.ToWire(driver.Kind),
                    ["members_before"] = memberIds,
                    // Revit's own answer, not our assumption about what
                    // "totally dependent" covers.
                    ["deleted_ids"] = deleted.Distinct().ToList(),
                    ["deleted_member_count"] = memberIds.Count(id => deleted.Contains(id)),
                    ["members_kept"] = memberIds.Where(id => !deleted.Contains(id)).ToList(),
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

        // ─── membership cores (used by Layer 1 and Layer 2) ─────────────

        /// <summary>No transaction, throws. Refusing to empty a system is the
        /// driver's job — RevitAPI.xml on MEPSystem.Remove: "It is forbidden to
        /// remove all terminal elements from system".</summary>
        internal static void AddMembersCore(Document doc, MEPSystem sys, IReadOnlyList<ElementId> ids)
            => MepSystemDrivers.ForSystem(sys).AddMembers(doc, sys, ids);

        internal static void RemoveMembersCore(Document doc, MEPSystem sys, IReadOnlyList<ElementId> ids)
            => MepSystemDrivers.ForSystem(sys).RemoveMembers(doc, sys, ids);

        // ─── shared ─────────────────────────────────────────────────────

        /// <summary>Driver from ``domain``, or from ``system_type`` when the
        /// caller gave only that.</summary>
        internal static IMepSystemDriver ResolveDriver(JsonElement args)
        {
            var domainWord = ArgsHelp.GetString(args, "domain");
            if (!string.IsNullOrWhiteSpace(domainWord))
            {
                var kind = MepDomains.Parse(domainWord);
                if (kind == MepDomainKind.Unknown)
                    throw new ArgumentException(
                        $"unknown domain '{domainWord}' — use one of: "
                        + string.Join(", ", MepDomains.AcceptedWords));
                return MepSystemDrivers.Resolve(kind);
            }

            var bySystemType = MepSystemDrivers.ResolveBySystemType(ArgsHelp.GetString(args, "system_type"));
            if (bySystemType != null) return bySystemType;

            throw new ArgumentException(
                "`domain` is required (or a `system_type` this build recognises) — one of: "
                + string.Join(", ", MepDomains.AcceptedWords));
        }

        /// <summary>Capability-driven pre-flight. Returns null when the request
        /// may proceed, otherwise a failure row that says what to do about it —
        /// Revit's own ArgumentException for these cases names nothing
        /// actionable.</summary>
        private static Dictionary<string, object?>? PreCheck(
            Document doc, IMepSystemDriver driver, IReadOnlyList<ElementId> members)
        {
            var caps = driver.Capabilities;

            if (caps.ValidMemberCategories != null && caps.ValidMemberCategories.Count > 0)
            {
                var wrong = members
                    .Select(id => doc.GetElement(id))
                    .Where(el => el?.Category != null
                              && !caps.ValidMemberCategories.Contains((BuiltInCategory)el.Category.Id.Value))
                    .Select(el => el!.Id.Value)
                    .ToList();
                if (wrong.Count > 0)
                    return MepTx.Failure(
                        $"element(s) {string.Join(", ", wrong)} are not a valid member category for a "
                        + $"{MepDomains.ToWire(driver.Kind)} system "
                        + $"(allowed: {string.Join(", ", caps.ValidMemberCategories)})",
                        "invalid_member_category");
            }

            if (caps.RequiresFreeConnector)
            {
                var domain = MepDomains.ToConnectorDomain(driver.Kind);
                var blocked = members
                    .Select(id => doc.GetElement(id))
                    .Where(el => el != null && MepConnectors.FreeConnectors(el, domain).Count == 0)
                    .Select(el => el!.Id.Value)
                    .ToList();
                if (blocked.Count > 0)
                    return MepTx.Failure(
                        $"element(s) {string.Join(", ", blocked)} have no free "
                        + $"{MepDomains.ToWire(driver.Kind)} connector — disconnect_elements first, "
                        + "or check get_element_mep_info",
                        "no_free_connector");
            }

            if (caps.RequiresPhysicalConnection && members.Count > 1)
            {
                var elements = members.Select(doc.GetElement).Where(e => e != null).ToList()!;
                var walk = MepConnectors.Traverse(elements!, maxDepth: 50, maxNodes: PreflightNodeCap);

                var graph = new MepGraph { Truncated = walk.Truncated, NodeCap = PreflightNodeCap };
                foreach (var el in walk.Nodes) graph.Nodes.Add(new GraphNode { Id = el.Id.Value });
                foreach (var e in walk.Edges) graph.Edges.Add(new GraphEdge(e.A, e.B));

                var wanted = members.Select(m => m.Value).Select(v => (long)v).ToList();
                var comp = MepGraphChecks.ComponentsOf(graph, wanted);
                var groups = comp.GroupBy(kv => kv.Value).ToList();

                if (groups.Count > 1)
                {
                    var reps = groups.Select(g => g.Min(kv => kv.Key)).OrderBy(x => x).ToList();
                    return MepTx.Failure(
                        $"these elements form {groups.Count} disconnected networks, but a "
                        + $"{MepDomains.ToWire(driver.Kind)} system requires one — "
                        + $"one element per network: {string.Join(", ", reps)}. "
                        + "Join them first (connect_elements, or route_duct/route_pipe).",
                        "not_physically_connected");
                }
            }

            return null;
        }

        internal static List<long> MemberIds(MEPSystem sys)
        {
            var ids = new List<long>();
            try
            {
                var set = sys.Elements;
                if (set != null) foreach (Element e in set) ids.Add(e.Id.Value);
            }
            catch { }
            return ids;
        }

        /// <summary>Common system row. The driver merges its own fields in, so
        /// nothing discipline-specific is spelled out here.</summary>
        internal static Dictionary<string, object?> Describe(MEPSystem sys, IMepSystemDriver driver)
        {
            var row = new Dictionary<string, object?>
            {
                ["system_id"] = sys.Id.Value,
                ["name"] = MepElementInfo.SafeName(sys),
                ["domain"] = MepDomains.ToWire(driver.Kind),
                ["member_ids"] = MemberIds(sys),
                ["base_equipment_id"] = SafeBaseEquipmentId(sys),
                ["is_empty"] = SafeIsEmpty(sys),
            };
            try { driver.Describe(sys, row); }
            catch (Exception ex) { row["describe_error"] = ex.Message; }
            return row;
        }

        internal static long? SafeBaseEquipmentId(MEPSystem sys)
        {
            try { return sys.BaseEquipment?.Id.Value; }
            catch { return null; }
        }

        internal static bool? SafeIsEmpty(MEPSystem sys)
        {
            try { return sys.IsEmpty; }
            catch { return null; }
        }

        /// <summary>Turn the one Revit failure worth branching on into a code.
        /// Both NewMechanicalSystem and NewPipingSystem throw
        /// InvalidOperationException "when calling this function outside of the
        /// Autodesk Revit MEP product" — that is a licensing fact, not a
        /// modelling mistake, and the agent must not retry it.</summary>
        private static string? ClassifyError(Exception ex)
            => ex.Message.IndexOf("Revit MEP", StringComparison.OrdinalIgnoreCase) >= 0
                ? "not_revit_mep"
                : null;
    }
}
