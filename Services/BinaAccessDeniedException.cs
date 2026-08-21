using System;

namespace RevitWebAppSync.Services
{
    /// <summary>
    /// The server answered 403: the signed-in user's role does not reach this
    /// project, folder, model or version.
    ///
    /// Kept distinct from the generic InvalidOperationException the API client
    /// throws for other failures because the two need different words in front
    /// of a drafter. "You do not have access to this folder" sends them to
    /// whoever administers the project; "Could not load models (HTTP 500)" sends
    /// them to us. Showing the second when the first is true wastes both.
    ///
    /// The add-in never decides this itself — it holds no copy of the permission
    /// model and filters nothing. Access is whatever bina-be says it is
    /// (docs/wip-browse-backend-spec.md §3).
    /// </summary>
    public class BinaAccessDeniedException : Exception
    {
        public BinaAccessDeniedException(string message) : base(message) { }

        public BinaAccessDeniedException(string message, Exception inner)
            : base(message, inner) { }
    }
}
