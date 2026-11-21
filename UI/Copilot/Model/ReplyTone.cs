#nullable enable
// r22 (PC round P5, turn 6bf0f4ec): a turn whose write card was answered by typing ("write it") wrote nothing: the server refuses typed
// text as an approval. Its reply ("Typed text isn't an approval, so nothing was written…") is no success, so its headline gets the
// neutral info disc, not the green check. Every other reply keeps its icon. WPF-free so it is unit-tested.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public static class ReplyTone
    {
        /// <summary>The info disc: a neutral fill, an "i" in a muted stroke (the calm "Notes" look), never the green check.</summary>
        public const string InfoDiscFill = "Cp.Hair2";
        public const string InfoGlyph = "M11,10 V15.5 M11,6.6 V6.7";
        public const string InfoGlyphStroke = "Cp.Muted";

        /// <summary>r22 (laptop L8 R1 959b3001): the card was answered with only a way out: Skip / Langkau (an option of kind skip), Cancel
        /// (write_cancel, force_cancel or a decline label), or "Don't answer" (an empty answer). Its reply, if nothing then committed, is no success.</summary>
        public static bool Declines(AskCardModel? ask, AnswerDto? answer)
        {
            if (ask == null || answer == null) return false;
            var values = answer.Values ?? new Dictionary<string, System.Text.Json.JsonElement>();
            var picked = values.Where(kv => kv.Value.ValueKind == System.Text.Json.JsonValueKind.True).Select(kv => kv.Key).ToList();
            var typed = values.Where(kv => kv.Value.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(kv.Value.GetString())).ToList();
            if (typed.Count > 0) return false;
            if (picked.Count == 0) return values.Count == 0 || values.All(kv => kv.Value.ValueKind == System.Text.Json.JsonValueKind.String || kv.Value.ValueKind == System.Text.Json.JsonValueKind.False);   // "Don't answer"
            return picked.All(id => id == WideWriteModel.WriteCancelId || id == "force_cancel"
                                    || ask.Options.Any(o => o.Id == id && (o.IsSkip || AskCardModel.IsDeclineLike(o.Label))));
        }

        /// <summary>The reply wrote nothing because typed text at a write card was refused: the turn's write card was answered by typing
        /// and no write of the turn committed.</summary>
        public static bool TypedRefusal(bool typedAtWriteCard, IEnumerable<Envelope?>? envelopes) =>
            typedAtWriteCard && !(envelopes ?? Enumerable.Empty<Envelope?>()).Any(e => e != null && e.Ok && e.Phase == EnvelopePhase.Commit);
    }
}
