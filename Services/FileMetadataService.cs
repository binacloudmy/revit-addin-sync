using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.ApplicationServices;
using RevitWebAppSync.Models;
using RevitWebAppSync.Utils;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service responsible for extracting metadata from Revit documents
    /// and preparing files for export/upload to the web application.
    /// This service handles various Revit API operations safely.
    /// </summary>
    public class FileMetadataService
    {
        #region Public Methods

        /// <summary>
        /// Extracts comprehensive metadata from a Revit document
        /// TODO: Customize based on your web application's requirements
        /// </summary>
        /// <param name="document">The Revit document to extract metadata from</param>
        /// <returns>FileMetadata object containing extracted information</returns>
        public FileMetadata ExtractMetadata(Document document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            try
            {
                var metadata = new FileMetadata
                {
                    // Basic file information
                    FileName = GetFileName(document),
                    FilePath = GetFilePath(document),
                    FileSize = GetFileSize(document),
                    LastModified = GetLastModified(document),
                    CreatedDate = GetCreatedDate(document),

                    // Revit-specific information
                    RevitVersion = GetRevitVersion(document),
                    DocumentTitle = GetDocumentTitle(document),
                    ProjectNumber = GetProjectNumber(document),
                    ProjectName = GetProjectName(document),
                    ClientName = GetClientName(document),
                    
                    // Building/Project information
                    ProjectAddress = GetProjectAddress(document),
                    BuildingName = GetBuildingName(document),
                    
                    // Technical information
                    UnitsSystem = GetUnitsSystem(document),
                    Categories = GetCategories(document),
                    Levels = GetLevels(document),
                    Phases = GetPhases(document),
                    
                    // Statistics
                    ElementCount = GetElementCount(document),
                    ViewCount = GetViewCount(document),
                    SheetCount = GetSheetCount(document),
                    FamilyCount = GetFamilyCount(document),
                    
                    // Custom parameters (if any)
                    CustomParameters = GetCustomParameters(document)
                };

                // TODO: Add any additional metadata specific to your workflow
                // For example: discipline, building type, design phase, etc.

                return metadata;
            }
            catch (Exception ex)
            {
                // TODO: Log the exception with detailed information
                throw new InvalidOperationException($"Failed to extract metadata from document: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Exports the Revit document to a file suitable for upload
        /// TODO: Implement based on your requirements (RVT, IFC, DWG, etc.)
        /// </summary>
        /// <param name="document">Document to export</param>
        /// <returns>Path to exported file</returns>
        public string ExportFile(Document document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            try
            {
                // TODO: Determine export format based on configuration or user preference
                var exportFormat = ConfigManager.GetSetting("ExportFormat", "RVT");
                
                switch (exportFormat.ToUpper())
                {
                    case "RVT":
                        return ExportAsRVT(document);
                    case "IFC":
                        return ExportAsIFC(document);
                    case "DWG":
                        return ExportAsDWG(document);
                    case "NWC":
                        return ExportAsNavisworks(document);
                    default:
                        // Default to RVT export
                        return ExportAsRVT(document);
                }
            }
            catch (Exception ex)
            {
                // TODO: Log the exception
                throw new InvalidOperationException($"Failed to export document: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Validates that a document is suitable for sync
        /// TODO: Add validation rules based on your requirements
        /// </summary>
        /// <param name="document">Document to validate</param>
        /// <returns>Validation result with any issues found</returns>
        public ValidationResult ValidateDocument(Document document)
        {
            var result = new ValidationResult { IsValid = true };

            try
            {
                // Check if document is saved
                if (document.IsModified)
                {
                    result.Warnings.Add("Document has unsaved changes");
                }

                // Check if it's a family document
                if (document.IsFamilyDocument)
                {
                    result.Warnings.Add("This is a family document, not a project");
                }

                // Check if it's a linked file
                // TODO: Add logic to detect if this is a linked model

                // Check file size
                var filePath = GetFilePath(document);
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    var maxSizeInMB = ConfigManager.GetSetting("MaxFileSizeMB", "100");
                    if (fileInfo.Length > long.Parse(maxSizeInMB) * 1024 * 1024)
                    {
                        result.Warnings.Add($"File size ({fileInfo.Length / (1024 * 1024)} MB) exceeds recommended maximum ({maxSizeInMB} MB)");
                    }
                }

                // TODO: Add more validation rules as needed
                // - Check for required project parameters
                // - Validate that certain elements exist
                // - Check for naming conventions
                // - Validate coordinates/location

            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Errors.Add($"Validation failed: {ex.Message}");
            }

            return result;
        }

        #endregion

        #region Private Metadata Extraction Methods

        private string GetFileName(Document document)
        {
            try
            {
                return Path.GetFileName(document.PathName) ?? "Untitled.rvt";
            }
            catch
            {
                return "Unknown.rvt";
            }
        }

        private string GetFilePath(Document document)
        {
            try
            {
                return document.PathName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private long GetFileSize(Document document)
        {
            try
            {
                var filePath = GetFilePath(document);
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    return new FileInfo(filePath).Length;
                }
            }
            catch
            {
                // Ignore exceptions
            }
            return 0;
        }

        private DateTime GetLastModified(Document document)
        {
            try
            {
                var filePath = GetFilePath(document);
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    return File.GetLastWriteTime(filePath);
                }
            }
            catch
            {
                // Ignore exceptions
            }
            return DateTime.Now;
        }

        private DateTime GetCreatedDate(Document document)
        {
            try
            {
                var filePath = GetFilePath(document);
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    return File.GetCreationTime(filePath);
                }
            }
            catch
            {
                // Ignore exceptions
            }
            return DateTime.Now;
        }

        private string GetRevitVersion(Document document)
        {
            try
            {
                return document.Application.VersionNumber ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        private string GetDocumentTitle(Document document)
        {
            try
            {
                return GetProjectInfo(document, BuiltInParameter.PROJECT_NAME) ?? "Untitled";
            }
            catch
            {
                return "Untitled";
            }
        }

        private string GetProjectNumber(Document document)
        {
            try
            {
                return GetProjectInfo(document, BuiltInParameter.PROJECT_NUMBER) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string GetProjectName(Document document)
        {
            try
            {
                return GetProjectInfo(document, BuiltInParameter.PROJECT_NAME) ?? GetFileName(document);
            }
            catch
            {
                return GetFileName(document);
            }
        }

        private string GetClientName(Document document)
        {
            try
            {
                return GetProjectInfo(document, BuiltInParameter.CLIENT_NAME) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string GetProjectAddress(Document document)
        {
            try
            {
                return GetProjectInfo(document, BuiltInParameter.PROJECT_ADDRESS) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string GetBuildingName(Document document)
        {
            try
            {
                return GetProjectInfo(document, BuiltInParameter.PROJECT_BUILDING_NAME) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string GetUnitsSystem(Document document)
        {
            try
            {
                var units = document.GetUnits();
                var lengthUnit = units.GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
                return UnitUtils.GetTypeCatalogStringForUnit(lengthUnit) ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        private List<string> GetCategories(Document document)
        {
            try
            {
                var categories = new List<string>();
                var collector = new FilteredElementCollector(document);
                var elements = collector.WhereElementIsNotElementType().ToElements();
                
                var categoryNames = elements
                    .Where(e => e.Category != null)
                    .Select(e => e.Category.Name)
                    .Distinct()
                    .OrderBy(name => name)
                    .Take(50) // Limit to avoid too much data
                    .ToList();

                return categoryNames;
            }
            catch
            {
                return new List<string>();
            }
        }

        private List<string> GetLevels(Document document)
        {
            try
            {
                var collector = new FilteredElementCollector(document);
                var levels = collector.OfClass(typeof(Level)).Cast<Level>();
                
                return levels
                    .Select(level => level.Name)
                    .OrderBy(name => name)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private List<string> GetPhases(Document document)
        {
            try
            {
                var phases = document.Phases;
                return phases.Cast<Phase>()
                    .Select(phase => phase.Name)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private int GetElementCount(Document document)
        {
            try
            {
                var collector = new FilteredElementCollector(document);
                return collector.WhereElementIsNotElementType().GetElementCount();
            }
            catch
            {
                return 0;
            }
        }

        private int GetViewCount(Document document)
        {
            try
            {
                var collector = new FilteredElementCollector(document);
                return collector.OfClass(typeof(View)).GetElementCount();
            }
            catch
            {
                return 0;
            }
        }

        private int GetSheetCount(Document document)
        {
            try
            {
                var collector = new FilteredElementCollector(document);
                return collector.OfClass(typeof(ViewSheet)).GetElementCount();
            }
            catch
            {
                return 0;
            }
        }

        private int GetFamilyCount(Document document)
        {
            try
            {
                var collector = new FilteredElementCollector(document);
                return collector.OfClass(typeof(Family)).GetElementCount();
            }
            catch
            {
                return 0;
            }
        }

        private Dictionary<string, string> GetCustomParameters(Document document)
        {
            var customParams = new Dictionary<string, string>();

            try
            {
                // TODO: Extract custom project parameters
                // This depends on what custom parameters your firm uses
                var projectInfo = document.ProjectInformation;
                if (projectInfo != null)
                {
                    foreach (Parameter param in projectInfo.Parameters)
                    {
                        if (!param.IsReadOnly && param.HasValue)
                        {
                            var paramName = param.Definition.Name;
                            var paramValue = GetParameterValue(param);
                            
                            if (!string.IsNullOrEmpty(paramValue))
                            {
                                customParams[paramName] = paramValue;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore exceptions
            }

            return customParams;
        }

        private string GetProjectInfo(Document document, BuiltInParameter parameter)
        {
            try
            {
                var projectInfo = document.ProjectInformation;
                if (projectInfo != null)
                {
                    var param = projectInfo.get_Parameter(parameter);
                    if (param != null && param.HasValue)
                    {
                        return GetParameterValue(param);
                    }
                }
            }
            catch
            {
                // Ignore exceptions
            }
            return null;
        }

        private string GetParameterValue(Parameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.AsString();
                    case StorageType.Integer:
                        return parameter.AsInteger().ToString();
                    case StorageType.Double:
                        return parameter.AsDouble().ToString("F2");
                    case StorageType.ElementId:
                        return parameter.AsElementId()?.ToString();
                    default:
                        return parameter.AsValueString();
                }
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Private Export Methods

        /// <summary>
        /// Exports document as RVT file (copy to temp location)
        /// TODO: Implement RVT copy/export logic
        /// </summary>
        private string ExportAsRVT(Document document)
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "RevitWebAppSync");
                Directory.CreateDirectory(tempPath);

                var fileName = GetFileName(document);
                var tempFilePath = Path.Combine(tempPath, $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now:yyyyMMdd_HHmmss}.rvt");

                // TODO: Implement RVT export
                // For now, copy the existing file if it's saved
                var sourcePath = GetFilePath(document);
                if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                {
                    File.Copy(sourcePath, tempFilePath, true);
                    return tempFilePath;
                }

                throw new InvalidOperationException("Cannot export unsaved document as RVT");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"RVT export failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Exports document as IFC file
        /// TODO: Implement IFC export with proper options
        /// </summary>
        private string ExportAsIFC(Document document)
        {
            try
            {
                // TODO: Implement IFC export using Revit API
                // This requires using the IFC export classes and setting up proper options
                
                throw new NotImplementedException("IFC export not yet implemented. Please implement IFC export logic using Revit.IFC classes.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"IFC export failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Exports document as DWG file
        /// TODO: Implement DWG export with proper options
        /// </summary>
        private string ExportAsDWG(Document document)
        {
            try
            {
                // TODO: Implement DWG export using Revit API
                // This requires setting up DWG export options and selecting views to export
                
                throw new NotImplementedException("DWG export not yet implemented. Please implement DWG export logic using DWGExportOptions.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"DWG export failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Exports document as Navisworks file
        /// TODO: Implement Navisworks export
        /// </summary>
        private string ExportAsNavisworks(Document document)
        {
            try
            {
                // TODO: Implement Navisworks export
                // This might require Navisworks Export plugin to be installed
                
                throw new NotImplementedException("Navisworks export not yet implemented. Please implement NWC export logic.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Navisworks export failed: {ex.Message}", ex);
            }
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Result of document validation
        /// TODO: Expand based on validation requirements
        /// </summary>
        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
            public List<string> Warnings { get; set; } = new List<string>();
        }

        #endregion
    }
}