using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Walks a Revit document and extracts all priceable elements with quantities.
    /// </summary>
    public static class RevitModelWalker
    {
        // Categories to skip (not priceable)
        private static readonly HashSet<string> SkipCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Rooms", "Areas", "Project Information", "Sheets", "Views",
            "Grids", "Levels", "Reference Planes", "Scope Boxes",
            "Matchline", "Survey Point", "Project Base Point"
        };

        // Categories measured by area (m²)
        private static readonly HashSet<BuiltInCategory> AreaCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_CurtainWallPanels
        };

        // Categories measured by count (units)
        private static readonly HashSet<BuiltInCategory> CountCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_PlumbingFixtures,
            BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_Casework,
            BuiltInCategory.OST_Furniture,
            BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_SpecialityEquipment
        };

        // Categories measured by length (m)
        private static readonly HashSet<BuiltInCategory> LengthCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_CableTray
        };

        /// <summary>
        /// Extract all priceable elements from the Revit document.
        /// Optionally filter by level name.
        /// </summary>
        public static List<CostItem> GetAllItems(Document doc, string levelFilter = null)
        {
            var items = new List<CostItem>();

            // Get all elements that have a category
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            foreach (Element elem in collector)
            {
                if (elem.Category == null) continue;

                string categoryName = elem.Category.Name;
                if (SkipCategories.Contains(categoryName)) continue;

                // Skip area/room boundaries
                if (categoryName.StartsWith("<")) continue;

                // Get level
                string levelName = GetElementLevel(elem, doc);

                // Apply level filter if specified
                if (!string.IsNullOrEmpty(levelFilter) &&
                    !string.Equals(levelName, levelFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Get names
                string elementName = elem.Name ?? "";
                string familyName = GetFamilyName(elem);
                string typeName = GetTypeName(elem);

                // Parse JKR code
                string jkrCode = JkrCodeParser.Parse(elementName, familyName, typeName);

                // Get quantity and unit
                var (quantity, unit) = GetQuantity(elem, doc);

                // Build display name
                string displayName = BuildDisplayName(elementName, familyName, typeName);

                items.Add(new CostItem
                {
                    ElementId = elem.Id.IntegerValue,
                    Name = displayName,
                    FamilyName = familyName,
                    TypeName = typeName,
                    Category = categoryName,
                    Level = levelName ?? "Unassigned",
                    JkrCode = jkrCode,
                    Quantity = Math.Round(quantity, 2),
                    Unit = unit,
                    UnitPrice = 0,
                    PriceSource = null
                });
            }

            return items;
        }

        /// <summary>
        /// Get all unique level names in the document
        /// </summary>
        public static List<string> GetLevelNames(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => l.Name)
                .ToList();
        }

        private static string GetElementLevel(Element elem, Document doc)
        {
            // Try Level parameter
            Parameter levelParam = elem.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
            if (levelParam == null)
                levelParam = elem.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
            if (levelParam == null)
                levelParam = elem.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
            if (levelParam == null)
                levelParam = elem.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
            if (levelParam == null)
                levelParam = elem.get_Parameter(BuiltInParameter.FLOOR_PARAM_LEVEL);

            if (levelParam != null && levelParam.StorageType == StorageType.ElementId)
            {
                ElementId levelId = levelParam.AsElementId();
                if (levelId != ElementId.InvalidElementId)
                {
                    Element levelElem = doc.GetElement(levelId);
                    if (levelElem != null)
                        return levelElem.Name;
                }
            }

            // Fallback: try LevelId property
            if (elem.LevelId != null && elem.LevelId != ElementId.InvalidElementId)
            {
                Element levelElem = doc.GetElement(elem.LevelId);
                if (levelElem != null)
                    return levelElem.Name;
            }

            return null;
        }

        private static string GetFamilyName(Element elem)
        {
            if (elem is FamilyInstance fi && fi.Symbol?.Family != null)
                return fi.Symbol.Family.Name;

            // For system families (walls, floors, etc.)
            ElementType elemType = elem.Document.GetElement(elem.GetTypeId()) as ElementType;
            if (elemType != null)
                return elemType.FamilyName;

            return null;
        }

        private static string GetTypeName(Element elem)
        {
            ElementType elemType = elem.Document.GetElement(elem.GetTypeId()) as ElementType;
            return elemType?.Name;
        }

        private static (double quantity, string unit) GetQuantity(Element elem, Document doc)
        {
            if (elem.Category == null) return (1, "unit");

            var bic = (BuiltInCategory)elem.Category.Id.IntegerValue;

            // Area-based elements
            if (AreaCategories.Contains(bic))
            {
                double area = GetParameterDouble(elem, BuiltInParameter.HOST_AREA_COMPUTED);
                if (area <= 0)
                    area = GetParameterDouble(elem, BuiltInParameter.WALL_ATTR_AREA);
                if (area > 0)
                    return (UnitUtils.ConvertFromInternalUnits(area, UnitTypeId.SquareMeters), "m²");
                return (1, "m²");
            }

            // Length-based elements
            if (LengthCategories.Contains(bic))
            {
                double length = GetParameterDouble(elem, BuiltInParameter.CURVE_ELEM_LENGTH);
                if (length > 0)
                    return (UnitUtils.ConvertFromInternalUnits(length, UnitTypeId.Meters), "m");
                return (1, "m");
            }

            // Count-based (doors, windows, fixtures, etc.)
            return (1, "unit");
        }

        private static double GetParameterDouble(Element elem, BuiltInParameter param)
        {
            Parameter p = elem.get_Parameter(param);
            if (p != null && p.HasValue && p.StorageType == StorageType.Double)
                return p.AsDouble();
            return 0;
        }

        private static string BuildDisplayName(string elementName, string familyName, string typeName)
        {
            // Prefer type name for display (most descriptive)
            if (!string.IsNullOrEmpty(typeName) && typeName != elementName)
                return typeName;
            if (!string.IsNullOrEmpty(elementName))
                return elementName;
            if (!string.IsNullOrEmpty(familyName))
                return familyName;
            return "Unknown Element";
        }
    }
}
