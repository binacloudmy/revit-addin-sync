// ModelContext — get_model_context: the geometry a grounded write lands on.
//
// The backend refuses to let the model invent a stair position or a roof
// boundary. To compute them itself it needs facts only Revit has: what the
// levels actually are, and where the building's exterior actually runs. This
// tool is that reading, stamped with the version counters that let a cached
// copy expire (DocVersion).
//
// Cheap by contract, like get_geometry_digest: levels, one perimeter trace
// over exterior wall centrelines, bounding boxes. No solids, no intersections.
// It is called once per turn and must never be the slow part of one.
//
// KNOWN LIMIT (v1): rooms are reported with a bounding box, not a boundary
// polygon. Nothing in the current rule set needs the polygon, and the only
// loop-extraction code that handles Revit's "outer loop is not index 0" rule
// correctly is private to SocketCandidates — copying it here would be a second
// home for geometry that is hard to get right. When a rule needs real room
// polygons, that extraction gets shared properly rather than duplicated.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace BinaVibe.Mcp.Tools
{
    internal static class ModelContextTool
    {
        private const double FtToMm = 304.8;

        /// <summary>A trace smaller than this is noise, not a building — a
        /// couple of stray detail walls in an otherwise empty model. Refusing
        /// beats handing the backend an "envelope" a stair trivially fits
        /// inside.</summary>
        private const double MinEnvelopeMm2 = 4_000_000.0;   // 2m x 2m

        private const int MaxElements = 200;
        private const int MaxRooms = 200;

        public static Dictionary<string, object?> Run(Document doc, JsonElement args)
        {
            var scope = (ArgsHelp.GetString(args, "scope") ?? "document").Trim().ToLowerInvariant();
            var levelName = ArgsHelp.GetString(args, "level");

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["snapshot_id"] = "ctx:" + Guid.NewGuid().ToString("N"),
                ["scope"] = scope,
                ["units"] = "mm",
                ["levels"] = Levels(doc),
            };
            DocVersion.Stamp(result, doc);

            var level = FindLevel(doc, levelName);
            if (levelName != null && level == null)
                throw new ArgumentException(
                    $"level '{levelName}' not found — known levels: "
                    + string.Join(", ", new FilteredElementCollector(doc)
                        .OfClass(typeof(Level)).Cast<Level>().Select(l => l.Name)));

            var envelope = Envelope(doc, out var envelopeSource);
            result["envelope_mm"] = envelope;
            result["envelope_source"] = envelopeSource;

            var bounds = Bounds(doc, level);
            if (bounds != null) result["bounds_mm"] = bounds;

            result["rooms"] = Rooms(doc, level);

            var elements = Elements(doc, scope, args, level);
            if (elements != null) result["elements"] = elements;

            return result;
        }

        // ─── levels ─────────────────────────────────────────────────────
        /// <summary>Every level with its elevation in mm, plus the names of the
        /// levels immediately above and below. The backend derives a stair's
        /// rise from these — never from a floor-to-floor it assumed.</summary>
        private static List<Dictionary<string, object?>> Levels(Document doc)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level))
                .Cast<Level>().OrderBy(l => l.Elevation).ToList();
            var rows = new List<Dictionary<string, object?>>();
            for (int i = 0; i < levels.Count; i++)
            {
                rows.Add(new Dictionary<string, object?>
                {
                    ["id"] = levels[i].Id.Value,
                    ["name"] = levels[i].Name,
                    ["elevation_mm"] = Math.Round(levels[i].Elevation * FtToMm, 1),
                    ["below"] = i > 0 ? levels[i - 1].Name : null,
                    ["above"] = i < levels.Count - 1 ? levels[i + 1].Name : null,
                });
            }
            return rows;
        }

        // ─── envelope ───────────────────────────────────────────────────
        /// <summary>The building's exterior footprint, and where it came from.
        ///
        /// A copilot-built model already stores its own footprint in the design
        /// spec, which is exact and free — no trace can beat the number the
        /// building was built from. Everything else gets a perimeter walk over
        /// exterior wall centrelines (EnvelopeTrace). Null when neither works:
        /// the backend then refuses the write and says so, which is the whole
        /// point of not guessing.</summary>
        private static List<double[]>? Envelope(Document doc, out string source)
        {
            source = "none";

            var stored = FromStoredSpec(doc);
            if (stored != null && EnvelopeTrace.Area(stored) >= MinEnvelopeMm2)
            {
                source = "design_spec";
                return stored;
            }

            var segments = new List<PlanSegment>();
            foreach (var w in new FilteredElementCollector(doc)
                         .OfClass(typeof(Wall)).Cast<Wall>())
            {
                // WallFunction.Exterior is the same discriminator query_geometry
                // already uses for its `nearest:exterior_wall` primitive.
                WallType? wt = null;
                try { wt = w.WallType; } catch { }
                if (wt == null || wt.Function != WallFunction.Exterior) continue;
                if (!(w.Location is LocationCurve lc) || lc.Curve == null) continue;

                Curve c;
                try { c = lc.Curve; } catch { continue; }
                XYZ a, b;
                try { a = c.GetEndPoint(0); b = c.GetEndPoint(1); } catch { continue; }
                segments.Add(new PlanSegment(a.X * FtToMm, a.Y * FtToMm,
                                             b.X * FtToMm, b.Y * FtToMm));
            }

            var traced = EnvelopeTrace.Outer(segments);
            if (traced != null && EnvelopeTrace.Area(traced) >= MinEnvelopeMm2)
            {
                source = "exterior_walls";
                return traced;
            }
            if (segments.Count > 0) source = "exterior_walls_open";  // found walls, no closed ring
            return null;
        }

        /// <summary>``args.footprint_mm`` off the stored design spec, when this
        /// document holds one.</summary>
        private static List<double[]>? FromStoredSpec(Document doc)
        {
            try
            {
                var json = DesignSpec.LoadJson(doc);
                if (string.IsNullOrWhiteSpace(json)) return null;
                using var parsed = JsonDocument.Parse(json);
                if (!parsed.RootElement.TryGetProperty("args", out var a)
                    || !a.TryGetProperty("footprint_mm", out var fp)
                    || fp.ValueKind != JsonValueKind.Array) return null;

                var ring = new List<double[]>();
                foreach (var p in fp.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Array) continue;
                    var xy = p.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    if (xy.Length >= 2) ring.Add(new[] { xy[0], xy[1] });
                }
                return ring.Count >= 3 ? ring : null;
            }
            catch { return null; }
        }

        // ─── bounds / rooms / elements ──────────────────────────────────
        private static Dictionary<string, object?>? Bounds(Document doc, Level? level)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var any = false;

            foreach (var el in new FilteredElementCollector(doc)
                         .WhereElementIsNotElementType())
            {
                if (level != null && el.LevelId != level.Id) continue;
                BoundingBoxXYZ? bb = null;
                try { bb = el.get_BoundingBox(null); } catch { }
                if (bb == null) continue;
                any = true;
                minX = Math.Min(minX, bb.Min.X); maxX = Math.Max(maxX, bb.Max.X);
                minY = Math.Min(minY, bb.Min.Y); maxY = Math.Max(maxY, bb.Max.Y);
                minZ = Math.Min(minZ, bb.Min.Z); maxZ = Math.Max(maxZ, bb.Max.Z);
            }
            if (!any) return null;
            return new Dictionary<string, object?>
            {
                ["x"] = new[] { Mm(minX), Mm(maxX) },
                ["y"] = new[] { Mm(minY), Mm(maxY) },
                ["z"] = new[] { Mm(minZ), Mm(maxZ) },
            };
        }

        private static List<Dictionary<string, object?>> Rooms(Document doc, Level? level)
        {
            var rows = new List<Dictionary<string, object?>>();
            foreach (var r in new FilteredElementCollector(doc)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType().Cast<SpatialElement>())
            {
                if (rows.Count >= MaxRooms) break;
                if (level != null && r.LevelId != level.Id) continue;
                if (!(r is Room room) || room.Area <= 0) continue;   // unplaced
                rows.Add(new Dictionary<string, object?>
                {
                    ["id"] = room.Id.Value,
                    ["name"] = room.Name,
                    ["level"] = doc.GetElement(room.LevelId)?.Name,
                    ["area_m2"] = Math.Round(room.Area * 0.09290304, 2),
                    ["bbox_mm"] = Box(room),
                });
            }
            return rows;
        }

        private static List<Dictionary<string, object?>>? Elements(
            Document doc, string scope, JsonElement args, Level? level)
        {
            if (scope == "document" || scope == "level") return null;   // too broad to be useful

            var wanted = new List<Element>();
            if (scope == "host" || scope == "room")
            {
                var id = ArgsHelp.GetLong(args, "element_id") ?? ArgsHelp.GetLong(args, "room_id");
                if (id == null)
                    throw new ArgumentException(
                        $"scope '{scope}' needs element_id (or room_id) — pass the id you want context around");
                var el = doc.GetElement(ElemIds.From(id.Value))
                    ?? throw new ArgumentException($"element {id} not found in this model");
                wanted.Add(el);
            }
            else if (scope == "bbox" || scope == "point")
            {
                var (min, max) = Region(args, scope);
                var outline = new Outline(min, max);
                wanted.AddRange(new FilteredElementCollector(doc)
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .WhereElementIsNotElementType()
                    .Take(MaxElements));
            }
            else
            {
                throw new ArgumentException(
                    $"unknown scope '{scope}' — use document | level | room | bbox | point | host");
            }

            return wanted.Where(e => level == null || e.LevelId == level.Id)
                .Take(MaxElements)
                .Select(e => new Dictionary<string, object?>
                {
                    ["id"] = e.Id.Value,
                    ["category"] = e.Category?.Name,
                    ["name"] = e.Name,
                    ["level"] = e.LevelId != ElementId.InvalidElementId
                        ? doc.GetElement(e.LevelId)?.Name : null,
                    ["bbox_mm"] = Box(e),
                })
                .ToList();
        }

        private static (XYZ Min, XYZ Max) Region(JsonElement args, string scope)
        {
            if (scope == "bbox")
            {
                var pts = ArgsHelp.GetPointListMm(args, "bbox_mm");
                if (pts == null || pts.Count < 2)
                    throw new ArgumentException(
                        "scope 'bbox' needs bbox_mm as [[xmin,ymin],[xmax,ymax]] in mm");
                var a = pts[0]; var b = pts[1];
                return (new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), -1e4),
                        new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), 1e4));
            }
            var c = ArgsHelp.GetPointMm(args, "point_mm")
                ?? throw new ArgumentException("scope 'point' needs point_mm [x,y]");
            var rMm = ArgsHelp.GetDouble(args, "radius_mm")
                ?? throw new ArgumentException("scope 'point' needs radius_mm");
            var r = rMm / FtToMm;
            return (new XYZ(c.X - r, c.Y - r, -1e4), new XYZ(c.X + r, c.Y + r, 1e4));
        }

        private static double[]? Box(Element el)
        {
            BoundingBoxXYZ? bb = null;
            try { bb = el.get_BoundingBox(null); } catch { }
            if (bb == null) return null;
            return new[] { Mm(bb.Min.X), Mm(bb.Min.Y), Mm(bb.Min.Z),
                           Mm(bb.Max.X), Mm(bb.Max.Y), Mm(bb.Max.Z) };
        }

        private static Level? FindLevel(Document doc, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static double Mm(double ft) => Math.Round(ft * FtToMm, 1);
    }
}
