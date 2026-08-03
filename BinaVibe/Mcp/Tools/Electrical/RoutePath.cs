// Route path generation — pure, Revit-free, MILLIMETRES ONLY.
//
// The pluggable path strategy for circuit routing. RoutePlanner.cs (Revit-
// bound) picks a strategy by name and walks each circuit's hops through it;
// swapping Manhattan for A* later means adding a class here and a string
// there — nothing around it changes.
//
// Types are internal (not public) because RouteRequest/PathResult carry
// Pt3Mm, which GeomMm.cs declares internal. Tests link this source directly,
// so internal is fully testable.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One point-to-point routing job.</summary>
    internal sealed class RouteRequest
    {
        public Pt3Mm Start;
        public Pt3Mm End;
        /// <summary>Z (project-internal mm) horizontal runs travel at.</summary>
        public double RoutingElevationMm;
        /// <summary>Optional obstacle probe: true when the straight leg a-b is
        /// clear. Null = assume clear (propose stays cheap by default).</summary>
        public Func<Pt3Mm, Pt3Mm, bool>? IsClear;
    }

    /// <summary>A polyline path; legs are consecutive vertex pairs.</summary>
    internal sealed class PathResult
    {
        public bool Ok;
        public List<Pt3Mm> Vertices = new();
        public List<string> Notes = new();

        public double TotalLengthMm
        {
            get
            {
                double sum = 0;
                for (int i = 1; i < Vertices.Count; i++)
                {
                    double dx = Vertices[i].X - Vertices[i - 1].X;
                    double dy = Vertices[i].Y - Vertices[i - 1].Y;
                    double dz = Vertices[i].Z - Vertices[i - 1].Z;
                    sum += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                }
                return sum;
            }
        }
    }

    internal interface IRoutePathStrategy
    {
        PathResult Plan(RouteRequest req);
    }

    /// <summary>Manhattan (orthogonal) routing: rise from Start to the routing
    /// elevation, orthogonal X/Y travel at that elevation, drop to End.
    ///
    /// Between the two elbow variants (X-then-Y vs Y-then-X) the strategy
    /// prefers the one whose plan legs probe clear when an IsClear probe is
    /// supplied; both blocked -> X-first plus a note (no auto-reroute — the
    /// obstruction report is the deliverable, per product decision). Without a
    /// probe the choice is X-first, deterministically.</summary>
    internal sealed class ManhattanRouteStrategy : IRoutePathStrategy
    {
        /// <summary>Legs shorter than this are collapsed — mirrors
        /// SocketLayout.JoinTolMm.</summary>
        public const double JoinTolMm = 1.0;

        public PathResult Plan(RouteRequest req)
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            var res = new PathResult { Ok = true };
            double z = req.RoutingElevationMm;

            var a = new Pt3Mm(req.Start.X, req.Start.Y, z);
            var b = new Pt3Mm(req.End.X, req.End.Y, z);

            // Elbow candidates at routing elevation.
            var viaXFirst = new Pt3Mm(b.X, a.Y, z);
            var viaYFirst = new Pt3Mm(a.X, b.Y, z);

            Pt3Mm corner = viaXFirst;
            bool degenerate = Math.Abs(a.X - b.X) < JoinTolMm || Math.Abs(a.Y - b.Y) < JoinTolMm;
            if (!degenerate && req.IsClear != null)
            {
                bool xClear = req.IsClear(a, viaXFirst) && req.IsClear(viaXFirst, b);
                bool yClear = req.IsClear(a, viaYFirst) && req.IsClear(viaYFirst, b);
                if (xClear && !yClear) corner = viaXFirst;
                else if (yClear && !xClear) corner = viaYFirst;
                else if (!xClear && !yClear)
                {
                    corner = viaXFirst;
                    res.Notes.Add("both orthogonal variants probe obstructed — kept X-first, review the obstruction report");
                }
            }

            var verts = new List<Pt3Mm> { req.Start, a };
            if (!degenerate) verts.Add(corner);
            verts.Add(b);
            verts.Add(req.End);

            res.Vertices = Collapse(verts);
            if (res.Vertices.Count < 2)
            {
                // Start and End coincide within tolerance — nothing to route.
                res.Ok = false;
                res.Notes.Add("start and end coincide — no path");
            }
            return res;
        }

        /// <summary>Drop consecutive duplicates and merge collinear runs so a
        /// degenerate rise/elbow never becomes a zero-length conduit.</summary>
        internal static List<Pt3Mm> Collapse(IReadOnlyList<Pt3Mm> verts)
        {
            var outv = new List<Pt3Mm>();
            foreach (var v in verts)
            {
                if (outv.Count > 0 && Dist(outv[outv.Count - 1], v) < JoinTolMm) continue;
                outv.Add(v);
            }
            // Merge collinear middle vertices (axis-aligned only — all
            // Manhattan legs are, so plain per-axis equality suffices).
            for (int i = outv.Count - 2; i >= 1; i--)
            {
                var p = outv[i - 1]; var m = outv[i]; var n = outv[i + 1];
                bool sameX = Math.Abs(p.X - m.X) < JoinTolMm && Math.Abs(m.X - n.X) < JoinTolMm;
                bool sameY = Math.Abs(p.Y - m.Y) < JoinTolMm && Math.Abs(m.Y - n.Y) < JoinTolMm;
                bool sameZ = Math.Abs(p.Z - m.Z) < JoinTolMm && Math.Abs(m.Z - n.Z) < JoinTolMm;
                if ((sameX && sameY) || (sameX && sameZ) || (sameY && sameZ))
                    outv.RemoveAt(i);
            }
            return outv;
        }

        private static double Dist(Pt3Mm a, Pt3Mm b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    internal static class RouteStrategies
    {
        /// <summary>Resolve a strategy by wire name. Unknown -> null; the
        /// caller soft-fails listing SupportedNames.</summary>
        public static IRoutePathStrategy? ByName(string? name)
        {
            switch ((name ?? "manhattan").Trim().ToLowerInvariant())
            {
                case "manhattan": return new ManhattanRouteStrategy();
                default: return null;
            }
        }

        public static readonly string[] SupportedNames = { "manhattan" };
    }
}
