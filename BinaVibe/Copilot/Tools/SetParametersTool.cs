#nullable enable
// Stub created by W0; body owned by W3
// set_parameters (spec §11 #5): one parameter on many elements, previewed,
// approved in the confirm card, committed and read back. Targets come from a
// filter, a handle, ids, or args.table_rows (rewritten by the cloud from
// from_table).

namespace BinaVibe.Copilot.Tools
{
    // Inside the namespace on purpose: BinaVibe.Copilot.Envelope is also a
    // namespace, which hides the Envelope type from usings at the top of a file.
    using System;
    using System.Text.Json;
    using BinaVibe.Copilot.Contracts;

    public sealed class SetParametersTool : IWriteTool
    {
        public string Name => ToolNames.SetParameters;

        public ToolKind Kind => ToolKind.Write;

        public Envelope Run(ToolContext ctx, JsonElement args) => EnvelopeFactory.NotImplemented(Name);

        public WritePlan Plan(ToolContext ctx, JsonElement args) => throw new NotImplementedException("W3");

        public Envelope Execute(ToolContext ctx, JsonElement args, WritePlan plan, WritePhase phase) =>
            throw new NotImplementedException("W3");
    }
}
