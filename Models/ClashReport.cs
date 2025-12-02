using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents a complete clash detection report
    /// Contains all clashes detected between two element sets, along with metadata and statistics
    /// </summary>
    public class ClashReport
    {
        #region Report Identification

        /// <summary>
        /// Unique identifier for this report
        /// Format: RPT-{YYYYMMDD}-{sequential number}
        /// Example: "RPT-20251201-001"
        /// </summary>
        public string ReportId { get; set; }

        /// <summary>
        /// Date and time when this report was generated
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Version of the clash detection plugin that generated this report
        /// </summary>
        public string GeneratedByVersion { get; set; } = "1.0.0";

        #endregion

        #region Project Information

        /// <summary>
        /// Project information from the Revit document
        /// Contains project name, number, client, etc.
        /// </summary>
        public ProjectInfo ProjectInfo { get; set; }

        /// <summary>
        /// List of files involved in the clash detection
        /// Index 0: Current model
        /// Index 1+: External files
        /// </summary>
        public List<string> FilesInvolved { get; set; } = new List<string>();

        #endregion

        #region Element Selection Information

        /// <summary>
        /// Selection set for Set A (typically current model elements)
        /// Defines which elements were checked from the current document
        /// </summary>
        public ElementSelectionSet SetA { get; set; }

        /// <summary>
        /// Selection set for Set B (typically external file elements)
        /// Defines which elements were checked from external files
        /// </summary>
        public ElementSelectionSet SetB { get; set; }

        #endregion

        #region Clash Detection Settings

        /// <summary>
        /// Tolerance value used for clash detection (in model units)
        /// Elements within this distance are considered "clearance clashes"
        /// </summary>
        public double ToleranceUsed { get; set; } = 0.0;

        /// <summary>
        /// Types of clashes that were checked
        /// Example: ["Hard", "Clearance"]
        /// </summary>
        public List<string> ClashTypesChecked { get; set; } = new List<string> { "Hard", "Clearance" };

        /// <summary>
        /// Name of the user who ran the clash detection
        /// </summary>
        public string RunByUser { get; set; }

        #endregion

        #region Clash Results

        /// <summary>
        /// Total number of clashes detected
        /// </summary>
        public int TotalClashCount { get; set; } = 0;

        /// <summary>
        /// List of all clash results found
        /// </summary>
        public List<ClashResult> Clashes { get; set; } = new List<ClashResult>();

        /// <summary>
        /// Breakdown of clash count by category pairs
        /// Key = "Category1 vs Category2", Value = Count
        /// Example: {"Walls vs Ducts": 23, "Floors vs Pipes": 12}
        /// </summary>
        public Dictionary<string, int> ClashStatistics { get; set; } = new Dictionary<string, int>();

        #endregion

        #region Performance Metrics

        /// <summary>
        /// Time taken to complete clash detection (in seconds)
        /// </summary>
        public double ExecutionTimeSeconds { get; set; } = 0.0;

        /// <summary>
        /// Total number of element pairs compared
        /// Calculated as SetA element count × SetB element count
        /// </summary>
        public long TotalComparisons { get; set; } = 0;

        /// <summary>
        /// Number of comparisons per second (performance metric)
        /// </summary>
        public double ComparisonsPerSecond
        {
            get
            {
                if (ExecutionTimeSeconds <= 0)
                    return 0;
                return TotalComparisons / ExecutionTimeSeconds;
            }
        }

        #endregion

        #region Computed Properties

        /// <summary>
        /// Gets count of critical clashes
        /// </summary>
        public int CriticalClashCount
        {
            get
            {
                return Clashes?.Count(c => c.IsCritical) ?? 0;
            }
        }

        /// <summary>
        /// Gets count of warning clashes
        /// </summary>
        public int WarningClashCount
        {
            get
            {
                return Clashes?.Count(c => c.Severity == "Warning") ?? 0;
            }
        }

        /// <summary>
        /// Gets count of info clashes
        /// </summary>
        public int InfoClashCount
        {
            get
            {
                return Clashes?.Count(c => c.Severity == "Info") ?? 0;
            }
        }

        /// <summary>
        /// Gets formatted execution time
        /// </summary>
        public string FormattedExecutionTime
        {
            get
            {
                if (ExecutionTimeSeconds < 60)
                    return $"{ExecutionTimeSeconds:F1} seconds";
                else if (ExecutionTimeSeconds < 3600)
                    return $"{ExecutionTimeSeconds / 60:F1} minutes";
                else
                    return $"{ExecutionTimeSeconds / 3600:F1} hours";
            }
        }

        /// <summary>
        /// Gets a summary of files involved
        /// </summary>
        public string FilesSummary
        {
            get
            {
                if (FilesInvolved == null || FilesInvolved.Count == 0)
                    return "No files specified";

                if (FilesInvolved.Count == 1)
                    return FilesInvolved[0];

                return $"{FilesInvolved.Count} files involved";
            }
        }

        /// <summary>
        /// Determines if any critical clashes were found
        /// </summary>
        public bool HasCriticalClashes
        {
            get
            {
                return CriticalClashCount > 0;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Creates a summary string of the clash report for display/logging
        /// </summary>
        /// <returns>Summary string</returns>
        public string GetSummary()
        {
            return $"Clash Detection Report: {ReportId}\n" +
                   $"Generated: {Timestamp:yyyy-MM-dd HH:mm}\n" +
                   $"Project: {ProjectInfo?.DisplayName ?? "Unknown"}\n" +
                   $"Files: {FilesSummary}\n" +
                   $"\n" +
                   $"Set A: {SetA?.CategorySummary ?? "Not specified"} ({SetA?.TotalElementCount ?? 0} elements)\n" +
                   $"Set B: {SetB?.CategorySummary ?? "Not specified"} ({SetB?.TotalElementCount ?? 0} elements)\n" +
                   $"Tolerance: {ToleranceUsed} units\n" +
                   $"\n" +
                   $"Total Clashes: {TotalClashCount}\n" +
                   $"  Critical: {CriticalClashCount}\n" +
                   $"  Warning: {WarningClashCount}\n" +
                   $"  Info: {InfoClashCount}\n" +
                   $"\n" +
                   $"Execution Time: {FormattedExecutionTime}\n" +
                   $"Comparisons: {TotalComparisons:N0} ({ComparisonsPerSecond:F0}/sec)";
        }

        /// <summary>
        /// Gets a short one-line summary
        /// </summary>
        /// <returns>Short summary</returns>
        public string GetShortSummary()
        {
            return $"{ReportId}: {TotalClashCount} clashes found ({CriticalClashCount} critical) - {Timestamp:yyyy-MM-dd}";
        }

        /// <summary>
        /// Validates the clash report data
        /// </summary>
        /// <returns>Validation result</returns>
        public ValidationResult Validate()
        {
            var result = new ValidationResult();

            if (string.IsNullOrEmpty(ReportId))
                result.Errors.Add("Report ID is required");

            if (ProjectInfo == null)
                result.Warnings.Add("Project information is not available");

            if (SetA == null)
                result.Errors.Add("Set A selection is required");
            else
            {
                var setAValidation = SetA.Validate();
                if (!setAValidation.IsValid)
                    result.Errors.AddRange(setAValidation.Errors.Select(e => "Set A: " + e));
            }

            if (SetB == null)
                result.Errors.Add("Set B selection is required");
            else
            {
                var setBValidation = SetB.Validate();
                if (!setBValidation.IsValid)
                    result.Errors.AddRange(setBValidation.Errors.Select(e => "Set B: " + e));
            }

            if (FilesInvolved == null || FilesInvolved.Count == 0)
                result.Warnings.Add("No files specified in report");

            if (TotalClashCount != (Clashes?.Count ?? 0))
                result.Warnings.Add("Total clash count does not match clash list count");

            if (ExecutionTimeSeconds < 0)
                result.Warnings.Add("Invalid execution time");

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Calculates and updates clash statistics based on clash results
        /// Groups clashes by category pairs and counts them
        /// </summary>
        public void CalculateStatistics()
        {
            ClashStatistics.Clear();

            if (Clashes == null || Clashes.Count == 0)
                return;

            // Group by category pair
            var grouped = Clashes.GroupBy(c => c.CategoryPair);

            foreach (var group in grouped)
            {
                ClashStatistics[group.Key] = group.Count();
            }

            // Update total count
            TotalClashCount = Clashes.Count;
        }

        /// <summary>
        /// Gets top N category pairs with most clashes
        /// </summary>
        /// <param name="topN">Number of top pairs to return</param>
        /// <returns>List of category pairs with clash counts</returns>
        public List<(string categoryPair, int count)> GetTopClashCategories(int topN = 5)
        {
            if (ClashStatistics == null || ClashStatistics.Count == 0)
                return new List<(string, int)>();

            return ClashStatistics
                .OrderByDescending(kvp => kvp.Value)
                .Take(topN)
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();
        }

        /// <summary>
        /// Filters clashes by severity
        /// </summary>
        /// <param name="severity">Severity to filter ("Critical", "Warning", "Info")</param>
        /// <returns>Filtered list of clashes</returns>
        public List<ClashResult> GetClashesBySeverity(string severity)
        {
            if (Clashes == null)
                return new List<ClashResult>();

            return Clashes.Where(c => string.Equals(c.Severity, severity, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Filters clashes by category pair
        /// </summary>
        /// <param name="category1">First category name</param>
        /// <param name="category2">Second category name</param>
        /// <returns>Filtered list of clashes</returns>
        public List<ClashResult> GetClashesByCategory(string category1, string category2)
        {
            if (Clashes == null)
                return new List<ClashResult>();

            return Clashes.Where(c =>
                (c.Category1 == category1 && c.Category2 == category2) ||
                (c.Category1 == category2 && c.Category2 == category1)
            ).ToList();
        }

        /// <summary>
        /// Serializes the report to JSON string
        /// </summary>
        /// <param name="indented">Whether to format JSON with indentation</param>
        /// <returns>JSON string</returns>
        public string ToJson(bool indented = true)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = indented,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            return JsonSerializer.Serialize(this, options);
        }

        /// <summary>
        /// Deserializes a clash report from JSON string
        /// </summary>
        /// <param name="json">JSON string</param>
        /// <returns>ClashReport object</returns>
        public static ClashReport FromJson(string json)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            return JsonSerializer.Deserialize<ClashReport>(json, options);
        }

        /// <summary>
        /// Exports a simple text summary of the report
        /// </summary>
        /// <returns>Text summary</returns>
        public string ExportTextSummary()
        {
            var lines = new List<string>
            {
                "=".PadRight(80, '='),
                "CLASH DETECTION REPORT",
                "=".PadRight(80, '='),
                "",
                $"Report ID: {ReportId}",
                $"Generated: {Timestamp:yyyy-MM-dd HH:mm:ss}",
                $"Project: {ProjectInfo?.DisplayName ?? "Unknown"}",
                "",
                "-".PadRight(80, '-'),
                "FILES INVOLVED",
                "-".PadRight(80, '-')
            };

            if (FilesInvolved != null)
            {
                for (int i = 0; i < FilesInvolved.Count; i++)
                {
                    lines.Add($"{i + 1}. {FilesInvolved[i]}");
                }
            }

            lines.AddRange(new[]
            {
                "",
                "-".PadRight(80, '-'),
                "ELEMENT SELECTION",
                "-".PadRight(80, '-'),
                $"Set A: {SetA?.CategorySummary ?? "Not specified"}",
                $"  Elements: {SetA?.TotalElementCount ?? 0}",
                $"  Filters: {SetA?.FilterSummary ?? "None"}",
                "",
                $"Set B: {SetB?.CategorySummary ?? "Not specified"}",
                $"  Elements: {SetB?.TotalElementCount ?? 0}",
                $"  Filters: {SetB?.FilterSummary ?? "None"}",
                "",
                $"Tolerance: {ToleranceUsed} units",
                "",
                "-".PadRight(80, '-'),
                "CLASH SUMMARY",
                "-".PadRight(80, '-'),
                $"Total Clashes: {TotalClashCount}",
                $"  Critical: {CriticalClashCount}",
                $"  Warning: {WarningClashCount}",
                $"  Info: {InfoClashCount}",
                "",
                "Top Clash Categories:"
            });

            var topCategories = GetTopClashCategories(10);
            foreach (var (categoryPair, count) in topCategories)
            {
                lines.Add($"  {categoryPair}: {count}");
            }

            lines.AddRange(new[]
            {
                "",
                "-".PadRight(80, '-'),
                "PERFORMANCE",
                "-".PadRight(80, '-'),
                $"Execution Time: {FormattedExecutionTime}",
                $"Total Comparisons: {TotalComparisons:N0}",
                $"Comparisons/Second: {ComparisonsPerSecond:F0}",
                "",
                "=".PadRight(80, '=')
            });

            return string.Join("\n", lines);
        }

        #endregion
    }
}
