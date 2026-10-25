#nullable enable
// Stub created by W0; body owned by W3
// set_parameters (spec §11 #5): one parameter on many elements, previewed,
// approved in the confirm card, committed and read back. Targets come from a
// filter, a handle, ids, or args.table_rows (rewritten by the cloud from
// from_table).

using System;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Tools
{
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
