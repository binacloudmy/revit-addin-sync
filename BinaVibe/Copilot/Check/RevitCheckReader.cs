#nullable enable
// Reads Revit elements into ElementValues for the Revit-free evaluators:
// name, type, family, level, and every parameter a rule needs (instance
// first, then type), with numbers converted to the engine's canonical units
// (mm, m², m³, degrees). Anything that throws becomes an unreadable reading
// or an unavailable element, never a guess. Revit-bound.
//
// Category names resolve through Inspectors.ResolveCategoryRobust, the same
// resolver the existing find/audit tools use.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Copilot.Check
{
    internal static class RevitCheckReader
    {
        private static readonly BuiltInParameter[] LevelParams =
        {
            BuiltInParameter.FAMILY_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,
            BuiltInParameter.WALL_BASE_CONSTRAINT,
            BuiltInParameter.STAIRS_BASE_LEVEL_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
            BuiltInParameter.RBS_START_LEVEL_PARAM,
            BuiltInParameter.ROOF_BASE_LEVEL_PARAM,
        };

        public static BuiltInCategory? ResolveCategory(Document doc, string name) =>
            Inspectors.ResolveCategoryRobust(doc, name);

        /// <summary>Instances of one category (no element types).</summary>
        public static IList<Element> Collect(Document doc, BuiltInCategory bic) =>
            new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType().ToElements();

        public static ElementValues Values(Document doc, Element e, CheckSource source, IEnumerable<string> paramNames)
        {
            var v = new ElementValues { Ref = source.RefOf(e), Source = source.Label };
            try
            {
                v.Category = e.Category?.Name;
                v.Name = e.Name;
                // Rooms, spaces and areas: the Name parameter, without the number
                // (R29: type_name_regex and param-less matches test the room name).
                if (e is SpatialElement)
                {
                    var roomName = e.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                    if (!string.IsNullOrEmpty(roomName)) v.Name = roomName;
                }
                var type = TypeOf(doc, e);
                v.TypeName = type?.Name;
                v.FamilyName = type?.FamilyName;
                v.LevelName = LevelName(doc, e);
                foreach (var name in paramNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                    v.Params[name] = Read(doc, e, type, name, v);
            }
            catch (Exception ex)
            {
                v.UnavailableReason = $"element {e.Id.Value} could not be read: {ex.GetType().Name}: {ex.Message}";
            }
            return v;
        }

        public static ElementType? TypeOf(Document doc, Element e)
        {
            var typeId = e.GetTypeId();
            return typeId == null || typeId == ElementId.InvalidElementId ? null : doc.GetElement(typeId) as ElementType;
        }

        public static string? LevelName(Document doc, Element e)
        {
            if (e is Level level) return level.Name;
            if (e is View view) return view.GenLevel?.Name;

            var id = e.LevelId;
            if (id == null || id == ElementId.InvalidElementId)
            {
                foreach (var bip in LevelParams)
                {
                    var p = e.get_Parameter(bip);
                    if (p == null || p.StorageType != StorageType.ElementId) continue;
                    var candidate = p.AsElementId();
                    if (candidate != null && candidate != ElementId.InvalidElementId)
                    {
                        id = candidate;
                        break;
                    }
                }
            }
            return id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;
        }

        private static ValueReading Read(Document doc, Element e, ElementType? type, string name, ElementValues v)
        {
            Parameter? p;
            try
            {
                p = e.LookupParameter(name) ?? type?.LookupParameter(name);
            }
            catch (Exception ex)
            {
                return ValueReading.Unreadable($"{ex.GetType().Name}: {ex.Message}");
            }
            return p != null ? ReadParameter(doc, p) : Pseudo(name, v);
        }

        /// <summary>Names that are element properties, not parameters, on some elements.</summary>
        private static ValueReading Pseudo(string name, ElementValues v)
        {
            string? value = name.Trim().ToLowerInvariant() switch
            {
                "name" => v.Name,
                "type name" => v.TypeName,
                "family name" => v.FamilyName,
                "category" => v.Category,
                "level" => v.LevelName,
                _ => null,
            };
            return value != null ? ValueReading.OfText(value) : ValueReading.Missing;
        }

        public static ValueReading ReadParameter(Document doc, Parameter p)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        return ValueReading.OfText(p.HasValue ? p.AsString() : null);
                    case StorageType.Integer:
                    {
                        if (!p.HasValue) return ValueReading.OfText(null);
                        var i = p.AsInteger();
                        return ValueReading.OfNumber(i, null, p.AsValueString() ?? i.ToString(CultureInfo.InvariantCulture));
                    }
                    case StorageType.Double:
                        return p.HasValue ? ReadDouble(p) : ValueReading.OfText(null);
                    case StorageType.ElementId:
                    {
                        var id = p.AsElementId();
                        if (id == null || id == ElementId.InvalidElementId) return ValueReading.OfText(null);
                        return ValueReading.OfText(p.AsValueString() ?? doc.GetElement(id)?.Name);
                    }
                    default:
                        return ValueReading.Unreadable("the parameter has no storage type");
                }
            }
            catch (Exception ex)
            {
                return ValueReading.Unreadable($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static ValueReading ReadDouble(Parameter p)
        {
            var raw = p.AsDouble();
            var display = p.AsValueString();
            ForgeTypeId? unit = null;
            try
            {
                unit = p.GetUnitTypeId();
            }
            catch
            {
                // Not a measurable spec: keep the raw number.
            }
            if (unit == null) return ValueReading.OfNumber(raw, null, display);

            if (IsUnitOf(SpecTypeId.Length, unit))
                return ValueReading.OfNumber(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Millimeters), CheckUnits.Length, display);
            if (IsUnitOf(SpecTypeId.Area, unit))
                return ValueReading.OfNumber(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.SquareMeters), CheckUnits.Area, display);
            if (IsUnitOf(SpecTypeId.Volume, unit))
                return ValueReading.OfNumber(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.CubicMeters), CheckUnits.Volume, display);
            if (IsUnitOf(SpecTypeId.Angle, unit))
                return ValueReading.OfNumber(UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Degrees), CheckUnits.Angle, display);
            return ValueReading.OfNumber(UnitUtils.ConvertFromInternalUnits(raw, unit), null, display);
        }

        private static bool IsUnitOf(ForgeTypeId spec, ForgeTypeId unit)
        {
            try
            {
                return UnitUtils.IsValidUnit(spec, unit);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Feet to mm.</summary>
        public static double Mm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
    }
}
