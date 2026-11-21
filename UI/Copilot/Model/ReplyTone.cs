#nullable enable
// r22 (PC round P5, turn 6bf0f4ec): a turn whose write card was answered by typing ("write it") wrote nothing: the server refuses typed
// text as an approval. Its reply ("Typed text isn't an approval, so nothing was written…") is no success, so its headline gets the
// neutral info disc, not the green check. r23: also a turn whose server result made or changed nothing (NothingMade), and a tool turn's short reply always has a disc
// (NeedsHeadline). WPF-free so it is unit-tested.

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

        /// <summary>r23 (live PC f85dd8f6 "Nothing was changed." with {skipped 1, findings 0}; laptop b372f902 "No circuits made." with
        /// {values 0, written 0, elements 0}; both drew the green check): the server's result says the turn made or changed nothing. True
        /// when no write of the turn committed a change, the result gives no positive count of something made, changed, selected or found,
        /// and either something was skipped or failed, or a changes/created result counts all zeros. A read that answered (a find of N,
        /// a check with findings 0 and nothing skipped) is not this: it keeps the green check.</summary>
        public static bool NothingMade(TurnResultDto? result, IEnumerable<Envelope?>? envelopes)
        {
            if (result == null) return false;
            if ((envelopes ?? Enumerable.Empty<Envelope?>()).Any(e => e != null && e.Ok && e.Phase == EnvelopePhase.Commit && e.Status != EnvelopeStatus.Noop)) return false;
            var c = result.Counts;
            if (c == null) return false;
            if (new[] { c.Elements, c.Types, c.Values, c.Written, c.Created, c.Reverted, c.Findings }.Any(n => (n ?? 0) > 0)) return false;
            if ((c.Skipped ?? 0) > 0 || (c.Failed ?? 0) > 0) return true;
            var writeKind = result.Kind == TurnResultKinds.Changes || result.Kind == TurnResultKinds.Created;
            return writeKind && TurnEvidence.CountsAllZero(c);
        }

        /// <summary>r23 (live PC P13 C6 44c1f9e6, "Muatkan semula semua pautan…": both reload runs, run_revit_code with expected modifies 6,
        /// were refused before Revit saw them, and the Malay reply "Maaf, tiada pautan dimuatkan…" drew the green check): the turn tried to
        /// change the model (a set_parameters call, or a run_revit_code that declares changes) and every such attempt came back refused or
        /// failed, with nothing committed. A read that answered beside them does not make it a success: the change asked for was not made.</summary>
        public static bool RefusedOnly(IEnumerable<(ToolCallDto Call, Envelope Env)>? calls)
        {
            var list = (calls ?? Enumerable.Empty<(ToolCallDto Call, Envelope Env)>()).ToList();
            if (list.Any(c => c.Env != null && c.Env.Ok && c.Env.Phase == EnvelopePhase.Commit && c.Env.Status != EnvelopeStatus.Noop)) return false;
            var attempts = list.Where(c => c.Call != null && (c.Call.Tool == ToolNames.SetParameters || TurnEvidence.DeclaresChange(c.Call))).ToList();
            return attempts.Count > 0 && attempts.All(c => c.Env == null || !c.Env.Ok);
        }

        /// <summary>r23 (live laptop f7d1c886, wall-by-points Skip: "Skipped, so no wall was made…" drew NO icon): a short reply of a
        /// turn that wrote nothing (a Skip, a Cancel, a refused typed answer, a server result that made nothing) gets the headline row,
        /// and so the neutral "i". Only those: audit of c848c5f9, a tool turn's other short replies ("I couldn't find a wall type named
        /// X") must not newly draw a green check, so they keep their markdown as before. A question back ("Which level do you mean?")
        /// never gets a disc; nor a table, a list or code.</summary>
        public static bool NeedsHeadline(bool aiReply, bool cardShown, bool nothingWritten, string? text) =>
            aiReply && !cardShown && nothingWritten && ProseSplit.IsPlainProse(text, 1) && !IsQuestion(text);

        /// <summary>The reply ends by asking (its last character is "?"): a question back, not a result.</summary>
        public static bool IsQuestion(string? text) => (text ?? "").TrimEnd().EndsWith("?", System.StringComparison.Ordinal);

        /// <summary>The reply wrote nothing because typed text at a write card was refused: the turn's write card was answered by typing
        /// and no write of the turn committed.</summary>
        public static bool TypedRefusal(bool typedAtWriteCard, IEnumerable<Envelope?>? envelopes) =>
            typedAtWriteCard && !(envelopes ?? Enumerable.Empty<Envelope?>()).Any(e => e != null && e.Ok && e.Phase == EnvelopePhase.Commit);
    }
}
