#nullable enable
// model_overview (spec §11 #1): levels, grids, rooms, types, views, sheets, links, worksets, warnings, project.
// Sections come from OverviewService (existing inspectors, cached per document
// state, prewarmed on Idling); OverviewSections narrows by category, cuts at
// limit and says how many more exist.

using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;

namespace BinaVibe.Copilot.Tools
{
    public sealed class ModelOverviewTool : ICopilotTool
    {
        public string Name => ToolNames.ModelOverview;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args)
        {
            if (!OverviewSections.TryParse(args, out var a, out var error)) return ReadEnvelope.InvalidArgs(error);

            var sections = a.Sections.Select(s => OverviewService.Get(ctx.App, ctx.Doc, s, a.Category)).ToList();

            // A types section asked for an unknown category is a not_found, with the real names.
            var types = sections.FirstOrDefault(s => s.Name == "types");
            if (types != null && types.Extra.ContainsKey("candidates") && a.Sections.Count == 1)
                return ReadEnvelope.Fail(ErrorCodes.NotFound,
                    "No category '" + a.Category + "' in this model.",
                    (types.Extra["candidates"] as System.Collections.Generic.IEnumerable<string>) ?? Enumerable.Empty<string>());

            return OverviewSections.Build(ctx.Doc.Title ?? "", sections, a);
        }
    }
}
