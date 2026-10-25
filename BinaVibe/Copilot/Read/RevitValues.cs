#nullable enable
// Revit-bound value helpers shared by the read tools: display-unit formatting
// (spec §10: values in display units, with the unit), element naming (type,
// family, level) and category resolution with candidates. The pure parts
// (unit symbols, WithUnit, fuzzy ranking, synonyms) live in Revit-free files.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BinaVibe.Mcp.Tools;

namespace BinaVibe.Copilot.Read
{
    internal static class RevitValues
    {
        public const double FeetToMetres = 0.3048;

        /// <summary>Display symbol of a spec in the project's units ("mm", "m²"); "" when unitless.</summary>
        public static string UnitSymbol(Document doc, ForgeTypeId? spec)
        {
            if (spec == null) return "";
            try
            {
                if (!UnitUtils.IsMeasurableSpec(spec)) return "";
                return DisplayUnits.SymbolFor(doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId().TypeId);
            }
            catch { return ""; }
        }

        /// <summary>An internal-units value formatted in project units, with the unit.</summary>
        public static string Format(Document doc, ForgeTypeId spec, double internalValue)
        {
            string formatted;
            try { formatted = UnitFormatUtils.Format(doc.GetUnits(), spec, internalValue, false); }
            catch { formatted = internalValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
            return DisplayUnits.WithUnit(formatted, UnitSymbol(doc, spec));
        }

        public static string FormatLength(Document doc, double feet) => Format(doc, SpecTypeId.Length, feet);

        /// <summary>"42 m × 18 m" for a plan extent in feet (captions; always metres).</summary>
        public static string MetresXY(double dxFeet, double dyFeet) =>
            Math.Round(Math.Abs(dxFeet) * FeetToMetres, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " m × "
            + Math.Round(Math.Abs(dyFeet) * FeetToMetres, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " m";

        /// <summary>A parameter's value as the drafter sees it, its storage type, and its unit symbol.</summary>
        public static (string? Display, string Storage, string Unit) ParamValue(Document doc, Parameter p)
        {
            string storage = p.StorageType switch
            {
                StorageType.String => "string",
                StorageType.Integer => "integer",
                StorageType.Double => "double",
                StorageType.ElementId => "element_id",
                _ => "none",
            };
            if (!p.HasValue && p.StorageType != StorageType.String) return (null, storage, "");
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        var s = p.AsString();
                        return (string.IsNullOrEmpty(s) ? null : s, storage, "");
                    case StorageType.Double:
                    {
                        ForgeTypeId? spec = null;
                        try { spec = p.Definition?.GetDataType(); } catch { }
                        var unit = UnitSymbol(doc, spec);
                        var shown = p.AsValueString();
                        if (string.IsNullOrEmpty(shown) && spec != null) return (Format(doc, spec, p.AsDouble()), storage, unit);
                        return (DisplayUnits.WithUnit(shown, unit), storage, unit);
                    }
                    case StorageType.Integer:
                    {
                        bool yesNo = false;
                        try { yesNo = p.Definition?.GetDataType()?.Equals(SpecTypeId.Boolean.YesNo) == true; } catch { }
                        if (yesNo) return (p.AsInteger() != 0 ? "Yes" : "No", storage, "");
                        return (p.AsValueString() ?? p.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture), storage, "");
                    }
                    case StorageType.ElementId:
                    {
                        var id = p.AsElementId();
                        if (id == null || id == ElementId.InvalidElementId) return (null, storage, "");
                        var shown = p.AsValueString();
                        if (string.IsNullOrEmpty(shown)) shown = doc.GetElement(id)?.Name;
                        return (string.IsNullOrEmpty(shown) ? id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : shown, storage, "");
                    }
                    default:
                        return (p.AsValueString(), storage, "");
                }
            }
            catch { return (null, storage, ""); }
        }

        /// <summary>Display value of a named parameter on the element, then on its type.</summary>
        public static string? ParamDisplay(Document doc, Element el, string name, bool includeType = true)
        {
            var p = el.LookupParameter(name);
            if (p == null && includeType)
            {
                var t = TypeOf(doc, el);
                p = t?.LookupParameter(name);
            }
            return p == null ? null : ParamValue(doc, p).Display;
        }

