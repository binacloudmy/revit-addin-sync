// BoxMm — an axis-aligned box in drawing millimetres (the engine's coordinate space
// after Units.Resolve). Kept in its own Revit-free file: the Tests project links it.
// `record struct` on net48 needs IsExternalInit, which Services/Net48Shims.cs supplies.

using System;

namespace RevitWebAppSync.Services.CadToBim
{
    public readonly record struct BoxMm(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        /// <summary>Inclusive on every edge.</summary>
        public bool Contains(double x, double y) =>
            x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

        public bool Contains(Cad2Bim.Point p) => Contains(p.x, p.y);

        /// <summary>From any two opposite corners, in any order — a drag can go up-left.</summary>
        public static BoxMm Normalised(double x1, double y1, double x2, double y2) =>
            new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }
}
