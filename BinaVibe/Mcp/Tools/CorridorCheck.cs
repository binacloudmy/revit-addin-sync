// check_corridor — clearance along a straight segment, host doc + loaded
// links. READ-ONLY. No Transaction is ever opened here: this is the look
// before create_duct/create_pipe/create_conduit leap, and firing the pane's
// Ya/Tidak card on a call that changes nothing would degrade the gate on the
// calls that do (same rationale as suggest_socket_points).
//
// Primitive, not a verdict (query-geometry relational-primitives rule): every
// element whose AABB comes within clearance_mm of the segment is returned with
// its actual distance_mm and along_mm; the agent compares. AABB distances are
// conservative for rotated elements — a reported near-miss may be clear in
// truth, but a reported CLEAR corridor is clear at AABB fidelity.
//
// All exact distances are computed in HOST space mm (link hit boxes are
// re-mapped through the link transform first) so every number shares one
// frame. Math lives in GeomMm (Revit-free, tested); link resolution in
// LinkGeom (shared with QueryGeometry's clashes).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal static class CorridorCheck
    {
        private const int DefaultMaxHits = 50;
        private const int HardCap = 100;

        // Plain words -> BuiltInCategory, named once here so the model never
        // guesses OST_* names (the OST_Pipes hallucination precedent).
        // Internal: Electrical.RoutePlanner reuses the same vocabulary so a
        // route probe and a corridor check never disagree on what a word means.
        internal static readonly Dictionary<string, BuiltInCategory> Cats = new()
        {
            ["wall"] = BuiltInCategory.OST_Walls,
            ["column"] = BuiltInCategory.OST_Columns,
            ["structural_column"] = BuiltInCategory.OST_StructuralColumns,
            ["structural_framing"] = BuiltInCategory.OST_StructuralFraming,
            ["floor"] = BuiltInCategory.OST_Floors,
            ["duct"] = BuiltInCategory.OST_DuctCurves,
            ["pipe"] = BuiltInCategory.OST_PipeCurves,
            ["cable_tray"] = BuiltInCategory.OST_CableTray,
            ["conduit"] = BuiltInCategory.OST_Conduit,
        };

        public static Dictionary<string, object?> Run(Document doc, JsonElement args)
        {
            var start = ArgsHelp.GetPointMm(args, "start_mm")
                ?? throw new ArgumentException("start_mm required ([x,y,z] mm)");
            var end = ArgsHelp.GetPointMm(args, "end_mm")
                ?? throw new ArgumentException("end_mm required ([x,y,z] mm)");
            var clearFt = ArgsHelp.GetLengthMm(args, "clearance_mm")
                ?? throw new ArgumentException("clearance_mm required");
            double clearMm = clearFt * LinkGeom.MmPerFoot;
            bool includeLinks = ArgsHelp.GetBool(args, "include_links") ?? true;
            int maxHits = (int)(ArgsHelp.GetLong(args, "max_hits") ?? DefaultMaxHits);
            maxHits = Math.Min(Math.Max(1, maxHits), HardCap);

            var wanted = ResolveCategories(args, out var unknown);
            if (unknown.Count > 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"unknown categories: {string.Join(", ", unknown)}",
                    ["supported"] = Cats.Keys.Cast<object>().ToList(),
                };

            var aMm = new Pt3Mm(start.X * LinkGeom.MmPerFoot, start.Y * LinkGeom.MmPerFoot, start.Z * LinkGeom.MmPerFoot);
            var bMm = new Pt3Mm(end.X * LinkGeom.MmPerFoot, end.Y * LinkGeom.MmPerFoot, end.Z * LinkGeom.MmPerFoot);
            double lenMm = Math.Sqrt(
                (bMm.X - aMm.X) * (bMm.X - aMm.X) +
                (bMm.Y - aMm.Y) * (bMm.Y - aMm.Y) +
                (bMm.Z - aMm.Z) * (bMm.Z - aMm.Z));

            var scan = ScanSegment(doc, aMm, bMm, clearMm, wanted, includeLinks, maxHits);

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["length_mm"] = Math.Round(lenMm, 0),
                ["clearance_mm"] = Math.Round(clearMm, 0),
                ["obstructions"] = scan.Rows
                    .OrderBy(r => (double)r["along_mm"]!)
                    .Cast<object?>().ToList(),
                ["truncated"] = scan.Truncated,
            };
            if (scan.LinksUnloaded.Count > 0)
                result["links_unloaded"] = scan.LinksUnloaded.Cast<object?>().ToList();
            return result;
        }

        internal sealed class ScanResult
        {
            public List<Dictionary<string, object?>> Rows = new();
            public bool Truncated;
            public List<string> LinksUnloaded = new();
        }

        /// <summary>The per-segment scan body, extracted verbatim so
        /// Electrical.RoutePlanner can probe each routed leg with exactly the
        /// same arithmetic as check_corridor. Host space mm in, wire-shaped
        /// obstruction rows out.</summary>
        internal static ScanResult ScanSegment(
            Document doc, Pt3Mm aMm, Pt3Mm bMm, double clearMm,
            IReadOnlyList<BuiltInCategory> wanted, bool includeLinks, int maxHits)
        {
            var corridorMm = GeomMm.CorridorAabb(aMm, bMm, clearMm);
            var catIds = wanted.Select(c => (long)c).ToArray();
            var res = new ScanResult();
            var rows = res.Rows;

            // ── host document ──
            var outline = new Outline(
                new XYZ(corridorMm.Min.X / LinkGeom.MmPerFoot, corridorMm.Min.Y / LinkGeom.MmPerFoot, corridorMm.Min.Z / LinkGeom.MmPerFoot),
                new XYZ(corridorMm.Max.X / LinkGeom.MmPerFoot, corridorMm.Max.Y / LinkGeom.MmPerFoot, corridorMm.Max.Z / LinkGeom.MmPerFoot));
            var hostHits = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(outline))
                .Where(e => e.Category != null && catIds.Contains(e.Category.Id.Value));
            foreach (var e in hostHits)
            {
                if (rows.Count >= maxHits) { res.Truncated = true; break; }
                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                var (dist, along) = GeomMm.SegmentToBoxDistance(aMm, bMm, LinkGeom.ToMmBox(bb));
                if (dist > clearMm) continue;
                rows.Add(ObstructionRow(dist, along, e.Category?.Name, source: "host",
                                        id: e.Id.Value, linkId: null, linkName: null));
            }

            // ── loaded links ──
            if (includeLinks)
            {
                foreach (var lc in LinkGeom.Build(doc, out res.LinksUnloaded))
                {
                    if (rows.Count >= maxHits) { res.Truncated = true; break; }
                    var lFilter = new BoundingBoxIntersectsFilter(
                        LinkGeom.ToLinkOutline(corridorMm, lc.ToLink));
                    var linkHits = new FilteredElementCollector(lc.Doc)
                        .WhereElementIsNotElementType()
                        .WherePasses(lFilter)
                        .Where(e => e.Category != null && catIds.Contains(e.Category.Id.Value));
                    foreach (var e in linkHits)
                    {
                        if (rows.Count >= maxHits) { res.Truncated = true; break; }
                        var bb = e.get_BoundingBox(null);
                        if (bb == null) continue;
                        // Exact distance in HOST space: hit box re-mapped first.
                        var (dist, along) = GeomMm.SegmentToBoxDistance(
                            aMm, bMm, LinkGeom.ToMmBox(bb, lc.ToHost));
                        if (dist > clearMm) continue;
                        rows.Add(ObstructionRow(dist, along, e.Category?.Name, source: "link",
                                                id: null, linkId: lc.LinkId, linkName: lc.Name,
                                                linkElementId: e.Id.Value));
                    }
                }
            }
            return res;
        }

        private static List<BuiltInCategory> ResolveCategories(JsonElement args, out List<string> unknown)
        {
            unknown = new List<string>();
            var names = new List<string>();
            if (args.TryGetProperty("categories", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) names.Add(e.GetString()!.Trim().ToLowerInvariant());

            if (names.Count == 0) return Cats.Values.ToList();   // default: everything we know

            var found = new List<BuiltInCategory>();
            foreach (var n in names)
            {
                if (Cats.TryGetValue(n, out var bic)) found.Add(bic);
                else unknown.Add(n);
            }
            return found;
        }

        private static Dictionary<string, object?> ObstructionRow(
            double distMm, double alongMm, string? category, string source,
            long? id, long? linkId, string? linkName, long? linkElementId = null)
        {
            var row = new Dictionary<string, object?>
            {
                ["source"] = source,
                ["category"] = category,
                ["distance_mm"] = Math.Round(distMm, 0),
                ["along_mm"] = Math.Round(alongMm, 0),
            };
            if (id.HasValue) row["id"] = id.Value;
            if (linkId.HasValue)
            {
                row["link_id"] = linkId.Value;
                row["link_name"] = linkName;
                row["link_element_id"] = linkElementId;
            }
            return row;
        }
    }
}
