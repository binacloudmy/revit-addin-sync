#nullable enable
// Stub created by W0; body owned by W6
// check (spec §11 #4, §21): runs args.rule_set_content (rewritten by the cloud) and returns pass / fail / not_verified.

namespace BinaVibe.Copilot.Tools
{
    // Inside the namespace on purpose: BinaVibe.Copilot.Envelope is also a
    // namespace, which hides the Envelope type from usings at the top of a file.
    using System.Text.Json;
    using BinaVibe.Copilot.Contracts;

    public sealed class CheckTool : ICopilotTool
    {
        public string Name => ToolNames.Check;

        public ToolKind Kind => ToolKind.Read;

        public Envelope Run(ToolContext ctx, JsonElement args) => EnvelopeFactory.NotImplemented(Name);
    }
}
