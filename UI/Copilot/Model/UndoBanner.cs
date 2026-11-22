#nullable enable
// Board 3: the banner at the top of a result card whose changes were undone. Words only; the card
// draws it. Pure, so a test pins them.
using System.Globalization;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class UndoBanner
    {
        /// <summary>"Undone. The model is back to how it was." and what was removed, in one line.</summary>
        public static (string Title, string Detail) For(ResultCardModel? card, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            string N(int n) => n.ToString(CultureInfo.InvariantCulture);
            var title = ms ? "Dibuat asal. Model kembali seperti asal." : "Undone. The model is back to how it was.";
            var nothing = ms ? "Tiada yang lain berubah." : "Nothing else changed.";
            if (card == null) return (title, nothing);

            if (card.Kind == ResultKind.Changes && card.ChangeCount > 0)
            {
                var n = card.ChangeCount;
                var live = card.Groups.FindAll(g => !g.ChangedBack);
                var types = live.FindAll(g => g.IsType).Count;
                var what = types > 0
                    ? (ms ? N(types) + " jenis" : N(types) + (types == 1 ? " type" : " types"))
                    : (ms ? N(card.InstanceCount) + " elemen" : N(card.InstanceCount) + (card.InstanceCount == 1 ? " element" : " elements"));
                // The values are restored, not deleted: they hold what they held before.
                var removed = ms
                    ? N(n) + " nilai dikembalikan seperti asal pada " + what + ". " + nothing
                    : N(n) + (n == 1 ? " value" : " values") + " put back to what " + (n == 1 ? "it was" : "they were") + " on " + what + ". " + nothing;
                return (title, removed);
            }
            if (card.Kind == ResultKind.Created && card.Created.Count > 0)
            {
                var what = card.CreatedBy?.Headline(lang) ?? N(card.Created.Count);
                return (title, (ms ? what + " dibuang. " : what + " removed. ") + nothing);
            }
            return (title, nothing);
        }
    }
}
