#nullable enable
// The text a created element shows in the result card. Revit-free: the Revit-facing code
// gathers plain strings and this decides what to show.

namespace BinaVibe.Copilot.Writes
{
    public static class CreatedNaming
    {
        /// <summary>A sheet shows "number - name" (just one part when the other is empty); any other
        /// element shows its name. Null when there is nothing to show.</summary>
        public static string? Display(bool isSheet, string? number, string? name)
        {
            var n = (number ?? "").Trim();
            var t = (name ?? "").Trim();
            var text = isSheet && n.Length > 0 && t.Length > 0 ? n + " - " + t
                : isSheet && n.Length > 0 ? n
                : t;
            return text.Length == 0 ? null : text;
        }
    }
}
