#nullable enable
// CadSymbolResolve - phase 1 of the CAD electrical to BIM work (spec 2026-11-17 §5.5).
//
// One drawn symbol, already carried into model coordinates by the fit, becomes one plan
// point: snapped to the face of the wall it is drawn against, facing into the room, at the
// mapping's height above the level (a drawing's legend heights are from finished floor level,
// which is the level, not a room's lower offset), with the review flags that apply. The pure core:
// walls, rooms and existing devices come in as plain data, with "room at this point" and
// "ceiling at this point" delegates, so the Revit side (CadElectricalResolver) only reads the
// model.
//
// A ceiling device (a light, a fan) goes on the underside of the ceiling over its drawn centre
// when the level has one there; with none, at the level plus its mount height; with neither,
// it is flagged no_height. A host "none" device stands at the level plus its mount height.
//
// Departs from spec §5.5 step 2 on purpose: the room probe goes along the wall's normal,
// not the symbol's facing. A drawn symbol's rotation says little about which way it faces.
//
// Millimetres, project-internal coordinates. Must compile on net48.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    using Cad2Bim;

    /// <summary>A model wall's centreline on one level, mm.</summary>
    internal sealed class ModelWall
    {
        public long Id;
        public double X1, Y1, X2, Y2, ThicknessMm;
        /// <summary>From a Revit link: nothing local to host on.</summary>
        public bool Linked;
        /// <summary>The top of the wall (its bounding box), mm; null when not read.</summary>
        public double? TopZMm;
    }

    internal sealed class RoomHit
    {
        public long Id;
        public string Name = "";
        /// <summary>The room's floor (level plus lower offset). Reported only: a drawing point's
        /// height comes from the target level.</summary>
        public double FloorZMm;
        public bool Linked;
    }

    /// <summary>The local ceiling over a plan point, and its underside there, mm.</summary>
    internal sealed class CeilingHit
    {
        public long Id;
        public double UndersideZMm;
    }

    internal sealed class ResolveOptions
    {
        /// <summary>How far past a wall's face a symbol may be drawn and still be on it.</summary>
        public double ReachMm = 400;
        /// <summary>How far past a wall's end.</summary>
        public double AxialTolMm = 150;
        /// <summary>A symbol closer than this to the centreline asks the rooms which side.</summary>
        public double SideAmbiguityMm = 15;
        /// <summary>How far past the face a room is looked for.</summary>
        public double RoomProbeMm = 150;
        /// <summary>An existing device this close in 3D means the point is already there.</summary>
        public double ExistingMm = 150;
        /// <summary>Two drawn symbols of one role this close are one drawn twice.</summary>
        public double DuplicateMm = 20;
        public IReadOnlyList<string> WetKeywords = SocketLayout.DefaultWetKeywords;
    }

    /// <summary>One drawn symbol in model coordinates, with what the mapping says of it.</summary>
    internal sealed class ResolveInput
    {
        public double XMm, YMm;
        public string Role = "";
        /// <summary>"wall" | "ceiling" | "none"</summary>
        public string Host = "wall";
        public double? MountHeightMm;
        /// <summary>Diagnostic: the symbol's other candidate anchors, model mm. Null: none
        /// reported (the point's Anchors stay null).</summary>
        public List<AnchorInput>? Anchors = null;
    }

    /// <summary>
    /// One candidate anchor of a drawn symbol, model mm: no candidate when it does not apply,
    /// one point, or several of which the one nearest the host wall's face is taken (the
    /// four side midpoints of the symbol's box, for wall_edge_mid).
    /// </summary>
    internal sealed class AnchorInput
    {
        public string Name = "";
        public List<(double X, double Y)> Candidates = new List<(double X, double Y)>();
    }

    /// <summary>
    /// An insert's local frame carried to world: rotation, then scale, the y axis turned over
    /// when mirrored. The census places a symbol at its drawn centre with it, and the anchor
    /// diagnostic reads the box corners with it.
    /// </summary>
    internal static class SymbolFrame
    {
        public static (double X, double Y) ToWorld(double lx, double ly, double x, double y, double rotationRad,
                                                   double scaleX, double scaleY, bool mirrored)
        {
            double cos = Math.Cos(rotationRad), sin = Math.Sin(rotationRad);
            double flip = mirrored ? -1 : 1;
            // Columns of the insert's linear map: x axis scaleX * (cos, sin), y axis
            // scaleY * (-sin, cos), turned over when mirrored.
            return (x + (scaleX * cos * lx) - (flip * scaleY * sin * ly),
                    y + (scaleX * sin * lx) + (flip * scaleY * cos * ly));
        }

        /// <summary>The local box's corners in world, in local order (min,min), (max,min),
        /// (max,max), (min,max), as x0, y0, x1, y1, ...</summary>
        public static double[] Corners(double minX, double minY, double maxX, double maxY, double x, double y,
                                       double rotationRad, double scaleX, double scaleY, bool mirrored)
        {
            var corners = new double[8];
            double[] lx = { minX, maxX, maxX, minX }, ly = { minY, minY, maxY, maxY };
            for (int i = 0; i < 4; i++)
            {
                (double wx, double wy) = ToWorld(lx[i], ly[i], x, y, rotationRad, scaleX, scaleY, mirrored);
                corners[2 * i] = wx;
                corners[(2 * i) + 1] = wy;
            }
            return corners;
        }
    }

    internal static class CadSymbolResolve
    {
        /// <summary>The anchor diagnostic's candidates, in report order. Placement uses centre.</summary>
        public static readonly IReadOnlyList<string> AnchorNames = new[] { "centre", "insert", "wall_edge_mid", "bbox_min", "bbox_max" };

        /// <summary>An insert point farther than this many symbol extents from the drawn centre
        /// is a far base point (phase 0 saw kilometres), not an anchor.</summary>
        public const double FarInsertExtents = 2;

        /// <summary>
        /// A drawn symbol's candidate anchors other than its centre, carried to model mm by
        /// <paramref name="toModel"/>: insert (none when farther than FarInsertExtents times
        /// the symbol's extent from its drawn centre), wall_edge_mid (the box's four side
        /// midpoints; the resolver takes the one nearest the wall's face), bbox_min and
        /// bbox_max (the local box's corners). A symbol whose frame was not read gets none.
        /// </summary>
        public static List<AnchorInput> AnchorInputs(ScanSymbol symbol, Func<double, double, (double X, double Y)> toModel)
        {
            var insert = new AnchorInput { Name = "insert" };
            var edge = new AnchorInput { Name = "wall_edge_mid" };
            var min = new AnchorInput { Name = "bbox_min" };
            var max = new AnchorInput { Name = "bbox_max" };
            double[] c = symbol.Corners;
            if (c.Length == 8)
            {
                double extent = Math.Max(symbol.WidthMm, symbol.HeightMm);
                double dx = symbol.InsertX - symbol.X, dy = symbol.InsertY - symbol.Y;
                if (Math.Sqrt((dx * dx) + (dy * dy)) <= (FarInsertExtents * extent) + 1e-6)
                    insert.Candidates.Add(toModel(symbol.InsertX, symbol.InsertY));
                for (int i = 0; i < 4; i++)
                {
                    int j = (i + 1) % 4;
                    edge.Candidates.Add(toModel((c[2 * i] + c[2 * j]) / 2, (c[(2 * i) + 1] + c[(2 * j) + 1]) / 2));
                }
                min.Candidates.Add(toModel(c[0], c[1]));
                max.Candidates.Add(toModel(c[4], c[5]));
            }
            return new List<AnchorInput> { insert, edge, min, max };
        }

        /// <summary>Every review flag and whether it keeps a point out of a default placement.</summary>
        public static readonly IReadOnlyDictionary<string, bool> FlagExcludes = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["no_wall_in_reach"] = true,
            ["no_room"] = false,
            ["wet_room"] = false,
            ["already_present"] = true,
            ["linked_wall"] = false,
            ["provisional_alignment"] = false,
            ["side_ambiguous"] = false,
            ["duplicate_in_drawing"] = true,
            ["family_host_mismatch"] = true,
            ["no_ceiling"] = true,
            ["no_height"] = true,
            ["above_wall_top"] = false,
        };

        /// <summary>Adds a flag once; a flag that excludes by default excludes the point.</summary>
        public static void AddFlag(PlannedPoint p, string flag)
        {
            if (!p.Flags.Contains(flag)) p.Flags.Add(flag);
            if (FlagExcludes.TryGetValue(flag, out bool excludes) && excludes) p.Excluded = true;
        }

        /// <summary>Walls thinner than this are finishes (render, tiling, a lining), not walls:
        /// neither fitted on nor hosted on.</summary>
        public const double MinStructuralWallMm = 50;

        /// <summary>
        /// The walls at least <paramref name="minMm"/> thick. A finish drawn as its own thin
        /// wall triples the fit's votes at 60-70 mm off, and a symbol at the finish surface is
        /// nearest the finish's centreline, so the side test and the room probe go wrong; the
        /// core wall behind it is the host. Fewer than four left keeps the input as it is, so a
        /// level of thin walls only still fits. Curtain walls report no width and drop out too.
        /// </summary>
        public static List<ModelWall> Structural(IReadOnlyList<ModelWall> walls, double minMm = MinStructuralWallMm)
        {
            List<ModelWall> kept = walls.Where(w => w.ThicknessMm >= minMm).ToList();
            return kept.Count >= 4 ? kept : walls.ToList();
        }

        /// <summary>
        /// Whether a drawn symbol belongs to the plan being placed: "plan" when, carried onto
        /// the model, it lies inside the level's footprint, whatever census cluster it is in
        /// (stacked plans share a cluster; one plan can be split across two); else
        /// "outside_model_footprint" in the chosen cluster, "other_plan" in any other.
        /// </summary>
        public static string Membership(int symbolCluster, int chosenCluster, bool insideFootprint)
        {
            if (insideFootprint) return "plan";
            return symbolCluster == chosenCluster ? "outside_model_footprint" : "other_plan";
        }

        /// <summary>The walls' centrelines filed for <see cref="Resolve"/>, same order.</summary>
        public static SegmentIndex Index(IReadOnlyList<ModelWall> walls) =>
            new SegmentIndex(walls.Select(w => new Segment(new Point(w.X1, w.Y1), new Point(w.X2, w.Y2))).ToList(), 2000);

        public static PlannedPoint Resolve(ResolveInput s, IReadOnlyList<ModelWall> walls, SegmentIndex wallIndex,
                                           Func<double, double, RoomHit?> roomAt, double levelZMm,
                                           IReadOnlyList<(double X, double Y, double Z)> existing, ResolveOptions o,
                                           Func<double, double, CeilingHit?>? ceilingAt = null)
        {
            var p = new PlannedPoint
            {
                Role = s.Role,
                Source = "cad",
                XMm = s.XMm,
                YMm = s.YMm,
                Host = "unhosted",
            };

            // Ceiling and host "none" devices stay at the drawn centre.
            ModelWall? onWall = null;
            double side = 1;
            RoomHit? room = s.Host == "wall"
                ? OnWall(s, walls, wallIndex, roomAt, o, p, out onWall, out side)
                : roomAt(s.XMm, s.YMm);

            if (room == null) AddFlag(p, "no_room");
            else
            {
                p.RoomId = room.Id;
                p.RoomName = room.Name;
                if (IsWet(room.Name, o.WetKeywords)) AddFlag(p, "wet_room");
            }

            // 6. Height. A ceiling device under a ceiling of this level takes the ceiling's
            // underside, whatever the mapping's height, and its mount height is read back as the
            // underside above the level. Anything else is measured from the target level, never
            // the room's floor: the legend measures from finished floor level, and a room's lower
            // offset (or its being on a link's level) would move every device by that much. The
            // room is for naming and the wet check.
            CeilingHit? ceiling = s.Host == "ceiling" && ceilingAt != null ? ceilingAt(s.XMm, s.YMm) : null;
            if (ceiling != null)
            {
                p.HostCeilingId = ceiling.Id;
                p.Host = "ceiling";
                p.ZMm = ceiling.UndersideZMm;
                p.MountHeightMm = ceiling.UndersideZMm - levelZMm;
            }
            else if (s.MountHeightMm.HasValue)
            {
                p.MountHeightMm = s.MountHeightMm.Value;
                p.ZMm = levelZMm + s.MountHeightMm.Value;
            }
            else
            {
                p.ZMm = levelZMm;
                AddFlag(p, "no_height");
            }

            // A wall point over the top of its wall: the wall's side face does not reach it, so a
            // face-based family cannot go on that face and the wall overload is used instead.
            if (AboveWallTop(p, onWall)) AddFlag(p, "above_wall_top");

            if (IsPresent(p.XMm, p.YMm, p.ZMm, existing, o.ExistingMm)) AddFlag(p, "already_present");
            if (s.Anchors != null) p.Anchors = Anchors(s.Anchors, p, onWall, side);
            return p;
        }

        /// <summary>
        /// The point each candidate anchor would give: on the point's wall, side and face
        /// (SnapToFace, as the point itself), at the point's Z; with no host wall, the
        /// candidate as drawn. Of several candidates the one nearest the chosen face is taken;
        /// with no host wall that choice cannot be made and the anchor is not known.
        /// </summary>
        private static List<PlanAnchor> Anchors(IReadOnlyList<AnchorInput> inputs, PlannedPoint p, ModelWall? wall, double side)
        {
            var anchors = new List<PlanAnchor>
            {
                new PlanAnchor
                {
                    Name = "centre", Known = true, XMm = p.XMm, YMm = p.YMm, ZMm = p.ZMm,
                    AlongOffsetMm = wall != null ? 0 : (double?)null,
                },
            };
            foreach (AnchorInput a in inputs)
            {
                var anchor = new PlanAnchor { Name = a.Name };
                anchors.Add(anchor);
                if (a.Candidates.Count == 0 || (wall == null && a.Candidates.Count > 1)) continue;
                (double X, double Y) pick = a.Candidates[0];
                if (wall != null)
                {
                    double best = double.MaxValue;
                    foreach ((double X, double Y) c in a.Candidates)
                    {
                        double gap = Math.Abs(Perp(wall, c.X, c.Y) - (side * wall.ThicknessMm / 2));
                        if (gap >= best) continue;
                        best = gap;
                        pick = c;
                    }
                }
                anchor.Known = true;
                anchor.ZMm = p.ZMm;
                if (wall == null)
                {
                    anchor.XMm = pick.X;
                    anchor.YMm = pick.Y;
                    continue;
                }
                (double x, double y, double station) = SnapToFace(wall, side, pick.X, pick.Y);
                anchor.XMm = x;
                anchor.YMm = y;
                anchor.AlongOffsetMm = station - p.StationMm;
            }
            return anchors;
        }

        /// <summary>
        /// The point on a wall's face nearest (x, y): its foot on the centreline, held to the
        /// wall's ends, moved out half the thickness on <paramref name="side"/> (+1 to the left
        /// of X1Y1 to X2Y2, -1 to the right). The station is along the wall from X1Y1. The
        /// placement and the anchor diagnostic both snap with it.
        /// </summary>
        internal static (double X, double Y, double StationMm) SnapToFace(ModelWall w, double side, double x, double y)
        {
            double length = Length(w);
            double ux = (w.X2 - w.X1) / length, uy = (w.Y2 - w.Y1) / length;
            double along = ((x - w.X1) * ux) + ((y - w.Y1) * uy);
            double station = Math.Max(0, Math.Min(length, along));
            double half = w.ThicknessMm / 2;
            return (w.X1 + (ux * station) - (uy * side * half), w.Y1 + (uy * station) + (ux * side * half), station);
        }

        /// <summary>The signed distance of (x, y) from a wall's centreline, + to the left.</summary>
        private static double Perp(ModelWall w, double x, double y)
        {
            double length = Length(w);
            double ux = (w.X2 - w.X1) / length, uy = (w.Y2 - w.Y1) / length;
            return ((x - w.X1) * -uy) + ((y - w.Y1) * ux);
        }

        /// <summary>True when an existing device stands within <paramref name="withinMm"/> of the
        /// point in plan and within <paramref name="withinMm"/> of its Z, so a light on the
        /// ceiling and a socket under it are not one device. OverheadPlacement.Matches with no
        /// category or level band known; kept here so this file stands alone in the headless build.</summary>
        public static bool IsPresent(double xMm, double yMm, double zMm,
                                     IReadOnlyList<(double X, double Y, double Z)> existing, double withinMm)
        {
            double limit = withinMm * withinMm;
            foreach ((double x, double y, double z) in existing)
            {
                double dx = x - xMm, dy = y - yMm;
                if ((dx * dx) + (dy * dy) <= limit && Math.Abs(z - zMm) <= withinMm) return true;
            }
            return false;
        }

        /// <summary>
        /// The Z a placement aims at: on a ceiling, the ceiling's underside (a mount height
        /// override does not take a device off its ceiling); otherwise the plan's Z with its
        /// mount height swapped for the override.
        /// </summary>
        public static double TargetZMm(PlannedPoint p, double? mountOverrideMm)
        {
            if (p.HostCeilingId.HasValue) return p.ZMm;
            return p.ZMm - p.MountHeightMm + (mountOverrideMm ?? p.MountHeightMm);
        }

        /// <summary>True when a ceiling device found no ceiling and its family cannot stand
        /// without one (ceiling-hosted, or another host-based kind): no_ceiling. Face, work plane
        /// and unhosted families still stand at the level plus the mount height.</summary>
        public static bool NoCeiling(PlannedPoint p, string host, string? hosting) =>
            host == "ceiling" && !p.HostCeilingId.HasValue && (hosting == "ceiling" || hosting == "hosted");

        /// <summary>True when a point snapped to a wall stands higher than that wall's top. A
        /// wall whose top was not read, a linked wall (nothing to host on), or a point with no
        /// height, is never above it.</summary>
        public static bool AboveWallTop(PlannedPoint p, ModelWall? wall) =>
            wall != null && !wall.Linked && wall.TopZMm.HasValue &&!p.Flags.Contains("no_height") && p.ZMm > wall.TopZMm.Value;

        /// <summary>The wall-host path: picks the wall, the side and the face, and returns the
        /// room the point faces into (null when none), with the wall picked.</summary>
        private static RoomHit? OnWall(ResolveInput s, IReadOnlyList<ModelWall> walls, SegmentIndex wallIndex,
                                       Func<double, double, RoomHit?> roomAt, ResolveOptions o, PlannedPoint p,
                                       out ModelWall? chosen, out double side)
        {
            chosen = null;
            side = 1;
            double maxHalf = 0;
            foreach (ModelWall w in walls) maxHalf = Math.Max(maxHalf, w.ThicknessMm / 2);

            // 1-2. Candidates within reach of the face and the ends; the nearest, and the most
            // central along its wall, wins.
            ModelWall? best = null;
            double bestScore = double.MaxValue, bestAlong = 0, bestPerp = 0;
            var at = new Point(s.XMm, s.YMm);
            foreach (int i in wallIndex.Near(new Segment(at, at), maxHalf + o.ReachMm))
            {
                ModelWall w = walls[i];
                double length = Length(w);
                if (length <= 0) continue;
                double dx = (w.X2 - w.X1) / length, dy = (w.Y2 - w.Y1) / length;
                double along = ((s.XMm - w.X1) * dx) + ((s.YMm - w.Y1) * dy);
                double perp = ((s.XMm - w.X1) * -dy) + ((s.YMm - w.Y1) * dx);
                if (Math.Abs(perp) > (w.ThicknessMm / 2) + o.ReachMm) continue;
                if (along < -o.AxialTolMm || along > length + o.AxialTolMm) continue;

                double score = (Math.Abs(perp) * 10) + (Math.Abs(along - (length / 2)) * 0.1);
                if (score >= bestScore) continue;
                best = w;
                bestScore = score;
                bestAlong = along;
                bestPerp = perp;
            }

            if (best == null)
            {
                AddFlag(p, "no_wall_in_reach");
                return roomAt(s.XMm, s.YMm);
            }

            chosen = best;
            double wallLength = Length(best);
            double ux = (best.X2 - best.X1) / wallLength, uy = (best.Y2 - best.Y1) / wallLength;
            double nx = -uy, ny = ux;
            double station = Math.Max(0, Math.Min(wallLength, bestAlong));
            double px = best.X1 + (ux * station), py = best.Y1 + (uy * station);
            double half = best.ThicknessMm / 2;

            // 3. The side the symbol is drawn on; on the centreline, the side with a room.
            side = bestPerp >= 0 ? 1 : -1;
            if (Math.Abs(bestPerp) < o.SideAmbiguityMm)
            {
                double probe = half + o.RoomProbeMm;
                bool plus = roomAt(px + (nx * probe), py + (ny * probe)) != null;
                bool minus = roomAt(px - (nx * probe), py - (ny * probe)) != null;
                if (plus == minus)
                {
                    side = 1;
                    AddFlag(p, "side_ambiguous");
                }
                else
                {
                    side = plus ? 1 : -1;
                }
            }

            // 4. Snap to that face, facing out of it. The symbol's own rotation is not used.
            (p.XMm, p.YMm, p.StationMm) = SnapToFace(best, side, s.XMm, s.YMm);
            p.FacingDx = nx * side;
            p.FacingDy = ny * side;
            p.WallLengthMm = wallLength;

            if (best.Linked)
            {
                AddFlag(p, "linked_wall");
            }
            else
            {
                p.HostWallId = best.Id;
                p.Host = "wall";
            }

            // 5. The room the point faces into, else the room at the drawn centre.
            return roomAt(p.XMm + (p.FacingDx * o.RoomProbeMm), p.YMm + (p.FacingDy * o.RoomProbeMm))
                   ?? roomAt(s.XMm, s.YMm);
        }

        /// <summary>Flags the later of two points of one role within DuplicateMm of each other
        /// (in plan): one symbol drawn twice.</summary>
        public static void MarkDuplicates(IReadOnlyList<PlannedPoint> points, ResolveOptions o)
        {
            double limit = o.DuplicateMm * o.DuplicateMm;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    if (!string.Equals(points[i].Role, points[j].Role, StringComparison.Ordinal)) continue;
                    if (points[j].Flags.Contains("duplicate_in_drawing")) continue;
                    double dx = points[i].XMm - points[j].XMm, dy = points[i].YMm - points[j].YMm;
                    if ((dx * dx) + (dy * dy) > limit) continue;
                    AddFlag(points[i], "duplicate_in_drawing");
                    break;
                }
            }
        }

        /// <summary>True when an insert around this one is itself a mapped placeable symbol:
        /// the outer one is placed, and this one is dropped as nested_in_mapped.</summary>
        public static bool NestedInMapped(IReadOnlyList<ScanSymbol> symbols, int index, Func<string, bool> isMappedPlaceable)
        {
            for (int at = symbols[index].Parent; at >= 0; at = symbols[at].Parent)
            {
                if (isMappedPlaceable(symbols[at].Key)) return true;
            }
            return false;
        }

        /// <summary>True when a family that hosts this way cannot take the mapping's host: a
        /// wall or ceiling family on another host, a floor or roof family anywhere, a hosted
        /// family with no host. Face, work plane and unhosted families take any host.</summary>
        public static bool HostMismatch(string host, string hosting)
        {
            switch (hosting)
            {
                case "wall": return host != "wall";
                case "ceiling": return host != "ceiling";
                case "floor":
                case "roof": return true;
                case "hosted": return host == "none";
                default: return false;
            }
        }

        internal static bool IsWet(string roomName, IReadOnlyList<string> keywords) =>
            keywords.Any(k => !string.IsNullOrWhiteSpace(k) && roomName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

        private static double Length(ModelWall w) =>
            Math.Sqrt(((w.X2 - w.X1) * (w.X2 - w.X1)) + ((w.Y2 - w.Y1) * (w.Y2 - w.Y1)));
    }
}
