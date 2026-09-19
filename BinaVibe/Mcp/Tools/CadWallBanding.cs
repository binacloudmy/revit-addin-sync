// Pure thickness→wall-type selection behind cad_walls_to_centerlines' create
// mode. No Revit types, so it compiles AND runs in Tests.csproj (which references
// a reference-only Revit API). The Wall.Create loop that consumes these decisions
// is Revit glue and lives in CadWallsToCenterlines.cs.

using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools
{
    public readonly struct ThicknessBand
    {
        public readonly double MinMm, MaxMm;
        public readonly string TypeName;
        public ThicknessBand(double minMm, double maxMm, string typeName)
        { MinMm = minMm; MaxMm = maxMm; TypeName = typeName; }
    }

    public static class CadWallBanding
    {
        // The wall type for a measured thickness: the FIRST band that CONTAINS it,
        // else `fallback` (which may be null → caller skips the wall). Deliberately
        // NOT "nearest band" — nearest silently mis-types outliers (a 500mm wall
        // landing in a 100mm band); the fallback type is the explicit catch-all.
        public static string? PickType(double thicknessMm, IReadOnlyList<ThicknessBand> bands, string? fallback)
        {
            if (bands != null)
                foreach (var b in bands)
                    if (thicknessMm >= b.MinMm && thicknessMm <= b.MaxMm)
                        return b.TypeName;
            return fallback;
        }

        // Inside the optional create window? A null bound is open on that side.
        public static bool InWindow(double thicknessMm, double? minMm, double? maxMm)
            => (!minMm.HasValue || thicknessMm >= minMm.Value)
            && (!maxMm.HasValue || thicknessMm <= maxMm.Value);
    }
}
