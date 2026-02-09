using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Contains the preview result of code execution (before committing changes)
    /// </summary>
    public class ExecutionPreview
    {
        /// <summary>
        /// Whether the preview execution succeeded
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Error message if preview failed
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// All detected changes
        /// </summary>
        public List<ElementChange> Changes { get; set; } = new List<ElementChange>();

        /// <summary>
        /// The code that will be executed
        /// </summary>
        public string Code { get; set; }

        /// <summary>
        /// AI explanation of what the code does
        /// </summary>
        public string Explanation { get; set; }

        /// <summary>
        /// Message returned from code execution
        /// </summary>
        public string ExecutionMessage { get; set; }

        #region Computed Properties

        /// <summary>
        /// Elements that will be created
        /// </summary>
        public List<ElementChange> CreatedElements =>
            Changes.Where(c => c.ChangeType == ChangeType.Created).ToList();

        /// <summary>
        /// Elements that will be modified
        /// </summary>
        public List<ElementChange> ModifiedElements =>
            Changes.Where(c => c.ChangeType == ChangeType.Modified).ToList();

        /// <summary>
        /// Elements that will be deleted
        /// </summary>
        public List<ElementChange> DeletedElements =>
            Changes.Where(c => c.ChangeType == ChangeType.Deleted).ToList();

        /// <summary>
        /// Elements that will be selected
        /// </summary>
        public List<ElementChange> SelectedElements =>
            Changes.Where(c => c.ChangeType == ChangeType.Selected).ToList();

        /// <summary>
        /// Total number of elements affected
        /// </summary>
        public int TotalAffected => Changes.Count;

        /// <summary>
        /// Whether this is a read-only operation (no modifications)
        /// </summary>
        public bool IsReadOnly =>
            !Changes.Any(c => c.ChangeType == ChangeType.Created ||
                             c.ChangeType == ChangeType.Modified ||
                             c.ChangeType == ChangeType.Deleted);

        /// <summary>
        /// Risk level based on change types
        /// </summary>
        public RiskLevel Risk
        {
            get
            {
                if (DeletedElements.Count > 0)
                    return RiskLevel.High;
                if (ModifiedElements.Count > 10 || CreatedElements.Count > 10)
                    return RiskLevel.Medium;
                if (ModifiedElements.Count > 0 || CreatedElements.Count > 0)
                    return RiskLevel.Low;
                return RiskLevel.None;
            }
        }

        /// <summary>
        /// Human-readable summary of all changes
        /// </summary>
        public string Summary
        {
            get
            {
                var parts = new List<string>();

                if (CreatedElements.Count > 0)
                    parts.Add($"{CreatedElements.Count} element(s) will be created");

                if (ModifiedElements.Count > 0)
                    parts.Add($"{ModifiedElements.Count} element(s) will be modified");

                if (DeletedElements.Count > 0)
                    parts.Add($"{DeletedElements.Count} element(s) will be deleted");

                if (SelectedElements.Count > 0)
                    parts.Add($"{SelectedElements.Count} element(s) will be selected");

                if (parts.Count == 0)
                    return "No changes detected";

                return string.Join(", ", parts);
            }
        }

        #endregion
    }

    /// <summary>
    /// Risk level for the operation
    /// </summary>
    public enum RiskLevel
    {
        None,   // Read-only, selection only
        Low,    // Minor modifications
        Medium, // Multiple modifications
        High    // Deletions involved
    }
}
