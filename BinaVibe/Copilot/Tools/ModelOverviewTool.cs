#nullable enable
// Stub created by W0; body owned by W2
// model_overview (spec §11 #1): levels, grids, rooms, types, views, sheets, links, worksets, warnings, project.

using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Tools
{
    public sealed class ModelOverviewTool : ICopilotTool
    {
        public string Name => ToolNames.ModelOverview;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args) => EnvelopeFactory.NotImplemented(Name);
    }
}
