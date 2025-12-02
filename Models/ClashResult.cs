using System;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents a single clash detected between two elements
    /// Contains all information about the clash including location, type, and severity
    /// </summary>
    public class ClashResult
    {
        #region Basic Identification

        /// <summary>
        /// Unique identifier for this clash
        /// Format: CLASH-{timestamp}-{sequential number}
        /// Example: "CLASH-20251201-001"
        /// </summary>
        public string ClashId { get; set; }

        /// <summary>
        /// Date and time when this clash was detected
        /// </summary>
        public DateTime DetectedDate { get; set; } = DateTime.UtcNow;

        #endregion

        #region Element Information - Element 1

        /// <summary>
        /// Element ID of the first element involved in clash (as string for JSON serialization)
        /// </summary>
        public string ElementId1 { get; set; }

        /// <summary>
        /// Name or type name of the first element
        /// Example: "Basic Wall: Generic - 200mm"
        /// </summary>
        public string ElementName1 { get; set; }

        /// <summary>
        /// Category of the first element
        /// Example: "Walls"
        /// </summary>
        public string Category1 { get; set; }

        /// <summary>
        /// Level name where the first element is located
        /// </summary>
        public string Level1 { get; set; }

        /// <summary>
        /// Workset name of the first element (if applicable)
        /// </summary>
        public string Workset1 { get; set; }

        #endregion

        #region Element Information - Element 2

        /// <summary>
        /// Element ID of the second element involved in clash (as string for JSON serialization)
        /// </summary>
        public string ElementId2 { get; set; }

        /// <summary>
        /// Name or type name of the second element
        /// Example: "Supply Air Duct: Round - 400mm"
        /// </summary>
        public string ElementName2 { get; set; }

        /// <summary>
        /// Category of the second element
        /// Example: "Ducts"
        /// </summary>
        public string Category2 { get; set; }

        /// <summary>
        /// Level name where the second element is located
        /// </summary>
        public string Level2 { get; set; }

        /// <summary>
        /// Workset name of the second element (if applicable)
        /// </summary>
        public string Workset2 { get; set; }

        #endregion

        #region Clash Details

        /// <summary>
        /// XYZ coordinates of the clash point (centroid of intersection)
        /// Stored as comma-separated values for JSON serialization
        /// Format: "X,Y,Z"
        /// </summary>
        public string ClashPointString { get; set; }

        /// <summary>
        /// Type of clash detected
        /// Possible values: "Hard" (geometric intersection), "Clearance" (within tolerance), "Duplicate"
        /// </summary>
        public string ClashType { get; set; }

        /// <summary>
        /// Severity level of the clash
        /// Possible values: "Critical" (large overlap), "Warning" (small overlap), "Info" (within tolerance)
        /// </summary>
        public string Severity { get; set; }

        /// <summary>
        /// Volume of the intersection/overlap in cubic units
        /// Only applicable for "Hard" clashes
        /// </summary>
        public double OverlapVolume { get; set; } = 0.0;

        /// <summary>
        /// Clearance distance between elements in model units
        /// Only applicable for "Clearance" clashes
        /// </summary>
        public double ClearanceDistance { get; set; } = 0.0;

        #endregion

        #region Computed Properties

        /// <summary>
        /// Gets the clash point as XYZ object (non-serialized)
        /// Parses ClashPointString back to XYZ
        /// </summary>
        public XYZ ClashPoint
        {
            get
            {
                if (string.IsNullOrEmpty(ClashPointString))
                    return null;

                try
                {
                    var parts = ClashPointString.Split(',');
                    if (parts.Length == 3)
                    {
                        return new XYZ(
                            double.Parse(parts[0]),
                            double.Parse(parts[1]),
                            double.Parse(parts[2])
                        );
                    }
                }
                catch
                {
                    // Invalid format, return null
                }

                return null;
            }
            set
            {
                if (value != null)
                {
                    ClashPointString = $"{value.X},{value.Y},{value.Z}";
                }
                else
                {
                    ClashPointString = null;
                }
            }
        }

        /// <summary>
        /// Gets a formatted clash point string for display
        /// Example: "X: 10.50, Y: 20.30, Z: 3.50"
        /// </summary>
        public string FormattedClashPoint
        {
            get
            {
                var point = ClashPoint;
                if (point == null)
                    return "Unknown";

                return $"X: {point.X:F2}, Y: {point.Y:F2}, Z: {point.Z:F2}";
            }
        }

        /// <summary>
        /// Gets a summary of the clash type and severity
        /// Example: "Hard Clash (Critical)"
        /// </summary>
        public string ClashTypeSummary
        {
            get
            {
                return $"{ClashType} Clash ({Severity})";
            }
        }

        /// <summary>
        /// Gets a summary of element categories involved
        /// Example: "Walls vs Ducts"
        /// </summary>
        public string CategoryPair
        {
            get
            {
                return $"{Category1} vs {Category2}";
            }
        }

        /// <summary>
        /// Determines if this is a critical clash (requires immediate attention)
        /// </summary>
        public bool IsCritical
        {
            get
            {
                return string.Equals(Severity, "Critical", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Gets formatted overlap volume for display
        /// </summary>
        public string FormattedOverlapVolume
        {
            get
            {
                if (OverlapVolume <= 0)
                    return "N/A";

                if (OverlapVolume < 0.001)
                    return $"{OverlapVolume * 1000000:F2} mm³";
                else if (OverlapVolume < 1.0)
                    return $"{OverlapVolume * 1000:F2} cm³";
                else
                    return $"{OverlapVolume:F2} m³";
            }
        }

        /// <summary>
        /// Gets formatted clearance distance for display
        /// </summary>
        public string FormattedClearanceDistance
        {
            get
            {
                if (ClearanceDistance <= 0)
                    return "N/A";

                if (ClearanceDistance < 1.0)
                    return $"{ClearanceDistance * 1000:F0} mm";
                else
                    return $"{ClearanceDistance:F2} m";
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Creates a summary string of the clash for display/logging
        /// </summary>
        /// <returns>Summary string</returns>
        public string GetSummary()
        {
            return $"Clash ID: {ClashId}\n" +
                   $"Type: {ClashTypeSummary}\n" +
                   $"Elements: {ElementName1} vs {ElementName2}\n" +
                   $"Categories: {CategoryPair}\n" +
                   $"Location: {FormattedClashPoint}\n" +
                   $"Levels: {Level1} / {Level2}\n" +
                   (ClashType == "Hard" ? $"Overlap Volume: {FormattedOverlapVolume}\n" : "") +
                   (ClashType == "Clearance" ? $"Clearance: {FormattedClearanceDistance}\n" : "") +
                   $"Detected: {DetectedDate:yyyy-MM-dd HH:mm}";
        }

        /// <summary>
        /// Creates a short one-line description of the clash
        /// </summary>
        /// <returns>Short description</returns>
        public string GetShortDescription()
        {
            return $"{ClashId}: {Category1} vs {Category2} at {FormattedClashPoint} ({Severity})";
        }

        /// <summary>
        /// Validates the clash result data
        /// </summary>
        /// <returns>Validation result</returns>
        public ValidationResult Validate()
        {
            var result = new ValidationResult();

            if (string.IsNullOrEmpty(ClashId))
                result.Errors.Add("Clash ID is required");

            if (string.IsNullOrEmpty(ElementId1))
                result.Errors.Add("Element ID 1 is required");

            if (string.IsNullOrEmpty(ElementId2))
                result.Errors.Add("Element ID 2 is required");

            if (string.IsNullOrEmpty(ClashType))
                result.Errors.Add("Clash type is required");

            if (string.IsNullOrEmpty(Severity))
                result.Errors.Add("Severity is required");

            if (string.IsNullOrEmpty(ClashPointString))
                result.Warnings.Add("Clash point is not specified");

            if (string.IsNullOrEmpty(Category1) || string.IsNullOrEmpty(Category2))
                result.Warnings.Add("Element categories should be specified");

            if (ClashType == "Hard" && OverlapVolume <= 0)
                result.Warnings.Add("Hard clash should have overlap volume > 0");

            if (ClashType == "Clearance" && ClearanceDistance <= 0)
                result.Warnings.Add("Clearance clash should have clearance distance > 0");

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Calculates severity based on overlap volume or clearance distance
        /// </summary>
        /// <param name="tolerance">Tolerance value used in clash detection</param>
        public void CalculateSeverity(double tolerance)
        {
            if (ClashType == "Hard")
            {
                // Severity based on overlap volume
                if (OverlapVolume > 0.1) // Large overlap (> 0.1 cubic units)
                    Severity = "Critical";
                else if (OverlapVolume > 0.01) // Medium overlap
                    Severity = "Warning";
                else
                    Severity = "Info";
            }
            else if (ClashType == "Clearance")
            {
                // Severity based on how close to tolerance
                if (ClearanceDistance < tolerance * 0.5) // Less than half tolerance
                    Severity = "Critical";
                else if (ClearanceDistance < tolerance * 0.75) // Less than 3/4 tolerance
                    Severity = "Warning";
                else
                    Severity = "Info";
            }
            else
            {
                Severity = "Info";
            }
        }

        /// <summary>
        /// Sets clash point from XYZ coordinates
        /// </summary>
        /// <param name="point">XYZ point</param>
        public void SetClashPoint(XYZ point)
        {
            ClashPoint = point;
        }

        /// <summary>
        /// Creates a copy of this clash result
        /// </summary>
        /// <returns>Cloned ClashResult</returns>
        public ClashResult Clone()
        {
            return new ClashResult
            {
                ClashId = this.ClashId,
                DetectedDate = this.DetectedDate,
                ElementId1 = this.ElementId1,
                ElementName1 = this.ElementName1,
                Category1 = this.Category1,
                Level1 = this.Level1,
                Workset1 = this.Workset1,
                ElementId2 = this.ElementId2,
                ElementName2 = this.ElementName2,
                Category2 = this.Category2,
                Level2 = this.Level2,
                Workset2 = this.Workset2,
                ClashPointString = this.ClashPointString,
                ClashType = this.ClashType,
                Severity = this.Severity,
                OverlapVolume = this.OverlapVolume,
                ClearanceDistance = this.ClearanceDistance
            };
        }

        #endregion
    }
}
