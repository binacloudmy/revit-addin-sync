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
            McpCallLog.Write(tool, args);   // UAT: trace tool-call sequence (roundtrip proof)
            var doc = app.ActiveUIDocument?.Document;
            var uidoc = app.ActiveUIDocument;
            if (doc == null || uidoc == null)
                throw new InvalidOperationException("no active document — open a Revit project first");

            return tool switch
            {
                // INSPECT — 20 tools, all live
                "list_levels"                   => Inspectors.ListLevels(doc),
                "list_wall_types"               => Inspectors.ListWallTypes(doc),
                "list_family_types"             => Inspectors.ListFamilyTypes(doc, args),
                "list_view_templates"           => Inspectors.ListViewTemplates(doc),
                "list_worksets"                 => Inspectors.ListWorksets(doc),
                "get_element_parameters"        => Inspectors.GetElementParameters(doc, args),
                "find_elements_by_filter"       => Inspectors.FindElementsByFilter(doc, args),
                "query_geometry"                => QueryGeometry.Run(doc, args),
                "filter_elements"               => ElementFilter.Run(app, doc, args),
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
                "get_material_quantities"       => Inspectors.GetMaterialQuantities(doc, args),
                "get_model_warnings"            => Inspectors.GetModelWarnings(doc),
                "list_view_filters"             => Inspectors.ListViewFilters(doc),
                "list_phases"                   => Inspectors.ListPhases(doc),
                "list_design_options"           => Inspectors.ListDesignOptions(doc),
                "list_rvt_links"                => Inspectors.ListRvtLinks(doc),
                "list_revisions"                => Inspectors.ListRevisions(doc),
                "list_model_groups"             => Inspectors.ListModelGroups(doc),
                "get_sheet_viewports"           => Inspectors.GetSheetViewports(doc, args),
                "list_project_parameters"       => Inspectors.ListProjectParameters(doc),
                "get_type_parameters"           => Inspectors.GetTypeParameters(doc, args),
                "list_rooms"                    => Inspectors.ListRooms(doc, args),
                "open_view"                     => Inspectors.OpenView(uidoc, args),
                "select_elements"               => Inspectors.SelectElements(uidoc, args),
                "count_by"                      => Inspectors.CountBy(doc, args),
                "export_schedule_to_excel"      => Inspectors.ExportScheduleToExcel(doc, args),
                "isolate_elements"              => Mutators.IsolateElements(doc, args),
                "tag_all_in_view"               => Mutators.TagAllInView(doc, args),
                "create_schedule"               => Mutators.CreateSchedule(doc, args),
                "dimension_grids"               => Mutators.DimensionGrids(doc, args),
                "crop_view_to_elements"         => Mutators.CropViewToElements(doc, args),
                "create_3d_view"                => Mutators.Create3dView(uidoc, args),
                "set_section_box"               => Mutators.SetSectionBox(doc, args),
                "find_missing_parameter"        => Inspectors.FindMissingParameter(doc, args),
                "rename_elements"               => Mutators.RenameElements(doc, args),
                "color_by_parameter"            => Mutators.ColorByParameter(doc, args),
                "delete_unused_views"           => Mutators.DeleteUnusedViews(doc, args),
                "purge_unused"                  => Mutators.PurgeUnused(doc, args),
                "create_project_parameter"      => Mutators.CreateProjectParameter(app, args),
                "place_in_each_room"            => Mutators.PlaceInEachRoom(doc, args),
                "set_parameter_where"           => Mutators.SetParameterWhere(doc, args),

                // MUTATE — Step 3, all wrap in Transaction.
                // Orchestrator-side policy gates destructive ones via
                // approval_decisions before they reach here.
                // Multi-element builds: ONE TransactionGroup, single undo.
                "execute_revit_batch"    => BatchExecutor.Run(app, args),
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
                "set_category_visibility" => Mutators.SetCategoryVisibility(doc, args),
                "place_family_instance"  => Mutators.PlaceFamilyInstance(doc, args),
                "load_family"            => Mutators.LoadFamily(app, args),
                "move_elements"          => Mutators.MoveElements(doc, args),
                "create_sheet"           => Mutators.CreateSheet(doc, args),
                "place_view_on_sheet"    => Mutators.PlaceViewOnSheet(doc, args),
                "tag_elements"                  => Mutators.TagElements(doc, app, args),
                "swap_element_type"             => Mutators.SwapElementType(doc, args),
                "replace_with_reference"        => Mutators.ReplaceWithReference(doc, args),
                "place_text_note"               => Mutators.PlaceTextNote(doc, args),
                "rotate_elements"               => Mutators.RotateElements(doc, args),
                "copy_elements"                 => Mutators.CopyElements(doc, args),
                "mirror_elements"               => Mutators.MirrorElements(doc, args),
                "export_views"                  => Mutators.ExportViews(doc, args),
                "group_elements"                => Mutators.GroupElements(doc, args),
                "pin_elements"                  => Mutators.PinElements(doc, args),
                "join_geometry"                 => Mutators.JoinGeometry(doc, args),
                "renumber_elements"             => Mutators.RenumberElements(doc, args),
                "create_view_filter"            => Mutators.CreateViewFilter(doc, args),
                "apply_view_filter"             => Mutators.ApplyViewFilter(doc, args),
                "create_floor"                  => Mutators.CreateFloor(doc, args),
                "create_ceiling"                => Mutators.CreateCeiling(doc, args),
                "create_beam_system"            => MutatorsStructure.CreateBeamSystem(doc, args),
                "create_beam"                   => MutatorsStructure.CreateBeam(doc, args),
                "create_duct"                   => MutatorsMep.CreateDuct(doc, args),
                "create_pipe"                   => MutatorsMep.CreatePipe(doc, args),

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
