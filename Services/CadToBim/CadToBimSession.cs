// CadToBimSession — everything the pane knows about one drawing between "Detecting…"
// and Confirm: the detect output, the drafter's corrections (Erased, Forced), and
// which walls an earlier Confirm already built. Pure C#: no Revit, no WPF, so the
// Confirm diff (Pending) is unit-tested rather than trusted.
//
// Identity is by reference. Cad2Bim.Wall does not override Equals, and that is what
// we want: a re-detect makes new Wall instances, SetWalls prunes erasures that point
// at instances no longer in play, and CarryForwardFrom re-keys what should survive a
// re-detect (erase marks, built ids) by centreline instead.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim.Services;
using CadWall = Cad2Bim.Wall;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class CadToBimSession
    {
        public string DrawingPath;

        /// <summary>Millimetres per drawing unit, as Units.Resolve decided.</summary>
        public double Scale = 1.0;

        /// <summary>The loaded drawing as the engine's service object (contract compatibility;
        /// the pane re-elaborates from Model instead).</summary>
        public ClassificationService Service;

        /// <summary>The drawing in millimetres (segments, arcs, texts, outlines) so openings and
        /// rooms can be re-derived after an erase or a brush without re-reading the file.</summary>
        public Cad2Bim.CadModel Model;

        public List<CadWall> Walls = new();
        public List<Cad2Bim.Opening> Openings = new();
        public List<Cad2Bim.Space> Spaces = new();

        /// <summary>Walls the drafter clicked away. Toggle, so a mis-click undoes itself.</summary>
        public HashSet<CadWall> Erased = new();

        /// <summary>Walls the brush produced. Independent of the detect pass.</summary>
        public List<CadWall> Forced = new();

        /// <summary>CAD wall -> Revit ElementId value for every wall a Confirm built.
        /// Never pruned here: erasing after a build hides the wall from the pane, it does
        /// not delete it — undo is the drafter's tool for that.</summary>
        public Dictionary<CadWall, long> BuiltWallIds = new();

        /// <summary>Thickness a single-line brush wall is given. The drawing's own
        /// median beats any constant; 100 mm until a detect pass has run.</summary>
        public double MedianThicknessMm = 100.0;

        public int ErasedCount => Erased.Count;
        public int ForcedCount => Forced.Count;
        public int BuiltCount => BuiltWallIds.Count;

        /// <summary>Detect output arrives here. Recomputes the median and drops
        /// erasures that pointed at instances of the previous pass.</summary>
        public void SetWalls(List<CadWall> walls)
        {
            Walls = walls ?? new List<CadWall>();
            MedianThicknessMm = Median(Walls, MedianThicknessMm);

            var live = new HashSet<CadWall>(Walls);
            live.UnionWith(Forced);
            Erased.RemoveWhere(wall => !live.Contains(wall));
        }

        /// <summary>Upper-middle median (same formula the old convert command used:
        /// sorted[count / 2]); the fallback when there are no walls.</summary>
        internal static double Median(IReadOnlyList<CadWall> walls, double fallback)
        {
            if (walls == null || walls.Count == 0) return fallback;

            List<double> thicknesses = walls.Select(wall => wall.Thickness).OrderBy(t => t).ToList();
            return thicknesses[thicknesses.Count / 2];
        }

        /// <summary>(Walls ∪ Forced) − Erased, detected walls first, no duplicates.</summary>
        public List<CadWall> Active()
        {
            var active = new List<CadWall>(Walls.Count + Forced.Count);
            var seen = new HashSet<CadWall>();

            foreach (CadWall wall in Walls)
            {
                if (Erased.Contains(wall) || !seen.Add(wall)) continue;
                active.Add(wall);
            }

            foreach (CadWall wall in Forced)
            {
                if (Erased.Contains(wall) || !seen.Add(wall)) continue;
                active.Add(wall);
            }

            return active;
        }

        /// <summary>What the next Confirm builds: Active() minus what an earlier one built.</summary>
        public List<CadWall> Pending() =>
            Active().Where(wall => !BuiltWallIds.ContainsKey(wall)).ToList();

        public void ToggleErase(CadWall wall)
        {
            if (wall == null) return;
            if (!Erased.Remove(wall)) Erased.Add(wall);
        }

        /// <summary>Brush results. Deduped by reference against Forced and Walls.</summary>
        public void AddForced(IEnumerable<CadWall> walls)
        {
            if (walls == null) return;

            var known = new HashSet<CadWall>(Walls);
            known.UnionWith(Forced);

            foreach (CadWall wall in walls)
            {
                if (wall == null || !known.Add(wall)) continue;
                Forced.Add(wall);
            }
        }

        /// <summary>Records what a build created. A failed report (Error set) was rolled
        /// back, so it records nothing.</summary>
        public void MarkBuilt(BuildReport report)
        {
            if (report == null || !report.Ok || report.WallIds == null) return;

            foreach (var (key, value) in report.WallIds)
            {
                if (key == null) continue;
                BuiltWallIds[key] = value;
            }
        }

        /// <summary>
        /// After a re-detect of the SAME drawing: the previous session's erase marks and built
        /// ids follow their centrelines onto this session's new Wall instances; brushed walls
        /// are the drafter's own objects and come across by identity, erased/built state and all.
        /// Nothing from the previous session's Walls list itself is kept — those instances are
        /// dead. Call after SetWalls.
        /// </summary>
        public void CarryForwardFrom(CadToBimSession previous)
        {
            if (previous == null) return;

            var byKey = new Dictionary<string, CadWall>(StringComparer.Ordinal);
            foreach (CadWall wall in Walls) byKey[CenterlineKey(wall)] = wall;

            foreach (CadWall old in previous.Erased)
            {
                if (byKey.TryGetValue(CenterlineKey(old), out CadWall match)) Erased.Add(match);
            }

            foreach (var (oldWall, id) in previous.BuiltWallIds)
            {
                if (byKey.TryGetValue(CenterlineKey(oldWall), out CadWall match)) BuiltWallIds[match] = id;
            }

            foreach (CadWall forced in previous.Forced)
            {
                AddForced(new[] { forced });
                if (previous.Erased.Contains(forced)) Erased.Add(forced);
                if (previous.BuiltWallIds.TryGetValue(forced, out long id)) BuiltWallIds[forced] = id;
            }
        }

        /// <summary>"x1,y1,x2,y2" of the centreline rounded to whole millimetres, endpoints
        /// ordered so the same wall drawn either way gives the same key.</summary>
        public static string CenterlineKey(CadWall wall)
        {
            long x1 = (long)Math.Round(wall.Centerline.P1.x), y1 = (long)Math.Round(wall.Centerline.P1.y);
            long x2 = (long)Math.Round(wall.Centerline.P2.x), y2 = (long)Math.Round(wall.Centerline.P2.y);
            bool swap = x1 > x2 || (x1 == x2 && y1 > y2);
            return swap ? x2 + "," + y2 + "," + x1 + "," + y1 : x1 + "," + y1 + "," + x2 + "," + y2;
        }
    }
}
