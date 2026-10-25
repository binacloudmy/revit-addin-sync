#nullable enable
// Stub created by W0; body owned by W2
// find_elements (spec §11 #2): 1-5 queries over host and links, counts, groups, handle, highlight; always says what was searched.

namespace BinaVibe.Copilot.Tools
{
    // Inside the namespace on purpose: BinaVibe.Copilot.Envelope is also a
    // namespace, which hides the Envelope type from usings at the top of a file.
    using System.Text.Json;
    using BinaVibe.Copilot.Contracts;

    public sealed class FindElementsTool : ICopilotTool
    {
        public string Name => ToolNames.FindElements;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args) => EnvelopeFactory.NotImplemented(Name);
    }
}
