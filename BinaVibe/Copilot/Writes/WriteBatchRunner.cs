#nullable enable
// Stub created by W0; body owned by W3
// Runs a protocol-3 write request (spec §7.4): fingerprint and ownership
// checks, plan + plan hash, preview (rolled back), the confirm card, commit in
// one named TransactionGroup, shape check, ledger. One envelope per call.

using System;
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public sealed class WriteBatchRunner : IWriteBatchRunner
    {
        public WriteBatchRunner(IConfirmPresenter presenter, IHandleStore handles)
        {
        }

        public Task<Envelope[]> RunAsync(WriteBatchRequest req, CancellationToken ct) => throw new NotImplementedException("W3");
    }
}
