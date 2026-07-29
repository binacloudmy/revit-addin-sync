// Pure double-precision 2D solver behind cad_walls_to_centerlines.
//
// Split out of the Revit-facing tool (CadWallsToCenterlines.cs) precisely so it
// compiles AND RUNS in Tests.csproj — that project references a REFERENCE-ONLY
// Revit API (metadata, no method bodies), so any `new XYZ()` / `Line.CreateBound`
// throws at runtime. This file touches no Revit type: it consumes the plain
// *_ft doubles CadExtract already emits and returns plain doubles. All the same
// formulas the spec cites, ported to 2D doubles:
//   - parallel test  = cross scalar          (Coordination.cs:125)
//   - perp gap        = v - dir*(v·dir)       (Coordination.cs:138-141)
//   - axis projection = dot product           (Coordination.cs:140)
//   - midline point   = (faceI + faceJ)*0.5   (Mutators.cs:212-222 averaging idiom)
// Everything is in FEET (CadExtract's *_ft space); the tool converts to mm once
// at emit. Walls are planar, so Z is dropped here and set from the level later.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>One straight CAD edge, projected to plan (feet).</summary>
    public readonly struct WallSeg
    {
        public readonly double Ax, Ay, Bx, By;
        public readonly string Layer;
        public WallSeg(double ax, double ay, double bx, double by, string layer)
        { Ax = ax; Ay = ay; Bx = bx; By = by; Layer = layer; }

        public double Len => Math.Sqrt((Bx - Ax) * (Bx - Ax) + (By - Ay) * (By - Ay));
    }

    /// <summary>A computed wall centerline (feet). Endpoints are mutable — junction
    /// cleanup trims/extends them.</summary>
    public sealed class Centerline
    {
        public double Ax, Ay, Bx, By;
        public double ThicknessFt;
        public string Layer = "";
    }

    public sealed class SolveOptions
    {
        public const double MmPerFoot = 304.8;

        public double MinThicknessFt = 50 / MmPerFoot;
        public double MaxThicknessFt = 500 / MmPerFoot;
        public double SinAngleTol = Math.Sin(1.5 * Math.PI / 180.0); // 1.5°
        public double OverlapMinRatio = 0.5;
        public double MinSegLenFt = 300 / MmPerFoot;
        public double SnapFt = 500 / MmPerFoot; // default = max thickness (spec)

        public static SolveOptions FromMm(double minThickMm, double maxThickMm,
            double angleTolDeg, double overlapMinRatio, double minSegLenMm, double snapMm)
            => new SolveOptions
            {
                MinThicknessFt = minThickMm / MmPerFoot,
                MaxThicknessFt = maxThickMm / MmPerFoot,
                SinAngleTol = Math.Sin(angleTolDeg * Math.PI / 180.0),
                OverlapMinRatio = overlapMinRatio,
                MinSegLenFt = minSegLenMm / MmPerFoot,
                SnapFt = snapMm / MmPerFoot,
            };
    }

    public sealed class SolveResult
    {
        public List<Centerline> Walls = new();
        public int UnpairedSegments;
        public int JunctionsSnapped;
    }

    public static class CadCenterlineSolver
    {
        // ─── tiny 2D vector helpers (no Revit) ──────────────────────────────
        private static double Dot(double ax, double ay, double bx, double by) => ax * bx + ay * by;
        // XY cross magnitude — equals XYZ.CrossProduct(...).GetLength() for planar
        // vectors (Coordination.cs:125). Signed here so it doubles as the line
        // intersection determinant.
        private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
        private static double Dist(double ax, double ay, double bx, double by)
            => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

        private static (double ux, double uy, double len) Unit(in WallSeg s)
        {
            double dx = s.Bx - s.Ax, dy = s.By - s.Ay;
            double len = Math.Sqrt(dx * dx + dy * dy);
            return len < 1e-12 ? (1, 0, 0) : (dx / len, dy / len, len);
        }

        private sealed class Cand
        {
            public int I, J;
            public double GapFt, Score, OvLo, OvHi; // overlap span in param along seg I's dir
        }

        public static SolveResult Solve(IReadOnlyList<WallSeg> segsRaw, SolveOptions opt)
        {
            // Step A — keep only segments long enough to be a wall face.
            var segs = segsRaw.Where(s => s.Len >= opt.MinSegLenFt).ToList();
            var dirs = segs.Select(s => Unit(s)).ToArray();

            // Step B — parallel pairing.
            var cands = new List<Cand>();
            for (int i = 0; i < segs.Count; i++)
            {
                var (uxi, uyi, leni) = dirs[i];
                if (leni == 0) continue;
                for (int j = i + 1; j < segs.Count; j++)
                {
                    if (!string.Equals(segs[i].Layer, segs[j].Layer, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var (uxj, uyj, lenj) = dirs[j];
                    if (lenj == 0) continue;

                    // parallel? cross of unit dirs = sin(angle).
                    if (Math.Abs(Cross(uxi, uyi, uxj, uyj)) > opt.SinAngleTol) continue;

                    // perpendicular gap = wall thickness (Coordination.cs:138-141).
                    double vx = segs[j].Ax - segs[i].Ax, vy = segs[j].Ay - segs[i].Ay;
                    double t = Dot(vx, vy, uxi, uyi);
                    double perpx = vx - t * uxi, perpy = vy - t * uyi;
                    double gap = Math.Sqrt(perpx * perpx + perpy * perpy);
                    if (gap < opt.MinThicknessFt || gap > opt.MaxThicknessFt) continue;

                    // overlap along seg I's axis (param from I.A). Seg I spans [0,leni].
                    double pjA = Dot(segs[j].Ax - segs[i].Ax, segs[j].Ay - segs[i].Ay, uxi, uyi);
                    double pjB = Dot(segs[j].Bx - segs[i].Ax, segs[j].By - segs[i].Ay, uxi, uyi);
                    double jLo = Math.Min(pjA, pjB), jHi = Math.Max(pjA, pjB);
                    double ovLo = Math.Max(0, jLo), ovHi = Math.Min(leni, jHi);
                    double overlap = ovHi - ovLo;
                    if (overlap <= 0) continue;
                    double ratio = overlap / Math.Min(leni, lenj);
                    if (ratio < opt.OverlapMinRatio) continue;

                    cands.Add(new Cand
                    {
                        I = i, J = j, GapFt = gap,
                        Score = ratio - gap / opt.MaxThicknessFt,
                        OvLo = ovLo, OvHi = ovHi,
                    });
                }
            }

            // Step C — greedy: best score first, each segment used once.
            var result = new SolveResult();
            var used = new bool[segs.Count];
            foreach (var c in cands.OrderByDescending(c => c.Score))
            {
                if (used[c.I] || used[c.J]) continue;
                used[c.I] = used[c.J] = true;
                result.Walls.Add(BuildCenterline(segs[c.I], dirs[c.I], segs[c.J], dirs[c.J], c));
            }
            for (int i = 0; i < segs.Count; i++) if (!used[i]) result.UnpairedSegments++;

            // Steps: junction cleanup.
            result.JunctionsSnapped = CleanupJunctions(result.Walls, opt);
            return result;
        }

        // Step D — midline over the overlap span. Face-I point at axial t is I.A + t*uI
        // (seg I is colinear with uI). The matching point on the parallel face J is
        // found by walking J to the same axial coordinate, then the two are averaged.
        private static Centerline BuildCenterline(
            WallSeg si, (double ux, double uy, double len) di,
            WallSeg sj, (double ux, double uy, double len) dj, Cand c)
        {
            double sign = Dot(di.ux, di.uy, dj.ux, dj.uy) >= 0 ? 1 : -1; // J may run opposite
            double pjA = Dot(sj.Ax - si.Ax, sj.Ay - si.Ay, di.ux, di.uy); // axial coord of J.A

            (double x, double y) Mid(double tAxial)
            {
                double fix = si.Ax + tAxial * di.ux, fiy = si.Ay + tAxial * di.uy;   // face I
                double delta = (tAxial - pjA) / sign;                                // walk J to axial t
                double fjx = sj.Ax + delta * dj.ux, fjy = sj.Ay + delta * dj.uy;     // face J
                return ((fix + fjx) * 0.5, (fiy + fjy) * 0.5);                        // average
            }

            var (ax, ay) = Mid(c.OvLo);
            var (bx, by) = Mid(c.OvHi);
            return new Centerline { Ax = ax, Ay = ay, Bx = bx, By = by, ThicknessFt = c.GapFt, Layer = si.Layer };
        }

        // ─── junction / corner cleanup ──────────────────────────────────────
        private static int CleanupJunctions(List<Centerline> walls, SolveOptions opt)
        {
            MergeCollinear(walls, opt);
            return SnapCorners(walls, opt);
        }

        // Collinear-join: two centerlines on the same axis with a small end gap →
        // one wall spanning both far ends. Kills mid-run breaks (CAD split a wall at
        // a door/column). Iterates until no pair merges.
        private static void MergeCollinear(List<Centerline> walls, SolveOptions opt)
        {
            for (int guard = 0; guard < 1000; guard++)
            {
                bool merged = false;
                for (int i = 0; i < walls.Count && !merged; i++)
                    for (int j = i + 1; j < walls.Count && !merged; j++)
                        if (TryMergeCollinear(walls, i, j, opt)) { walls.RemoveAt(j); merged = true; }
                if (!merged) return;
            }
        }

        private static bool TryMergeCollinear(List<Centerline> w, int i, int j, SolveOptions opt)
        {
            var a = w[i]; var b = w[j];
            double aux = a.Bx - a.Ax, auy = a.By - a.Ay;
            double al = Math.Sqrt(aux * aux + auy * auy);
            if (al < 1e-9) return false;
            aux /= al; auy /= al;
            double bux = b.Bx - b.Ax, buy = b.By - b.Ay;
            double bl = Math.Sqrt(bux * bux + buy * buy);
            if (bl < 1e-9) return false;
            bux /= bl; buy /= bl;

            if (Math.Abs(Cross(aux, auy, bux, buy)) > opt.SinAngleTol) return false; // not parallel

            // colinear: both of b's endpoints sit on a's infinite line.
            if (PerpDist(a, aux, auy, b.Ax, b.Ay) > opt.SnapFt) return false;
            if (PerpDist(a, aux, auy, b.Bx, b.By) > opt.SnapFt) return false;

            // axial spans along a's dir from a.A; a spans [0, al].
            double pbA = Dot(b.Ax - a.Ax, b.Ay - a.Ay, aux, auy);
            double pbB = Dot(b.Bx - a.Ax, b.By - a.Ay, aux, auy);
            double bLo = Math.Min(pbA, pbB), bHi = Math.Max(pbA, pbB);
            double gap = bLo > al ? bLo - al : (bHi < 0 ? -bHi : 0); // end-gap between [0,al] and [bLo,bHi]
            if (gap > opt.SnapFt) return false;

            // new span [lo,hi] along a's dir, measured from a's ORIGINAL anchor (a.A).
            double ox = a.Ax, oy = a.Ay;
            double lo = Math.Min(0, bLo), hi = Math.Max(al, bHi);
            a.Ax = ox + lo * aux; a.Ay = oy + lo * auy;
            a.Bx = ox + hi * aux; a.By = oy + hi * auy;
            a.ThicknessFt = (a.ThicknessFt + b.ThicknessFt) * 0.5;
            return true;
        }

        private static double PerpDist(Centerline line, double ux, double uy, double px, double py)
        {
            double vx = px - line.Ax, vy = py - line.Ay;
            double t = Dot(vx, vy, ux, uy);
            double perpx = vx - t * ux, perpy = vy - t * uy;
            return Math.Sqrt(perpx * perpx + perpy * perpy);
        }

        // L/T snapping: move each non-parallel pair's nearby endpoints onto the
        // intersection of their infinite lines. L-corner = both move; T = only the
        // endpoint within snap moves (the through-wall's interior is left alone and
        // Revit auto-joins the wall end into it).
        private static int SnapCorners(List<Centerline> walls, SolveOptions opt)
        {
            int snapped = 0;
            for (int i = 0; i < walls.Count; i++)
                for (int j = i + 1; j < walls.Count; j++)
                {
                    var a = walls[i]; var b = walls[j];
                    double aux = a.Bx - a.Ax, auy = a.By - a.Ay;
                    double al = Math.Sqrt(aux * aux + auy * auy); if (al < 1e-9) continue; aux /= al; auy /= al;
                    double bux = b.Bx - b.Ax, buy = b.By - b.Ay;
                    double bl = Math.Sqrt(bux * bux + buy * buy); if (bl < 1e-9) continue; bux /= bl; buy /= bl;

                    double crs = Cross(aux, auy, bux, buy);
                    if (Math.Abs(crs) < 1e-9) continue; // parallel — handled by merge

                    // intersection of infinite lines a(A + t*ua) and b(A + s*ub).
                    double wx = b.Ax - a.Ax, wy = b.Ay - a.Ay;
                    double t = Cross(wx, wy, bux, buy) / crs;
                    double xX = a.Ax + t * aux, xY = a.Ay + t * auy;

                    bool moved = SnapNearEndpoint(a, xX, xY, opt.SnapFt);
                    moved |= SnapNearEndpoint(b, xX, xY, opt.SnapFt);
                    if (moved) snapped++;
                }
            return snapped;
        }

        // Move whichever endpoint (A or B) is closer to X, but only if within snap.
        private static bool SnapNearEndpoint(Centerline c, double xX, double xY, double snapFt)
        {
            double dA = Dist(c.Ax, c.Ay, xX, xY);
            double dB = Dist(c.Bx, c.By, xX, xY);
            if (dA <= dB) { if (dA > snapFt) return false; c.Ax = xX; c.Ay = xY; }
            else { if (dB > snapFt) return false; c.Bx = xX; c.By = xY; }
            return true;
        }
    }
}
