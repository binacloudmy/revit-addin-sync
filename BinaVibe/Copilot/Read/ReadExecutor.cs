#nullable enable
// Stub created by W0; body owned by W2
// Runs a protocol-3 read request on Revit's thread (via McpJobPump) and returns
// one envelope per call, in call order.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public sealed class ReadExecutor : IReadExecutor
    {
        public ReadExecutor(IHandleStore handles)
        {
        }

        public Task<Envelope[]> RunAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct) =>
            throw new NotImplementedException("W2");
    }
}
