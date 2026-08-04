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
                // One-call orientation aggregate (pull-based context): project
                // + levels + phases + active view + selection summary + top
                // category counts. Composes the inspectors below.
                "get_scene_overview"            => SceneOverview.Run(uidoc, app),
                "list_levels"                   => Inspectors.ListLevels(doc),
                "list_wall_types"               => Inspectors.ListWallTypes(doc),
                "list_family_types"             => Inspectors.ListFamilyTypes(doc, args),
                "list_view_templates"           => Inspectors.ListViewTemplates(doc),
                "list_worksets"                 => Inspectors.ListWorksets(doc),
                "get_element_parameters"        => Inspectors.GetElementParameters(doc, args),
                "find_elements_by_filter"       => Inspectors.FindElementsByFilter(doc, args),
                "find_elements_between_grids"   => Inspectors.FindElementsBetweenGrids(doc, args),
                "find_mep_elements"             => Inspectors.FindMepElements(doc, args),
                "query_geometry"                => QueryGeometry.Run(doc, args),
                "measure_wall_openings"         => WallOpenings.Run(doc, args),
                "filter_elements"               => ElementFilter.Run(app, doc, args),
                "extract_cad_geometry"          => CadExtract.Run(uidoc, args),
                "cad_walls_to_centerlines"      => CadWallsToCenterlines.Run(uidoc, args),
                "cad_walls_from_attachment"     => CadWallsFromAttachment.Run(uidoc, args),
                "cad_doors_from_attachment"     => CadDoorsFromAttachment.Run(uidoc, args),
                "cad_windows_from_attachment"   => CadWindowsFromAttachment.Run(uidoc, args),
                "compare_levels"                => LevelCompare.Run(uidoc, args),
                "batch_link_models"             => BatchLink.Run(uidoc, args),
                // link_file: link ONE CAD/IFC/Revit file by path, dispatched by
                // extension — the "link this file the drafter just received"
                // counterpart to batch_link_models's folder scan. MUTATE (it
                // adds a link to the document), but grouped here next to
                // batch_link_models since it shares BatchLink.cs's link+verify
                // machinery. See BatchLink.RunLinkFile for the per-format notes.
                "link_file"                     => BatchLink.RunLinkFile(uidoc, args),
                "get_current_selection"         => Inspectors.GetCurrentSelection(uidoc),
                "get_active_view"               => Inspectors.GetActiveView(doc),
                "get_current_view_elements"     => Inspectors.GetCurrentViewElements(uidoc),
                "get_project_info"              => Inspectors.GetProjectInfo(doc, app),
                "list_views"                    => Inspectors.ListViews(doc),
                "list_sheets"                   => Inspectors.ListSheets(doc),
                "list_schedules"                => Inspectors.ListSchedules(doc),
                // Cells AND the element ids behind them — export_schedule_to_excel
                // reads the same cells but hands back a file, so the rows never
                // reach the agent.
                "read_schedule"                 => Schedules.Read(doc, args),
                "list_grids"                    => Inspectors.ListGrids(doc),
                "analyze_model_statistics"      => Inspectors.AnalyzeModelStatistics(doc),
                "find_elements_by_parameter"    => Inspectors.FindElementsByParameter(doc, args),
                "get_material_quantities"       => Inspectors.GetMaterialQuantities(doc, args),
                "get_model_warnings"            => Inspectors.GetModelWarnings(doc),
                "find_duplicate_walls"          => Inspectors.FindDuplicateWalls(doc),
                "list_view_filters"             => Inspectors.ListViewFilters(doc),
                "list_phases"                   => Inspectors.ListPhases(doc),
                "list_design_options"           => Inspectors.ListDesignOptions(doc),
                "list_rvt_links"                => Inspectors.ListRvtLinks(doc),
                // DWG reads — see DwgReader for the fidelity ceiling. Every
                // arm takes a dwg_ref: "model:<id>" for CAD in the model,
                // "att:<guid>" for a drawing the pane attached.
                "list_cad_links"                => Inspectors.ListCadLinks(doc),
                "get_dwg_summary"               => DwgSummary(app, doc, args),
                "get_dwg_layer_detail"          => DwgLayerDetail(app, doc, args),
                "get_dwg_blocks"                => DwgBlocks(app, doc, args),
                // Enhanced DWG reads via ACadSharp — block NAMES, text content,
                // attributes. Only works for attachments (att:<guid>); model CAD
                // has no local file to read. Returns error for model refs.
                "get_dwg_block_names"           => DwgBlockNames(args),
                "get_dwg_texts"                 => DwgTexts(args),
                // Pane-only: opens an attached DWG and hands back its ref +
                // summary. Deliberately NOT advertised to the model (it takes a
                // local file path), so it has no entry in the agent's catalog.
                "dwg_open_attachment"           => DwgOpenAttachment(app, args),
                // PDF reads — no Revit document involved, but routed through the
                // same registry so tracing, the resume loop and the tool manifest
                // work exactly as for every other tool.
                "get_pdf_summary"               => PdfSummary(args),
                "get_pdf_page_text"             => PdfPage(args),
                "search_pdf"                    => PdfSearch(args),
                // Pane-only, like dwg_open_attachment: takes a local file path.
                "pdf_open_attachment"           => PdfOpenAttachment(args),
                // Audit-form reads — fill_audit parses the attached form and
                // evaluates deterministic checkers against the live model;
                // draft_export renders a cached fill_audit result to a file.
                // Both read-only (no Transaction).
                "fill_audit"                    => Audit.AuditTools.FillAudit(doc, args),
                "draft_export"                  => Audit.AuditTools.DraftExport(doc, args),
                "list_revisions"                => Inspectors.ListRevisions(doc),
                "list_model_groups"             => Inspectors.ListModelGroups(doc),
                "get_sheet_viewports"           => Inspectors.GetSheetViewports(doc, args),
                "list_project_parameters"       => Inspectors.ListProjectParameters(doc),
                "get_type_parameters"           => Inspectors.GetTypeParameters(doc, args),
                "list_rooms"                    => Inspectors.ListRooms(doc, args),
                // Socket placement candidates. Read-only (no Transaction): it
                // caches a plan and returns reviewable points, and
                // place_socket_points commits them. Same two-step shape as
                // fill_audit -> draft_export.
                "suggest_socket_points"         => Electrical.SocketCandidates.Suggest(doc, args),
                "audit_parameters"              => Inspectors.AuditParameters(doc, args),
                "audit_view_names"              => Inspectors.AuditViewNames(doc, args),
                "audit_family_names"            => Inspectors.AuditFamilyNames(doc, args),
                "open_view"                     => Inspectors.OpenView(uidoc, args),
                "select_elements"               => Inspectors.SelectElements(uidoc, args),
                "count_by"                      => Inspectors.CountBy(doc, args),
                "export_schedule_to_excel"      => Inspectors.ExportScheduleToExcel(doc, args),
                "get_project_base_point"        => Coordination.GetProjectBasePoint(doc, args),
                "check_grid_alignment"          => Coordination.CheckGridAlignment(doc, args),
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
                // Writes element parameters behind schedule columns (Revit has
                // no settable cell); fields validated against the schedule.
                "write_schedule"         => Schedules.Write(doc, args),
                "change_type"            => Mutators.ChangeType(doc, args),
                "delete_elements"        => Mutators.DeleteElements(doc, args),
                "duplicate_view"         => Mutators.DuplicateView(doc, args),
                "apply_view_template"    => Mutators.ApplyViewTemplate(doc, args),
                "place_door"             => Mutators.PlaceDoor(doc, args),
                "place_window"           => Mutators.PlaceWindow(doc, args),
                // Sockets: one TransactionGroup, single undo, per-item failure
                // tolerance. Coordinates come from the cached plan, never from
                // the model re-emitting them.
                "place_socket_points"    => Electrical.SocketPlacement.PlaceSocketPoints(doc, args),
                "place_socket_on_wall"   => Electrical.SocketPlacement.PlaceSocketOnWall(doc, args),
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
                "create_roof"                   => Mutators.CreateRoof(doc, args),
                "create_beam_system"            => MutatorsStructure.CreateBeamSystem(doc, args),
                "create_beam"                   => MutatorsStructure.CreateBeam(doc, args),
                "create_duct"                   => MutatorsMep.CreateDuct(doc, args),
                "create_pipe"                   => MutatorsMep.CreatePipe(doc, args),
                "create_dimensions"             => Dimensioning.CreateDimensions(app, doc, args),
                "list_connectors"               => MutatorsMepRouting.ListConnectors(doc, args),
                "trace_connections"             => MutatorsMepRouting.TraceConnections(doc, args),
                "route_duct"                    => MutatorsMepRouting.RouteDuct(doc, args),
                "route_pipe"                    => MutatorsMepRouting.RoutePipe(doc, args),
                "tap_branch"                    => MutatorsMepRouting.TapBranch(doc, args),

                // Generic OSS-compatible wrappers — dispatch to typed tools.
                // Arg names get remapped per target below (RemapArgs) since the
                // generic contract's keys don't all match what the typed
                // handlers read (see mapping table in RemapArgs call sites).
                "create_point_element"          => CreatePointElement(app, args),
                "create_line_element"           => CreateLineElement(app, args),
                "create_surface_element"        => CreateSurfaceElement(app, args),

                "store_data"                    => ScratchStore.Store(doc, args),
                "query_data"                    => ScratchStore.Query(doc, args),

                _ => NotImplemented(tool),
            };
        }

        private static Dictionary<string, object?> NotImplemented(string tool) =>
            new()
            {
                ["error"] = $"tool {tool} not implemented yet",
                ["status"] = "not_implemented",
            };

        // ─── DWG dispatch ───────────────────────────────────────────────
        // One resolver behind every DWG tool so in-model CAD and an attached
        // drawing are indistinguishable downstream — DwgReader never learns
        // which source it is reading.

        private static T WithDwg<T>(UIApplication app, Document doc, string dwgRef,
                                    Func<Document, ImportInstance, string, T> body)
        {
            if (string.IsNullOrWhiteSpace(dwgRef))
                throw new InvalidOperationException(
                    "dwg_ref is required — call list_cad_links, or use the ref from the [Attached DWG] block");

            if (DwgScratchCache.IsAttachmentRef(dwgRef))
                return DwgScratchCache.Use(app, dwgRef, (d, imp) => body(d, imp, "attachment"));

            var raw = dwgRef.StartsWith("model:", StringComparison.OrdinalIgnoreCase)
                ? dwgRef.Substring("model:".Length) : dwgRef;
            if (!long.TryParse(raw, out var id))
                throw new InvalidOperationException(
                    "bad dwg_ref '" + dwgRef + "' — expected \"model:<id>\" or \"att:<guid>\"");
            if (doc.GetElement(ElemIds.From(id)) is not ImportInstance imp2)
                throw new InvalidOperationException(
                    "no CAD import with id " + id + " in this model — call list_cad_links for the current refs");
            return body(doc, imp2, imp2.IsLinked ? "model_link" : "model_import");
        }

        private static Dictionary<string, object?> DwgSummary(UIApplication app, Document doc, JsonElement args)
        {
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref") ?? "";
            return WithDwg(app, doc, dwgRef, (d, imp, source) => DwgReader.Summarize(d, imp, dwgRef, source));
        }

        private static Dictionary<string, object?> DwgLayerDetail(UIApplication app, Document doc, JsonElement args)
        {
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref") ?? "";
            var layer = ArgsHelp.GetString(args, "layer") ?? "";
            var limit = (int)(ArgsHelp.GetLong(args, "limit") ?? 200);
            return WithDwg(app, doc, dwgRef, (d, imp, _) => DwgReader.LayerDetail(d, imp, dwgRef, layer, limit));
        }

        private static Dictionary<string, object?> DwgBlocks(UIApplication app, Document doc, JsonElement args)
        {
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref") ?? "";
            var limit = (int)(ArgsHelp.GetLong(args, "limit") ?? 100);
            return WithDwg(app, doc, dwgRef, (d, imp, _) => DwgReader.BlockInstances(d, imp, dwgRef, limit));
        }

        // Pane path: attach -> ref + summary in one call, so the composer can
        // embed the summary in the turn without a second round-trip.
        private static Dictionary<string, object?> DwgOpenAttachment(UIApplication app, JsonElement args)
        {
            var path = ArgsHelp.GetString(args, "path") ?? "";
            var dwgRef = DwgScratchCache.OpenAttachment(app, path);
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("no active document — open a Revit project first");
            var summary = WithDwg(app, doc, dwgRef, (d, imp, source) => DwgReader.Summarize(d, imp, dwgRef, source));

            // Enrich with ACadSharp data if available — block names, text preview.
            var acadData = DwgScratchCache.GetAcadSharpData(dwgRef);
            if (acadData?.Ok == true)
            {
                summary["has_enhanced_data"] = true;
                summary["block_names_available"] = acadData.Blocks.Count > 0;
                summary["text_content_available"] = acadData.Texts.Count > 0;
                summary["unique_block_names"] = acadData.Blocks
                    .Select(b => b.Name).Distinct().Take(20).ToList<object>();
                summary["text_count"] = acadData.Texts.Count;
                if (acadData.SourceInfo != null)
                {
                    summary["source_app"] = acadData.SourceInfo.Source;
                    if (!acadData.SourceInfo.Supported)
                        summary["source_warning"] = acadData.SourceInfo.Warning;
                }
            }

            return summary;
        }

        // ─── Enhanced DWG reads (ACadSharp) ─────────────────────────────
        // Block NAMES, text CONTENT, attributes — what Revit API cannot read.
        // Only works for attachments (we have the file path); model CAD would
        // require exporting the ImportInstance back to DWG, which Revit cannot do.

        private static Dictionary<string, object?> DwgBlockNames(JsonElement args)
        {
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref") ?? "";
            if (!DwgScratchCache.IsAttachmentRef(dwgRef))
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "get_dwg_block_names only works for attachments (att:<guid>). " +
                        "For model CAD, use get_dwg_blocks (returns positions but not names).",
                };

            var data = DwgScratchCache.GetAcadSharpData(dwgRef);
            if (data == null || !data.Ok)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = data?.Error ?? "ACadSharp extraction failed for this file",
                };

            var limit = (int)(ArgsHelp.GetLong(args, "limit") ?? 100);
            var layerFilter = ArgsHelp.GetString(args, "layer");

            var blocks = data.Blocks.AsEnumerable();
            if (!string.IsNullOrEmpty(layerFilter))
                blocks = blocks.Where(b => b.Layer.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            var result = blocks.Take(limit).Select(b => new Dictionary<string, object?>
            {
                ["name"] = b.Name,
                ["x"] = b.X,
                ["y"] = b.Y,
                ["rotation"] = b.Rotation,
                ["layer"] = b.Layer,
                ["attributes"] = b.Attributes.Count > 0 ? b.Attributes : null,
            }).ToList<object>();

            // Block census (name -> count)
            var census = data.Blocks
                .GroupBy(b => b.Name)
                .OrderByDescending(g => g.Count())
                .Take(50)
                .Select(g => new Dictionary<string, object> { ["name"] = g.Key, ["count"] = g.Count() })
                .ToList<object>();

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["dwg_ref"] = dwgRef,
                ["blocks"] = result,
                ["count"] = result.Count,
                ["total"] = data.Blocks.Count,
                ["truncated"] = data.Blocks.Count > result.Count,
                ["block_census"] = census,
                ["note"] = "Block NAMES are now available (via ACadSharp). Use for door/window type mapping.",
            };
        }

        private static Dictionary<string, object?> DwgTexts(JsonElement args)
        {
            var dwgRef = ArgsHelp.GetString(args, "dwg_ref") ?? "";
            if (!DwgScratchCache.IsAttachmentRef(dwgRef))
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = "get_dwg_texts only works for attachments (att:<guid>). " +
                        "Text content is not readable from model CAD via Revit API.",
                };

            var data = DwgScratchCache.GetAcadSharpData(dwgRef);
            if (data == null || !data.Ok)
                return new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = data?.Error ?? "ACadSharp extraction failed for this file",
                };

            var limit = (int)(ArgsHelp.GetLong(args, "limit") ?? 200);
            var layerFilter = ArgsHelp.GetString(args, "layer");

            var texts = data.Texts.AsEnumerable();
            if (!string.IsNullOrEmpty(layerFilter))
                texts = texts.Where(t => t.Layer.IndexOf(layerFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            var result = texts.Take(limit).Select(t => new Dictionary<string, object?>
            {
                ["type"] = t.Type,
                ["content"] = t.Content,
                ["x"] = t.X,
                ["y"] = t.Y,
                ["layer"] = t.Layer,
            }).ToList<object>();

            // Group by layer
            var byLayer = data.Texts
                .GroupBy(t => t.Layer)
                .OrderByDescending(g => g.Count())
                .Take(20)
                .ToDictionary(
                    g => g.Key,
                    g => g.Count() as object);

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["dwg_ref"] = dwgRef,
                ["texts"] = result,
                ["count"] = result.Count,
                ["total"] = data.Texts.Count,
                ["truncated"] = data.Texts.Count > result.Count,
                ["by_layer"] = byLayer,
                ["note"] = "Text CONTENT is now available (via ACadSharp). Use for room names, grid labels.",
            };
        }

        // ─── PDF dispatch ───────────────────────────────────────────────
        // Mirrors the DWG block above: one resolver, then thin arms. The ref
        // namespace is "pdf:<guid>" — a PDF only ever comes from an attachment,
        // so there is no in-model form to disambiguate.

        private static T WithPdf<T>(JsonElement args, Func<PdfDoc, string, T> body)
        {
            var pdfRef = ArgsHelp.GetString(args, "pdf_ref") ?? "";
            if (string.IsNullOrWhiteSpace(pdfRef))
                throw new InvalidOperationException(
                    "pdf_ref is required — use the ref from the [Attached PDF] block");
            if (!PdfAttachmentCache.IsAttachmentRef(pdfRef))
                throw new InvalidOperationException(
                    "bad pdf_ref '" + pdfRef + "' — expected \"pdf:<guid>\"");
            return PdfAttachmentCache.Use(pdfRef, doc => body(doc, pdfRef));
        }

        private static Dictionary<string, object?> PdfSummary(JsonElement args) =>
            WithPdf(args, (doc, pdfRef) => PdfReader.Summarize(doc, pdfRef));

        private static Dictionary<string, object?> PdfPage(JsonElement args)
        {
            var page = (int)(ArgsHelp.GetLong(args, "page") ?? 1);
            var maxChars = (int)(ArgsHelp.GetLong(args, "max_chars") ?? 4000);
            return WithPdf(args, (doc, pdfRef) => PdfReader.PageContent(doc, pdfRef, page, maxChars));
        }

        private static Dictionary<string, object?> PdfSearch(JsonElement args)
        {
            var query = ArgsHelp.GetString(args, "query") ?? "";
            var limit = (int)(ArgsHelp.GetLong(args, "limit") ?? 10);
            return WithPdf(args, (doc, pdfRef) => PdfReader.Search(doc, pdfRef, query, limit));
        }

        // Pane path: attach -> ref + summary in one call, so the composer can
        // embed the summary in the turn without a second round-trip.
        private static Dictionary<string, object?> PdfOpenAttachment(JsonElement args)
        {
            var path = ArgsHelp.GetString(args, "path") ?? "";
            var pdfRef = PdfAttachmentCache.OpenAttachment(path);
            return PdfAttachmentCache.Use(pdfRef, doc => PdfReader.Summarize(doc, pdfRef));
        }

        // ─── generic-tool arg remapping ─────────────────────────────────
        // Rebuild an args object with generic-contract keys renamed to the
        // target tool's native keys. Unmapped keys pass through unchanged.
        private static JsonElement RemapArgs(JsonElement args, params (string from, string to)[] renames)
        {
            var dict = new Dictionary<string, JsonElement>();
            if (args.ValueKind == JsonValueKind.Object)
                foreach (var p in args.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
            foreach (var (from, to) in renames)
                if (dict.TryGetValue(from, out var v) && !dict.ContainsKey(to))
                {
                    dict[to] = v;
                    dict.Remove(from);
                }
            return JsonSerializer.SerializeToElement(dict);
        }

        // create_point_element: generic {category, type_name, xyz_mm, level, host_id?}
        //   place_door / place_window (PlaceFamilyOnWall) read host_wall_id + location_mm; type_name matches already.
        //   place_family_instance reads family_type + xyz_mm (matches) + level (matches).
        private static Dictionary<string, object?> CreatePointElement(UIApplication app, JsonElement args)
        {
            var category = ArgsHelp.GetString(args, "category")?.ToLowerInvariant();
            return category switch
            {
                "doors" or "ost_doors" => Invoke(app, "place_door",
                    RemapArgs(args, ("host_id", "host_wall_id"), ("xyz_mm", "location_mm"))),
                "windows" or "ost_windows" => Invoke(app, "place_window",
                    RemapArgs(args, ("host_id", "host_wall_id"), ("xyz_mm", "location_mm"))),
                _ => Invoke(app, "place_family_instance",
                    RemapArgs(args, ("type_name", "family_type"))),
            };
        }

        // create_line_element: generic {category, start_mm, end_mm, type_name?, level}
        //   create_wall reads start_mm/end_mm/level/type_name — matches verbatim, no remap.
        //   create_beam (MutatorsStructure) reads beam_type_name.
        //   create_pipe (MutatorsMep) reads pipe_type_name.
        //   create_duct (MutatorsMep) reads duct_type_name.
        private static Dictionary<string, object?> CreateLineElement(UIApplication app, JsonElement args)
        {
            var category = ArgsHelp.GetString(args, "category")?.ToLowerInvariant();
            return category switch
            {
                "walls" or "ost_walls" => Invoke(app, "create_wall", args),
                "structuralframing" or "ost_structuralframing" or "beams" => Invoke(app, "create_beam",
                    RemapArgs(args, ("type_name", "beam_type_name"))),
                "pipecurves" or "ost_pipecurves" or "pipes" => Invoke(app, "create_pipe",
                    RemapArgs(args, ("type_name", "pipe_type_name"))),
                "ductcurves" or "ost_ductcurves" or "ducts" => Invoke(app, "create_duct",
                    RemapArgs(args, ("type_name", "duct_type_name"))),
                _ => throw new InvalidOperationException(
                    "create_line_element category must be Walls|StructuralFraming|PipeCurves|DuctCurves"),
            };
        }

        // create_surface_element: generic {category, boundary_mm, type_name?, level}
        //   create_floor reads boundary_mm/level/type_name — matches verbatim, no remap.
        //   create_ceiling reads boundary_mm/level/type_name — matches verbatim, no remap.
        //   create_roof reads boundary_mm/level/roof_type_name.
        private static Dictionary<string, object?> CreateSurfaceElement(UIApplication app, JsonElement args)
        {
            var category = ArgsHelp.GetString(args, "category")?.ToLowerInvariant();
            return category switch
            {
                "floors" or "ost_floors" => Invoke(app, "create_floor", args),
                "ceilings" or "ost_ceilings" => Invoke(app, "create_ceiling", args),
                "roofs" or "ost_roofs" => Invoke(app, "create_roof",
                    RemapArgs(args, ("type_name", "roof_type_name"))),
                _ => throw new InvalidOperationException(
                    "create_surface_element category must be Floors|Ceilings|Roofs"),
            };
        }
    }
}
