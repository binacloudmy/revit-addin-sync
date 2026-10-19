using System;
using System.Threading.Tasks;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// AI-59: prepare the Autodesk viewer URN as a sync's final step, using the
    /// CURRENT BINA Cloud token and reporting a failure instead of dropping it.
    ///
    /// A long upload can outlive its token: the sync client refreshes its bearer
    /// on a 401 mid-sync, so the token captured before the dialog opened is
    /// stale by the time this runs. That stale token got a 401, so every version
    /// was saved without a viewer URN and the user was never told.
    ///
    /// Revit-free and dependency-free, so the token hand-off is unit-tested.
    /// </summary>
    public static class ViewerStep
    {
        internal const string FailedMessage =
            "The model is saved, but its 3D viewer could not be prepared. Open it once in BINA Cloud, or re-sync.";

        /// <param name="currentToken">Mints or returns the token to use right now.</param>
        /// <param name="upload">
        /// Given that token, returns the viewer URN (base64), or null/empty on failure.
        /// </param>
        public static async Task<(string Urn, string Message)> PrepareAsync(
            Func<Task<string>> currentToken,
            Func<string, Task<string>> upload)
        {
            try
            {
                string token = currentToken == null ? null : await currentToken().ConfigureAwait(false);
                if (string.IsNullOrEmpty(token)) return (null, FailedMessage);

                string urn = await upload(token).ConfigureAwait(false);
                return string.IsNullOrEmpty(urn) ? (null, FailedMessage) : (urn, null);
            }
            catch
            {
                // Best-effort: a viewer that cannot be prepared never fails the
                // sync, which is about the model bytes — but it is no longer silent.
                return (null, FailedMessage);
            }
        }
    }
}
