// r17 (coordinator, 21 Nov): the "/" list's curated commands come from the backend's catalogue (GET /revit-copilot/commands), so a
// command the backend adds or retires shows without an add-in release; the built-in list stays when the fetch fails. Commands saved
// on this PC stay local (CopilotViewModel.Commands.cs).
using System;
using System.Threading;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot
{
    public partial class CopilotViewModel
    {
        private int _curatedLoading;

        /// <summary>Fetch the curated catalogue once at a time; on any failure the menu keeps what it has (the built-in list at first).</summary>
        public async System.Threading.Tasks.Task RefreshCuratedCatalogAsync()
        {
            if (Interlocked.Exchange(ref _curatedLoading, 1) == 1) return;
            try
            {
                var token = _pane.LoadConfig()?.AccessToken;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var catalog = await new AIService().GetCommandsAsync(token, null, cts.Token).ConfigureAwait(true);
                if (catalog?.Commands == null || catalog.Commands.Count == 0) return;
                ToolCatalog.MergeCurated(catalog.Commands);
                PaletteInvalidated?.Invoke();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] command catalogue: " + ex.Message); }
            finally { Interlocked.Exchange(ref _curatedLoading, 0); }
        }
    }
}
