// MEP creation tools — straight segments only, no system routing.
// create_duct: adapted from mcp-servers-for-revit (MIT) —
// CreateLineElementEventHandler.cs OST_DuctCurves case. Kept: rectangular-
// duct-type fallback, MEPSystemType requirement, RBS_OFFSET_PARAM offset.
// create_pipe: ours, shaped like the duct case (their handler has no pipe).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace BinaVibe.Mcp.Tools
{
    internal static class MutatorsMep
    {
        public static Dictionary<string, object?> CreateDuct(Document doc, JsonElement args)
        {
            var (start, end, level, offsetFt) = ParseRun(doc, args);
            var typeName = ArgsHelp.GetString(args, "duct_type_name");
            var ductType = new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>()
                .FirstOrDefault(d => typeName != null
                    ? string.Equals(d.Name, typeName, StringComparison.OrdinalIgnoreCase)
                    : d.Shape == ConnectorProfileType.Rectangular)
                ?? throw new InvalidOperationException(typeName != null
                    ? $"duct type '{typeName}' not found (use list_family_types(\"OST_DuctCurves\"))"
                    : "no rectangular duct types in project");
            var systemType = new FilteredElementCollector(doc).OfClass(typeof(MEPSystemType)).Cast<MEPSystemType>()
                .FirstOrDefault(m => m.SystemClassification == MEPSystemClassification.SupplyAir)
                ?? throw new InvalidOperationException("no supply-air MEP system type in project");

            using var tx = new Transaction(doc, "BINA: create duct");
            TxGuard.StartSwallowing(tx);
            try
            {
                var duct = Duct.Create(doc, systemType.Id, ductType.Id, level.Id, start, end);
                SetOffset(duct, offsetFt);
                var widthFt = ArgsHelp.GetLengthMm(args, "width_mm");
                var heightFt = ArgsHelp.GetLengthMm(args, "height_mm");
                if (widthFt.HasValue)
                    duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)?.Set(widthFt.Value);
                if (heightFt.HasValue)
                    duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)?.Set(heightFt.Value);
                tx.Commit();

                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["new_ids"] = new List<long> { duct.Id.Value },
                    ["duct_type"] = ductType.Name, ["level"] = level.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        public static Dictionary<string, object?> CreatePipe(Document doc, JsonElement args)
        {
            var (start, end, level, offsetFt) = ParseRun(doc, args);
            var typeName = ArgsHelp.GetString(args, "pipe_type_name");
            var pipeType = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>()
                .FirstOrDefault(p => typeName == null
                    || string.Equals(p.Name, typeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(typeName != null
                    ? $"pipe type '{typeName}' not found"
                    : "no pipe types in project");
            var systemType = new FilteredElementCollector(doc).OfClass(typeof(MEPSystemType)).Cast<MEPSystemType>()
                .FirstOrDefault(m => m.SystemClassification == MEPSystemClassification.DomesticColdWater
                                  || m.SystemClassification == MEPSystemClassification.SupplyHydronic)
                ?? throw new InvalidOperationException("no cold-water/hydronic MEP system type in project");

            using var tx = new Transaction(doc, "BINA: create pipe");
            TxGuard.StartSwallowing(tx);
            try
            {
                var pipe = Pipe.Create(doc, systemType.Id, pipeType.Id, level.Id, start, end);
                SetOffset(pipe, offsetFt);
                var diaFt = ArgsHelp.GetLengthMm(args, "diameter_mm");
                if (diaFt.HasValue)
                    pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(diaFt.Value);
                tx.Commit();

                return new Dictionary<string, object?>
                {
                    ["ok"] = true, ["new_ids"] = new List<long> { pipe.Id.Value },
                    ["pipe_type"] = pipeType.Name, ["level"] = level.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        private static (XYZ start, XYZ end, Level level, double offsetFt) ParseRun(Document doc, JsonElement args)
        {
            var start = ArgsHelp.GetPointMm(args, "start_mm")
                ?? throw new InvalidOperationException("start_mm required ([x,y,z] mm)");
            var end = ArgsHelp.GetPointMm(args, "end_mm")
                ?? throw new InvalidOperationException("end_mm required ([x,y,z] mm)");
            var levelName = ArgsHelp.GetString(args, "level")
                ?? throw new InvalidOperationException("level required");
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"level '{levelName}' not found (use list_levels)");
            var offsetFt = ArgsHelp.GetLengthMm(args, "offset_mm") ?? 0;
            return (start, end, level, offsetFt);
        }

        private static void SetOffset(Element mepCurve, double offsetFt)
        {
            if (Math.Abs(offsetFt) < 1e-9) return;
            mepCurve.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM)?.Set(offsetFt);
        }
    }
}
