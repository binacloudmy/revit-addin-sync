using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using RevitProjectInfo = Autodesk.Revit.DB.ProjectInfo;

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

            // V2: Extract project-level metadata
            ExtractProjectMetadata(doc, result);

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
                    ElementId = elem.Id.Value,
                    Category = category,
                    TypeName = typeName,
                    ElementName = elementName,
                    FamilyName = familyName,
                    LevelName = levelName,
                    JkrCode = jkrCode ?? "",
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

        /// <summary>
        /// Extract project-level metadata: template, shared params, linked models.
        /// Each section is wrapped in its own try/catch so one failure (e.g. one
        /// corrupt grid) doesn't wipe ALL metadata and force the backend to
        /// cannot_verify every project-scope check.
        /// </summary>
        private static void ExtractProjectMetadata(Document doc, JkrExtractionResult result)
        {
            var projInfo = doc.ProjectInformation;

            // --- Template detection ---
            try
            {
                if (projInfo != null)
                {
                    var templateParam = projInfo.LookupParameter("Project Template")
                        ?? projInfo.LookupParameter("Template");
                    if (templateParam != null && templateParam.HasValue)
                        result.TemplateUsed = templateParam.AsString() ?? "";

                    if (string.IsNullOrEmpty(result.TemplateUsed))
                    {
                        try
                        {
                            var basicFileInfo = BasicFileInfo.Extract(doc.PathName);
                            if (basicFileInfo != null && !string.IsNullOrEmpty(basicFileInfo.AllLocalChangesSavedToCentral.ToString()))
                            {
                                if (doc.PathName != null && doc.PathName.ToLower().Contains("jkr"))
                                    result.TemplateUsed = "JKR Template (inferred from filename)";
                            }
                        }
                        catch { /* BasicFileInfo throws on unsaved/corrupt files — non-fatal */ }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractProjectMetadata[template] warning: {ex.Message}");
            }

            // --- Shared parameter file presence ---
            try
            {
                var defFile = doc.Application?.OpenSharedParameterFile();
                if (defFile != null)
                    result.SharedParamFiles.Add(defFile.Filename ?? "shared_params.txt");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractProjectMetadata[shared_params] warning: {ex.Message}");
            }

            // --- Project Information fields ---
            try
            {
                if (projInfo != null)
                {
                    ReadProjectInfoField(projInfo, "Client Name", result);
                    ReadProjectInfoField(projInfo, "Project Name", result);
                    ReadProjectInfoField(projInfo, "Project Number", result);
                    ReadProjectInfoField(projInfo, "Project Address", result);
                    ReadProjectInfoField(projInfo, "Building Name", result);
                    ReadProjectInfoField(projInfo, "Organization Name", result);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractProjectMetadata[project_info] warning: {ex.Message}");
            }

            // --- Project Base Point ---
            try
            {
                // BasePoint returns the Project Base Point when IsShared=false.
                var bpCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(BasePoint))
                    .Cast<BasePoint>()
                    .Where(bp => !bp.IsShared);
                var projBp = bpCollector.FirstOrDefault();
                if (projBp != null)
                {
                    // Revit stores base point coords in decimal feet. Convert to metres for spec parity.
                    const double FEET_TO_M = 0.3048;
                    result.BasePointE = projBp.Position.X * FEET_TO_M;
                    result.BasePointN = projBp.Position.Y * FEET_TO_M;
                    result.BasePointElev = projBp.Position.Z * FEET_TO_M;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractProjectMetadata[base_point] warning: {ex.Message}");
            }

            // --- Grid names ---
            try
            {
                var grids = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>();
                foreach (var g in grids)
                {
                    try
                    {
                        var gn = g?.Name ?? "";
                        if (!string.IsNullOrEmpty(gn)) result.GridNames.Add(gn);
                    }
                    catch { /* one bad grid shouldn't drop the rest */ }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractProjectMetadata[grids] warning: {ex.Message}");
            }

            // --- Level names + elevations ---
            try
            {
                // ProjectElevation is in decimal feet; convert to metres.
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.ProjectElevation);
                foreach (var lvl in levels)
                {
                    try
                    {
                        var lname = lvl?.Name ?? "";
                        if (string.IsNullOrEmpty(lname)) continue;
                        result.LevelNames.Add(lname);
                        result.LevelElevations.Add(lvl.ProjectElevation * 0.3048);
                    }
                    catch { /* one bad level shouldn't drop the rest */ }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractProjectMetadata[levels] warning: {ex.Message}");
            }

            // --- Linked models ---
            try
            {
                var linkCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance));
                var links = linkCollector.ToElements();
                if (links.Count > 0)
                {
                    result.HasLinkedModels = true;
                    foreach (var link in links)
                    {
                        try
                        {
                            var linkName = link.Name;
                            if (!string.IsNullOrEmpty(linkName) && !result.LinkedModelNames.Contains(linkName))
                                result.LinkedModelNames.Add(linkName);
                        }
                        catch { /* one bad link shouldn't drop the rest */ }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractProjectMetadata[links] warning: {ex.Message}");
            }
        }

        /// <summary>
        /// Lookup a built-in Project Information parameter by display name and stash
        /// its value into result.ProjectInfo so the backend validator can check it.
        /// </summary>
        private static void ReadProjectInfoField(RevitProjectInfo projInfo, string displayName, JkrExtractionResult result)
        {
            try
            {
                var p = projInfo.LookupParameter(displayName);
                var val = p?.AsString() ?? "";
                // Always record the key — an empty value is itself evidence that the field is unset.
                result.ProjectInfo[displayName] = val ?? "";
            }
            catch
            {
                // Some params throw on inaccessible projects — skip quietly.
            }
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
                    return param.AsValueString() ?? id.Value.ToString();
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

        // V2: Project-level metadata
        public string TemplateUsed { get; set; } = "";
        public List<string> SharedParamFiles { get; set; } = new List<string>();
        public bool HasLinkedModels { get; set; } = false;
        public List<string> LinkedModelNames { get; set; } = new List<string>();

        // Inputs for the new project-scope validators (Project Information, Base Point, Grids, Levels).
        public Dictionary<string, string> ProjectInfo { get; set; } = new Dictionary<string, string>();
        public double? BasePointE { get; set; }
        public double? BasePointN { get; set; }
        public double? BasePointElev { get; set; }
        public List<string> GridNames { get; set; } = new List<string>();
        public List<string> LevelNames { get; set; } = new List<string>();
        public List<double> LevelElevations { get; set; } = new List<double>();

        public int TotalElements => Elements.Count;
        public int ElementsWithJkrParams => Elements.Count(e => e.Parameters.Count > 0);
        public int ElementsWithJkrCode => Elements.Count(e => !string.IsNullOrEmpty(e.JkrCode));
        public HashSet<string> Categories => new HashSet<string>(Elements.Select(e => e.Category));

        /// <summary>
        /// Build a V2 request from extracted data.
        /// </summary>
        public JkrComplianceRequestV2 ToV2Request(int lodLevel = 300, string projectPhase = "", bool hasBpep = false)
        {
            return new JkrComplianceRequestV2
            {
                Project = new JkrProjectMetadata
                {
                    ProjectName = ProjectName,
                    FileName = FileName,
                    Discipline = Discipline,
                    LodLevel = lodLevel,
                    ProjectPhase = projectPhase,
                    HasBpep = hasBpep,
                    TemplateUsed = TemplateUsed,
                    SharedParamFiles = SharedParamFiles,
                    ProjectInfo = ProjectInfo,
                    BasePointE = BasePointE,
                    BasePointN = BasePointN,
                    BasePointElev = BasePointElev,
                    GridNames = GridNames,
                },
                Model = new JkrModelMetadata
                {
                    HasLinkedModels = HasLinkedModels,
                    LinkedModelNames = LinkedModelNames,
                    Levels = LevelNames,
                    LevelElevations = LevelElevations,
                },
                Elements = Elements,
            };
        }
    }
}
