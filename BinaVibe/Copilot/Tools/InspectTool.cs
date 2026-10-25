#nullable enable
// Stub created by W0; body owned by W2
// inspect (spec §11 #3): parameters in display units, schedule rows, and why elements are not visible in a view.

using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Tools
{
    public sealed class InspectTool : ICopilotTool
    {
        public string Name => ToolNames.Inspect;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args) => EnvelopeFactory.NotImplemented(Name);
    }
}
