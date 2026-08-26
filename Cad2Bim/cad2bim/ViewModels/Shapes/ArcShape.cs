namespace Cad2Bim.ViewModels.Shapes {
    // Model arcs carry only center + radius, so they render as full circles.
    public record ArcShape(double Cx, double Cy, double Radius);
}
