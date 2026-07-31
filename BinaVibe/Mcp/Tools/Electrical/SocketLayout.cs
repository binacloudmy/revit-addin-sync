// Socket placement layout math — pure, Revit-free, MILLIMETRES ONLY.
//
// Split out of SocketCandidates.cs precisely so it can be linked into
// Tests/Tests.csproj (that project uses explicit <Compile Include> items with
// no globs, so anything touching Autodesk.Revit.DB is untestable). Same reason
// ConfirmGate.cs was split out of ToolLoopService.cs.
//
// UNITS: every number that crosses into this file is in mm. Note that
// ArgsHelp.GetPointMm (Mutators.cs:3348) and ArgsHelp.GetLengthMm
// (Mutators.cs:3334) read mm from the wire but return FEET for direct Revit
// API use — so they are NOT the boundary. The single ft<->mm conversion
// boundary for socket placement is SocketCandidates.cs; nothing in this file
// ever sees a foot.
//
// Curves are already flattened to polylines by the caller, so this file has no
// concept of an Arc — everything is chords.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>A plan-view point in mm (project-internal XY).</summary>
    public struct Pt2
    {
        public double XMm;
        public double YMm;
        public Pt2(double xMm, double yMm) { XMm = xMm; YMm = yMm; }
    }

    /// <summary>A blocked or usable stretch of a wall run's station axis, mm
    /// from the run start. Reason is carried for reporting only.</summary>
    public struct Interval
    {
        public double StartMm;
        public double EndMm;
        public string Reason;
        public Interval(double startMm, double endMm, string reason)
        {
            StartMm = startMm; EndMm = endMm; Reason = reason ?? "";
        }
        public double LengthMm => EndMm - StartMm;
    }

    /// <summary>One BoundarySegment after curve flattening. Revit splits a
    /// single physical wall into several of these at joins, so they get merged
    /// (see <see cref="SocketLayout.MergeRuns"/>) before any spacing walk.</summary>
    public sealed class RawSegment
    {
        /// <summary>Groups segments belonging to the same physical wall.
        /// "w:&lt;id&gt;" for a local wall, "lw:&lt;a&gt;:&lt;b&gt;" for a wall
        /// living in a Revit link.</summary>
        public string RunKey = "";
        /// <summary>Local wall this can be hosted on; null when the boundary
        /// comes from a linked model (nothing local to host against).</summary>
        public long? HostWallId;
        public int LoopIndex;
        public List<Pt2> Points = new();
    }

    /// <summary>One contiguous stretch of a single wall, with a monotone
    /// station axis 0..LengthMm.</summary>
    public sealed class WallRun
    {
        public string RunKey = "";
        public long? HostWallId;
        public int LoopIndex;
        public List<Pt2> Points = new();
        public double LengthMm;
        public List<Interval> Blocked = new();
        /// <summary>The full boundary loop this run belongs to. Used to resolve
        /// which side of the wall faces into the room — see
        /// <see cref="SocketLayout.NormalAt"/>.</summary>
        public IReadOnlyList<Pt2> LoopPolygon = Array.Empty<Pt2>();
    }

    /// <summary>Caller-supplied rule numbers. Deliberately neutral: no
    /// regulatory value is baked into the addin — the defaults live in the
    /// recipe (app/knowledge/revit_recipes/socket_placement_by_room.md) so a
    /// standards change does not require an addin release.</summary>
    public sealed class LayoutOptions
    {
        public double SpacingMm = 3500;
        public double CornerClearanceMm = 300;
        public double MinRunMm = 900;
        public double MountHeightMm = 300;
        public int MaxPerWall = 20;
        public int MaxPerRoom = 40;
    }

    /// <summary>One reviewable candidate point.</summary>
    public sealed class Candidate
    {
        public string RunKey = "";
        public long? HostWallId;
        public int LoopIndex;
        public double XMm;
        public double YMm;
        public double StationMm;
        public double WallLengthMm;
        public double FacingDx;
        public double FacingDy;
        public double MountHeightMm;
        /// <summary>"wall" when HostWallId is set, otherwise "unhosted".</summary>
        public string Host = "unhosted";
    }

    public sealed class LayoutResult
    {
        public List<Candidate> Candidates = new();
        public List<string> Notes = new();
    }

    public static class SocketLayout
    {
        /// <summary>Two points closer than this are the same point. Revit
        /// boundary segments meet exactly in theory and to ~1e-9 ft in
        /// practice; 1 mm is far below any real socket tolerance.</summary>
        public const double JoinTolMm = 1.0;

        // ── loops ────────────────────────────────────────────────────────

        /// <summary>Shoelace signed area in mm². Positive = counter-clockwise.
        /// Used to pick the OUTER boundary loop by largest |area| — Revit does
        /// not guarantee the outer loop is index 0, and trusting that puts
        /// sockets on a column face.</summary>
        public static double SignedArea(IReadOnlyList<Pt2> loop)
        {
            if (loop == null || loop.Count < 3) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                sum += (a.XMm * b.YMm) - (b.XMm * a.YMm);
            }
            return sum / 2.0;
        }

        /// <summary>Ray-cast point-in-polygon. Only ever called ~1 mm off a
        /// boundary edge, well away from corners (candidates are at least
        /// CornerClearanceMm in), so the classic on-edge ambiguity cannot
        /// bite.</summary>
        public static bool PointInPolygon(IReadOnlyList<Pt2> poly, Pt2 p)
        {
            if (poly == null || poly.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                bool straddles = (a.YMm > p.YMm) != (b.YMm > p.YMm);
                if (!straddles) continue;
                double t = (p.YMm - a.YMm) / (b.YMm - a.YMm);
                if (p.XMm < a.XMm + t * (b.XMm - a.XMm)) inside = !inside;
            }
            return inside;
        }

        // ── runs ─────────────────────────────────────────────────────────

        /// <summary>Collapse boundary segments into one run per contiguous
        /// stretch of wall.
        ///
        /// Without this the spacing walk restarts at every wall join and
        /// sockets cluster at junctions — the single most visible failure a
        /// drafter would reject. Handles Revit's two habits: emitting the same
        /// segment twice, and emitting it reversed.</summary>
        public static List<WallRun> MergeRuns(IReadOnlyList<RawSegment> segments)
        {
            var runs = new List<WallRun>();
            if (segments == null || segments.Count == 0) return runs;

            // Group by wall, preserving loop traversal order within each group.
            var order = new List<string>();
            var groups = new Dictionary<string, List<RawSegment>>(StringComparer.Ordinal);
            foreach (var seg in segments)
            {
                if (seg == null || seg.Points == null || seg.Points.Count < 2) continue;
                if (!groups.TryGetValue(seg.RunKey, out var list))
                {
                    list = new List<RawSegment>();
                    groups[seg.RunKey] = list;
                    order.Add(seg.RunKey);
                }
                list.Add(seg);
            }

            foreach (var key in order)
            {
                var group = groups[key];

                // Drop exact repeats, in either direction.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var unique = new List<RawSegment>();
                foreach (var seg in group)
                {
                    var a = seg.Points[0];
                    var b = seg.Points[seg.Points.Count - 1];
                    var fwd = EndpointKey(a, b);
                    var rev = EndpointKey(b, a);
                    if (seen.Contains(fwd) || seen.Contains(rev)) continue;
                    seen.Add(fwd);
                    unique.Add(seg);
                }

                // Chain head-to-tail, reversing a segment when it only connects
                // the other way round.
                var remaining = new List<RawSegment>(unique);
                while (remaining.Count > 0)
                {
                    var head = remaining[0];
                    remaining.RemoveAt(0);
                    var pts = new List<Pt2>(head.Points);

                    bool grew = true;
                    while (grew && remaining.Count > 0)
                    {
                        grew = false;
                        var tail = pts[pts.Count - 1];
                        for (int i = 0; i < remaining.Count; i++)
                        {
                            var cand = remaining[i];
                            var cs = cand.Points[0];
                            var ce = cand.Points[cand.Points.Count - 1];
                            if (Near(tail, cs))
                            {
                                for (int k = 1; k < cand.Points.Count; k++) pts.Add(cand.Points[k]);
                            }
                            else if (Near(tail, ce))
                            {
                                for (int k = cand.Points.Count - 2; k >= 0; k--) pts.Add(cand.Points[k]);
                            }
                            else continue;

                            remaining.RemoveAt(i);
                            grew = true;
                            break;
                        }
                    }

                    var run = new WallRun
                    {
                        RunKey = head.RunKey,
                        HostWallId = head.HostWallId,
                        LoopIndex = head.LoopIndex,
                        Points = pts,
                        LengthMm = PolylineLength(pts),
                    };
                    if (run.LengthMm > 0) runs.Add(run);
                }
            }

            return runs;
        }

        public static double PolylineLength(IReadOnlyList<Pt2> pts)
        {
            if (pts == null || pts.Count < 2) return 0.0;
            double total = 0.0;
            for (int i = 1; i < pts.Count; i++) total += Dist(pts[i - 1], pts[i]);
            return total;
        }

        // ── station axis ─────────────────────────────────────────────────

        /// <summary>Point at a station along the polyline. Clamps to the ends
        /// rather than extrapolating.</summary>
        public static Pt2 PointAt(IReadOnlyList<Pt2> pts, double stationMm)
        {
            if (pts == null || pts.Count == 0) return new Pt2(0, 0);
            if (pts.Count == 1 || stationMm <= 0) return pts[0];

            double walked = 0.0;
            for (int i = 1; i < pts.Count; i++)
            {
                double seg = Dist(pts[i - 1], pts[i]);
                if (seg <= 0) continue;
                if (stationMm <= walked + seg)
                {
                    double t = (stationMm - walked) / seg;
                    return new Pt2(
                        pts[i - 1].XMm + t * (pts[i].XMm - pts[i - 1].XMm),
                        pts[i - 1].YMm + t * (pts[i].YMm - pts[i - 1].YMm));
                }
                walked += seg;
            }
            return pts[pts.Count - 1];
        }

        /// <summary>Unit normal at a station, pointing INTO the room.
        ///
        /// Not derived from wall.Orientation (that is the wall's exterior
        /// normal — wrong sign for roughly half the rooms a wall bounds) and
        /// not from loop winding alone (a merged run's polyline direction can
        /// be reversed relative to loop travel). Instead: take either chord
        /// normal, step 1 mm along it, and keep the one that lands inside the
        /// loop polygon. Direction-agnostic and self-correcting.</summary>
        public static void NormalAt(WallRun run, double stationMm, out double dx, out double dy)
        {
            ChordDirAt(run.Points, stationMm, out double tx, out double ty);
            dx = -ty; dy = tx;

            if (run.LoopPolygon != null && run.LoopPolygon.Count >= 3)
            {
                var p = PointAt(run.Points, stationMm);
                var probe = new Pt2(p.XMm + dx * JoinTolMm, p.YMm + dy * JoinTolMm);
                if (!PointInPolygon(run.LoopPolygon, probe)) { dx = -dx; dy = -dy; }
            }
        }

        /// <summary>Unit tangent of the chord containing the station.</summary>
        public static void ChordDirAt(IReadOnlyList<Pt2> pts, double stationMm, out double dx, out double dy)
        {
            dx = 1.0; dy = 0.0;
            if (pts == null || pts.Count < 2) return;

            double walked = 0.0;
            for (int i = 1; i < pts.Count; i++)
            {
                double seg = Dist(pts[i - 1], pts[i]);
                if (seg <= 0) continue;
                if (stationMm <= walked + seg || i == pts.Count - 1)
                {
                    dx = (pts[i].XMm - pts[i - 1].XMm) / seg;
                    dy = (pts[i].YMm - pts[i - 1].YMm) / seg;
                    return;
                }
                walked += seg;
            }
        }

        /// <summary>Station of the polyline point closest to p. Used to map
        /// door/window bounding-box corners and plumbing fixtures onto the run
        /// axis so they can be blocked out.</summary>
        public static double ProjectStation(IReadOnlyList<Pt2> pts, Pt2 p)
        {
            if (pts == null || pts.Count == 0) return 0.0;
            if (pts.Count == 1) return 0.0;

            double walked = 0.0, bestStation = 0.0, bestDistSq = double.MaxValue;
            for (int i = 1; i < pts.Count; i++)
            {
                var a = pts[i - 1];
                var b = pts[i];
                double segLen = Dist(a, b);
                if (segLen <= 0) continue;

                double ux = (b.XMm - a.XMm) / segLen;
                double uy = (b.YMm - a.YMm) / segLen;
                double t = (p.XMm - a.XMm) * ux + (p.YMm - a.YMm) * uy;
                if (t < 0) t = 0; else if (t > segLen) t = segLen;

                double cx = a.XMm + ux * t;
                double cy = a.YMm + uy * t;
                double dsq = (p.XMm - cx) * (p.XMm - cx) + (p.YMm - cy) * (p.YMm - cy);
                if (dsq < bestDistSq) { bestDistSq = dsq; bestStation = walked + t; }

                walked += segLen;
            }
            return bestStation;
        }

        /// <summary>Perpendicular distance in mm from p to a run's polyline.
        /// Companion to <see cref="ProjectStation"/> — same projection, distance
        /// instead of station. double.MaxValue for a degenerate run, so a
        /// min-scan never picks one.</summary>
        public static double DistanceToRun(IReadOnlyList<Pt2> pts, Pt2 p)
        {
            if (pts == null || pts.Count < 2) return double.MaxValue;
            var onRun = PointAt(pts, ProjectStation(pts, p));
            return Dist(onRun, p);
        }

        /// <summary>Index of the run whose polyline is nearest p, or -1 when the
        /// list is empty or every run is degenerate.
        ///
        /// `ambiguous` is set when the runner-up is within tieTolMm of the
        /// winner: the point sits equidistant between two candidate runs — a
        /// party wall's centreline, where it belongs to neither room — and there
        /// is no honest way to pick a side. The caller must refuse rather than
        /// guess, because guessing here ships a socket facing into the
        /// neighbour's unit.</summary>
        public static int NearestRunIndex(IReadOnlyList<WallRun> runs, Pt2 p,
                                          double tieTolMm, out bool ambiguous)
        {
            ambiguous = false;
            if (runs == null || runs.Count == 0) return -1;

            int best = -1;
            double bestDist = double.MaxValue, runnerUpDist = double.MaxValue;
            for (int i = 0; i < runs.Count; i++)
            {
                double d = runs[i] == null ? double.MaxValue : DistanceToRun(runs[i].Points, p);
                if (d == double.MaxValue) continue;
                if (d < bestDist) { runnerUpDist = bestDist; bestDist = d; best = i; }
                else if (d < runnerUpDist) { runnerUpDist = d; }
            }

            if (best < 0) return -1;
            if (runnerUpDist != double.MaxValue && runnerUpDist - bestDist < tieTolMm)
                ambiguous = true;
            return best;
        }

        // ── intervals ────────────────────────────────────────────────────

        /// <summary>Usable stretches of a run: the full length minus corner
        /// clearance at both ends, minus every blocked interval. Never returns
        /// a zero- or negative-length interval.</summary>
        public static List<Interval> SubtractBlocked(
            double lengthMm, double cornerClearanceMm, IReadOnlyList<Interval> blocked)
        {
            var free = new List<Interval>();
            double lo = cornerClearanceMm;
            double hi = lengthMm - cornerClearanceMm;
            if (hi <= lo) return free;   // wall shorter than twice the corner clearance

            var merged = MergeIntervals(blocked, lo, hi);

            double cursor = lo;
            foreach (var b in merged)
            {
                if (b.StartMm > cursor) free.Add(new Interval(cursor, b.StartMm, ""));
                if (b.EndMm > cursor) cursor = b.EndMm;
                if (cursor >= hi) break;
            }
            if (cursor < hi) free.Add(new Interval(cursor, hi, ""));

            free.RemoveAll(iv => iv.LengthMm <= 0);
            return free;
        }

        /// <summary>Sort, clamp to [lo,hi] and coalesce overlapping/touching
        /// blocked intervals.</summary>
        public static List<Interval> MergeIntervals(IReadOnlyList<Interval> input, double lo, double hi)
        {
            var result = new List<Interval>();
            if (input == null || input.Count == 0) return result;

            var sorted = new List<Interval>();
            foreach (var iv in input)
            {
                double s = Math.Max(lo, Math.Min(iv.StartMm, iv.EndMm));
                double e = Math.Min(hi, Math.Max(iv.StartMm, iv.EndMm));
                if (e > s) sorted.Add(new Interval(s, e, iv.Reason));
            }
            if (sorted.Count == 0) return result;
            sorted.Sort((a, b) => a.StartMm.CompareTo(b.StartMm));

            var cur = sorted[0];
            for (int i = 1; i < sorted.Count; i++)
            {
                var nxt = sorted[i];
                if (nxt.StartMm <= cur.EndMm)
                {
                    if (nxt.EndMm > cur.EndMm) cur.EndMm = nxt.EndMm;
                }
                else { result.Add(cur); cur = nxt; }
            }
            result.Add(cur);
            return result;
        }

        // ── distribution ─────────────────────────────────────────────────

        /// <summary>Stations within one usable interval.
        ///
        /// CENTRED, not "walk from one end every spacing_mm". The naive walk
        /// leaves an asymmetric stub at the far end, accumulates float drift,
        /// and produces different output depending on which end of the wall
        /// Revit happened to hand back first. The centred form is symmetric,
        /// drift-free and invariant under run reversal — which is what makes
        /// it pinnable in a golden test.</summary>
        public static List<double> Stations(Interval free, LayoutOptions opts)
        {
            var stations = new List<double>();
            double len = free.LengthMm;
            if (opts == null || len <= 0 || len < opts.MinRunMm) return stations;

            double spacing = opts.SpacingMm > 0 ? opts.SpacingMm : len;
            int n = (int)Math.Floor(len / spacing);
            if (n < 1) n = 1;

            for (int i = 0; i < n; i++)
                stations.Add(free.StartMm + len * (2.0 * i + 1.0) / (2.0 * n));

            return stations;
        }

        // ── top level ────────────────────────────────────────────────────

        /// <summary>Candidate points for one room's wall runs. Caller has
        /// already populated each run's Blocked list (openings, wet radii,
        /// existing outlets) and LoopPolygon.</summary>
        public static LayoutResult Plan(IReadOnlyList<WallRun> runs, LayoutOptions opts)
        {
            var result = new LayoutResult();
            if (runs == null || opts == null) return result;

            foreach (var run in runs)
            {
                if (run == null || run.Points.Count < 2) continue;

                if (run.LengthMm < opts.MinRunMm)
                {
                    result.Notes.Add($"run {run.RunKey}: {Math.Round(run.LengthMm)}mm shorter than min_run_mm");
                    continue;
                }

                var free = SubtractBlocked(run.LengthMm, opts.CornerClearanceMm, run.Blocked);
                if (free.Count == 0)
                {
                    result.Notes.Add($"run {run.RunKey}: fully blocked or shorter than twice corner_clearance_mm");
                    continue;
                }

                int onThisRun = 0;
                foreach (var interval in free)
                {
                    foreach (var station in Stations(interval, opts))
                    {
                        if (onThisRun >= opts.MaxPerWall) break;
                        if (result.Candidates.Count >= opts.MaxPerRoom) return result;

                        var p = PointAt(run.Points, station);
                        NormalAt(run, station, out double nx, out double ny);

                        result.Candidates.Add(new Candidate
                        {
                            RunKey = run.RunKey,
                            HostWallId = run.HostWallId,
                            LoopIndex = run.LoopIndex,
                            XMm = p.XMm,
                            YMm = p.YMm,
                            StationMm = station,
                            WallLengthMm = run.LengthMm,
                            FacingDx = nx,
                            FacingDy = ny,
                            MountHeightMm = opts.MountHeightMm,
                            Host = run.HostWallId.HasValue ? "wall" : "unhosted",
                        });
                        onThisRun++;
                    }
                    if (onThisRun >= opts.MaxPerWall) break;
                }
            }

            return result;
        }

        // ── plan angles ──────────────────────────────────────────────────
        //
        // The arithmetic that decides which way a socket points lives here, not
        // in SocketPlacement.cs, so it is unit-testable rather than only
        // observable in Revit. Degrees, CCW positive, plan view.

        /// <summary>Signed angle from one plan vector to another, degrees,
        /// CCW positive, in (-180, 180]. Returns 0 if either is degenerate.
        ///
        /// Same convention as Mutators.ReplaceCrossFamily (Mutators.cs:176):
        /// magnitude from the angle between, sign from the cross product's Z.
        /// Rotating `from` by this result lands exactly on `to`.</summary>
        public static double SignedAngleDeg(double fromDx, double fromDy, double toDx, double toDy)
        {
            double fl = Math.Sqrt(fromDx * fromDx + fromDy * fromDy);
            double tl = Math.Sqrt(toDx * toDx + toDy * toDy);
            if (fl < 1e-9 || tl < 1e-9) return 0.0;

            double fx = fromDx / fl, fy = fromDy / fl;
            double tx = toDx / tl, ty = toDy / tl;

            double cross = fx * ty - fy * tx;   // z of the 2D cross product
            double dot = fx * tx + fy * ty;
            return Math.Atan2(cross, dot) * 180.0 / Math.PI;
        }

        /// <summary>Unsigned angle between two plan vectors, 0..180 degrees.
        /// This is the error measure — a socket 90 degrees out is equally wrong
        /// whichever way it was turned.</summary>
        public static double AbsAngleDeg(double fromDx, double fromDy, double toDx, double toDy)
            => Math.Abs(SignedAngleDeg(fromDx, fromDy, toDx, toDy));

        /// <summary>Rotate a plan vector by an angle in degrees, CCW positive.
        /// Returns a UNIT vector; a degenerate input comes back unchanged.</summary>
        public static void ApplyOffsetDeg(double dx, double dy, double offsetDeg,
                                          out double outDx, out double outDy)
        {
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) { outDx = dx; outDy = dy; return; }

            double ux = dx / len, uy = dy / len;
            double r = offsetDeg * Math.PI / 180.0;
            double c = Math.Cos(r), s = Math.Sin(r);
            outDx = ux * c - uy * s;
            outDy = ux * s + uy * c;
        }

        // ── small helpers ────────────────────────────────────────────────

        public static double Dist(Pt2 a, Pt2 b)
        {
            double dx = a.XMm - b.XMm, dy = a.YMm - b.YMm;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static bool Near(Pt2 a, Pt2 b) => Dist(a, b) <= JoinTolMm;

        private static string EndpointKey(Pt2 a, Pt2 b) =>
            $"{Q(a.XMm)},{Q(a.YMm)}|{Q(b.XMm)},{Q(b.YMm)}";

        private static long Q(double mm) => (long)Math.Round(mm / JoinTolMm);
    }
}
