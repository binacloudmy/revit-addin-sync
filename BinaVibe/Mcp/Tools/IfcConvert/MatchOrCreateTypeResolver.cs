// BinaVibe/Mcp/Tools/IfcConvert/MatchOrCreateTypeResolver.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public sealed class MatchOrCreateTypeResolver : ITypeResolver
    {
        readonly double _tolMm;
        public MatchOrCreateTypeResolver(double toleranceMm = 2.0) => _tolMm = toleranceMm;

        public TypeResolution Resolve(IfcEntity entity, double thicknessMm, string? ifcName, string? material,
                                      IReadOnlyList<ExistingType> existing)
        {
            // 1. Exact name match (case-insensitive) among thickness-compatible types.
            if (!string.IsNullOrWhiteSpace(ifcName))
            {
                var named = existing.FirstOrDefault(t =>
                    string.Equals(t.Name, ifcName, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(t.ThicknessMm - thicknessMm) <= _tolMm);
                if (named != null) return new TypeResolution(named.Name, false, 0, null);
            }
            // 2. Closest thickness within tolerance.
            var byThk = existing
                .Where(t => Math.Abs(t.ThicknessMm - thicknessMm) <= _tolMm)
                .OrderBy(t => Math.Abs(t.ThicknessMm - thicknessMm))
                .FirstOrDefault();
            if (byThk != null) return new TypeResolution(byThk.Name, false, 0, null);
            // 3. Create a new type, deterministic name.
            var name = $"IFC {entity} {Math.Round(thicknessMm)}mm";
            return new TypeResolution(name, true, thicknessMm, material);
        }
    }
}
