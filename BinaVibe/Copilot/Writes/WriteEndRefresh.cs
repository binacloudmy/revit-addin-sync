#nullable enable
// r23 (0.0.171), live PC P16 G3 7241e714 on 0.0.169: after a committed own-code write emptied the selection, Revit's ribbon stayed on
// "Modify | Ducts" with greyed tools and Manage > Select by ID disabled until a canvas click and Esc. The selection was changed through
// the API while the keyboard was in the Copilot pane, and Revit did not refresh its command state for a view that was not active (the
// same stale UI ViewActivation fixes after a find). At the end of every write request, commit or not, the engine now activates the view
// the same way (ViewActivation.Show: the active view's window gets the activation; no view change, no selection change), after the
// AI-456 give-back, and only when the document is still valid and no transaction is open. Revit-free rule so it is tested.

namespace BinaVibe.Copilot.Writes
{
    public static class WriteEndRefresh
    {
        /// <summary>Refresh Revit's command state at the end of a write request: there is an open, valid document and nothing is
        /// being changed in it (no transaction or group left open).</summary>
        public static bool Applies(bool hasDocument, bool documentValid, bool transactionOpen) =>
            hasDocument && documentValid && !transactionOpen;
    }
}
