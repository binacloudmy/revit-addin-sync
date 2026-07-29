// BinaVibe/Mcp/Tools/IfcConvert/IfcElement.cs
namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public enum IfcEntity { Wall, Slab, Column, Beam, Other }

    /// <summary>Transport-neutral description of one imported-IFC element. Primitives only
    /// (mm, [x,y,z] arrays) so the mapper is pure + unit-testable without Revit.</summary>
    public sealed record IfcElement
    {
        public long SourceId { get; init; }
        public IfcEntity Entity { get; init; }
        public double[]? StartMm { get; init; }        // wall/beam axis start [x,y,z]
        public double[]? EndMm { get; init; }          // wall/beam axis end
        public double[]? PointMm { get; init; }        // column insertion point
        public double[][]? BoundaryMm { get; init; }   // slab boundary loop [[x,y,z],...]
        public double HeightMm { get; init; }
        public double ThicknessMm { get; init; }
        public string? Material { get; init; }
        public string? Level { get; init; }
        public string? IfcTypeName { get; init; }      // name carried on the IFC element
        public bool Convertible { get; init; } = true;
        public string? Reason { get; init; }           // set when Convertible == false
    }
}
