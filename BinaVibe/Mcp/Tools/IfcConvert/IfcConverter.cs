// BinaVibe/Mcp/Tools/IfcConvert/IfcConverter.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools.IfcConvert
{
    public sealed class IfcConverter
    {
        readonly IfcReader _reader = new();
        readonly IfcMapper _mapper = new(new MatchOrCreateTypeResolver(2.0));

        public Dictionary<string, object?> Preview(UIApplication app, ConvertScope scope,
                                                   ICollection<ElementId>? sel = null, string? activeLevelName = null)
        {
            var (report, _) = Plan(app, scope, sel, activeLevelName);
            return new() { ["ok"] = true, ["mode"] = "preview", ["report"] = report.ToDict() };
        }

        public Dictionary<string, object?> Build(UIApplication app, ConvertScope scope,
                                                 ICollection<ElementId>? sel = null, string? activeLevelName = null)
        {
            var (report, steps) = Plan(app, scope, sel, activeLevelName);
            if (steps.Count == 0)
                return new() { ["ok"] = true, ["mode"] = "build", ["report"] = report.ToDict(), ["note"] = "nothing convertible" };

            var batchArgs = JsonSerializer.SerializeToElement(NativeStep.BatchArgs(steps));
            var batch = BatchExecutor.Run(app, batchArgs);
            var ok = batch.TryGetValue("ok", out var b) && b is bool bb && bb;
            return new() { ["ok"] = ok, ["mode"] = "build", ["report"] = report.ToDict(), ["batch"] = batch };
        }

        // Shared read+map: the SAME path feeds both preview and build (parity guarantee).
        (ConversionReport report, List<NativeStep> steps) Plan(UIApplication app, ConvertScope scope,
                                                               ICollection<ElementId>? sel, string? activeLevelName)
        {
            var doc = app.ActiveUIDocument.Document;
            var report = new ConversionReport();

            // I1: scope="level" — filter to the active level. If it can't be resolved,
            // warn and fall back to whole-model (activeLevelName stays null → no filter).
            if (scope == ConvertScope.ActiveLevel && activeLevelName == null)
                report.Warnings.Add("no active level for scope=level; converting the whole model instead");

            // C1/I5: resolve each entity against ITS OWN native types, not wall types
            // for everything. Read once, then hand the mapper the right list per element.
            var typesByEntity = new Dictionary<IfcEntity, IReadOnlyList<ExistingType>>
            {
                [IfcEntity.Wall]   = _reader.ReadExistingWallTypes(doc),
                [IfcEntity.Slab]   = _reader.ReadExistingFloorTypes(doc),
                [IfcEntity.Column] = _reader.ReadExistingColumnTypes(doc),
                [IfcEntity.Beam]   = _reader.ReadExistingBeamTypes(doc),
            };
            IReadOnlyList<ExistingType> NoTypes = Array.Empty<ExistingType>();

            var elements = _reader.Read(doc, scope, sel, activeLevelName);
            var steps = new List<NativeStep>();
            var createdTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var el in elements)
            {
                var existing = typesByEntity.TryGetValue(el.Entity, out var list) ? list : NoTypes;
                var res = _mapper.Map(el, existing);

                // Emit the create-type step ONCE per distinct new type name, and BEFORE
                // the element step (BatchExecutor runs in order). createdTypes is populated
                // ONLY from these steps → walls/floors only, never columns/beams (C2).
                if (res.PreStep != null)
                {
                    var newTypeName = res.Resolution!.TypeName;
                    if (createdTypeNames.Add(newTypeName))
                    {
                        steps.Add(res.PreStep);
                        report.CreatedTypes.Add(newTypeName);
                    }
                }

                // Record with the mapper's keep-reason when it kept the element as-is
                // for a non-geometry reason (e.g. no matching loadable family).
                var recorded = (res.Step == null && res.KeptReason != null)
                    ? el with { Convertible = false, Reason = res.KeptReason }
                    : el;
                report.Add(recorded, res.Step);
                if (res.Step != null) steps.Add(res.Step);
            }
            return (report, steps);
        }
    }
}
