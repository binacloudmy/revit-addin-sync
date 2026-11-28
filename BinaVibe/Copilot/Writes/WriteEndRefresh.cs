#nullable enable
// r23 (0.0.171), live PC P16 G3 7241e714 on 0.0.169: after a committed own-code write emptied the selection, Revit's ribbon stayed on
// "Modify | Ducts" with greyed tools and Manage > Select by ID disabled until a canvas click and Esc. The selection was changed through
// the API while the keyboard was in the Copilot pane, and Revit did not refresh its command state for a view that was not active (the
// same stale UI ViewActivation fixes after a find). At the end of every write request, commit or not, the engine now activates the view
// the same way (ViewActivation.Show: the active view's window gets the activation; no view change, no selection change), after the
// AI-456 give-back, and only when the document is still valid and no transaction is open. Revit-free rule so it is tested.
// Audit of b3e3c99b: the activation holds the keyboard about a second, so it runs only for a request during which Revit's selection
// actually changed (the live case: the commit emptied it), and at most once per turn (a drafter typing the next step keeps the keys).

namespace BinaVibe.Copilot.Writes
{
    public static class WriteEndRefresh
    {
        /// <summary>Refresh Revit's command state at the end of a write request: there is an open, valid document and nothing is
        /// being changed in it (no transaction or group left open).</summary>
        public static bool Applies(bool hasDocument, bool documentValid, bool transactionOpen) =>
            hasDocument && documentValid && !transactionOpen;

        /// <summary>The full rule: the document test above, Revit's selection differs between the start and the end of the request
        /// (as sets), and this turn has not been refreshed yet.</summary>
        public static bool Applies(bool hasDocument, bool documentValid, bool transactionOpen,
            System.Collections.Generic.IEnumerable<long>? selectionBefore, System.Collections.Generic.IEnumerable<long>? selectionAfter,
            string? turnId, string? refreshedTurn)
        {
            if (!Applies(hasDocument, documentValid, transactionOpen)) return false;
            var before = new System.Collections.Generic.HashSet<long>(selectionBefore ?? System.Linq.Enumerable.Empty<long>());
            if (before.SetEquals(selectionAfter ?? System.Linq.Enumerable.Empty<long>())) return false;
            return string.IsNullOrEmpty(turnId) || !string.Equals(turnId, refreshedTurn, System.StringComparison.Ordinal);
        }
    }
}
