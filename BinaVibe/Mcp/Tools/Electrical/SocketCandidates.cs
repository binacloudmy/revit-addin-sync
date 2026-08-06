// suggest_socket_points — candidate power-socket points along a room's walls.
// READ-ONLY, INSPECT for the usual confirm-fatigue reason; SocketPlacement
// commits. Same two-step shape as fill_audit -> draft_export.
//
// THIS FILE IS THE ft<->mm BOUNDARY. Everything handed to SocketLayout is mm.
// ArgsHelp.GetLengthMm/GetPointMm return FEET, so they are deliberately NOT
// used for the rule args — those stay mm and are read with GetDouble.
//
// No regulatory number is baked in: the four rule args are required, so a
// standards change is a recipe re-ingest. The one default that ships here is
// the wet-room keyword list — linguistic, not regulatory, and echoed back in
// params_used so any answer stays auditable.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using static BinaVibe.Mcp.Tools.GeomMm;

namespace BinaVibe.Mcp.Tools.Electrical
{
    internal static class SocketCandidates
    {

        /// <summary>How far past the wall face a room probe is pushed, mm. A
        /// probe at exactly half the wall thickness lands ON the Finish
        /// boundary, where IsPointInRoom is undefined; this clears it.</summary>
        private const double RoomProbeClearMm = 100.0;

        /// <summary>Two candidate runs whose distance to the point differs by
        /// less than this are a tie: the point is on a party wall's centreline
        /// and belongs to neither room. Half the thinnest partition anyone
        /// builds.</summary>
        private const double RoomTieTolMm = 50.0;

        /// <summary>Linguistic fallback only — see the file header. A bare call
        /// must not silently place sockets in a bathroom.</summary>
        private static readonly string[] DefaultWetKeywords =
        {
            "bilik air", "bilik mandi", "tandas", "jamban",
            "bathroom", "toilet", "washroom", "wc", "shower",
            "dapur", "kitchen", "pantri", "pantry", "basuh", "laundry",
        };

