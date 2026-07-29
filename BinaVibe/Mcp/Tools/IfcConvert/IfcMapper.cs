// BinaVibe/Mcp/Tools/IfcConvert/IfcMapper.cs
using System;
using System.Collections.Generic;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    /// <summary>Result of mapping one element.
    /// <para><b>Step</b>: the element create step (null = keep original DirectShape).</para>
    /// <para><b>PreStep</b>: an OPTIONAL create-type step (create_wall_type / create_floor_type)
    /// that MUST run before <b>Step</b> so the new type exists when the element references it.
    /// Only emitted for Wall/Slab that need a new SYSTEM type. Columns/beams (loadable families)
    /// never get one — an unmatched loadable family can't be synthesized from a thickness.</para>
    /// <para><b>KeptReason</b>: set when the element is kept-as-is for a mapper-level reason
    /// (e.g. no matching loadable family), so the report records a truthful cause.</para></summary>
    public sealed record MapResult(NativeStep? Step, TypeResolution? Resolution,
                                   NativeStep? PreStep = null, string? KeptReason = null);

    /// <summary>PURE: neutral IfcElement + resolved type -> an execute_revit_batch step.
    /// No Revit API. Un-convertible elements map to (null, …) so the converter keeps
    /// the original DirectShape and reports it.</summary>
    public sealed class IfcMapper
    {
        readonly ITypeResolver _resolver;
        public IfcMapper(ITypeResolver resolver) => _resolver = resolver;

        public MapResult Map(IfcElement el, IReadOnlyList<ExistingType> existingTypes)
        {
            if (!el.Convertible) return new MapResult(null, null);

            var res = _resolver.Resolve(el.Entity, el.ThicknessMm, el.IfcTypeName, el.Material, existingTypes);

            // C2: loadable families (columns/beams) can't be conjured from a thickness.
            // No matching family symbol → keep the original DirectShape, report why.
            // NEVER emit a create_column/create_beam step against an unresolvable type.
            if (res.NeedsCreate && (el.Entity == IfcEntity.Column || el.Entity == IfcEntity.Beam))
                return new MapResult(null, res, null, "no matching column/beam family to convert into");

            var level = el.Level ?? throw new ArgumentException($"element {el.SourceId}: missing level");

            // C2: system families (wall/floor) CAN be synthesized. When the type is
            // new, emit a create-type PreStep so it exists before the element step.
            NativeStep? preStep = null;
            if (res.NeedsCreate && el.Entity == IfcEntity.Wall)
                preStep = new NativeStep("create_wall_type", new()
                    { ["type_name"] = res.TypeName, ["thickness_mm"] = res.CreateThicknessMm });
            else if (res.NeedsCreate && el.Entity == IfcEntity.Slab)
                preStep = new NativeStep("create_floor_type", new()
                    { ["type_name"] = res.TypeName, ["thickness_mm"] = res.CreateThicknessMm });

            NativeStep step = el.Entity switch
            {
                IfcEntity.Wall => new NativeStep("create_wall", new()
                {
                    ["start_mm"] = el.StartMm, ["end_mm"] = el.EndMm, ["level"] = level,
                    ["height_mm"] = el.HeightMm, ["type_name"] = res.TypeName,
                }),
                IfcEntity.Slab => new NativeStep("create_floor", new()
                {
                    ["boundary_mm"] = el.BoundaryMm, ["level"] = level, ["type_name"] = res.TypeName,
                }),
                IfcEntity.Column => new NativeStep("create_column", new()
                {
                    ["point_mm"] = el.PointMm, ["level"] = level, ["type_name"] = res.TypeName,
                }),
                IfcEntity.Beam => new NativeStep("create_beam", new()
                {
                    ["start_mm"] = el.StartMm, ["end_mm"] = el.EndMm, ["level"] = level,
                    ["beam_type_name"] = res.TypeName,
                }),
                _ => throw new ArgumentException($"element {el.SourceId}: unsupported entity {el.Entity}"),
            };
            return new MapResult(step, res, preStep);
        }
    }
}
