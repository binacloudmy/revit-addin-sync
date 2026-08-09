// DesignSpec — vibe modeling: the building as a description that owns its elements.
//
// Why this exists (UAT 2026-08-06): the copilot placed elements one at a time
// and needed 27 tool calls and 92 seconds to produce a box, and the next prompt
// started from nothing — it had no idea what it had just built.
//
// Here the copilot writes a SPEC instead: footprint, levels, walls, roof, room
// program, structure. The generator builds the whole design in one transaction,
// and the spec is stored INSIDE the .rvt with the ids of everything it created.
// A later prompt edits one field and only the affected roles are rebuilt —
// "tukar bumbung jadi hip" touches one element, not a building.
//
// Ownership model: the spec carries a role -> element-ids map. Elements the
// drafter drew themselves are never in that map and are never touched. Per the
// product decision of 2026-08-06, spec-owned elements are MANAGED — a rebuild
// overwrites them and REPORTS what it replaced; one transaction means one
// Ctrl+Z puts everything back.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class DesignSpec
    {
        private const double FT = 304.8;
        private const double TOL = 1e-6;
        private const string SchemaName = "BinaBuildSpec";
        private const string FieldName = "spec_json";
        // Stable GUID: changing it orphans every spec already saved in a file.
        private static readonly Guid SchemaGuid = new Guid("7b2f1c48-9a3e-4d5b-8c61-2e7a4f0d9b13");

        // ─── storage ────────────────────────────────────────────────────

        private static Schema GetOrCreateSchema()
        {
            var existing = Schema.Lookup(SchemaGuid);
            if (existing != null) return existing;
            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName(SchemaName);
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField(FieldName, typeof(string));
            return b.Finish();
        }

        /// <summary>The DataStorage element holding the spec, or null.</summary>
        private static DataStorage? FindStore(Document doc)
        {
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return null;
            return new FilteredElementCollector(doc).OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(ds => ds.GetEntity(schema)?.IsValid() == true);
        }

        /// <summary>Read the stored spec as a JSON string ("" when none).
        /// Must run outside a transaction — it only reads.</summary>
        public static string LoadJson(Document doc)
        {
            var store = FindStore(doc);
            if (store == null) return "";
            var schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return "";
            var ent = store.GetEntity(schema);
            return ent != null && ent.IsValid() ? ent.Get<string>(FieldName) ?? "" : "";
        }

        /// <summary>Write the spec. Caller owns the transaction.</summary>
        public static void SaveJson(Document doc, string json)
        {
            var schema = GetOrCreateSchema();
            var store = FindStore(doc) ?? DataStorage.Create(doc);
            var ent = new Entity(schema);
            ent.Set(FieldName, json ?? "");
            store.SetEntity(ent);
        }

        // ─── spec helpers ───────────────────────────────────────────────

        private static JsonElement? Obj(JsonElement args, string name) =>
            args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var v)
            && v.ValueKind != JsonValueKind.Null ? v : (JsonElement?)null;

        private static string? Str(JsonElement? o, string name)
        {
            if (o == null || o.Value.ValueKind != JsonValueKind.Object) return null;
            return o.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }

        private static double? Num(JsonElement? o, string name)
        {
            if (o == null || o.Value.ValueKind != JsonValueKind.Object) return null;
            return o.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : (double?)null;
        }

        private static List<XYZ> Footprint(JsonElement args)
        {
            var pts = ArgsHelp.GetPointListMm(args, "footprint_mm");
            if (pts.Count < 3)
                throw new ArgumentException("footprint_mm needs at least 3 [x,y] points (mm)");
            // Drop a repeated closing point — the loops below close themselves.
            if (pts.Count > 3 && pts[0].DistanceTo(pts[pts.Count - 1]) < 1e-6)
                pts.RemoveAt(pts.Count - 1);
            return pts;
        }

        private static WallType FindWallType(Document doc, string? name, bool interior)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Where(t => t.Kind == WallKind.Basic).ToList();
            if (all.Count == 0) throw new InvalidOperationException("no basic wall types in this project");
            if (!string.IsNullOrWhiteSpace(name))
                return all.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"wall type '{name}' not found (use list_wall_types)");
            // No name given: interior partitions want the thinnest type, exterior
            // the thickest. Guessing badly here is visible immediately, so bias
            // toward the obvious rather than the first match.
            var ordered = all.OrderBy(t => t.Width).ToList();
            return interior ? ordered.First() : ordered.Last();
        }

        /// <summary>A floor type, falling back rather than failing the build.
        ///
        /// The model picked "Wall-Fnd_300Con_Footing" as a slab type on
        /// 2026-08-06 — a wall type name, from the wrong list. Refusing to build
        /// an entire house over one mis-chosen type name is the wrong trade:
        /// build it with a sensible default and let the reply say which type was
        /// actually used.</summary>
        private static FloorType FindFloorType(Document doc, string? name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
            if (all.Count == 0) throw new InvalidOperationException("no floor types in this project");

            // FloorType covers BOTH floors and structural foundation slabs, and
            // foundation types often sort first. Creating with one produces an
            // OST_StructuralFoundation element that is not a Floor — measured
            // 2026-08-06: element 326154 built fine and the digest counted zero
            // floors, because it genuinely was not one. Keep only real floors.
            var floorsOnly = all.Where(t =>
                t.Category != null &&
                t.Category.Id.Value == (long)BuiltInCategory.OST_Floors).ToList();
            var pool = floorsOnly.Count > 0 ? floorsOnly : all;

            if (string.IsNullOrWhiteSpace(name)) return pool.First();
            return pool.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? pool.First();
        }

        /// <summary>A roof type NewFootPrintRoof will actually accept.
        ///
        /// "Sloped Glazing" is a RoofType and sorts first in most templates, so
        /// taking the first match hands the API a type it rejects with a bare
        /// ArgumentNullException — "Value cannot be null", which reads like a
        /// bad argument and sends the caller off retrying the boundary. Only
        /// Basic Roof families are footprint-capable.</summary>
        private static RoofType? FindRoofType(Document doc, string? name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(RoofType))
                .OfCategory(BuiltInCategory.OST_Roofs).Cast<RoofType>().ToList();
            var basic = all.Where(t => (t.FamilyName ?? "").IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0)
                           .ToList();
            var pool = basic.Count > 0 ? basic : all;
            if (string.IsNullOrWhiteSpace(name)) return pool.FirstOrDefault();
            var hit = pool.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            // Named a type that exists but is not footprint-capable, or does not
            // exist at all: fall back rather than failing the whole build, and
            // let the digest report what was actually made.
            return pool.FirstOrDefault();
        }

        private static FamilySymbol? FindSymbol(Document doc, BuiltInCategory bic, string? name)
        {
            var all = new FilteredElementCollector(doc).WhereElementIsElementType()
                .OfCategory(bic).Cast<FamilySymbol>().ToList();
            if (all.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(name)) return all.First();
            return all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"type '{name}' not found in {bic}");
        }

        private static CurveArray Loop(IList<XYZ> pts, double z)
        {
            var arr = new CurveArray();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = new XYZ(pts[i].X, pts[i].Y, z);
                var n = pts[(i + 1) % pts.Count];
                var b = new XYZ(n.X, n.Y, z);
                if (a.DistanceTo(b) > 1e-6) arr.Append(Line.CreateBound(a, b));
            }
            return arr;
        }

        private static CurveLoop CurveLoopOf(IList<XYZ> pts, double z)
        {
            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = new XYZ(pts[i].X, pts[i].Y, z);
                var n = pts[(i + 1) % pts.Count];
                var b = new XYZ(n.X, n.Y, z);
                if (a.DistanceTo(b) > 1e-6) loop.Append(Line.CreateBound(a, b));
            }
            return loop;
        }

        // ─── build_design ───────────────────────────────────────────────

        /// <summary>Build a whole design from a spec, in ONE transaction.
        ///
        /// Regenerations are minimised, not eliminated: family symbols must be
        /// activated and the document regenerated before instances can be
        /// placed, and rooms cannot find their boundaries until the walls
        /// enclosing them have been regenerated. Both are batched — activate
        /// every symbol once up front, regenerate once before rooms.</summary>
        public static Dictionary<string, object?> BuildDesign(Document doc, JsonElement args,
                                                              UIDocument? uidoc = null)
        {
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var footprint = Footprint(args);

            var levelsSpec = Obj(args, "levels");
            var count = (int)(Num(levelsSpec, "count") ?? 1);
            if (count < 1) throw new ArgumentException("levels.count must be at least 1");
            var f2f = (Num(levelsSpec, "floor_to_floor_mm") ?? 3000) / FT;
            var prefix = Str(levelsSpec, "prefix") ?? "Level ";

            var wallsSpec = Obj(args, "walls");
            var intType = FindWallType(doc, Str(wallsSpec, "interior_type"), interior: true);

            // Facade system. A curtain wall is just a wall whose type is
            // curtain-kind, so the generator picks the type and then drives the
            // grid and mullions on it — the same tools that already existed and
            // that build_design never called.
            var facadeSpec = Obj(args, "facade");
            var facadeKind = (Str(facadeSpec, "kind") ?? "punched").ToLowerInvariant();
            WallType? curtainType = null;
            if (facadeKind == "curtain" || facadeKind == "curtain_wall" || facadeKind == "glazed")
            {
                var named = Str(facadeSpec, "wall_type") ?? Str(wallsSpec, "exterior_type");
                curtainType = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                    .Cast<WallType>().Where(t => t.Kind == WallKind.Curtain)
                    .OrderByDescending(t => named != null
                        && string.Equals(t.Name, named, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
            }
            var extType = curtainType ?? FindWallType(doc, Str(wallsSpec, "exterior_type"),
                                                      interior: false);

            var floorsSpec = Obj(args, "floors");
            var slabType = FindFloorType(doc, Str(floorsSpec, "slab_type"));

            var roofSpec = Obj(args, "roof");
            var roofKind = (Str(roofSpec, "kind") ?? "flat").ToLowerInvariant();
            var roofPitch = Num(roofSpec, "pitch_deg");
            var roofType = FindRoofType(doc, Str(roofSpec, "type_name"));

            var openSpec = Obj(args, "openings");
            var doorSym = FindSymbol(doc, BuiltInCategory.OST_Doors, Str(openSpec, "door_type"));
            var winSym = FindSymbol(doc, BuiltInCategory.OST_Windows, Str(openSpec, "window_type"));
            var winSpacing = (Num(openSpec, "window_spacing_mm") ?? 3000) / FT;
            var sill = (Num(openSpec, "sill_mm") ?? 900) / FT;

            // A roof needs a plan view active. Shared with create_roof via
            // ViewGuard so the two cannot drift apart again — that divergence
            // is exactly how create_roof shipped without this guard and lost a
            // bungalow its roof. Must happen BEFORE the transaction opens.
            using var viewSwitch = ViewGuard.EnsurePlanView(doc, uidoc);

            var owns = new Dictionary<string, List<long>>();
            void Own(string role, long id)
            {
                if (!owns.TryGetValue(role, out var list)) owns[role] = list = new List<long>();
                list.Add(id);
            }

            // Overlap guard for openings. Two windows placed within a window's
            // width of each other on the same wall interpenetrate — Revit allows
            // it, and it reads as a glazing error in every view. Reported from a
            // live model 2026-08-06. Every placement path below checks this, so
            // no future rule can reintroduce it.
            var placedOnWall = new Dictionary<long, List<XYZ>>();
            var winWidth = winSym?.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble()
                           ?? (1200.0 / FT);
            bool TooClose(long wallId, XYZ p, double minGap)
            {
                if (!placedOnWall.TryGetValue(wallId, out var pts)) return false;
                foreach (var q in pts)
                    if (Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Y - q.Y, 2)) < minGap)
                        return true;
                return false;
            }
            void NotePlaced(long wallId, XYZ p)
            {
                if (!placedOnWall.TryGetValue(wallId, out var pts))
                    placedOnWall[wallId] = pts = new List<XYZ>();
                pts.Add(p);
            }

            using var tx = new Transaction(doc, "BINA: build design");
            TxGuard.StartSwallowing(tx);
            try
            {
                // Symbols first: activation forces a regeneration, so do them all
                // once here rather than one per placement.
                foreach (var sym in new[] { doorSym, winSym }.Where(s => s != null && !s!.IsActive))
                    sym!.Activate();
                doc.Regenerate();

                // Levels ------------------------------------------------------
                var levels = new List<Level>();
                var baseLevel = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).FirstOrDefault()
                    ?? throw new InvalidOperationException("project has no levels");
                levels.Add(baseLevel);
                for (int i = 1; i <= count; i++)   // one extra: the roof bearing level
                {
                    var elev = baseLevel.Elevation + f2f * i;
                    var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                        .FirstOrDefault(l => Math.Abs(l.Elevation - elev) < 1e-6);
                    if (lvl == null)
                    {
                        lvl = Level.Create(doc, elev);
                        try { lvl.Name = i == count ? "Roof" : $"{prefix}{i + 1}"; } catch { }
                        Own("level", lvl.Id.Value);
                    }
                    levels.Add(lvl);
                }

                // Slabs + perimeter walls, per storey, per volume ---------------
                // A building is parts. No `volumes` means one part named "main"
                // from footprint_mm, so every spec already stored keeps working.
                var vols = Volumes.Parse(args, footprint, f2f);
                for (int s = 0; s < count; s++)
                {
                    var lvl = levels[s];
                    foreach (var vol in vols)
                    {
                        // A part shorter than the storey (a porch at 2700 under a
                        // 3600 house) only exists on the ground floor.
                        if (s > 0 && vol.HeightFt > TOL && vol.HeightFt < f2f - TOL) continue;

                        var slab = Floor.Create(doc, new List<CurveLoop> { CurveLoopOf(vol.Outline, 0) },
                                                slabType.Id, lvl.Id);
                        Own("slab", slab.Id.Value);

                        // Edges another volume already walls are skipped, so a
                        // porch flush against the house gets three walls and the
                        // shared wall is never built twice.
                        var wallHeight = vol.HeightFt > TOL ? vol.HeightFt : f2f;
                        foreach (var (a, b) in Volumes.UnsharedEdges(vol, vols))
                        {
                            var line = Line.CreateBound(new XYZ(a.X, a.Y, lvl.Elevation),
                                                        new XYZ(b.X, b.Y, lvl.Elevation));
                            var w = Wall.Create(doc, line, extType.Id, lvl.Id, wallHeight, 0, false, false);
                            // Top-constrain full-height walls so a later
                            // floor-to-floor change moves them; a deliberately
                            // short part keeps its own height.
                            if (Math.Abs(wallHeight - f2f) < TOL)
                                w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(levels[s + 1].Id);
                            Own(vol.Role == "main" ? "perimeter_wall" : $"{vol.Role}_wall",
                                w.Id.Value);
                        }
                    }
                }

                // Roof ---------------------------------------------------------
                // A roof failure must NOT throw away the rest of the building.
                // NewFootPrintRoof is the most fragile call in this method, and
                // losing a correct house because the roof would not take is a
                // far worse outcome than a house that is honestly reported as
                // roofless — which is exactly what the digest will say.
                // The roof is built AFTER this transaction commits — see below.
                // RoofBuilder tries several strategies and owns a transaction per
                // attempt, which cannot nest inside this one, and a roof failure
                // must never cost the building that is already correct.

                // Curtain grid + mullions, set on the TYPE so one write dresses
                // every panel of every floor. Punched windows are skipped for a
                // glazed facade — a curtain wall does not take them.
                if (curtainType != null)
                {
                    const int MaximumSpacing = 3;      // CurtainGridLayout
                    var gv = (Num(facadeSpec, "grid_vertical_mm") ?? 1500) / FT;
                    var gh = (Num(facadeSpec, "grid_horizontal_mm") ?? 3000) / FT;
                    curtainType.get_Parameter(BuiltInParameter.SPACING_LAYOUT_VERT)?.Set(MaximumSpacing);
                    curtainType.get_Parameter(BuiltInParameter.SPACING_LENGTH_VERT)?.Set(gv);
                    curtainType.get_Parameter(BuiltInParameter.SPACING_LAYOUT_HORIZ)?.Set(MaximumSpacing);
                    curtainType.get_Parameter(BuiltInParameter.SPACING_LENGTH_HORIZ)?.Set(gh);

                    var mullName = Str(facadeSpec, "mullion_type");
                    var mull = new FilteredElementCollector(doc).OfClass(typeof(MullionType))
                        .Cast<MullionType>()
                        .OrderByDescending(m => mullName != null
                            && string.Equals(m.Name, mullName, StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault();
                    if (mull != null)
                    {
                        // Both groups: Revit gives vertical and horizontal
                        // mullions parameters that share a display name, so a
                        // generic set-parameter call can only ever reach one.
                        foreach (var bip in new[]
                                 { BuiltInParameter.AUTO_MULLION_INTERIOR_VERT,
                                   BuiltInParameter.AUTO_MULLION_BORDER1_VERT,
                                   BuiltInParameter.AUTO_MULLION_BORDER2_VERT,
                                   BuiltInParameter.AUTO_MULLION_INTERIOR_HORIZ,
                                   BuiltInParameter.AUTO_MULLION_BORDER1_HORIZ,
                                   BuiltInParameter.AUTO_MULLION_BORDER2_HORIZ })
                        {
                            var p = curtainType.get_Parameter(bip);
                            if (p != null && !p.IsReadOnly) p.Set(mull.Id);
                        }
                    }
                }

                // Openings on the ground floor ---------------------------------
                var groundWalls = owns.TryGetValue("perimeter_wall", out var pw)
                    ? pw.Take(footprint.Count).ToList() : new List<long>();
                if (curtainType != null) { winSym = null; }
                if (doorSym != null && groundWalls.Count > 0)
                {
                    var host = doc.GetElement(ElemIds.From(groundWalls[0])) as Wall;
                    if (host?.Location is LocationCurve lc)
                    {
                        var p = lc.Curve.Evaluate(0.5, true);
                        var fi = doc.Create.NewFamilyInstance(
                            new XYZ(p.X, p.Y, levels[0].Elevation), doorSym, host, levels[0],
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        Own("door", fi.Id.Value);
                    }
                }
                // Blanket window spacing, and it STAYS. When the layout was
                // solved, each room gets its own window on the wall it actually
                // touches (below) — running both would double-glaze every room
                // and put windows in bathrooms that already have one.
                //
                // The 2026-08-07 plan called for deleting this alongside the
                // equal-strip fallback. Reading it first says otherwise: a SHELL
                // build carries no program by design (a 20-storey tower asks for
                // an envelope, not a floor plan), and for a shell an even pitch
                // per wall is the correct answer, not a degradation. The real
                // defect is narrower — every wall uses the same pitch whatever
                // is behind it, so a bathroom gets a living room's rhythm — and
                // that only applies where rooms exist, which is the branch below.
                // Deleting this would have broken every tower to fix a house.
                var haveSolvedRooms = Obj(args, "rooms") is { ValueKind: JsonValueKind.Array } rr
                                      && rr.GetArrayLength() > 0;
                if (winSym != null && !haveSolvedRooms)
                {
                    foreach (var wid in groundWalls.Skip(1))
                    {
                        var host = doc.GetElement(ElemIds.From(wid)) as Wall;
                        if (host?.Location is not LocationCurve wlc) continue;
                        var len = wlc.Curve.Length;
                        // Never ask for more windows than the wall can hold at the
                        // window's own width — a spacing smaller than the family
                        // is how openings end up interpenetrating.
                        var pitch = Math.Max(winSpacing, winWidth * 1.2);
                        var n = Math.Max(1, (int)Math.Floor(len / Math.Max(pitch, 1e-6)));
                        for (int k = 0; k < n; k++)
                        {
                            var t = (k + 0.5) / n;
                            var p = wlc.Curve.Evaluate(t, true);
                            var wpt = new XYZ(p.X, p.Y, levels[0].Elevation);
                            if (TooClose(wid, wpt, winWidth * 1.2)) continue;
                            var fi = doc.Create.NewFamilyInstance(
                                wpt, winSym, host, levels[0],
                                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                            fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sill);
                            Own("window", fi.Id.Value);
                            NotePlaced(wid, wpt);
                        }
                    }
                }

                // Structure ----------------------------------------------------
                var structSpec = Obj(args, "structure");
                if (structSpec != null)
                {
                    var grid = Obj(structSpec.Value, "grid");
                    var xs = (Num(grid, "x_spacing_mm") ?? 6000) / FT;
                    var ys = (Num(grid, "y_spacing_mm") ?? 8000) / FT;
                    var xb = (int)(Num(grid, "x_bays") ?? 0);
                    var yb = (int)(Num(grid, "y_bays") ?? 0);
                    var minX = footprint.Min(p => p.X);
                    var minY = footprint.Min(p => p.Y);
                    var maxY = footprint.Max(p => p.Y);
                    var maxX = footprint.Max(p => p.X);
                    for (int i = 0; i <= xb; i++)
                    {
                        var x = minX + i * xs;
                        var g = Grid.Create(doc, Line.CreateBound(new XYZ(x, minY, 0), new XYZ(x, maxY, 0)));
                        Own("grid", g.Id.Value);
                    }
                    for (int j = 0; j <= yb; j++)
                    {
                        var y = minY + j * ys;
                        var g = Grid.Create(doc, Line.CreateBound(new XYZ(minX, y, 0), new XYZ(maxX, y, 0)));
                        Own("grid", g.Id.Value);
                    }
                    var colSym = FindSymbol(doc, BuiltInCategory.OST_StructuralColumns,
                                            Str(Obj(structSpec.Value, "columns"), "type_name"));
                    if (colSym != null && xb > 0 && yb > 0)
                    {
                        if (!colSym.IsActive) { colSym.Activate(); doc.Regenerate(); }
                        for (int i = 0; i <= xb; i++)
                            for (int j = 0; j <= yb; j++)
                            {
                                var pt = new XYZ(minX + i * xs, minY + j * ys, levels[0].Elevation);
                                var col = doc.Create.NewFamilyInstance(
                                    pt, colSym, levels[0],
                                    Autodesk.Revit.DB.Structure.StructuralType.Column);
                                Own("column", col.Id.Value);
                            }
                    }
                }

                // Interior. When the backend has SOLVED the layout it sends
                // explicit room rectangles; build those. Equal strips are the
                // fallback only, because a diagram of "N spaces" is not a floor
                // plan and looks like one at a glance.
                //
                // Rooms cannot find their boundaries until the partitions are
                // regenerated, so that regeneration is required, not incidental.
                var lvl0 = levels[0];
                var solvedRooms = Obj(args, "rooms");
                if (solvedRooms != null && solvedRooms.Value.ValueKind == JsonValueKind.Array
                    && solvedRooms.Value.GetArrayLength() > 0)
                {
                    var fMinX = footprint.Min(p => p.X); var fMaxX = footprint.Max(p => p.X);
                    var fMinY = footprint.Min(p => p.Y); var fMaxY = footprint.Max(p => p.Y);
                    const double EPS = 1.0 / FT;      // 1mm

                    var rects = new List<(string Name, double X, double Y, double W, double H)>();
                    foreach (var r in solvedRooms.Value.EnumerateArray())
                    {
                        var nm = r.TryGetProperty("name", out var nv) ? nv.GetString() ?? "Room" : "Room";
                        double G(string k) => r.TryGetProperty(k, out var v)
                            && v.ValueKind == JsonValueKind.Number ? v.GetDouble() / FT : 0;
                        rects.Add((nm, G("x_mm"), G("y_mm"), G("w_mm"), G("h_mm")));
                    }

                    // One wall per unique interior edge. Room rectangles share
                    // edges, so without de-duplication every partition between
                    // two rooms would be built twice — overlapping walls, which
                    // the digest would (correctly) flag.
                    var built = new Dictionary<string, Wall>();
                    string Key(XYZ a, XYZ b)
                    {
                        var (p, q) = (a.X < b.X || (Math.Abs(a.X - b.X) < EPS && a.Y < b.Y)) ? (a, b) : (b, a);
                        return $"{Math.Round(p.X * FT)},{Math.Round(p.Y * FT)}|" +
                               $"{Math.Round(q.X * FT)},{Math.Round(q.Y * FT)}";
                    }
                    bool OnPerimeter(XYZ a, XYZ b) =>
                        (Math.Abs(a.X - b.X) < EPS && (Math.Abs(a.X - fMinX) < EPS || Math.Abs(a.X - fMaxX) < EPS))
                     || (Math.Abs(a.Y - b.Y) < EPS && (Math.Abs(a.Y - fMinY) < EPS || Math.Abs(a.Y - fMaxY) < EPS));

                    foreach (var rc in rects)
                    {
                        var c0 = new XYZ(rc.X, rc.Y, lvl0.Elevation);
                        var c1 = new XYZ(rc.X + rc.W, rc.Y, lvl0.Elevation);
                        var c2 = new XYZ(rc.X + rc.W, rc.Y + rc.H, lvl0.Elevation);
                        var c3 = new XYZ(rc.X, rc.Y + rc.H, lvl0.Elevation);
                        foreach (var (a, b) in new[] { (c0, c1), (c1, c2), (c2, c3), (c3, c0) })
                        {
                            if (OnPerimeter(a, b)) continue;        // the exterior wall already exists
                            var k = Key(a, b);
                            if (built.ContainsKey(k)) continue;
                            if (a.DistanceTo(b) < EPS) continue;
                            var w = Wall.Create(doc, Line.CreateBound(a, b), intType.Id,
                                                lvl0.Id, f2f, 0, false, false);
                            w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(levels[1].Id);
                            Own("partition", w.Id.Value);
                            built[k] = w;
                        }
                    }

                    doc.Regenerate();          // rooms need bounded walls to exist

                    foreach (var rc in rects)
                    {
                        var cx = rc.X + rc.W / 2; var cy = rc.Y + rc.H / 2;
                        var room = doc.Create.NewRoom(lvl0, new UV(cx, cy));
                        if (room != null)
                        {
                            try { room.Name = rc.Name; } catch { }
                            Own("room", room.Id.Value);
                        }

                        // A door per room, in the partition the room shares with
                        // the corridor — a room you cannot walk into is not a
                        // room. Pick the interior edge nearest the plan centre,
                        // which for a corridor layout is the corridor side.
                        if (doorSym != null)
                        {
                            var planCy = (fMinY + fMaxY) / 2;
                            var edges = new[]
                            {
                                (A: new XYZ(rc.X, rc.Y, lvl0.Elevation),
                                 B: new XYZ(rc.X + rc.W, rc.Y, lvl0.Elevation), Mid: rc.Y),
                                (A: new XYZ(rc.X, rc.Y + rc.H, lvl0.Elevation),
                                 B: new XYZ(rc.X + rc.W, rc.Y + rc.H, lvl0.Elevation), Mid: rc.Y + rc.H),
                            }.OrderBy(e => Math.Abs(e.Mid - planCy)).ToList();
                            foreach (var e in edges)
                            {
                                if (!built.TryGetValue(Key(e.A, e.B), out var host)) continue;
                                try
                                {
                                    var d = doc.Create.NewFamilyInstance(
                                        new XYZ(cx, e.Mid, lvl0.Elevation), doorSym, host, lvl0,
                                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                                    Own("door", d.Id.Value);
                                }
                                catch { /* wall too short for this door type */ }
                                break;
                            }
                        }

                        // A window per room on the exterior wall it touches, so
                        // openings follow the plan instead of a blind spacing
                        // rule that gives a bathroom the same treatment as a
                        // living room.
                        if (winSym != null && groundWalls.Count == footprint.Count)
                        {
                            for (int i = 0; i < footprint.Count; i++)
                            {
                                var a = footprint[i];
                                var b = footprint[(i + 1) % footprint.Count];
                                var horizontal = Math.Abs(a.Y - b.Y) < EPS;
                                var touches = horizontal
                                    ? (Math.Abs(rc.Y - a.Y) < EPS || Math.Abs(rc.Y + rc.H - a.Y) < EPS)
                                    : (Math.Abs(rc.X - a.X) < EPS || Math.Abs(rc.X + rc.W - a.X) < EPS);
                                if (!touches) continue;
                                if (doc.GetElement(ElemIds.From(groundWalls[i])) is not Wall hostW) continue;
                                var pt = horizontal ? new XYZ(cx, a.Y, lvl0.Elevation)
                                                    : new XYZ(a.X, cy, lvl0.Elevation);
                                if (TooClose(groundWalls[i], pt, winWidth * 1.2)) break;
                                try
                                {
                                    var fi = doc.Create.NewFamilyInstance(
                                        pt, winSym, hostW, lvl0,
                                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                                    fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sill);
                                    Own("window", fi.Id.Value);
                                    NotePlaced(groundWalls[i], pt);
                                }
                                catch { }
                                break;
                            }
                        }
                    }
                }
                else
                {
                    // NO FALLBACK. A program with no solved rectangles used to be
                    // sliced into equal strips, and on 2026-08-06 that turned an
                    // 18 m frontage into ten 1.8 m cells with a door in each —
                    // reported as a finished house, because nothing said no.
                    //
                    // The layout is solved server-side now (design_preflight) and
                    // a program that cannot be solved is refused there, before
                    // this call is made. If rectangles are still missing, the
                    // spec is wrong and building a diagram of "N spaces" is worse
                    // than building nothing: it looks like an answer.
                    var program = Obj(args, "program");
                    if (program != null && program.Value.ValueKind == JsonValueKind.Array
                        && program.Value.GetArrayLength() > 1)
                        throw new InvalidOperationException(
                            "this build carries a room program but no solved room rectangles. "
                            + "Refusing to slice the footprint into equal strips — that is a "
                            + "diagram of room COUNT, not a floor plan. Call "
                            + "suggest_floor_layout and pass its rooms, or drop the program "
                            + "to build a shell.");
                }

                // Persist the spec with what it owns, so the NEXT prompt can edit
                // this design instead of starting blind.
                var specId = Guid.NewGuid().ToString("N").Substring(0, 12);
                var stored = new Dictionary<string, object?>
                {
                    ["spec_id"] = specId,
                    ["version"] = 1,
                    ["args"] = JsonSerializer.Deserialize<object>(args.GetRawText()),
                    ["owns"] = owns.ToDictionary(k => k.Key, v => v.Value),
                };
                SaveJson(doc, JsonSerializer.Serialize(stored));

                TxGuard.CommitOrThrow(tx);

                // ── roof, after the commit ────────────────────────────────
                // Every strategy RoofBuilder knows, including the extrusion that
                // works where footprint roofs are refused. Failure here leaves a
                // correct building plus an honest warning, which the digest also
                // reports as floor area with nothing over it.
                string? roofWarning = null;
                string? roofStrategy = null;
                if (roofType != null)
                {
                    // The eaves. A roof boundary pushed outward is the single
                    // strongest cue that a roof is a roof — without projecting
                    // eaves a pitched plane reads as a lid dropped on a box, which
                    // is what a drafter called "still ugly" on 2026-08-08.
                    var overhangFt = (Num(roofSpec, "overhang_mm") ?? 0) / FT;

                    // One roof per volume it covers. `covers` names the volumes
                    // under the main roof; anything omitted takes its own, which
                    // is how a porch gets a lower roof of its own rather than
                    // forcing one plane over an L-shaped building.
                    var covers = roofSpec != null && roofSpec.Value.TryGetProperty("covers", out var cv)
                                 && cv.ValueKind == JsonValueKind.Array
                        ? cv.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : null;
                    var roofed = vols.Where(v => covers == null || covers.Contains(v.Role)).ToList();
                    if (roofed.Count == 0) roofed = vols;

                    var roofIds = new List<long>();
                    RoofBuilder.Result? roofRes = null;
                    foreach (var vol in roofed)
                    {
                        var boundary = Volumes.WithEaves(vol.Outline, overhangFt);
                        // A part shorter than the storey carries its roof at its
                        // own height, not the top of the house.
                        var roofLevel = vol.HeightFt > TOL && vol.HeightFt < f2f - TOL
                            ? levels[0] : levels[count];
                        var res = RoofBuilder.Build(doc, boundary, roofLevel, roofType,
                                                    roofKind == "flat" ? null : roofPitch,
                                                    null, roofKind);
                        if (res.Ok) roofIds.Add(res.Id!.Value);
                        roofRes ??= res;
                        if (res.Ok && roofRes?.Ok != true) roofRes = res;
                    }

                    if (roofIds.Count > 0 && roofIds.Count < roofed.Count)
                        roofWarning = $"only {roofIds.Count} of {roofed.Count} volumes got a "
                            + "roof — part of this building is open to the sky. Say so; do not "
                            + "describe it as covered.";

                    if (roofIds.Count > 0)
                    {
                        owns["roof"] = roofIds;
                        roofStrategy = roofRes?.Strategy;
                        using var txR = new Transaction(doc, "BINA: record roof");
                        TxGuard.StartSwallowing(txR);
                        SaveJson(doc, JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["spec_id"] = specId, ["version"] = 1,
                            ["args"] = JsonSerializer.Deserialize<object>(args.GetRawText()),
                            ["owns"] = owns,
                        }));
                        TxGuard.CommitOrThrow(txR);
                    }
                    else
                    {
                        roofWarning = "the roof could not be created. Attempts: "
                            + string.Join(" | ", roofRes?.Attempts ?? new List<string>())
                            + $". Type '{roofType.Name}' (family '{roofType.FamilyName}'). "
                            + "Everything else was built — tell the drafter the roof is missing "
                            + "and offer Architecture > Roof by Footprint. Do NOT describe this "
                            + "as a finished shell.";
                    }
                }
                else
                {
                    roofWarning = "this project has no footprint-capable roof type, so no roof "
                        + "was created. Everything else was built.";
                }

                t0.Stop();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["spec_id"] = specId,
                    ["created"] = owns.ToDictionary(k => k.Key, v => (object?)v.Value.Count),
                    ["new_ids"] = owns.SelectMany(k => k.Value).ToList(),
                    ["roof_strategy"] = roofStrategy,
                    ["warning"] = roofWarning,
                    ["elapsed_ms"] = t0.ElapsedMilliseconds,
                    ["digest"] = Inspectors.GetGeometryDigest(doc, args),
                };
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                throw;
            }
            finally
            {
                // view restored by viewSwitch's dispose
            }
        }

        // ─── get_design ─────────────────────────────────────────────────

        public static Dictionary<string, object?> GetDesign(Document doc, JsonElement args)
        {
            var json = LoadJson(doc);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["exists"] = false,
                    ["note"] = "no design spec in this model — it was built by hand, "
                             + "or by an older copilot that did not record one",
                };
            var spec = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
            return new Dictionary<string, object?>
            {
                ["ok"] = true, ["exists"] = true, ["spec"] = spec,
                ["digest"] = Inspectors.GetGeometryDigest(doc, args),
            };
        }

        // ─── update_design ──────────────────────────────────────────────

        /// <summary>Edit fields on the stored spec and rebuild ONLY the roles
        /// those fields touch.
        ///
        /// The rule table lives here rather than in a prompt: a roof change
        /// rebuilds one element, a footprint change stretches the walls it can
        /// and rebuilds the slab and roof, and a floor-to-floor change is a
        /// parameter edit on the levels with nothing rebuilt at all. Preferring
        /// an edit over a rebuild is what keeps hosted doors and windows
        /// alive.</summary>
        public static Dictionary<string, object?> UpdateDesign(Document doc, JsonElement args,
                                                               UIDocument? uidoc = null)
        {
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var json = LoadJson(doc);
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException(
                    "no design spec in this model — nothing to update. Build one with "
                    + "build_design first, or edit the elements directly.");

            using var doc0 = JsonDocument.Parse(json);
            var root = doc0.RootElement;
            var oldArgs = root.GetProperty("args");
            var owns = new Dictionary<string, List<long>>();
            if (root.TryGetProperty("owns", out var ownsEl))
                foreach (var p in ownsEl.EnumerateObject())
                    owns[p.Name] = p.Value.EnumerateArray().Select(v => v.GetInt64()).ToList();

            // Merge: the caller sends only what changed.
            var merged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(oldArgs.GetRawText())
                         ?? new Dictionary<string, JsonElement>();
            var changed = new List<string>();
            foreach (var p in args.EnumerateObject())
            {
                if (p.Name == "spec_id") continue;
                var isNew = !merged.TryGetValue(p.Name, out var prev)
                            || prev.GetRawText() != p.Value.GetRawText();
                merged[p.Name] = p.Value;
                if (isNew) changed.Add(p.Name);
            }
            if (changed.Count == 0)
                return new Dictionary<string, object?>
                { ["ok"] = true, ["changed_fields"] = new List<object>(), ["note"] = "spec unchanged" };

            // Which roles each changed field touches.
            var roles = new HashSet<string>();
            foreach (var f in changed)
            {
                switch (f)
                {
                    case "roof": roles.Add("roof"); break;
                    case "openings": roles.Add("window"); roles.Add("door"); break;
                    case "program": roles.Add("partition"); roles.Add("room"); break;
                    case "structure": roles.Add("grid"); roles.Add("column"); break;
                    case "levels": roles.Add("__levels__"); break;
                    case "footprint_mm":
                        roles.Add("perimeter_wall"); roles.Add("slab"); roles.Add("roof"); break;
                    case "walls": roles.Add("__walltype__"); break;
                    default: roles.Add("__all__"); break;
                }
            }

            var mergedJson = JsonSerializer.Serialize(merged);
            using var mergedDoc = JsonDocument.Parse(mergedJson);
            var newArgs = mergedDoc.RootElement;

            var removed = new List<long>();
            var report = new Dictionary<string, object?>();

            // Level height change is a PARAMETER edit — nothing is rebuilt, and
            // top-constrained walls follow on their own.
            if (roles.Contains("__levels__") && !roles.Contains("__all__"))
            {
                var f2f = (Num(Obj(newArgs, "levels"), "floor_to_floor_mm") ?? 3000) / FT;
                using var tx1 = new Transaction(doc, "BINA: update levels");
                TxGuard.StartSwallowing(tx1);
                var lvls = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.Elevation).ToList();
                var baseElev = lvls.First().Elevation;
                for (int i = 1; i < lvls.Count; i++)            // bottom-up, order matters
                    lvls[i].get_Parameter(BuiltInParameter.LEVEL_ELEV)?.Set(baseElev + f2f * i);
                SaveJson(doc, JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["spec_id"] = root.GetProperty("spec_id").GetString(),
                    ["version"] = 1,
                    ["args"] = JsonSerializer.Deserialize<object>(mergedJson),
                    ["owns"] = owns,
                }));
                TxGuard.CommitOrThrow(tx1);
                t0.Stop();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["changed_fields"] = changed,
                    ["strategy"] = "parameter edit — levels moved, hosted elements followed",
                    ["rebuilt"] = new Dictionary<string, object?>(),
                    ["elapsed_ms"] = t0.ElapsedMilliseconds,
                    ["digest"] = Inspectors.GetGeometryDigest(doc, args),
                };
            }

            // Wall TYPE change is a type swap — openings survive.
            if (roles.Contains("__walltype__") && roles.Count == 1)
            {
                var wt = FindWallType(doc, Str(Obj(newArgs, "walls"), "exterior_type"), interior: false);
                using var tx2 = new Transaction(doc, "BINA: swap wall type");
                TxGuard.StartSwallowing(tx2);
                var n = 0;
                foreach (var id in owns.TryGetValue("perimeter_wall", out var pws) ? pws : new List<long>())
                    if (doc.GetElement(ElemIds.From(id)) is Wall w)
                    { w.WallType = wt; n++; }
                SaveJson(doc, JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["spec_id"] = root.GetProperty("spec_id").GetString(),
                    ["version"] = 1,
                    ["args"] = JsonSerializer.Deserialize<object>(mergedJson),
                    ["owns"] = owns,
                }));
                TxGuard.CommitOrThrow(tx2);
                t0.Stop();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["changed_fields"] = changed,
                    ["strategy"] = "type swap — openings preserved",
                    ["rebuilt"] = new Dictionary<string, object?> { ["perimeter_wall"] = n },
                    ["elapsed_ms"] = t0.ElapsedMilliseconds,
                    ["digest"] = Inspectors.GetGeometryDigest(doc, args),
                };
            }

            // Footprint change: STRETCH the perimeter walls rather than replacing
            // them. A wall's location curve can be re-pointed in place, and the
            // doors and windows hosted in it survive — delete-and-recreate
            // destroys every one of them, which is the difference between
            // "lebarkan ke 16m" being an edit and being a demolition.
            //
            // Only valid while the outline keeps the same number of edges. A
            // different edge count is a different building, and there is no
            // sensible mapping from old wall to new edge.
            if (changed.Contains("footprint_mm") && changed.Count == 1
                && owns.TryGetValue("perimeter_wall", out var pwIds) && pwIds.Count > 0)
            {
                var oldPts = Footprint(oldArgs);
                var newPts = Footprint(newArgs);
                if (oldPts.Count == newPts.Count && pwIds.Count % newPts.Count == 0)
                {
                    var storeys = pwIds.Count / newPts.Count;
                    var stretched = 0;
                    var newOwns = new Dictionary<string, List<long>>(owns);

                    using var txS = new Transaction(doc, "BINA: resize footprint");
                    TxGuard.StartSwallowing(txS);
                    try
                    {
                        // 1. Walls: re-point each location curve to its new edge.
                        for (int s = 0; s < storeys; s++)
                            for (int i = 0; i < newPts.Count; i++)
                            {
                                var el = doc.GetElement(ElemIds.From(pwIds[s * newPts.Count + i]));
                                if (el is not Wall w || w.Location is not LocationCurve lc) continue;
                                var z = lc.Curve.GetEndPoint(0).Z;
                                var a = newPts[i];
                                var b = newPts[(i + 1) % newPts.Count];
                                lc.Curve = Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));
                                stretched++;
                            }

                        // 2. Slabs and roof have no hosted children, so replacing
                        //    them costs nothing and is simpler than reshaping a
                        //    sketch.
                        var kill = new List<ElementId>();
                        foreach (var role in new[] { "slab", "roof" })
                            if (owns.TryGetValue(role, out var ids))
                                foreach (var id in ids)
                                    if (doc.GetElement(ElemIds.From(id)) != null)
                                        kill.Add(ElemIds.From(id));
                        if (kill.Count > 0) doc.Delete(kill);
                        newOwns["slab"] = new List<long>();
                        newOwns["roof"] = new List<long>();

                        var lvls = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                            .OrderBy(l => l.Elevation).ToList();
                        var slabType2 = FindFloorType(doc, Str(Obj(newArgs, "floors"), "slab_type"));
                        for (int s = 0; s < storeys && s < lvls.Count; s++)
                        {
                            var slab = Floor.Create(doc,
                                new List<CurveLoop> { CurveLoopOf(newPts, 0) }, slabType2.Id, lvls[s].Id);
                            newOwns["slab"].Add(slab.Id.Value);
                        }

                        var roofSpec2 = Obj(newArgs, "roof");
                        var roofType2 = new FilteredElementCollector(doc).OfClass(typeof(RoofType))
                            .OfCategory(BuiltInCategory.OST_Roofs).Cast<RoofType>()
                            .FirstOrDefault(t => Str(roofSpec2, "type_name") == null
                                || string.Equals(t.Name, Str(roofSpec2, "type_name"),
                                                 StringComparison.OrdinalIgnoreCase));
                        if (roofType2 != null && storeys < lvls.Count)
                        {
                            var top = lvls[Math.Min(storeys, lvls.Count - 1)];
                            var kind2 = (Str(roofSpec2, "kind") ?? "flat").ToLowerInvariant();
                            var pitch2 = Num(roofSpec2, "pitch_deg");
                            var roof2 = doc.Create.NewFootPrintRoof(Loop(newPts, top.Elevation), top,
                                                                    roofType2, out ModelCurveArray sh2);
                            var ratio2 = pitch2.HasValue ? Math.Tan(pitch2.Value * Math.PI / 180.0) : 0.0;
                            var k = 0;
                            foreach (ModelCurve mc in sh2)
                            {
                                var def = kind2 == "hip" ? pitch2.HasValue
                                    : kind2 == "gable" && pitch2.HasValue && (k % 2 == 0);
                                roof2.set_DefinesSlope(mc, def);
                                if (def) roof2.set_SlopeAngle(mc, ratio2);
                                k++;
                            }
                            newOwns["roof"].Add(roof2.Id.Value);
                        }

                        SaveJson(doc, JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["spec_id"] = root.GetProperty("spec_id").GetString(),
                            ["version"] = 1,
                            ["args"] = JsonSerializer.Deserialize<object>(mergedJson),
                            ["owns"] = newOwns,
                        }));
                        TxGuard.CommitOrThrow(txS);
                    }
                    catch
                    {
                        if (txS.GetStatus() == TransactionStatus.Started) txS.RollBack();
                        throw;
                    }

                    t0.Stop();
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["changed_fields"] = changed,
                        ["strategy"] = "walls stretched in place — doors and windows preserved; "
                                     + "slab and roof rebuilt (they host nothing)",
                        ["walls_stretched"] = stretched,
                        ["rebuilt"] = new Dictionary<string, object?>
                        {
                            ["slab"] = newOwns["slab"].Count, ["roof"] = newOwns["roof"].Count,
                        },
                        ["replaced_element_count"] = 0,
                        ["elapsed_ms"] = t0.ElapsedMilliseconds,
                        ["digest"] = Inspectors.GetGeometryDigest(doc, args),
                    };
                }

                // Different edge count: the outline is a different shape, so
                // there is no wall-to-edge mapping to stretch along. Fall
                // through to the rebuild, and say so — the caller must warn the
                // drafter that hosted openings are lost.
                report["topology_changed"] = true;
                report["warning"] = $"the outline went from {oldPts.Count} to {newPts.Count} edges, "
                    + "so the walls were rebuilt rather than stretched — doors and windows hosted "
                    + "in them are gone. Tell the drafter before moving on.";
            }

            // Otherwise: replace the ENTIRE spec-owned set and rebuild from the
            // merged spec.
            //
            // This deliberately deletes every owned role, not just the ones the
            // changed field touches. BuildDesign is a whole-building build: ask
            // it to "rebuild the roof" and it also creates fresh levels, slabs,
            // partitions, rooms, columns and grids. Deleting only the affected
            // roles therefore leaves the untouched ones in place AND builds a
            // second copy of them.
            //
            // Measured 2026-08-06 on a 24-storey tower: one update produced a
            // complete duplicate building — 96 extra walls, 24 extra slabs, 20
            // extra columns, hundreds of extra rooms and windows — and the
            // drafter had to delete them by hand. Replacing the whole owned set
            // costs a longer rebuild and is always correct; the fast paths above
            // (level heights, wall types, roof-only, footprint stretch) are what
            // keep the common edits cheap, and none of them reach this code.
            var toDelete = new List<ElementId>();
            var targetRoles = owns.Keys.ToList();
            foreach (var role in targetRoles)
                if (owns.TryGetValue(role, out var ids))
                    foreach (var id in ids)
                        if (doc.GetElement(ElemIds.From(id)) != null)
                        { toDelete.Add(ElemIds.From(id)); removed.Add(id); }

            using var tx3 = new Transaction(doc, "BINA: update design");
            TxGuard.StartSwallowing(tx3);
            try
            {
                if (toDelete.Count > 0) doc.Delete(toDelete);
                TxGuard.CommitOrThrow(tx3);
            }
            catch
            {
                if (tx3.GetStatus() == TransactionStatus.Started) tx3.RollBack();
                throw;
            }

            // Rebuild from the merged spec. BuildDesign opens its own
            // transaction and writes the new spec + ownership map.
            var rebuilt = BuildDesign(doc, newArgs, uidoc);
            t0.Stop();
            report["ok"] = true;
            report["changed_fields"] = changed;
            report["strategy"] = "replaced the whole spec-owned set and rebuilt "
                               + "(no cheaper strategy applies to this change)";
            report["roles_rebuilt"] = targetRoles;
            report["replaced_element_count"] = removed.Count;
            report["note"] = removed.Count > 0
                ? $"{removed.Count} spec-owned element(s) were replaced. Anything the drafter "
                + "edited by hand in that set is gone — one Ctrl+Z restores it."
                : null;
            report["created"] = rebuilt.TryGetValue("created", out var c) ? c : null;
            report["elapsed_ms"] = t0.ElapsedMilliseconds;
            report["digest"] = rebuilt.TryGetValue("digest", out var d) ? d : null;
            return report;
        }
    }
}
