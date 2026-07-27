using System.Threading.Tasks;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Services
{
    /// <summary>Configurable stand-in until billing lands. Swap via CopilotViewModel.UsageService.</summary>
    public class StubUsageService : IUsageService
    {
        private readonly UsageState _state;

        public StubUsageService(string planName = "Free", int pct = 88, bool atLimit = false, bool isAdmin = true,
            string resetsAt = null, bool unlimited = false)
            => _state = new UsageState
            {
                PlanName = planName, Pct = pct, AtLimit = atLimit, IsAdmin = isAdmin,
                // Lets the harness exercise the popover's reset line and the
                // ring's hidden (uncapped-wallet) state, not just the percent ramp.
                ResetsAt = resetsAt, Unlimited = unlimited,
            };

        public Task<UsageState> GetAsync() => Task.FromResult(_state);
        public Task NotifyAdminAsync() => Task.CompletedTask;
    }
}
