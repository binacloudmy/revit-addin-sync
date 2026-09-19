using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitWebAppSync
{
    /// <summary>
    /// Marks a ribbon button as usable in the zero-document state — the ribbon
    /// a drafter sees after backing out of Revit's Home screen with no model
    /// open. Revit greys out external commands until a document is active
    /// unless the button names an availability class, so this exists for the
    /// commands that never touch the open document: Login to CDE (a browser
    /// round-trip plus config writes) and Download Model (browses the server
    /// and saves to disk; it OPENS a document rather than needing one).
    ///
    /// Buttons whose commands read or write the active document (Sync, Sync
    /// Parameters, Issues, …) must NOT use this — Revit's default gating is
    /// exactly right for them.
    /// </summary>
    public class ZeroDocCommandAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            return true;
        }
    }
}
