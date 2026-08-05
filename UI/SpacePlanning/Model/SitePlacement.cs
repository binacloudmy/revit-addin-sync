using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.SpacePlanning.Model
{
    /// <summary>
    /// Where a scheme lands on the site.
    ///
    /// The backend generates every scheme from (0,0) — it has no idea where the
    /// drafter's land is. Placement is therefore the add-in's job: it reads the
    /// boundary out of the model, so it is the only side that knows both.
    ///
    /// Pure: no Revit, no WPF, so Tests/ can pin the arithmetic. All lengths in
    /// MILLIMETRES here, matching place_massing_scheme's offsets.
    /// </summary>
    public sealed class SiteBoundaryInfo
    {
        /// <summary>property_line | scope_box | topography | none.</summary>
        public string Source;
        public string Name;
        /// <summary>Boundary points in mm, model coordinates.</summary>
        public List<double[]> PolygonMm = new List<double[]>();
        public double AreaM2;
        public double WidthM;
        public double DepthM;

        public bool HasBoundary => PolygonMm != null && PolygonMm.Count >= 3;

        /// <summary>The polygon in METRES for the backend, which works in metres
        /// throughout (same convention as room coordinates).</summary>
        public List<List<double>> PolygonM() =>
            !HasBoundary ? null
            : PolygonMm.Select(p => new List<double> { p[0] / 1000.0, p[1] / 1000.0 }).ToList();
    }

    public static class SitePlacement
    {
        /// <summary>
        /// Offset that moves a scheme generated at the origin to the FRONT-LEFT
        /// corner of the buildable area — i.e. the site's minimum corner plus the
        /// setback on both axes.
        ///
        /// Front-left rather than centred: it is where a drafter starts a layout, it
        /// is predictable between runs, and it leaves the remainder of the site in
        /// one usable piece for the padang and any later block. Centring looks tidy
        /// on its own and wastes the land.
        ///
        /// Returns (0,0) when there is no boundary — the scheme then lands at the
        /// origin exactly as before, which is the honest behaviour when we do not
        /// know where the site is.
        /// </summary>
        public static (double x, double y) OffsetMm(
            SiteBoundaryInfo site, double setbackM, MassingScheme scheme)
        {
            if (site == null || !site.HasBoundary) return (0, 0);

            double minX = site.PolygonMm.Min(p => p[0]);
            double minY = site.PolygonMm.Min(p => p[1]);
            double setbackMm = Math.Max(0, setbackM) * 1000.0;

            // The scheme's own minimum is not always 0 — subtract it so the block's
            // left/front edge lands on the setback line rather than its origin.
            double schemeMinX = 0, schemeMinY = 0;
            var rooms = scheme?.Rooms?.Where(r => r != null && r.CountsAsGfa).ToList();
            if (rooms != null && rooms.Count > 0)
            {
                schemeMinX = rooms.Min(r => r.X) * 1000.0;
                schemeMinY = rooms.Min(r => r.Y) * 1000.0;
            }

            return (minX + setbackMm - schemeMinX, minY + setbackMm - schemeMinY);
        }

        /// <summary>
        /// Does the scheme fit inside the buildable envelope? The backend already
        /// rejects schemes that do not, but it works from the polygon's bounding box
        /// while placement happens here — so this is the check on what will ACTUALLY
        /// be drawn, and it catches the case where the backend was never sent a
        /// boundary at all.
        /// </summary>
        public static bool FitsInside(SiteBoundaryInfo site, double setbackM, MassingScheme scheme)
        {
            if (site == null || !site.HasBoundary || scheme == null) return true;
            var rooms = scheme.Rooms?.Where(r => r != null && r.CountsAsGfa).ToList();
            if (rooms == null || rooms.Count == 0) return true;

            double schemeW = rooms.Max(r => r.X + r.W) - rooms.Min(r => r.X);
            double schemeD = rooms.Max(r => r.Y + r.H) - rooms.Min(r => r.Y);
            double availW = site.WidthM - 2 * Math.Max(0, setbackM);
            double availD = site.DepthM - 2 * Math.Max(0, setbackM);
            return schemeW <= availW && schemeD <= availD;
        }
    }
}
