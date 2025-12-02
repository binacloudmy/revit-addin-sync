using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Models
{
    /// <summary>
    /// Represents a set of element selection criteria for clash detection
    /// Defines which elements to include in Set A or Set B for clash detection
    /// </summary>
    public class ElementSelectionSet
    {
        #region Basic Information

        /// <summary>
        /// Name of this selection set (e.g., "Set A", "Set B", "Current Model", "External Files")
        /// </summary>
        public string SetName { get; set; }

        /// <summary>
        /// Optional description of what this set represents
        /// </summary>
        public string Description { get; set; }

        #endregion

        #region Category Selection

        /// <summary>
        /// List of selected category names to include in clash detection
        /// Examples: "Walls", "Floors", "Ducts", "Pipes", "Structural Framing"
        /// </summary>
        public List<string> SelectedCategories { get; set; } = new List<string>();

        /// <summary>
        /// Indicates if all categories should be selected (overrides SelectedCategories list)
        /// </summary>
        public bool SelectAll { get; set; } = false;

        #endregion

        #region Filtering Options

        /// <summary>
        /// List of selected level names to filter elements
        /// Empty list means all levels are included
        /// </summary>
        public List<string> SelectedLevels { get; set; } = new List<string>();

        /// <summary>
        /// List of selected workset names to filter elements
        /// Empty list means all worksets are included
        /// </summary>
        public List<string> SelectedWorksets { get; set; } = new List<string>();

        /// <summary>
        /// List of selected phase names to filter elements
        /// Empty list means all phases are included
        /// </summary>
        public List<string> SelectedPhases { get; set; } = new List<string>();

        #endregion

        #region Current Selection Option

        /// <summary>
        /// List of specific element IDs to include (used for "Current Selection" option)
        /// When this is populated, category/level/workset filters are ignored
        /// </summary>
        public List<ElementId> SpecificElementIds { get; set; } = new List<ElementId>();

        /// <summary>
        /// Indicates if this set uses the current Revit selection
        /// </summary>
        public bool UseCurrentSelection { get; set; } = false;

        #endregion

        #region Statistics

        /// <summary>
        /// Total number of elements that match the selection criteria
        /// This is calculated after applying all filters
        /// </summary>
        public int TotalElementCount { get; set; } = 0;

        /// <summary>
        /// Breakdown of element count by category
        /// Key = Category name, Value = Element count
        /// </summary>
        public Dictionary<string, int> ElementCountByCategory { get; set; } = new Dictionary<string, int>();

        #endregion

        #region File Association

        /// <summary>
        /// File path or name that this selection set is associated with
        /// For Set A: typically the current model
        /// For Set B: typically the external linked file
        /// </summary>
        public string AssociatedFile { get; set; }

        /// <summary>
        /// Indicates if this is a selection from the current document or an external file
        /// </summary>
        public bool IsCurrentDocument { get; set; } = true;

        #endregion

        #region Computed Properties

        /// <summary>
        /// Gets a summary of selected categories
        /// </summary>
        public string CategorySummary
        {
            get
            {
                if (SelectAll)
                    return "All Categories";

                if (SelectedCategories == null || SelectedCategories.Count == 0)
                    return "No categories selected";

                if (SelectedCategories.Count <= 3)
                    return string.Join(", ", SelectedCategories);

                return $"{SelectedCategories.Count} categories selected";
            }
        }

        /// <summary>
        /// Gets a summary of applied filters
        /// </summary>
        public string FilterSummary
        {
            get
            {
                var filters = new List<string>();

                if (SelectedLevels != null && SelectedLevels.Count > 0)
                    filters.Add($"{SelectedLevels.Count} level(s)");

                if (SelectedWorksets != null && SelectedWorksets.Count > 0)
                    filters.Add($"{SelectedWorksets.Count} workset(s)");

                if (SelectedPhases != null && SelectedPhases.Count > 0)
                    filters.Add($"{SelectedPhases.Count} phase(s)");

                if (filters.Count == 0)
                    return "No filters applied";

                return "Filtered by: " + string.Join(", ", filters);
            }
        }

        /// <summary>
        /// Determines if this selection set has any valid selection criteria
        /// </summary>
        public bool HasValidSelection
        {
            get
            {
                // If using current selection, must have specific element IDs
                if (UseCurrentSelection)
                    return SpecificElementIds != null && SpecificElementIds.Count > 0;

                // If selecting all, that's valid
                if (SelectAll)
                    return true;

                // Otherwise, must have at least one category selected
                return SelectedCategories != null && SelectedCategories.Count > 0;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Creates a summary string of the selection set for display/logging
        /// </summary>
        /// <returns>Summary string</returns>
        public string GetSummary()
        {
            return $"Selection Set: {SetName}\n" +
                   $"File: {AssociatedFile ?? "Not specified"}\n" +
                   $"Categories: {CategorySummary}\n" +
                   $"Filters: {FilterSummary}\n" +
                   $"Use Current Selection: {UseCurrentSelection}\n" +
                   $"Total Elements: {TotalElementCount:N0}";
        }

        /// <summary>
        /// Validates the selection set configuration
        /// </summary>
        /// <returns>Validation result</returns>
        public ValidationResult Validate()
        {
            var result = new ValidationResult();

            if (string.IsNullOrEmpty(SetName))
                result.Warnings.Add("Set name is not specified");

            if (!HasValidSelection)
                result.Errors.Add("No valid selection criteria specified. Select categories or use current selection.");

            if (UseCurrentSelection && (SpecificElementIds == null || SpecificElementIds.Count == 0))
                result.Errors.Add("'Use Current Selection' is enabled but no element IDs are specified");

            if (!UseCurrentSelection && !SelectAll && (SelectedCategories == null || SelectedCategories.Count == 0))
                result.Errors.Add("No categories selected. Select at least one category or enable 'Select All'");

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Clears all selection criteria
        /// </summary>
        public void Clear()
        {
            SelectedCategories?.Clear();
            SelectedLevels?.Clear();
            SelectedWorksets?.Clear();
            SelectedPhases?.Clear();
            SpecificElementIds?.Clear();
            SelectAll = false;
            UseCurrentSelection = false;
            TotalElementCount = 0;
            ElementCountByCategory?.Clear();
        }

        /// <summary>
        /// Creates a copy of this selection set
        /// </summary>
        /// <returns>Cloned ElementSelectionSet</returns>
        public ElementSelectionSet Clone()
        {
            return new ElementSelectionSet
            {
                SetName = this.SetName,
                Description = this.Description,
                SelectedCategories = this.SelectedCategories != null ? new List<string>(this.SelectedCategories) : new List<string>(),
                SelectAll = this.SelectAll,
                SelectedLevels = this.SelectedLevels != null ? new List<string>(this.SelectedLevels) : new List<string>(),
                SelectedWorksets = this.SelectedWorksets != null ? new List<string>(this.SelectedWorksets) : new List<string>(),
                SelectedPhases = this.SelectedPhases != null ? new List<string>(this.SelectedPhases) : new List<string>(),
                SpecificElementIds = this.SpecificElementIds != null ? new List<ElementId>(this.SpecificElementIds) : new List<ElementId>(),
                UseCurrentSelection = this.UseCurrentSelection,
                TotalElementCount = this.TotalElementCount,
                ElementCountByCategory = this.ElementCountByCategory != null ? new Dictionary<string, int>(this.ElementCountByCategory) : new Dictionary<string, int>(),
                AssociatedFile = this.AssociatedFile,
                IsCurrentDocument = this.IsCurrentDocument
            };
        }

        #endregion
    }

    /// <summary>
    /// Predefined selection set presets for quick selection
    /// </summary>
    public static class SelectionPresets
    {
        /// <summary>
        /// Gets preset for "Architecture vs MEP" clash detection
        /// </summary>
        public static (List<string> setA, List<string> setB) ArchitectureVsMEP => (
            new List<string> { "Walls", "Floors", "Roofs", "Ceilings", "Columns", "Structural Framing" },
            new List<string> { "Ducts", "Pipes", "Cable Trays", "Conduits", "Mechanical Equipment", "Plumbing Fixtures" }
        );

        /// <summary>
        /// Gets preset for "Architecture vs Structure" clash detection
        /// </summary>
        public static (List<string> setA, List<string> setB) ArchitectureVsStructure => (
            new List<string> { "Walls", "Floors", "Roofs", "Ceilings", "Doors", "Windows" },
            new List<string> { "Structural Framing", "Structural Columns", "Structural Foundations", "Structural Rebar" }
        );

        /// <summary>
        /// Gets preset for "Structure vs MEP" clash detection
        /// </summary>
        public static (List<string> setA, List<string> setB) StructureVsMEP => (
            new List<string> { "Structural Framing", "Structural Columns", "Structural Foundations" },
            new List<string> { "Ducts", "Pipes", "Cable Trays", "Conduits", "Mechanical Equipment" }
        );

        /// <summary>
        /// Gets all common architectural categories
        /// </summary>
        public static List<string> ArchitecturalCategories => new List<string>
        {
            "Walls", "Floors", "Roofs", "Ceilings", "Doors", "Windows",
            "Curtain Panels", "Curtain Wall Mullions", "Railings", "Stairs"
        };

        /// <summary>
        /// Gets all common structural categories
        /// </summary>
        public static List<string> StructuralCategories => new List<string>
        {
            "Structural Framing", "Structural Columns", "Structural Foundations",
            "Structural Rebar", "Structural Trusses"
        };

        /// <summary>
        /// Gets all common MEP categories
        /// </summary>
        public static List<string> MEPCategories => new List<string>
        {
            "Ducts", "Pipes", "Cable Trays", "Conduits",
            "Mechanical Equipment", "Plumbing Fixtures", "Electrical Fixtures",
            "Lighting Fixtures", "Air Terminals"
        };
    }
}
