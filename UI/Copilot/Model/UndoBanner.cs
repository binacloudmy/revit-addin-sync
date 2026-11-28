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
            // r13: said only when the Undo put back exactly what the card lists; an Undo that also brought deleted elements back,
            // or a card that lists nothing, does not claim it.
            var nothing = card != null && card.UndoRestoresExactly ? (ms ? " Tiada yang lain berubah." : " Nothing else changed.") : "";
            if (card == null) return (title, "");
            // r14: Revit's own changes beside the write (fittings added, the run adjusted) are reverted by the same Undo: said, never hidden.
            var rippled = card.Rippled > 0
                ? (ms ? " Perubahan Revit sendiri di sekitarnya (" + N(card.Rippled) + " elemen) turut dibuat asal."
                      : " Revit's own changes around it (" + N(card.Rippled) + (card.Rippled == 1 ? " element" : " elements") + ") were undone too.")
                : "";
            nothing = rippled + nothing;

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
                    ? N(n) + " nilai dikembalikan seperti asal pada " + what + "." + nothing
                    : N(n) + (n == 1 ? " value" : " values") + " put back to what " + (n == 1 ? "it was" : "they were") + " on " + what + "." + nothing;
                return (title, removed);
            }
            if (card.Kind == ResultKind.Created && card.Created.Count > 0)
            {
                var what = card.CreatedBy?.Headline(lang) ?? N(card.Created.Count);
                if (ms) what = StepDetailText.MalayNames(what);  // r17: "1 jadual dibuang.", never "1 Schedule" in a Malay turn (live 646bdf7f)
                return (title, (ms ? what + " dibuang." : what + " removed.") + nothing);
            }
            return (title, "");
        }
    }
}
