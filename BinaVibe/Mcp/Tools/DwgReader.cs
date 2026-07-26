// DwgReader — the ONE extractor behind every DWG/DXF question.
//
// Two sources, one code path: CAD linked/imported in the open model, and a
// standalone DWG the drafter attached in the pane (which DwgScratchCache links
// into a scratch document first). Both arrive here as an ImportInstance, so the
// summary shape is identical and the agent needs no source-specific rules.
//
// Traversal follows the rules already proven in the walls_from_cad_layer recipe
// (app/knowledge/revit_recipes/walls_from_cad_layer.md): recurse
// GeometryInstance, treat PolyLine separately from Curve (a PolyLine is NOT a
// Curve — `as Curve` silently drops whole wall runs), skip sub-50mm slivers,
// and count InvalidElementId (block) geometry instead of dropping it.
//
// FIDELITY CEILING — read this before "fixing" a missing field. Revit exposes a
// CAD import as GEOMETRY. DWG text/MTEXT strings, dimension VALUES, block NAMES
// and the drawing's unit header are NOT reachable through the Revit API at any
// version. They are reported in the summary's `unavailable` list so the agent
// says "not readable" instead of inventing annotations. Everything file-format
// specific is confined to this class: a licensed DXF extractor can later fill
// those fields by emitting the same dwg.summary/1 dictionary, with no change to
// the tools, the prompt, or the pane.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    public static class DwgReader
    {
        public const string Schema = "dwg.summary/1";

        private const double MmPerFoot = 304.8;
        // ≈50mm — same sliver threshold the CAD recipe uses. Closed polylines
        // repeat their first point; without this the duplicate lands as a
        // zero-length segment.
        private const double MinSegmentFt = 0.164;
        private const int MaxLayers = 25;
        // GeometryInstance nesting: 0 = the import wrapper, 1 = a block, 2 = a
        // nested block. Deeper than that is pathological CAD; stop and say so
        // rather than walking forever.
        private const int MaxDepth = 3;

        /// <summary>What Revit cannot read out of a DWG, whatever the drawing
        /// contains. Shipped in every summary so the agent can never claim
        /// otherwise. Units join this list when the import gives us none.</summary>
        private static List<object> UnavailableFields(bool unitsKnown)
        {
            var list = new List<object> { "text", "dimensions", "block_names" };
            if (!unitsKnown) list.Add("units");
            return list;
        }

        // ─── summary (dwg.summary/1) ────────────────────────────────────

        public static Dictionary<string, object?> Summarize(
            Document doc, ImportInstance imp, string dwgRef, string source)
        {
            var st = new WalkState(doc);

            // Seed from the import category's subcategories FIRST: that lists
            // every DWG layer, including ones whose geometry is hidden or empty
            // (a layer the drafter asks about but sees nothing on is still a
            // real answer). The geometry walk then fills in the counts.
            foreach (var name in LayerNames(imp))
                st.Bucket(name);

            Walk(imp.get_Geometry(new Options()), 0, st);

            var ordered = st.Layers
                .OrderByDescending(kv => kv.Value.Entities)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var layers = ordered.Take(MaxLayers).Select(kv => (object)new Dictionary<string, object?>
            {
                ["name"] = kv.Key,
                ["entities"] = kv.Value.Entities,
                ["kinds"] = kv.Value.Kinds.OrderBy(k => k, StringComparer.Ordinal).ToList<object>(),
            }).ToList();

            var units = UnitsOf(doc, imp);

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["schema"] = Schema,
                ["dwg_ref"] = dwgRef,
                ["name"] = NameOf(imp),
                ["source"] = source,
                ["path"] = PathOf(doc, imp),
                ["units_detected"] = units,
                ["extents_mm"] = Extents(imp),
                ["entity_totals"] = new Dictionary<string, object?>
                {
                    ["lines"] = st.Lines,
                    ["arcs"] = st.Arcs,
                    ["polylines"] = st.Polylines,
                    ["points"] = st.Points,
                    ["meshes"] = st.Meshes,
                    ["solids"] = st.Solids,
                    ["block_instances"] = st.Blocks.Count,
                },
                ["layers"] = layers,
                ["layers_truncated"] = Math.Max(0, ordered.Count - layers.Count),
                ["unavailable"] = UnavailableFields(units != null),
            };
        }

        // ─── one layer's segments ───────────────────────────────────────

        /// <summary>Segments on ONE layer, polylines already expanded. Layer is
        /// matched loosely (substring, case-insensitive) because CAD importers
        /// suffix layer names — an exact match would return nothing for a name
        /// the user read off their own drawing.</summary>
        public static Dictionary<string, object?> LayerDetail(
            Document doc, ImportInstance imp, string dwgRef, string layer, int limit)
        {
            if (string.IsNullOrWhiteSpace(layer))
                throw new InvalidOperationException("layer is required");
            limit = Math.Max(1, Math.Min(limit <= 0 ? 200 : limit, 2000));

            var st = new WalkState(doc)
            {
                LayerFilter = layer,
                CollectSegments = true,
                SegmentCap = limit,
            };
            Walk(imp.get_Geometry(new Options()), 0, st);

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["dwg_ref"] = dwgRef,
                ["layer"] = layer,
                ["matched_layers"] = st.MatchedLayers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList<object>(),
                ["segments"] = st.Segments,
                ["count"] = st.Segments.Count,
                ["total_found"] = st.SegmentsFound,
                ["truncated"] = st.SegmentsFound > st.Segments.Count,
                ["unavailable"] = UnavailableFields(UnitsOf(doc, imp) != null),
            };
        }

        // ─── block instances ────────────────────────────────────────────

        /// <summary>Block INSTANCES with their placement. Block names are not
        /// exposed by the Revit API — the caller gets position and content, and
        /// the `unavailable` list says the name is missing.</summary>
        public static Dictionary<string, object?> BlockInstances(
            Document doc, ImportInstance imp, string dwgRef, int limit)
        {
            limit = Math.Max(1, Math.Min(limit <= 0 ? 100 : limit, 1000));
            var st = new WalkState(doc);
            Walk(imp.get_Geometry(new Options()), 0, st);

            var blocks = st.Blocks.Take(limit).ToList<object>();
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["dwg_ref"] = dwgRef,
                ["blocks"] = blocks,
                ["count"] = blocks.Count,
                ["total_found"] = st.Blocks.Count,
                ["truncated"] = st.Blocks.Count > blocks.Count,
                ["unavailable"] = UnavailableFields(UnitsOf(doc, imp) != null),
            };
        }

        // ─── identity helpers (shared with list_cad_links) ──────────────

        public static string NameOf(ImportInstance imp) =>
            imp.Category?.Name ?? imp.Name ?? "(unnamed CAD)";

        public static string PathOf(Document doc, ImportInstance imp)
        {
            try
            {
                // Linked CAD carries an external file reference; an IMPORTED
                // DWG lives inside the .rvt and has none — its name is all
                // there is, so say nothing rather than invent a path.
                var type = doc.GetElement(imp.GetTypeId());
                if (type == null || !type.IsExternalFileReference()) return "";
                var path = type.GetExternalFileReference()?.GetAbsolutePath();
                return path == null ? "" : ModelPathUtils.ConvertModelPathToUserVisiblePath(path);
            }
            catch { return ""; }
        }

        public static List<string> LayerNames(ImportInstance imp)
        {
            var names = new List<string>();
            try
            {
                var cat = imp.Category;
                if (cat == null) return names;
                foreach (Category sub in cat.SubCategories)
                    if (!string.IsNullOrEmpty(sub.Name)) names.Add(sub.Name);
            }
            catch { /* category-less import — the geometry walk still finds layers */ }
            return names;
        }

        public static Dictionary<string, object?>? Extents(ImportInstance imp)
        {
            var bb = imp.get_BoundingBox(null);
            if (bb == null) return null;
            return new Dictionary<string, object?>
            {
                ["min"] = Xyz(bb.Min),
                ["max"] = Xyz(bb.Max),
            };
        }

        /// <summary>The DWG's own unit header is not exposed by the Revit API.
        /// Returns null so the caller adds "units" to `unavailable` — a guessed
        /// unit is worse than an admitted gap on a drawing you scale from.</summary>
        private static string? UnitsOf(Document doc, ImportInstance imp) => null;

        private static List<object> Xyz(XYZ p) => new()
        {
            Math.Round(p.X * MmPerFoot, 0),
            Math.Round(p.Y * MmPerFoot, 0),
            Math.Round(p.Z * MmPerFoot, 0),
        };

        // ─── traversal ──────────────────────────────────────────────────

        private sealed class Bucket
        {
            public int Entities;
            public readonly HashSet<string> Kinds = new(StringComparer.Ordinal);
        }

        private sealed class WalkState
        {
            public readonly Document Doc;
            public readonly Dictionary<string, Bucket> Layers = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<object> Blocks = new();
            public readonly HashSet<string> MatchedLayers = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<object> Segments = new();

            public int Lines, Arcs, Polylines, Points, Meshes, Solids;
            public int SegmentsFound;

            public string? LayerFilter;
            public bool CollectSegments;
            public int SegmentCap;

            public WalkState(Document doc) { Doc = doc; }

            public Bucket Bucket(string layer)
            {
                if (!Layers.TryGetValue(layer, out var b)) Layers[layer] = b = new Bucket();
                return b;
            }
        }

        private static void Walk(GeometryElement? ge, int depth, WalkState st)
        {
            if (ge == null || depth >= MaxDepth) return;

            foreach (var obj in ge)
            {
                if (obj is GeometryInstance gi)
                {
                    // depth 0 is the import wrapper itself; anything nested
                    // inside it is a block reference.
                    if (depth > 0) st.Blocks.Add(BlockRecord(gi, st));
                    Walk(gi.GetInstanceGeometry(), depth + 1, st);
                    continue;
                }

                var layer = LayerOf(st.Doc, obj);
                var kind = KindOf(obj);
                if (kind == null) continue;

                var bucket = st.Bucket(layer);
                bucket.Entities++;
                bucket.Kinds.Add(kind);

                switch (kind)
                {
                    case "line": st.Lines++; break;
                    case "arc": st.Arcs++; break;
                    case "polyline": st.Polylines++; break;
                    case "point": st.Points++; break;
                    case "mesh": st.Meshes++; break;
                    case "solid": st.Solids++; break;
                }

                if (st.LayerFilter != null) CollectSegments(obj, layer, st);
            }
        }

        private static void CollectSegments(GeometryObject obj, string layer, WalkState st)
        {
            if (layer.IndexOf(st.LayerFilter!, StringComparison.OrdinalIgnoreCase) < 0) return;
            st.MatchedLayers.Add(layer);
            if (!st.CollectSegments) return;

            if (obj is PolyLine pl)
            {
                // A rectangle or a connected wall run arrives as ONE PolyLine —
                // expand it, or the caller sees "1 entity" for 4 walls.
                var pts = pl.GetCoordinates();
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    if (pts[i].DistanceTo(pts[i + 1]) <= MinSegmentFt) continue;
                    AddSegment(st, "line", pts[i], pts[i + 1], pts[i].DistanceTo(pts[i + 1]));
                }
            }
            else if (obj is Curve c && c.Length > MinSegmentFt)
            {
                AddSegment(st, obj is Arc ? "arc" : "line",
                    c.GetEndPoint(0), c.GetEndPoint(1), c.Length);
            }
        }

        private static void AddSegment(WalkState st, string kind, XYZ a, XYZ b, double lengthFt)
        {
            st.SegmentsFound++;
            if (st.Segments.Count >= st.SegmentCap) return;   // capped, but keep counting
            st.Segments.Add(new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["start_mm"] = Xyz(a),
                ["end_mm"] = Xyz(b),
                ["length_mm"] = Math.Round(lengthFt * MmPerFoot, 0),
            });
        }

        private static Dictionary<string, object?> BlockRecord(GeometryInstance gi, WalkState st)
        {
            var t = gi.Transform;
            var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int entities = 0;
            try
            {
                foreach (var o in gi.GetInstanceGeometry())
                {
                    if (o is GeometryInstance) continue;   // nested block, counted on its own
                    if (KindOf(o) == null) continue;
                    entities++;
                    layers.Add(LayerOf(st.Doc, o));
                }
            }
            catch { /* unreadable block body — report the placement anyway */ }

            return new Dictionary<string, object?>
            {
                ["insertion_mm"] = Xyz(t.Origin),
                ["rotation_deg"] = Math.Round(Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180.0 / Math.PI, 1),
                ["entities"] = entities,
                ["layers"] = layers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList<object>(),
            };
        }

        private static string LayerOf(Document doc, GeometryObject obj)
        {
            try
            {
                // Block geometry can carry InvalidElementId — bucket it under a
                // visible name instead of dropping it (recipe gotcha).
                var gs = doc.GetElement(obj.GraphicsStyleId) as GraphicsStyle;
                var name = gs?.GraphicsStyleCategory?.Name ?? gs?.Name ?? "";
                return name.Length > 0 ? name : "(no layer)";
            }
            catch { return "(no layer)"; }
        }

        private static string? KindOf(GeometryObject obj) => obj switch
        {
            PolyLine => "polyline",
            Arc => "arc",
            Curve => "line",
            Point => "point",
            Mesh => "mesh",
            Solid s => s.Faces.Size > 0 ? "solid" : null,
            _ => null,
        };
    }
}
