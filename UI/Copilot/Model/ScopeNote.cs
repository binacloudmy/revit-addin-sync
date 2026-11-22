#nullable enable
// The footnote that says what a turn did not look at (UX review Q1/B2, spec 3.1): "Not searched: link A (unloaded); link B (unloaded)". The backend
// keeps the same words out of its headlines once the pane draws this line (be dev, 18 Nov). Pure.
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ScopeNote
    {
        /// <summary>At most this many items are named; the rest is "+ N more".</summary>
        public const int MaxNamed = 3;

        public static string Line(IReadOnlyList<string>? notSearched, ReceiptLanguage lang)
        {
            var items = (notSearched ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            if (items.Count == 0) return "";
            var ms = lang == ReceiptLanguage.Ms;
            var named = string.Join("; ", items.Take(MaxNamed));
            var more = items.Count > MaxNamed ? (ms ? " dan " + (items.Count - MaxNamed) + " lagi" : " and " + (items.Count - MaxNamed) + " more") : "";
            return (ms ? "Tidak dicari: " : "Not searched: ") + named + more;
        }
    }
}
