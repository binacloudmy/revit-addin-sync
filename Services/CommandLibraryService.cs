using Newtonsoft.Json;
using RevitWebAppSync.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Service for managing saved commands library
    /// </summary>
    public class CommandLibraryService
    {
        private readonly string _libraryPath;
        private CommandLibrary _library;

        public CommandLibraryService()
        {
            // Store in AppData/Roaming/RevitAI/commands.json
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var revitAiFolder = Path.Combine(appDataPath, "RevitAI");

            if (!Directory.Exists(revitAiFolder))
            {
                Directory.CreateDirectory(revitAiFolder);
            }

            _libraryPath = Path.Combine(revitAiFolder, "commands.json");
            LoadLibrary();
        }

        /// <summary>
        /// Get all saved commands
        /// </summary>
        public List<SavedCommand> GetAllCommands()
        {
            return _library.Commands
                .OrderByDescending(c => c.IsBuiltIn)
                .ThenByDescending(c => c.UseCount)
                .ThenByDescending(c => c.LastUsedAt)
                .ToList();
        }

        /// <summary>
        /// Get commands by category
        /// </summary>
        public List<SavedCommand> GetCommandsByCategory(string category)
        {
            return _library.Commands
                .Where(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.UseCount)
                .ToList();
        }

        /// <summary>
        /// Search commands by name or description
        /// </summary>
        public List<SavedCommand> SearchCommands(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return GetAllCommands();

            return _library.Commands
                .Where(c => c.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                           (c.Description?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                           (c.Prompt?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                .OrderByDescending(c => c.UseCount)
                .ToList();
        }

        /// <summary>
        /// Get all unique categories
        /// </summary>
        public List<string> GetCategories()
        {
            return _library.Commands
                .Select(c => c.Category)
                .Distinct()
                .OrderBy(c => c)
                .ToList();
        }

        /// <summary>
        /// Save a new command
        /// </summary>
        public SavedCommand SaveCommand(string name, string prompt, string code, string description, string category = "General", string icon = "⚡")
        {
            var command = new SavedCommand
            {
                Name = name,
                Prompt = prompt,
                Code = code,
                Description = description,
                Category = category,
                Icon = icon,
                CreatedAt = DateTime.Now
            };

            _library.Commands.Add(command);
            SaveLibrary();

            return command;
        }

        /// <summary>
        /// Update command usage statistics
        /// </summary>
        public void RecordUsage(string commandId)
        {
            var command = _library.Commands.FirstOrDefault(c => c.Id == commandId);
            if (command != null)
            {
                command.UseCount++;
                command.LastUsedAt = DateTime.Now;
                SaveLibrary();
            }
        }

        /// <summary>
        /// Delete a command (only non-built-in)
        /// </summary>
        public bool DeleteCommand(string commandId)
        {
            var command = _library.Commands.FirstOrDefault(c => c.Id == commandId);
            if (command != null && !command.IsBuiltIn)
            {
                _library.Commands.Remove(command);
                SaveLibrary();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Load library from disk
        /// </summary>
        private void LoadLibrary()
        {
            try
            {
                if (File.Exists(_libraryPath))
                {
                    var json = File.ReadAllText(_libraryPath);
                    _library = JsonConvert.DeserializeObject<CommandLibrary>(json) ?? new CommandLibrary();
                }
                else
                {
                    _library = new CommandLibrary();
                    InitializeBuiltInCommands();
                    SaveLibrary();
                }
            }
            catch
            {
                _library = new CommandLibrary();
                InitializeBuiltInCommands();
            }

            // Ensure built-in commands exist
            EnsureBuiltInCommands();
        }

        /// <summary>
        /// Save library to disk
        /// </summary>
        private void SaveLibrary()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_library, Formatting.Indented);
                File.WriteAllText(_libraryPath, json);
            }
            catch
            {
                // Ignore save errors
            }
        }

        /// <summary>
        /// Initialize built-in commands
        /// </summary>
        private void InitializeBuiltInCommands()
        {
            _library.Commands.AddRange(GetBuiltInCommands());
        }

        /// <summary>
        /// Ensure all built-in commands exist
        /// </summary>
        private void EnsureBuiltInCommands()
        {
            var builtInCommands = GetBuiltInCommands();
            foreach (var builtIn in builtInCommands)
            {
                if (!_library.Commands.Any(c => c.IsBuiltIn && c.Name == builtIn.Name))
                {
                    _library.Commands.Add(builtIn);
                }
            }
        }

        /// <summary>
        /// Get list of built-in commands
        /// </summary>
        private List<SavedCommand> GetBuiltInCommands()
        {
            return new List<SavedCommand>
            {
                // Export Commands
                new SavedCommand
                {
                    Name = "Export Rooms to CSV",
                    Prompt = "Export all rooms with their names, numbers, areas, and levels to a CSV file on the desktop",
                    Code = @"var rooms = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Rooms)
    .WhereElementIsNotElementType()
    .Cast<Autodesk.Revit.DB.Architecture.Room>()
    .Where(r => r.Area > 0);

var sb = new StringBuilder();
sb.AppendLine(""Number,Name,Area (sqm),Level"");

foreach (var room in rooms)
{
    var number = room.Number ?? """";
    var name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? """";
    var area = (room.Area * 0.092903).ToString(""F2"");
    var level = room.Level?.Name ?? """";
    sb.AppendLine($""{number},{name},{area},{level}"");
}

var path = Path.Combine(DesktopPath, ""Rooms_Export.csv"");
File.WriteAllText(path, sb.ToString());
ShowMessage(""Export Complete"", $""Exported {rooms.Count()} rooms to:\\n{path}"");",
                    Description = "Exports all rooms with area > 0 to a CSV file on desktop",
                    Category = "Export",
                    Icon = "📊",
                    IsBuiltIn = true
                },

                new SavedCommand
                {
                    Name = "Export Doors to CSV",
                    Prompt = "Export all doors with their mark, type, level, and host wall to CSV",
                    Code = @"var doors = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Doors)
    .WhereElementIsNotElementType()
    .Cast<FamilyInstance>();

var sb = new StringBuilder();
sb.AppendLine(""Mark,Type,Level,Host Wall"");

foreach (var door in doors)
{
    var mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? """";
    var typeName = door.Symbol?.Name ?? """";
    var level = door.LevelId != ElementId.InvalidElementId ? doc.GetElement(door.LevelId)?.Name ?? """" : """";
    var host = door.Host?.Name ?? """";
    sb.AppendLine($""{mark},{typeName},{level},{host}"");
}

var path = Path.Combine(DesktopPath, ""Doors_Export.csv"");
File.WriteAllText(path, sb.ToString());
ShowMessage(""Export Complete"", $""Exported {doors.Count()} doors to:\\n{path}"");",
                    Description = "Exports all doors to a CSV file on desktop",
                    Category = "Export",
                    Icon = "🚪",
                    IsBuiltIn = true
                },

                // Selection Commands
                new SavedCommand
                {
                    Name = "Select All Doors",
                    Prompt = "Select all doors in the model",
                    Code = @"var doorIds = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Doors)
    .WhereElementIsNotElementType()
    .ToElementIds();

uidoc.Selection.SetElementIds(doorIds);
ShowMessage(""Selection"", $""Selected {doorIds.Count} doors"");",
                    Description = "Selects all door instances in the model",
                    Category = "Selection",
                    Icon = "🚪",
                    IsBuiltIn = true
                },

                new SavedCommand
                {
                    Name = "Select Untagged Doors",
                    Prompt = "Select all doors that don't have a door tag in the current view",
                    Code = @"var currentView = activeView;

var doors = new FilteredElementCollector(doc, currentView.Id)
    .OfCategory(BuiltInCategory.OST_Doors)
    .WhereElementIsNotElementType()
    .ToElementIds()
    .ToHashSet();

var taggedDoorIds = new FilteredElementCollector(doc, currentView.Id)
    .OfCategory(BuiltInCategory.OST_DoorTags)
    .WhereElementIsNotElementType()
    .Cast<IndependentTag>()
    .Select(t => t.TaggedLocalElementId)
    .ToHashSet();

var untaggedDoors = doors.Where(id => !taggedDoorIds.Contains(id)).ToList();
uidoc.Selection.SetElementIds(untaggedDoors);
ShowMessage(""Untagged Doors"", $""Found {untaggedDoors.Count} untagged doors in current view"");",
                    Description = "Finds doors without tags in the active view",
                    Category = "Selection",
                    Icon = "🏷️",
                    IsBuiltIn = true
                },

                new SavedCommand
                {
                    Name = "Select Short Walls",
                    Prompt = "Select all walls shorter than 1 meter",
                    Code = @"var shortWalls = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Walls)
    .WhereElementIsNotElementType()
    .Cast<Wall>()
    .Where(w => {
        var curve = (w.Location as LocationCurve)?.Curve;
        return curve != null && curve.Length * 0.3048 < 1.0;
    })
    .Select(w => w.Id)
    .ToList();

uidoc.Selection.SetElementIds(shortWalls);
ShowMessage(""Short Walls"", $""Selected {shortWalls.Count} walls shorter than 1m"");",
                    Description = "Selects walls less than 1 meter in length",
                    Category = "Selection",
                    Icon = "📏",
                    IsBuiltIn = true
                },

                // Audit Commands
                new SavedCommand
                {
                    Name = "Audit Room Areas",
                    Prompt = "Find all rooms with zero area or that are unenclosed",
                    Code = @"var problemRooms = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Rooms)
    .WhereElementIsNotElementType()
    .Cast<Autodesk.Revit.DB.Architecture.Room>()
    .Where(r => r.Area == 0)
    .ToList();

if (problemRooms.Count > 0)
{
    var ids = problemRooms.Select(r => r.Id).ToList();
    uidoc.Selection.SetElementIds(ids);

    var names = string.Join(""\\n"", problemRooms.Take(10).Select(r => $""- {r.Number}: {r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()}""));
    if (problemRooms.Count > 10) names += $""\\n...and {problemRooms.Count - 10} more"";

    ShowMessage(""Room Audit"", $""Found {problemRooms.Count} rooms with zero area:\\n{names}"");
}
else
{
    ShowMessage(""Room Audit"", ""All rooms have valid areas."");
}",
                    Description = "Finds unenclosed or problematic rooms",
                    Category = "Audit",
                    Icon = "🔍",
                    IsBuiltIn = true
                },

                new SavedCommand
                {
                    Name = "Count Elements by Category",
                    Prompt = "Count all doors, windows, rooms, and walls in the model",
                    Code = @"var counts = new Dictionary<string, int>
{
    {""Walls"", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().GetElementCount()},
    {""Doors"", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().GetElementCount()},
    {""Windows"", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().GetElementCount()},
    {""Rooms"", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().GetElementCount()},
    {""Floors"", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType().GetElementCount()},
    {""Ceilings"", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Ceilings).WhereElementIsNotElementType().GetElementCount()},
    {""Furniture"", new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Furniture).WhereElementIsNotElementType().GetElementCount()}
};

var summary = string.Join(""\\n"", counts.Select(kvp => $""{kvp.Key}: {kvp.Value}""));
var total = counts.Values.Sum();
ShowMessage(""Element Count"", $""{summary}\\n\\nTotal: {total} elements"");",
                    Description = "Shows count of common element categories",
                    Category = "Audit",
                    Icon = "🔢",
                    IsBuiltIn = true
                },

                // Delete Commands
                new SavedCommand
                {
                    Name = "Delete All Doors",
                    Prompt = "Delete all doors in the model",
                    Code = @"var doorIds = new FilteredElementCollector(doc)
    .OfCategory(BuiltInCategory.OST_Doors)
    .WhereElementIsNotElementType()
    .ToElementIds();

var count = doorIds.Count;
doc.Delete(doorIds);
ShowMessage(""Delete Complete"", $""Deleted {count} doors"");",
                    Description = "Deletes all door instances",
                    Category = "Modification",
                    Icon = "🗑️",
                    IsBuiltIn = true
                },

                new SavedCommand
                {
                    Name = "Delete Selected Elements",
                    Prompt = "Delete currently selected elements",
                    Code = @"var selectedIds = uidoc.Selection.GetElementIds();
if (selectedIds.Count == 0)
{
    ShowMessage(""Delete"", ""No elements selected"");
}
else
{
    var count = selectedIds.Count;
    doc.Delete(selectedIds);
    ShowMessage(""Delete Complete"", $""Deleted {count} elements"");
}",
                    Description = "Deletes whatever is currently selected",
                    Category = "Modification",
                    Icon = "🗑️",
                    IsBuiltIn = true
                },

                // View Commands
                new SavedCommand
                {
                    Name = "Create 3D Views for Rooms",
                    Prompt = "For each room in the current view, create a 3D view with section box and 50mm margin",
                    Code = @"var viewFamilyType = new FilteredElementCollector(doc)
    .OfClass(typeof(ViewFamilyType))
    .Cast<ViewFamilyType>()
    .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);

if (viewFamilyType == null)
{
    ShowMessage(""Error"", ""No 3D view type found in the project"");
}
else
{
    var rooms = new FilteredElementCollector(doc, activeView.Id)
        .OfCategory(BuiltInCategory.OST_Rooms)
        .WhereElementIsNotElementType()
        .Cast<Autodesk.Revit.DB.Architecture.Room>()
        .Where(r => r.Area > 0)
        .ToList();

    double marginFeet = 50 / 304.8;
    int count = 0;
    var createdViews = new List<string>();
    View3D lastView = null;

    foreach (var room in rooms)
    {
        var bb = room.get_BoundingBox(null);
        if (bb == null) continue;

        var view3D = View3D.CreateIsometric(doc, viewFamilyType.Id);
        string roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? ""Room"";
        string roomNumber = room.Number ?? """";

        try
        {
            view3D.Name = $""3D - {roomName} ({roomNumber})"";
        }
        catch
        {
            view3D.Name = $""3D - {roomName} ({roomNumber}) - {count}"";
        }

        var sectionBox = new BoundingBoxXYZ();
        sectionBox.Min = new XYZ(bb.Min.X - marginFeet, bb.Min.Y - marginFeet, bb.Min.Z - marginFeet);
        sectionBox.Max = new XYZ(bb.Max.X + marginFeet, bb.Max.Y + marginFeet, bb.Max.Z + marginFeet);

        view3D.SetSectionBox(sectionBox);
        count++;
        createdViews.Add(view3D.Name);
        lastView = view3D;
    }

    // Open the last created view (deferred, after transaction)
    if (lastView != null)
    {
        OpenView(lastView);
    }

    var viewList = string.Join(""\\n"", createdViews.Take(10));
    if (createdViews.Count > 10) viewList += $""\\n...and {createdViews.Count - 10} more"";
    ShowMessage(""3D Views Created"", $""Created {count} 3D views with section boxes:\\n{viewList}"");
}",
                    Description = "Creates individual 3D views for each room with section box boundaries",
                    Category = "View",
                    Icon = "🎯",
                    IsBuiltIn = true
                },

                new SavedCommand
                {
                    Name = "Create 3D View for Selection",
                    Prompt = "Create a 3D view with section box around the selected elements",
                    Code = @"var selectedIds = uidoc.Selection.GetElementIds();
if (selectedIds.Count == 0)
{
    ShowMessage(""Error"", ""Please select some elements first"");
}
else
{
    var viewFamilyType = new FilteredElementCollector(doc)
        .OfClass(typeof(ViewFamilyType))
        .Cast<ViewFamilyType>()
        .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);

    if (viewFamilyType == null)
    {
        ShowMessage(""Error"", ""No 3D view type found"");
    }
    else
    {
        BoundingBoxXYZ combinedBox = null;
        foreach (var id in selectedIds)
        {
            var elem = doc.GetElement(id);
            var bb = elem.get_BoundingBox(null);
            if (bb == null) continue;

            if (combinedBox == null)
            {
                combinedBox = new BoundingBoxXYZ();
                combinedBox.Min = bb.Min;
                combinedBox.Max = bb.Max;
            }
            else
            {
                combinedBox.Min = new XYZ(
                    Math.Min(combinedBox.Min.X, bb.Min.X),
                    Math.Min(combinedBox.Min.Y, bb.Min.Y),
                    Math.Min(combinedBox.Min.Z, bb.Min.Z));
                combinedBox.Max = new XYZ(
                    Math.Max(combinedBox.Max.X, bb.Max.X),
                    Math.Max(combinedBox.Max.Y, bb.Max.Y),
                    Math.Max(combinedBox.Max.Z, bb.Max.Z));
            }
        }

        if (combinedBox != null)
        {
            double margin = 1.0;
            var view3D = View3D.CreateIsometric(doc, viewFamilyType.Id);
            view3D.Name = $""3D - Selection ({selectedIds.Count} elements)"";

            var sectionBox = new BoundingBoxXYZ();
            sectionBox.Min = new XYZ(combinedBox.Min.X - margin, combinedBox.Min.Y - margin, combinedBox.Min.Z - margin);
            sectionBox.Max = new XYZ(combinedBox.Max.X + margin, combinedBox.Max.Y + margin, combinedBox.Max.Z + margin);
            view3D.SetSectionBox(sectionBox);

            OpenView(view3D);
            ShowMessage(""Success"", $""Created 3D view with section box for {selectedIds.Count} elements"");
        }
    }
}",
                    Description = "Creates a 3D view with section box around selected elements",
                    Category = "View",
                    Icon = "📦",
                    IsBuiltIn = true
                }
            };
        }
    }
}