        public static Element? TypeOf(Document doc, Element el)
        {
            try
            {
                var tid = el.GetTypeId();
                return tid == null || tid == ElementId.InvalidElementId ? null : doc.GetElement(tid);
            }
            catch { return null; }
        }

        public static string? TypeName(Document doc, Element el) => TypeOf(doc, el)?.Name;

        public static string? FamilyName(Document doc, Element el)
        {
            try
            {
                if (el is FamilyInstance fi) return fi.Symbol?.FamilyName;
                return (TypeOf(doc, el) as ElementType)?.FamilyName;
            }
            catch { return null; }
        }

        private static readonly BuiltInParameter[] LevelParams =
        {
            BuiltInParameter.FAMILY_LEVEL_PARAM,
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
            BuiltInParameter.STAIRS_BASE_LEVEL_PARAM,
            BuiltInParameter.WALL_BASE_CONSTRAINT,
            BuiltInParameter.ROOF_BASE_LEVEL_PARAM,
            BuiltInParameter.LEVEL_PARAM,
        };

        /// <summary>The element's level id: LevelId, else the level parameter hosted elements use.</summary>
        public static ElementId LevelIdOf(Element el)
        {
            try
            {
                var lid = el.LevelId;
                if (lid != null && lid != ElementId.InvalidElementId) return lid;
                foreach (var bip in LevelParams)
                {
                    var p = el.get_Parameter(bip);
                    if (p == null || p.StorageType != StorageType.ElementId) continue;
                    var id = p.AsElementId();
                    if (id != null && id != ElementId.InvalidElementId) return id;
                }
            }
            catch { }
            return ElementId.InvalidElementId;
        }

        public static string? LevelName(Document doc, Element el)
        {
            var id = LevelIdOf(el);
            return id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;
        }

        /// <summary>Element display name: rooms by their ROOM_NAME (Room.Name appends the number).</summary>
        public static string ElementName(Element el)
        {
            try
            {
                if (el is SpatialElement)
                {
                    var n = el.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                    if (!string.IsNullOrEmpty(n)) return n!;
                }
                return el.Name ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Category names present in the document, for not_found candidates.</summary>
        public static List<string> CategoryNames(Document doc)
        {
            var names = new List<string>();
            try
            {
                foreach (Category c in doc.Settings.Categories)
                    if (c != null && !string.IsNullOrEmpty(c.Name) && c.CategoryType == CategoryType.Model) names.Add(c.Name);
            }
            catch { }
            return names;
        }

        /// <summary>Resolves a category the way the drafter or model names it: a Malay or
        /// English word ("pintu", "tandas"), a friendly name ("Plumbing Fixtures") or an
        /// OST_ name. Null plus near category names when it does not resolve.</summary>
        public static BuiltInCategory? ResolveCategory(Document doc, string text, out IReadOnlyList<string> candidates)
        {
            candidates = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(text)) return null;
            var viaSynonym = Synonyms.CategoryFor(text);
            if (viaSynonym != null && TryParseBic(viaSynonym, out var s)) return s;
            var direct = CategoryResolve.Resolve(text.Trim());
            if (direct.HasValue && Category.GetCategory(doc, direct.Value) != null) return direct;
            BuiltInCategory? robust = null;
            try { robust = Inspectors.ResolveCategoryRobust(doc, text.Trim()); } catch { }
            if (robust.HasValue) return robust;
            if (direct.HasValue) return direct;
            candidates = Fuzzy.Rank(text, CategoryNames(doc), 5);
            return null;
        }

        public static bool TryParseBic(string ost, out BuiltInCategory bic) =>
            Enum.TryParse(ost, true, out bic) && ost.StartsWith("OST_", StringComparison.OrdinalIgnoreCase);

        /// <summary>The friendly name of a built-in category in this document ("Doors").</summary>
        public static string CategoryLabel(Document doc, BuiltInCategory bic)
        {
            try
            {
                var c = Category.GetCategory(doc, bic);
                if (c != null && !string.IsNullOrEmpty(c.Name)) return c.Name;
            }
            catch { }
            var s = bic.ToString();
            return s.StartsWith("OST_", StringComparison.Ordinal) ? s.Substring(4) : s;
        }
    }
}