        public static Dictionary<string, object?> Suggest(Document doc, JsonElement args)
        {
            // ── rule args (mm) ────────────────────────────────────────────
            var spacingMm = ArgsHelp.GetDouble(args, "spacing_mm");
            var cornerMm = ArgsHelp.GetDouble(args, "corner_clearance_mm");
            var mountMm = ArgsHelp.GetDouble(args, "mount_height_mm");
            var wetRadiusMm = ArgsHelp.GetDouble(args, "wet_radius_mm");

            var missing = new List<string>();
            if (!spacingMm.HasValue) missing.Add("spacing_mm");
            if (!cornerMm.HasValue) missing.Add("corner_clearance_mm");
            if (!mountMm.HasValue) missing.Add("mount_height_mm");
            if (!wetRadiusMm.HasValue) missing.Add("wet_radius_mm");
            if (missing.Count > 0)
                return ToolResult.Fail("missing required rule args: " + string.Join(", ", missing) +
                    ". These are placement standards, not defaults the addin may " +
                    "assume — take the values from the socket_placement_by_room " +
                    "recipe and pass them explicitly.");

            // Geometric hygiene, not code compliance — derived from the rule
            // args above so no new constant is asserted here. min_run is
            // exactly the length at which corner clearance leaves nothing.
            double openingMm = ArgsHelp.GetDouble(args, "opening_clearance_mm") ?? 0.0;
            double existingMm = ArgsHelp.GetDouble(args, "existing_clearance_mm") ?? cornerMm!.Value;
            double minRunMm = ArgsHelp.GetDouble(args, "min_run_mm") ?? (2.0 * cornerMm!.Value);

            int maxPerRoom = (int)(ArgsHelp.GetLong(args, "max_per_room") ?? 40);
            int maxPerWall = (int)(ArgsHelp.GetLong(args, "max_per_wall") ?? 20);
            int maxCandidates = (int)(ArgsHelp.GetLong(args, "max_candidates") ?? 200);
            bool includeWet = ArgsHelp.GetBool(args, "include_wet_rooms") ?? false;
            bool includeIslands = ArgsHelp.GetBool(args, "include_islands") ?? false;

            var wetKeywords = ArgsHelp.GetStringList(args, "wet_room_keywords");
            if (wetKeywords.Count == 0) wetKeywords = DefaultWetKeywords.ToList();

            var opts = new LayoutOptions
            {
                SpacingMm = spacingMm!.Value,
                CornerClearanceMm = cornerMm!.Value,
                MinRunMm = minRunMm,
                MountHeightMm = mountMm!.Value,
                MaxPerWall = maxPerWall,
                MaxPerRoom = maxPerRoom,
            };

            // ── rooms ─────────────────────────────────────────────────────
            var roomIds = ArgsHelp.GetLongList(args, "room_ids");
            var levelFilter = ArgsHelp.GetString(args, "level");

            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => roomIds.Count == 0 || roomIds.Contains(r.Id.Value))
                .Where(r => levelFilter == null ||
                            string.Equals(r.Level?.Name, levelFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var fixtures = CollectFixtures(doc);

            var plan = new SocketPlan();
            var roomRows = new List<object>();
            var skippedRooms = new List<object>();
            var skippedSegments = new List<object>();
            bool truncated = false;

            foreach (var room in rooms)
            {
                if (plan.Points.Count >= maxCandidates) { truncated = true; break; }

                var roomName = room.Name ?? "";

                if (!includeWet)
                {
                    var hit = wetKeywords.FirstOrDefault(k =>
                        !string.IsNullOrWhiteSpace(k) &&
                        roomName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hit != null)
                    {
                        skippedRooms.Add(new Dictionary<string, object?>
                        {
                            ["room_id"] = room.Id.Value,
                            ["name"] = roomName,
                            ["reason"] = "wet_room",
                            ["matched_keyword"] = hit,
                        });
                        continue;
                    }
                }

                // Checked BEFORE GetBoundarySegments — an unplaced room returns
                // an empty loop list and would otherwise vanish silently.
                if (room.Area <= 0)
                {
                    skippedRooms.Add(new Dictionary<string, object?>
                    {
                        ["room_id"] = room.Id.Value,
                        ["name"] = roomName,
                        ["reason"] = "unenclosed_or_unplaced",
                    });
                    continue;
                }

                var boundary = RoomBoundary.Build(doc, room, includeIslands, skippedSegments);
                var runs = boundary.Runs;
                if (boundary.LoopCount == 0)
                {
                    skippedRooms.Add(new Dictionary<string, object?>
                    {
                        ["room_id"] = room.Id.Value,
                        ["name"] = roomName,
                        ["reason"] = "no_boundary",
                    });
                    continue;
                }

                double zMm = RoomBoundary.FloorZMm(doc, room) + mountMm!.Value;
                var levelName = room.Level?.Name ?? "";

                BlockRuns(doc, room, runs, fixtures, wetRadiusMm!.Value, existingMm, openingMm);

                var layout = SocketLayout.Plan(runs, opts);

                int before = plan.Points.Count;
                foreach (var c in layout.Candidates)
                {
                    if (plan.Points.Count >= maxCandidates) { truncated = true; break; }
                    plan.Points.Add(new PlannedPoint
                    {
                        Index = plan.Points.Count,
                        RoomId = room.Id.Value,
                        RoomName = roomName,
                        LevelName = levelName,
                        HostWallId = c.HostWallId,
                        Host = c.Host,
                        XMm = c.XMm,
                        YMm = c.YMm,
                        ZMm = zMm,
                        MountHeightMm = c.MountHeightMm,
                        FacingDx = c.FacingDx,
                        FacingDy = c.FacingDy,
                        StationMm = c.StationMm,
                        WallLengthMm = c.WallLengthMm,
                        LoopIndex = c.LoopIndex,
                    });
                }

                roomRows.Add(new Dictionary<string, object?>
                {
                    ["room_id"] = room.Id.Value,
                    ["number"] = room.Number,
                    ["name"] = roomName,
                    ["level"] = levelName,
                    ["wall_run_count"] = runs.Count,
                    ["candidate_count"] = plan.Points.Count - before,
                    ["notes"] = layout.Notes.Cast<object>().ToList(),
                });
            }

            plan.ParamsUsed = new Dictionary<string, object?>
            {
                ["spacing_mm"] = spacingMm.Value,
                ["corner_clearance_mm"] = cornerMm.Value,
                ["mount_height_mm"] = mountMm.Value,
                ["wet_radius_mm"] = wetRadiusMm.Value,
                ["opening_clearance_mm"] = openingMm,
                ["existing_clearance_mm"] = existingMm,
                ["min_run_mm"] = minRunMm,
                ["max_per_wall"] = maxPerWall,
                ["max_per_room"] = maxPerRoom,
                ["max_candidates"] = maxCandidates,
                ["include_wet_rooms"] = includeWet,
                ["include_islands"] = includeIslands,
                ["wet_room_keywords"] = wetKeywords.Cast<object>().ToList(),
            };

            var planId = SocketPlanCache.Store(plan, DocKey(doc));

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["plan_id"] = planId,
                ["count"] = plan.Points.Count,
                ["truncated"] = truncated,
                ["params_used"] = plan.ParamsUsed,
                ["rooms"] = roomRows,
                ["candidates"] = plan.Points.Select(p => (object)new Dictionary<string, object?>
                {
                    ["index"] = p.Index,
                    ["room_id"] = p.RoomId,
                    ["room_name"] = p.RoomName,
                    ["host_wall_id"] = p.HostWallId,
                    ["host"] = p.Host,
                    ["xyz_mm"] = new List<object> { Round(p.XMm), Round(p.YMm), Round(p.ZMm) },
                    ["facing"] = new List<object> { Math.Round(p.FacingDx, 4), Math.Round(p.FacingDy, 4) },
                    ["mount_height_mm"] = Round(p.MountHeightMm),
                    ["station_mm"] = Round(p.StationMm),
                    ["wall_length_mm"] = Round(p.WallLengthMm),
                    ["loop_index"] = p.LoopIndex,
                }).ToList(),
                ["skipped_rooms"] = skippedRooms,
                ["skipped_segments"] = skippedSegments,
            };
        }

