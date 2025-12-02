using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service responsible for loading element categories, levels, worksets from documents
    /// and filtering elements based on selection criteria for clash detection.
    /// This service provides helper methods for both UI population and clash detection logic.
    /// </summary>
    public class ElementFilterService
    {
        #region Category Loading Methods

        /// <summary>
        /// Gets all available categories in the document with element counts and discipline grouping
        /// </summary>
        /// <param name="doc">The Revit document to query</param>
        /// <returns>List of CategoryInfo objects with name, count, and discipline</returns>
        public List<CategoryInfo> GetAllCategories(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            var categoryInfoList = new List<CategoryInfo>();

            try
            {
                // Get all categories that have instances in the model
                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WhereElementIsViewIndependent();

                // Group elements by category
                var elementsByCategory = collector
                    .Where(e => e.Category != null)
                    .GroupBy(e => e.Category.Name);

                foreach (var group in elementsByCategory)
                {
                    var categoryName = group.Key;
                    var elementCount = group.Count();

                    var categoryInfo = new CategoryInfo
                    {
                        Name = categoryName,
                        ElementCount = elementCount,
                        DisciplineGroup = DetermineDisciplineGroup(categoryName)
                    };

                    categoryInfoList.Add(categoryInfo);
                }

                // Sort by discipline group, then by name
                return categoryInfoList
                    .OrderBy(c => c.DisciplineGroup)
                    .ThenBy(c => c.Name)
                    .ToList();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load categories from document: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets all categories from a linked document
        /// </summary>
        /// <param name="linkInstance">The Revit link instance</param>
        /// <returns>List of CategoryInfo objects</returns>
        public List<CategoryInfo> GetCategoriesFromLink(RevitLinkInstance linkInstance)
        {
            if (linkInstance == null)
                throw new ArgumentNullException(nameof(linkInstance));

            try
            {
                var linkDoc = linkInstance.GetLinkDocument();
                if (linkDoc == null)
                    throw new InvalidOperationException("Link document is not loaded");

                return GetAllCategories(linkDoc);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load categories from linked document: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets element count for a specific category
        /// </summary>
        /// <param name="doc">The Revit document</param>
        /// <param name="categoryName">Name of the category</param>
        /// <returns>Count of elements in that category</returns>
        public int GetElementCount(Document doc, string categoryName)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            if (string.IsNullOrEmpty(categoryName))
                return 0;

            try
            {
                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WhereElementIsViewIndependent();

                return collector.Count(e => e.Category != null && e.Category.Name == categoryName);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to count elements in category '{categoryName}': {ex.Message}", ex);
            }
        }

        #endregion

        #region Level, Workset, Phase Loading Methods

        /// <summary>
        /// Gets all level names in the document
        /// </summary>
        /// <param name="doc">The Revit document</param>
        /// <returns>List of level names, sorted by elevation</returns>
        public List<string> GetAllLevels(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            try
            {
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => l.Name)
                    .ToList();

                return levels;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load levels from document: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets all workset names in the document
        /// </summary>
        /// <param name="doc">The Revit document</param>
        /// <returns>List of workset names, or empty list if document is not workshared</returns>
        public List<string> GetAllWorksets(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            try
            {
                // Check if document is workshared
                if (!doc.IsWorkshared)
                    return new List<string>();

                var worksets = new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .Select(w => w.Name)
                    .OrderBy(name => name)
                    .ToList();

                return worksets;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load worksets from document: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets all phase names in the document
        /// </summary>
        /// <param name="doc">The Revit document</param>
        /// <returns>List of phase names</returns>
        public List<string> GetAllPhases(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            try
            {
                var phases = new FilteredElementCollector(doc)
                    .OfClass(typeof(Phase))
                    .Cast<Phase>()
                    .Select(p => p.Name)
                    .ToList();

                return phases;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load phases from document: {ex.Message}", ex);
            }
        }

        #endregion

        #region Element Filtering Methods

        /// <summary>
        /// Gets filtered elements from document based on ElementSelectionSet criteria
        /// This is the main method used for clash detection element collection
        /// </summary>
        /// <param name="doc">The Revit document</param>
        /// <param name="selectionSet">The selection criteria</param>
        /// <returns>List of elements matching the selection criteria</returns>
        public List<Element> GetFilteredElements(Document doc, ElementSelectionSet selectionSet)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            if (selectionSet == null)
                throw new ArgumentNullException(nameof(selectionSet));

            try
            {
                // If using specific element IDs (current selection)
                if (selectionSet.UseCurrentSelection && selectionSet.SpecificElementIds != null && selectionSet.SpecificElementIds.Count > 0)
                {
                    return selectionSet.SpecificElementIds
                        .Select(id => doc.GetElement(id))
                        .Where(e => e != null)
                        .ToList();
                }

                // Start with all elements
                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WhereElementIsViewIndependent();

                List<Element> elements;

                // Filter by categories
                if (selectionSet.SelectAll)
                {
                    // Get all elements
                    elements = collector.ToList();
                }
                else if (selectionSet.SelectedCategories != null && selectionSet.SelectedCategories.Count > 0)
                {
                    // Filter by selected categories
                    elements = FilterByCategories(doc, selectionSet.SelectedCategories);
                }
                else
                {
                    // No categories selected, return empty list
                    return new List<Element>();
                }

                // Apply additional filters
                if (selectionSet.SelectedLevels != null && selectionSet.SelectedLevels.Count > 0)
                {
                    elements = FilterByLevel(elements, selectionSet.SelectedLevels);
                }

                if (selectionSet.SelectedWorksets != null && selectionSet.SelectedWorksets.Count > 0)
                {
                    elements = FilterByWorkset(elements, selectionSet.SelectedWorksets);
                }

                if (selectionSet.SelectedPhases != null && selectionSet.SelectedPhases.Count > 0)
                {
                    elements = FilterByPhase(elements, selectionSet.SelectedPhases);
                }

                return elements;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to filter elements: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Filters elements by category names
        /// </summary>
        /// <param name="doc">The Revit document</param>
        /// <param name="categoryNames">List of category names to include</param>
        /// <returns>List of elements in the specified categories</returns>
        public List<Element> FilterByCategories(Document doc, List<string> categoryNames)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            if (categoryNames == null || categoryNames.Count == 0)
                return new List<Element>();

            try
            {
                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WhereElementIsViewIndependent();

                var elements = collector
                    .Where(e => e.Category != null && categoryNames.Contains(e.Category.Name))
                    .ToList();

                return elements;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to filter elements by categories: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Filters elements by level names
        /// </summary>
        /// <param name="elements">Elements to filter</param>
        /// <param name="levelNames">List of level names to include</param>
        /// <returns>Filtered list of elements</returns>
        public List<Element> FilterByLevel(IEnumerable<Element> elements, List<string> levelNames)
        {
            if (elements == null)
                return new List<Element>();

            if (levelNames == null || levelNames.Count == 0)
                return elements.ToList();

            try
            {
                var filtered = elements.Where(e =>
                {
                    // Try to get level parameter
                    var levelParam = e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                    if (levelParam != null && levelParam.HasValue)
                    {
                        var levelId = levelParam.AsElementId();
                        if (levelId != null && levelId != ElementId.InvalidElementId)
                        {
                            var level = e.Document.GetElement(levelId) as Level;
                            if (level != null)
                            {
                                return levelNames.Contains(level.Name);
                            }
                        }
                    }

                    // Try alternative level parameter
                    levelParam = e.get_Parameter(BuiltInParameter.LEVEL_PARAM);
                    if (levelParam != null && levelParam.HasValue)
                    {
                        var levelId = levelParam.AsElementId();
                        if (levelId != null && levelId != ElementId.InvalidElementId)
                        {
                            var level = e.Document.GetElement(levelId) as Level;
                            if (level != null)
                            {
                                return levelNames.Contains(level.Name);
                            }
                        }
                    }

                    // Element doesn't have level parameter or level not in selected list
                    return false;
                }).ToList();

                return filtered;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to filter elements by level: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Filters elements by workset names
        /// </summary>
        /// <param name="elements">Elements to filter</param>
        /// <param name="worksetNames">List of workset names to include</param>
        /// <returns>Filtered list of elements</returns>
        public List<Element> FilterByWorkset(IEnumerable<Element> elements, List<string> worksetNames)
        {
            if (elements == null)
                return new List<Element>();

            if (worksetNames == null || worksetNames.Count == 0)
                return elements.ToList();

            try
            {
                var filtered = elements.Where(e =>
                {
                    // Check if element has workset parameter
                    var worksetParam = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (worksetParam != null && worksetParam.HasValue)
                    {
                        var worksetId = worksetParam.AsElementId();
                        if (worksetId != null && worksetId != ElementId.InvalidElementId)
                        {
                            var workset = e.Document.GetWorksetTable().GetWorkset(worksetId);
                            if (workset != null)
                            {
                                return worksetNames.Contains(workset.Name);
                            }
                        }
                    }

                    return false;
                }).ToList();

                return filtered;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to filter elements by workset: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Filters elements by phase names
        /// </summary>
        /// <param name="elements">Elements to filter</param>
        /// <param name="phaseNames">List of phase names to include</param>
        /// <returns>Filtered list of elements</returns>
        public List<Element> FilterByPhase(IEnumerable<Element> elements, List<string> phaseNames)
        {
            if (elements == null)
                return new List<Element>();

            if (phaseNames == null || phaseNames.Count == 0)
                return elements.ToList();

            try
            {
                var filtered = elements.Where(e =>
                {
                    // Try to get phase created parameter
                    var phaseParam = e.get_Parameter(BuiltInParameter.PHASE_CREATED);
                    if (phaseParam != null && phaseParam.HasValue)
                    {
                        var phaseId = phaseParam.AsElementId();
                        if (phaseId != null && phaseId != ElementId.InvalidElementId)
                        {
                            var phase = e.Document.GetElement(phaseId) as Phase;
                            if (phase != null)
                            {
                                return phaseNames.Contains(phase.Name);
                            }
                        }
                    }

                    return false;
                }).ToList();

                return filtered;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to filter elements by phase: {ex.Message}", ex);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Determines the discipline group for a category name
        /// Used for organizing categories in the UI by discipline
        /// </summary>
        /// <param name="categoryName">Name of the category</param>
        /// <returns>Discipline group name</returns>
        private string DetermineDisciplineGroup(string categoryName)
        {
            if (string.IsNullOrEmpty(categoryName))
                return "Other";

            // Architecture
            if (IsArchitecturalCategory(categoryName))
                return "Architecture";

            // Structure
            if (IsStructuralCategory(categoryName))
                return "Structure";

            // MEP
            if (IsMEPCategory(categoryName))
                return "MEP";

            // Default
            return "Other";
        }

        /// <summary>
        /// Checks if category is architectural
        /// </summary>
        private bool IsArchitecturalCategory(string categoryName)
        {
            var archCategories = new[]
            {
                "Walls", "Floors", "Roofs", "Ceilings", "Doors", "Windows",
                "Curtain Panels", "Curtain Wall Mullions", "Railings", "Stairs",
                "Ramps", "Columns", "Generic Models", "Casework", "Furniture",
                "Specialty Equipment", "Entourage", "Planting", "Site"
            };

            return archCategories.Any(c => categoryName.Contains(c));
        }

        /// <summary>
        /// Checks if category is structural
        /// </summary>
        private bool IsStructuralCategory(string categoryName)
        {
            var structCategories = new[]
            {
                "Structural Framing", "Structural Columns", "Structural Foundations",
                "Structural Rebar", "Structural Trusses", "Structural Connections",
                "Structural Stiffeners", "Structural Fabric"
            };

            return structCategories.Any(c => categoryName.Contains(c));
        }

        /// <summary>
        /// Checks if category is MEP
        /// </summary>
        private bool IsMEPCategory(string categoryName)
        {
            var mepCategories = new[]
            {
                "Ducts", "Pipes", "Cable Trays", "Conduits", "Flex Ducts", "Flex Pipes",
                "Mechanical Equipment", "Plumbing Fixtures", "Electrical Fixtures",
                "Lighting Fixtures", "Air Terminals", "Sprinklers", "Fire Alarm Devices",
                "Communication Devices", "Data Devices", "Electrical Equipment",
                "Duct Fittings", "Pipe Fittings", "Duct Accessories", "Pipe Accessories"
            };

            return mepCategories.Any(c => categoryName.Contains(c));
        }

        /// <summary>
        /// Gets elements with valid solid geometry only (for clash detection)
        /// </summary>
        /// <param name="elements">Elements to filter</param>
        /// <returns>Elements that have solid geometry</returns>
        public List<Element> FilterElementsWithGeometry(IEnumerable<Element> elements)
        {
            if (elements == null)
                return new List<Element>();

            try
            {
                var filtered = elements.Where(e =>
                {
                    try
                    {
                        var geomOptions = new Options
                        {
                            ComputeReferences = false,
                            DetailLevel = ViewDetailLevel.Coarse,
                            IncludeNonVisibleObjects = false
                        };

                        var geomElement = e.get_Geometry(geomOptions);
                        if (geomElement == null)
                            return false;

                        // Check if element has any solid geometry
                        foreach (GeometryObject geomObj in geomElement)
                        {
                            if (geomObj is Solid solid && solid.Volume > 0)
                                return true;

                            if (geomObj is GeometryInstance geomInstance)
                            {
                                var instGeom = geomInstance.GetInstanceGeometry();
                                if (instGeom != null)
                                {
                                    foreach (GeometryObject instObj in instGeom)
                                    {
                                        if (instObj is Solid instSolid && instSolid.Volume > 0)
                                            return true;
                                    }
                                }
                            }
                        }

                        return false;
                    }
                    catch
                    {
                        // If we can't get geometry, exclude element
                        return false;
                    }
                }).ToList();

                return filtered;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to filter elements with geometry: {ex.Message}", ex);
            }
        }

        #endregion
    }

    /// <summary>
    /// Represents information about a category including name, count, and discipline grouping
    /// Used for populating category tree views in the UI
    /// </summary>
    public class CategoryInfo
    {
        /// <summary>
        /// Name of the category (e.g., "Walls", "Ducts")
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Number of elements in this category
        /// </summary>
        public int ElementCount { get; set; }

        /// <summary>
        /// Discipline group this category belongs to
        /// Possible values: "Architecture", "Structure", "MEP", "Other"
        /// </summary>
        public string DisciplineGroup { get; set; }

        /// <summary>
        /// Display string with name and count
        /// </summary>
        public string DisplayName => $"{Name} ({ElementCount})";
    }
}
