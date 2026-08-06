// get_system_by_id, list_systems_in_project — Layer 0 reads. No Transaction.
//
// Both go through IMepSystemDriver.Describe for the discipline-specific
// fields, so a circuit's rating and a duct system's flow arrive on the same
// row shape without this file knowing either exists.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools.Mep
{
    internal static class MepSystemInspect
    {
        private const int DefaultLimit = 200;

        // ─── get_system_by_id ───────────────────────────────────────────

        public static Dictionary<string, object?> GetSystemById(Document doc, JsonElement args)
        {
            var sysId = ArgsHelp.GetLong(args, "system_id")
                ?? throw new ArgumentException("missing system_id");

            var el = doc.GetElement(ElemIds.From(sysId));
            if (el == null) return MepTx.Failure($"element {sysId} not found");
            if (el is not MEPSystem sys)
                return MepTx.Failure(
                    $"element {sysId} is a {el.Category?.Name ?? el.GetType().Name}, not an MEP system "
                    + "(use list_systems_in_project to find one)");

            var driver = MepSystemDrivers.ForSystem(sys);
            var row = MepSystemTools.Describe(sys, driver);
            row["ok"] = true;

            // Members with enough context to act on, not just ids — the whole
            // point of asking for a system is usually to do something to its
            // members next.
            var members = new List<Dictionary<string, object?>>();
            foreach (var id in MepSystemTools.MemberIds(sys))
            {
                var m = doc.GetElement(ElemIds.From(id));
                if (m == null)
                {
                    members.Add(new Dictionary<string, object?>
                    {
                        ["element_id"] = id,
                        ["missing"] = true,   // surfaces as orphan_member in is_system_graph_valid
                    });
                    continue;
                }
                members.Add(new Dictionary<string, object?>
                {
                    ["element_id"] = id,
                    ["name"] = MepElementInfo.SafeName(m),
                    ["category"] = m.Category?.Name,
                    ["kind"] = MepElementInfo.ClassifyKind(m),
                    ["level"] = doc.GetElement(m.LevelId) is Level lv ? lv.Name : null,
                });
            }
            row["members"] = members;
            row["member_count"] = members.Count;
            row["physical_networks"] = SafeNetworkCount(sys);
            return row;
        }

        // ─── list_systems_in_project ────────────────────────────────────

        public static Dictionary<string, object?> ListSystemsInProject(Document doc, JsonElement args)
        {
            var domainWord = ArgsHelp.GetString(args, "domain");
            var limit = (int)(ArgsHelp.GetLong(args, "limit") ?? DefaultLimit);

            var drivers = new List<IMepSystemDriver>();
            if (string.IsNullOrWhiteSpace(domainWord))
            {
                drivers.AddRange(MepSystemDrivers.All);
            }
            else
            {
                var kind = MepDomains.Parse(domainWord);
                if (kind == MepDomainKind.Unknown)
                    return MepTx.Failure(
                        $"unknown domain '{domainWord}' — use one of: "
                        + string.Join(", ", MepDomains.AcceptedWords));
                var d = MepSystemDrivers.TryResolve(kind);
                if (d == null)
                    return MepTx.Blocked("domain_not_supported",
                        $"'{MepDomains.ToWire(kind)}' systems are not implemented in this build yet — "
                        + $"available: {string.Join(", ", MepSystemDrivers.RegisteredKinds.Select(MepDomains.ToWire))}");
                drivers.Add(d);
            }

            var rows = new List<Dictionary<string, object?>>();
            var byDomain = new Dictionary<string, int>();
            var total = 0;

            foreach (var driver in drivers)
            {
                var wire = MepDomains.ToWire(driver.Kind);
                foreach (var el in driver.Collect(doc))
                {
                    if (el is not MEPSystem sys) continue;
                    total++;
                    byDomain[wire] = byDomain.TryGetValue(wire, out var n) ? n + 1 : 1;
                    if (rows.Count >= limit) continue;
                    rows.Add(MepSystemTools.Describe(sys, driver));
                }
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                // by_domain is computed over the WHOLE match set; systems is
                // capped. Same contract as find_mep_elements' by_level —
                // quote the aggregate, never tally the rows.
                ["total"] = total,
                ["by_domain"] = byDomain,
                ["truncated"] = total > rows.Count,
                ["count"] = rows.Count,
                ["systems"] = rows,
            };
        }

        internal static int? SafeNetworkCount(MEPSystem sys)
        {
            try { return sys.GetPhysicalNetworksNumber(); }
            catch { return null; }
        }
    }
}
