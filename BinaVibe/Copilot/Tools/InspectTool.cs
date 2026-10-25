#nullable enable
// Stub created by W0; body owned by W2
// inspect (spec §11 #3): parameters in display units, schedule rows, and why elements are not visible in a view.

namespace BinaVibe.Copilot.Tools
{
    // Inside the namespace on purpose: BinaVibe.Copilot.Envelope is also a
    // namespace, which hides the Envelope type from usings at the top of a file.
    using System.Text.Json;
    using BinaVibe.Copilot.Contracts;

    public sealed class InspectTool : ICopilotTool
    {
        public string Name => ToolNames.Inspect;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args) => EnvelopeFactory.NotImplemented(Name);
    }
}
