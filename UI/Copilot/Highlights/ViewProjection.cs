#nullable enable
// Model point → percent of the view rectangle, from UIView.GetZoomCorners()
// and the view's Right/Up directions. Pure math, Revit-free (points are
// plain doubles so the scratch harness can test it).

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    public readonly struct Vec3
    {
        public readonly double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
    }

    public static class ViewProjection
    {
        /// <summary>Where <paramref name="p"/> lands in the view, as percent of its width and height
        /// (0,0 = top-left). Null when the zoom rectangle is degenerate. Corner order does not matter.</summary>
        public static (double XPct, double YPct)? ToPercent(Vec3 p, Vec3 corner0, Vec3 corner1, Vec3 right, Vec3 up)
        {
            double r0 = corner0.Dot(right), r1 = corner1.Dot(right);
            double u0 = corner0.Dot(up), u1 = corner1.Dot(up);
            double left = r0 < r1 ? r0 : r1, rightEdge = r0 < r1 ? r1 : r0;
            double bottom = u0 < u1 ? u0 : u1, top = u0 < u1 ? u1 : u0;
            double w = rightEdge - left, h = top - bottom;
            if (w < 1e-9 || h < 1e-9) return null;
            double x = 100.0 * (p.Dot(right) - left) / w;
            double y = 100.0 * (1.0 - (p.Dot(up) - bottom) / h);
            return (x, y);
        }

        public static bool InView((double XPct, double YPct)? pt) =>
            pt.HasValue && pt.Value.XPct >= 0 && pt.Value.XPct <= 100 && pt.Value.YPct >= 0 && pt.Value.YPct <= 100;
    }
}
