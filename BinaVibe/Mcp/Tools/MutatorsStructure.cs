// Structural creation tools.
// create_beam_system: adapted from mcp-servers-for-revit (MIT) —
// CreateStructuralFramingSystemEventHandler.cs. Kept: rectangular profile,
// LayoutRuleFixedDistance, ResolveBeamType fallback, Regenerate-before-
// GetBeamIds, actual-spacing readback. Dropped: their warnings prose,
// Z_OFFSET elevation shifting (callers pass the right level), AIResult.
// create_beam: ours (no OSS source — their line handler has no beam case).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace BinaVibe.Mcp.Tools
{
    internal static class MutatorsStructure
    {
        public static Dictionary<string, object?> CreateBeamSystem(Document doc, JsonElement args)
        {
            var boundary = ArgsHelp.GetPointListMm(args, "boundary_mm");
            if (boundary.Count < 4)
                throw new InvalidOperationException("boundary_mm needs 4 [x,y] points (rectangle corners, mm)");
            var spacingFt = ArgsHelp.GetLengthMm(args, "spacing_mm")
                ?? throw new InvalidOperationException("spacing_mm required");
            var directionEdge = (ArgsHelp.GetString(args, "direction_edge") ?? "south").ToLowerInvariant();
            var beamTypeName = ArgsHelp.GetString(args, "beam_type_name");
            var levelName = ArgsHelp.GetString(args, "level")
                ?? throw new InvalidOperationException("level required");
            var justify = (ArgsHelp.GetString(args, "justify") ?? "center").ToLowerInvariant();

            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"level '{levelName}' not found (use list_levels)");

            var beamType = ResolveBeamType(doc, beamTypeName);

            // Rectangular closed profile at level elevation, from the bounding
            // rectangle of the passed points (their BuildRectangularProfile).
            double xMin = boundary.Min(p => p.X), xMax = boundary.Max(p => p.X);
            double yMin = boundary.Min(p => p.Y), yMax = boundary.Max(p => p.Y);
            double z = level.Elevation;
            var p1 = new XYZ(xMin, yMin, z); var p2 = new XYZ(xMax, yMin, z);
            var p3 = new XYZ(xMax, yMax, z); var p4 = new XYZ(xMin, yMax, z);
            var profile = new List<Curve>
            {
                Line.CreateBound(p1, p2),   // 0: south edge
                Line.CreateBound(p2, p3),   // 1: east edge
                Line.CreateBound(p3, p4),   // 2: north edge
                Line.CreateBound(p4, p1),   // 3: west edge
            };
            int dirIndex = directionEdge switch
            {
                "south" => 0, "east" => 1, "north" => 2, "west" => 3,
                _ => throw new InvalidOperationException("direction_edge must be north|south|east|west"),
            };
            var justifyType = justify switch
            {
                "beginning" => BeamSystemJustifyType.Beginning,
                "end" => BeamSystemJustifyType.End,
                _ => BeamSystemJustifyType.Center,
            };

            using var tx = new Transaction(doc, "BINA: create beam system");
            tx.Start();
            if (!beamType.IsActive) beamType.Activate();
            var beamSystem = BeamSystem.Create(doc, profile, level, dirIndex, false);
            beamSystem.BeamType = beamType;
            var layoutRule = new LayoutRuleFixedDistance(spacingFt.Value, justifyType);
            beamSystem.LayoutRule = layoutRule;
            doc.Regenerate();   // BeamSystem materialises member beams on regen
            var beamIds = beamSystem.GetBeamIds().Select(id => id.Value).ToList();
            tx.Commit();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["new_ids"] = beamIds,
                ["beam_system_id"] = beamSystem.Id.Value,
                ["count"] = beamIds.Count,
                ["actual_spacing_mm"] = Math.Round(layoutRule.Spacing * 304.8, 0),
                ["beam_type"] = $"{beamType.Family.Name}: {beamType.Name}",
                ["level"] = level.Name,
            };
        }

        public static Dictionary<string, object?> CreateBeam(Document doc, JsonElement args)
        {
            var start = ArgsHelp.GetPointMm(args, "start_mm")
                ?? throw new InvalidOperationException("start_mm required ([x,y,z] mm)");
            var end = ArgsHelp.GetPointMm(args, "end_mm")
                ?? throw new InvalidOperationException("end_mm required ([x,y,z] mm)");
            var levelName = ArgsHelp.GetString(args, "level")
                ?? throw new InvalidOperationException("level required");
            var beamTypeName = ArgsHelp.GetString(args, "beam_type_name");

            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"level '{levelName}' not found (use list_levels)");
            var beamType = ResolveBeamType(doc, beamTypeName);

            using var tx = new Transaction(doc, "BINA: create beam");
            tx.Start();
            if (!beamType.IsActive) beamType.Activate();
            var line = Line.CreateBound(
                new XYZ(start.X, start.Y, level.Elevation + start.Z),
                new XYZ(end.X, end.Y, level.Elevation + end.Z));
            var beam = doc.Create.NewFamilyInstance(line, beamType, level, StructuralType.Beam);
            tx.Commit();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["new_ids"] = new List<long> { beam.Id.Value },
                ["beam_type"] = $"{beamType.Family.Name}: {beamType.Name}",
                ["level"] = level.Name,
            };
        }

        // adapted from mcp-servers-for-revit (MIT) — ResolveBeamType: exact
        // name match ("Family: Type" or type name), else first available.
        private static FamilySymbol ResolveBeamType(Document doc, string? name)
        {
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_StructuralFraming)
                .Cast<FamilySymbol>().ToList();
            if (symbols.Count == 0)
                throw new InvalidOperationException(
                    "no structural framing families loaded — load a beam family first (search_family_library)");
            if (string.IsNullOrWhiteSpace(name)) return symbols[0];
            return symbols.FirstOrDefault(s =>
                       string.Equals($"{s.Family.Name}: {s.Name}", name, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException(
                       $"beam type '{name}' not found — use list_family_types(\"OST_StructuralFraming\")");
        }
    }
}
