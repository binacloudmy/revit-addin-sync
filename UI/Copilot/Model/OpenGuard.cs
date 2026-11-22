#nullable enable
// Before the pane asks Revit to open a sheet or a view from an id in a result row: the checks that need no Revit
// API, so a test pins them. The server only knows that the id appeared in a tool result; whether it is still the
// right kind of element, in the model that is open now, is the pane's to check.
namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>What an element id turned out to be in the open model.</summary>
    public enum ItemClass
    {
        /// <summary>Not looked up (or not in the model that is open): no button.</summary>
        Missing,
        Sheet,
        View,
        /// <summary>A view template: not a view to open.</summary>
        Template,
        /// <summary>Any other element.</summary>
        Other,
    }

    public static class OpenGuard
    {
        /// <summary>May this row's button be offered for an element of this class? A sheet button needs a sheet, a view
        /// button a view that is not a template, a select button an element that exists.</summary>
        public static bool Allows(ItemButton button, ItemClass found) =>
            button == ItemButton.OpenSheet ? found == ItemClass.Sheet
            : button == ItemButton.OpenView ? found == ItemClass.View
            : button == ItemButton.Select && found != ItemClass.Missing;

        /// <summary>Why the pane will not open it, or null when it may.</summary>
        /// <param name="expectedDocKey">The model the result came from (null when not known: not checked).</param>
        /// <param name="activeDocKey">The model that is active now.</param>
        /// <param name="wantSheet">The row is a sheet (else a view).</param>
        public static string? Why(string? expectedDocKey, string? activeDocKey, bool wantSheet, bool found, bool isSheet, bool isView, bool isTemplate, ReceiptLanguage lang = ReceiptLanguage.En)
        {
            var ms = lang == ReceiptLanguage.Ms;
            if (!string.IsNullOrEmpty(expectedDocKey) && !string.Equals(expectedDocKey, activeDocKey, System.StringComparison.Ordinal))
                return ms ? "keputusan ini datang daripada model lain: tukar balik kepadanya dahulu" : "this result is from another model: switch back to it first";
            if (!found) return ms ? "sheet atau view itu tiada dalam model lagi" : "that sheet or view is not in the model any more";
            if (wantSheet && !isSheet) return ms ? "id itu bukan sheet" : "that id is not a sheet";
            if (!wantSheet && (!isView || isSheet || isTemplate)) return ms ? "id itu bukan view" : "that id is not a view";
            return null;
        }
    }
}
