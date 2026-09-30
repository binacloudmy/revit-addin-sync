using System;
using System.Threading.Tasks;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// "Send diagnostics" for every place that offers it (Copilot kebab menu,
    /// the forced-update window): upload the diagnostics zip and turn the
    /// outcome into one message the drafter can read out to support.
    /// Never throws.
    /// </summary>
    internal static class DiagnosticsAction
    {
        private static int _busy;

        public static async Task<string> SendAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1)
                return "Diagnostics are already being sent.";
            try
            {
                var id = await DiagnosticsUploader.SendAsync();
                return $"Diagnostics sent to BINA support.\n\nReference: {id}\n\nQuote this reference when you contact support.";
            }
            catch (Exception ex)
            {
                return $"Could not send diagnostics: {ex.Message}\n\nCheck the internet connection and try again.";
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _busy, 0);
            }
        }
    }
}
