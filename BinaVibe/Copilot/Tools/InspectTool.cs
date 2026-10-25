#nullable enable
// Skeleton by W0; body owned by W2
// inspect (spec §11 #3): parameters in display units, schedule rows, and why elements are not visible in a view.
// InspectArgs (Revit-free) enforces "target or schedule" (ruling R16),
// InspectExecutor reads, InspectResultBuilder (Revit-free) shapes.

using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;

namespace BinaVibe.Copilot.Tools
{
    public sealed class InspectTool : ICopilotTool
    {
        public string Name => ToolNames.Inspect;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args)
        {
            if (!InspectArgs.TryParse(args, out var a, out var error)) return ReadEnvelope.InvalidArgs(error);
            return InspectExecutor.Run(ctx, a);
        }
    }
}