        internal static string DocKey(Document doc) =>
            string.IsNullOrEmpty(doc.PathName) ? (doc.Title ?? "") : doc.PathName;

        private static object Round(double mm) => Math.Round(mm, 1);

        // ── blocked intervals ────────────────────────────────────────────

        private sealed class FixtureHit
        {
            public XYZ Point = XYZ.Zero;
            public ElementId? RoomId;
            public bool Electrical;
        }

        /// <summary>All plumbing + electrical fixtures once per call, with
        /// their room association resolved up front.</summary>
        private static List<FixtureHit> CollectFixtures(Document doc)
        {
            var hits = new List<FixtureHit>();
            var cats = new[] { BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_ElectricalFixtures };

            foreach (var cat in cats)
            {
                foreach (var el in new FilteredElementCollector(doc)
                    .OfCategory(cat).WhereElementIsNotElementType())
                {
                    if (el.Location is not LocationPoint lp) continue;
                    ElementId? roomId = null;
                    try { roomId = (el as FamilyInstance)?.Room?.Id; } catch { roomId = null; }
                    hits.Add(new FixtureHit
                    {
                        Point = lp.Point,
                        RoomId = roomId,
                        Electrical = cat == BuiltInCategory.OST_ElectricalFixtures,
                    });
                }
            }
            return hits;
        }

        private static void BlockRuns(
            Document doc, Room room, List<WallRun> runs, List<FixtureHit> fixtures,
            double wetRadiusMm, double existingMm, double openingMm)
        {
            // Fixtures in THIS room. The FamilyInstance.Room association is
            // authoritative when present; IsPointInRoom is the fallback, and it
            // is Z-sensitive — a fixture's LocationPoint sits at floor level and
            // fails a naive test, so the probe is raised into the room volume.
            double probeZ = RoomBoundary.FloorZMm(doc, room) / MmPerFoot + (room.UnboundedHeight > 0 ? room.UnboundedHeight / 2.0 : 3.0);

            var inRoom = new List<FixtureHit>();
            foreach (var f in fixtures)
            {
                bool member;
                if (f.RoomId != null) member = f.RoomId == room.Id;
                else
                {
                    try { member = room.IsPointInRoom(new XYZ(f.Point.X, f.Point.Y, probeZ)); }
                    catch { member = false; }
                }
                if (member) inRoom.Add(f);
            }

            foreach (var run in runs)
            {
                // Openings: one FindInserts per wall covers doors, windows,
                // Opening elements and already-hosted families. Bounding-box
                // projection rather than the type's Width parameter — width
                // parameter names differ per family and category, a bbox never
                // does. NOTE: door swing / leaf clearance is NOT modelled; that
                // is a floor-plane concern, not a wall-face one.
                if (run.HostWallId.HasValue &&
                    doc.GetElement(ElemIds.From(run.HostWallId.Value)) is Wall wall)
                {
                    ICollection<ElementId> inserts;
                    try { inserts = wall.FindInserts(true, false, true, true); }
                    catch { inserts = new List<ElementId>(); }

                    foreach (var id in inserts)
                    {
                        var ins = doc.GetElement(id);
                        var bb = ins?.get_BoundingBox(null);
                        if (bb == null) continue;
                        if (!SpanOnRun(run, bb, out double lo, out double hi)) continue;
                        run.Blocked.Add(new Interval(lo - openingMm, hi + openingMm, "opening"));
                    }
                }

                foreach (var f in inRoom)
                {
                    double radius = f.Electrical ? existingMm : wetRadiusMm;
                    if (radius <= 0) continue;
                    var s = SocketLayout.ProjectStation(run.Points, RoomBoundary.ToPt(f.Point));
                    run.Blocked.Add(new Interval(s - radius, s + radius,
                        f.Electrical ? "existing_outlet" : "wet_fixture"));
                }
            }
        }

