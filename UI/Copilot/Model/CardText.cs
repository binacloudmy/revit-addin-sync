#nullable enable
// Small pure text rules of the result card.
namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class CardText
    {
        /// <summary>The headline, cut at a word with "…" past <paramref name="max"/> characters (a safety net: the server caps
        /// its own sentence; the full text is the headline's tooltip).</summary>
        public static string Clamp(string? text, int max = 160)
        {
            var t = (text ?? "").Trim();
            if (t.Length <= max) return t;
            var space = t.LastIndexOf(' ', max - 1);
            return t.Substring(0, space > max / 2 ? space : max - 1).TrimEnd(',', ';', ':', ' ') + "…";
        }
    }

    /// <summary>The in-canvas headline toast ("Set Comments = ... fades in 8 s, Undo") shows only while the pane is closed: with
    /// the pane open the result card says the same, with its own Undo all.</summary>
    public static class ToastPolicy
    {
        public static bool Show(bool? paneOpen) => paneOpen != true;
    }
}
