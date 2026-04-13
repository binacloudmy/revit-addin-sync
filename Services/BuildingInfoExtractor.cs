using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Extracts building information from Revit model for UKBS compliance checking.
    /// Reads fire ratings, dimensions, element properties relevant to Jadual 5-11.
    /// </summary>
    public class BuildingInfoExtractor
    {
        /// <summary>
        /// Extract all compliance-relevant data from the current Revit model.
        /// </summary>
        public static BuildingComplianceData Extract(Document doc)
        {
            var data = new BuildingComplianceData();

            // Project info
            var projectInfo = doc.ProjectInformation;
            data.ProjectName = System.IO.Path.GetFileNameWithoutExtension(doc.PathName ?? "Untitled");

            // Get all levels to determine building height and storey count
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            data.StoreyCount = levels.Count;
            if (levels.Count >= 2)
                data.BuildingHeightM = Math.Round((levels.Last().Elevation - levels.First().Elevation) * 0.3048, 2); // feet to metres

            // Extract walls with fire rating
            data.Walls = ExtractWalls(doc);

            // Extract doors with fire rating
            data.Doors = ExtractDoors(doc);

            // Extract floors/slabs
            data.Floors = ExtractFloors(doc);

            // Extract stairs
            data.Stairs = ExtractStairs(doc);

            // Floor areas per level
            data.FloorAreasM2 = new Dictionary<string, double>();
            foreach (var level in levels)
            {
                var areas = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Floors)
                    .WhereElementIsNotElementType()
                    .Where(e => e.LevelId == level.Id)
                    .Cast<Element>();

                double totalArea = 0;
                foreach (var floor in areas)
                {
                    var areaParam = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                    if (areaParam != null)
                        totalArea += areaParam.AsDouble() * 0.092903; // sq ft to m²
                }
                if (totalArea > 0)
                    data.FloorAreasM2[level.Name] = Math.Round(totalArea, 2);
            }

            return data;
        }

        private static List<ComplianceElement> ExtractWalls(Document doc)
        {
            var results = new List<ComplianceElement>();
            var walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType()
                .Cast<Wall>();

            foreach (var wall in walls)
            {
                var elem = new ComplianceElement
                {
                    ElementId = (int)wall.Id.Value,
                    Category = "Walls",
                    FamilyName = wall.WallType?.FamilyName ?? "",
                    TypeName = wall.WallType?.Name ?? "",
                    LevelName = doc.GetElement(wall.LevelId)?.Name ?? "",
                };

                // Try to get fire rating parameter
                elem.FireRating = GetFireRating(wall);
                elem.ThicknessMm = Math.Round(wall.Width * 304.8, 0); // feet to mm

                // Get wall height
                var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                if (heightParam != null)
                    elem.HeightMm = Math.Round(heightParam.AsDouble() * 304.8, 0);

                results.Add(elem);
            }
            return results;
        }

        private static List<ComplianceElement> ExtractDoors(Document doc)
        {
            var results = new List<ComplianceElement>();
            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>();

            foreach (var door in doors)
            {
                var elem = new ComplianceElement
                {
                    ElementId = (int)door.Id.Value,
                    Category = "Doors",
                    FamilyName = door.Symbol?.Family?.Name ?? "",
                    TypeName = door.Symbol?.Name ?? "",
                    LevelName = doc.GetElement(door.LevelId)?.Name ?? "",
                };

                elem.FireRating = GetFireRating(door);

                // Door dimensions
                var widthParam = door.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH) ??
                                 door.Symbol?.get_Parameter(BuiltInParameter.GENERIC_WIDTH);
                var heightParam = door.Symbol?.get_Parameter(BuiltInParameter.DOOR_HEIGHT) ??
                                  door.Symbol?.get_Parameter(BuiltInParameter.GENERIC_HEIGHT);

                if (widthParam != null) elem.WidthMm = Math.Round(widthParam.AsDouble() * 304.8, 0);
                if (heightParam != null) elem.HeightMm = Math.Round(heightParam.AsDouble() * 304.8, 0);

                results.Add(elem);
            }
            return results;
        }

        private static List<ComplianceElement> ExtractFloors(Document doc)
        {
            var results = new List<ComplianceElement>();
            var floors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Floors)
                .WhereElementIsNotElementType()
                .Cast<Element>();

            foreach (var floor in floors)
            {
                var floorType = doc.GetElement(floor.GetTypeId());
                var elem = new ComplianceElement
                {
                    ElementId = (int)floor.Id.Value,
                    Category = "Floors",
                    FamilyName = floorType?.get_Parameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME)?.AsString() ?? "",
                    TypeName = floorType?.Name ?? "",
                    LevelName = doc.GetElement(floor.LevelId)?.Name ?? "",
                };

                elem.FireRating = GetFireRating(floor);

                var areaParam = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
                if (areaParam != null)
                    elem.AreaM2 = Math.Round(areaParam.AsDouble() * 0.092903, 2);

                results.Add(elem);
            }
            return results;
        }

        private static List<ComplianceElement> ExtractStairs(Document doc)
        {
            var results = new List<ComplianceElement>();
            var stairs = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Stairs)
                .WhereElementIsNotElementType()
                .Cast<Element>();

            foreach (var stair in stairs)
            {
                var elem = new ComplianceElement
                {
                    ElementId = (int)stair.Id.Value,
                    Category = "Stairs",
                    TypeName = doc.GetElement(stair.GetTypeId())?.Name ?? "",
                    LevelName = doc.GetElement(stair.LevelId)?.Name ?? "",
                };

                // Stair width
                var widthParam = stair.get_Parameter(BuiltInParameter.STAIRS_RUN_ACTUAL_RUN_WIDTH);
                if (widthParam != null)
                    elem.WidthMm = Math.Round(widthParam.AsDouble() * 304.8, 0);

                results.Add(elem);
            }
            return results;
        }

        /// <summary>
        /// Try to read fire rating from common Revit parameters.
        /// Returns value in hours (e.g. "1", "1.5", "2") or null.
        /// </summary>
        private static string GetFireRating(Element element)
        {
            // Try instance parameter first, then type
            string[] paramNames = { "Fire Rating", "FireRating", "Fire_Rating", "Fire Resistance Rating" };

            foreach (var name in paramNames)
            {
                // Instance
                var param = element.LookupParameter(name);
                if (param != null && param.HasValue)
                {
                    string val = param.AsValueString() ?? param.AsString();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }

                // Type
                var typeElem = element.Document.GetElement(element.GetTypeId());
                if (typeElem != null)
                {
                    var typeParam = typeElem.LookupParameter(name);
                    if (typeParam != null && typeParam.HasValue)
                    {
                        string val = typeParam.AsValueString() ?? typeParam.AsString();
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }
            }

            // Try built-in FIRE_RATING parameter
            var builtIn = element.get_Parameter(BuiltInParameter.FIRE_RATING);
            if (builtIn != null && builtIn.HasValue)
            {
                string val = builtIn.AsValueString() ?? builtIn.AsString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            return null;
        }
    }

    // --- Data Models ---

    public class BuildingComplianceData
    {
        public string ProjectName { get; set; }
        public string PurposeGroup { get; set; } // User-selected: I-VIII
        public int StoreyCount { get; set; }
        public double BuildingHeightM { get; set; }
        public Dictionary<string, double> FloorAreasM2 { get; set; } = new Dictionary<string, double>();
        public List<ComplianceElement> Walls { get; set; } = new List<ComplianceElement>();
        public List<ComplianceElement> Doors { get; set; } = new List<ComplianceElement>();
        public List<ComplianceElement> Floors { get; set; } = new List<ComplianceElement>();
        public List<ComplianceElement> Stairs { get; set; } = new List<ComplianceElement>();

        public double TotalFloorAreaM2 => FloorAreasM2.Values.Sum();
        public int TotalElements => Walls.Count + Doors.Count + Floors.Count + Stairs.Count;
    }

    public class ComplianceElement
    {
        public int ElementId { get; set; }
        public string Category { get; set; }
        public string FamilyName { get; set; }
        public string TypeName { get; set; }
        public string LevelName { get; set; }
        public string FireRating { get; set; } // e.g. "1", "2", null
        public double? ThicknessMm { get; set; }
        public double? WidthMm { get; set; }
        public double? HeightMm { get; set; }
        public double? AreaM2 { get; set; }

        // After compliance check
        public string ComplianceStatus { get; set; } // "pass", "fail", "unknown"
        public string RequiredFireRating { get; set; }
        public string UkbsReference { get; set; } // e.g. "Ninth Schedule, By-law 147"
        public string Issue { get; set; }
    }
}
