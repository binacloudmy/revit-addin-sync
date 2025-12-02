using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service responsible for saving and managing clash detection reports
    /// Handles local file storage, report archiving, and report retrieval
    /// </summary>
    public class ClashReportService
    {
        #region Private Fields

        private readonly string _reportsDirectory;
        private readonly JsonSerializerOptions _jsonOptions;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the clash report service
        /// Creates reports directory if it doesn't exist
        /// </summary>
        public ClashReportService()
        {
            // Get user's documents folder and create reports subdirectory
            string documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            _reportsDirectory = Path.Combine(documentsPath, "RevitClashReports");

            // Create directory if it doesn't exist
            if (!Directory.Exists(_reportsDirectory))
            {
                Directory.CreateDirectory(_reportsDirectory);
            }

            // Configure JSON serialization options
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
        }

        /// <summary>
        /// Initializes the clash report service with a custom directory
        /// </summary>
        /// <param name="reportsDirectory">Custom directory path for storing reports</param>
        public ClashReportService(string reportsDirectory)
        {
            _reportsDirectory = reportsDirectory;

            // Create directory if it doesn't exist
            if (!Directory.Exists(_reportsDirectory))
            {
                Directory.CreateDirectory(_reportsDirectory);
            }

            // Configure JSON serialization options
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Saves a clash report to the local file system
        /// Creates both JSON and text summary versions
        /// </summary>
        /// <param name="report">Clash report to save</param>
        /// <returns>Path to the saved JSON file</returns>
        public string SaveReport(ClashReport report)
        {
            try
            {
                // Validate report
                var validation = report.Validate();
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException($"Cannot save invalid report: {string.Join(", ", validation.Errors)}");
                }

                // Generate file name based on report ID and timestamp
                string fileName = $"{report.ReportId}_{report.Timestamp:yyyyMMdd_HHmmss}";
                string jsonPath = Path.Combine(_reportsDirectory, $"{fileName}.json");
                string txtPath = Path.Combine(_reportsDirectory, $"{fileName}.txt");

                // Serialize report to JSON
                string jsonContent = JsonSerializer.Serialize(report, _jsonOptions);
                File.WriteAllText(jsonPath, jsonContent);

                // Save text summary
                string textSummary = report.ExportTextSummary();
                File.WriteAllText(txtPath, textSummary);

                return jsonPath;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to save clash report: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Loads a clash report from a JSON file
        /// </summary>
        /// <param name="filePath">Path to the JSON file</param>
        /// <returns>Loaded clash report</returns>
        public ClashReport LoadReport(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"Report file not found: {filePath}");
                }

                string jsonContent = File.ReadAllText(filePath);
                var report = JsonSerializer.Deserialize<ClashReport>(jsonContent, _jsonOptions);

                return report;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load clash report: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets a list of all saved clash reports
        /// </summary>
        /// <returns>List of report file information</returns>
        public List<ReportFileInfo> GetAllReports()
        {
            try
            {
                var reportFiles = Directory.GetFiles(_reportsDirectory, "*.json");
                var reports = new List<ReportFileInfo>();

                foreach (var filePath in reportFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);

                        // Try to load report to get metadata
                        var report = LoadReport(filePath);

                        reports.Add(new ReportFileInfo
                        {
                            FilePath = filePath,
                            FileName = fileInfo.Name,
                            ReportId = report.ReportId,
                            Timestamp = report.Timestamp,
                            ProjectName = report.ProjectInfo?.Name ?? "Unknown",
                            TotalClashes = report.TotalClashCount,
                            CriticalClashes = report.CriticalClashCount,
                            FileSize = fileInfo.Length
                        });
                    }
                    catch
                    {
                        // Skip corrupted or invalid files
                        continue;
                    }
                }

                // Sort by timestamp descending (newest first)
                return reports.OrderByDescending(r => r.Timestamp).ToList();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to retrieve reports: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets reports for a specific project
        /// </summary>
        /// <param name="projectName">Project name to filter by</param>
        /// <returns>List of report file information</returns>
        public List<ReportFileInfo> GetReportsByProject(string projectName)
        {
            var allReports = GetAllReports();
            return allReports
                .Where(r => string.Equals(r.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Deletes a clash report file
        /// </summary>
        /// <param name="filePath">Path to the report file to delete</param>
        public void DeleteReport(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);

                    // Also delete text summary if it exists
                    string txtPath = Path.ChangeExtension(filePath, ".txt");
                    if (File.Exists(txtPath))
                    {
                        File.Delete(txtPath);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to delete report: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Archives old reports by moving them to an archive subdirectory
        /// </summary>
        /// <param name="olderThanDays">Archive reports older than this many days</param>
        /// <returns>Number of reports archived</returns>
        public int ArchiveOldReports(int olderThanDays = 30)
        {
            try
            {
                string archiveDirectory = Path.Combine(_reportsDirectory, "Archive");
                if (!Directory.Exists(archiveDirectory))
                {
                    Directory.CreateDirectory(archiveDirectory);
                }

                var cutoffDate = DateTime.Now.AddDays(-olderThanDays);
                var allReports = GetAllReports();
                int archivedCount = 0;

                foreach (var report in allReports)
                {
                    if (report.Timestamp < cutoffDate)
                    {
                        string archivePath = Path.Combine(archiveDirectory, report.FileName);
                        File.Move(report.FilePath, archivePath);

                        // Also move text summary
                        string txtPath = Path.ChangeExtension(report.FilePath, ".txt");
                        if (File.Exists(txtPath))
                        {
                            string txtArchivePath = Path.ChangeExtension(archivePath, ".txt");
                            File.Move(txtPath, txtArchivePath);
                        }

                        archivedCount++;
                    }
                }

                return archivedCount;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to archive reports: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets the reports directory path
        /// </summary>
        /// <returns>Full path to reports directory</returns>
        public string GetReportsDirectory()
        {
            return _reportsDirectory;
        }

        /// <summary>
        /// Exports a report to CSV format for analysis in Excel
        /// </summary>
        /// <param name="report">Clash report to export</param>
        /// <returns>Path to the CSV file</returns>
        public string ExportToCSV(ClashReport report)
        {
            try
            {
                string fileName = $"{report.ReportId}_{report.Timestamp:yyyyMMdd_HHmmss}.csv";
                string csvPath = Path.Combine(_reportsDirectory, fileName);

                using (var writer = new StreamWriter(csvPath))
                {
                    // Write header
                    writer.WriteLine("Clash ID,Severity,Type,Category 1,Category 2,Element ID 1,Element ID 2,Location X,Location Y,Location Z,Overlap Volume,Distance,Category Pair");

                    // Write clash data
                    foreach (var clash in report.Clashes)
                    {
                        writer.WriteLine($"{clash.ClashId}," +
                                       $"{clash.Severity}," +
                                       $"{clash.ClashType}," +
                                       $"{EscapeCSV(clash.Category1)}," +
                                       $"{EscapeCSV(clash.Category2)}," +
                                       $"{clash.ElementId1}," +
                                       $"{clash.ElementId2}," +
                                       $"{clash.ClashPoint.X:F3}," +
                                       $"{clash.ClashPoint.Y:F3}," +
                                       $"{clash.ClashPoint.Z:F3}," +
                                       $"{clash.OverlapVolume:F6}," +
                                       $"{clash.Distance:F3}," +
                                       $"{EscapeCSV(clash.CategoryPair)}");
                    }
                }

                return csvPath;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to export to CSV: {ex.Message}", ex);
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Escapes CSV fields that contain commas or quotes
        /// </summary>
        private string EscapeCSV(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Information about a saved report file
        /// </summary>
        public class ReportFileInfo
        {
            public string FilePath { get; set; }
            public string FileName { get; set; }
            public string ReportId { get; set; }
            public DateTime Timestamp { get; set; }
            public string ProjectName { get; set; }
            public int TotalClashes { get; set; }
            public int CriticalClashes { get; set; }
            public long FileSize { get; set; }
        }

        #endregion
    }
}
