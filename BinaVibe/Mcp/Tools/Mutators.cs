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
                // Probe: tx.Commit() triggers the regen. Isolates the regen
                // cost from the Wall.Create call. Big commit time => (B) regen tax.
                var _swCommit = System.Diagnostics.Stopwatch.StartNew();
                tx.Commit();
                _swCommit.Stop();
                System.Diagnostics.Debug.WriteLine(
                    $"[BinaVibe][timing] create_wall commit+regen={_swCommit.ElapsedMilliseconds}ms");
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

        // ─── rotate_elements ────────────────────────────────────────────
        /// <summary>
        /// args: { element_ids:[long], angle_deg:double, axis_x?:double, axis_y?:double }
        /// Rotates elements about a vertical (Z-up) axis through (axis_x, axis_y) by angle_deg degrees.
        /// Uses ElementTransformUtils.RotateElements (Revit 2015+).
        /// </summary>
        public static Dictionary<string, object?> RotateElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            double angleDeg = ArgsHelp.GetDouble(args, "angle_deg") ?? throw new ArgumentException("missing angle_deg");
            double axisX = ArgsHelp.GetDouble(args, "axis_x") ?? 0.0;
            double axisY = ArgsHelp.GetDouble(args, "axis_y") ?? 0.0;

            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = true, ["rotated"] = 0 };

            var elementIds = ids.Select(id => new ElementId(id)).ToList();
            double angleRad = angleDeg * Math.PI / 180.0;

            // Vertical axis through (axisX, axisY): two points along Z.
            var axisLine = Line.CreateBound(
                new XYZ(axisX, axisY, 0),
                new XYZ(axisX, axisY, 10));

            using var tx = new Transaction(doc, $"BinaVibe: rotate_elements ({ids.Count}, {angleDeg}°)");
            tx.Start();
            try
            {
                ElementTransformUtils.RotateElements(doc, elementIds, axisLine, angleRad);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["rotated"] = ids.Count,
            };
        }

        // ─── copy_elements ──────────────────────────────────────────────
        /// <summary>
        /// args: { element_ids:[long], dx:double, dy:double, dz:double }
        /// Copies elements by translation (dx,dy,dz in feet).
        /// Returns created_ids: the new element ids from the copy operation.
        /// Uses ElementTransformUtils.CopyElements (Revit 2015+).
        /// </summary>
        public static Dictionary<string, object?> CopyElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            double dx = ArgsHelp.GetDouble(args, "dx") ?? throw new ArgumentException("missing dx");
            double dy = ArgsHelp.GetDouble(args, "dy") ?? throw new ArgumentException("missing dy");
            double dz = ArgsHelp.GetDouble(args, "dz") ?? throw new ArgumentException("missing dz");

            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = true, ["created_ids"] = new List<object>() };

            var elementIds = ids.Select(id => new ElementId(id)).ToList();
            var translation = new XYZ(dx, dy, dz);

            using var tx = new Transaction(doc, $"BinaVibe: copy_elements ({ids.Count})");
            tx.Start();
            ICollection<ElementId> newIds;
            try
            {
                newIds = ElementTransformUtils.CopyElements(doc, elementIds, translation);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            var createdIds = newIds.Select(eid => (object)eid.Value).ToList<object>();
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["created_ids"] = createdIds,
            };
        }

        // ─── mirror_elements ─────────────────────────────────────────────
        /// <summary>
        /// args: { element_ids:[long], plane:"x"|"y", origin_x?:double, origin_y?:double, copy?:bool }
        /// Mirrors elements across a vertical plane.
        ///   plane="x" → mirror plane normal along X at x=origin_x  (the YZ plane shifted to origin_x).
        ///   plane="y" → mirror plane normal along Y at y=origin_y  (the XZ plane shifted to origin_y).
        /// copy=true (default) keeps originals; copy=false moves them.
        /// Uses ElementTransformUtils.MirrorElements (Revit 2015+).
        /// </summary>
        public static Dictionary<string, object?> MirrorElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var planeName = ArgsHelp.GetString(args, "plane") ?? "x";
            double originX = ArgsHelp.GetDouble(args, "origin_x") ?? 0.0;
            double originY = ArgsHelp.GetDouble(args, "origin_y") ?? 0.0;
            bool copy = ArgsHelp.GetBool(args, "copy") ?? true;

            if (ids.Count == 0)
                return new Dictionary<string, object?> { ["ok"] = true, ["mirrored"] = 0 };

            var elementIds = ids.Select(id => new ElementId(id)).ToList();

            // Build the mirror plane (vertical, Z is the out-of-plane axis).
            Plane mirrorPlane;
            if (planeName.Equals("y", StringComparison.OrdinalIgnoreCase))
                mirrorPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(0, originY, 0));
            else
                mirrorPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisX, new XYZ(originX, 0, 0));

            using var tx = new Transaction(doc, $"BinaVibe: mirror_elements ({ids.Count}, plane={planeName})");
            tx.Start();
            try
            {
                ElementTransformUtils.MirrorElements(doc, elementIds, mirrorPlane, copy);
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["mirrored"] = ids.Count,
            };
        }

        // ─── export_views ────────────────────────────────────────────────
        /// <summary>
        /// args: { view_names:[string], fmt:"pdf"|"dwg", folder?:string }
        /// Exports named views to PDF (Revit 2022+ Document.Export overload with PDFExportOptions)
        /// or DWG (Document.Export with DWGExportOptions). folder defaults to a temp path.
        /// Export methods may not need a Transaction (they are read-only/I-O), but we guard
        /// with try/catch only. Returns files:[] and folder.
        ///
        /// NOTE: PDFExportOptions and the matching Document.Export overload require Revit 2022+.
        /// On older Revit versions this method will throw NotSupportedException at runtime.
        /// DWGExportOptions is available from Revit 2015+.
        /// </summary>
        public static Dictionary<string, object?> ExportViews(Document doc, JsonElement args)
        {
            var viewNames = ArgsHelp.GetStringList(args, "view_names");
            var fmt = ArgsHelp.GetString(args, "fmt") ?? "pdf";
            var folder = ArgsHelp.GetString(args, "folder");

            // Default folder: system temp / BinaVibe_exports.
            if (string.IsNullOrEmpty(folder))
                folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BinaVibe_exports");

            System.IO.Directory.CreateDirectory(folder);

            // Resolve views by name → ElementIds.
            var viewIdList = new List<ElementId>();
            var notFound = new List<string>();
            foreach (var vn in viewNames)
            {
                var v = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(x => !x.IsTemplate && string.Equals(x.Name, vn, StringComparison.OrdinalIgnoreCase));
                if (v == null) { notFound.Add(vn); continue; }
                viewIdList.Add(v.Id);
            }
            if (viewIdList.Count == 0)
                throw new ArgumentException(
                    $"none of the supplied view names were found: {string.Join(", ", viewNames)}");

            var exportedFiles = new List<object>();

            if (fmt.Equals("dwg", StringComparison.OrdinalIgnoreCase))
            {
                // DWG: export each view individually so we control the file name.
                var dwgOpts = new DWGExportOptions();
                foreach (var viewId in viewIdList)
                {
                    var v = doc.GetElement(viewId) as View;
                    if (v == null) continue;
                    var baseName = SanitizeFileName(v.Name);
                    var single = new List<ElementId> { viewId };
                    try
                    {
                        doc.Export(folder, baseName, single, dwgOpts);
                        exportedFiles.Add((object)System.IO.Path.Combine(folder, baseName + ".dwg"));
                    }
                    catch (Exception ex)
                    {
                        exportedFiles.Add((object)$"ERROR:{v.Name}:{ex.Message}");
                    }
                }
            }
            else
            {
                // PDF: batch export all views in one call (Revit 2022+).
                // PDFExportOptions — available in Revit API 2022+. Compile-time reference
                // is fine if the project targets Revit 2022+ assemblies; throws at runtime
                // on older installs.
                try
                {
                    var pdfOpts = new PDFExportOptions();
                    pdfOpts.FileName = "BinaVibe_export";
                    pdfOpts.Combine = false; // one file per view
                    doc.Export(folder, viewIdList, pdfOpts);
                    // Revit names files as <ViewName>.pdf — report all pdf files created after export.
                    foreach (var viewId in viewIdList)
                    {
                        var v = doc.GetElement(viewId) as View;
                        if (v == null) continue;
                        var expectedFile = System.IO.Path.Combine(folder, SanitizeFileName(v.Name) + ".pdf");
                        exportedFiles.Add((object)expectedFile);
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"PDF export failed (requires Revit 2022+): {ex.Message}", ex);
                }
            }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["files"] = exportedFiles,
                ["folder"] = folder,
                ["not_found"] = notFound,
            };
        }

        // ─── group_elements ──────────────────────────────────────────────
        /// <summary>
        /// args: { element_ids:[long], name?:string }
        /// Groups elements into a model group using Document.Create.NewGroup.
        /// If name is supplied, renames the new group's GroupType (guarding
        /// against duplicate names). Returns {ok, group_id}.
        /// Requires Revit 2015+.
        /// </summary>
        public static Dictionary<string, object?> GroupElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            var name = ArgsHelp.GetString(args, "name");

            if (ids.Count == 0)
                throw new ArgumentException("element_ids must not be empty");

            var elementIds = ids.Select(id => new ElementId(id)).ToList();

            using var tx = new Transaction(doc, "BinaVibe: group_elements");
            tx.Start();
            try
            {
                var group = doc.Create.NewGroup(elementIds);

                if (!string.IsNullOrEmpty(name))
                {
                    // Guard duplicate group type names: only rename if not already taken.
                    var existingNames = new FilteredElementCollector(doc)
                        .OfClass(typeof(GroupType))
                        .Select(gt => gt.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    if (!existingNames.Contains(name))
                        group.GroupType.Name = name;
                }

                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["group_id"] = group.Id.Value,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── pin_elements ────────────────────────────────────────────────
        /// <summary>
        /// args: { element_ids:[long], pinned:bool }
        /// Sets element.Pinned for each element in a single Transaction.
        /// pinned=false unpins. Returns {ok, affected}.
        /// Pinned property available Revit 2015+.
        /// </summary>
        public static Dictionary<string, object?> PinElements(Document doc, JsonElement args)
        {
            var ids = ArgsHelp.GetLongList(args, "element_ids");
            bool pinned = ArgsHelp.GetBool(args, "pinned") ?? true;

            int affected = 0;
            var failures = new List<object>();

            using var tx = new Transaction(doc, $"BinaVibe: pin_elements (pinned={pinned}, count={ids.Count})");
            tx.Start();
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        var el = doc.GetElement(new ElementId(id));
                        if (el == null) continue;
                        el.Pinned = pinned;
                        affected++;
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
                ["affected"] = affected,
                ["failures"] = failures,
            };
        }

        // ─── join_geometry ───────────────────────────────────────────────
        /// <summary>
        /// args: { element_id_a:long, element_id_b:long }
        /// Joins the geometry of two elements using JoinGeometryUtils.JoinGeometry.
        /// Catches the "already joined" case and returns ok=true so it is idempotent.
        /// Returns {ok}.
        /// JoinGeometryUtils available Revit 2015+.
        /// </summary>
        public static Dictionary<string, object?> JoinGeometry(Document doc, JsonElement args)
        {
            var idA = ArgsHelp.GetLong(args, "element_id_a") ?? throw new ArgumentException("missing element_id_a");
            var idB = ArgsHelp.GetLong(args, "element_id_b") ?? throw new ArgumentException("missing element_id_b");

            var elA = doc.GetElement(new ElementId(idA)) ?? throw new ArgumentException($"element {idA} not found");
            var elB = doc.GetElement(new ElementId(idB)) ?? throw new ArgumentException($"element {idB} not found");

            using var tx = new Transaction(doc, "BinaVibe: join_geometry");
            tx.Start();
            try
            {
                // JoinGeometryUtils.AreElementsJoined can tell us if they're already joined;
                // attempting to join again throws, so we check first.
                if (!JoinGeometryUtils.AreElementsJoined(doc, elA, elB))
                    JoinGeometryUtils.JoinGeometry(doc, elA, elB);

                tx.Commit();
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // Elements cannot be joined (incompatible category, already joined race, etc.)
                tx.RollBack();
                throw;
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?> { ["ok"] = true };
        }

        // ─── renumber_elements ────────────────────────────────────────────
        /// <summary>
        /// args: { category:string, parameter?:string, start?:int, prefix?:string }
        /// Collects all instances of category, orders by ElementId (stable),
        /// then sets LookupParameter(parameter) to prefix+(start+i) for each.
        /// Skips elements where the parameter is missing or read-only.
        /// Returns {ok, renumbered}.
        /// </summary>
        public static Dictionary<string, object?> RenumberElements(Document doc, JsonElement args)
        {
            var category = ArgsHelp.GetString(args, "category") ?? throw new ArgumentException("missing category");
            var parameter = ArgsHelp.GetString(args, "parameter") ?? "Mark";
            int start = (int)(ArgsHelp.GetDouble(args, "start") ?? 1.0);
            var prefix = ArgsHelp.GetString(args, "prefix") ?? "";

            // Resolve BuiltInCategory by friendly or enum name (same as tag_elements).
            BuiltInCategory bic = BuiltInCategory.INVALID;
            foreach (BuiltInCategory c in Enum.GetValues(typeof(BuiltInCategory)))
            {
                try
                {
                    var cat = Category.GetCategory(doc, c);
                    if (cat == null) continue;
                    if (string.Equals(cat.Name, category, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.ToString(), category, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(c.ToString(), $"OST_{category}", StringComparison.OrdinalIgnoreCase))
                    {
                        bic = c;
                        break;
                    }
                }
                catch { }
            }
            if (bic == BuiltInCategory.INVALID)
                throw new ArgumentException($"category '{category}' not recognised");

            var elements = new FilteredElementCollector(doc)
                .OfCategory(bic)
                .WhereElementIsNotElementType()
                .OrderBy(el => el.Id.Value)   // stable ordering by ElementId
                .ToList();

            int renumbered = 0;
            using var tx = new Transaction(doc, $"BinaVibe: renumber_elements ({category})");
            tx.Start();
            try
            {
                for (int i = 0; i < elements.Count; i++)
                {
                    var el = elements[i];
                    var p = el.LookupParameter(parameter);
                    if (p == null || p.IsReadOnly) continue;
                    try
                    {
                        p.Set(prefix + (start + i).ToString());
                        renumbered++;
                    }
                    catch { /* skip this element */ }
                }
                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["renumbered"] = renumbered,
            };
        }

        // ─── create_view_filter ──────────────────────────────────────────
        /// <summary>
        /// args: { name:string, categories:[string], param:string, op:string, value:string }
        /// Creates a reusable ParameterFilterElement (view/parameter filter) that
        /// selects elements of the given categories where param &lt;op&gt; value.
        /// op: = != &lt; &gt; &lt;= &gt;= contains
        ///
        /// Implementation notes:
        ///   1. categories → List&lt;ElementId&gt; via Category.GetCategory(doc, bic).
        ///   2. param → ElementId by sampling the first instance of the first category
        ///      and calling element.LookupParameter(param).Id.  This is best-effort;
        ///      if no instances exist or the parameter is not found, the call throws.
        ///   3. FilterRule built with ParameterFilterRuleFactory (factory method chosen
        ///      by op and by whether value parses to double).
        ///   4. ParameterFilterElement.Create(doc, name, catIds, epf) in a Transaction.
        ///
        /// Returns {ok, filter_id}.
        /// FLAG: param→ElementId resolution is best-effort (samples first instance).
        /// </summary>
        public static Dictionary<string, object?> CreateViewFilter(Document doc, JsonElement args)
        {
            var name = ArgsHelp.GetString(args, "name") ?? throw new ArgumentException("missing name");
            var categoriesRaw = ArgsHelp.GetStringList(args, "categories");
            var paramName = ArgsHelp.GetString(args, "param") ?? throw new ArgumentException("missing param");
            var op = ArgsHelp.GetString(args, "op") ?? "=";
            var value = ArgsHelp.GetString(args, "value") ?? throw new ArgumentException("missing value");

            if (categoriesRaw.Count == 0)
                throw new ArgumentException("categories must not be empty");

            // 1. Resolve categories → ElementId list.
            var catIds = new List<ElementId>();
            BuiltInCategory? firstBic = null;
            foreach (var catStr in categoriesRaw)
            {
                BuiltInCategory bic;
                if (!TryResolveBuiltInCategory(catStr, out bic))
                    throw new ArgumentException($"category '{catStr}' not recognised");
                catIds.Add(new ElementId(bic));
                firstBic ??= bic;
            }

            // 2. Resolve param → ElementId by sampling first instance of first category.
            ElementId paramId = ElementId.InvalidElementId;
            var sampleEl = firstBic.HasValue
                ? new FilteredElementCollector(doc)
                    .OfCategory(firstBic.Value)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault()
                : null;

            if (sampleEl != null)
            {
                var p = sampleEl.LookupParameter(paramName);
                if (p != null)
                    paramId = p.Id;
            }

            if (paramId == ElementId.InvalidElementId)
            {
                // Fallback: try built-in parameter map for common names.
                paramId = ResolveBuiltInParameterId(doc, paramName)
                    ?? throw new ArgumentException(
                        $"param '{paramName}' not found on any instance of '{categoriesRaw[0]}' and not a known built-in");
            }

            // 3. Build FilterRule from op + value.
            FilterRule rule = BuildFilterRule(paramId, op, value);

            var epf = new ElementParameterFilter(rule);

            // 4. Create ParameterFilterElement.
            using var tx = new Transaction(doc, "BinaVibe: create_view_filter");
            tx.Start();
            try
            {
                var pfe = ParameterFilterElement.Create(doc, name, catIds, epf);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["filter_id"] = pfe.Id.Value,
                    ["name"] = pfe.Name,
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── apply_view_filter ───────────────────────────────────────────
        /// <summary>
        /// args: { view_name:string, filter_name:string, hide?:bool,
        ///         r?:int, g?:int, b?:int }
        /// Applies an existing ParameterFilterElement to a view.
        ///   hide=true → view.SetFilterVisibility(filter.Id, false)
        ///   r/g/b present → OverrideGraphicSettings with surface + projection color
        ///     (FLAG: solid-fill pattern may be needed for surface color to render in
        ///      3D views; we set surface foreground pattern color but do not force a
        ///      solid-fill pattern — if the element has no fill pattern the color may
        ///      only appear on edges)
        /// Transaction.  Returns {ok}.
        /// </summary>
        public static Dictionary<string, object?> ApplyViewFilter(Document doc, JsonElement args)
        {
            var viewName = ArgsHelp.GetString(args, "view_name") ?? throw new ArgumentException("missing view_name");
            var filterName = ArgsHelp.GetString(args, "filter_name") ?? throw new ArgumentException("missing filter_name");
            bool hide = ArgsHelp.GetBool(args, "hide") ?? false;
            double? rRaw = ArgsHelp.GetDouble(args, "r");
            double? gRaw = ArgsHelp.GetDouble(args, "g");
            double? bRaw = ArgsHelp.GetDouble(args, "b");
            bool hasColor = rRaw.HasValue && gRaw.HasValue && bRaw.HasValue;

            // Resolve view by name.
            var view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate &&
                    string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"view '{viewName}' not found");

            // Resolve filter by name.
            var filter = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .FirstOrDefault(pf => string.Equals(pf.Name, filterName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"filter '{filterName}' not found — create it first with create_view_filter");

            using var tx = new Transaction(doc, $"BinaVibe: apply_view_filter ({filterName})");
            tx.Start();
            try
            {
                // Ensure the filter is added to the view first.
                if (!view.GetFilters().Contains(filter.Id))
                    view.AddFilter(filter.Id);

                if (hide)
                {
                    view.SetFilterVisibility(filter.Id, false);
                }
                else if (hasColor)
                {
                    byte r = (byte)Math.Clamp((int)rRaw!.Value, 0, 255);
                    byte g = (byte)Math.Clamp((int)gRaw!.Value, 0, 255);
                    byte b = (byte)Math.Clamp((int)bRaw!.Value, 0, 255);

                    var color = new Color(r, g, b);
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetProjectionLineColor(color);
                    // FLAG: to guarantee surface fill is visible in floor plans the
                    // caller should also ensure a solid-fill pattern is assigned to the
                    // element category's surface pattern (not done here — would require
                    // locating a solid-fill FillPatternElement and assigning it to ogs).
                    view.SetFilterOverrides(filter.Id, ogs);
                }

                tx.Commit();
            }
            catch { tx.RollBack(); throw; }

            return new Dictionary<string, object?> { ["ok"] = true };
        }

        // ─── create_floor ────────────────────────────────────────────────
        /// <summary>
        /// args: { boundary:[[x,y],...], level:string, type_name?:string }
        /// Creates a floor from a closed 2D boundary (list of [x,y] in feet) on a level.
        ///
        /// API: Floor.Create(doc, IList&lt;CurveLoop&gt;, floorTypeId, levelId)
        ///   Available Revit 2022+.  On older Revit the overload does not exist;
        ///   FLAG: falls back to doc.Create.NewFloor(profile, floorType, level, false)
        ///   (Revit 2015-2021) via reflection-free try/catch structural approach — not
        ///   implemented here; older-Revit users will get an exception.
        ///
        /// Returns {ok, floor_id}.
        /// FLAG: Requires Revit 2022+ for Floor.Create(doc, loops, typeId, levelId).
        /// </summary>
        public static Dictionary<string, object?> CreateFloor(Document doc, JsonElement args)
        {
            var levelName = ArgsHelp.GetString(args, "level") ?? throw new ArgumentException("missing level");
            var typeName = ArgsHelp.GetString(args, "type_name");

            // Parse boundary: array of [x,y] pairs.
            var points = ParseBoundary2D(args, "boundary");
            if (points.Count < 3)
                throw new ArgumentException("boundary must have at least 3 points");

            // Build CurveLoop (close it: last point back to first).
            var loop = BuildCurveLoop(points);

            // Resolve level.
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"level '{levelName}' not found");

            // Resolve floor type — named or first available.
            ElementId floorTypeId;
            if (!string.IsNullOrEmpty(typeName))
            {
                var ft = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                    .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"floor type '{typeName}' not found");
                floorTypeId = ft.Id;
            }
            else
            {
                var first = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).FirstOrDefault()
                    ?? throw new InvalidOperationException("no FloorType found in document");
                floorTypeId = first.Id;
            }

            using var tx = new Transaction(doc, "BinaVibe: create_floor");
            tx.Start();
            try
            {
                // Revit 2022+ API: Floor.Create(doc, IList<CurveLoop>, ElementId typeId, ElementId levelId)
                var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorTypeId, level.Id);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["floor_id"] = floor.Id.Value,
                    ["level"] = levelName,
                    ["type_name"] = typeName ?? "(default)",
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── create_ceiling ──────────────────────────────────────────────
        /// <summary>
        /// args: { boundary:[[x,y],...], level:string, type_name?:string }
        /// Creates a ceiling from a closed 2D boundary on a level.
        ///
        /// API: Ceiling.Create(doc, IList&lt;CurveLoop&gt;, ceilingTypeId, levelId)
        /// FLAG: Ceiling.Create is only available in Revit 2022+.  There is no
        ///   equivalent pre-2022 API — this will throw a MissingMethodException on
        ///   older installs (hard requirement).
        ///
        /// Returns {ok, ceiling_id}.
        /// FLAG: Hard requirement — Revit 2022+ only.
        /// </summary>
        public static Dictionary<string, object?> CreateCeiling(Document doc, JsonElement args)
        {
            var levelName = ArgsHelp.GetString(args, "level") ?? throw new ArgumentException("missing level");
            var typeName = ArgsHelp.GetString(args, "type_name");

            var points = ParseBoundary2D(args, "boundary");
            if (points.Count < 3)
                throw new ArgumentException("boundary must have at least 3 points");

            var loop = BuildCurveLoop(points);

            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"level '{levelName}' not found");

            // Resolve ceiling type — named or first available.
            ElementId ceilingTypeId;
            if (!string.IsNullOrEmpty(typeName))
            {
                var ct = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<CeilingType>()
                    .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"ceiling type '{typeName}' not found");
                ceilingTypeId = ct.Id;
            }
            else
            {
                var first = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).FirstOrDefault()
                    ?? throw new InvalidOperationException("no CeilingType found in document");
                ceilingTypeId = first.Id;
            }

            using var tx = new Transaction(doc, "BinaVibe: create_ceiling");
            tx.Start();
            try
            {
                // Revit 2022+ API: Ceiling.Create(doc, IList<CurveLoop>, ElementId typeId, ElementId levelId)
                var ceiling = Ceiling.Create(doc, new List<CurveLoop> { loop }, ceilingTypeId, level.Id);
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["ceiling_id"] = ceiling.Id.Value,
                    ["level"] = levelName,
                    ["type_name"] = typeName ?? "(default)",
                };
            }
            catch { tx.RollBack(); throw; }
        }

        // ─── filter/boundary helpers ─────────────────────────────────────

        /// <summary>
        /// Parse a JSON array of [x,y] pairs from the args element.
        /// Returns a list of XYZ with z=0.
        /// </summary>
        private static List<XYZ> ParseBoundary2D(JsonElement args, string name)
        {
            var pts = new List<XYZ>();
            if (args.ValueKind != JsonValueKind.Object) return pts;
            if (!args.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return pts;

            foreach (var pt in arr.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array) continue;
                var nums = new List<double>();
                foreach (var n in pt.EnumerateArray())
                {
                    if (n.ValueKind == JsonValueKind.Number && n.TryGetDouble(out var d))
                        nums.Add(d);
                }
                if (nums.Count >= 2)
                    pts.Add(new XYZ(nums[0], nums[1], 0.0));
            }
            return pts;
        }

        /// <summary>
        /// Build a closed CurveLoop from a list of XYZ points (at least 3).
        /// Connects consecutive points with Line.CreateBound, then closes last→first.
        /// </summary>
        private static CurveLoop BuildCurveLoop(List<XYZ> pts)
        {
            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                // Skip degenerate zero-length segments.
                if (a.DistanceTo(b) < 1e-6) continue;
                loop.Append(Line.CreateBound(a, b));
            }
            return loop;
        }

        /// <summary>
        /// Resolve a category string (friendly or OST_ enum) to a BuiltInCategory.
        /// Extends the Inspectors version with floors/ceilings/columns.
        /// </summary>
        private static bool TryResolveBuiltInCategory(string category, out BuiltInCategory bic)
        {
            if (category.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<BuiltInCategory>(category, true, out bic))
                return true;

            bic = category.ToLowerInvariant() switch
            {
                "walls"     => BuiltInCategory.OST_Walls,
                "doors"     => BuiltInCategory.OST_Doors,
                "windows"   => BuiltInCategory.OST_Windows,
                "floors"    => BuiltInCategory.OST_Floors,
                "ceilings"  => BuiltInCategory.OST_Ceilings,
                "rooms"     => BuiltInCategory.OST_Rooms,
                "levels"    => BuiltInCategory.OST_Levels,
                "grids"     => BuiltInCategory.OST_Grids,
                "columns"   => BuiltInCategory.OST_Columns,
                "beams"     => BuiltInCategory.OST_StructuralFraming,
                _           => BuiltInCategory.INVALID,
            };
            return bic != BuiltInCategory.INVALID;
        }

        /// <summary>
        /// Try to resolve a parameter name to an ElementId via common BuiltInParameter
        /// mappings. Returns null when no mapping is found.
        /// FLAG: list is not exhaustive; best-effort.
        /// </summary>
        private static ElementId? ResolveBuiltInParameterId(Document doc, string paramName)
        {
            // Map well-known friendly names → BuiltInParameter.
            BuiltInParameter? bip = paramName.ToLowerInvariant() switch
            {
                "mark"              => BuiltInParameter.ALL_MODEL_MARK,
                "comments"          => BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
                "type name"         => BuiltInParameter.ELEM_TYPE_PARAM,
                "level"             => BuiltInParameter.FAMILY_LEVEL_PARAM,
                "base constraint"   => BuiltInParameter.WALL_BASE_CONSTRAINT,
                "top constraint"    => BuiltInParameter.WALL_HEIGHT_TYPE,
                "fire_rating"       => BuiltInParameter.DOOR_FIRE_RATING,
                "fire rating"       => BuiltInParameter.DOOR_FIRE_RATING,
                _                   => null,
            };

            if (!bip.HasValue) return null;
            var id = new ElementId(bip.Value);
            return id == ElementId.InvalidElementId ? null : id;
        }

        /// <summary>
        /// Build a FilterRule for ParameterFilterElement given a parameter ElementId,
        /// comparison op, and string value. Uses ParameterFilterRuleFactory.
        /// Chooses numeric vs string overload based on whether value parses to double.
        /// </summary>
        private static FilterRule BuildFilterRule(ElementId paramId, string op, string value)
        {
            bool isNumeric = double.TryParse(value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double numValue);

            // Tolerance for numeric equality/inequality.
            const double eps = 1e-6;

            if (isNumeric)
            {
                return op switch
                {
                    "="  or "==" => ParameterFilterRuleFactory.CreateEqualsRule(paramId, numValue, eps),
                    "!="         => ParameterFilterRuleFactory.CreateNotEqualsRule(paramId, numValue, eps),
                    "<"          => ParameterFilterRuleFactory.CreateLessRule(paramId, numValue, eps),
                    "<="         => ParameterFilterRuleFactory.CreateLessOrEqualRule(paramId, numValue, eps),
                    ">"          => ParameterFilterRuleFactory.CreateGreaterRule(paramId, numValue, eps),
                    ">="         => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(paramId, numValue, eps),
                    // "contains" on a numeric column → treat as string contains.
                    _            => StringRule(paramId, "contains", value),
                };
            }

            return StringRule(paramId, op, value);
        }

        /// <summary>
        /// String FilterRule built directly. Revit 2023+ removed the
        /// ParameterFilterRuleFactory string overloads and the caseSensitive
        /// arg, so we construct FilterStringRule ourselves. Not-equals is the
        /// inverse of equals; string ordering ops fall back to equals.
        /// </summary>
        private static FilterRule StringRule(ElementId paramId, string op, string value)
        {
            var pvp = new ParameterValueProvider(paramId);
            switch (op)
            {
                case "!=":
                    return new FilterInverseRule(
                        new FilterStringRule(pvp, new FilterStringEquals(), value));
                case "contains":
                    return new FilterStringRule(pvp, new FilterStringContains(), value);
                default: // "=", "==", and unsupported string ordering ops
                    return new FilterStringRule(pvp, new FilterStringEquals(), value);
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
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
