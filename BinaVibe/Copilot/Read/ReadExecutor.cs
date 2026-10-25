#nullable enable
// Stub created by W0; body owned by W2
// Runs a protocol-3 read request on Revit's thread (via McpJobPump) and returns
// one envelope per call, in call order.

namespace BinaVibe.Copilot.Read
{
    // Inside the namespace on purpose: BinaVibe.Copilot.Envelope is also a
    // namespace, which hides the Envelope type from usings at the top of a file.
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using BinaVibe.Copilot.Contracts;

    public sealed class ReadExecutor : IReadExecutor
    {
        public ReadExecutor(IHandleStore handles)
        {
        }

        public Task<Envelope[]> RunAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct) =>
            throw new NotImplementedException("W2");
    }
}
