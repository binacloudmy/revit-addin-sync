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
                // INSPECT — 10 tools, all live
                "list_levels"            => Inspectors.ListLevels(doc),
                "list_wall_types"        => Inspectors.ListWallTypes(doc),
                "list_family_types"      => Inspectors.ListFamilyTypes(doc, args),
                "list_view_templates"    => Inspectors.ListViewTemplates(doc),
                "list_worksets"          => Inspectors.ListWorksets(doc),
                "get_element_parameters" => Inspectors.GetElementParameters(doc, args),
                "find_elements_by_filter"=> Inspectors.FindElementsByFilter(doc, args),
                "get_current_selection"  => Inspectors.GetCurrentSelection(uidoc),
                "get_active_view"        => Inspectors.GetActiveView(doc),
                "get_project_info"       => Inspectors.GetProjectInfo(doc, app),

                _ => NotImplemented(tool),
            };
        }

        private static Dictionary<string, object?> NotImplemented(string tool) =>
            new()
            {
                ["error"] = $"tool {tool} not implemented yet — Step-1 ships only INSPECT",
                ["status"] = "not_implemented",
            };
    }
}
