using System.Threading.Tasks;
using RevitWebAppSync.Models;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// Produces the run data that drives the JKR Audit Copilot. The offline
    /// <see cref="FixtureCopilotSource"/> mirrors the design constants so the
    /// whole S1&rarr;S6 flow is testable without a live backend; a later card
    /// wires this to the actual audit backend.
    /// </summary>
    public interface IJkrCopilotSource
    {
        Task<JkrCopilotRunData> LoadRunAsync(PanelRunRequest request);
    }
}