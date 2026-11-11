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
            if (string.IsNullOrEmpty(s)) return null;
            return Unrounded(p, s!) ?? s;
        }

        /// <summary>A small value the project's rounding shows as zero ("0 A" for 0.04 A), shown in full so a
        /// read-back never reports a value that was set as gone. Null when Properties' text is fine.</summary>
        private static string? Unrounded(Parameter p, string shown)
        {
            if (p.StorageType != StorageType.Double) return null;
            var digits = new string(shown.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
            if (!double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n != 0) return null;
            try
            {
                var raw = p.AsDouble();
                if (raw == 0) return null;
                var value = UnitUtils.ConvertFromInternalUnits(raw, p.GetUnitTypeId());
                var symbol = Symbol(p);
                return value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + (symbol.Length > 0 ? " " + symbol : "");
            }
            catch (Exception) { return null; }
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
                    var unit = v.Unit != null ? UnitOf(v.Unit) : DisplayUnitId(doc, p);
                    return p.Set(unit == null ? v.Number : UnitUtils.ConvertToInternalUnits(v.Number, unit));
                default:
                    return p.Set(UnitUtils.ConvertToInternalUnits(v.Number, BaseUnit(v.Kind)));
            }
        }

        /// <summary>Why a value given with its own unit ("10 W") cannot go into <paramref name="p"/>;
        /// null when it can (or carries no unit).</summary>
        public static string? UnitProblem(Document doc, Parameter p, ParsedValue v)
        {
            if (v.Kind != ValueKind.Measured || v.Unit == null) return null;
            var unit = UnitOf(v.Unit);
            var spec = Spec(p);
            var symbol = Symbol(p);
            var shown = symbol.Length > 0 ? " (it shows " + symbol + ")" : "";
            if (unit == null || spec == null) return "'" + ValueParser.SymbolOf(v.Unit) + "' is not a unit Revit knows for this parameter" + shown;
            try
            {
                if (!UnitUtils.IsValidUnit(spec, unit)) return "'" + ValueParser.SymbolOf(v.Unit) + "' is not a unit of this parameter" + shown;
            }
            catch (Exception) { return "'" + ValueParser.SymbolOf(v.Unit) + "' cannot be checked against this parameter" + shown; }
            return null;
        }

        private static ForgeTypeId? UnitOf(string key) => key switch
        {
            "a" => UnitTypeId.Amperes,
            "ma" => UnitTypeId.Milliamperes,
            "ka" => UnitTypeId.Kiloamperes,
            "v" => UnitTypeId.Volts,
            "mv" => UnitTypeId.Millivolts,
            "kv" => UnitTypeId.Kilovolts,
            "w" => UnitTypeId.Watts,
            "kw" => UnitTypeId.Kilowatts,
            "va" => UnitTypeId.VoltAmperes,
            "kva" => UnitTypeId.KilovoltAmperes,
            "lm" => UnitTypeId.Lumens,
            "lx" => UnitTypeId.Lux,
            "cd" => UnitTypeId.Candelas,
            "hz" => UnitTypeId.Hertz,
            "°c" => UnitTypeId.Celsius,
            "kelvin" => UnitTypeId.Kelvin,
            "kg" => UnitTypeId.Kilograms,
            "n" => UnitTypeId.Newtons,
            "kn" => UnitTypeId.Kilonewtons,
            "pa" => UnitTypeId.Pascals,
            "kpa" => UnitTypeId.Kilopascals,
            "l/s" => UnitTypeId.LitersPerSecond,
            "%" => UnitTypeId.Percentage,
            "‰" => UnitTypeId.PerMille,
            _ => null,
        };

        /// <summary>The unit text Properties puts after the number ("A" from "0 A"), or "" when none.</summary>
        private static string Symbol(Parameter p)
        {
            string? s;
            try { s = p.AsValueString(); } catch (Exception) { s = null; }
            if (string.IsNullOrEmpty(s)) return "";
            var i = s!.Length;
            while (i > 0 && !char.IsDigit(s[i - 1])) i--;
            return s.Substring(i).Trim();
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
                case ValueKind.Measured when v.Unit != null && UnitOf(v.Unit) is ForgeTypeId given:
                    return Math.Abs(UnitUtils.ConvertFromInternalUnits(p.AsDouble(), given) - v.Number) <= FilterCondition.Tolerance(v.Number);
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
