using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents metadata extracted from a Revit document
    /// Contains all relevant information needed for file synchronization
    /// TODO: Customize properties based on your web application's requirements
    /// </summary>
    public class FileMetadata
    {
        #region Basic File Information

        /// <summary>
        /// Name of the Revit file (e.g., "Building_A.rvt")
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Full path to the Revit file on local system
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Size of the file in bytes
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// Date and time when file was last modified
        /// </summary>
        public DateTime LastModified { get; set; }

        /// <summary>
        /// Date and time when file was created
        /// </summary>
        public DateTime CreatedDate { get; set; }

        #endregion

        #region Revit-Specific Information

        /// <summary>
        /// Version of Revit used to create/modify the file (e.g., "2025")
        /// </summary>
        public string RevitVersion { get; set; }

        /// <summary>
        /// Document title from project information
        /// </summary>
        public string DocumentTitle { get; set; }

        /// <summary>
        /// Project number from project information
        /// TODO: This is often used for project identification
        /// </summary>
        public string ProjectNumber { get; set; }

        /// <summary>
        /// Project name from project information
        /// </summary>
        public string ProjectName { get; set; }

        /// <summary>
        /// Client name from project information
        /// </summary>
        public string ClientName { get; set; }

        #endregion

        #region Building/Project Information

        /// <summary>
        /// Project address from project information
        /// </summary>
        public string ProjectAddress { get; set; }

        /// <summary>
        /// Building name from project information
        /// </summary>
        public string BuildingName { get; set; }

        /// <summary>
        /// Units system used in the project (e.g., "Imperial", "Metric")
        /// </summary>
        public string UnitsSystem { get; set; }

        #endregion

        #region Model Content Information

        /// <summary>
        /// List of categories present in the model
        /// TODO: Consider limiting this list for performance
        /// </summary>
        public List<string> Categories { get; set; } = new List<string>();

        /// <summary>
        /// List of levels in the project
        /// </summary>
        public List<string> Levels { get; set; } = new List<string>();

        /// <summary>
        /// List of phases in the project
        /// </summary>
        public List<string> Phases { get; set; } = new List<string>();

        #endregion

        #region Statistics

        /// <summary>
        /// Total number of elements in the model
        /// Useful for understanding model complexity
        /// </summary>
        public int ElementCount { get; set; }

        /// <summary>
        /// Number of views in the project
        /// </summary>
        public int ViewCount { get; set; }

        /// <summary>
        /// Number of sheets in the project
        /// </summary>
        public int SheetCount { get; set; }

        /// <summary>
        /// Number of loaded families
        /// </summary>
        public int FamilyCount { get; set; }

        #endregion

        #region Custom Parameters

        /// <summary>
        /// Dictionary of custom project parameters
        /// TODO: Expand this based on your firm's standard parameters
        /// Key = Parameter name, Value = Parameter value as string
        /// </summary>
        public Dictionary<string, string> CustomParameters { get; set; } = new Dictionary<string, string>();

        #endregion

        #region Computed Properties

        /// <summary>
        /// Gets file size formatted as human-readable string
        /// </summary>
        public string FileSizeFormatted
        {
            get
            {
                const long kb = 1024;
                const long mb = kb * 1024;
                const long gb = mb * 1024;

                if (FileSize >= gb)
                    return $"{FileSize / (double)gb:F2} GB";
                else if (FileSize >= mb)
                    return $"{FileSize / (double)mb:F2} MB";
                else if (FileSize >= kb)
                    return $"{FileSize / (double)kb:F2} KB";
                else
                    return $"{FileSize} bytes";
            }
        }

        /// <summary>
        /// Gets a display name for the project combining name and number
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(ProjectNumber) && !string.IsNullOrEmpty(ProjectName))
                    return $"{ProjectNumber} - {ProjectName}";
                else if (!string.IsNullOrEmpty(ProjectName))
                    return ProjectName;
                else if (!string.IsNullOrEmpty(DocumentTitle))
                    return DocumentTitle;
                else
                    return FileName;
            }
        }

        /// <summary>
        /// Determines if this appears to be a valid project file
        /// (as opposed to a template, family, or test file)
        /// </summary>
        public bool IsValidProjectFile
        {
            get
            {
                // TODO: Add your own logic for determining valid project files
                // This might check for:
                // - Presence of project information
                // - Minimum element count
                // - Specific naming conventions
                // - Required parameters

                return !string.IsNullOrEmpty(ProjectName) || 
                       !string.IsNullOrEmpty(ProjectNumber) ||
                       ElementCount > 100; // Arbitrary threshold
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Creates a summary string of the metadata for logging/display
        /// </summary>
        /// <returns>Summary string</returns>
        public string GetSummary()
        {
            return $"File: {FileName}\n" +
                   $"Project: {DisplayName}\n" +
                   $"Size: {FileSizeFormatted}\n" +
                   $"Elements: {ElementCount:N0}\n" +
                   $"Views: {ViewCount}\n" +
                   $"Sheets: {SheetCount}\n" +
                   $"Revit Version: {RevitVersion}\n" +
                   $"Last Modified: {LastModified:yyyy-MM-dd HH:mm}";
        }

        /// <summary>
        /// Gets a unique identifier for this file/project combination
        /// TODO: Customize based on how you want to identify projects
        /// </summary>
        /// <returns>Unique identifier string</returns>
        public string GetUniqueId()
        {
            // Combine project number, name, and file name for unique identification
            var components = new List<string>();

            if (!string.IsNullOrEmpty(ProjectNumber))
                components.Add(ProjectNumber.Trim());

            if (!string.IsNullOrEmpty(ProjectName))
                components.Add(ProjectName.Trim());

            if (components.Count == 0 && !string.IsNullOrEmpty(FileName))
                components.Add(Path.GetFileNameWithoutExtension(FileName));

            return string.Join("-", components).Replace(" ", "-");
        }

        /// <summary>
        /// Validates that required metadata is present
        /// </summary>
        /// <returns>Validation result with any issues</returns>
        public ValidationResult Validate()
        {
            var result = new ValidationResult();

            if (string.IsNullOrEmpty(FileName))
                result.Errors.Add("File name is required");

            if (FileSize <= 0)
                result.Warnings.Add("File size is not available");

            if (string.IsNullOrEmpty(ProjectName) && string.IsNullOrEmpty(ProjectNumber))
                result.Warnings.Add("Project name or number should be specified");

            if (ElementCount <= 0)
                result.Warnings.Add("Element count is zero - this might be an empty or template file");

            // TODO: Add more validation rules based on your requirements

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        #endregion
    }

    /// <summary>
    /// Result of metadata validation
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; } = true;
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();

        public bool HasWarnings => Warnings.Count > 0;
        public bool HasErrors => Errors.Count > 0;
    }
}