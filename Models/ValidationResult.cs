using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents the result of a validation operation
    /// </summary>
    public class ValidationResult
    {
        /// <summary>
        /// Indicates if the validation passed (no errors)
        /// </summary>
        public bool IsValid { get; set; } = true;

        /// <summary>
        /// List of validation errors (critical issues)
        /// </summary>
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>
        /// List of validation warnings (non-critical issues)
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>
        /// Error message for display (used in ClashDetectionCommand)
        /// </summary>
        public string ErrorMessage { get; set; }
    }
}
