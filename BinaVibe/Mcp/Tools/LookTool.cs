// LookTool: the `look` copilot tool (row G-03). Exports the drafter's view as
// a PNG so the backend can hand it to a vision model, and keeps a copy for
// the pane to show under the reply (RevitWebAppSync.Services.LookImageStash).
//
// Wire contract (backend app/services/view_look.py):
//   args:   { view_name?: string, question?: string (ignored here) }
//   result: { ok, view, view_id, view_type, scale, level,
//             image: { media_type: "image/png", width, height, base64 } }
//   `image` is last so the 2 KB tool-card digest shows the view first.
//
// Why not TurnReceiptService's export: its TryExportActiveView is private
// (and laptop 1's P2.2 is changing that file), so it cannot be called. The
// active-view export below uses the same recipe (VisibleRegionOfCurrentView,
// PNG, FitToPage) so the drafter's zoom is what the model sees. A view picked
// by name is not on screen, so it goes through the existing public
// Mutators.CaptureViewImage (the capture_view_image tool), unchanged.
//
// Privacy: the PNG is read into memory and the file deleted at once; the
// bytes live only in this result (scrubbed by the backend before anything is
// stored) and in LookImageStash (memory, cleared at turn end).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;

namespace BinaVibe.Mcp.Tools
{
    public static class LookTool
    {
        public const int PixelSize = 1280;

        public static Dictionary<string, object?> Run(UIDocument uidoc, Document doc, JsonElement args)
        {
            var viewName = ArgsHelp.GetString(args, "view_name");
            View? view;
            bool active;
            if (!string.IsNullOrWhiteSpace(viewName))
            {
                view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate
                        && string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase));
                if (view == null) throw new ArgumentException($"view '{viewName}' not found (use list_views)");
                active = uidoc.ActiveView != null && view.Id == uidoc.ActiveView.Id;
            }
            else
            {
                view = uidoc.ActiveView ?? doc.ActiveView;
                if (view == null) throw new InvalidOperationException("no active view");
                active = true;
            }
            if (!IsPicturable(view))
                throw new ArgumentException($"'{view.Name}' is a {view.ViewType} view, which has no picture to look at — open a plan, section, elevation, 3D view or sheet");

            var path = active ? ExportActiveView(doc) : ExportNamedView(doc, view);
            byte[] png;
            try { png = File.ReadAllBytes(path); }
            finally { TryDelete(path); }

            var stored = LookImageStash.Add(view.Name, png);
            LookImageStash.TryPngSize(png, out var w, out var h);

            var result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["view"] = view.Name,
                ["view_id"] = view.Id.Value,
                ["view_type"] = view.ViewType.ToString(),
                ["scale"] = SafeScale(view),
                ["level"] = view.GenLevel?.Name,
                ["shown_in_pane"] = stored != null,
                ["image"] = new Dictionary<string, object?>
                {
                    ["media_type"] = "image/png",
                    ["width"] = w,
                    ["height"] = h,
                    ["base64"] = Convert.ToBase64String(png),
                },
            };
            return result;
        }

        internal static bool IsPicturable(View view)
        {
            if (view.IsTemplate) return false;
            switch (view.ViewType)
            {
                case ViewType.Schedule:
                case ViewType.ColumnSchedule:
                case ViewType.PanelSchedule:
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.Internal:
                case ViewType.Undefined:
                case ViewType.Report:
                    return false;
                default:
                    return true;
            }
        }

        // Same ImageExportOptions recipe as TurnReceiptService.TryExportActiveView
        // (private there), at a size a vision model reads well.
        private static string ExportActiveView(Document doc)
        {
            var dir = Path.Combine(Path.GetTempPath(), "bina_look");
            Directory.CreateDirectory(dir);
            var stem = Path.Combine(dir, $"look-{DateTime.Now:HHmmssfff}");
            var opts = new ImageExportOptions
            {
                ExportRange = ExportRange.VisibleRegionOfCurrentView,
                FilePath = stem,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                PixelSize = PixelSize,
                FitDirection = FitDirectionType.Horizontal,
                ZoomType = ZoomFitType.FitToPage,
            };
            doc.ExportImage(opts);
            // Revit appends view-name suffixes: find the file it really wrote.
            var made = Directory.GetFiles(dir, Path.GetFileName(stem) + "*")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            return made ?? throw new InvalidOperationException("Revit reported no error but wrote no image");
        }

        private static string ExportNamedView(Document doc, View view)
        {
            using var json = JsonDocument.Parse(
                $"{{\"view_id\":{view.Id.Value},\"pixel_size\":{PixelSize}}}");
            var res = Mutators.CaptureViewImage(doc, json.RootElement);
            return res.TryGetValue("path", out var p) && p is string s && File.Exists(s)
                ? s
                : throw new InvalidOperationException("Revit reported no error but wrote no image");
        }

        private static object? SafeScale(View view)
        {
            try { return view.Scale; } catch { return null; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* temp dir; best effort */ }
        }
    }
}