        /// <summary>Station span an insert occupies on a run, from its eight
        /// bounding-box corners. Works for curved runs, where projecting onto
        /// "the wall direction" would be meaningless.</summary>
        private static bool SpanOnRun(WallRun run, BoundingBoxXYZ bb, out double lo, out double hi)
        {
            lo = 0; hi = 0;
            var t = bb.Transform ?? Transform.Identity;
            var corners = new List<XYZ>();
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
                foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                        corners.Add(t.OfPoint(new XYZ(x, y, z)));

            bool first = true;
            foreach (var c in corners)
            {
                double s = SocketLayout.ProjectStation(run.Points, RoomBoundary.ToPt(c));
                if (first) { lo = hi = s; first = false; }
                else { if (s < lo) lo = s; if (s > hi) hi = s; }
            }
            return !first;
        }

        // ── ad-hoc facing derivation (place_socket_on_wall) ──────────────
        //
        // suggest_socket_points derives every candidate's inward normal during
        // the room walk and carries it in the plan. The single-socket tool has
        // no plan, so before this it placed whatever Revit produced and the
        // faceplate could end up parallel to the wall. Same derivation, same
        // NormalAt, computed on demand for one point.

        /// <summary>Inward plan normal for an arbitrary point on a host wall,
        /// derived exactly the way suggest_socket_points derives it (room
        /// boundary loop -> merged run -> SocketLayout.NormalAt), so an ad-hoc
        /// socket faces the same way a planned one at the same coordinate would.
        ///
        /// pointFt is in FEET — straight off ArgsHelp.GetPointMm, which reads mm
        /// from the wire and returns feet. THIS METHOD IS THE ft->mm HOP for the
        /// single-socket path, consistent with this file's header; no millimetre
        /// value escapes it and no foot reaches SocketLayout.
        ///
        /// Read-only: opens no Transaction and never throws. Returns false with
        /// a machine-readable <paramref name="reason"/> so the caller can still
        /// place the socket and SAY it went in uncorrected.</summary>
        internal static bool TryFacingAt(Document doc, Wall wall, XYZ pointFt,
                                         out double dx, out double dy,
                                         out long roomId, out string reason)
        {
            dx = 0.0; dy = 0.0; roomId = 0; reason = "";

            var axis = (wall?.Location as LocationCurve)?.Curve;
            if (axis == null) { reason = "no_location_curve"; return false; }

            try
            {
                var probeDir = WallPlanNormalAt(axis, pointFt);
                var rooms = CandidateRooms(doc, wall!, pointFt, probeDir);
                if (rooms.Count == 0) { reason = "no_room_at_point"; return false; }

                // One best run per room, then pick between rooms. Both passes
                // use the same tie tolerance, but only the second one's verdict
                // matters — within a single room the runs are different walls.
                var pMm = RoomBoundary.ToPt(pointFt);
                var bestRuns = new List<WallRun>();
                var bestRoomIds = new List<long>();
                var throwaway = new List<object>();

                foreach (var room in rooms)
                {
                    var runs = RoomBoundary.Build(doc, room, includeIslands: false, throwaway).Runs
                        .Where(r => r.HostWallId.HasValue && r.HostWallId.Value == wall!.Id.Value)
                        .ToList();
                    int idx = SocketLayout.NearestRunIndex(runs, pMm, RoomTieTolMm, out _);
                    if (idx < 0) continue;
                    bestRuns.Add(runs[idx]);
                    bestRoomIds.Add(room.Id.Value);
                }

                if (bestRuns.Count == 0) { reason = "wall_not_on_room_boundary"; return false; }

                int win = SocketLayout.NearestRunIndex(bestRuns, pMm, RoomTieTolMm, out bool tie);
                if (win < 0) { reason = "degenerate_run"; return false; }
                if (tie)
                {
                    // Equidistant from two rooms' finish faces: the caller gave a
                    // centreline point on a party wall. Guessing here faces the
                    // socket into the neighbour's unit.
                    reason = "ambiguous_room:" + string.Join(",", bestRoomIds);
                    return false;
                }

                var run = bestRuns[win];
                if (run.LoopPolygon == null || run.LoopPolygon.Count < 3)
                {
                    // Without the polygon NormalAt cannot resolve the sign
                    // (SocketLayout.NormalAt) and would pick a side arbitrarily —
                    // the exact silent-sideways-socket outcome this fixes.
                    reason = "no_loop_polygon";
                    return false;
                }

                SocketLayout.NormalAt(run, SocketLayout.ProjectStation(run.Points, pMm), out dx, out dy);
                roomId = bestRoomIds[win];
                return true;
            }
            catch (Exception ex)
            {
                // GetBoundarySegments, IsPointInRoom and Wall.Width all throw on
                // some inputs. A derivation failure must never lose a placement
                // that would otherwise commit.
                dx = 0.0; dy = 0.0; roomId = 0;
                reason = "derivation_failed:" + ex.Message;
                return false;
            }
        }

