// BinaVibe/Mcp/Tools/IfcConvert/ITypeResolver.cs
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public sealed record ExistingType(string Name, double ThicknessMm);
    public sealed record TypeResolution(string TypeName, bool NeedsCreate, double CreateThicknessMm, string? CreateMaterial);

    /// <summary>Picks the native Revit type for an IFC element. v1 = MatchOrCreate;
    /// v2 = a JkrFamilyLibraryResolver drops in here without touching reader/mapper.</summary>
    public interface ITypeResolver
    {
        TypeResolution Resolve(IfcEntity entity, double thicknessMm, string? ifcName, string? material,
                               IReadOnlyList<ExistingType> existing);
    }
}
