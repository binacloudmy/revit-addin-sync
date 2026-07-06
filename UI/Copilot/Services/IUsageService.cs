using System.Threading.Tasks;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Services
{
    public interface IUsageService
    {
        Task<UsageState> GetAsync();
        /// <summary>Member-plan "Notify admin to upgrade". Stub: no-op.</summary>
        Task NotifyAdminAsync();
    }
}
