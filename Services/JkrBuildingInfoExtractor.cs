using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Extracts building elements with JKR parameters for BIM compliance checking.
    /// Reads all _jkr_ shared parameters from type and instance, plus JKR code.
    /// </summary>
    public class JkrBuildingInfoExtractor
    {
        // Categories to skip (non-physical / system)
        private static readonly HashSet<string> SkipCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Automatic Sketch Dimensions", "Constraints", "Legend Components",
            "Schedules", "Lines", "Cameras", "Elevations", "Section Boxes",
            "Sun Path", "Color Fill Schema", "Dimensions", "Guide Grid",
            "Phases", "Revision", "Site", "RVT Links", "Materials",
            "Piping Systems", "Views", "Sheets", "Project Information",
        };

        /// <summary>
        /// Extract all elements with their JKR parameters from the Revit model.
        /// </summary>
        public static JkrExtractionResult Extract(Document doc)
        {
            var result = new JkrExtractionResult();

            // Project info
            result.ProjectName = System.IO.Path.GetFileNameWithoutExtension(doc.PathName ?? "Untitled");
            result.FileName = System.IO.Path.GetFileNameWithoutExtension(doc.PathName ?? "");

            // Detect discipline from filename (jkrAR..., jkrST..., jkrME..., jkrEL...)
            result.Discipline = DetectDiscipline(result.FileName);

            // Get all model elements (not element types)
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .Where(e => e.Category != null && !string.IsNullOrEmpty(e.Category.Name))
                .Where(e => !SkipCategories.Contains(e.Category.Name));

            foreach (var elem in collector)
            {
                var category = elem.Category.Name;
                var typeElem = doc.GetElement(elem.GetTypeId());
                var typeName = typeElem?.Name ?? "";
                var familyName = "";

                // Get family name
                if (elem is FamilyInstance fi)
                    familyName = fi.Symbol?.Family?.Name ?? "";
                else if (typeElem != null)
                {
                    var famParam = typeElem.get_Parameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME);
                    familyName = famParam?.AsString() ?? "";
                }

                // Extract all _jkr_ parameters (both type and instance)
                var jkrParams = new Dictionary<string, string>();
                string jkrCode = null;

                // Instance parameters
                foreach (Parameter param in elem.Parameters)
                {
                    if (param.Definition == null) continue;
                    var name = param.Definition.Name;
                    if (name.Contains("_jkr_") || name == "LOD_jkr_sit")
                    {
                        var val = GetParamValue(param);
                        if (val != null) jkrParams[name] = val;
                    }
                    // JKR code might be in various params
                    if (name == "Kod_Komponen_jkr_stt" || name == "Kod_DAK_Komponen_jkr_stt")
                        jkrCode = jkrCode ?? GetParamValue(param);
                }

                // Type parameters
                if (typeElem != null)
                {
                    foreach (Parameter param in typeElem.Parameters)
                    {
                        if (param.Definition == null) continue;
                        var name = param.Definition.Name;
                        if (name.Contains("_jkr_"))
                        {
                            var val = GetParamValue(param);
                            if (val != null && !jkrParams.ContainsKey(name))
                                jkrParams[name] = val;
                        }
                        if (name == "Kod_Komponen_jkr_stt" || name == "Kod_DAK_Komponen_jkr_stt")
                            jkrCode = jkrCode ?? GetParamValue(param);
                    }
                }

                // Get level name
                string levelName = "";
                var levelId = elem.LevelId;
                if (levelId != null && levelId != ElementId.InvalidElementId)
                    levelName = doc.GetElement(levelId)?.Name ?? "";
                if (string.IsNullOrEmpty(levelName))
                {
                    // Try FAMILY_LEVEL_PARAM or SCHEDULE_LEVEL_PARAM
                    var lp = elem.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                          ?? elem.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                          ?? elem.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                    if (lp != null && lp.AsElementId() != ElementId.InvalidElementId)
                        levelName = doc.GetElement(lp.AsElementId())?.Name ?? "";
                }

                // Element name = type name for JKR naming check
                var elementName = typeName;

                result.Elements.Add(new JkrElementData
                {
                    ElementId = elem.Id.IntegerValue,
                    Category = category,
                    TypeName = typeName,
                    ElementName = elementName,
                    FamilyName = familyName,
                    LevelName = levelName,
                    JkrCode = jkrCode,
                    Parameters = jkrParams,
                });
            }

            // Deduplicate by type+category+level (send one per unique combo)
            result.Elements = result.Elements
                .GroupBy(e => $"{e.Category}|{e.TypeName}|{e.LevelName}")
                .Select(g =>
                {
                    var first = g.First();
                    // Merge all params from the group (some instance params may differ)
                    foreach (var other in g.Skip(1))
                    {
                        foreach (var kv in other.Parameters)
                        {
                            if (!first.Parameters.ContainsKey(kv.Key))
                                first.Parameters[kv.Key] = kv.Value;
                        }
                    }
                    return first;
                })
                .ToList();

            return result;
        }

        private static string DetectDiscipline(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "AR";
            var upper = fileName.ToUpper();
            if (upper.Contains("JKRAR") || upper.StartsWith("AR")) return "AR";
            if (upper.Contains("JKRST") || upper.StartsWith("ST")) return "ST";
            if (upper.Contains("JKRME") || upper.StartsWith("ME")) return "ME";
            if (upper.Contains("JKREL") || upper.StartsWith("EL")) return "EL";
            if (upper.Contains("JKRCD") || upper.StartsWith("CD")) return "CD";
            if (upper.Contains("JKRLD") || upper.StartsWith("LD")) return "LD";
            return "AR"; // default
        }

        private static string GetParamValue(Parameter param)
        {
            if (param == null || !param.HasValue) return null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    var s = param.AsString();
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                case StorageType.Integer:
                    return param.AsInteger().ToString();
                case StorageType.Double:
                    // Try display value first
                    var vs = param.AsValueString();
                    return !string.IsNullOrWhiteSpace(vs) ? vs : param.AsDouble().ToString("F2");
                case StorageType.ElementId:
                    var id = param.AsElementId();
                    if (id == ElementId.InvalidElementId) return null;
                    return param.AsValueString() ?? id.IntegerValue.ToString();
                default:
                    return null;
            }
        }
    }

    public class JkrExtractionResult
    {
        public string ProjectName { get; set; }
        public string FileName { get; set; }
        public string Discipline { get; set; } = "AR";
        public List<JkrElementData> Elements { get; set; } = new List<JkrElementData>();

        public int TotalElements => Elements.Count;
        public int ElementsWithJkrParams => Elements.Count(e => e.Parameters.Count > 0);
        public int ElementsWithJkrCode => Elements.Count(e => !string.IsNullOrEmpty(e.JkrCode));
        public HashSet<string> Categories => new HashSet<string>(Elements.Select(e => e.Category));
    }
}
