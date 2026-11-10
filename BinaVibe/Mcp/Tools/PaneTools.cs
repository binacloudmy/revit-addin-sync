// PaneTools — the six reads/actions the copilot pane runs on its own, by direct
// call on Revit's thread (McpJob.Work), not by tool name through ToolRegistry:
// pdf_open_attachment, dwg_open_attachment, get_dwg_summary, list_cad_links,
// analyze_model_statistics, select_elements.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    public static class PaneTools
    {
        /// <summary>Run <paramref name="work"/> on Revit's thread through
        /// <see cref="BinaVibe.Copilot.Writes.RevitThread"/> and await its result.</summary>
        public static Task<T> RunAsync<T>(Func<UIApplication, T> work)
            => BinaVibe.Copilot.Writes.RevitThread.RunAsync(work, default);

        private static (UIDocument uidoc, Document doc) Active(UIApplication app)
        {
            var uidoc = app.ActiveUIDocument;
            if (uidoc?.Document == null)
                throw new InvalidOperationException("no active document — open a Revit project first");
            return (uidoc, uidoc.Document);
        }

        public static Dictionary<string, object?> ModelStatistics(UIApplication app) =>
            Inspectors.AnalyzeModelStatistics(Active(app).doc);

        public static Dictionary<string, object?> ListCadLinks(UIApplication app) =>
            Inspectors.ListCadLinks(Active(app).doc);

        public static Dictionary<string, object?> SelectElements(UIApplication app, long[] elementIds)
        {
            var args = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["element_ids"] = elementIds });
            return Inspectors.SelectElements(Active(app).uidoc, args);
        }

        public static Dictionary<string, object?> DwgSummary(UIApplication app, Document doc, string dwgRef) =>
            ToolRegistry.WithDwg(app, doc, dwgRef, (d, imp, source) => DwgReader.Summarize(d, imp, dwgRef, source));

        public static Dictionary<string, object?> DwgSummary(UIApplication app, string dwgRef) =>
            DwgSummary(app, Active(app).doc, dwgRef);

        // Attach -> ref + summary in one call, so the composer can embed the
        // summary in the turn without a second round-trip.
        public static Dictionary<string, object?> DwgOpenAttachment(UIApplication app, string path)
        {
            var dwgRef = DwgScratchCache.OpenAttachment(app, path);
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("no active document — open a Revit project first");
            var summary = ToolRegistry.WithDwg(app, doc, dwgRef, (d, imp, source) => DwgReader.Summarize(d, imp, dwgRef, source));

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

        public static Dictionary<string, object?> PdfOpenAttachment(string path)
        {
            var pdfRef = PdfAttachmentCache.OpenAttachment(path);
            return PdfAttachmentCache.Use(pdfRef, doc => PdfReader.Summarize(doc, pdfRef));
        }
    }
}
