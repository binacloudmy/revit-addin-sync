using System.Collections.Generic;
using System.Text.Json;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Single source of truth for turning raw Revit tool names (list_grids,
    /// get_project_info) into human-friendly progress labels shown in the copilot's
    /// thinking trail. EDIT LABELS HERE — one place. Used by both the local Revit
    /// tool-execution path (ToolLoopRunner) and the SSE parser's fallback for
    /// backend tool events that arrive without a rich label. Unmapped tools fall
    /// back to a title-cased form so a brand-new tool still reads sensibly.
    /// </summary>
    public static class ToolLabels
    {
        // Raw tool name -> present-tense verb phrase (NO trailing ellipsis; the
        // caller adds "…"). Keep phrases short — they render as one trail row.
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>
        {
            // ── read / inspect ──
            ["list_levels"]                = "Reading levels",
            ["list_wall_types"]            = "Querying wall types",
            ["list_family_types"]          = "Listing family types",
            ["list_view_templates"]        = "Reading view templates",
            ["list_worksets"]              = "Reading worksets",
            ["get_element_parameters"]     = "Reading element parameters",
            ["find_elements_by_filter"]    = "Finding elements",
            ["find_elements_by_parameter"] = "Finding elements by parameter",
            ["get_current_selection"]      = "Reading the current selection",
            ["get_active_view"]            = "Reading the active view",
            ["get_current_view_elements"]  = "Reading elements in the view",
            ["get_project_info"]           = "Reading project info",
            ["list_views"]                 = "Listing views",
            ["list_sheets"]                = "Listing sheets",
            ["list_schedules"]             = "Listing schedules",
            ["list_grids"]                 = "Reading grids",
            ["find_elements_between_grids"] = "Finding elements between grids",
            ["find_mep_elements"]          = "Finding MEP elements",
            ["analyze_model_statistics"]   = "Analyzing model statistics",
            ["get_material_quantities"]    = "Reading material quantities",
            ["get_model_warnings"]         = "Checking model warnings",
            ["list_view_filters"]          = "Reading view filters",
            ["open_view"]                  = "Opening the view",
            ["select_elements"]            = "Selecting elements",
            ["count_by"]                   = "Counting elements",
            ["export_schedule_to_excel"]   = "Exporting schedule to Excel",
            ["find_missing_parameter"]     = "Finding missing parameters",

            // ── create / mutate ──
            ["create_wall"]                = "Creating walls",
            ["create_room"]                = "Creating rooms",
            ["create_level"]               = "Creating levels",
            ["create_grid"]                = "Creating grids",
            ["create_floor"]               = "Creating floors",
            ["create_ceiling"]             = "Creating ceilings",
            ["create_schedule"]            = "Creating the schedule",
            ["create_sheet"]               = "Creating the sheet",
            ["create_view_filter"]         = "Creating a view filter",
            ["create_3d_view"]             = "Creating a 3D view",
            ["create_project_parameter"]   = "Creating a project parameter",
            ["place_door"]                 = "Placing doors",
            ["place_window"]               = "Placing windows",
            ["place_family_instance"]      = "Placing family instances",
            ["place_in_each_room"]         = "Placing elements in rooms",
            ["suggest_socket_points"]      = "Suggesting socket positions",
            ["place_socket_points"]        = "Placing sockets",
            ["place_socket_on_wall"]       = "Placing a socket on the wall",
            ["place_view_on_sheet"]        = "Placing the view on the sheet",
            ["place_text_note"]            = "Placing a text note",
            ["tag_all_in_view"]            = "Tagging elements in the view",
            ["tag_elements"]               = "Tagging elements",
            ["set_parameter"]              = "Setting a parameter",
            ["set_parameter_bulk"]         = "Setting parameters",
            ["set_parameter_where"]        = "Setting parameters",
            ["set_category_visibility"]    = "Adjusting category visibility",
            ["set_section_box"]            = "Setting the section box",
            ["change_type"]                = "Changing element types",
            ["swap_element_type"]          = "Swapping element types",
            ["rename_elements"]            = "Renaming elements",
            ["renumber_elements"]          = "Renumbering elements",
            ["color_by_parameter"]         = "Colouring by parameter",
            ["color_elements"]             = "Colouring elements",
            ["move_elements"]              = "Moving elements",
            ["rotate_elements"]            = "Rotating elements",
            ["copy_elements"]              = "Copying elements",
            ["mirror_elements"]            = "Mirroring elements",
            ["delete_elements"]            = "Deleting elements",
            ["duplicate_view"]             = "Duplicating the view",
            ["apply_view_template"]        = "Applying the view template",
            ["apply_view_filter"]          = "Applying the view filter",
            ["isolate_elements"]           = "Isolating elements",
            ["hide_isolate_elements"]      = "Isolating elements",
            ["crop_view_to_elements"]      = "Cropping the view",
            ["dimension_grids"]            = "Dimensioning grids",
            ["delete_unused_views"]        = "Deleting unused views",
            ["purge_unused"]               = "Purging unused elements",
            ["group_elements"]             = "Grouping elements",
            ["pin_elements"]               = "Pinning elements",
            ["join_geometry"]              = "Joining geometry",
            ["export_views"]               = "Exporting views",
            ["replace_with_reference"]     = "Replacing with reference",
            ["execute_revit_batch"]        = "Applying changes to the model",
        };

        /// <summary>Readable label for a tool, e.g. "Reading grids". Appends a key
        /// argument where useful ("Finding elements — Walls"). Never empty.</summary>
        public static string Label(string tool, JsonElement args = default)
        {
            if (string.IsNullOrWhiteSpace(tool)) return "Working";
            if (!Map.TryGetValue(tool, out var phrase))
                phrase = TitleCase(tool);   // graceful fallback for a new/unknown tool

            var hint = KeyArg(args);
            return string.IsNullOrEmpty(hint) ? phrase : phrase + " — " + hint;
        }

        // Pull ONE presentable argument (category/type/name/level/…) to append when
        // the tool passed one. Best-effort; returns "" when nothing useful is found.
        private static string KeyArg(JsonElement args)
        {
            if (args.ValueKind != JsonValueKind.Object) return "";
            foreach (var key in KeyOrder)
            {
                if (!args.TryGetProperty(key, out var v)) continue;
                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return Clip(s);
                }
                else if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
                         && v[0].ValueKind == JsonValueKind.String)
                {
                    var s = v[0].GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return v.GetArrayLength() > 1 ? Clip(s) + " +" + (v.GetArrayLength() - 1) : Clip(s);
                }
            }
            return "";
        }

        private static readonly string[] KeyOrder =
        {
            "category", "categories", "type", "type_name", "family",
            "level", "level_name", "name", "filter", "parameter", "view",
        };

        private static string Clip(string s) => s.Length > 40 ? s.Substring(0, 39) + "…" : s;

        private static string TitleCase(string tool)
        {
            var words = tool.Replace('_', ' ').Trim();
            if (words.Length == 0) return "Working";
            return char.ToUpperInvariant(words[0]) + words.Substring(1);
        }
    }
}
