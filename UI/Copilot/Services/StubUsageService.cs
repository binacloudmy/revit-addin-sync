using System.Threading.Tasks;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Services
{
    /// <summary>Configurable stand-in until billing lands. Swap via CopilotViewModel.UsageService.</summary>
    public class StubUsageService : IUsageService
    {
        private readonly UsageState _state;

        public StubUsageService(string planName = "Free", int pct = 88, bool atLimit = false, bool isAdmin = true)
            => _state = new UsageState { PlanName = planName, Pct = pct, AtLimit = atLimit, IsAdmin = isAdmin };

        public Task<UsageState> GetAsync() => Task.FromResult(_state);
        public Task NotifyAdminAsync() => Task.CompletedTask;
    }
}
