#nullable enable
// The decision card's logic (card spec "What the card shows", approved mockup 12 Nov 2026). Revit-free
// and WPF-free so it is unit-tested: which shape a field gets, the "N left" counts, the "What you will
// get" lines, the button label, bulk create/skip, which choice a key picks, the answer and the collapsed
// record. AskCard draws from here and keeps no state of its own: the draft lives on the model, so a
// redraw of the thread (theme switch, new message) keeps the drafter's picks.
//
// Owner's rules: English only for what the card writes; nothing is preselected; no confirm step.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>How a field is drawn.</summary>
    public enum AskFieldShape
    {
        /// <summary>Exactly one create and one skip choice: one row with a two-way switch.</summary>
        Switch,
        /// <summary>Up to <see cref="AskCardModel.TileLimit"/> ordinary choices: one tile each; a skip choice is a quiet text button.</summary>
        Tiles,
        /// <summary>More ordinary choices than tiles: a filter box and a list.</summary>
        List,
        Text,
        Number,
    }

    /// <summary>What kind of question the card is.</summary>
    public enum AskCardKind { Credential, ProjectNote, Options, Fields, FreeText }

    public enum AskOutcomeState
    {
        /// <summary>From the frame's ready list: made without a decision.</summary>
        Ready,
        /// <summary>Still open ("waiting for you", amber).</summary>
        Waiting,
        Picked,
        /// <summary>The pick is a create choice (a "new" tag).</summary>
        New,
        /// <summary>The pick is a skip choice (struck out).</summary>
        Skipped,
    }

    public sealed class AskOutcomeLine
    {
        public string Label = "";
        public string? Detail;
        public AskOutcomeState State;
    }

    public sealed class AskFieldGroup
    {
        /// <summary>The group heading; null for fields the frame gave no group.</summary>
        public string? Heading;
        public List<AskFieldModel> Fields = new List<AskFieldModel>();
    }

    /// <summary>One line of the collapsed "Your answers" record.</summary>
    public sealed class AskAnsweredLine
    {
        /// <summary>The field's label; empty for a single question's one answer.</summary>
        public string Label = "";
        public string Pick = "";
    }

    public sealed partial class AskCardModel
    {
        /// <summary>Most ordinary choices drawn as tiles; more get the filterable list.</summary>
        public const int TileLimit = 4;
        /// <summary>Rows the list shows before "Show N more".</summary>
        public const int ListPreview = 6;
        /// <summary>The heading when the frame has no title (every question the model asks itself).</summary>
        public const string DefaultTitle = "Before I go on";
        public const string AnsweredHeading = "Your answers";
        public const string ReadyHeading = "Ready";
        public const string PreviewHeading = "What you will get";
        public const string WaitingText = "waiting for you";
        public const string NewTag = "new";
        public const string SkippedText = "Skipped";
        public const string ContinueLabel = "Continue";
        public const string TypeOwnHint = "Or type your own answer in the message box.";
        public const string FilterHint = "Type to filter";

        // ─── The draft (what the drafter has picked so far) ────────────────────────────────────────────────

        /// <summary>Field id → value: the chosen choice ID for a choice field, the typed text otherwise.</summary>
        public Dictionary<string, string> Picks = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>The options ticked on a multi-select question.</summary>
        public HashSet<string> MultiPicks = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>The free-text box's text.</summary>
        public string FreeTextDraft = "";
        /// <summary>The field the keyboard acts on: an index into <see cref="OrderedFields"/>.</summary>
        public int Active;
        /// <summary>Set by the card when the drafter answers from it; the collapsed record lists these.</summary>
        public List<AskAnsweredLine>? AnsweredLines;

        public AskCardKind Kind =>
            AsksForCredential ? AskCardKind.Credential
            : IsProjectNote ? AskCardKind.ProjectNote
            : Options.Count > 0 ? AskCardKind.Options
            : Fields.Count > 0 ? AskCardKind.Fields
            : AskCardKind.FreeText;

        public string HeaderTitle => string.IsNullOrWhiteSpace(Title) ? DefaultTitle : Title!;

        // ─── Shapes ────────────────────────────────────────────────────────────────────────────────────────

        public static AskFieldShape ShapeOf(AskFieldModel f)
        {
            if (f.Type == AskFieldTypes.Number) return AskFieldShape.Number;
            if (f.Type != AskFieldTypes.Choice || f.Choices.Count == 0) return AskFieldShape.Text;
            if (f.Choices.Count == 2 && f.Choices.Count(c => c.IsCreate) == 1 && f.Choices.Count(c => c.IsSkip) == 1)
                return AskFieldShape.Switch;
            return PickChoices(f).Count > TileLimit ? AskFieldShape.List : AskFieldShape.Tiles;
        }

        /// <summary>The choices drawn as tiles or list rows (every choice but a skip).</summary>
        public static IReadOnlyList<AskOptionModel> PickChoices(AskFieldModel f) => f.Choices.Where(c => !c.IsSkip).ToList();

        public static AskOptionModel? SkipChoice(AskFieldModel f) => f.Choices.FirstOrDefault(c => c.IsSkip);

        public static AskOptionModel? CreateChoice(AskFieldModel f) => f.Choices.FirstOrDefault(c => c.IsCreate);

        /// <summary>The list's rows for a filter text: label or meta contains it, any case. Empty text: all.</summary>
        public static IReadOnlyList<AskOptionModel> Filter(AskFieldModel f, string? text)
        {
            var all = PickChoices(f);
            var t = (text ?? "").Trim();
            if (t.Length == 0) return all;
            return all.Where(c => c.Label.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0
                                  || (c.Meta ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        // ─── Groups and counts ─────────────────────────────────────────────────────────────────────────────

        /// <summary>The fields under their headings, in the order each heading first appears; fields without one
        /// keep their place in a group with no heading.</summary>
        public IReadOnlyList<AskFieldGroup> Groups()
        {
            var groups = new List<AskFieldGroup>();
            foreach (var f in Fields)
            {
                var g = f.Group == null ? null : groups.FirstOrDefault(x => x.Heading == f.Group);
                if (g == null && f.Group == null && groups.Count > 0 && groups[groups.Count - 1].Heading == null) g = groups[groups.Count - 1];
                if (g == null) { g = new AskFieldGroup { Heading = f.Group }; groups.Add(g); }
                g.Fields.Add(f);
            }
            return groups;
        }

        /// <summary>The fields in the order the card draws them (group by group).</summary>
        public IReadOnlyList<AskFieldModel> OrderedFields => Groups().SelectMany(g => g.Fields).ToList();

        public bool IsAnswered(AskFieldModel f)
        {
            if (!Picks.TryGetValue(f.Id, out var v) || string.IsNullOrWhiteSpace(v)) return false;
            return f.Type != AskFieldTypes.Choice || f.Choices.Any(c => c.Id == v);
        }

        /// <summary>The choice picked for a choice field, or null.</summary>
        public AskOptionModel? Picked(AskFieldModel f) =>
            f.Type == AskFieldTypes.Choice && Picks.TryGetValue(f.Id, out var v) ? f.Choices.FirstOrDefault(c => c.Id == v) : null;

        public int Unanswered => Fields.Count(f => !IsAnswered(f));

        public int LeftIn(AskFieldGroup g) => g.Fields.Count(f => !IsAnswered(f));

        /// <summary>"3 left" or "done".</summary>
        public static string CountLabel(int left) => left <= 0 ? "done" : left.ToString(CultureInfo.InvariantCulture) + " left";

        /// <summary>One segment per field (draw order), lit when answered.</summary>
        public IReadOnlyList<bool> Segments => OrderedFields.Select(IsAnswered).ToList();

        // ─── Picking ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Records a value for a field and moves the keyboard to the next open field.</summary>
        public void Pick(string fieldId, string value)
        {
            Picks[fieldId] = value ?? "";
            var order = OrderedFields;
            var i = IndexOf(order, fieldId);
            if (i >= 0 && IsAnswered(order[i])) Active = NextOpen(i);
        }

        /// <summary>Records typed text for a field (the keyboard stays where it is).</summary>
        public void SetText(string fieldId, string text) => Picks[fieldId] = text ?? "";

        /// <summary>"Create all" / "Skip all": every switch row in the group gets its create (or skip) choice.</summary>
        public bool CanBulk(AskFieldGroup g) => g.Fields.Count(f => ShapeOf(f) == AskFieldShape.Switch) >= 2;

        public void Bulk(AskFieldGroup g, string kind)
        {
            foreach (var f in g.Fields.Where(f => ShapeOf(f) == AskFieldShape.Switch))
            {
                var c = kind == AskOptionKinds.Create ? CreateChoice(f) : kind == AskOptionKinds.Skip ? SkipChoice(f) : null;
                if (c != null) Picks[f.Id] = c.Id;
            }
            Active = NextOpen(Math.Max(0, OrderedFields.Count - 1));
        }

        /// <summary>The next unanswered field after <paramref name="from"/> (wrapping); <paramref name="from"/> when all are answered.</summary>
        public int NextOpen(int from)
        {
            var order = OrderedFields;
            for (var k = 1; k <= order.Count; k++)
            {
                var i = (from + k) % order.Count;
                if (!IsAnswered(order[i])) return i;
            }
            return Math.Max(0, Math.Min(from, order.Count - 1));
        }

        /// <summary>Up (-1) / Down (+1), wrapping.</summary>
        public void MoveActive(int delta)
        {
            var n = OrderedFields.Count;
            if (n == 0) return;
            Active = ((Active + delta) % n + n) % n;
        }

        public AskFieldModel? ActiveField
        {
            get { var order = OrderedFields; return order.Count == 0 ? null : order[Math.Max(0, Math.Min(Active, order.Count - 1))]; }
        }

        // ─── Keys ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The choice a key picks on a field: 1..9 the Nth tile or row (of <paramref name="visible"/> when the
        /// list is filtered), C the create choice, S the skip choice. Null when the key picks nothing there.</summary>
        public static AskOptionModel? ChoiceForKey(AskFieldModel f, char key, IReadOnlyList<AskOptionModel>? visible = null)
        {
            if (f.Type != AskFieldTypes.Choice) return null;
            var k = char.ToLowerInvariant(key);
            if (k == 'c') return CreateChoice(f);
            if (k == 's') return SkipChoice(f);
            if (k >= '1' && k <= '9')
            {
                var list = visible ?? PickChoices(f);
                var n = k - '1';
                return n < list.Count ? list[n] : null;
            }
            return null;
        }

        /// <summary>The option a key picks on a single question: C a create option, S a skip option, 1..9 the Nth.</summary>
        public AskOptionModel? OptionForKey(char key)
        {
            var k = char.ToLowerInvariant(key);
            if (k == 'c') return Options.FirstOrDefault(o => o.IsCreate);
            if (k == 's') return Options.FirstOrDefault(o => o.IsSkip);
            if (k >= '1' && k <= '9') { var n = k - '1'; return n < Options.Count ? Options[n] : null; }
            return null;
        }

        /// <summary>The key shown on a single question's option: "C", "S" or its number.</summary>
        public static string OptionKeyHint(AskOptionModel o, int index) =>
            o.IsCreate ? "C" : o.IsSkip ? "S" : index < 9 ? (index + 1).ToString(CultureInfo.InvariantCulture) : "";

        // ─── "What you will get" and the button ────────────────────────────────────────────────────────────

        /// <summary>The ready items, then one line per field in draw order.</summary>
        public IReadOnlyList<AskOutcomeLine> Outcome()
        {
            var lines = Ready.Select(r => new AskOutcomeLine { Label = r.Label, Detail = r.Detail, State = AskOutcomeState.Ready }).ToList();
            foreach (var f in OrderedFields)
            {
                if (!IsAnswered(f)) { lines.Add(new AskOutcomeLine { Label = f.Label, Detail = WaitingText, State = AskOutcomeState.Waiting }); continue; }
                var c = Picked(f);
                if (c != null && c.IsSkip) lines.Add(new AskOutcomeLine { Label = f.Label, State = AskOutcomeState.Skipped });
                else if (c != null) lines.Add(new AskOutcomeLine { Label = f.Label, Detail = c.Label, State = c.IsCreate ? AskOutcomeState.New : AskOutcomeState.Picked });
                else lines.Add(new AskOutcomeLine { Label = f.Label, Detail = TypedText(f), State = AskOutcomeState.Picked });
            }
            return lines;
        }

        /// <summary>The ready items plus the fields answered with anything but a skip.</summary>
        public int MadeCount => Ready.Count + Fields.Count(f => IsAnswered(f) && !(Picked(f)?.IsSkip ?? false));

        /// <summary>The preview's count ("3 sheets so far", "5 sheets"), or null without a noun.</summary>
        public string? PreviewCount => Noun == null ? null : CountOf(MadeCount, Noun) + (Unanswered > 0 ? " so far" : "");

        /// <summary>Whether the card draws "What you will get": only when there is something to make or a noun to count.</summary>
        public bool ShowsPreview => Kind == AskCardKind.Fields && (Ready.Count > 0 || Noun != null);

        /// <summary>The primary button: "Answer K more" while fields are open; then "Make N sheets" / "Make 1 sheet" /
        /// "Make nothing" with a noun, "Continue" without.</summary>
        public string FooterLabel
        {
            get
            {
                if (Kind == AskCardKind.Fields)
                {
                    var open = Unanswered;
                    if (open > 0) return "Answer " + open.ToString(CultureInfo.InvariantCulture) + " more";
                    if (Noun == null) return ContinueLabel;
                    var n = MadeCount;
                    return n == 0 ? "Make nothing" : "Make " + CountOf(n, Noun);
                }
                return ContinueLabel;
            }
        }

        public bool FooterEnabled =>
            Kind == AskCardKind.Fields ? Unanswered == 0
            : Kind == AskCardKind.Options ? Multi && MultiPicks.Count > 0
            : Kind == AskCardKind.FreeText && !string.IsNullOrWhiteSpace(FreeTextDraft);

        /// <summary>"1 sheet", "5 sheets".</summary>
        public static string CountOf(int n, string noun) =>
            n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? noun : Plural(noun));

        public static string Plural(string noun)
        {
            if (string.IsNullOrEmpty(noun)) return noun;
            var lower = noun.ToLowerInvariant();
            if (lower.EndsWith("s") || lower.EndsWith("x") || lower.EndsWith("ch") || lower.EndsWith("sh")) return noun + "es";
            if (lower.Length > 1 && lower.EndsWith("y") && "aeiou".IndexOf(lower[lower.Length - 2]) < 0) return noun.Substring(0, noun.Length - 1) + "ies";
            return noun + "s";
        }

        // ─── Answering ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The fields' answer: field id → value (a choice's ID, numbers as numbers).</summary>
        public AnswerDto AnswerForDraft() => AnswerForFields(Picks);

        /// <summary>The record of the fields' answer, one line per field in draw order.</summary>
        public List<AskAnsweredLine> LinesForDraft() =>
            OrderedFields.Where(IsAnswered).Select(f => new AskAnsweredLine { Label = f.Label, Pick = PickText(f) }).ToList();

        /// <summary>The short summary the pane keeps as the drafter's message ("Aras Tanah: Pelan Tapak · ...").</summary>
        public string SummaryForDraft() => string.Join(" · ", LinesForDraft().Select(l => l.Label + ": " + l.Pick));

        /// <summary>The ticked options of a multi-select question, in the card's order.</summary>
        public IReadOnlyList<AskOptionModel> MultiPicked => Options.Where(o => MultiPicks.Contains(o.Id)).ToList();

        public void ToggleMulti(string optionId)
        {
            if (!MultiPicks.Remove(optionId)) MultiPicks.Add(optionId);
        }

        /// <summary>Marks the card answered from the card itself (the collapsed record shows these lines).</summary>
        public void RecordLines(IEnumerable<AskAnsweredLine> lines) => AnsweredLines = lines.ToList();

        private string PickText(AskFieldModel f)
        {
            var c = Picked(f);
            if (c != null) return c.IsSkip ? SkippedText : c.Label;
            return TypedText(f);
        }

        private string TypedText(AskFieldModel f)
        {
            var v = Picks.TryGetValue(f.Id, out var s) ? (s ?? "").Trim() : "";
            return string.IsNullOrEmpty(f.Unit) ? v : v + " " + f.Unit;
        }

        private static int IndexOf(IReadOnlyList<AskFieldModel> order, string id)
        {
            for (var i = 0; i < order.Count; i++) if (order[i].Id == id) return i;
            return -1;
        }
    }
}
