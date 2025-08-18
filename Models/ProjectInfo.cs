using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents a project in the web application
    /// Contains information needed to associate Revit files with web app projects
    /// TODO: Customize properties based on your web application's project structure
    /// </summary>
    public class ProjectInfo
    {
        #region Basic Project Information

        /// <summary>
        /// Unique identifier for the project in the web application
        /// This is typically assigned by the web app when project is created
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Project name/title
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Project number (often used for identification and folder organization)
        /// </summary>
        public string Number { get; set; }

        /// <summary>
        /// Detailed project description
        /// </summary>
        public string Description { get; set; }

        #endregion

        #region Client and Location Information

        /// <summary>
        /// Name of the client/owner
        /// </summary>
        public string ClientName { get; set; }

        /// <summary>
        /// Project address or location
        /// </summary>
        public string Address { get; set; }

        /// <summary>
        /// City where project is located
        /// </summary>
        public string City { get; set; }

        /// <summary>
        /// State/Province where project is located
        /// </summary>
        public string State { get; set; }

        /// <summary>
        /// Country where project is located
        /// </summary>
        public string Country { get; set; }

        #endregion

        #region Project Classification

        /// <summary>
        /// Type of building/project (e.g., "Office", "Residential", "Hospital")
        /// TODO: Define standard building types for your organization
        /// </summary>
        public string BuildingType { get; set; }

        /// <summary>
        /// Project phase (e.g., "Design Development", "Construction Documents", "Construction Administration")
        /// TODO: Define standard phases for your workflow
        /// </summary>
        public string Phase { get; set; }

        /// <summary>
        /// Project status (e.g., "Active", "On Hold", "Complete", "Cancelled")
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Discipline or department responsible (e.g., "Architecture", "MEP", "Structural")
        /// </summary>
        public string Discipline { get; set; }

        #endregion

        #region Team Information

        /// <summary>
        /// Project manager name
        /// </summary>
        public string ProjectManager { get; set; }

        /// <summary>
        /// Principal architect/engineer name
        /// </summary>
        public string PrincipalDesigner { get; set; }

        /// <summary>
        /// List of team members assigned to project
        /// TODO: Consider creating separate TeamMember class if more detail is needed
        /// </summary>
        public List<string> TeamMembers { get; set; } = new List<string>();

        #endregion

        #region Timeline Information

        /// <summary>
        /// Date when project was created in the system
        /// </summary>
        public DateTime CreatedDate { get; set; }

        /// <summary>
        /// Date when project information was last updated
        /// </summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>
        /// Project start date
        /// </summary>
        public DateTime? StartDate { get; set; }

        /// <summary>
        /// Expected completion date
        /// </summary>
        public DateTime? CompletionDate { get; set; }

        #endregion

        #region Technical Information

        /// <summary>
        /// Total project area (in project units)
        /// </summary>
        public double? TotalArea { get; set; }

        /// <summary>
        /// Number of floors/levels
        /// </summary>
        public int? FloorCount { get; set; }

        /// <summary>
        /// Approximate construction cost
        /// </summary>
        public decimal? EstimatedCost { get; set; }

        /// <summary>
        /// Currency for cost information
        /// </summary>
        public string Currency { get; set; } = "USD";

        #endregion

        #region File Sync Information

        /// <summary>
        /// Date when Revit file was last synced to this project
        /// </summary>
        public DateTime? LastSyncDate { get; set; }

        /// <summary>
        /// Name of user who performed last sync
        /// </summary>
        public string LastSyncUser { get; set; }

        /// <summary>
        /// Current Revit file name associated with project
        /// </summary>
        public string CurrentFileName { get; set; }

        /// <summary>
        /// Hash of the current file (for change detection)
        /// </summary>
        public string CurrentFileHash { get; set; }

        /// <summary>
        /// URL where current file is stored (OSS or other cloud storage)
        /// </summary>
        public string CurrentFileUrl { get; set; }

        /// <summary>
        /// Number of times files have been synced to this project
        /// </summary>
        public int SyncCount { get; set; } = 0;

        #endregion

        #region Custom Properties

        /// <summary>
        /// Dictionary for storing custom project properties
        /// TODO: Expand based on your organization's specific requirements
        /// Key = Property name, Value = Property value as string
        /// </summary>
        public Dictionary<string, string> CustomProperties { get; set; } = new Dictionary<string, string>();

        #endregion

        #region Computed Properties

        /// <summary>
        /// Gets project display name combining number and name
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Number) && !string.IsNullOrEmpty(Name))
                    return $"{Number} - {Name}";
                else if (!string.IsNullOrEmpty(Name))
                    return Name;
                else if (!string.IsNullOrEmpty(Number))
                    return Number;
                else
                    return Id ?? "Unnamed Project";
            }
        }

        /// <summary>
        /// Gets full address as single string
        /// </summary>
        public string FullAddress
        {
            get
            {
                var addressParts = new List<string>();

                if (!string.IsNullOrEmpty(Address))
                    addressParts.Add(Address);

                if (!string.IsNullOrEmpty(City))
                    addressParts.Add(City);

                if (!string.IsNullOrEmpty(State))
                    addressParts.Add(State);

                if (!string.IsNullOrEmpty(Country))
                    addressParts.Add(Country);

                return string.Join(", ", addressParts);
            }
        }

        /// <summary>
        /// Gets formatted cost string
        /// </summary>
        public string FormattedCost
        {
            get
            {
                if (!EstimatedCost.HasValue)
                    return "Not specified";

                return EstimatedCost.Value.ToString("C0") + " " + Currency;
            }
        }

        /// <summary>
        /// Determines if project appears to be active
        /// </summary>
        public bool IsActive
        {
            get
            {
                return string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(Status, "In Progress", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Gets days since last sync (if any)
        /// </summary>
        public int? DaysSinceLastSync
        {
            get
            {
                if (!LastSyncDate.HasValue)
                    return null;

                return (int)(DateTime.UtcNow - LastSyncDate.Value).TotalDays;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Creates a summary string of the project for display/logging
        /// </summary>
        /// <returns>Project summary</returns>
        public string GetSummary()
        {
            return $"Project: {DisplayName}\n" +
                   $"Client: {ClientName ?? "Not specified"}\n" +
                   $"Location: {FullAddress}\n" +
                   $"Type: {BuildingType ?? "Not specified"}\n" +
                   $"Phase: {Phase ?? "Not specified"}\n" +
                   $"Status: {Status ?? "Unknown"}\n" +
                   $"Last Sync: {LastSyncDate?.ToString("yyyy-MM-dd HH:mm") ?? "Never"}";
        }

        /// <summary>
        /// Validates project information
        /// </summary>
        /// <returns>Validation result</returns>
        public ValidationResult Validate()
        {
            var result = new ValidationResult();

            if (string.IsNullOrEmpty(Name))
                result.Errors.Add("Project name is required");

            if (string.IsNullOrEmpty(Id))
                result.Errors.Add("Project ID is required");

            if (string.IsNullOrEmpty(Status))
                result.Warnings.Add("Project status should be specified");

            if (string.IsNullOrEmpty(ClientName))
                result.Warnings.Add("Client name should be specified");

            if (CreatedDate == default(DateTime))
                result.Warnings.Add("Created date is not set");

            // TODO: Add more validation rules based on your requirements

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Updates sync information when a file is synced
        /// </summary>
        /// <param name="fileName">Name of synced file</param>
        /// <param name="fileHash">Hash of synced file</param>
        /// <param name="fileUrl">URL where file is stored</param>
        /// <param name="userName">Name of user performing sync</param>
        public void UpdateSyncInfo(string fileName, string fileHash, string fileUrl, string userName)
        {
            CurrentFileName = fileName;
            CurrentFileHash = fileHash;
            CurrentFileUrl = fileUrl;
            LastSyncUser = userName;
            LastSyncDate = DateTime.UtcNow;
            SyncCount++;
            LastUpdated = DateTime.UtcNow;
        }

        /// <summary>
        /// Checks if this project matches the given file metadata
        /// Useful for auto-detecting which project a file belongs to
        /// </summary>
        /// <param name="metadata">File metadata to match against</param>
        /// <returns>Match score (higher = better match)</returns>
        public int CalculateMatchScore(FileMetadata metadata)
        {
            int score = 0;

            // Exact matches get high scores
            if (!string.IsNullOrEmpty(Number) && !string.IsNullOrEmpty(metadata.ProjectNumber))
            {
                if (string.Equals(Number, metadata.ProjectNumber, StringComparison.OrdinalIgnoreCase))
                    score += 100;
            }

            if (!string.IsNullOrEmpty(Name) && !string.IsNullOrEmpty(metadata.ProjectName))
            {
                if (string.Equals(Name, metadata.ProjectName, StringComparison.OrdinalIgnoreCase))
                    score += 90;
                else if (Name.Contains(metadata.ProjectName, StringComparison.OrdinalIgnoreCase) ||
                        metadata.ProjectName.Contains(Name, StringComparison.OrdinalIgnoreCase))
                    score += 50;
            }

            if (!string.IsNullOrEmpty(ClientName) && !string.IsNullOrEmpty(metadata.ClientName))
            {
                if (string.Equals(ClientName, metadata.ClientName, StringComparison.OrdinalIgnoreCase))
                    score += 30;
            }

            if (!string.IsNullOrEmpty(Address) && !string.IsNullOrEmpty(metadata.ProjectAddress))
            {
                if (Address.Contains(metadata.ProjectAddress, StringComparison.OrdinalIgnoreCase) ||
                    metadata.ProjectAddress.Contains(Address, StringComparison.OrdinalIgnoreCase))
                    score += 20;
            }

            // TODO: Add more matching criteria based on your requirements

            return score;
        }

        #endregion
    }

    /// <summary>
    /// Validation result for project information
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