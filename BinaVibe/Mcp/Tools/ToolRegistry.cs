// ToolRegistry — dispatch table from tool name to Revit-API impl.
//
// Each tool runs on Revit's main thread (called from
// McpExternalEventHandler). Return values are flat dictionaries that
// JSON-serialize cleanly — keep types simple (string / int / double /
// bool / list / dict). No FamilyInstance or ElementId objects in
// results — convert to int + name first.
//
// **Step-1 scope:** all 10 INSPECT tools wired. MUTATE tools return
// {"error": "not implemented"} so the bina-ai Executor surfaces a
// clean failure. Step 3 lights them up.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    public static class ToolRegistry
    {
        public static Dictionary<string, object?> Invoke(UIApplication app, string tool, JsonElement args)
        {
            var doc = app.ActiveUIDocument?.Document;
            var uidoc = app.ActiveUIDocument;
            if (doc == null || uidoc == null)
                throw new InvalidOperationException("no active document — open a Revit project first");

            return tool switch
            {
                // INSPECT — 17 tools, all live
                "list_levels"                   => Inspectors.ListLevels(doc),
                "list_wall_types"               => Inspectors.ListWallTypes(doc),
                "list_family_types"             => Inspectors.ListFamilyTypes(doc, args),
                "list_view_templates"           => Inspectors.ListViewTemplates(doc),
                "list_worksets"                 => Inspectors.ListWorksets(doc),
                "get_element_parameters"        => Inspectors.GetElementParameters(doc, args),
                "find_elements_by_filter"       => Inspectors.FindElementsByFilter(doc, args),
                "get_current_selection"         => Inspectors.GetCurrentSelection(uidoc),
                "get_active_view"               => Inspectors.GetActiveView(doc),
                "get_current_view_elements"     => Inspectors.GetCurrentViewElements(uidoc),
                "get_project_info"              => Inspectors.GetProjectInfo(doc, app),
                "list_views"                    => Inspectors.ListViews(doc),
                "list_sheets"                   => Inspectors.ListSheets(doc),
                "list_schedules"                => Inspectors.ListSchedules(doc),
                "list_grids"                    => Inspectors.ListGrids(doc),
                "analyze_model_statistics"      => Inspectors.AnalyzeModelStatistics(doc),
                "find_elements_by_parameter"    => Inspectors.FindElementsByParameter(doc, args),

                // MUTATE — Step 3, 10 tools, all wrap in Transaction.
                // Orchestrator-side policy gates destructive ones via
                // approval_decisions before they reach here.
                "set_parameter"          => Mutators.SetParameter(doc, args),
                "set_parameter_bulk"     => Mutators.SetParameterBulk(doc, args),
                "change_type"            => Mutators.ChangeType(doc, args),
                "delete_elements"        => Mutators.DeleteElements(doc, args),
                "duplicate_view"         => Mutators.DuplicateView(doc, args),
                "apply_view_template"    => Mutators.ApplyViewTemplate(doc, args),
                "place_door"             => Mutators.PlaceDoor(doc, args),
                "place_window"           => Mutators.PlaceWindow(doc, args),
                "create_wall"            => Mutators.CreateWall(doc, args),
                "create_room"            => Mutators.CreateRoomXY(doc, args),
                "create_level"           => Mutators.CreateLevel(doc, args),
                "create_grid"            => Mutators.CreateGrid(doc, args),
                "color_elements"         => Mutators.ColorElements(doc, args),
                "hide_isolate_elements"  => Mutators.HideIsolateElements(doc, args),
                "place_family_instance"  => Mutators.PlaceFamilyInstance(doc, args),
                "move_elements"          => Mutators.MoveElements(doc, args),
                "create_sheet"           => Mutators.CreateSheet(doc, args),
                "place_view_on_sheet"    => Mutators.PlaceViewOnSheet(doc, args),
                "tag_elements"                  => Mutators.TagElements(doc, app, args),
                "swap_element_type"             => Mutators.SwapElementType(doc, args),
                "place_text_note"               => Mutators.PlaceTextNote(doc, args),

                _ => NotImplemented(tool),
            };
        }

        private static Dictionary<string, object?> NotImplemented(string tool) =>
            new()
            {
                ["error"] = $"tool {tool} not implemented yet",
                ["status"] = "not_implemented",
            };
    }
}
