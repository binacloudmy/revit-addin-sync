namespace Cad2Bim.ViewModels.Shapes {
    // Render-ready, immutable, in raw CAD coordinates (Y up). Every curve the DWG holds
    // (arc, circle, ellipse, spline, bulged polyline) arrives here already tessellated,
    // so the viewport only ever has to stroke straight runs.
    public record PolylineShape(IReadOnlyList<(double X, double Y)> Points, bool IsClosed);
}
