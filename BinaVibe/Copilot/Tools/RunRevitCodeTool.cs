#nullable enable
// Stub created by W0; body owned by W4
// run_revit_code (spec §11 #7, §7.8): C# method-body statements against the
// Revit API. Reads run at once inside a rolled-back group; writes go through
// W3's batch runner (preview, confirm card, commit).

namespace BinaVibe.Copilot.Tools
{
    // Inside the namespace on purpose: BinaVibe.Copilot.Envelope is also a
    // namespace, which hides the Envelope type from usings at the top of a file.
    using System;
    using System.Text.Json;
    using BinaVibe.Copilot.Contracts;

    public sealed class RunRevitCodeTool : IWriteTool
    {
        public string Name => ToolNames.RunRevitCode;

        public ToolKind Kind => ToolKind.Code;

        public Envelope Run(ToolContext ctx, JsonElement args) => EnvelopeFactory.NotImplemented(Name);

        public WritePlan Plan(ToolContext ctx, JsonElement args) => throw new NotImplementedException("W4");

        public Envelope Execute(ToolContext ctx, JsonElement args, WritePlan plan, WritePhase phase) =>
            throw new NotImplementedException("W4");
    }
}
