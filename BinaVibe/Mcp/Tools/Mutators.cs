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
        public static Dictionary<string, object?> DuplicateView(Document doc, JsonElement args)
        {
            var sourceId = ArgsHelp.GetLong(args, "source_view_id") ?? throw new ArgumentException("missing source_view_id");
            var withDetailing = ArgsHelp.GetBool(args, "with_detailing") ?? false;
            var prefix = ArgsHelp.GetString(args, "prefix") ?? "";

            var src = doc.GetElement(new ElementId(sourceId)) as View
                ?? throw new ArgumentException($"view {sourceId} not found");

            using var tx = new Transaction(doc, "BinaVibe: duplicate_view");
            tx.Start();
            try
            {
                var newId = src.Duplicate(withDetailing ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate);
                var newView = doc.GetElement(newId) as View;
                if (!string.IsNullOrEmpty(prefix) && newView != null)
                {
                    newView.Name = $"{prefix}{src.Name}";
                }
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
        public static Dictionary<string, object?> ApplyViewTemplate(Document doc, JsonElement args)
        {
            var templateName = ArgsHelp.GetString(args, "template_name") ?? throw new ArgumentException("missing template_name");
            var viewIds = ArgsHelp.GetLongList(args, "view_ids");

            var template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => v.IsTemplate && string.Equals(v.Name, templateName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"template '{templateName}' not found");

            if (viewIds.Count == 0)
            {
                var av = doc.ActiveView;
                if (av == null) throw new InvalidOperationException("no active view and no view_ids supplied");
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

        // ─── create_room ────────────────────────────────────────────────
        public static Dictionary<string, object?> CreateRoom(Document doc, JsonElement args)
        {
            var p = ArgsHelp.GetXyz(args, "location") ?? throw new ArgumentException("missing location [x,y,z]");
            var levelName = ArgsHelp.GetString(args, "level") ?? throw new ArgumentException("missing level");
            var name = ArgsHelp.GetString(args, "name");

            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"level '{levelName}' not found");

            using var tx = new Transaction(doc, "BinaVibe: create_room");
            tx.Start();
            try
            {
                var uv = new UV(p.X, p.Y);
                var room = doc.Create.NewRoom(level, uv);
                if (!string.IsNullOrEmpty(name))
                {
                    var p1 = room.LookupParameter("Name");
                    if (p1 != null && !p1.IsReadOnly) p1.Set(name);
                }
                tx.Commit();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["created_id"] = room.Id.Value,
                    ["level"] = levelName,
                    ["name"] = name,
                };
            }
            catch { tx.RollBack(); throw; }
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
