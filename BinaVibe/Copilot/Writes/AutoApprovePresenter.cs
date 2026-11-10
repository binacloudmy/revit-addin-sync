#nullable enable
using System.Threading;
using System.Threading.Tasks;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Writes
{
    /// <summary>
    /// Approves every write batch without showing a card. The preview still runs
    /// first, so a failed preview still stops the batch; the result card and Undo
    /// are the drafter's safety net.
    /// </summary>
    public sealed class AutoApprovePresenter : IConfirmPresenter
    {
        public Task<ConfirmDecision> AskAsync(ConfirmRequest req, CancellationToken ct) =>
            Task.FromResult(new ConfirmDecision { Approved = true, ApproveThisBuild = false });
    }
}
