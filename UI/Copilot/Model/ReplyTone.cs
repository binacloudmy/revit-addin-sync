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
            var any = false;
            foreach (var kv in values)
            {
                var v = kv.Value;
                if (v.ValueKind == System.Text.Json.JsonValueKind.False) continue;
                if (v.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    // an option card's pick: a way out only when its option is Skip/Cancel
                    if (!(kv.Key == WideWriteModel.WriteCancelId || kv.Key == "force_cancel"
                          || ask.Options.Any(o => o.Id == kv.Key && (o.IsSkip || AskCardModel.IsDeclineLike(o.Label))))) return false;
                    any = true;
                    continue;
                }
                if (v.ValueKind != System.Text.Json.JsonValueKind.String) return false;   // a number typed into a field
                var text = (v.GetString() ?? "").Trim();
                if (text.Length == 0) continue;                                          // "Don't answer", an empty field
                // P7 9a2a937e: a form field's pick comes back as its choice id ({"tag type": "Skip"}); a multi field joins its ticks with " | "
                var field = ask.Fields.FirstOrDefault(f => f.Id == kv.Key);
                if (field == null) return false;                                         // typed text ("text"): an answer, not a way out
                var ids = field.Multi ? text.Split(new[] { " | " }, System.StringSplitOptions.RemoveEmptyEntries) : new[] { text };
                if (!ids.All(id => field.Choices.Any(c => c.Id == id && (c.IsSkip || AskCardModel.IsDeclineLike(c.Label))))) return false;
                any = true;
            }
            return any || values.Count == 0 || values.Values.All(v => v.ValueKind == System.Text.Json.JsonValueKind.String || v.ValueKind == System.Text.Json.JsonValueKind.False);
        }

        /// <summary>The reply wrote nothing because typed text at a write card was refused: the turn's write card was answered by typing
        /// and no write of the turn committed.</summary>
        public static bool TypedRefusal(bool typedAtWriteCard, IEnumerable<Envelope?>? envelopes) =>
            typedAtWriteCard && !(envelopes ?? Enumerable.Empty<Envelope?>()).Any(e => e != null && e.Ok && e.Phase == EnvelopePhase.Commit);
    }
}
