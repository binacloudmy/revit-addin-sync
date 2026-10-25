#nullable enable
// Stub created by W0; body owned by W2
// find_elements (spec §11 #2): 1-5 queries over host and links, counts, groups, handle, highlight; always says what was searched.

using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Tools
{
    public sealed class FindElementsTool : ICopilotTool
    {
        public string Name => ToolNames.FindElements;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args) => EnvelopeFactory.NotImplemented(Name);
    }
}