        /// <summary>A plan direction perpendicular to the wall axis at the
        /// projected point. Its SIGN IS DELIBERATELY IRRELEVANT — this exists
        /// only to generate two opposed probe points, both of which are tested.
        /// It is not a second opinion on NormalAt and must not be used as
        /// one.</summary>
        private static XYZ WallPlanNormalAt(Curve axis, XYZ pointFt)
        {
            XYZ tangent;
            try
            {
                var hit = axis.Project(pointFt);
                double u = hit != null ? axis.ComputeNormalizedParameter(hit.Parameter) : 0.5;
                tangent = axis.ComputeDerivatives(u, true).BasisX;
            }
            catch { tangent = axis.GetEndPoint(1) - axis.GetEndPoint(0); }

            var plan = new XYZ(-tangent.Y, tangent.X, 0);
            return plan.GetLength() < 1e-9 ? XYZ.BasisX : plan.Normalize();
        }

        /// <summary>Rooms that could own this point: the point itself plus one
        /// probe each side of the wall.
        ///
        /// Neither existing approach works alone. doc.GetRoomAtPoint
        /// (Mutators.cs) takes one global Z and one phase, and cannot tell a
        /// phasing miss from "no room here". A bare IsPointInRoom scan
        /// (QueryGeometry.cs) is exact but fails at floor level, and fails
        /// outright for a point on the wall CENTRELINE — inside the wall,
        /// outside every room — which is exactly what a caller gets from
        /// wall.LocationCurve. So: scan, raise the probe per room the way
        /// BlockRuns does, and offset in plan to clear the wall.
        ///
        /// Two hits is normal for an interior wall; the caller adjudicates.</summary>
        private static List<Room> CandidateRooms(Document doc, Wall wall, XYZ pointFt, XYZ probeDir)
        {
            double halfWidthFt;
            // Wall.Width throws for curtain walls. 300mm is a plausible partition,
            // and the probe only has to clear the face, not measure it.
            try { halfWidthFt = wall.Width / 2.0; } catch { halfWidthFt = 150.0 / MmPerFoot; }
            double offFt = halfWidthFt + RoomProbeClearMm / MmPerFoot;

            var probes = new List<XYZ>
            {
                pointFt,
                pointFt + probeDir.Multiply(offFt),
                pointFt - probeDir.Multiply(offFt),
            };

            var hits = new List<Room>();
            foreach (var room in new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>())
            {
                if (room.Area <= 0) continue;              // unplaced, same guard as Suggest
                var bb = room.get_BoundingBox(null);
                if (bb == null) continue;
                if (!probes.Any(p => InPlanBox(bb, p))) continue;

                // Lifted from BlockRuns: a point at floor level fails the naive
                // test, so probe half way up the room volume.
                double probeZ = RoomBoundary.FloorZMm(doc, room) / MmPerFoot
                                + (room.UnboundedHeight > 0 ? room.UnboundedHeight / 2.0 : 3.0);

                foreach (var p in probes)
                {
                    bool inside;
                    try { inside = room.IsPointInRoom(new XYZ(p.X, p.Y, probeZ)); }
                    catch { inside = false; }
                    if (inside) { hits.Add(room); break; }
                }
            }
            return hits;
        }

        /// <summary>Plan-only bounding-box prefilter. A room's bbox always
        /// contains its volume, so this cannot produce a false negative.</summary>
        private static bool InPlanBox(BoundingBoxXYZ bb, XYZ p)
        {
            var t = bb.Transform ?? Transform.Identity;
            var lo = t.OfPoint(bb.Min);
            var hi = t.OfPoint(bb.Max);
            return p.X >= Math.Min(lo.X, hi.X) && p.X <= Math.Max(lo.X, hi.X)
                && p.Y >= Math.Min(lo.Y, hi.Y) && p.Y <= Math.Max(lo.Y, hi.Y);
        }

    }
}
