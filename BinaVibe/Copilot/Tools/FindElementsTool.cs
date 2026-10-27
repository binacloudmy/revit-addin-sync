#nullable enable
// find_elements (spec §11 #2): 1-5 queries over host and links, counts, groups, handle, highlight; always says what was searched.
// QueryParser (Revit-free) turns the args into a plan, FindExecutor collects,
// FindResultBuilder (Revit-free) shapes the envelope.

using System.Text.Json;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Read;

namespace BinaVibe.Copilot.Tools
{
    public sealed class FindElementsTool : ICopilotTool
    {
        public string Name => ToolNames.FindElements;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args)
        {
            if (!QueryParser.TryParse(args, out var plan, out var error)) return ReadEnvelope.InvalidArgs(error);
            return FindExecutor.Run(ctx, plan);
        }
    }
}
