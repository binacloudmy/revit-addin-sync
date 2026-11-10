#nullable enable
// The add-in's WriteBatchRunner (W0 stub signature; body owned by W3): the
// Revit write engine, the Revit fingerprint provider, the shared commit
// ledger and the shared "Approve this build" scope. The orchestration itself
// is Revit-free, in WriteBatchRunner.cs.

using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    public sealed partial class WriteBatchRunner
    {
        public WriteBatchRunner(IConfirmPresenter presenter, IHandleStore handles)
            : this(presenter, new RevitWriteEngine(handles), new RevitDocIdentityProvider(), OperationLedgerCommits.Shared, ApprovalScope.Shared)
        {
        }
    }
}
