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

        private static FloorType FindFloorType(Document doc, string? name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
            if (all.Count == 0) throw new InvalidOperationException("no floor types in this project");
            if (string.IsNullOrWhiteSpace(name)) return all.First();
            return all.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"floor type '{name}' not found");
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
            var extType = FindWallType(doc, Str(wallsSpec, "exterior_type"), interior: false);
            var intType = FindWallType(doc, Str(wallsSpec, "interior_type"), interior: true);

            var floorsSpec = Obj(args, "floors");
            var slabType = FindFloorType(doc, Str(floorsSpec, "slab_type"));

            var roofSpec = Obj(args, "roof");
            var roofKind = (Str(roofSpec, "kind") ?? "flat").ToLowerInvariant();
            var roofPitch = Num(roofSpec, "pitch_deg");
            var roofType = new FilteredElementCollector(doc).OfClass(typeof(RoofType))
                .OfCategory(BuiltInCategory.OST_Roofs).Cast<RoofType>()
                .FirstOrDefault(t => Str(roofSpec, "type_name") == null
                    || string.Equals(t.Name, Str(roofSpec, "type_name"), StringComparison.OrdinalIgnoreCase));

            var openSpec = Obj(args, "openings");
            var doorSym = FindSymbol(doc, BuiltInCategory.OST_Doors, Str(openSpec, "door_type"));
            var winSym = FindSymbol(doc, BuiltInCategory.OST_Windows, Str(openSpec, "window_type"));
            var winSpacing = (Num(openSpec, "window_spacing_mm") ?? 3000) / FT;
            var sill = (Num(openSpec, "sill_mm") ?? 900) / FT;

            // A roof needs a plan view active (see CreateRoof) — switch before
            // the transaction opens, Revit rejects a view change inside one.
            View? restoreView = null;
            if (uidoc != null && uidoc.ActiveView is not ViewPlan)
            {
                var plan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && v.GenLevel != null);
                if (plan != null) { restoreView = uidoc.ActiveView; uidoc.ActiveView = plan; }
            }

            var owns = new Dictionary<string, List<long>>();
            void Own(string role, long id)
            {
                if (!owns.TryGetValue(role, out var list)) owns[role] = list = new List<long>();
                list.Add(id);
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

                // Slabs + perimeter walls, per storey ---------------------------
                for (int s = 0; s < count; s++)
                {
                    var lvl = levels[s];
                    var slab = Floor.Create(doc, new List<CurveLoop> { CurveLoopOf(footprint, 0) },
                                            slabType.Id, lvl.Id);
                    Own("slab", slab.Id.Value);

                    for (int i = 0; i < footprint.Count; i++)
                    {
                        var a = footprint[i];
                        var b = footprint[(i + 1) % footprint.Count];
                        var line = Line.CreateBound(new XYZ(a.X, a.Y, lvl.Elevation),
                                                    new XYZ(b.X, b.Y, lvl.Elevation));
                        var w = Wall.Create(doc, line, extType.Id, lvl.Id, f2f, 0, false, false);
                        // Top-constrain so a later floor-to-floor change moves the
                        // walls with it instead of leaving them behind.
                        w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(levels[s + 1].Id);
                        Own("perimeter_wall", w.Id.Value);
                    }
                }

                // Roof ---------------------------------------------------------
                if (roofType != null)
                {
                    var top = levels[count];
                    var roof = doc.Create.NewFootPrintRoof(Loop(footprint, top.Elevation), top,
                                                           roofType, out ModelCurveArray shape);
                    var ratio = roofPitch.HasValue ? Math.Tan(roofPitch.Value * Math.PI / 180.0) : 0.0;
                    var idx = 0;
                    foreach (ModelCurve mc in shape)
                    {
                        // gable: the first pair of opposite edges slope. hip: all.
                        var defines = roofKind == "hip" ? roofPitch.HasValue
                            : roofKind == "gable" && roofPitch.HasValue && (idx % 2 == 0);
                        roof.set_DefinesSlope(mc, defines);
                        if (defines) roof.set_SlopeAngle(mc, ratio);
                        idx++;
                    }
                    Own("roof", roof.Id.Value);
                }

                // Openings on the ground floor ---------------------------------
                var groundWalls = owns.TryGetValue("perimeter_wall", out var pw)
                    ? pw.Take(footprint.Count).ToList() : new List<long>();
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
                if (winSym != null)
                {
                    foreach (var wid in groundWalls.Skip(1))
                    {
                        var host = doc.GetElement(ElemIds.From(wid)) as Wall;
                        if (host?.Location is not LocationCurve wlc) continue;
                        var len = wlc.Curve.Length;
                        var n = Math.Max(1, (int)Math.Floor(len / Math.Max(winSpacing, 1e-6)));
                        for (int k = 0; k < n; k++)
                        {
                            var t = (k + 0.5) / n;
                            var p = wlc.Curve.Evaluate(t, true);
                            var fi = doc.Create.NewFamilyInstance(
                                new XYZ(p.X, p.Y, levels[0].Elevation), winSym, host, levels[0],
                                Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                            fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sill);
                            Own("window", fi.Id.Value);
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

                // Interior program: partitions then rooms. Rooms cannot find
                // their boundaries until the partitions are regenerated, so this
                // regeneration is required, not incidental.
                var program = Obj(args, "program");
                if (program != null && program.Value.ValueKind == JsonValueKind.Array
                    && program.Value.GetArrayLength() > 0)
                {
                    var names = program.Value.EnumerateArray()
                        .Select(e => e.TryGetProperty("name", out var n) ? n.GetString() : null)
                        .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                    var minX = footprint.Min(p => p.X);
                    var maxX = footprint.Max(p => p.X);
                    var minY = footprint.Min(p => p.Y);
                    var maxY = footprint.Max(p => p.Y);
                    var lvl = levels[0];
                    var n2 = names.Count;
                    if (n2 > 1)
                    {
                        var step = (maxX - minX) / n2;
                        for (int i = 1; i < n2; i++)
                        {
                            var x = minX + step * i;
                            var line = Line.CreateBound(new XYZ(x, minY, lvl.Elevation),
                                                        new XYZ(x, maxY, lvl.Elevation));
                            var w = Wall.Create(doc, line, intType.Id, lvl.Id, f2f, 0, false, false);
                            w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(levels[1].Id);
                            Own("partition", w.Id.Value);
                        }
                        doc.Regenerate();
                        for (int i = 0; i < n2; i++)
                        {
                            var cx = minX + step * (i + 0.5);
                            var cy = (minY + maxY) / 2;
                            var room = doc.Create.NewRoom(lvl, new UV(cx, cy));
                            if (room != null)
                            {
                                try { room.Name = names[i]; } catch { }
                                Own("room", room.Id.Value);
                            }
                        }
                    }
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
                t0.Stop();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["spec_id"] = specId,
                    ["created"] = owns.ToDictionary(k => k.Key, v => (object?)v.Value.Count),
                    ["new_ids"] = owns.SelectMany(k => k.Value).ToList(),
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
                if (restoreView != null && uidoc != null)
                { try { uidoc.ActiveView = restoreView; } catch { } }
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

            // Otherwise: delete the spec-owned elements of the affected roles and
            // rebuild them from the merged spec. Everything is in ONE transaction,
            // so a single Ctrl+Z restores the previous state.
            var toDelete = new List<ElementId>();
            var targetRoles = roles.Contains("__all__")
                ? owns.Keys.ToList()
                : roles.Where(r => !r.StartsWith("__")).ToList();
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
            report["strategy"] = "rebuilt the affected roles";
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
