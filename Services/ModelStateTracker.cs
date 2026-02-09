using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Tracks model state before and after code execution to detect changes
    /// </summary>
    public class ModelStateTracker
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;

        private Dictionary<long, ElementSnapshot> _beforeState;
        private HashSet<long> _beforeSelection;

        public ModelStateTracker(Document doc, UIDocument uidoc)
        {
            _doc = doc;
            _uidoc = uidoc;
        }

        /// <summary>
        /// Capture the current state of the model
        /// </summary>
        public void CaptureBeforeState()
        {
            _beforeState = new Dictionary<long, ElementSnapshot>();
            _beforeSelection = new HashSet<long>();

            // Capture current selection
            foreach (var id in _uidoc.Selection.GetElementIds())
            {
                _beforeSelection.Add(id.Value);
            }

            // Capture state of elements in common categories
            var categories = new[]
            {
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_Doors,
                BuiltInCategory.OST_Windows,
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Roofs,
                BuiltInCategory.OST_Ceilings,
                BuiltInCategory.OST_Rooms,
                BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_Stairs,
                BuiltInCategory.OST_Railings,
                BuiltInCategory.OST_Casework,
                BuiltInCategory.OST_Parking
            };

            foreach (var category in categories)
            {
                try
                {
                    var elements = new FilteredElementCollector(_doc)
                        .OfCategory(category)
                        .WhereElementIsNotElementType()
                        .ToElements();

                    foreach (var element in elements)
                    {
                        _beforeState[element.Id.Value] = CreateSnapshot(element);
                    }
                }
                catch
                {
                    // Some categories may not exist in the model
                }
            }
        }

        /// <summary>
        /// Compare current state with before state and return changes
        /// </summary>
        public List<ElementChange> DetectChanges()
        {
            var changes = new List<ElementChange>();
            var afterState = new Dictionary<long, ElementSnapshot>();
            var afterSelection = new HashSet<long>();

            // Capture current selection
            foreach (var id in _uidoc.Selection.GetElementIds())
            {
                afterSelection.Add(id.Value);
            }

            // Capture current state of same categories
            var categories = new[]
            {
                BuiltInCategory.OST_Walls,
                BuiltInCategory.OST_Doors,
                BuiltInCategory.OST_Windows,
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Roofs,
                BuiltInCategory.OST_Ceilings,
                BuiltInCategory.OST_Rooms,
                BuiltInCategory.OST_Furniture,
                BuiltInCategory.OST_Columns,
                BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_Stairs,
                BuiltInCategory.OST_Railings,
                BuiltInCategory.OST_Casework,
                BuiltInCategory.OST_Parking
            };

            foreach (var category in categories)
            {
                try
                {
                    var elements = new FilteredElementCollector(_doc)
                        .OfCategory(category)
                        .WhereElementIsNotElementType()
                        .ToElements();

                    foreach (var element in elements)
                    {
                        afterState[element.Id.Value] = CreateSnapshot(element);
                    }
                }
                catch
                {
                    // Skip categories that don't exist
                }
            }

            // Detect created elements (in after but not in before)
            foreach (var kvp in afterState)
            {
                if (!_beforeState.ContainsKey(kvp.Key))
                {
                    changes.Add(new ElementChange
                    {
                        ElementId = kvp.Key,
                        ElementName = kvp.Value.Name,
                        Category = kvp.Value.Category,
                        Level = kvp.Value.Level,
                        ChangeType = ChangeType.Created
                    });
                }
            }

            // Detect deleted elements (in before but not in after)
            foreach (var kvp in _beforeState)
            {
                if (!afterState.ContainsKey(kvp.Key))
                {
                    changes.Add(new ElementChange
                    {
                        ElementId = kvp.Key,
                        ElementName = kvp.Value.Name,
                        Category = kvp.Value.Category,
                        Level = kvp.Value.Level,
                        ChangeType = ChangeType.Deleted
                    });
                }
            }

            // Detect modified elements (parameters changed)
            foreach (var kvp in afterState)
            {
                if (_beforeState.TryGetValue(kvp.Key, out var beforeSnapshot))
                {
                    var paramChanges = DetectParameterChanges(beforeSnapshot, kvp.Value);
                    if (paramChanges.Count > 0)
                    {
                        changes.Add(new ElementChange
                        {
                            ElementId = kvp.Key,
                            ElementName = kvp.Value.Name,
                            Category = kvp.Value.Category,
                            Level = kvp.Value.Level,
                            ChangeType = ChangeType.Modified,
                            ParameterChanges = paramChanges
                        });
                    }
                }
            }

            // Detect selection changes (newly selected elements)
            var newlySelected = afterSelection.Except(_beforeSelection);
            foreach (var id in newlySelected)
            {
                // Only add if not already in changes list
                if (!changes.Any(c => c.ElementId == id))
                {
                    if (afterState.TryGetValue(id, out var snapshot))
                    {
                        changes.Add(new ElementChange
                        {
                            ElementId = id,
                            ElementName = snapshot.Name,
                            Category = snapshot.Category,
                            Level = snapshot.Level,
                            ChangeType = ChangeType.Selected
                        });
                    }
                }
            }

            return changes;
        }

        private ElementSnapshot CreateSnapshot(Element element)
        {
            var snapshot = new ElementSnapshot
            {
                ElementId = element.Id.Value,
                Name = GetElementName(element),
                Category = element.Category?.Name ?? "Unknown"
            };

            // Get level
            try
            {
                var levelParam = element.get_Parameter(BuiltInParameter.LEVEL_PARAM)
                               ?? element.get_Parameter(BuiltInParameter.ROOM_LEVEL_ID)
                               ?? element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);

                if (levelParam != null)
                {
                    var levelId = levelParam.AsElementId();
                    if (levelId != null && levelId != ElementId.InvalidElementId)
                    {
                        var level = _doc.GetElement(levelId) as Level;
                        snapshot.Level = level?.Name ?? "";
                    }
                }
            }
            catch { }

            // Capture key parameters
            CaptureParameters(element, snapshot);

            return snapshot;
        }

        private string GetElementName(Element element)
        {
            // Try to get a meaningful name
            try
            {
                // For rooms, use Number + Name
                if (element is Autodesk.Revit.DB.Architecture.Room room)
                {
                    var number = room.Number ?? "";
                    var name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
                    return string.IsNullOrEmpty(number) ? name : $"{number} - {name}";
                }

                // For family instances, use mark or type name
                if (element is FamilyInstance fi)
                {
                    var mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    if (!string.IsNullOrEmpty(mark))
                        return mark;

                    return fi.Symbol?.Name ?? element.Name;
                }

                // Try Mark parameter
                var markParam = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (markParam != null && !string.IsNullOrEmpty(markParam.AsString()))
                    return markParam.AsString();

                return element.Name;
            }
            catch
            {
                return element.Name ?? $"Element {element.Id.Value}";
            }
        }

        private void CaptureParameters(Element element, ElementSnapshot snapshot)
        {
            // Capture commonly modified parameters
            var parametersToCapture = new[]
            {
                BuiltInParameter.ALL_MODEL_MARK,
                BuiltInParameter.ROOM_NAME,
                BuiltInParameter.ROOM_NUMBER,
                BuiltInParameter.ROOM_AREA,
                BuiltInParameter.DOOR_NUMBER,
                BuiltInParameter.WINDOW_TYPE_ID,
                BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
                BuiltInParameter.WALL_BASE_OFFSET,
                BuiltInParameter.WALL_TOP_OFFSET,
                BuiltInParameter.WALL_USER_HEIGHT_PARAM
            };

            foreach (var paramId in parametersToCapture)
            {
                try
                {
                    var param = element.get_Parameter(paramId);
                    if (param != null && param.HasValue)
                    {
                        var value = GetParameterDisplayValue(param);
                        if (!string.IsNullOrEmpty(value))
                        {
                            snapshot.Parameters[param.Definition.Name] = value;
                        }
                    }
                }
                catch { }
            }

            // Also capture any user-visible instance parameters
            try
            {
                foreach (Parameter param in element.Parameters)
                {
                    if (param.IsReadOnly) continue;
                    if (!param.HasValue) continue;
                    if (snapshot.Parameters.ContainsKey(param.Definition.Name)) continue;

                    var value = GetParameterDisplayValue(param);
                    if (!string.IsNullOrEmpty(value) && value != "N/A")
                    {
                        snapshot.Parameters[param.Definition.Name] = value;
                    }
                }
            }
            catch { }
        }

        private string GetParameterDisplayValue(Parameter param)
        {
            if (param == null || !param.HasValue) return null;

            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString();

                case StorageType.Integer:
                    // Check if it's a Yes/No parameter
                    if (param.Definition.GetDataType() == SpecTypeId.Boolean.YesNo)
                        return param.AsInteger() == 1 ? "Yes" : "No";
                    return param.AsInteger().ToString();

                case StorageType.Double:
                    return param.AsValueString() ?? param.AsDouble().ToString("F2");

                case StorageType.ElementId:
                    var id = param.AsElementId();
                    if (id != null && id != ElementId.InvalidElementId)
                    {
                        var elem = _doc.GetElement(id);
                        return elem?.Name ?? id.Value.ToString();
                    }
                    return null;

                default:
                    return null;
            }
        }

        private List<ParameterChange> DetectParameterChanges(ElementSnapshot before, ElementSnapshot after)
        {
            var changes = new List<ParameterChange>();

            // Check all parameters in after state
            foreach (var kvp in after.Parameters)
            {
                if (before.Parameters.TryGetValue(kvp.Key, out var beforeValue))
                {
                    if (beforeValue != kvp.Value)
                    {
                        changes.Add(new ParameterChange
                        {
                            ParameterName = kvp.Key,
                            BeforeValue = beforeValue ?? "(empty)",
                            AfterValue = kvp.Value ?? "(empty)"
                        });
                    }
                }
                else
                {
                    // New parameter value
                    changes.Add(new ParameterChange
                    {
                        ParameterName = kvp.Key,
                        BeforeValue = "(not set)",
                        AfterValue = kvp.Value ?? "(empty)"
                    });
                }
            }

            // Check for removed parameters
            foreach (var kvp in before.Parameters)
            {
                if (!after.Parameters.ContainsKey(kvp.Key))
                {
                    changes.Add(new ParameterChange
                    {
                        ParameterName = kvp.Key,
                        BeforeValue = kvp.Value ?? "(empty)",
                        AfterValue = "(removed)"
                    });
                }
            }

            return changes;
        }
    }

    /// <summary>
    /// Snapshot of an element's state at a point in time
    /// </summary>
    internal class ElementSnapshot
    {
        public long ElementId { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Level { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    }
}
