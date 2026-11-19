#nullable enable
// CadSymbolResolve - phase 1 of the CAD electrical to BIM work (spec 2026-11-17 §5.5).
//
// One drawn symbol, already carried into model coordinates by the fit, becomes one plan
// point: snapped to the face of the wall it is drawn against, facing into the room, at the
// mapping's height above that room's floor, with the review flags that apply. The pure core:
// walls, rooms and existing devices come in as plain data and a "room at this point"
// delegate, so the Revit side (CadElectricalResolver) only reads the model.
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
    }

    internal sealed class RoomHit
    {
        public long Id;
        public string Name = "";
        public double FloorZMm;
        public bool Linked;
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
    }

    internal static class CadSymbolResolve
    {
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
                                           IReadOnlyList<(double X, double Y, double Z)> existing, ResolveOptions o)
        {
            var p = new PlannedPoint
            {
                Role = s.Role,
                Source = "cad",
                XMm = s.XMm,
                YMm = s.YMm,
                Host = "unhosted",
            };

            RoomHit? room;
            if (s.Host == "wall")
            {
                room = OnWall(s, walls, wallIndex, roomAt, o, p);
            }
            else
            {
                // At the drawn centre. A ceiling host is looked for on the Revit side (commit
                // 11); until then a ceiling device stands where a host "none" one does.
                room = roomAt(s.XMm, s.YMm);
            }

            if (room == null) AddFlag(p, "no_room");
            else
            {
                p.RoomId = room.Id;
                p.RoomName = room.Name;
                if (IsWet(room.Name, o.WetKeywords)) AddFlag(p, "wet_room");
            }

            if (s.MountHeightMm.HasValue)
            {
                p.MountHeightMm = s.MountHeightMm.Value;
                p.ZMm = (room != null ? room.FloorZMm : levelZMm) + s.MountHeightMm.Value;
            }
            else
            {
                p.ZMm = room != null ? room.FloorZMm : levelZMm;
                AddFlag(p, "no_height");
            }

            double limit = o.ExistingMm * o.ExistingMm;
            foreach ((double x, double y, double z) in existing)
            {
                double dx = x - p.XMm, dy = y - p.YMm, dz = z - p.ZMm;
                if ((dx * dx) + (dy * dy) + (dz * dz) > limit) continue;
                AddFlag(p, "already_present");
                break;
            }

            return p;
        }

        /// <summary>The wall-host path: picks the wall, the side and the face, and returns the
        /// room the point faces into (null when none).</summary>
        private static RoomHit? OnWall(ResolveInput s, IReadOnlyList<ModelWall> walls, SegmentIndex wallIndex,
                                       Func<double, double, RoomHit?> roomAt, ResolveOptions o, PlannedPoint p)
        {
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

            double wallLength = Length(best);
            double ux = (best.X2 - best.X1) / wallLength, uy = (best.Y2 - best.Y1) / wallLength;
            double nx = -uy, ny = ux;
            double station = Math.Max(0, Math.Min(wallLength, bestAlong));
            double px = best.X1 + (ux * station), py = best.Y1 + (uy * station);
            double half = best.ThicknessMm / 2;

            // 3. The side the symbol is drawn on; on the centreline, the side with a room.
            double side = bestPerp >= 0 ? 1 : -1;
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
            p.XMm = px + (nx * side * half);
            p.YMm = py + (ny * side * half);
            p.FacingDx = nx * side;
            p.FacingDy = ny * side;
            p.StationMm = station;
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
