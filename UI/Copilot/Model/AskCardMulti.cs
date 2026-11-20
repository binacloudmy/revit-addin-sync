#nullable enable
// r13 (0.0.147): a multi choice looks multi. Live turn 951374ce on 0.0.146: the fill form's type field had "multi": true but
// drew the same numbered tiles with a disc tick as a single pick, so the drafter could not tell several may be ticked. Now
// a multi field (and a multi options card) draws a checkbox on each tile (empty box / ticked box), a small hint line under
// its question ("Pick any"; Malay turn: "Pilih satu atau lebih"), and the Send button counts the ticks ("Send (2)").
// Single-choice tiles are unchanged (number hint until picked, then the disc tick). WPF-free so it is unit-tested.

using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>The mark a choice tile draws on itself.</summary>
    public enum AskTileMark
    {
        /// <summary>A single pick: the number hint until picked, then the accent disc with a tick (unchanged).</summary>
        Disc,
        /// <summary>A multi choice not ticked: an empty box.</summary>
        BoxEmpty,
        /// <summary>A multi choice ticked: a filled box with a tick.</summary>
        BoxTicked,
    }

    public sealed partial class AskCardModel
    {
        public const string SendLabel = "Send";
        public const string MultiHintEn = "Pick any";
        public const string MultiHintMs = "Pilih satu atau lebih";

        /// <summary>A tile's mark: single choices keep the disc; multi choices draw a box, ticked or not.</summary>
        public static AskTileMark TileMark(bool multi, bool ticked) =>
            !multi ? AskTileMark.Disc : ticked ? AskTileMark.BoxTicked : AskTileMark.BoxEmpty;

        /// <summary>The hint under a multi question, in the turn's language.</summary>
        public static string MultiHintFor(ReceiptLanguage lang) => lang == ReceiptLanguage.Ms ? MultiHintMs : MultiHintEn;

        public string MultiHint => MultiHintFor(Language);

        /// <summary>A multi options card: the hint goes under the card's question.</summary>
        public bool ShowsMultiHint => Kind == AskCardKind.Options && Multi;

        /// <summary>A multi field drawn as tiles or list rows: the hint goes under the field's question.</summary>
        public static bool FieldShowsMultiHint(AskFieldModel f) =>
            f.Multi && (ShapeOf(f) == AskFieldShape.Tiles || ShapeOf(f) == AskFieldShape.List);

        /// <summary>The card asks for ticks somewhere (a multi options card, or a form with a multi field).</summary>
        public bool HasMulti => Kind == AskCardKind.Options ? Multi : Kind == AskCardKind.Fields && Fields.Any(f => f.Multi);

        /// <summary>How many choices are ticked: a multi options card's ticks, or every multi field's ticks (a skip is not a tick).</summary>
        public int TickCount =>
            Kind == AskCardKind.Options ? (Multi ? MultiPicks.Count(id => !Options.Any(o => o.Id == id && o.IsSkip)) : 0)
            : Kind == AskCardKind.Fields
                ? Fields.Where(f => f.Multi).Sum(f => TickedIds(f, Picks.TryGetValue(f.Id, out var v) ? v : "")
                    .Count(id => !f.Choices.Any(c => c.Id == id && c.IsSkip)))
                : 0;

        /// <summary>r18: the tiles of a multi options card: every option but its skip (a skip is the quiet row under the tiles, as on a multi
        /// field, never a numbered tick box counted in "Send (N)"). A single question keeps every option as a tile.</summary>
        public IReadOnlyList<AskOptionModel> OptionTiles => Multi ? Options.Where(o => !o.IsSkip).ToList() : Options;

        /// <summary>r18: the skip option a multi options card draws as its quiet row; null for none (or a single question).</summary>
        public AskOptionModel? MultiSkip => Multi ? Options.FirstOrDefault(o => o.IsSkip) : null;

        /// <summary>The collapsed record of a sent multi options card: the ticked labels, or "Skipped" for a skip.</summary>
        public string MultiSummary => MultiPicked.Count == 1 && MultiPicked[0].IsSkip ? Say(SkippedText) : string.Join(", ", MultiPicked.Select(o => o.Label));

        /// <summary>"Send (2)": the send label with the tick count, "Send" with none.</summary>
        public static string SendWithCount(int n) =>
            n > 0 ? SendLabel + " (" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : SendLabel;
    }
}
