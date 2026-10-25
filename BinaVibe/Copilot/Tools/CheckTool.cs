#nullable enable
// Stub created by W0; body owned by W6
// check (spec §11 #4, §21): runs args.rule_set_content (rewritten by the cloud) and returns pass / fail / not_verified.

using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Tools
{
    public sealed class CheckTool : ICopilotTool
    {
        public string Name => ToolNames.Check;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args) => EnvelopeFactory.NotImplemented(Name);
    }
}
