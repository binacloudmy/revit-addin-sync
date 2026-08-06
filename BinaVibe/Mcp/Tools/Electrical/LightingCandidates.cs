// suggest_lighting_points — candidate lighting positions on a grid inside a
// room. READ-ONLY, INSPECT for the usual confirm-fatigue reason;
// LightingPlacement commits. Same two-step shape as suggest_socket_points.
//
// THIS FILE IS THE ft<->mm BOUNDARY. Everything handed to LightingLayout is mm.
// ArgsHelp.GetLengthMm/GetPointMm return FEET, so they are deliberately NOT
// used for the rule args — those stay mm and are read with GetDouble.
//
// The COUNT is computed here, not by the caller: the agent asks for a power
// density and gets back the arithmetic already done, with every input echoed in
// params_used. That is the whole point of the tool — a model that multiplies
// and rounds for itself is a model one step away from writing a script to do it.
//
// No regulatory number is baked in. target_w_per_m2 comes from the drafter's
// own schedule, the fixture wattage is read from the family, and the layout
// defaults live in the recipe.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class LightingCandidates
    {
        /// <summary>Hard ceiling on one call's output, mirroring
        /// suggest_socket_points. A runaway target_w_per_m2 against a tiny
        /// fixture wattage must not try to place ten thousand downlights.</summary>
        private const int DefaultMaxPerRoom = 200;

        /// <summary>Drop below the room's upper bound for a "ceiling height"
        /// fixture when no ceiling element is found, mm. Not a standard — it
        /// only keeps the fixture off the slab soffit, and it is echoed in
        /// params_used.</summary>
        private const double DefaultCeilingOffsetMm = 0.0;

        public static Dictionary<string, object?> Suggest(Document doc, JsonElement args)
        {
            var familyType = ArgsHelp.GetString(args, "family_type");
            if (string.IsNullOrWhiteSpace(familyType))
                return ToolResult.Fail("missing family_type — call list_family_types " +
                    "(category \"Lighting Fixtures\") and pass an exact type name. The " +
                    "wattage is read from that type, so it cannot be defaulted.");

            var symbol = SocketPlacement.ResolveSymbol(doc, familyType!);
            if (symbol == null)
                return ToolResult.Fail($"family type '{familyType}' not found in document — " +
                    "list_family_types shows what is loaded; search_family_library + " +
                    "load_family brings in what is not.");

            // ── the count inputs: a density to hit, or an explicit count ──
            double? targetWPerM2 = ArgsHelp.GetDouble(args, "target_w_per_m2");
            int? countPerRoom = (int?)ArgsHelp.GetLong(args, "count_per_room");
            if (!targetWPerM2.HasValue && !countPerRoom.HasValue)
                return ToolResult.Fail("pass either target_w_per_m2 (read from the " +
                    "schedule column) or count_per_room — the tool will not invent a " +
                    "lighting requirement.");

            double? fixtureW = ArgsHelp.GetDouble(args, "fixture_w") ?? ElecReads.ApparentLoadVa(symbol);
            if (targetWPerM2.HasValue && !fixtureW.HasValue)
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["blocker"] = new Dictionary<string, object?>
                    {
                        ["code"] = "fixture_wattage_unknown",
                        ["family_type"] = familyType,
                        ["message"] = $"'{familyType}' declares no apparent load, so the " +
                            "fixture count for a W/m2 target cannot be derived. Read the " +
                            "wattage with get_type_parameters and pass it as fixture_w, or " +
                            "ask the drafter — never assume one.",
                    },
                    ["count"] = 0,
                    ["candidates"] = new List<object>(),
                };

            // ── layout args (mm) ─────────────────────────────────────────
            var opts = new LightingGridOptions
            {
                EdgeMarginMm = ArgsHelp.GetDouble(args, "edge_margin_mm") ?? 900.0,
                MinSpacingMm = ArgsHelp.GetDouble(args, "min_spacing_mm") ?? 0.0,
            };
            double? mountHeightMm = ArgsHelp.GetDouble(args, "mount_height_mm");
            double ceilingOffsetMm = ArgsHelp.GetDouble(args, "ceiling_offset_mm") ?? DefaultCeilingOffsetMm;
            int maxPerRoom = (int)(ArgsHelp.GetLong(args, "max_per_room") ?? DefaultMaxPerRoom);
            bool includeIslands = ArgsHelp.GetBool(args, "include_islands") ?? true;

            // ── rooms ────────────────────────────────────────────────────
            var roomIds = ArgsHelp.GetLongList(args, "room_ids");
            var roomsNamed = ArgsHelp.GetStringList(args, "rooms_named");
            var levelFilter = ArgsHelp.GetString(args, "level");

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => roomIds.Count == 0 || roomIds.Contains(r.Id.Value))
                .Where(r => roomsNamed.Count == 0 || roomsNamed.Any(n =>
                    !string.IsNullOrWhiteSpace(n) &&
                    (r.Name ?? "").IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                .Where(r => levelFilter == null ||
                            string.Equals(r.Level?.Name, levelFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (rooms.Count == 0)
                return ToolResult.Fail("no rooms matched — check room_ids / rooms_named / " +
                    "level against list_rooms.");

            var plan = new LightingPlan { FamilyType = familyType! };
            var roomRows = new List<object>();
            var skippedRooms = new List<object>();
            var skippedSegments = new List<object>();

            foreach (var room in rooms)
            {
                var roomName = room.Name ?? "";

                // Checked BEFORE GetBoundarySegments — an unplaced room returns
                // an empty loop list and would otherwise vanish silently.
                if (room.Area <= 0)
                {
                    Skip(skippedRooms, room, roomName, "unenclosed_or_unplaced");
                    continue;
                }

                double areaM2 = UnitUtils.ConvertFromInternalUnits(room.Area, UnitTypeId.SquareMeters);

                int required = countPerRoom
                    ?? LightingLayout.CountForTarget(targetWPerM2!.Value, areaM2, fixtureW!.Value);
                if (required <= 0)
                {
                    Skip(skippedRooms, room, roomName, "no_requirement_resolvable");
                    continue;
                }

                bool capped = required > maxPerRoom;
                if (capped) required = maxPerRoom;

                var boundary = RoomBoundary.Build(doc, room, includeIslands, skippedSegments);
                if (boundary.LoopCount == 0 || boundary.OuterIndex < 0)
                {
                    Skip(skippedRooms, room, roomName, "no_boundary");
                    continue;
                }

                var islands = new List<IReadOnlyList<Pt2>>();
                for (int i = 0; i < boundary.Polygons.Count; i++)
                    if (i != boundary.OuterIndex) islands.Add(boundary.Polygons[i]);

                var grid = LightingLayout.Plan(boundary.Outer, islands, required, opts);
                if (grid.Points.Count == 0)
                {
                    Skip(skippedRooms, room, roomName, "no_point_inside_boundary");
                    continue;
                }

                double floorZMm = RoomBoundary.FloorZMm(doc, room);
                double roomTopZMm = RoomTopZMm(doc, room, floorZMm);
                var ceilings = CollectCeilings(doc, room);
                var levelName = room.Level?.Name ?? "";

                foreach (var g in grid.Points)
                {
                    var ceiling = CeilingAt(ceilings, g.XMm, g.YMm, floorZMm);

                    // Z, in order of authority: the drafter's mount height, then
                    // the ceiling actually found, then the room's upper bound.
                    double zMm;
                    if (mountHeightMm.HasValue) zMm = floorZMm + mountHeightMm.Value;
                    else if (ceiling != null) zMm = ceiling.ZMm - ceilingOffsetMm;
                    else zMm = roomTopZMm - ceilingOffsetMm;

                    plan.Points.Add(new PlannedLight
                    {
                        Index = plan.Points.Count,
                        RoomId = room.Id.Value,
                        RoomName = roomName,
                        LevelName = levelName,
                        HostCeilingId = ceiling?.Id,
                        Host = ceiling != null ? "ceiling" : "unhosted",
                        XMm = g.XMm,
                        YMm = g.YMm,
                        ZMm = zMm,
                        MountHeightMm = zMm - floorZMm,
                    });
                }

                var row = new PlannedLightRoom
                {
                    RoomId = room.Id.Value,
                    RoomName = roomName,
                    LevelName = levelName,
                    AreaM2 = Math.Round(areaM2, 2),
                    TargetWPerM2 = targetWPerM2 ?? 0,
                    FixtureW = fixtureW ?? 0,
                    RequiredCount = required,
                    PlannedCount = grid.Points.Count,
                    RequiredW = Math.Round((targetWPerM2 ?? 0) * areaM2, 1),
                    InstalledW = Math.Round((fixtureW ?? 0) * grid.Points.Count, 1),
                };
                plan.Rooms.Add(row);

                if (capped)
                    grid.Notes.Add($"count capped at max_per_room {maxPerRoom} — the target " +
                                   "needs more fixtures than that; raise the cap or the wattage");

                roomRows.Add(new Dictionary<string, object?>
                {
                    ["room_id"] = row.RoomId,
                    ["name"] = row.RoomName,
                    ["level"] = row.LevelName,
                    ["area_m2"] = row.AreaM2,
                    ["target_w_per_m2"] = row.TargetWPerM2,
                    ["fixture_w"] = row.FixtureW,
                    ["required_w"] = row.RequiredW,
                    ["required_count"] = row.RequiredCount,
                    ["planned_count"] = row.PlannedCount,
                    ["installed_w"] = row.InstalledW,
                    ["surplus_w"] = Math.Round(row.InstalledW - row.RequiredW, 1),
                    ["short_by"] = Math.Max(0, row.RequiredCount - row.PlannedCount),
                    ["min_spacing_achieved_mm"] = Math.Round(grid.MinSpacingAchievedMm, 0),
                    ["notes"] = grid.Notes.Cast<object>().ToList(),
                });
            }

            if (plan.Points.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["count"] = 0,
                    ["candidates"] = new List<object>(),
                    ["rooms"] = new List<object>(),
                    ["skipped_rooms"] = skippedRooms,
                    ["skipped_segments"] = skippedSegments,
                    ["blocker"] = new Dictionary<string, object?>
                    {
                        ["code"] = "no_placeable_rooms",
                        ["message"] = "every matched room was skipped — see skipped_rooms for " +
                            "the reason per room. This is a model/scoping answer, not a " +
                            "reason to place anything by other means.",
                    },
                };

            var connector = ConnectorVerdict(doc, symbol);

            plan.ParamsUsed = new Dictionary<string, object?>
            {
                ["family_type"] = familyType,
                ["target_w_per_m2"] = targetWPerM2,
                ["count_per_room"] = countPerRoom,
                ["fixture_w"] = fixtureW,
                ["fixture_w_source"] = ArgsHelp.GetDouble(args, "fixture_w").HasValue
                    ? "arg" : "family_apparent_load",
                ["edge_margin_mm"] = opts.EdgeMarginMm,
                ["min_spacing_mm"] = opts.MinSpacingMm,
                ["mount_height_mm"] = mountHeightMm,
                ["ceiling_offset_mm"] = ceilingOffsetMm,
                ["max_per_room"] = maxPerRoom,
                ["include_islands"] = includeIslands,
            };

            var planId = LightingPlanCache.Store(plan, SocketCandidates.DocKey(doc));

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = planId,
                ["family_type"] = familyType,
                ["count"] = plan.Points.Count,
                ["params_used"] = plan.ParamsUsed,
                ["rooms"] = roomRows,
                ["candidates"] = plan.Points.Select(p => (object)new Dictionary<string, object?>
                {
                    ["index"] = p.Index,
                    ["room_id"] = p.RoomId,
                    ["room_name"] = p.RoomName,
                    ["host"] = p.Host,
                    ["host_ceiling_id"] = p.HostCeilingId,
                    ["xyz_mm"] = new List<object> { Round(p.XMm), Round(p.YMm), Round(p.ZMm) },
                    ["mount_height_mm"] = Round(p.MountHeightMm),
                }).ToList(),
                ["unhosted_count"] = plan.Points.Count(p => p.HostCeilingId == null),
                ["skipped_rooms"] = skippedRooms,
                ["skipped_segments"] = skippedSegments,
                // Placed-but-uncircuitable is the expensive failure: the fixtures
                // land, look right, and suggest_circuits then skips every one of
                // them with no_electrical_connector. Cheaper to say so now.
                ["no_electrical_connector"] = connector == "absent",
                ["connector_check"] = connector,
            };

            if (connector == "absent")
                result["connector_warning"] =
                    $"'{familyType}' has no electrical connector — these fixtures can be " +
                    "placed but can NEVER be circuited (suggest_circuits skips them as " +
                    "no_electrical_connector). Fix the family with " +
                    "set_connector_electrical_data, or pick a type that has one, before " +
                    "committing.";

            return result;
        }

        private static void Skip(List<object> into, Room room, string name, string reason) =>
            into.Add(new Dictionary<string, object?>
            {
                ["room_id"] = room.Id.Value,
                ["name"] = name,
                ["reason"] = reason,
            });

        private static object Round(double mm) => Math.Round(mm, 1);

        /// <summary>Upper bound of the room in absolute mm: floor plus the room's
        /// own height. The fallback when no ceiling element covers a point —
        /// never a hardcoded storey height.</summary>
        private static double RoomTopZMm(Document doc, Room room, double floorZMm)
        {
            double heightFt = 0.0;
            try { heightFt = room.UnboundedHeight; } catch { heightFt = 0.0; }
            var upper = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
            if (heightFt <= 0 && upper != null && upper.StorageType == StorageType.Double)
                heightFt = upper.AsDouble();
            if (heightFt <= 0) return floorZMm;
            return floorZMm + heightFt * MmPerFoot;
        }

        private sealed class CeilingHit
        {
            public long Id;
            public double MinXMm, MinYMm, MaxXMm, MaxYMm;
            /// <summary>Underside of the ceiling in absolute mm — bbox minimum Z,
            /// which is the face a fixture sits on.</summary>
            public double ZMm;
        }

        /// <summary>Ceilings whose bounding box overlaps the room's, once per
        /// room. ReferenceIntersector would be the textbook answer and needs a
        /// View3D the model may not have; a bbox pass needs nothing and is what
        /// query_geometry already reports clashes with.</summary>
        private static List<CeilingHit> CollectCeilings(Document doc, Room room)
        {
            var hits = new List<CeilingHit>();
            BoundingBoxXYZ? roomBox;
            try { roomBox = room.get_BoundingBox(null); } catch { roomBox = null; }
            if (roomBox == null) return hits;

            var outline = new Outline(roomBox.Min, roomBox.Max);
            IEnumerable<Element> ceilings;
            try
            {
                ceilings = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Ceilings)
                    .WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(outline));
            }
            catch { return hits; }

            foreach (var el in ceilings)
            {
                BoundingBoxXYZ? bb;
                try { bb = el.get_BoundingBox(null); } catch { bb = null; }
                if (bb == null) continue;
                hits.Add(new CeilingHit
                {
                    Id = el.Id.Value,
                    MinXMm = bb.Min.X * MmPerFoot,
                    MinYMm = bb.Min.Y * MmPerFoot,
                    MaxXMm = bb.Max.X * MmPerFoot,
                    MaxYMm = bb.Max.Y * MmPerFoot,
                    ZMm = bb.Min.Z * MmPerFoot,
                });
            }
            return hits;
        }

        /// <summary>Lowest ceiling above the room floor whose plan extent covers
        /// the point. Lowest wins: a bulkhead under a slab is the surface the
        /// fixture actually goes into.</summary>
        private static CeilingHit? CeilingAt(List<CeilingHit> ceilings, double xMm, double yMm, double floorZMm)
        {
            CeilingHit? best = null;
            foreach (var c in ceilings)
            {
                if (xMm < c.MinXMm || xMm > c.MaxXMm) continue;
                if (yMm < c.MinYMm || yMm > c.MaxYMm) continue;
                if (c.ZMm <= floorZMm) continue;
                if (best == null || c.ZMm < best.ZMm) best = c;
            }
            return best;
        }

        /// <summary>"present" | "absent" | "unknown" for the type's electrical
        /// connector.
        ///
        /// Connectors live on INSTANCES, not on a FamilySymbol, so the only way
        /// to know before placing is to look at an instance of the same type
        /// that is already in the model. None placed yet = "unknown", and the
        /// caller warns about nothing: a guessed defect would send the drafter
        /// to the Family Editor for a family that is fine.</summary>
        private static string ConnectorVerdict(Document doc, FamilySymbol symbol)
        {
            try
            {
                var existing = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .FirstOrDefault(fi => fi.GetTypeId() == symbol.Id);
                if (existing == null) return "unknown";

                var mgr = existing.MEPModel?.ConnectorManager;
                if (mgr == null) return "absent";
                foreach (Connector c in mgr.Connectors)
                    if (c.Domain == Domain.DomainElectrical) return "present";
                return "absent";
            }
            catch { return "unknown"; }
        }
    }
}
