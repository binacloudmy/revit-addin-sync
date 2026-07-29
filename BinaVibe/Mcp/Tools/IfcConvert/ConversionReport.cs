// BinaVibe/Mcp/Tools/IfcConvert/ConversionReport.cs
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public sealed record KeptElement(long SourceId, string Entity, string Reason);

    /// <summary>Aggregated, transport-safe result. Preview and Build build the SAME shape.</summary>
    public sealed class ConversionReport
    {
        public Dictionary<string, int> ConvertedCounts { get; } = new()
            { ["Wall"] = 0, ["Slab"] = 0, ["Column"] = 0, ["Beam"] = 0 };
        public List<KeptElement> KeptAsIs { get; } = new();
        public List<string> CreatedTypes { get; } = new();
        public List<string> Warnings { get; } = new();

        /// <summary>Record one element. step != null => converted; step == null => kept as-is.</summary>
        public void Add(IfcElement el, NativeStep? step)
        {
            if (step != null)
                ConvertedCounts[el.Entity.ToString()] = ConvertedCounts.GetValueOrDefault(el.Entity.ToString()) + 1;
            else
                KeptAsIs.Add(new KeptElement(el.SourceId, el.Entity.ToString(), el.Reason ?? "unknown"));
        }

        public Dictionary<string, object?> ToDict() => new()
        {
            ["converted"] = ConvertedCounts,
            ["keptAsIs"] = KeptAsIs,
            ["createdTypes"] = CreatedTypes,
            ["warnings"] = Warnings,
            // M2: the original imported-IFC DirectShapes are NEVER deleted — native
            // elements are created alongside them, and kept-as-is elements are left
            // untouched. So neither "converted" nor "keptAsIs" implies any data loss.
            ["note"] = "Original imported IFC DirectShapes are retained (not deleted); "
                     + "native elements are created alongside them.",
        };
    }
}
