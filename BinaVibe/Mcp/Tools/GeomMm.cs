// Geometry math for obstruction queries — pure, Revit-free, MILLIMETRES ONLY.
//
// Split out of QueryGeometry.cs / CorridorCheck.cs precisely so it can be
// linked into Tests/Tests.csproj (that project uses explicit <Compile Include>
// items with no globs, so anything touching Autodesk.Revit.DB is untestable).
// Same reason SocketLayout.cs was split out of SocketCandidates.cs.
//
// The ft<->mm boundary lives in the callers: they convert Revit XYZ/bboxes to
// mm before calling in, and round for the wire on the way out.

using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>A point in plan+height space, millimetres.</summary>
    internal readonly struct Pt3Mm
    {
        public readonly double X, Y, Z;
        public Pt3Mm(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    /// <summary>Axis-aligned box, millimetres.</summary>
    internal readonly struct BoxMm
    {
        public readonly Pt3Mm Min, Max;
        public BoxMm(Pt3Mm min, Pt3Mm max) { Min = min; Max = max; }
    }

    /// <summary>One AABB overlap: penetration depth and the minimal push-out
    /// vector that clears it, both mm.</summary>
    internal sealed class ClashHitMm
    {
        public double PenetrationMm;
        public double PushX, PushY, PushZ;
    }

    internal static class GeomMm
    {
        /// <summary>Below this, contact is flush mounting, not a clash. Mirrors
        /// the old CLASH_TOL_FT (0.082 ft) in QueryGeometry.</summary>
        public const double ClashTolMm = 25.0;

        /// <summary>Revit's internal length unit is the foot. This is the one
        /// definition of the ratio for the tools that convert at their own
        /// boundary — it is a constant, not a conversion policy.</summary>
        public const double MmPerFoot = 304.8;

        /// <summary>AABB of a transformed box, from ALL its corners.
        ///
        /// A rotated link's bbox cannot be carried across a transform by
        /// mapping Min and Max alone — the axis-aligned hull of a rotated box
        /// is bounded by different corners. This is the only sanctioned path:
        /// transform the 8 corners, then min/max per axis.</summary>
        public static BoxMm AabbOfCorners(IReadOnlyList<Pt3Mm> corners)
        {
            if (corners == null || corners.Count == 0)
                throw new ArgumentException("corners required");
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var c in corners)
            {
                if (c.X < minX) minX = c.X; if (c.X > maxX) maxX = c.X;
                if (c.Y < minY) minY = c.Y; if (c.Y > maxY) maxY = c.Y;
                if (c.Z < minZ) minZ = c.Z; if (c.Z > maxZ) maxZ = c.Z;
            }
            return new BoxMm(new Pt3Mm(minX, minY, minZ), new Pt3Mm(maxX, maxY, maxZ));
        }

        /// <summary>The 8 corners of a box — feed the transformed results back
        /// through AabbOfCorners.</summary>
        public static List<Pt3Mm> Corners(BoxMm b) => new List<Pt3Mm>
        {
            new Pt3Mm(b.Min.X, b.Min.Y, b.Min.Z), new Pt3Mm(b.Max.X, b.Min.Y, b.Min.Z),
            new Pt3Mm(b.Min.X, b.Max.Y, b.Min.Z), new Pt3Mm(b.Max.X, b.Max.Y, b.Min.Z),
            new Pt3Mm(b.Min.X, b.Min.Y, b.Max.Z), new Pt3Mm(b.Max.X, b.Min.Y, b.Max.Z),
            new Pt3Mm(b.Min.X, b.Max.Y, b.Max.Z), new Pt3Mm(b.Max.X, b.Max.Y, b.Max.Z),
        };

        /// <summary>AABB overlap test — the exact arithmetic QueryGeometry's
        /// clash check has always used, in mm. Null when there is no true 3D
        /// overlap or the penetration is under tolerance (flush contact).
        /// Penetration = smaller HORIZONTAL overlap; push is along that axis,
        /// away from the other box's centre (a deep overlap on one axis =
        /// buried; Z is never the push axis).</summary>
        public static ClashHitMm? Overlap(BoxMm el, BoxMm other, double tolMm = ClashTolMm)
        {
            double ox = Math.Min(el.Max.X, other.Max.X) - Math.Max(el.Min.X, other.Min.X);
            double oy = Math.Min(el.Max.Y, other.Max.Y) - Math.Max(el.Min.Y, other.Min.Y);
            double oz = Math.Min(el.Max.Z, other.Max.Z) - Math.Max(el.Min.Z, other.Min.Z);
            if (ox <= 0 || oy <= 0 || oz <= 0) return null;

            double elCx = (el.Min.X + el.Max.X) / 2.0, elCy = (el.Min.Y + el.Max.Y) / 2.0;
            double otCx = (other.Min.X + other.Max.X) / 2.0, otCy = (other.Min.Y + other.Max.Y) / 2.0;

            double pen; double px = 0, py = 0;
            if (ox <= oy) { pen = ox; px = (elCx >= otCx ? 1.0 : -1.0) * ox; }
            else { pen = oy; py = (elCy >= otCy ? 1.0 : -1.0) * oy; }
            if (pen < tolMm) return null;

            return new ClashHitMm { PenetrationMm = pen, PushX = px, PushY = py, PushZ = 0 };
        }

        /// <summary>Shortest distance from segment a-b to an AABB, plus how far
        /// along the segment (mm from a) the closest approach sits.
        ///
        /// f(t) = |P(t) - clampToBox(P(t))| is convex in t (distance to a
        /// convex set composed with an affine map), so ternary search on [0,1]
        /// converges without the region-case explosion of the closed form.
        /// A zero-length segment degrades to point-to-box distance.</summary>
        public static (double DistMm, double AlongMm) SegmentToBoxDistance(Pt3Mm a, Pt3Mm b, BoxMm box)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-9) return (PointToBox(a, box), 0.0);

            double lo = 0.0, hi = 1.0;
            for (int i = 0; i < 100; i++)
            {
                double m1 = lo + (hi - lo) / 3.0, m2 = hi - (hi - lo) / 3.0;
                double d1 = PointToBox(At(a, dx, dy, dz, m1), box);
                double d2 = PointToBox(At(a, dx, dy, dz, m2), box);
                if (d1 <= d2) hi = m2; else lo = m1;
                if ((hi - lo) * len < 0.01) break;   // 0.01mm — beyond wire rounding
            }
            double t = (lo + hi) / 2.0;
            return (PointToBox(At(a, dx, dy, dz, t), box), t * len);
        }

        /// <summary>Coarse prefilter box around a segment: endpoint min/max
        /// grown by the clearance on every axis.</summary>
        public static BoxMm CorridorAabb(Pt3Mm a, Pt3Mm b, double clearanceMm)
        {
            return new BoxMm(
                new Pt3Mm(Math.Min(a.X, b.X) - clearanceMm,
                          Math.Min(a.Y, b.Y) - clearanceMm,
                          Math.Min(a.Z, b.Z) - clearanceMm),
                new Pt3Mm(Math.Max(a.X, b.X) + clearanceMm,
                          Math.Max(a.Y, b.Y) + clearanceMm,
                          Math.Max(a.Z, b.Z) + clearanceMm));
        }

        private static Pt3Mm At(Pt3Mm a, double dx, double dy, double dz, double t)
            => new Pt3Mm(a.X + dx * t, a.Y + dy * t, a.Z + dz * t);

        private static double PointToBox(Pt3Mm p, BoxMm b)
        {
            double cx = Math.Max(b.Min.X, Math.Min(p.X, b.Max.X));
            double cy = Math.Max(b.Min.Y, Math.Min(p.Y, b.Max.Y));
            double cz = Math.Max(b.Min.Z, Math.Min(p.Z, b.Max.Z));
            double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
