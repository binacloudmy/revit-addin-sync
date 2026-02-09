using System.Collections.Generic;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents a change to a single Revit element
    /// </summary>
    public class ElementChange
    {
        /// <summary>
        /// Element ID in Revit
        /// </summary>
        public long ElementId { get; set; }

        /// <summary>
        /// Element name (e.g., "Door-101", "Room 201")
        /// </summary>
        public string ElementName { get; set; }

        /// <summary>
        /// Category name (e.g., "Doors", "Walls", "Rooms")
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Type of change
        /// </summary>
        public ChangeType ChangeType { get; set; }

        /// <summary>
        /// Level name where element is located
        /// </summary>
        public string Level { get; set; }

        /// <summary>
        /// Parameter changes (parameter name -> before/after values)
        /// </summary>
        public List<ParameterChange> ParameterChanges { get; set; } = new List<ParameterChange>();

        /// <summary>
        /// Human-readable summary of the change
        /// </summary>
        public string Summary
        {
            get
            {
                return ChangeType switch
                {
                    ChangeType.Created => $"{Category}: \"{ElementName}\" will be created",
                    ChangeType.Deleted => $"{Category}: \"{ElementName}\" will be deleted",
                    ChangeType.Modified => $"{Category}: \"{ElementName}\" will be modified",
                    ChangeType.Selected => $"{Category}: \"{ElementName}\" will be selected",
                    _ => $"{Category}: \"{ElementName}\""
                };
            }
        }
    }

    /// <summary>
    /// Type of change to an element
    /// </summary>
    public enum ChangeType
    {
        Created,
        Modified,
        Deleted,
        Selected
    }

    /// <summary>
    /// Represents a parameter value change
    /// </summary>
    public class ParameterChange
    {
        public string ParameterName { get; set; }
        public string BeforeValue { get; set; }
        public string AfterValue { get; set; }

        public string Summary => $"{ParameterName}: \"{BeforeValue}\" -> \"{AfterValue}\"";
    }
}
