#nullable enable
// Stub created by W0; body owned by W3
// Runs a protocol-3 write request (spec §7.4): fingerprint and ownership
// checks, plan + plan hash, preview (rolled back), the confirm card, commit in
// one named TransactionGroup, shape check, ledger. One envelope per call.

namespace BinaVibe.Copilot.Writes
{
    // Inside the namespace on purpose: BinaVibe.Copilot.Envelope is also a
    // namespace, which hides the Envelope type from usings at the top of a file.
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using BinaVibe.Copilot.Contracts;

    public sealed class WriteBatchRunner : IWriteBatchRunner
    {
        public WriteBatchRunner(IConfirmPresenter presenter, IHandleStore handles)
        {
        }

        public Task<Envelope[]> RunAsync(WriteBatchRequest req, CancellationToken ct) => throw new NotImplementedException("W3");
    }
}
