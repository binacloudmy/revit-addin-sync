#nullable enable
// read_dwg_plan (AI-607): plans, walls, doors and windows of the DWG attached to the chat.
// Read-only: it resolves the dwg_ref to the file on this PC and hands the path to
// DwgPlanReader (ACadSharp, Revit-free). No transaction, nothing in Undo, and the
// open model is never needed for an attachment ("att:" ref).

using System;
using System.Text.Json;
using Autodesk.Revit.DB;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;
using BinaVibe.Mcp.Tools;
using RevitWebAppSync.Services.CadToBim;

namespace BinaVibe.Copilot.Tools
{
    public sealed class ReadDwgPlanTool : ICopilotTool
    {
        public string Name => ToolNames.ReadDwgPlan;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args)
        {
            if (!DwgPlanReader.TryParseArgs(args, out var a, out var error)) return ReadEnvelope.InvalidArgs(error!);
            if (string.IsNullOrEmpty(a.DwgRef)) return DwgPlanReader.NoDwg();

            string? path;
            if (DwgScratchCache.IsAttachmentRef(a.DwgRef!))
            {
                path = DwgScratchCache.GetPath(a.DwgRef!);
                if (path == null)
                    return ReadEnvelope.Fail(ErrorCodes.NotFound,
                        "The attachment " + a.DwgRef + " is no longer open. Ask the user to attach the DWG again.", Array.Empty<string>());
            }
            else
            {
                path = LinkedPath(ctx.Doc, a.DwgRef!, out var why);
                if (path == null) return ReadEnvelope.Fail(ErrorCodes.NotFound, why, Array.Empty<string>());
            }

            if (!System.IO.File.Exists(path))
                return ReadEnvelope.Fail(ErrorCodes.NotFound, "The DWG file is no longer on disk: " + System.IO.Path.GetFileName(path) + ". Ask the user to attach it again.", Array.Empty<string>());

            try
            {
                return DwgPlanReader.Build(DwgPlanReader.ReadFile(path, CadToBimSettings.Load()), a.Plans);
            }
            catch (NotSupportedException ex)
            {
                return ReadEnvelope.Fail(ErrorCodes.NotAllowed, ex.Message);
            }
        }

        /// <summary>File behind a "model:&lt;id&gt;" ref: the drawing is already linked in the open model.</summary>
        private static string? LinkedPath(Document? doc, string dwgRef, out string why)
        {
            why = DwgPlanReader.ModelRefProblem(dwgRef, doc != null, out var id) ?? "";
            if (why.Length > 0 || doc == null) return null;
            if (doc.GetElement(ElemIds.From(id)) is not ImportInstance imp)
            {
                why = "No CAD link with id " + id + " in this model. Ask the user to attach the DWG.";
                return null;
            }
            try
            {
                var reference = ExternalFileUtils.GetExternalFileReference(doc, imp.GetTypeId());
                if (reference != null)
                    return ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
            }
            catch (Exception) { /* an import, not a link: no file to read */ }
            why = "That CAD is imported into the model, not linked, so there is no file to read. Ask the user to attach the DWG.";
            return null;
        }
    }
}
