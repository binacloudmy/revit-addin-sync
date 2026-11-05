#nullable enable
// Reads, sets and reads back Revit parameter values in the units ValueParser
// speaks (spec §7.3 read-back, §10 display units). Lengths are exchanged in
// mm, areas in m², volumes in m³, angles in degrees, converted with UnitUtils;
// a bare number takes the project's display unit for that spec, which is
// what the drafter sees in Properties.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    internal static class RevitParamValues
    {
        public static ValueKind KindOf(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    return ValueKind.Text;
                case StorageType.ElementId:
                    return ValueKind.ElementId;
                case StorageType.Integer:
                    return Spec(p) is ForgeTypeId yes && yes.Equals(SpecTypeId.Boolean.YesNo) ? ValueKind.YesNo : ValueKind.Integer;
                case StorageType.Double:
                    var spec = Spec(p);
                    if (spec == null) return ValueKind.Number;
                    if (spec.Equals(SpecTypeId.Length)) return ValueKind.Length;
                    if (spec.Equals(SpecTypeId.Area)) return ValueKind.Area;
                    if (spec.Equals(SpecTypeId.Volume)) return ValueKind.Volume;
                    if (spec.Equals(SpecTypeId.Angle)) return ValueKind.Angle;
                    return Measurable(spec) ? ValueKind.Measured : ValueKind.Number;
                default:
                    return ValueKind.Text;
            }
        }

        /// <summary>The display-unit symbol ValueParser understands ("mm", "m", "m2", "°"…), or null.</summary>
        public static string? DisplayUnitOf(Document doc, Parameter p)
        {
            var unit = DisplayUnitId(doc, p);
            if (unit == null) return null;
            if (unit.Equals(UnitTypeId.Millimeters)) return "mm";
            if (unit.Equals(UnitTypeId.Centimeters)) return "cm";
            if (unit.Equals(UnitTypeId.Decimeters)) return "dm";
            if (unit.Equals(UnitTypeId.Meters) || unit.Equals(UnitTypeId.MetersCentimeters)) return "m";
            if (unit.Equals(UnitTypeId.Feet) || unit.Equals(UnitTypeId.FeetFractionalInches)) return "ft";
            if (unit.Equals(UnitTypeId.Inches) || unit.Equals(UnitTypeId.FractionalInches)) return "in";
            if (unit.Equals(UnitTypeId.SquareMeters)) return "m2";
            if (unit.Equals(UnitTypeId.SquareMillimeters)) return "mm2";
            if (unit.Equals(UnitTypeId.SquareCentimeters)) return "cm2";
            if (unit.Equals(UnitTypeId.SquareFeet)) return "ft2";
            if (unit.Equals(UnitTypeId.SquareInches)) return "in2";
            if (unit.Equals(UnitTypeId.CubicMeters)) return "m3";
            if (unit.Equals(UnitTypeId.CubicMillimeters)) return "mm3";
            if (unit.Equals(UnitTypeId.CubicCentimeters)) return "cm3";
            if (unit.Equals(UnitTypeId.CubicFeet)) return "ft3";
            if (unit.Equals(UnitTypeId.Liters)) return "l";
            if (unit.Equals(UnitTypeId.Degrees) || unit.Equals(UnitTypeId.DegreesMinutes)) return "°";
            if (unit.Equals(UnitTypeId.Radians)) return "rad";
            return null;
        }

        /// <summary>The value as Properties shows it; null when empty.</summary>
        public static string? Display(Parameter p)
        {
            string? s;
            try { s = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString(); }
            catch (Exception) { s = null; }
            return string.IsNullOrEmpty(s) ? null : s;
        }

        public static ParamReading Read(Document doc, Parameter? p)
        {
            if (p == null) return ParamReading.Missing;
            var kind = KindOf(p);
            var display = Display(p) ?? "";
            switch (kind)
            {
                case ValueKind.Text:
                case ValueKind.ElementId:
                    return ParamReading.OfText(display);
                case ValueKind.Integer:
                case ValueKind.YesNo:
                    return ParamReading.OfNumber(kind, p.AsInteger(), display, null);
                case ValueKind.Number:
                    return ParamReading.OfNumber(kind, p.AsDouble(), display, null);
                case ValueKind.Measured:
                    var unit = DisplayUnitId(doc, p);
                    var v = unit == null ? p.AsDouble() : UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unit);
                    return ParamReading.OfNumber(kind, v, display, DisplayUnitOf(doc, p));
                default:
                    return ParamReading.OfNumber(kind, UnitUtils.ConvertFromInternalUnits(p.AsDouble(), BaseUnit(kind)), display, DisplayUnitOf(doc, p));
            }
        }

        /// <summary>Resolves an element NAME given for an ElementId parameter to an id
        /// (levels, phases, materials, then element types); refuses ambiguity.</summary>
        public static ParsedValue Resolve(Document doc, ParsedValue v, string param)
        {
            if (v.Kind != ValueKind.ElementId || !v.Canonical.StartsWith("elementname|", StringComparison.Ordinal)) return v;
            var name = v.Text.Trim();
            var classes = new[] { typeof(Level), typeof(Phase), typeof(Material) };
            foreach (var cls in classes)
            {
                var hits = new FilteredElementCollector(doc).OfClass(cls)
                    .Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 1) return ParsedValue.OfInteger(ValueKind.ElementId, (long)hits[0].Id.Value);
                if (hits.Count > 1)
                    throw new WriteRefusal(ErrorCodes.InvalidArgs, "'" + name + "' names " + hits.Count + " elements; give the element id for '" + param + "'.");
            }
            var types = new FilteredElementCollector(doc).WhereElementIsElementType()
                .Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (types.Count == 1) return ParsedValue.OfInteger(ValueKind.ElementId, (long)types[0].Id.Value);
            if (types.Count > 1)
                throw new WriteRefusal(ErrorCodes.InvalidArgs, "'" + name + "' names " + types.Count + " types; give the element id for '" + param + "'.");

            var near = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Element>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(Material)))
                .Select(e => e.Name).Where(n => n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct().Take(10).ToList();
            throw new WriteRefusal(ErrorCodes.NotFound, "No level, phase, material or type is named '" + name + "' for '" + param + "'.", near);
        }

        /// <summary>Parameter.Set with the value converted to internal units; false when Revit refused.</summary>
        public static bool Set(Document doc, Parameter p, ParsedValue v)
        {
            switch (v.Kind)
            {
                case ValueKind.Text:
                    return p.Set(v.Text);
                case ValueKind.Integer:
                case ValueKind.YesNo:
                    if (v.Integer < int.MinValue || v.Integer > int.MaxValue) return false;
                    return p.Set((int)v.Integer);
                case ValueKind.ElementId:
                    return p.Set(ElemIds.From(v.Integer));
                case ValueKind.Number:
                    return p.Set(v.Number);
                case ValueKind.Measured:
                    var unit = DisplayUnitId(doc, p);
                    return p.Set(unit == null ? v.Number : UnitUtils.ConvertToInternalUnits(v.Number, unit));
                default:
                    return p.Set(UnitUtils.ConvertToInternalUnits(v.Number, BaseUnit(v.Kind)));
            }
        }

        /// <summary>Read-back: the parameter now holds <paramref name="v"/>.</summary>
        public static bool Holds(Document doc, Parameter p, ParsedValue v)
        {
            switch (v.Kind)
            {
                case ValueKind.Text:
                    return string.Equals(p.AsString() ?? "", v.Text, StringComparison.Ordinal);
                case ValueKind.Integer:
                case ValueKind.YesNo:
                    return p.AsInteger() == v.Integer;
                case ValueKind.ElementId:
                    return (long)p.AsElementId().Value == v.Integer;
                default:
                    var reading = Read(doc, p);
                    return reading.Number != null && Math.Abs(reading.Number.Value - v.Number) <= FilterCondition.Tolerance(v.Number);
            }
        }

        private static ForgeTypeId BaseUnit(ValueKind kind) => kind switch
        {
            ValueKind.Length => UnitTypeId.Millimeters,
            ValueKind.Area => UnitTypeId.SquareMeters,
            ValueKind.Volume => UnitTypeId.CubicMeters,
            _ => UnitTypeId.Degrees,
        };

        private static ForgeTypeId? Spec(Parameter p)
        {
            try { return p.Definition?.GetDataType(); }
            catch (Exception) { return null; }
        }

        private static bool Measurable(ForgeTypeId spec)
        {
            try { return UnitUtils.IsMeasurableSpec(spec); }
            catch (Exception) { return false; }
        }

        private static ForgeTypeId? DisplayUnitId(Document doc, Parameter p)
        {
            var spec = Spec(p);
            if (spec == null || !Measurable(spec)) return null;
            try { return doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId(); }
            catch (Exception) { return null; }
        }
    }
}
