// Mutators — write-side Revit API implementations for the Step-3 MUTATE
// tools the bina-ai Executor calls through the tunnel.
//
// Each method MUST run on Revit's main thread (callers are inside the
// IExternalEventHandler.Execute). Each MUTATE wraps its work in a
// Transaction (or TransactionGroup for multi-step plans — orchestrator-
// owned, see App.cs / CodeExecutionHandler for the wrapper pattern).
//
// Returns are plain dicts so the JSON serializer doesn't have to
// understand Revit types.
//
// Step-3 scope (10 MUTATE tools — PRD §6.4 top 20 / shortlist):
//   set_parameter, set_parameter_bulk, change_type, delete_elements,
//   duplicate_view, apply_view_template, place_door, place_window,
//   create_wall, create_room

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    internal static class Mutators
    {
        // ─── set_parameter ──────────────────────────────────────────────
        public static Dictionary<string, object?> SetParameter(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id") ?? throw new ArgumentException("missing element_id");
            var paramName = ArgsHelp.GetString(args, "param") ?? ArgsHelp.GetString(args, "parameter")
                ?? throw new ArgumentException("missing param/parameter");
            var value = ArgsHelp.GetValueRaw(args, "value");

            var el = doc.GetElement(new ElementId(id)) ?? throw new ArgumentException($"element {id} not found");
            var p = el.LookupParameter(paramName) ?? throw new ArgumentException($"parameter {paramName} not on element");
            if (p.IsReadOnly) throw new InvalidOperationException($"parameter {paramName} is read-only");

            using var tx = new Transaction(doc, $"BinaVibe: set_parameter {paramName}");
            tx.Start();
            try
            {
                SetParamValue(p, value);
                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                throw;
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["element_id"] = id,
                ["parameter"] = paramName,
                ["new_value"] = SafeParamValue(p),
            };
        }

        // ─── set_parameter_bulk ─────────────────────────────────────────
        public static Dictionary<string, object?> SetParameterBulk(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var paramName = ArgsHelp.GetString(args, "param") ?? ArgsHelp.GetString(args, "parameter")
                ?? throw new ArgumentException("missing param/parameter");
            var value = ArgsHelp.GetValueRaw(args, "value");

            int updated = 0, skippedReadOnly = 0, skippedMissing = 0, skippedGroups = 0;
            var failures = new List<object>();

            using var tx = new Transaction(doc, $"BinaVibe: set_parameter_bulk {paramName}");
            tx.Start();
            try
            {
                foreach (var id in ids)
                {
                    var el = doc.GetElement(new ElementId(id));
                    if (el == null) { skippedMissing++; continue; }
                    if (el.GroupId.Value != ElementId.InvalidElementId.Value) { skippedGroups++; continue; }
                    var p = el.LookupParameter(paramName);
                    if (p == null) { skippedMissing++; continue; }
                    if (p.IsReadOnly) { skippedReadOnly++; continue; }
                    try { SetParamValue(p, value); updated++; }
                    catch (Exception ex) { failures.Add(new { id, error = ex.Message }); }
                }
                tx.Commit();
            }
            catch
            {
                tx.RollBack();
                throw;
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["updated"] = updated,
                ["skipped_groups"] = skippedGroups,
                ["skipped_readonly"] = skippedReadOnly,
                ["skipped_missing"] = skippedMissing,
                ["failures"] = failures,
            };
        }

        // ─── change_type ────────────────────────────────────────────────
        public static Dictionary<string, object?> ChangeType(Document doc, JsonElement args)
        {
            var id = ArgsHelp.GetLong(args, "element_id") ?? throw new ArgumentException("missing element_id");
            var typeName = ArgsHelp.GetString(args, "type_name") ?? throw new ArgumentException("missing type_name");
            var el = doc.GetElement(new ElementId(id)) ?? throw new ArgumentException($"element {id} not found");

            // Find a type with that name in the same category.
            var newType = new FilteredElementCollector(doc).WhereElementIsElementType()
                .OfCategoryId(el.Category.Id)
                .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"type '{typeName}' not found in category {el.Category.Name}");

            using var tx = new Transaction(doc, $"BinaVibe: change_type {typeName}");
            tx.Start();
            try
            {
                el.ChangeTypeId(newType.Id);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["element_id"] = id,
                ["new_type"] = typeName,
            };
        }

        // ─── delete_elements ────────────────────────────────────────────
        public static Dictionary<string, object?> DeleteElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            using var tx = new Transaction(doc, $"BinaVibe: delete_elements ({ids.Count})");
            tx.Start();
            int deleted = 0;
            var failures = new List<object>();
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        var del = doc.Delete(new ElementId(id));
                        deleted += del?.Count ?? 0;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new { id, error = ex.Message });
                    }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["deleted"] = deleted,
                ["failures"] = failures,
            };
        }

        // ─── duplicate_view ─────────────────────────────────────────────
        /// <summary>
        /// args: { source (view name, string), new_name (string), with_detailing (bool) }
        /// Looks up the source view by name, duplicates it, renames the copy to new_name.
        /// (Folds in what DuplicateViewByName did.)
        /// </summary>
        public static Dictionary<string, object?> DuplicateView(Document doc, JsonElement args)
        {
            var sourceName = ArgsHelp.GetString(args, "source") ?? throw new ArgumentException("missing source (view name)");
            var newName = ArgsHelp.GetString(args, "new_name") ?? throw new ArgumentException("missing new_name");
            var withDetailing = ArgsHelp.GetBool(args, "with_detailing") ?? false;

            var src = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"view '{sourceName}' not found");

            using var tx = new Transaction(doc, "BinaVibe: duplicate_view");
            tx.Start();
            try
            {
                var newId = src.Duplicate(withDetailing ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate);
                var newView = doc.GetElement(newId) as View;
                if (newView != null)
                    newView.Name = newName;
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_view_id"] = newId.Value,
                    ["name"] = newView?.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── apply_view_template ────────────────────────────────────────
        /// <summary>
        /// args: { template_name, view_ids?:[long], view_names?:[string] }
        /// Accepts view_ids, view_names, or both. When both absent/empty,
        /// applies to the active view. (Folds in ApplyViewTemplateByName.)
        /// </summary>
        public static Dictionary<string, object?> ApplyViewTemplate(Document doc, JsonElement args)
        {
            var templateName = ArgsHelp.GetString(args, "template_name") ?? throw new ArgumentException("missing template_name");
            var viewIds = ArgsHelp.GetLongList(args, "view_ids");
            var viewNames = ArgsHelp.GetStringList(args, "view_names");

            var template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && string.Equals(v.Name, templateName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"template '{templateName}' not found");

            // Resolve view_names to ids and merge with view_ids.
            foreach (var vn in viewNames)
            {
                var found = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, vn, StringComparison.OrdinalIgnoreCase));
                if (found == null)
                    throw new ArgumentException($"view '{vn}' not found");
                viewIds.Add(found.Id.Value);
            }

            // When both lists are empty, default to active view.
            if (viewIds.Count == 0)
            {
                var av = doc.ActiveView;
                if (av == null) throw new InvalidOperationException("no active view and no view_ids/view_names supplied");
                viewIds = new List<long> { av.Id.Value };
            }

            int applied = 0;
            using var tx = new Transaction(doc, "BinaVibe: apply_view_template");
            tx.Start();
            try
            {
                foreach (var vid in viewIds)
                {
                    var v = doc.GetElement(new ElementId(vid)) as View;
                    if (v == null || v.IsTemplate) continue;
                    v.ViewTemplateId = template.Id;
                    applied++;
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["template"] = templateName,
                ["applied"] = applied,
            };
        }

        // ─── place_door ─────────────────────────────────────────────────
        public static Dictionary<string, object?> PlaceDoor(Document doc, JsonElement args) =>
            PlaceFamilyOnWall(doc, args, BuiltInCategory.OST_Doors, "place_door");

        // ─── place_window ───────────────────────────────────────────────
        public static Dictionary<string, object?> PlaceWindow(Document doc, JsonElement args) =>
            PlaceFamilyOnWall(doc, args, BuiltInCategory.OST_Windows, "place_window");

        // ─── create_wall ────────────────────────────────────────────────
        public static Dictionary<string, object?> CreateWall(Document doc, JsonElement args)
        {
            var p1 = ArgsHelp.GetXyz(args, "start") ?? throw new ArgumentException("missing start [x,y,z]");
            var p2 = ArgsHelp.GetXyz(args, "end") ?? throw new ArgumentException("missing end [x,y,z]");
            var levelName = ArgsHelp.GetString(args, "level") ?? throw new ArgumentException("missing level");
            var typeName = ArgsHelp.GetString(args, "type_name");
            double height = ArgsHelp.GetDouble(args, "height_ft") ?? 10.0;

            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"level '{levelName}' not found");

            WallType? wallType = null;
            if (!string.IsNullOrEmpty(typeName))
            {
                wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                    .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
            }

            using var tx = new Transaction(doc, "BinaVibe: create_wall");
            tx.Start();
            try
            {
                var line = Line.CreateBound(p1, p2);
                var wall = wallType != null
                    ? Wall.Create(doc, line, wallType.Id, level.Id, height, 0, false, false)
                    : Wall.Create(doc, line, level.Id, false);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_id"] = wall.Id.Value,
                    ["level"] = levelName,
                    ["type_name"] = wallType?.Name ?? "<default>",
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── place_family_instance ──────────────────────────────────────
        public static Dictionary<string, object?> PlaceFamilyInstance(Document doc, JsonElement args)
        {
            var familyType = ArgsHelp.GetString(args, "family_type") ?? throw new ArgumentException("missing family_type");
            double x = ArgsHelp.GetDouble(args, "x") ?? throw new ArgumentException("missing x");
            double y = ArgsHelp.GetDouble(args, "y") ?? throw new ArgumentException("missing y");
            double z = ArgsHelp.GetDouble(args, "z") ?? throw new ArgumentException("missing z");
            var levelName = ArgsHelp.GetString(args, "level");

            // Resolve FamilySymbol by name across all loadable family categories.
            FamilySymbol? symbol = null;
            foreach (FamilySymbol fs in new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>())
            {
                if (string.Equals(fs.Name, familyType, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals($"{fs.FamilyName} : {fs.Name}", familyType, StringComparison.OrdinalIgnoreCase))
                {
                    symbol = fs;
                    break;
                }
            }
            if (symbol == null)
                throw new ArgumentException($"family type '{familyType}' not found in document");

            // Resolve optional level.
            Level? level = null;
            if (!string.IsNullOrEmpty(levelName))
            {
                level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"level '{levelName}' not found");
            }

            using var tx = new Transaction(doc, "BinaVibe: place_family_instance");
            tx.Start();
            try
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                var pt = new XYZ(x, y, z);
                FamilyInstance fi;
                if (level != null)
                    fi = doc.Create.NewFamilyInstance(pt, symbol, level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                else
                    fi = doc.Create.NewFamilyInstance(pt, symbol,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_id"] = fi.Id.Value,
                    ["family_type"] = familyType,
                    ["level"] = level?.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── move_elements ──────────────────────────────────────────────
        public static Dictionary<string, object?> MoveElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            double dx = ArgsHelp.GetDouble(args, "dx") ?? throw new ArgumentException("missing dx");
            double dy = ArgsHelp.GetDouble(args, "dy") ?? throw new ArgumentException("missing dy");
            double dz = ArgsHelp.GetDouble(args, "dz") ?? throw new ArgumentException("missing dz");

            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = true, ["moved"] = 0 };

            var elementIds = ids.Select(id => new ElementId(id)).ToList();
            var translation = new XYZ(dx, dy, dz);

            using var tx = new Transaction(doc, $"BinaVibe: move_elements ({ids.Count})");
            tx.Start();
            try
            {
                ElementTransformUtils.MoveElements(doc, elementIds, translation);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["moved"] = ids.Count,
            };
        }

        // ─── create_sheet ───────────────────────────────────────────────
        public static Dictionary<string, object?> CreateSheet(Document doc, JsonElement args)
        {
            var number = ArgsHelp.GetString(args, "number") ?? throw new ArgumentException("missing number");
            var name = ArgsHelp.GetString(args, "name") ?? throw new ArgumentException("missing name");
            var titleblockName = ArgsHelp.GetString(args, "titleblock");

            // Resolve titleblock FamilySymbol — use supplied name, fall back to first available.
            ElementId titleblockId = ElementId.InvalidElementId;
            var titleblocks = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>()
                .ToList();

            if (!string.IsNullOrEmpty(titleblockName))
            {
                var match = titleblocks.FirstOrDefault(t =>
                    string.Equals(t.Name, titleblockName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals($"{t.FamilyName} : {t.Name}", titleblockName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    throw new ArgumentException($"titleblock '{titleblockName}' not found");
                titleblockId = match.Id;
            }
            else if (titleblocks.Count > 0)
            {
                titleblockId = titleblocks[0].Id;
            }

            using var tx = new Transaction(doc, "BinaVibe: create_sheet");
            tx.Start();
            try
            {
                var sheet = ViewSheet.Create(doc, titleblockId);
                sheet.SheetNumber = number;
                sheet.Name = name;
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["sheet_id"] = sheet.Id.Value,
                    ["number"] = sheet.SheetNumber,
                    ["name"] = sheet.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── place_view_on_sheet ────────────────────────────────────────
        public static Dictionary<string, object?> PlaceViewOnSheet(Document doc, JsonElement args)
        {
            var viewName = ArgsHelp.GetString(args, "view_name") ?? throw new ArgumentException("missing view_name");
            var sheetNumber = ArgsHelp.GetString(args, "sheet_number") ?? throw new ArgumentException("missing sheet_number");
            double x = ArgsHelp.GetDouble(args, "x") ?? 0.0;
            double y = ArgsHelp.GetDouble(args, "y") ?? 0.0;

            var view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"view '{viewName}' not found");

            var sheet = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, sheetNumber, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"sheet number '{sheetNumber}' not found");

            using var tx = new Transaction(doc, "BinaVibe: place_view_on_sheet");
            tx.Start();
            try
            {
                var viewport = Viewport.Create(doc, sheet.Id, view.Id, new XYZ(x, y, 0));
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["viewport_id"] = viewport.Id.Value,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── tag_elements ───────────────────────────────────────────────
        public static Dictionary<string, object?> TagElements(Document doc, UIApplication app, JsonElement args)
        {
            var categoryName = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            var viewName = ArgsHelp.GetString(args, "view_name");

            // Resolve target view — named or active.
            View? targetView = null;
            if (!string.IsNullOrEmpty(viewName))
            {
                targetView = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"view '{viewName}' not found");
            }
            else
            {
                targetView = doc.ActiveView ?? throw new InvalidOperationException("no active view and no view_name supplied");
            }

            // Resolve BuiltInCategory by friendly or enum name.
            BuiltInCategory bic = BuiltInCategory.INVALID;
            foreach (BuiltInCategory c in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var cat = Category.GetCategory(doc, c);
                    if (cat == null) continue;
                    if (string.Equals(cat.Name, categoryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.ToString(), categoryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.ToString(), $"OST_{categoryName}", StringComparison.OrdinalIgnoreCase))
                    {
                        bic = c;
                        break;
                    }
                }
                catch { /* some BICs don't have a category in this doc */ }
            }
            if (bic == BuiltInCategory.INVALID)
                throw new ArgumentException($"category '{categoryName}' not recognised");

            // Collect elements of that category visible in the target view.
            var elements = new FilteredElementCollector(doc, targetView.Id)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .ToList();

            int tagged = 0;
            using var tx = new Transaction(doc, $"BinaVibe: tag_elements ({categoryName})");
            tx.Start();
            try
            {
                // Room/Space tags use a different API.
                bool isRoom = bic == BuiltInCategory.OST_Rooms || bic == BuiltInCategory.OST_MEPSpaces;
                foreach (var el in elements)
                {
                    try
                    {
                        if (isRoom && el is SpatialElement spatial)
                        {
                            // Use the midpoint of the room's location as tag origin.
                            var loc = spatial.Location as LocationPoint;
                            if (loc == null) continue;
                            var uv = new UV(loc.Point.X, loc.Point.Y);
                            doc.Create.NewRoomTag(new LinkElementId(el.Id), uv, targetView.Id);
                        }
                        else
                        {
                            var bb = el.get_BoundingBox(targetView);
                            if (bb == null) continue;
                            var mid = (bb.Min + bb.Max) / 2.0;
                            var uv = new UV(mid.X, mid.Y);
                            IndependentTag.Create(doc, targetView.Id, new Reference(el), false,
                                TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal,
                                new XYZ(mid.X, mid.Y, 0));
                        }
                        tagged++;
                    }
                    catch { /* skip elements that cannot be tagged */ }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["tagged"] = tagged,
            };
        }

        // ─── create_level ──────────────────────────────────────────────
        public static Dictionary<string, object?> CreateLevel(Document doc, JsonElement args)
        {
            var name = ArgsHelp.GetString(args, "name") ?? throw new ArgumentException("missing name");
            double elevation = ArgsHelp.GetDouble(args, "elevation") ?? throw new ArgumentException("missing elevation");

            using var tx = new Transaction(doc, "BinaVibe: create_level");
            tx.Start();
            try
            {
                var level = Level.Create(doc, elevation);
                level.Name = name;
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["level_id"] = level.Id.Value,
                    ["name"] = level.Name,
                    ["elevation"] = elevation,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── create_grid ────────────────────────────────────────────────
        public static Dictionary<string, object?> CreateGrid(Document doc, JsonElement args)
        {
            var name = ArgsHelp.GetString(args, "name") ?? throw new ArgumentException("missing name");
            double startX = ArgsHelp.GetDouble(args, "start_x") ?? throw new ArgumentException("missing start_x");
            double startY = ArgsHelp.GetDouble(args, "start_y") ?? throw new ArgumentException("missing start_y");
            double endX = ArgsHelp.GetDouble(args, "end_x") ?? throw new ArgumentException("missing end_x");
            double endY = ArgsHelp.GetDouble(args, "end_y") ?? throw new ArgumentException("missing end_y");

            var line = Line.CreateBound(new XYZ(startX, startY, 0), new XYZ(endX, endY, 0));

            using var tx = new Transaction(doc, "BinaVibe: create_grid");
            tx.Start();
            try
            {
                var grid = Grid.Create(doc, line);
                grid.Name = name;
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["grid_id"] = grid.Id.Value,
                    ["name"] = grid.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── create_room (x,y signature) ────────────────────────────────
        public static Dictionary<string, object?> CreateRoomXY(Document doc, JsonElement args)
        {
            var levelName = ArgsHelp.GetString(args, "level") ?? throw new ArgumentException("missing level");
            double x = ArgsHelp.GetDouble(args, "x") ?? throw new ArgumentException("missing x");
            double y = ArgsHelp.GetDouble(args, "y") ?? throw new ArgumentException("missing y");
            var name = ArgsHelp.GetString(args, "name");

            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"level '{levelName}' not found");

            using var tx = new Transaction(doc, "BinaVibe: create_room");
            tx.Start();
            try
            {
                var room = doc.Create.NewRoom(level, new UV(x, y));
                if (!string.IsNullOrEmpty(name))
                {
                    var p1 = room.LookupParameter("Name");
                    if (p1 != null && !p1.IsReadOnly) p1.Set(name);
                }
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["room_id"] = room.Id.Value,
                    ["level"] = levelName,
                    ["name"] = name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── color_elements ─────────────────────────────────────────────
        public static Dictionary<string, object?> ColorElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            int r = (int)(ArgsHelp.GetDouble(args, "r") ?? throw new ArgumentException("missing r"));
            int g = (int)(ArgsHelp.GetDouble(args, "g") ?? throw new ArgumentException("missing g"));
            int b = (int)(ArgsHelp.GetDouble(args, "b") ?? throw new ArgumentException("missing b"));
            var viewName = ArgsHelp.GetString(args, "view_name");

            // Resolve target view — named or active.
            View targetView;
            if (!string.IsNullOrEmpty(viewName))
            {
                targetView = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"view '{viewName}' not found");
            }
            else
            {
                targetView = doc.ActiveView ?? throw new InvalidOperationException("no active view and no view_name supplied");
            }

            var color = new Color((byte)r, (byte)g, (byte)b);
            var ogs = new OverrideGraphicSettings();
            ogs.SetSurfaceForegroundPatternColor(color);
            ogs.SetProjectionLineColor(color);

            int colored = 0;
            using var tx = new Transaction(doc, $"BinaVibe: color_elements ({ids.Count})");
            tx.Start();
            try
            {
                foreach (var id in ids)
                {
                    var eid = new ElementId(id);
                    targetView.SetElementOverrides(eid, ogs);
                    colored++;
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["colored"] = colored,
            };
        }

        // ─── swap_element_type ──────────────────────────────────────────
        public static Dictionary<string, object?> SwapElementType(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var newTypeName = ArgsHelp.GetString(args, "type_name") ?? throw new ArgumentException("missing type_name");

            // Resolve the target ElementType / FamilySymbol by name.
            var newType = new FilteredElementCollector(doc).WhereElementIsElementType()
                .FirstOrDefault(t => string.Equals(t.Name, newTypeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"type '{newTypeName}' not found in document");

            // Activate FamilySymbol if needed.
            if (newType is FamilySymbol fs && !fs.IsActive)
            {
                using var txActivate = new Transaction(doc, "BinaVibe: activate_symbol");
                txActivate.Start();
                fs.Activate();
                doc.Regenerate();
                txActivate.Commit();
            }

            int swapped = 0;
            var failures = new List<object>();

            using var tx = new Transaction(doc, $"BinaVibe: swap_element_type ({ids.Count})");
            tx.Start();
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        var el = doc.GetElement(new ElementId(id));
                        if (el == null) continue;
                        el.ChangeTypeId(newType.Id);
                        swapped++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new { id, error = ex.Message });
                    }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["swapped"] = swapped,
                ["new_type"] = newTypeName,
                ["failures"] = failures,
            };
        }

        // ─── place_text_note ────────────────────────────────────────────
        public static Dictionary<string, object?> PlaceTextNote(Document doc, JsonElement args)
        {
            var viewName = ArgsHelp.GetString(args, "view_name") ?? throw new ArgumentException("missing view_name");
            double x = ArgsHelp.GetDouble(args, "x") ?? throw new ArgumentException("missing x");
            double y = ArgsHelp.GetDouble(args, "y") ?? throw new ArgumentException("missing y");
            var text = ArgsHelp.GetString(args, "text") ?? throw new ArgumentException("missing text");

            var view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"view '{viewName}' not found");

            // Resolve the default TextNoteType.
            var textNoteTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (textNoteTypeId == ElementId.InvalidElementId)
            {
                var textNoteType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType))
                    .FirstOrDefault();
                if (textNoteType == null)
                    throw new InvalidOperationException("no TextNoteType found in document");
                textNoteTypeId = textNoteType.Id;
            }

            using var tx = new Transaction(doc, "BinaVibe: place_text_note");
            tx.Start();
            try
            {
                var note = TextNote.Create(doc, view.Id, new XYZ(x, y, 0), text, textNoteTypeId);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["note_id"] = note.Id.Value,
                    ["view_name"] = viewName,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── hide_isolate_elements ───────────────────────────────────────
        public static Dictionary<string, object?> HideIsolateElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var mode = ArgsHelp.GetString(args, "mode") ?? "hide";
            var viewName = ArgsHelp.GetString(args, "view_name");

            // Resolve target view — named or active.
            View targetView;
            if (!string.IsNullOrEmpty(viewName))
            {
                targetView = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"view '{viewName}' not found");
            }
            else
            {
                targetView = doc.ActiveView ?? throw new InvalidOperationException("no active view and no view_name supplied");
            }

            var elementIds = ids.Select(id => new ElementId(id)).ToList();

            using var tx = new Transaction(doc, $"BinaVibe: hide_isolate_elements ({mode}, {ids.Count})");
            tx.Start();
            try
            {
                if (string.Equals(mode, "isolate", StringComparison.OrdinalIgnoreCase))
                    targetView.IsolateElementsTemporary(elementIds);
                else
                    targetView.HideElements(elementIds);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["affected"] = ids.Count,
                ["mode"] = mode,
            };
        }

        // ─── shared place-family-on-wall helper ─────────────────────────
        private static Dictionary<string, object?> PlaceFamilyOnWall(
            Document doc, JsonElement args, BuiltInCategory cat, string label)
        {
            var hostId = ArgsHelp.GetLong(args, "host_wall_id") ?? throw new ArgumentException("missing host_wall_id");
            var typeName = ArgsHelp.GetString(args, "type_name") ?? throw new ArgumentException("missing type_name");
            var loc = ArgsHelp.GetXyz(args, "location") ?? throw new ArgumentException("missing location [x,y,z]");

            var host = doc.GetElement(new ElementId(hostId)) as Wall
                ?? throw new ArgumentException($"host wall {hostId} not found");

            var symbol = new FilteredElementCollector(doc).WhereElementIsElementType()
                .OfCategory(cat).Cast<FamilySymbol>()
                .FirstOrDefault(s => string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"type '{typeName}' not found in category {cat}");

            using var tx = new Transaction(doc, $"BinaVibe: {label}");
            tx.Start();
            try
            {
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                var hostLevel = doc.GetElement(host.LevelId) as Level
                    ?? throw new InvalidOperationException("host wall has no level");
                var fi = doc.Create.NewFamilyInstance(loc, symbol, host, hostLevel,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_id"] = fi.Id.Value,
                    ["host_wall_id"] = hostId,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── value helpers ──────────────────────────────────────────────

        private static void SetParamValue(Parameter p, object? value)
        {
            if (value == null) { p.Set(""); return; }
            switch (p.StorageType)
            {
                case StorageType.String: p.Set(value.ToString() ?? ""); break;
                case StorageType.Integer:
                    if (int.TryParse(value.ToString(), out var i)) p.Set(i);
                    else throw new ArgumentException($"value '{value}' is not Integer");
                    break;
                case StorageType.Double:
                    if (double.TryParse(value.ToString(), out var d)) p.Set(d);
                    else throw new ArgumentException($"value '{value}' is not Double");
                    break;
                case StorageType.ElementId:
                    if (long.TryParse(value.ToString(), out var eid)) p.Set(new ElementId(eid));
                    else throw new ArgumentException($"value '{value}' is not ElementId");
                    break;
                default: throw new NotSupportedException($"unsupported StorageType {p.StorageType}");
            }
        }

        private static object? SafeParamValue(Parameter p)
        {
            try
            {
                return p.StorageType switch
                {
                    StorageType.String => p.AsString(),
                    StorageType.Integer => p.AsInteger(),
                    StorageType.Double => p.AsDouble(),
                    StorageType.ElementId => p.AsElementId().Value,
                    _ => p.AsValueString(),
                };
            }
            catch { return null; }
        }
    }

    // ─── ArgsHelp — shared JSON arg extraction ──────────────────────────

    internal static class ArgsHelp
    {
        public static string? GetString(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        public static long? GetLong(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
            return null;
        }

        public static double? GetDouble(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var s)) return s;
            return null;
        }

        public static bool? GetBool(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            return null;
        }

        public static List<string> GetStringList(JsonElement el, string name)
        {
            var items = new List<string>();
            if (el.ValueKind != JsonValueKind.Object) return items;
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return items;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (s != null) items.Add(s);
                }
            }
            return items;
        }

        public static List<long> GetLongList(JsonElement el, string name)
        {
            var ids = new List<long>();
            if (el.ValueKind != JsonValueKind.Object) return ids;
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return ids;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var n)) ids.Add(n);
                else if (item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), out var s)) ids.Add(s);
            }
            return ids;
        }

        public static XYZ? GetXyz(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var nums = new List<double>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d)) nums.Add(d);
            }
            return nums.Count >= 3 ? new XYZ(nums[0], nums[1], nums[2]) : null;
        }

        public static object? GetValueRaw(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var n) ? (object)n : v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => v.GetRawText(),
            };
        }
    }
}
