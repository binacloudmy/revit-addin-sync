using System.Threading.Tasks;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Services
{
    /// <summary>Configurable stand-in until billing lands. Swap via CopilotViewModel.UsageService.</summary>
    public class StubUsageService : IUsageService
    {
        private readonly UsageState _state;

        public StubUsageService(string planName = "Free", int pct = 88, bool atLimit = false, bool isAdmin = true,
            string resetsAt = null, bool unlimited = false, int limit = 1000)
            => _state = new UsageState
            {
                PlanName = planName, Pct = pct, AtLimit = atLimit, IsAdmin = isAdmin,
                // Lets the harness exercise the popover's reset line and the
                // uncapped-wallet state, not just the percent ramp.
                ResetsAt = resetsAt, Unlimited = unlimited, Limit = limit,
                // A stub caller NAMES the plan, so the tier pill is entitled to show.
                // (Against the real backend PlanKnown is false — /credits/balance
                // carries no "plan" — and the pill correctly stays hidden.)
                PlanKnown = !string.IsNullOrWhiteSpace(planName),
            };

        public Task<UsageState> GetAsync() => Task.FromResult(_state);
        public Task NotifyAdminAsync() => Task.CompletedTask;
    }
}
