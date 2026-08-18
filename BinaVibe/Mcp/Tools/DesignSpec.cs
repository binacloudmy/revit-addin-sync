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
using System.Text.Json.Nodes;
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
            // Search ALL kinds, then explain kind mismatches. The old
            // Basic-only collector made a Stacked/Curtain name copied straight
            // from list_wall_types come back "not found" — a lying error that
            // sent the model on a 3-round name-guessing flail before it
            // abandoned build_design entirely (2026-08-18, trace 498a5cf1).
            var every = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
            var all = every.Where(t => t.Kind == WallKind.Basic).ToList();
            if (all.Count == 0) throw new InvalidOperationException("no basic wall types in this project");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var hit = all.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
                var wrongKind = every.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (wrongKind != null)
                    throw new ArgumentException(
                        $"wall type '{name}' is a {wrongKind.Kind} wall type; build_design needs a Basic wall. " +
                        $"Closest Basic types: {TypeCandidates.Nearest(all.Select(t => t.Name), name!)}");
                throw new ArgumentException(
                    $"wall type '{name}' not found; closest Basic types: {TypeCandidates.Nearest(all.Select(t => t.Name), name!)}");
            }
            // No name given: pick by TARGET THICKNESS, not by extreme. "Take
            // the thickest" chose a 400mm+ compound wall on the 2026-08-11
            // smoke and the drafter's first question was "why the dinding so
            // tebal?" — a house wants ~200mm outside, ~100mm partitions; the
            // closest match to those beats whatever extreme the template
            // happens to carry.
            double targetFt = (interior ? 100.0 : 200.0) / 304.8;
            return all.OrderBy(t => Math.Abs(t.Width - targetFt)).First();
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
            // Accept every spelling a model plausibly sends: the type name
            // ("900 x 2100mm"), "Family : Type" (how Revit DISPLAYS types, so
            // models copy it — 2026-08-11 loop: every such guess threw), and a
            // bare family name (first type of that family).
            var trimmed = name.Trim();
            var byType = all.FirstOrDefault(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (byType != null) return byType;
            var colon = trimmed.LastIndexOf(':');
            if (colon > 0)
            {
                var fam = trimmed.Substring(0, colon).Trim();
                var typ = trimmed.Substring(colon + 1).Trim();
                var combo = all.FirstOrDefault(s =>
                    string.Equals(s.FamilyName, fam, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.Name, typ, StringComparison.OrdinalIgnoreCase));
                if (combo != null) return combo;
            }
            var byFamily = all.FirstOrDefault(s =>
                string.Equals(s.FamilyName, trimmed, StringComparison.OrdinalIgnoreCase));
            if (byFamily != null) return byFamily;
            throw new ArgumentException(
                $"type '{name}' not found in {bic} — known: "
                + string.Join(", ", all.Take(8).Select(s => $"{s.FamilyName} : {s.Name}")));
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

        /// <summary>Build a design.
        ///
        /// Two paths, and which one runs is decided by the SPEC, not a flag:
        ///
        ///   `parts` present  → the backend solved this building (design_preflight
        ///                      → design_parts) and shipped an ordered part list
        ///                      with a prediction per part, plus the walls, doors
        ///                      and windows it wants BY ID. The addin builds
        ///                      exactly that and measures every part against the
        ///                      prediction — it invents no geometry.
        ///
        ///   `parts` absent   → an older backend (or a caller driving the tool
        ///                      directly). The original monolithic build runs
        ///                      unchanged, heuristics and all, so an addin
        ///                      updated ahead of its server keeps working.
        ///
        /// The heuristics the plan calls for deleting — the corridor-centre door
        /// and the blanket window spacing — are gone from the solved path by not
        /// existing in it. They survive ONLY inside BuildLegacy, quarantined
        /// where a solved plan can never reach them.</summary>
        public static Dictionary<string, object?> BuildDesign(Document doc, JsonElement args,
                                                              UIDocument? uidoc = null)
        {
            // Repair: the backend re-solved (or re-sent) a FAILED/BLOCKED subset
            // of a prior build's parts, named by `repair_of` (that build's
            // spec_id) + `parts` (the subset only — not the whole plan). Rebuild
            // just those, against the SAME args as the original build, and merge
            // the result into what is already standing instead of starting over.
            bool isRepair = args.TryGetProperty("repair_of", out var repairOf)
                            && repairOf.ValueKind == JsonValueKind.String;
            if (isRepair)
                return RepairFromParts(doc, args, uidoc, repairOf.GetString()!);

            var parts = Obj(args, "parts");
            if (parts != null && parts.Value.ValueKind == JsonValueKind.Array
                && parts.Value.GetArrayLength() > 0)
                return BuildFromParts(doc, args, uidoc, parts.Value);
            return BuildLegacy(doc, args, uidoc);
        }

        /// <summary>Repair entry: rebuild only the subset `parts` names, against
        /// the SAME build engine BuildFromParts uses (see the repair parameters
        /// threaded through it) — same wall types, same solved geometry, same
        /// scorecard shape. The clear step in BuildFromParts prefers the
        /// per-PART ownership map (owns_by_part, loaded here) so clearing one
        /// part's elements can never reach a sibling volume's — e.g. repairing
        /// `roof.main` never touches `roof.porch`'s roof. A spec saved before
        /// owns_by_part existed falls back to the old per-ROLE map and refuses
        /// (never clears) any part whose bucket is shared by a sibling outside
        /// the subset. Everything else standing in the model — including
        /// whatever the drafter drew by hand — is left alone regardless.</summary>
        private static Dictionary<string, object?> RepairFromParts(
            Document doc, JsonElement args, UIDocument? uidoc, string repairOf)
        {
            if (!args.TryGetProperty("parts", out var subset)
                || subset.ValueKind != JsonValueKind.Array || subset.GetArrayLength() == 0)
                throw new ArgumentException(
                    $"repair_of '{repairOf}' given, but `parts` carries no subset to rebuild");

            var storedJson = LoadJson(doc);
            if (string.IsNullOrEmpty(storedJson))
                throw new InvalidOperationException(
                    $"repair_of '{repairOf}' given, but this document carries no BINA spec to "
                    + "repair — build_design must run once before a repair round");
            using var storedDoc = JsonDocument.Parse(storedJson);
            var stored = storedDoc.RootElement;

            // `repairOf` is a ROUND fingerprint (scorecard_fingerprint on the
            // backend: "sc-" + a hash of this round's part/status pairs), NOT
            // the building's stable spec_id — it changes every repair round
            // by design, so it must never be written back as spec_id. The
            // stable id is whatever the stored spec already carries.
            string? priorSpecId = stored.TryGetProperty("spec_id", out var specIdEl)
                                   && specIdEl.ValueKind == JsonValueKind.String
                ? specIdEl.GetString() : null;

            var priorOwns = new Dictionary<string, List<long>>();
            if (stored.TryGetProperty("owns", out var ownsEl) && ownsEl.ValueKind == JsonValueKind.Object)
                foreach (var p in ownsEl.EnumerateObject())
                    priorOwns[p.Name] = p.Value.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.Number)
                        .Select(v => v.GetInt64()).ToList();

            // Per-PART ownership (as opposed to `owns`, which is per ROLE and,
            // for slab/roof, shared by every volume — see RoleForPart). A spec
            // saved by this build or later carries this map, and the repair
            // clear below prefers it: clearing owns_by_part["roof.porch"] can
            // never touch owns_by_part["roof.main"]'s elements, whereas the
            // bare "roof" role bucket cannot tell them apart.
            var priorOwnsByPart = new Dictionary<string, List<long>>();
            if (stored.TryGetProperty("owns_by_part", out var obpEl)
                && obpEl.ValueKind == JsonValueKind.Object)
                foreach (var p in obpEl.EnumerateObject())
                    priorOwnsByPart[p.Name] = p.Value.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.Number)
                        .Select(v => v.GetInt64()).ToList();

            var priorScorecard = new JsonArray();
            if (stored.TryGetProperty("scorecard", out var scEl) && scEl.ValueKind == JsonValueKind.Array)
                foreach (var row in scEl.EnumerateArray())
                    priorScorecard.Add(JsonNode.Parse(row.GetRawText()));

            var priorWallBySpecId = new Dictionary<string, long>();
            if (stored.TryGetProperty("wall_by_spec_id", out var wbsEl)
                && wbsEl.ValueKind == JsonValueKind.Object)
                foreach (var p in wbsEl.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number)
                        priorWallBySpecId[p.Name] = p.Value.GetInt64();

            return BuildFromParts(doc, args, uidoc, subset,
                repairOf: repairOf, priorSpecId: priorSpecId,
                priorOwns: priorOwns, priorOwnsByPart: priorOwnsByPart,
                priorScorecard: priorScorecard, priorWallBySpecId: priorWallBySpecId);
        }

        /// <summary>The full set of part ids that, under the pre-owns_by_part
        /// role scheme, could have contributed to the given bare role bucket —
        /// i.e. every volume's own slab or roof part. Used ONLY to decide
        /// whether a legacy spec's repair-clear is safe (the subset covers
        /// every sibling) or must be refused (it doesn't). Every other role in
        /// this file was already 1:1 with a part id (see RoleForPart), so an
        /// empty list here means "no legacy ambiguity for this role" — the
        /// bare-role fallback is safe on its own.</summary>
        private static List<string> LegacySharedRoleSiblings(string role, List<BuildVolume> vols)
        {
            if (role == "slab") return vols.Select(v => $"slab.{v.Role}").ToList();
            if (role == "roof") return vols.Select(v => $"roof.{v.Role}").ToList();
            return new List<string>();
        }

        /// <summary>Maps a part id to the bare ROLE bucket it used to be
        /// recorded under, before owns_by_part (per-part ownership) existed.
        /// LEGACY FALLBACK ONLY — a repair prefers owns_by_part, which never
        /// needs this. `slab` and `roof` are shared here by every volume's own
        /// slab/roof part (walls.main -> perimeter_wall is not, each volume
        /// already has its own bucket), which is exactly why the legacy clear
        /// path checks LegacySharedRoleSiblings before trusting this mapping —
        /// clearing the bare bucket for one volume would otherwise clear every
        /// volume's.</summary>
        private static string? RoleForPart(string partId)
        {
            if (string.IsNullOrEmpty(partId)) return null;
            if (partId.StartsWith("slab.", StringComparison.Ordinal)) return "slab";
            if (partId == "walls.partitions") return "partition";
            if (partId.StartsWith("walls.", StringComparison.Ordinal))
            {
                var role = partId.Substring("walls.".Length);
                return role == "main" ? "perimeter_wall" : $"{role}_wall";
            }
            if (partId == "doors") return "door";
            if (partId == "windows") return "window";
            if (partId.StartsWith("roof.", StringComparison.Ordinal)) return "roof";
            // fixtures.* and unknown ids own nothing to clear here — but a
            // `.l<k>` id is NOT one of them (M4, final review 2026-08-13,
            // correcting a stale claim this comment used to make): `slab.l2`
            // matches the `slab.` branch above and returns "slab"; `walls.
            // main.l2` matches the `walls.` branch and returns a meaningless
            // "main.l2_wall". Both branches fire before this final `return
            // null` is ever reached — a `.l<k>` id never actually falls
            // through to here.
            //
            // That misclassification is harmless in practice, not because
            // this function is never called on such an id, but because its
            // ONLY caller (the legacy bare-role clear path above) checks
            // owns_by_part FIRST and only falls back to RoleForPart when
            // that lookup misses — and owns_by_part is always present on any
            // spec new enough to carry multi-storey parts at all (this
            // bucket scheme predates owns_by_part; rung 4's per-level parts
            // could not exist before it). So the bogus bucket this returns
            // for a `.l<k>` id is computed but never consulted. Left
            // unchanged rather than taught a shape its caller will never
            // actually need it to classify.
            return null;
        }

        /// <summary>Peel a trailing `.l<digits>` storey suffix off a part id —
        /// `slab.l2` -> ("slab", 2), `walls.main.l2` -> ("walls.main", 2),
        /// `walls.main` -> ("walls.main", 1) (no suffix = level 1, unchanged).
        /// Every per-level part id design_parts.py mints (rung 4, task 5/8)
        /// follows this one grammar, so one helper covers slab/walls/doors/
        /// windows/partitions/fixtures alike.</summary>
        private static (string baseId, int level) SplitLevel(string id)
        {
            var m = System.Text.RegularExpressions.Regex.Match(id, @"^(.*)\.l(\d+)$");
            return m.Success ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value))
                             : (id, 1);
        }

        /// <summary>Scope a door/window/partition `wall_id` to the storey it
        /// was solved on, for the wallBySpecId lookup — NOT for anything the
        /// backend sees.
        ///
        /// Every level's interior solve (`solve_design`, called once per
        /// storey by `solve_multistorey`) mints its own partitions starting
        /// from `partition.0` again — level 2's first partition and level 1's
        /// first partition are BOTH literally `"partition.0"` (confirmed by
        /// reading `layout_solver.solve_openings`, which builds that id with
        /// no level threaded through it, and `design_preflight
        /// ._shift_upper_level_walls`, which shifts coordinates but never
        /// touches `id`). `wallBySpecId` is one flat dict for the whole
        /// build, so recording level 2's "partition.0" under the bare string
        /// would silently overwrite (or be shadowed by) level 1's own
        /// "partition.0" — a door hosted on "partition.0" at level 2 could
        /// resolve to a level-1 wall three metres below it.
        ///
        /// Exterior ids need no scoping: `ext.<role>.<i>` (L1) and
        /// `ext.<role>.l<k>.<i>` (L2+) are already globally distinct by
        /// construction (`shadow_model.wall_specs`), so this only touches
        /// interior ids, and only at level 2+ — level 1's own keys stay
        /// exactly as before this task (a persisted `wall_by_spec_id` from a
        /// pre-rung-4 build still resolves).</summary>
        private static string ScopedWallSpecId(string specId, int level) =>
            level >= 2 && !specId.StartsWith("ext.", StringComparison.Ordinal)
                ? $"l{level}:{specId}" : specId;

        // ─── the solved path: one part at a time, measured ──────────────

        /// <summary>Build the SOLVED plan, part by part, inside one undo.
        ///
        /// Everything the model will narrate comes out of PartLoop's scorecard,
        /// so the shape of this method matters: anything built outside a part is
        /// something the drafter is told about without evidence. What stays
        /// outside the loop is only what no part describes — the level stack and
        /// the curtain-wall type dressing (both preconditions), the structural
        /// grid, storeys above the ground floor, and the room tags, which need
        /// the partitions to exist and be regenerated first.
        ///
        /// Transactions: PartLoop opens one per part, so this method's own work
        /// is split into a prepare transaction before the loop and a finish
        /// transaction after it, with a TransactionGroup around the lot — one
        /// Ctrl+Z still puts the whole building back.</summary>
        private static Dictionary<string, object?> BuildFromParts(
            Document doc, JsonElement args, UIDocument? uidoc, JsonElement partsJson,
            string? repairOf = null,
            string? priorSpecId = null,
            Dictionary<string, List<long>>? priorOwns = null,
            Dictionary<string, List<long>>? priorOwnsByPart = null,
            JsonArray? priorScorecard = null,
            Dictionary<string, long>? priorWallBySpecId = null)
        {
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var footprint = Footprint(args);

            var levelsSpec = Obj(args, "levels");
            var count = (int)(Num(levelsSpec, "count") ?? 1);
            if (count < 1) throw new ArgumentException("levels.count must be at least 1");
            var f2f = (Num(levelsSpec, "floor_to_floor_mm") ?? 3000) / FT;
            var prefix = Str(levelsSpec, "prefix") ?? "Level ";

            // `walls` is the wall-TYPE dict and nothing else. The solved
            // partitions arrive in `partitions` and the solved exterior segments
            // in `exterior_walls` — both ABSOLUTE mm, both carrying the spec ids
            // that doors and windows are hosted by.
            //
            // The array-in-`walls` read below is the ONE transitional release for
            // a backend that still overloads the key; it is removed next release.
            var wallsEl = Obj(args, "walls");
            var wallTypeSpec = wallsEl != null && wallsEl.Value.ValueKind == JsonValueKind.Object
                ? wallsEl : null;
            var partitionsEl = Obj(args, "partitions");
            var solvedPartitions =
                partitionsEl != null && partitionsEl.Value.ValueKind == JsonValueKind.Array
                    ? partitionsEl
                    : (wallsEl != null && wallsEl.Value.ValueKind == JsonValueKind.Array
                        ? wallsEl : null);
            var extWallsEl = Obj(args, "exterior_walls");
            var solvedExteriors = extWallsEl != null
                                  && extWallsEl.Value.ValueKind == JsonValueKind.Array
                ? extWallsEl : null;

            var facadeSpec = Obj(args, "facade");
            var facadeKind = (Str(facadeSpec, "kind") ?? "punched").ToLowerInvariant();
            WallType? curtainType = null;
            if (facadeKind == "curtain" || facadeKind == "curtain_wall" || facadeKind == "glazed")
            {
                var named = Str(facadeSpec, "wall_type") ?? Str(wallTypeSpec, "exterior_type");
                curtainType = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                    .Cast<WallType>().Where(t => t.Kind == WallKind.Curtain)
                    .OrderByDescending(t => named != null
                        && string.Equals(t.Name, named, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
            }
            var extType = curtainType ?? FindWallType(doc, Str(wallTypeSpec, "exterior_type"),
                                                      interior: false);
            var intType = FindWallType(doc, Str(wallTypeSpec, "interior_type"), interior: true);

            var slabType = FindFloorType(doc, Str(Obj(args, "floors"), "slab_type"));

            var roofSpec = Obj(args, "roof");
            var roofKind = (Str(roofSpec, "kind") ?? "flat").ToLowerInvariant();
            var roofPitch = Num(roofSpec, "pitch_deg");
            var roofType = FindRoofType(doc, Str(roofSpec, "type_name"));
            var overhangFt = (Num(roofSpec, "overhang_mm") ?? 0) / FT;

            var openSpec = Obj(args, "openings");
            var doorSym = FindSymbol(doc, BuiltInCategory.OST_Doors, Str(openSpec, "door_type"));
            var winSym = FindSymbol(doc, BuiltInCategory.OST_Windows, Str(openSpec, "window_type"));
            var defaultSill = (Num(openSpec, "sill_mm") ?? 900) / FT;

            // A roof needs a plan view active, and a view cannot be switched
            // while a transaction is open — so this happens first, as it does
            // in the legacy path.
            using var viewSwitch = ViewGuard.EnsurePlanView(doc, uidoc);

            var vols = Volumes.Parse(args, footprint, f2f);
            // The volume every `.l<k>` part id implicitly names — only the
            // PRIMARY volume (the spec's first, same convention `walls.partitions`
            // already uses for "no other volume hosts them") ever climbs past
            // level 1 (design_parts.py, rung 4 task 5). A porch or garage never
            // gets a `.l<k>` part at all, so its own level 1 is always its top.
            var primaryRole = vols.Count > 0 ? vols[0].Role : null;
            BuildVolume VolByRole(string role) =>
                vols.FirstOrDefault(v => string.Equals(v.Role, role, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"a part names volume '{role}', which this spec does not declare");

            // Ownership, tracked per PART as well as per role. PartLoop DELETES
            // a part's elements when it fails, then rebuilds it — so a part that
            // is entered twice must forget what its first attempt recorded, or
            // the stored spec ends up pointing at dead ids and the next
            // update_design deletes somebody else's element.
            var owns = priorOwns != null
                ? priorOwns.ToDictionary(kv => kv.Key, kv => new List<long>(kv.Value))
                : new Dictionary<string, List<long>>();
            var ownedByPart = new Dictionary<string, List<(string Role, long Id)>>();
            var specIdsByPart = new Dictionary<string, List<string>>();

            // Part id -> the full part element, populated just before the loop
            // runs (below). PartLoop only ever hands BuildOnePart the part's
            // `expected` — `info` (fixtures.* carries its solved instances
            // there) rides in a sibling key BuildOnePart otherwise cannot see,
            // so this is the lookup that gets it there without changing
            // PartLoop's delegate signature.
            var partsById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            // spec wall id -> built ElementId. THE lookup for door/window
            // hosting — there is deliberately no coordinate fallback: an id that
            // is not in this map is a reported failure, never a silent skip.
            var wallBySpecId = new Dictionary<string, ElementId>();

            void Own(string role, long id)
            {
                if (!owns.TryGetValue(role, out var list)) owns[role] = list = new List<long>();
                list.Add(id);
            }
            void Record(string partId, string role, ElementId id)
            {
                if (!ownedByPart.TryGetValue(partId, out var list))
                    ownedByPart[partId] = list = new List<(string, long)>();
                list.Add((role, id.Value));
                Own(role, id.Value);
            }
            void RecordSpecId(string partId, string specId, ElementId id)
            {
                if (!specIdsByPart.TryGetValue(partId, out var list))
                    specIdsByPart[partId] = list = new List<string>();
                list.Add(specId);
                wallBySpecId[specId] = id;
            }
            void ForgetPart(string partId)
            {
                if (ownedByPart.TryGetValue(partId, out var list))
                {
                    foreach (var (role, id) in list)
                        if (owns.TryGetValue(role, out var ids)) ids.Remove(id);
                    list.Clear();
                }
                if (specIdsByPart.TryGetValue(partId, out var specIds))
                {
                    foreach (var s in specIds) wallBySpecId.Remove(s);
                    specIds.Clear();
                }
            }

            var levels = new List<Level>();
            // Roofs are built AFTER the loop: RoofBuilder owns a transaction per
            // strategy and a transaction cannot nest inside the one PartLoop
            // opens around the delegate. The part still gets a real scorecard
            // line — measured by PartMeasure against the same prediction — it is
            // just written after the fact.
            var deferredRoofs = new Dictionary<string, (BuildVolume Vol, JsonElement Expected)>();

            // `stairs.main` is deferred for the same reason: StairsEditScope
            // owns its own transaction and cannot nest inside the one
            // PartLoop.BuildAndMeasure wraps around BuildOnePart (verified
            // against the 2023/2027 assemblies — a StairsEditScope.Start()
            // inside an open Transaction throws). Built after the loop, in
            // the same TransactionGroup, mirroring deferredRoofs above.
            var deferredStairs = new Dictionary<string, JsonElement>();

            // The solved exterior segments belonging to one volume, AT ONE
            // LEVEL. `exterior_walls` concatenates every level (rung 4, task
            // 8): L1 ids are `ext.<role>.<i>` (unsuffixed, backward
            // compatible), L2+ are `ext.<role>.l<k>.<i>`. A plain prefix match
            // on `ext.<role>.` matches BOTH shapes — the L2+ id literally
            // starts with the L1 prefix — so a level-1 build would silently
            // also pull in every upper level's segments at level 1's own
            // elevation without the exact shape check below (caught while
            // wiring this up; a 2-storey `exterior_walls` array already
            // concatenates both, whether or not the addin routes `.l<k>`
            // parts yet).
            IEnumerable<(XYZ A, XYZ B, string SpecId)> ExteriorSegments(string role, int level, double z)
            {
                if (solvedExteriors == null) yield break;
                foreach (var seg in solvedExteriors.Value.EnumerateArray())
                {
                    if (seg.ValueKind != JsonValueKind.Object) continue;
                    var segId = seg.TryGetProperty("id", out var idv)
                                && idv.ValueKind == JsonValueKind.String ? idv.GetString() : null;
                    if (segId == null) continue;
                    var segParts = segId.Split('.');
                    var matches = level >= 2
                        ? segParts.Length == 4 && segParts[0] == "ext" && segParts[1] == role
                          && segParts[2] == $"l{level}"
                        : segParts.Length == 3 && segParts[0] == "ext" && segParts[1] == role;
                    if (!matches) continue;
                    yield return (PointMm(seg, "a_mm", z), PointMm(seg, "b_mm", z), segId);
                }
            }

            // One part. Throwing is a legitimate outcome: PartLoop turns it into
            // a failed line on the scorecard, which is the honest answer.
            List<ElementId> BuildOnePart(string id, JsonElement expected)
            {
                ForgetPart(id);
                var created = new List<ElementId>();
                var lvl0 = levels[0];

                if (id.StartsWith("slab.", StringComparison.Ordinal))
                {
                    var (slabBase, slabLevel) = SplitLevel(id);
                    // L1 ids are `slab.<role>` (any volume); L2+ ids are the
                    // bare `slab.l<k>` — no role token, because only the
                    // PRIMARY volume ever climbs (design_parts.py).
                    var slabRole = slabBase == "slab"
                        ? primaryRole ?? throw new InvalidOperationException(
                            $"part '{id}' names an upper-storey slab but this spec declares no volumes")
                        : slabBase.Substring("slab.".Length);
                    var vol = VolByRole(slabRole);
                    var slabLvl = levels[slabLevel - 1];
                    var slab = Floor.Create(doc, new List<CurveLoop> { CurveLoopOf(vol.Outline, 0) },
                                            slabType.Id, slabLvl.Id);
                    Record(id, "slab", slab.Id);
                    created.Add(slab.Id);
                }
                else if (id == "walls.partitions"
                         || id.StartsWith("walls.partitions.l", StringComparison.Ordinal))
                {
                    var (_, partitionsLevel) = SplitLevel(id);
                    // L1 partitions still build from `args["partitions"]`
                    // (design_preflight._attach_solved_level1). L2+ carries
                    // no args-level key of its own — its segments ride in
                    // this part's own `info.walls` instead (rung 4, task 8b:
                    // design_parts.py embeds `solved["levels"][k]["walls"]`,
                    // already shifted into the site frame by
                    // design_preflight._shift_upper_level_walls, the same
                    // frame `walls.<role>.l<k>`'s own bbox prediction uses).
                    JsonElement partitionsArr;
                    if (partitionsLevel >= 2)
                    {
                        if (!partsById.TryGetValue(id, out var partEl)
                            || !partEl.TryGetProperty("info", out var info)
                            || !info.TryGetProperty("walls", out var infoWalls)
                            || infoWalls.ValueKind != JsonValueKind.Array)
                            throw new InvalidOperationException(
                                $"part '{id}', but the spec carries no level-{partitionsLevel} "
                                + "partition geometry (info.walls) — the backend must ship the "
                                + "upper-storey segments before this addin can build them");
                        partitionsArr = infoWalls;
                    }
                    else
                    {
                        if (solvedPartitions == null)
                            throw new InvalidOperationException(
                                "walls.partitions part, but the spec carries no `partitions` array — "
                                + "the backend must send the partitions it solved, by id");
                        partitionsArr = solvedPartitions.Value;
                    }
                    var baseLvl = levels[partitionsLevel - 1];
                    // The next level up, same convention `walls.<role>.l<k>`
                    // uses for its own top constraint (`levels[wallsLevel]`
                    // below) — `EnsureLevels` builds `count + 1` levels
                    // (index `count` is the "Roof" bearing level) precisely
                    // so this always resolves, even for the top storey.
                    var topLvl = levels[partitionsLevel];
                    foreach (var w in partitionsArr.EnumerateArray())
                    {
                        var specId = w.GetProperty("id").GetString()!;
                        var a = PointMm(w, "a_mm", baseLvl.Elevation);
                        var b = PointMm(w, "b_mm", baseLvl.Elevation);
                        if (a.DistanceTo(b) < 1e-6)
                            throw new InvalidOperationException(
                                $"partition '{specId}' has zero length — the solver sent a point, "
                                + "not a wall");
                        var wall = Wall.Create(doc, Line.CreateBound(a, b), intType.Id,
                                               baseLvl.Id, f2f, 0, false, false);
                        wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(topLvl.Id);
                        Record(id, "partition", wall.Id);
                        // Scoped so a level-2 "partition.0" never collides
                        // with level 1's own "partition.0" in wallBySpecId —
                        // see ScopedWallSpecId's docstring.
                        RecordSpecId(id, ScopedWallSpecId(specId, partitionsLevel), wall.Id);
                        created.Add(wall.Id);
                        JoinToNeighbours(doc, wall, wallBySpecId.Values);
                    }
                }
                else if (id.StartsWith("walls.", StringComparison.Ordinal))
                {
                    // The exterior walls are the BACKEND'S, by id. They used to be
                    // re-derived here from Volumes.UnsharedEdges and numbered
                    // ext.<role>.<i> by yield order — two implementations of the
                    // same geometry, agreeing only by luck. Every ext.* id a door
                    // or window is hosted by comes out of this array, so an id
                    // that is missing here is a reported failure, not a shifted
                    // index that silently hosts a door in the wrong wall.
                    var (wallsBase, wallsLevel) = SplitLevel(id);
                    var role = wallsBase.Substring("walls.".Length);
                    var vol = VolByRole(role);
                    var wallHeight = vol.HeightFt > TOL ? vol.HeightFt : f2f;
                    var wallLvl = levels[wallsLevel - 1];
                    // Gable-end treatment (below) belongs ONLY on the level the
                    // roof actually bears on. A climbing primary volume's
                    // intermediate storeys stay flat-topped (design_parts.py's
                    // `_wall_top_mm`: `h` below the top level, `h + ridge_rise`
                    // only at it) — a non-primary volume (porch, garage) never
                    // gets a `.l<k>` part at all, so its own single level is
                    // always "the top" here, exactly as before this task.
                    var isTopLevel = !string.Equals(role, primaryRole, StringComparison.OrdinalIgnoreCase)
                                      || wallsLevel >= count;
                    if (solvedExteriors == null)
                        throw new InvalidOperationException(
                            $"part '{id}', but the spec carries no `exterior_walls` array — "
                            + "the backend must send the exterior segments it solved, by id");
                    var built = 0;
                    foreach (var (a, b, segId) in ExteriorSegments(role, wallsLevel, wallLvl.Elevation))
                    {
                        if (a.DistanceTo(b) < 1e-6)
                            throw new InvalidOperationException(
                                $"exterior wall '{segId}' has zero length — the solver sent a "
                                + "point, not a wall");
                        // Gable-end walls carry the ROOF'S silhouette. Revit
                        // exposes no wall-attach API (verified against the 2023
                        // and 2027 assemblies — column attach only), so a
                        // flat-topped wall under a pitched roof leaves the
                        // gable triangle OPEN — "why there's gap between
                        // bumbung and wall?" (2026-08-12). The pentagon profile
                        // rises to the same ridge line RoofBuilder's extrusion
                        // draws, from the same arithmetic. When the footprint
                        // hip path someday works, these must become conditional
                        // on the BUILT shape — the scorecard's roof bbox will
                        // flag pentagons poking through a hip.
                        var isGableEnd = isTopLevel && GableEnd.IsGableEnd(vol, a, b, args);
                        var w = isGableEnd
                            ? GableEnd.CreateProfiled(doc, vol, a, b, extType, wallLvl,
                                                      wallHeight, args)
                            : Wall.Create(doc, Line.CreateBound(a, b), extType.Id, wallLvl.Id,
                                          wallHeight, 0, false, false);
                        // NEVER top-constrain a profiled wall: the constraint
                        // CROPS the profile back to the level plane. Measured
                        // 2026-08-12: main (the only volume whose height equals
                        // f2f, so the only one entering this branch) lost both
                        // gable apexes — z 3934 vs 6024 — while porch and
                        // garage pentagons, which skip it, came out perfect.
                        // The top constraint is the NEXT level up from this
                        // storey's own base — `levels[1]` only happened to be
                        // right before this task because level 1 was the only
                        // storey that ever reached here.
                        if (!isGableEnd && Math.Abs(wallHeight - f2f) < TOL)
                            w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(levels[wallsLevel].Id);
                        Record(id, vol.Role == "main" ? "perimeter_wall" : $"{vol.Role}_wall", w.Id);
                        RecordSpecId(id, segId, w.Id);
                        created.Add(w.Id);
                        // Corners join like partitions do — the 2026-08-11 smoke
                        // reported 16 open perimeter ends because only interior
                        // walls ever called this.
                        JoinToNeighbours(doc, w, wallBySpecId.Values);
                        built++;
                    }
                    if (built == 0)
                        throw new InvalidOperationException(
                            $"part '{id}' built nothing: `exterior_walls` carries no segment "
                            + $"matching role '{role}' at level {wallsLevel}");
                }
                else if (id == "doors" || id.StartsWith("doors.l", StringComparison.Ordinal))
                {
                    var (_, doorsLevel) = SplitLevel(id);
                    // L1 still builds from `args["doors"]`. L2+'s `doors.l<k>`
                    // part carries a `hosts` list (narration only) AND, as of
                    // rung 4 task 8b, the real per-door `wall_id`/`at_mm`/
                    // `room` records this addin actually places from, in
                    // `info.doors` — same reasoning as walls.partitions.l<k>
                    // above. Note (backend, task 8b report): L2 doors never
                    // carry an ext.-hosted wall_id by design — the solver
                    // strips the exterior entrance door above L1 — so this
                    // will typically place onto that level's own partitions.
                    JsonElement? doorsArr;
                    if (doorsLevel >= 2)
                    {
                        if (!partsById.TryGetValue(id, out var partEl)
                            || !partEl.TryGetProperty("info", out var info)
                            || !info.TryGetProperty("doors", out var infoDoors)
                            || infoDoors.ValueKind != JsonValueKind.Array)
                            throw new InvalidOperationException(
                                $"part '{id}', but the spec carries no level-{doorsLevel} door "
                                + "placements (info.doors) — the backend must ship the upper-storey "
                                + "opening data before this addin can build it");
                        doorsArr = infoDoors;
                    }
                    else
                    {
                        doorsArr = Obj(args, "doors");
                    }
                    var doorLvl = levels[doorsLevel - 1];
                    foreach (var eid in PlaceDoors(doc, doorsArr, doorLvl, doorsLevel, doorSym,
                                                   wallBySpecId))
                    { Record(id, "door", eid); created.Add(eid); }
                }
                else if (id == "windows" || id.StartsWith("windows.l", StringComparison.Ordinal))
                {
                    var (_, windowsLevel) = SplitLevel(id);
                    // Same shape as doors.l<k> above, for windows.
                    JsonElement? windowsArr;
                    if (windowsLevel >= 2)
                    {
                        if (!partsById.TryGetValue(id, out var partEl)
                            || !partEl.TryGetProperty("info", out var info)
                            || !info.TryGetProperty("windows", out var infoWindows)
                            || infoWindows.ValueKind != JsonValueKind.Array)
                            throw new InvalidOperationException(
                                $"part '{id}', but the spec carries no level-{windowsLevel} window "
                                + "placements (info.windows) — the backend must ship the "
                                + "upper-storey opening data before this addin can build it");
                        windowsArr = infoWindows;
                    }
                    else
                    {
                        windowsArr = Obj(args, "windows");
                    }
                    var windowLvl = levels[windowsLevel - 1];
                    foreach (var eid in PlaceWindows(doc, windowsArr, windowLvl, windowsLevel, winSym,
                                                     defaultSill, wallBySpecId))
                    { Record(id, "window", eid); created.Add(eid); }
                }
                else if (id.StartsWith("roof.", StringComparison.Ordinal))
                {
                    deferredRoofs[id] = (VolByRole(id.Substring("roof.".Length)), expected);
                }
                else if (id.StartsWith("fixtures.", StringComparison.Ordinal))
                {
                    // Predicted parts (fixtures_solver, Task 10) carry their
                    // solved instances in a sibling `info` dict; a wet room the
                    // solver reached but placed nothing into ships the legacy
                    // `expected: {"status": "not_implemented"}` stub instead —
                    // returning nothing here is exactly what that measures as
                    // today, so the stub case falls straight through. This
                    // already covers `fixtures.<room>.l<k>` (rung 4, task 8)
                    // with no change: design_parts.py never attaches
                    // `info.instances` to an upper-storey wet room (placement
                    // stays L1-only this rung), so the id-suffix is invisible
                    // here — the lookup is by the part's own FULL id, and the
                    // stub path is exactly what a missing instances key means.
                    if (partsById.TryGetValue(id, out var partEl)
                        && partEl.TryGetProperty("info", out var info)
                        && info.TryGetProperty("instances", out var instances))
                    {
                        foreach (var fid in BuildFixtures(doc, instances, lvl0))
                        { Record(id, "fixture", fid); created.Add(fid); }
                    }
                }
                else if (id == "stairs.main")
                {
                    // Deferred (see deferredStairs' own comment above) —
                    // nothing built here, so this part's ROW from this pass
                    // is a placeholder the deferred loop below overwrites via
                    // SetScore, same as roof.* rows are.
                    deferredStairs[id] = expected;
                }
                else
                {
                    throw new ArgumentException(
                        $"unknown part id '{id}' — this addin does not know how to build it");
                }
                return created;
            }

            using var tg = new TransactionGroup(doc, repairOf != null ? "BINA: repair design"
                                                                       : "BINA: build design");
            tg.Start();
            JsonArray scorecard;
            string specId;
            string? roofWarning = null;
            string? roofStrategy = null;
            var undoFailures = new List<string>();
            // Parts this round REFUSES to touch — a legacy spec whose bare
            // role bucket (slab/roof, pre owns_by_part) is shared by a
            // sibling part outside the subset, so clearing it would destroy
            // that sibling's geometry. Never built this round; the scorecard
            // says so honestly instead of silently going quiet on them.
            var refusedParts = new List<(string PartId, string Reason)>();
            JsonElement partsToRun = partsJson;
            try
            {
                // ── before the loop: preconditions no part describes ──────
                using (var txPrep = new Transaction(doc, "BINA: prepare design"))
                {
                    TxGuard.StartSwallowing(txPrep);
                    try
                    {
                        // Repair only: clear the subset's PRIOR elements before
                        // anything is rebuilt, or the new parts land on top of
                        // the broken ones instead of replacing them.
                        //
                        // Preferred path: owns_by_part carries this part's OWN
                        // element ids (recorded by every build since this
                        // field shipped) — always safe, since it can never
                        // contain a sibling volume's elements.
                        //
                        // Fallback: a spec saved before owns_by_part existed
                        // only has the bare-role `owns` map, which for
                        // slab/roof is shared by every volume (RoleForPart
                        // collapses "slab.porch" and "slab.main" onto one
                        // "slab" bucket). Clearing it is only safe when the
                        // subset covers every sibling part that could have
                        // fed that bucket — otherwise the part is REFUSED,
                        // not rebuilt, so the repair never destroys geometry
                        // it wasn't asked to touch and never lies about it.
                        if (repairOf != null && priorOwns != null)
                        {
                            var subsetIds = partsJson.EnumerateArray()
                                .Select(p => p.TryGetProperty("id", out var pidv)
                                    ? pidv.GetString() : null)
                                .Where(id => !string.IsNullOrEmpty(id))
                                .Select(id => id!)
                                .ToHashSet(StringComparer.Ordinal);

                            var toClear = new List<ElementId>();
                            foreach (var partId in subsetIds)
                            {
                                if (priorOwnsByPart != null
                                    && priorOwnsByPart.TryGetValue(partId, out var own))
                                {
                                    toClear.AddRange(own.Select(ElemIds.From)
                                                         .Where(eid => doc.GetElement(eid) != null));
                                    continue;
                                }

                                var role = RoleForPart(partId);
                                if (role == null || !priorOwns.TryGetValue(role, out var ids)) continue;

                                var siblings = LegacySharedRoleSiblings(role, vols);
                                if (siblings.Count > 0 && !siblings.All(subsetIds.Contains))
                                {
                                    refusedParts.Add((partId,
                                        $"repair would destroy sibling {role} geometry from an "
                                        + "older build — rebuild fully"));
                                    continue;
                                }
                                toClear.AddRange(ids.Select(ElemIds.From)
                                                     .Where(eid => doc.GetElement(eid) != null));
                            }
                            // Two subset parts can share one legacy bucket (the
                            // whole reason for the sibling-coverage check above)
                            // and so add the same ids twice — dedupe before the
                            // API call rather than lean on Revit tolerating it.
                            toClear = toClear.Distinct().ToList();
                            if (toClear.Count > 0)
                            {
                                try { doc.Delete(toClear); }
                                catch (Exception e)
                                {
                                    var survivors = toClear.Count(eid => doc.GetElement(eid) != null);
                                    if (survivors > 0)
                                        undoFailures.Add($"undo incomplete before repair: {survivors} "
                                                        + $"elements remain ({e.Message})");
                                }
                            }
                            if (refusedParts.Count > 0)
                            {
                                var refusedIds = refusedParts.Select(r => r.PartId)
                                    .ToHashSet(StringComparer.Ordinal);
                                var kept = new JsonArray();
                                foreach (var part in partsJson.EnumerateArray())
                                {
                                    var pid = part.TryGetProperty("id", out var pidv2)
                                        ? pidv2.GetString() : null;
                                    if (pid != null && refusedIds.Contains(pid)) continue;
                                    kept.Add(JsonNode.Parse(part.GetRawText()));
                                }
                                // Not disposed: the JsonElement must outlive this
                                // block (PartLoop.Run reads it further down) — a
                                // one-off allocation on an infrequent repair call.
                                partsToRun = JsonDocument.Parse(kept.ToJsonString()).RootElement;
                            }
                        }

                        // Activation forces a regeneration, so batch it: every
                        // symbol once, here, rather than one per placement.
                        foreach (var sym in new[] { doorSym, winSym }.Where(s => s != null && !s!.IsActive))
                            sym!.Activate();
                        doc.Regenerate();

                        levels.AddRange(EnsureLevels(doc, count, f2f, prefix, Own));
                        // Repair rebuilds PARTS only — level stack, curtain-type
                        // dressing and the structural grid/columns are none of
                        // them a part, so a repair call must never redo them: the
                        // grid and column code below carries no existence check
                        // and would mint a second copy on every repair round.
                        if (repairOf == null)
                        {
                            if (curtainType != null) DressCurtainType(doc, curtainType, facadeSpec);
                            BuildStructure(doc, args, footprint, levels[0], Own);
                        }
                        TxGuard.CommitOrThrow(txPrep);
                    }
                    catch
                    {
                        if (txPrep.GetStatus() == TransactionStatus.Started) txPrep.RollBack();
                        throw;
                    }
                }

                // Repair only: seed the spec-id -> element lookup from the PRIOR
                // build so doors/windows in the subset can host on walls that are
                // not themselves being rebuilt this round. Filtered to ids that
                // still resolve — a wall the delete pass above just cleared (or
                // one gone for any other reason) must not hand out a dead host.
                if (priorWallBySpecId != null)
                    foreach (var kv in priorWallBySpecId)
                    {
                        var eid = ElemIds.From(kv.Value);
                        if (doc.GetElement(eid) != null) wallBySpecId[kv.Key] = eid;
                    }

                // ── the loop ──────────────────────────────────────────────
                // partsToRun EXCLUDES refused parts (see the clear pass above)
                // — a refused part is never handed to BuildOnePart, so it
                // never lands geometry on top of the sibling-owned elements
                // this round declined to clear.
                foreach (var p in partsToRun.EnumerateArray())
                    if (p.TryGetProperty("id", out var pidv3) && pidv3.ValueKind == JsonValueKind.String)
                        partsById[pidv3.GetString()!] = p;
                var freshCard = PartLoop.Run(doc, partsToRun, BuildOnePart);
                if (priorScorecard != null)
                {
                    // Merge: replace rows by part id for the parts this round
                    // rebuilt, keep every other row exactly as the prior build
                    // left it — SetScore already implements replace-or-append.
                    scorecard = priorScorecard;
                    foreach (var row in freshCard.OfType<JsonObject>())
                        SetScore(scorecard, row["part"]!.GetValue<string>(),
                                 row["status"]!.GetValue<string>(),
                                 row["predicted"]?.GetValue<string>() ?? "",
                                 row["measured"]?.GetValue<string>() ?? "");
                    // A refused part's OLD row (whatever it said before this
                    // round) must never survive unchanged looking like "ok" —
                    // overwrite it with the honest refusal instead.
                    foreach (var (partId, reason) in refusedParts)
                        SetScore(scorecard, partId, "failed", "", reason);
                }
                else
                {
                    scorecard = freshCard;
                    foreach (var (partId, reason) in refusedParts)
                        SetScore(scorecard, partId, "failed", "", reason);
                }

                // ── roofs, outside any open transaction ───────────────────
                foreach (var kv in deferredRoofs)
                {
                    var (vol, expected) = kv.Value;
                    if (roofType == null)
                    {
                        SetScore(scorecard, kv.Key, "failed", expected.ToString(),
                                 "this project has no footprint-capable roof type");
                        roofWarning = "this project has no footprint-capable roof type, so no roof "
                                    + "was created. Everything else was built.";
                        continue;
                    }
                    // The eaves. A roof boundary pushed outward is the single
                    // strongest cue that a roof is a roof. Measurement is a
                    // containment check, so a roof built WITHOUT the overhang
                    // still passes — it is only caught when the part's plan
                    // extents were predicted with the overhang and compared
                    // tightly, which is why the offset is applied here, not
                    // trusted to the scorecard.
                    var boundary = Volumes.WithEaves(vol.Outline, overhangFt);
                    // The PRIMARY volume's roof bears on the TOP OCCUPIED
                    // level regardless of its own height_mm — `count` IS that
                    // level: EnsureLevels' levels[count] is the "Roof" level,
                    // and design_preflight refuses a spec where the primary's
                    // program does not populate every storey 1..count, so
                    // `count` always equals the backend's own `top_level`
                    // (design_parts.py). Every OTHER volume (porch, garage) is
                    // a single-storey add-on no matter how tall the house
                    // gets, so it keeps today's own-height rule at level 1 —
                    // unchanged for a single-storey build, where levels[1] ==
                    // levels[count] anyway (rung 4, task 8).
                    //
                    // A part shorter than the storey carries its roof at its own
                    // height, not the top of the house. It stays on level 0 (the
                    // level stack has no level at 2700) and is lifted by a base
                    // offset instead — building it AT level 0 would put a porch
                    // roof on the ground, which is what the part predicts against.
                    var isPrimaryRoof = string.Equals(vol.Role, primaryRole,
                                                      StringComparison.OrdinalIgnoreCase);
                    // I2 (final review 2026-08-13): a SINGLE-STOREY primary
                    // volume whose own height_mm is set below the storey
                    // height is "short" exactly like a porch/garage would
                    // be — its roof belongs at its own wall top, not at
                    // levels[count]. For count==1, levels[count] ==
                    // levels[1], the SAME elevation as f2f, which sits
                    // above the volume's own (shorter) walls — the
                    // isPrimaryRoof branch below used to send it there with
                    // NO offset, opening a gap between wall top and roof
                    // that the old, pre-rung-4 code (roof at the wall's own
                    // top) never had. A multi-storey (count>=2) primary
                    // volume is never "short" this way — `count` IS its top
                    // occupied level regardless of its own height_mm, by
                    // design (comment above) — so this only ever fires for
                    // count==1.
                    var shortPrimary = isPrimaryRoof && count == 1
                                       && vol.HeightFt > TOL && vol.HeightFt < f2f - TOL;
                    var shortVol = !isPrimaryRoof && vol.HeightFt > TOL && vol.HeightFt < f2f - TOL;
                    var roofLevel = shortPrimary ? levels[0]
                                    : isPrimaryRoof ? levels[count]
                                    : (shortVol ? levels[0] : levels[1]);
                    var res = RoofBuilder.Build(doc, boundary, roofLevel, roofType,
                                                roofKind == "flat" ? null : roofPitch,
                                                null, roofKind,
                                                (shortVol || shortPrimary) ? vol.HeightFt : 0);
                    if (!res.Ok)
                    {
                        SetScore(scorecard, kv.Key, "failed", expected.ToString(),
                                 "roof could not be created: "
                                 + string.Join(" | ", res.Attempts));
                        roofWarning = "the roof could not be created. Attempts: "
                            + string.Join(" | ", res.Attempts)
                            + $". Type '{roofType.Name}' (family '{roofType.FamilyName}'). "
                            + "Everything else was built — tell the drafter the roof is missing "
                            + "and offer Architecture > Roof by Footprint. Do NOT describe this "
                            + "as a finished shell.";
                        continue;
                    }
                    // lift handled inside RoofBuilder now — extrusion roofs draw it
                    // into the profile, footprint roofs take the offset param.
                    Record(kv.Key, "roof", res.Id!);
                    roofStrategy ??= res.Strategy;
                    var measured = PartMeasure.Measure(doc, kv.Key, expected,
                                                       new List<ElementId> { res.Id! });
                    // The strategy + attempt log ride IN the scorecard line, so
                    // a fallback roof explains itself in the reply — chasing
                    // "why extrusion?" through Langfuse cost a morning on
                    // 2026-08-11; never again.
                    var story = res.Strategy ?? "";
                    if (res.Attempts.Count > 0)
                        story += (story.Length > 0 ? " | " : "")
                              + string.Join(" | ", res.Attempts);
                    if (story.Length > 0)
                        measured.Measured = (string.IsNullOrEmpty(measured.Measured)
                                                 ? "" : measured.Measured + " — ") + story;
                    SetScore(scorecard, kv.Key, measured.Status, measured.Predicted, measured.Measured);
                }

                // ── stairs, outside any open transaction (StairsEditScope
                //    owns its own — see deferredStairs' own comment above) ──
                foreach (var kv in deferredStairs)
                {
                    var stairPartId = kv.Key;
                    var expected = kv.Value;
                    // Honest failure (missing info, a level out of range, a
                    // rect too small for the rise, the edit scope itself
                    // refusing): never let this crash the rest of the build —
                    // the row this writes IS the failed part, same contract
                    // every other branch in this method upholds.
                    try
                    {
                        if (!partsById.TryGetValue(stairPartId, out var partEl)
                            || !partEl.TryGetProperty("info", out var info)
                            || !info.TryGetProperty("rect_mm", out var rect))
                            throw new InvalidOperationException(
                                $"part '{stairPartId}', but the spec carries no stair "
                                + "geometry (info.rect_mm) — the backend must ship the "
                                + "reserved rect before this addin can build it");

                        double RectNum(string k) => rect.TryGetProperty(k, out var v)
                                                     && v.ValueKind == JsonValueKind.Number
                            ? v.GetDouble()
                            : throw new InvalidOperationException(
                                $"part '{stairPartId}' rect_mm has no '{k}'");
                        // info.rect_mm ships in the SITE frame, like every
                        // other part's geometry (design_preflight._shift_stair,
                        // rung 4 task 9 backend fix) — no shift needed here.
                        var xFt = RectNum("x_mm") / FT;
                        var yFt = RectNum("y_mm") / FT;
                        var wFt = RectNum("w_mm") / FT;
                        var hFt = RectNum("h_mm") / FT;
                        if (wFt <= TOL || hFt <= TOL)
                            throw new InvalidOperationException(
                                $"part '{stairPartId}' rect_mm is degenerate — the "
                                + "reserved rect has no area");

                        int LevelNum(string k) => info.TryGetProperty(k, out var v)
                                                   && v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : throw new InvalidOperationException(
                                $"part '{stairPartId}' info has no '{k}'");
                        var fromLevel = LevelNum("from_level");
                        var toLevel = LevelNum("to_level");
                        if (fromLevel < 1 || fromLevel > levels.Count
                            || toLevel < 1 || toLevel > levels.Count)
                            throw new InvalidOperationException(
                                $"part '{stairPartId}' names levels {fromLevel}->{toLevel}, "
                                + $"outside the {levels.Count} levels this build has");
                        var stairBaseLevel = levels[fromLevel - 1];
                        var stairTopLevel = levels[toLevel - 1];

                        var built = Mutators.BuildStairsFromRect(
                            doc, stairBaseLevel, stairTopLevel, xFt, yFt, wFt, hFt);

                        Record(stairPartId, "stairs", built.StairsId);
                        var measured = PartMeasure.Measure(doc, stairPartId, expected,
                                                           new List<ElementId> { built.StairsId });
                        SetScore(scorecard, stairPartId, measured.Status,
                                 measured.Predicted, measured.Measured);
                    }
                    catch (Exception e)
                    {
                        SetScore(scorecard, stairPartId, "failed", expected.ToString(),
                                 $"stairs could not be created: {e.Message}");
                    }
                }

                // ── after the loop: storeys above ground, rooms, the spec ──
                using (var txFin = new Transaction(doc, "BINA: rooms and spec"))
                {
                    TxGuard.StartSwallowing(txFin);
                    try
                    {
                        // Upper storeys are no longer built here — they arrive
                        // as parts (`slab.l<k>`, `walls.<role>.l<k>`, rung 4
                        // task 8) and are built, measured and owned inside
                        // PartLoop above like every other part; the standalone
                        // unmeasured loop that used to live here is deleted.
                        // Separation lines and rooms are still NOT parts —
                        // there is no part id PartLoop could mark ok/failed
                        // for them — so a repair round must never redo this
                        // block: nothing here checks for what already exists,
                        // and re-running it on the SAME args a repair carries
                        // would mint a second copy of every separation line
                        // and room on top of the ones the original build
                        // already made. Room tagging and separations also stay
                        // GROUND-FLOOR ONLY this rung — an upper storey's rooms
                        // are out of rung-4 scope; the wet-room stub
                        // (`fixtures.<room>.l<k>`) is what keeps them from
                        // reading as silently absent instead.
                        if (repairOf == null)
                        {
                        // Open-plan edges get a ROOM SEPARATION LINE — no wall
                        // by design, but without a boundary Revit merges the
                        // open pair into one enclosure and both rooms lose
                        // their area (2026-08-11: Ruang Tamu + Ruang Makan).
                        var seps = Obj(args, "separations");
                        if (seps != null && seps.Value.ValueKind == JsonValueKind.Array)
                        {
                            var planView = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                                .FirstOrDefault(v => !v.IsTemplate
                                                     && v.GenLevel?.Id == levels[0].Id);
                            if (planView != null)
                            {
                                var curves = new CurveArray();
                                foreach (var s in seps.Value.EnumerateArray())
                                {
                                    var pa = ArgsHelp.GetPointMm(s, "a_mm");
                                    var pb = ArgsHelp.GetPointMm(s, "b_mm");
                                    if (pa == null || pb == null) continue;
                                    var z = levels[0].Elevation;
                                    curves.Append(Line.CreateBound(
                                        new XYZ(pa.X, pa.Y, z),
                                        new XYZ(pb.X, pb.Y, z)));
                                }
                                if (curves.Size > 0)
                                {
                                    var sk = SketchPlane.Create(doc,
                                        Plane.CreateByNormalAndOrigin(XYZ.BasisZ,
                                            new XYZ(0, 0, levels[0].Elevation)));
                                    var lines = doc.Create.NewRoomBoundaryLines(
                                        sk, curves, planView);
                                    foreach (ModelCurve mc in lines)
                                        Own("separation", mc.Id.Value);
                                }
                            }
                        }

                        // Rooms are tagged from the SOLVED rectangles, after the
                        // partitions exist — a room cannot find its boundary
                        // until the walls enclosing it have been regenerated.
                        var solvedRooms = Obj(args, "rooms");
                        if (solvedRooms != null && solvedRooms.Value.ValueKind == JsonValueKind.Array)
                        {
                            doc.Regenerate();
                            foreach (var r in solvedRooms.Value.EnumerateArray())
                            {
                                double G(string k) => r.TryGetProperty(k, out var v)
                                    && v.ValueKind == JsonValueKind.Number ? v.GetDouble() / FT : 0;
                                var cx = G("x_mm") + G("w_mm") / 2;
                                var cy = G("y_mm") + G("h_mm") / 2;
                                var room = doc.Create.NewRoom(levels[0], new UV(cx, cy));
                                if (room == null) continue;
                                try
                                {
                                    room.Name = r.TryGetProperty("name", out var nv)
                                        ? nv.GetString() ?? "Room" : "Room";
                                }
                                catch { }
                                Own("room", room.Id.Value);
                            }
                        }
                        }   // repairOf == null

                        // A failed part is DELETED by PartLoop, so the ownership
                        // map must be pruned before it is stored — a spec that
                        // points at dead ids makes the next update_design delete
                        // whatever inherited them.
                        foreach (var role in owns.Keys.ToList())
                            owns[role] = owns[role]
                                .Where(id => doc.GetElement(ElemIds.From(id)) != null).ToList();

                        // Per-part ownership: start from what this round
                        // inherited, overlay whatever it actually rebuilt (only
                        // parts BuildOnePart ran for this round have an entry
                        // in ownedByPart — see ForgetPart/Record), then prune
                        // dead ids the same way `owns` is pruned above. A part
                        // this round REFUSED or left untouched keeps its prior
                        // entry unchanged, so a later repair round still finds
                        // it under its own key next time.
                        var ownsByPart = priorOwnsByPart != null
                            ? priorOwnsByPart.ToDictionary(kv => kv.Key, kv => new List<long>(kv.Value))
                            : new Dictionary<string, List<long>>();
                        foreach (var kv in ownedByPart)
                            ownsByPart[kv.Key] = kv.Value.Select(t => t.Id).ToList();
                        foreach (var partId in ownsByPart.Keys.ToList())
                            ownsByPart[partId] = ownsByPart[partId]
                                .Where(id => doc.GetElement(ElemIds.From(id)) != null).ToList();

                        // A repair keeps the ORIGINAL spec_id (read from the
                        // stored spec, NOT `repairOf` — that is a per-round
                        // scorecard fingerprint the backend mints fresh every
                        // round, never a stable id; see priorSpecId above). It
                        // is the same building, only some of its parts
                        // rebuilt, and a fresh id here would orphan every
                        // prior scorecard row this round did not touch.
                        specId = priorSpecId ?? Guid.NewGuid().ToString("N").Substring(0, 12);
                        // scorecard + wall_by_spec_id + owns_by_part ride along
                        // so a LATER repair round (another failed subset) can
                        // load them back — the FULL prior scorecard to merge
                        // into, the spec-id -> element lookup so doors/windows
                        // it does not touch can still resolve the walls they
                        // are hosted in, and the per-part map so THAT round's
                        // clear stays precise too, not just this one's.
                        SaveJson(doc, JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["spec_id"] = specId,
                            ["version"] = 1,
                            ["args"] = JsonSerializer.Deserialize<object>(args.GetRawText()),
                            ["owns"] = owns,
                            ["owns_by_part"] = ownsByPart,
                            ["scorecard"] = scorecard,
                            ["wall_by_spec_id"] = wallBySpecId.ToDictionary(kv => kv.Key, kv => kv.Value.Value),
                        }));
                        TxGuard.CommitOrThrow(txFin);
                    }
                    catch
                    {
                        if (txFin.GetStatus() == TransactionStatus.Started) txFin.RollBack();
                        throw;
                    }
                }

                tg.Assimilate();      // one Ctrl+Z puts the whole building back
            }
            catch
            {
                if (tg.GetStatus() == TransactionStatus.Started) tg.RollBack();
                throw;
            }

            // The scorecard is the ONLY evidence the reply may narrate from, so
            // anything short of every part measuring ok is said out loud here
            // too — a warning the model cannot miss.
            var bad = scorecard.OfType<JsonObject>()
                .Where(o => (o["status"]?.GetValue<string>() ?? "") != "ok")
                .Select(o => $"{o["part"]?.GetValue<string>()}: {o["status"]?.GetValue<string>()}")
                .ToList();
            var warning = roofWarning;
            // Repair's own delete pass (clearing the subset's prior elements
            // before rebuilding) is best-effort like PartLoop's own undo — a
            // survivor here means the model must be told, not left to imply a
            // clean repair.
            if (undoFailures.Count > 0)
                warning = (warning == null ? "" : warning + " ") + string.Join(" ", undoFailures);
            if (bad.Count > 0)
                warning = (warning == null ? "" : warning + " ")
                    + $"{bad.Count} part(s) did not verify: {string.Join(", ", bad)}. "
                    + "Say what is missing; do not describe this build as complete.";

            t0.Stop();

            // The scorecard goes INSIDE the digest as well as beside it. The
            // acceptance checklist (evals/checklist_bungalow.py) reads every
            // fact it judges off the digest envelope — including
            // digest["scorecard"] — so a scorecard that was only ever a
            // sibling left four criteria permanently "unknown" no matter how
            // the build actually went. The sibling stays for callers that
            // already read it (update_design's report copies it).
            var digest = Inspectors.GetGeometryDigest(doc, args);
            digest["scorecard"] = scorecard;

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["spec_id"] = specId,
                ["created"] = owns.ToDictionary(k => k.Key, v => (object?)v.Value.Count),
                ["new_ids"] = owns.SelectMany(k => k.Value).ToList(),
                ["scorecard"] = scorecard,
                ["roof_strategy"] = roofStrategy,
                ["warning"] = warning,
                ["elapsed_ms"] = t0.ElapsedMilliseconds,
                ["digest"] = digest,
            };
            if (repairOf != null) result["repair_of"] = repairOf;
            return result;
        }

        /// <summary>Doors, hosted BY ID in the wall the solver named.
        ///
        /// No fallback placement. A wall id that is not in the registry means
        /// the walls part did not build what the openings part was solved
        /// against, and putting the door somewhere plausible instead would hide
        /// exactly the mismatch this loop exists to catch.
        ///
        /// `doorsArr` is the solved door array to place from — L1's
        /// `args["doors"]` or an upper storey's own `info.doors` (rung 4,
        /// task 8b), both the same shape. `level` is the 1-based storey this
        /// call is placing for, used only to scope an interior `wall_id`
        /// lookup (see ScopedWallSpecId) — a door's own `wall_id` string is
        /// never altered.</summary>
        private static List<ElementId> PlaceDoors(Document doc, JsonElement? doorsArr, Level lvl,
                                                  int level, FamilySymbol? doorSym,
                                                  IReadOnlyDictionary<string, ElementId> wallBySpecId)
        {
            var created = new List<ElementId>();
            if (doorsArr == null || doorsArr.Value.ValueKind != JsonValueKind.Array) return created;
            // Activate HERE, inside this part's own transaction — the batched
            // txPrep activation can be undone by an intermediate rollback, and
            // Revit then fails NewFamilyInstance with "Can't make type ..."
            // (2026-08-11: 8 doors predicted, 0 built, exactly that error).
            if (doorSym != null && !doorSym.IsActive) { doorSym.Activate(); doc.Regenerate(); }
            if (doorsArr.Value.GetArrayLength() > 0 && doorSym == null)
                throw new InvalidOperationException(
                    "no door family is loaded in this project, so the solved doors cannot be placed");

            foreach (var d in doorsArr.Value.EnumerateArray())
            {
                var room = d.TryGetProperty("room", out var rn) ? rn.GetString() ?? "?" : "?";
                // A door with no wall_id is an UNSOLVED door. GetProperty would
                // throw "The given key was not present", which reads like an
                // addin bug; say which door and what is missing instead.
                if (!d.TryGetProperty("wall_id", out var wid)
                    || wid.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(wid.GetString()))
                    throw new InvalidOperationException(
                        $"door for {room} carries no wall_id — the backend must name the wall "
                        + "each opening is hosted in (design_preflight solves this)");
                var wallId = wid.GetString()!;
                if (!wallBySpecId.TryGetValue(ScopedWallSpecId(wallId, level), out var hostId)
                    || doc.GetElement(hostId) is not Wall host)
                    throw new InvalidOperationException(
                        $"door for {room} references wall '{wallId}' "
                        + "which was not built — check the walls part");
                var lc = (LocationCurve)host.Location;
                double at = (d.TryGetProperty("at_mm", out var am)
                             && am.ValueKind == JsonValueKind.Number ? am.GetDouble() : 0) / FT;
                var a = lc.Curve.GetEndPoint(0);
                var dir = (lc.Curve.GetEndPoint(1) - a).Normalize();
                var p = a + dir * at;                       // at_mm: from a toward b, centre
                var fi = doc.Create.NewFamilyInstance(
                    new XYZ(p.X, p.Y, lvl.Elevation), doorSym, host, lvl,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                created.Add(fi.Id);
            }
            return created;
        }

        /// <summary>Windows, hosted BY ID, with the solved sill. Same contract
        /// as PlaceDoors: an unknown wall id is a failure, never a guess.
        /// `windowsArr`/`level` follow PlaceDoors' own contract above.</summary>
        private static List<ElementId> PlaceWindows(Document doc, JsonElement? windowsArr, Level lvl,
                                                    int level, FamilySymbol? winSym,
                                                    double defaultSillFt,
                                                    IReadOnlyDictionary<string, ElementId> wallBySpecId)
        {
            var created = new List<ElementId>();
            if (winSym != null && !winSym.IsActive) { winSym.Activate(); doc.Regenerate(); }
            if (windowsArr == null || windowsArr.Value.ValueKind != JsonValueKind.Array) return created;
            if (windowsArr.Value.GetArrayLength() > 0 && winSym == null)
                throw new InvalidOperationException(
                    "no window family is loaded in this project, so the solved windows cannot be placed");

            foreach (var w in windowsArr.Value.EnumerateArray())
            {
                var room = w.TryGetProperty("room", out var rn) ? rn.GetString() ?? "?" : "?";
                // Same contract as PlaceDoors: a missing wall_id is a named
                // failure, not a KeyNotFoundException from deep in the addin.
                if (!w.TryGetProperty("wall_id", out var wid)
                    || wid.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(wid.GetString()))
                    throw new InvalidOperationException(
                        $"window for {room} carries no wall_id — the backend must name the wall "
                        + "each opening is hosted in (design_preflight solves this)");
                var wallId = wid.GetString()!;
                if (!wallBySpecId.TryGetValue(ScopedWallSpecId(wallId, level), out var hostId)
                    || doc.GetElement(hostId) is not Wall host)
                    throw new InvalidOperationException(
                        $"window for {room} references wall '{wallId}' "
                        + "which was not built — check the walls part");
                var lc = (LocationCurve)host.Location;
                double at = (w.TryGetProperty("at_mm", out var am)
                             && am.ValueKind == JsonValueKind.Number ? am.GetDouble() : 0) / FT;
                var a = lc.Curve.GetEndPoint(0);
                var dir = (lc.Curve.GetEndPoint(1) - a).Normalize();
                var p = a + dir * at;
                var fi = doc.Create.NewFamilyInstance(
                    new XYZ(p.X, p.Y, lvl.Elevation), winSym, host, lvl,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                var sill = w.TryGetProperty("sill_mm", out var sm)
                           && sm.ValueKind == JsonValueKind.Number ? sm.GetDouble() / FT : defaultSillFt;
                fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sill);
                created.Add(fi.Id);
            }
            return created;
        }

        /// <summary>Place one fixtures.&lt;room&gt; part's solved sanitary
        /// instances (`fixtures_solver.solve_fixtures`, Task 10): a wc, basin
        /// or shower per `instances[]`, at `xy_mm` on the ground floor,
        /// rotated about Z by `rotation_deg`.
        ///
        /// `kind` selects a symbol from OST_PlumbingFixtures by name hint —
        /// wc/basin/shower are drafter vocabulary, not Revit family names, so
        /// this tries a short list of plausible ones per kind. No match (or an
        /// empty category — no plumbing fixture family loaded at all) is a
        /// named failure, never a silent guess at which loaded family stands
        /// in for a toilet: PartLoop turns the exception into the part's
        /// failed row, naming the missing kind.</summary>
        private static List<ElementId> BuildFixtures(Document doc, JsonElement instances, Level lvl0)
        {
            var created = new List<ElementId>();
            if (instances.ValueKind != JsonValueKind.Array) return created;

            var symByKind = new Dictionary<string, FamilySymbol>(StringComparer.Ordinal);
            FamilySymbol SymbolFor(string kind)
            {
                if (symByKind.TryGetValue(kind, out var cached)) return cached;
                var hints = kind switch
                {
                    "wc" => new[] { "Tandas Duduk", "Tandas", "Toilet", "WC", "Water Closet", "_TDS_" },
                    "basin" => new[] { "Basin", "Besen", "Sink", "Lavatory", "Wash", "_BSN_", "_LSB_" },
                    "shower" => new[] { "Shower", "Pancuran", "_SWR_" },
                    _ => Array.Empty<string>(),
                };
                // SUBSTRING match, not FindSymbol's exact-equality: real fleet
                // families carry the whole JKR code in the family name
                // ("jkrAR18_plb-fx_LSw_TDS_tk01_(LSw952)-3 Tandas Duduk"), so
                // an exact hint like "Tandas Duduk" can never equal it — the
                // 2026-08-13 live loop shipped "no wc family loaded" with the
                // family sitting right there in the document.
                var candidates = new FilteredElementCollector(doc)
                    .WhereElementIsElementType()
                    .OfCategory(BuiltInCategory.OST_PlumbingFixtures)
                    .Cast<FamilySymbol>().ToList();
                if (candidates.Count == 0)
                    throw new InvalidOperationException(
                        $"no {(string.IsNullOrEmpty(kind) ? "fixture" : kind)} family loaded — load a plumbing fixture family first");
                FamilySymbol? sym = null;
                foreach (var hint in hints)
                {
                    sym = candidates.FirstOrDefault(s =>
                        (s.FamilyName ?? "").IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0
                        || (s.Name ?? "").IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (sym != null) break;
                }
                if (sym == null)
                    throw new InvalidOperationException(
                        $"no plumbing fixture family matches '{(string.IsNullOrEmpty(kind) ? "fixture" : kind)}' — loaded: "
                        + string.Join(", ", candidates.Take(6).Select(s => $"{s.FamilyName} : {s.Name}")));
                // Activate HERE, inside this part's own transaction — same
                // rollback hazard PlaceDoors guards against: a batched
                // txPrep activation can be undone by an intermediate
                // rollback, and NewFamilyInstance then fails with
                // "Can't make type ...".
                if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                symByKind[kind] = sym;
                return sym;
            }

            foreach (var inst in instances.EnumerateArray())
            {
                var kind = inst.TryGetProperty("kind", out var kv) ? kv.GetString() ?? "" : "";
                var sym = SymbolFor(kind);
                var p = PointMm(inst, "xy_mm", lvl0.Elevation);
                var fi = doc.Create.NewFamilyInstance(
                    p, sym, lvl0, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                var rotDeg = inst.TryGetProperty("rotation_deg", out var rv)
                             && rv.ValueKind == JsonValueKind.Number ? rv.GetDouble() : 0;
                if (Math.Abs(rotDeg) > 1e-9)
                    ElementTransformUtils.RotateElement(
                        doc, fi.Id, Line.CreateBound(p, p + XYZ.BasisZ), rotDeg * Math.PI / 180.0);
                created.Add(fi.Id);
            }
            return created;
        }

        /// <summary>Join a freshly built partition to every wall it meets.
        ///
        /// Without this a partition reads as a separate object butting into the
        /// wall it should merge with — no cleaned junction in plan, which is the
        /// first thing a drafter looks at. 50mm is the same "touching" tolerance
        /// PartMeasure counts open ends with, so a join and a closed end mean the
        /// same thing on both sides.</summary>
        private static void JoinToNeighbours(Document doc, Wall wall, IEnumerable<ElementId> candidates)
        {
            if (wall.Location is not LocationCurve mine) return;
            foreach (var otherId in candidates.ToList())
            {
                if (doc.GetElement(otherId) is not Wall other || other.Id == wall.Id) continue;
                if (other.Location is not LocationCurve olc) continue;
                for (int end = 0; end <= 1; end++)
                {
                    if (olc.Curve.Distance(mine.Curve.GetEndPoint(end)) >= 50 / FT) continue;
                    try { JoinGeometryUtils.JoinGeometry(doc, wall, other); }
                    catch { /* already joined, or Revit refused — not worth a failure */ }
                    break;
                }
            }
        }

        /// <summary>An [x, y] pair in mm from the solved plan, at a level.</summary>
        private static XYZ PointMm(JsonElement obj, string name, double z)
        {
            var v = obj.GetProperty(name);
            var xs = v.EnumerateArray().Select(n => n.GetDouble()).ToList();
            if (xs.Count < 2)
                throw new ArgumentException($"{name} must be [x, y] in mm");
            return new XYZ(xs[0] / FT, xs[1] / FT, z);
        }

        /// <summary>Raise a roof to a short volume's own height with a base
        /// offset, since the level stack has no level at (say) 2700.
        ///
        /// Best effort BY DESIGN: RoofBuilder may have fallen back to an
        /// extrusion, which carries no "Base Offset From Level". When the offset
        /// does not take, the roof stays where it was built and the part's
        /// prediction — which puts it at the volume's height — FAILS on the
        /// scorecard. Never silently pass.</summary>
        private static void LiftRoof(Document doc, ElementId roofId, double offsetFt)
        {
            if (offsetFt <= TOL) return;
            try
            {
                using var tx = new Transaction(doc, "BINA: raise roof to volume height");
                TxGuard.StartSwallowing(tx);
                try
                {
                    var roof = doc.GetElement(roofId);
                    var p = roof?.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)
                            ?? roof?.LookupParameter("Base Offset From Level");
                    if (p != null && !p.IsReadOnly) p.Set(offsetFt);
                    TxGuard.CommitOrThrow(tx);
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    throw;
                }
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BinaVibe][roof] base offset {offsetFt * FT:F0}mm not applied: {e.Message}");
            }
        }

        /// <summary>Overwrite one part's scorecard line (or append it if the
        /// loop never wrote one). Used by the roof pass, which measures after
        /// PartLoop has already moved on.</summary>
        private static void SetScore(JsonArray card, string partId, string status,
                                     string predicted, string measured)
        {
            var entry = new JsonObject
            {
                ["part"] = partId, ["status"] = status,
                ["predicted"] = predicted, ["measured"] = measured,
            };
            for (int i = 0; i < card.Count; i++)
                if (card[i] is JsonObject o && o["part"]?.GetValue<string>() == partId)
                { card[i] = entry; return; }
            card.Add(entry);
        }

        // ─── shared between both build paths ────────────────────────────

        /// <summary>The level stack: the project's lowest level, one level per
        /// storey, plus one extra at the top for the roof to bear on.</summary>
        private static List<Level> EnsureLevels(Document doc, int count, double f2f,
                                                string prefix, Action<string, long> own)
        {
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
                    lvl.Pinned = true;   // datums pin at birth (field-guide guardrail)
                    try { lvl.Name = i == count ? "Roof" : $"{prefix}{i + 1}"; } catch { }
                    own("level", lvl.Id.Value);
                }
                levels.Add(lvl);
            }
            return levels;
        }

        /// <summary>Curtain grid + mullions, set on the TYPE so one write
        /// dresses every panel of every floor.</summary>
        private static void DressCurtainType(Document doc, WallType curtainType,
                                             JsonElement? facadeSpec)
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
            if (mull == null) return;
            // Both groups: Revit gives vertical and horizontal mullions
            // parameters that share a display name, so a generic set-parameter
            // call can only ever reach one.
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

        /// <summary>Structural grid + columns from `structure`. No-op when the
        /// spec does not ask for them.</summary>
        private static void BuildStructure(Document doc, JsonElement args, List<XYZ> footprint,
                                           Level lvl0, Action<string, long> own)
        {
            var structSpec = Obj(args, "structure");
            if (structSpec == null) return;

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
                g.Pinned = true;   // datums pin at birth (field-guide guardrail)
                own("grid", g.Id.Value);
            }
            for (int j = 0; j <= yb; j++)
            {
                var y = minY + j * ys;
                var g = Grid.Create(doc, Line.CreateBound(new XYZ(minX, y, 0), new XYZ(maxX, y, 0)));
                g.Pinned = true;
                own("grid", g.Id.Value);
            }
            var colSym = FindSymbol(doc, BuiltInCategory.OST_StructuralColumns,
                                    Str(Obj(structSpec.Value, "columns"), "type_name"));
            if (colSym == null || xb <= 0 || yb <= 0) return;
            if (!colSym.IsActive) { colSym.Activate(); doc.Regenerate(); }
            for (int i = 0; i <= xb; i++)
                for (int j = 0; j <= yb; j++)
                {
                    var pt = new XYZ(minX + i * xs, minY + j * ys, lvl0.Elevation);
                    var col = doc.Create.NewFamilyInstance(
                        pt, colSym, lvl0, Autodesk.Revit.DB.Structure.StructuralType.Column);
                    own("column", col.Id.Value);
                }
        }

        // ─── the legacy path (no `parts` in the spec) ───────────────────

        /// <summary>Build a whole design from a spec, in ONE transaction.
        ///
        /// This is the pre-2026-08-10 build, kept verbatim for a backend that
        /// does not solve the plan yet. It still carries the heuristics the
        /// solved path replaced — the door in the corridor-side partition, the
        /// blanket window spacing — because for an UNSOLVED spec they are the
        /// only answer available, and a shell (a tower asking for an envelope)
        /// legitimately has no program to solve.
        ///
        /// Regenerations are minimised, not eliminated: family symbols must be
        /// activated and the document regenerated before instances can be
        /// placed, and rooms cannot find their boundaries until the walls
        /// enclosing them have been regenerated. Both are batched — activate
        /// every symbol once up front, regenerate once before rooms.</summary>
        private static Dictionary<string, object?> BuildLegacy(Document doc, JsonElement args,
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
                var levels = EnsureLevels(doc, count, f2f, prefix, Own);

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
                if (curtainType != null) DressCurtainType(doc, curtainType, facadeSpec);

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
                BuildStructure(doc, args, footprint, levels[0], Own);

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

            // Per-PART ownership (see RepairFromParts' own loading of this —
            // same field, same shape). Every path below that persists the
            // spec must carry this forward, or a later repair_of round
            // degrades to the legacy bare-role fallback (RoleForPart) and
            // refuses any part whose role bucket is shared by a sibling.
            // Levels and type-swap carry it unchanged (nothing is deleted
            // or created); footprint-stretch prunes and re-attaches it
            // (see below); the full-rebuild path at the bottom needs no
            // handling here — it deletes every owned id first and lets
            // BuildDesign compute a fresh map from scratch.
            var ownsByPart = new Dictionary<string, List<long>>();
            if (root.TryGetProperty("owns_by_part", out var obpEl)
                && obpEl.ValueKind == JsonValueKind.Object)
                foreach (var p in obpEl.EnumerateObject())
                    ownsByPart[p.Name] = p.Value.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.Number)
                        .Select(v => v.GetInt64()).ToList();

            // Merge: the caller sends only what changed.
            var merged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(oldArgs.GetRawText())
                         ?? new Dictionary<string, JsonElement>();

            // The solved output (`parts`, `rooms`, `partitions`, `doors`,
            // `windows`, `exterior_walls`) is deliberately NOT touched here. It
            // is dropped only on the full-rebuild path, at the bottom of this
            // method — a fast path rebuilds nothing, so it must persist the
            // stored spec exactly as it found it. Dropping the solved plan while
            // only nudging a level height would delete it from the .rvt for
            // good, and the NEXT update would demolish the building before
            // discovering it has no layout to rebuild from.
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
                    // `walls` is the wall-TYPE dict, and the type-swap fast path
                    // only makes sense for that. A transitional spec may still
                    // carry the solved partition ARRAY under this key; swapping
                    // exterior wall types because the partitions moved would be
                    // the wrong edit entirely, so that falls to a full rebuild.
                    case "walls":
                        roles.Add(merged.TryGetValue("walls", out var wallsVal)
                                  && wallsVal.ValueKind == JsonValueKind.Object
                            ? "__walltype__" : "__all__");
                        break;
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
                    // A parameter edit rebuilds nothing — every id stays put,
                    // so the per-part map carries forward unchanged.
                    ["owns_by_part"] = ownsByPart,
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

            // Wall TYPE change is a type swap — openings survive. Only reachable
            // when `walls` really is the type dict (see the switch above).
            if (roles.Contains("__walltype__") && roles.Count == 1
                && Obj(newArgs, "walls") is { ValueKind: JsonValueKind.Object })
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
                    // A type swap sets a property on the SAME wall ids — none
                    // are deleted or created, so the per-part map is untouched.
                    ["owns_by_part"] = ownsByPart,
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

                        // Per-part propagation: prune the ids `kill` just
                        // deleted, then re-attach the freshly created ones
                        // under the part id(s) that role belongs to — the
                        // same RoleForPart inverse RepairFromParts uses —
                        // so a repair after a footprint stretch still gets
                        // per-part scoping instead of degrading to the
                        // legacy bare-role refusal path.
                        var killedIds = kill.Select(e => (long)e.Value).ToHashSet();
                        var newOwnsByPart = ownsByPart.ToDictionary(
                            kv => kv.Key, kv => kv.Value.Where(id => !killedIds.Contains(id)).ToList());
                        void AttachCreated(string role, List<long> created)
                        {
                            if (created.Count == 0) return;
                            var partIds = ownsByPart.Keys
                                .Where(k => RoleForPart(k) == role)
                                .OrderBy(k => SplitLevel(k).level)
                                .ToList();
                            // Level-order pairing is only unambiguous when the
                            // prior per-part map names exactly as many parts
                            // for this role as it just rebuilt (the ordinary
                            // single-volume case, one slab per storey). A
                            // mismatch — a legacy spec with no owns_by_part
                            // entries at all, or a role shared by sibling
                            // volumes (RoleForPart's own docstring) — is left
                            // alone rather than guess a wrong pairing; the
                            // next repair on those parts falls back to the
                            // legacy bucket, same as before this change.
                            if (partIds.Count != created.Count) return;
                            for (int i = 0; i < created.Count; i++)
                            {
                                if (!newOwnsByPart.TryGetValue(partIds[i], out var list))
                                    newOwnsByPart[partIds[i]] = list = new List<long>();
                                list.Add(created[i]);
                            }
                        }
                        AttachCreated("slab", newOwns["slab"]);
                        AttachCreated("roof", newOwns["roof"]);

                        SaveJson(doc, JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["spec_id"] = root.GetProperty("spec_id").GetString(),
                            ["version"] = 1,
                            ["args"] = JsonSerializer.Deserialize<object>(mergedJson),
                            ["owns"] = newOwns,
                            ["owns_by_part"] = newOwnsByPart,
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
                        // The stored solved plan is KEPT (this path rebuilt no
                        // layout), but the outline it was solved against has
                        // moved, so say so rather than let the next prompt trust
                        // it. Nothing is deleted from the spec here.
                        ["warning"] = "the outline moved. Partitions and rooms were NOT re-solved, "
                                    + "so the stored layout no longer matches the walls — re-run "
                                    + "design_preflight before rebuilding anything from it.",
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
            // SOLVED OUTPUT DOES NOT SURVIVE A REBUILD — and this is the only
            // place it is dropped, because this is the only path that rebuilds.
            // `parts`, `rooms`, `partitions`, `doors`, `windows` and
            // `exterior_walls` are the backend's answer to the OLD spec:
            // absolute coordinates and bbox predictions for a building that,
            // after this edit, no longer exists. Rebuilding from them measures
            // the wrong house — the scorecard would compare a widened frontage
            // against yesterday's walls and report failures the drafter cannot
            // act on. Each stale key goes unless THIS call brings a fresh one.
            var droppedSolved = new List<string>();
            foreach (var k in new[] { "parts", "rooms", "partitions", "doors", "windows",
                                      "exterior_walls" })
            {
                if (!merged.ContainsKey(k)) continue;
                if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(k, out _)) continue;
                merged.Remove(k);
                droppedSolved.Add(k);
            }
            var rebuildJson = JsonSerializer.Serialize(merged);
            using var rebuildDoc = JsonDocument.Parse(rebuildJson);
            var rebuildArgs = rebuildDoc.RootElement;

            // …but not before checking the rebuild can actually succeed. The
            // test is on the STATE of the spec about to be built, not on what
            // this particular call dropped: a room program with no solved rooms
            // and no parts has nothing to build a layout FROM, whether this call
            // stripped them or an earlier one left the spec that way. BuildLegacy
            // rightly refuses to slice the footprint into equal strips — and
            // refusing HERE, before the delete transaction, is the difference
            // between an error the drafter can act on and a demolished building
            // followed by an error.
            bool Solved(string k) => merged.TryGetValue(k, out var v)
                && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0;
            if (merged.TryGetValue("program", out var progEl)
                && progEl.ValueKind == JsonValueKind.Array && progEl.GetArrayLength() > 1
                && !Solved("rooms") && !Solved("parts"))
                throw new InvalidOperationException(
                    "this design carries a room program but no solved layout to rebuild from "
                    + "(the stored plan described the previous spec, so it was not reused). "
                    + "NOTHING WAS DELETED — the building is untouched. Re-run design_preflight "
                    + "on the updated spec and send the fresh parts, partitions, exterior_walls, "
                    + "doors, windows and rooms with this call. "
                    // The recovery, named, because the refusal is otherwise a
                    // dead end the model retries verbatim: calling update_design
                    // again with footprint_mm AND program re-runs the solver
                    // backend-side, and the fresh plan rides in with the call.
                    + "Panggil update_design semula dan hantar semula footprint_mm + program "
                    + "supaya susun atur diselesaikan semula.");

            var toDelete = new List<ElementId>();
            var targetRoles = owns.Keys.ToList();
            foreach (var role in targetRoles)
                if (owns.TryGetValue(role, out var ids))
                    foreach (var id in ids)
                        if (doc.GetElement(ElemIds.From(id)) != null)
                        { toDelete.Add(ElemIds.From(id)); removed.Add(id); }

            // Delete-then-rebuild must be ATOMIC (I1, final review
            // 2026-08-13). This used to commit the delete as its OWN
            // standalone transaction, then call BuildDesign — which opens
            // its own TransactionGroup only around the REBUILD. An
            // interrupt between the two (a thrown exception inside
            // BuildDesign, observed live as the "floating box"/demolished-
            // site incident on an add-storey) rolled the rebuild back but
            // left the demolition committed: a permanently gone building
            // with nothing rebuilt in its place. Revit TransactionGroups
            // nest, so wrapping the delete AND the call to BuildDesign (which
            // opens its own, nested, group) in ONE outer group here means an
            // interrupt anywhere in the rebuild rolls the demolition back
            // too — one Ctrl+Z, either the old house or the new one, never
            // neither.
            using var outerTg = new TransactionGroup(doc, "BINA: add storey (delete + rebuild)");
            outerTg.Start();
            try
            {
                using (var tx3 = new Transaction(doc, "BINA: update design"))
                {
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
                }

                // Rebuild from the merged spec MINUS the stale solved plan.
                // BuildDesign opens its own (nested) TransactionGroup and
                // writes the new spec + ownership map — so the strip above is
                // also what finally clears the stale keys out of storage, on
                // the one path that has replaced what they described.
                var rebuilt = BuildDesign(doc, rebuildArgs, uidoc);
                outerTg.Assimilate();      // one Ctrl+Z puts delete + rebuild back together
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
                report["scorecard"] = rebuilt.TryGetValue("scorecard", out var sc) ? sc : null;
                if (droppedSolved.Count > 0)
                {
                    report["dropped_solved_fields"] = droppedSolved;
                    report["warning"] = (report.TryGetValue("warning", out var prevW)
                                         && prevW is string pw2 ? pw2 + " " : "")
                        + $"the previously solved plan ({string.Join(", ", droppedSolved)}) described "
                        + "the OLD spec, so it was dropped rather than rebuilt against a building that "
                        + "changed. Re-run design_preflight and build_design to get a measured, "
                        + "room-by-room plan back.";
                }
                report["elapsed_ms"] = t0.ElapsedMilliseconds;
                report["digest"] = rebuilt.TryGetValue("digest", out var d) ? d : null;
            }
            catch
            {
                if (outerTg.GetStatus() == TransactionStatus.Started) outerTg.RollBack();
                throw;
            }
            return report;
        }
    }
}
