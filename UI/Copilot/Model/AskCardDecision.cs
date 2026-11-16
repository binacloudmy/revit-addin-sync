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
        /// <summary>Rows a group shows before its "Show N more" row.</summary>
        public const int GroupPreview = 5;
        /// <summary>Ready pills shown before a "+N more" pill.</summary>
        public const int ReadyPreview = 6;
        /// <summary>More fields than this: the header shows "3 / 12" instead of one segment per field.</summary>
        public const int SegmentLimit = 8;
        /// <summary>More ready items than this: "What you will get" sums them up in one line (the pills name them).</summary>
        public const int ReadyLineLimit = 4;
        /// <summary>Field lines "What you will get" shows before its "Show N more" row.</summary>
        public const int OutcomePreview = 6;
        public const string ShowFewerLabel = "Show fewer";

        /// <summary>"Show 7 more".</summary>
        public static string ShowMoreLabel(int hidden) => "Show " + hidden.ToString(CultureInfo.InvariantCulture) + " more";

        /// <summary>The ready pills' toggle: "+12 more" while folded, "Show fewer" once open.</summary>
        public static string ReadyToggleLabel(int hidden, bool expanded) =>
            expanded ? ShowFewerLabel : "+" + hidden.ToString(CultureInfo.InvariantCulture) + " more";
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

        /// <summary>The card's heading: the frame's title, else the question's first paragraph (without its markdown
        /// marks). A question the model asks itself has no title and gets no filler heading.</summary>
        public string HeaderTitle => !string.IsNullOrWhiteSpace(Title) ? Title! : SplitQuestion(Question).Heading;

        /// <summary>What the card shows under the heading: the whole question when the frame has a title, else the
        /// rest of the question after its first paragraph (markdown), or null.</summary>
        public string? HeaderBody => !string.IsNullOrWhiteSpace(Title)
            ? (string.IsNullOrWhiteSpace(Question) ? null : Question.Trim())
            : SplitQuestion(Question).Body;

        /// <summary>A question as heading + rest: the first paragraph (or the first line, when a list or table starts
        /// on the next line) with inline markdown removed, and the remainder as written (null when empty).</summary>
        public static (string Heading, string? Body) SplitQuestion(string? question)
        {
            var text = (question ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (text.Length == 0) return ("", null);
            var lines = text.Split('\n');
            var head = new List<string>();
            var i = 0;
            for (; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) break;                                   // end of the first paragraph
                if (head.Count > 0 && StartsBlock(line)) break;                // a list or table starts
                head.Add(line);
                if (head.Count == 1 && StartsBlock(line) && !line.StartsWith("#", StringComparison.Ordinal)) break;
            }
            var rest = string.Join("\n", lines.Skip(i)).Trim();
            return (StripInline(string.Join(" ", head)), rest.Length == 0 ? null : rest);
        }

        /// <summary>Whether a question's remainder needs the markdown renderer (a list, table, heading, emphasis or code).</summary>
        public static bool NeedsMarkdown(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text!.Contains("**") || text.Contains("__") || text.Contains("`")) return true;
            return text.Split('\n').Any(l => StartsBlock(l.Trim()));
        }

        private static bool StartsBlock(string line) =>
            line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)
            || line.StartsWith("+ ", StringComparison.Ordinal) || line.StartsWith("|", StringComparison.Ordinal)
            || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(">", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+[.)] ");

        private static string StripInline(string s)
        {
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^(#+|[-*+>]|\d+[.)])\s+", "");
            return s.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
        }

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

        /// <summary>A choice field: the only kind that can be "left". A text or number field never blocks the card.</summary>
        public static bool IsChoiceField(AskFieldModel f) => f.Type == AskFieldTypes.Choice;

        /// <summary>A choice field with no pick yet.</summary>
        public bool IsOpen(AskFieldModel f) => IsChoiceField(f) && !IsAnswered(f);

        /// <summary>Some text or number field has text in it.</summary>
        public bool AnyTyped => Fields.Any(f => !IsChoiceField(f) && IsAnswered(f));

        /// <summary>The form can be sent: every choice field has a pick (and there is something to send), or the drafter
        /// typed into a text field (the levels question: pick one choice, or type several names).</summary>
        public bool CanSend => AnyTyped || (Unanswered == 0 && Fields.Any(IsChoiceField));

        public bool IsAnswered(AskFieldModel f)
        {
            if (!Picks.TryGetValue(f.Id, out var v) || string.IsNullOrWhiteSpace(v)) return false;
            return f.Type != AskFieldTypes.Choice || f.Choices.Any(c => c.Id == v);
        }

        /// <summary>The choice picked for a choice field, or null.</summary>
        public AskOptionModel? Picked(AskFieldModel f) =>
            f.Type == AskFieldTypes.Choice && Picks.TryGetValue(f.Id, out var v) ? f.Choices.FirstOrDefault(c => c.Id == v) : null;

        /// <summary>Choice fields with no pick ("Answer K more").</summary>
        public int Unanswered => Fields.Count(IsOpen);

        public int LeftIn(AskFieldGroup g) => g.Fields.Count(IsOpen);

        /// <summary>Whether a group shows the "N left" / "done" badge (it has a choice field to count).</summary>
        public static bool CountsIn(AskFieldGroup g) => g.Fields.Any(IsChoiceField);

        /// <summary>Rows of a group hidden behind "Show N more" until it is expanded (0 when all show).</summary>
        public static int HiddenIn(AskFieldGroup g) => g.Fields.Count > GroupPreview ? g.Fields.Count - GroupPreview : 0;

        /// <summary>Ready items hidden behind the "+N more" pill (0 when all show).</summary>
        public int HiddenReady => Ready.Count > ReadyPreview ? Ready.Count - ReadyPreview : 0;

        /// <summary>Rows a group shows: all when expanded (or when nothing is hidden), else the first <see cref="GroupPreview"/>.</summary>
        public static int VisibleIn(AskFieldGroup g, bool expanded) => expanded || HiddenIn(g) == 0 ? g.Fields.Count : GroupPreview;

        /// <summary>Ready pills shown: all when expanded, else the first <see cref="ReadyPreview"/>.</summary>
        public int VisibleReady(bool expanded) => expanded || HiddenReady == 0 ? Ready.Count : ReadyPreview;

        /// <summary>The fields the header's progress counts: the choice fields, or every field when there is none.</summary>
        public IReadOnlyList<AskFieldModel> ProgressFields
        {
            get { var order = OrderedFields; var choices = order.Where(IsChoiceField).ToList(); return choices.Count > 0 ? choices : order.ToList(); }
        }

        /// <summary>The header shows a count ("3 / 12") instead of the segments.</summary>
        public bool ShowsProgressCount => ProgressFields.Count > SegmentLimit;

        /// <summary>"3 / 12": progress fields answered / all of them.</summary>
        public string ProgressText
        {
            get { var p = ProgressFields; return p.Count(IsAnswered).ToString(CultureInfo.InvariantCulture) + " / " + p.Count.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>"3 left" or "done".</summary>
        public static string CountLabel(int left) => left <= 0 ? "done" : left.ToString(CultureInfo.InvariantCulture) + " left";

        /// <summary>One segment per progress field (draw order), lit when answered.</summary>
        public IReadOnlyList<bool> Segments => ProgressFields.Select(IsAnswered).ToList();

        // ─── Picking ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Records a value for a field and moves the keyboard to the next open field.</summary>
        public void Pick(string fieldId, string value)
        {
            Picks[fieldId] = value ?? "";
            var order = OrderedFields;
            var i = IndexOf(order, fieldId);
            if (i >= 0 && IsAnswered(order[i])) Active = NextOpen(i);
        }

        /// <summary>The pick is the whole answer, so the card sends it at once, as a single question sends the option clicked:
        /// Skip on a form with ONE field (live 14 Nov, E-d09: a one-field form, "pick a dimension type or Skip"; after Skip
        /// the drafter still had to click Continue). On a form with several fields a pick, Skip included, is one answer of
        /// several and stays a draft until the footer button: that is the form's design (nothing preselected, every field
        /// answered, then one send), so it is left as it is.</summary>
        public bool PickSends(string fieldId, string choiceId)
        {
            if (Kind != AskCardKind.Fields || Fields.Count != 1 || Fields[0].Id != fieldId) return false;
            return Fields[0].Choices.Any(c => c.Id == choiceId && c.IsSkip);
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

        /// <summary>The next choice field without a pick after <paramref name="from"/> (wrapping); <paramref name="from"/>
        /// when there is none.</summary>
        public int NextOpen(int from)
        {
            var order = OrderedFields;
            for (var k = 1; k <= order.Count; k++)
            {
                var i = (from + k) % order.Count;
                if (IsOpen(order[i])) return i;
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

        /// <summary>The ready items, then one line per field in draw order (an empty text field adds none).</summary>
        public IReadOnlyList<AskOutcomeLine> Outcome()
        {
            var lines = Ready.Select(r => new AskOutcomeLine { Label = r.Label, Detail = r.Detail, State = AskOutcomeState.Ready }).ToList();
            foreach (var f in OrderedFields)
            {
                if (!IsAnswered(f))
                {
                    if (IsChoiceField(f)) lines.Add(new AskOutcomeLine { Label = f.Label, Detail = WaitingText, State = AskOutcomeState.Waiting });
                    continue;
                }
                var c = Picked(f);
                if (c != null && c.IsSkip) lines.Add(new AskOutcomeLine { Label = f.Label, State = AskOutcomeState.Skipped });
                else if (c != null && c.IsCreate) lines.Add(new AskOutcomeLine { Label = f.Label, Detail = NewLabel(c.Label), State = AskOutcomeState.New });
                else if (c != null) lines.Add(new AskOutcomeLine { Label = f.Label, Detail = c.Label, State = AskOutcomeState.Picked });
                else lines.Add(new AskOutcomeLine { Label = f.Label, Detail = TypedText(f), State = AskOutcomeState.Picked });
            }
            return lines;
        }

        /// <summary>"What you will get" as the card lists it. Up to <see cref="ReadyLineLimit"/> ready items each get a
        /// line; more are one line ("18 sheets ready to make" with a noun, else "18 ready"), since the pills name them.
        /// Then the field lines: all when <paramref name="expanded"/>, else the first <see cref="OutcomePreview"/>;
        /// <c>Hidden</c> is how many field lines the "Show N more" row holds back (0 when it is not needed).</summary>
        public (IReadOnlyList<AskOutcomeLine> Lines, int Hidden) OutcomeView(bool expanded)
        {
            var all = Outcome();
            var fieldLines = all.Where(l => l.State != AskOutcomeState.Ready).ToList();
            var lines = new List<AskOutcomeLine>();
            if (Ready.Count > ReadyLineLimit) lines.Add(new AskOutcomeLine { Label = ReadySummary, State = AskOutcomeState.Ready });
            else lines.AddRange(all.Where(l => l.State == AskOutcomeState.Ready));
            var hidden = fieldLines.Count > OutcomePreview ? fieldLines.Count - OutcomePreview : 0;
            lines.AddRange(expanded || hidden == 0 ? fieldLines : fieldLines.Take(OutcomePreview));
            return (lines, hidden);
        }

        /// <summary>The one line for many ready items: "18 sheets ready to make", or "18 ready" without a noun.</summary>
        public string ReadySummary => Noun != null ? CountOf(Ready.Count, Noun) + " ready to make" : Ready.Count.ToString(CultureInfo.InvariantCulture) + " ready";

        /// <summary>The ready items plus the fields answered with anything but a skip.</summary>
        public int MadeCount => Ready.Count + Fields.Count(f => IsAnswered(f) && !(Picked(f)?.IsSkip ?? false));

        /// <summary>The preview's count ("3 sheets so far", "5 sheets"), or null without a noun.</summary>
        public string? PreviewCount => !KnowsTotal ? null : CountOf(MadeCount, Noun!) + (Unanswered > 0 ? " so far" : "");

        /// <summary>The card can count what will be made: it has a noun AND the frame carried a ready list (an empty one
        /// counts: nothing is ready). Without the list it does not know the total (live R4: a frame with "sheet" and no
        /// ready list said "Make 1 sheet" while four were made), so it shows no count and the button reads "Continue".</summary>
        public bool KnowsTotal => Noun != null && ReadyKnown;

        /// <summary>Whether the card draws "What you will get": only with the frame's ready list (without it the list
        /// would leave out what is made anyway) and something to show or a noun to count.</summary>
        public bool ShowsPreview => Kind == AskCardKind.Fields && ReadyKnown && (Ready.Count > 0 || Noun != null);

        /// <summary>What a create pick makes, for "What you will get": "Create plan" reads "New plan", "Create a ceiling
        /// plan, then the sheet" reads "New ceiling plan". A label that does not start with "Create" is kept.</summary>
        public static string NewLabel(string label)
        {
            var l = (label ?? "").Trim();
            if (!l.StartsWith("Create ", StringComparison.OrdinalIgnoreCase)) return l;
            var rest = l.Substring("Create ".Length).Trim();
            foreach (var article in new[] { "a ", "an ", "the " })
                if (rest.StartsWith(article, StringComparison.OrdinalIgnoreCase)) { rest = rest.Substring(article.Length); break; }
            foreach (var cut in new[] { ",", " then ", " for " })
            {
                var at = rest.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
                if (at > 0) rest = rest.Substring(0, at);
            }
            rest = rest.Trim();
            if (rest.Length == 0) return "New";
            // "plan" -> "New plan"; "FloorPlan" keeps its capital.
            if (rest.Length > 1 && char.IsUpper(rest[0]) && char.IsLower(rest[1]) && rest.IndexOf(' ') < 0 && !rest.Skip(1).Any(char.IsUpper))
                rest = char.ToLowerInvariant(rest[0]) + rest.Substring(1);
            return "New " + rest;
        }

        /// <summary>The primary button: "Answer K more" (K = choice fields without a pick) until the card can be sent;
        /// then "Make N sheets" / "Make 1 sheet" / "Make nothing" with a noun, "Continue" without.</summary>
        public string FooterLabel
        {
            get
            {
                if (Kind == AskCardKind.Fields)
                {
                    var open = Unanswered;
                    if (!CanSend) return open > 0 ? "Answer " + open.ToString(CultureInfo.InvariantCulture) + " more" : ContinueLabel;
                    if (!KnowsTotal) return ContinueLabel;
                    var n = MadeCount;
                    return n == 0 ? "Make nothing" : "Make " + CountOf(n, Noun!);
                }
                return ContinueLabel;
            }
        }

        public bool FooterEnabled =>
            Kind == AskCardKind.Fields ? CanSend
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
