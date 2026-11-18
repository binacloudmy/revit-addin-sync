#nullable enable
// The result card (result-pattern spec, Nov 2026): one shape for every task
// kind, built from the turn's envelopes and never from the reply text.
//
//   changes   set_parameters / code commits with changes.touched.modified
//   created   commits that only added elements
//   findings  warnings or a check table, with no commit
//   selection a read that left a selection (highlight.selected > 0)
//
// Revit-free: compiles in Tests.csproj and the scratch harness.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public enum ResultKind { None, Changes, Created, Findings, Selection }

    /// <summary>What the parameter of a changed row holds, as the write's own typed rows say (before_raw / after_raw: a JSON number for a quantity,
    /// a string for text). The card's value edit checks units only for a quantity.</summary>
    public enum RowValueKind { Unknown, Text, Quantity }

    public sealed class ChangeRow
    {
        public string Param = "";
        /// <summary>Text, a quantity, or unknown (a row the add-in could not type, e.g. from a code run).</summary>
        public RowValueKind ValueKind = RowValueKind.Unknown;
        public string? Before;
        public string? After;
        /// <summary>Where the value came from, as text: "Your prompt", "Copilot's choice", "brochure.pdf, page 8",
        /// "list.xlsx, row 3", or the attachment's name when the row carries no source of its own; "" when unknown.</summary>
        public string Source = "";
        /// <summary>The elements (or types) this row was written to: what an edit writes again.</summary>
        public List<long> Ids = new List<long>();
        /// <summary>The read-back did not match what was written, so the drafter should look.</summary>
        public bool NeedsCheck;
        /// <summary>The row's own source (AI-296), null when it declared none.</summary>
        public ResultSourceDto? SourceInfo;
        /// <summary>The value was written and then put back by a later call: After is what the element holds now
        /// (its old value), <see cref="Intended"/> is what was written.</summary>
        public bool ChangedBack;
        /// <summary>For a changed-back row: the one value that was written to all its elements, or null when they
        /// differed (then only <see cref="IntendedLabel"/> is set, and the row cannot be written again as one value).</summary>
        public string? Intended;
        /// <summary>For a changed-back row whose written values differed: "3 values".</summary>
        public string? IntendedLabel;
        /// <summary>What to show for the written value of a changed-back row.</summary>
        public string IntendedText => Intended ?? IntendedLabel ?? "";
    }

    /// <summary>Text for a value's source, in the pane's language.</summary>
    public static class SourceLabels
    {
        public static string Of(ResultSourceDto? s, string fallback, ReceiptLanguage lang)
        {
            if (s == null) return fallback;
            var ms = lang == ReceiptLanguage.Ms;
            var at = string.IsNullOrWhiteSpace(s.Attachment) ? "" : s.Attachment!.Trim();
            string Where(string word, int? n) => n == null ? at : at + ", " + word + " " + n.Value.ToString(CultureInfo.InvariantCulture);
            switch (s.Kind)
            {
                case ResultSourceKinds.Prompt: return ms ? "Prompt anda" : "Your prompt";
                case ResultSourceKinds.Calculated: return ms ? "Pilihan Copilot" : "Copilot's choice";
                case ResultSourceKinds.Rule: return ms ? "Peraturan" : "Rule";
                case ResultSourceKinds.Document: return at == "" ? fallback : Where(ms ? "halaman" : "page", s.Page);
                case ResultSourceKinds.Excel: return at == "" ? fallback : Where(ms ? "baris" : "row", s.Row);
                default: return fallback;
            }
        }
    }

    public sealed class TypeGroup
    {
        /// <summary>The type's own name ("Standard"), or the category of an element group. Never what tells two types
        /// apart: two families can each have a type of the same name (live 13 Nov, F-t04). Their ids do.</summary>
        public string TypeName = "";
        /// <summary>The family of a type group when the write named it, "" otherwise.</summary>
        public string Family = "";
        /// <summary>The category of the type's instances ("Lighting Fixtures"), "" when not known.</summary>
        public string Category = "";
        /// <summary>What the card calls a type when its name alone is not enough ("Alpha Downlight: Standard"); set by
        /// <see cref="ResultCardModel.LabelTypes"/>. "" means <see cref="TypeName"/> is the whole label.</summary>
        public string Label = "";
        /// <summary>What every part of the card, the grid and the model's marks show for the group.</summary>
        public string Title => Label.Length > 0 ? Label : TypeName;
        /// <summary>True when Ids are type ids whose instances changed (a type parameter).</summary>
        public bool IsType;
        /// <summary>How many elements the group's change reached: a type's instances, or the elements written. An element
        /// group counts every element written, past the 50 rows an envelope lists (live 14 Nov, SP-4: 60 doors written, the
        /// card said 50).</summary>
        public int Instances;
        public List<long> Ids = new List<long>();
        /// <summary>An element group: the elements the card's rows list (the envelope keeps at most
        /// <see cref="EnvelopeLimits.MaxRows"/> rows per write). 0 for a type group.</summary>
        public int Listed;
        /// <summary>The rows list fewer elements than were written: the card says "50 of 60 listed".</summary>
        public bool ListCut => !IsType && Listed > 0 && Listed < Instances;
        /// <summary>What the group's select arrow selects: a type's instances, or the element ids the card holds.</summary>
        public int Selectable => IsType ? Instances : Ids.Count;
        /// <summary>An element group's writes whose rows were cut: the largest of each write's own totals (its count, read-back
        /// and per-category change count), and the add-in's full id lists of them (CommitIds) when this session kept them.</summary>
        internal int WrittenTotal;
        internal HashSet<long> FullIds = new HashSet<long>();
        public List<ChangeRow> Rows = new List<ChangeRow>();
        /// <summary>Every row of this type was written and then put back: it holds its old values.</summary>
        public bool ChangedBack => Rows.Count > 0 && Rows.All(r => r.ChangedBack);
        /// <summary>At least one row was written and put back (all of them, or only some).</summary>
        public bool AnyChangedBack => Rows.Any(r => r.ChangedBack);
        /// <summary>The drafter chose "Leave as is" for this type's put-back values (a view state, not written anywhere).</summary>
        public bool LeftAsIs;
        /// <summary>The changed-back rows can all be written again: each has one value to write.</summary>
        public bool CanWriteAgain => AnyChangedBack && !LeftAsIs && Rows.Where(r => r.ChangedBack).All(r => !string.IsNullOrEmpty(r.Intended) && r.Ids.Count > 0);
    }

    public sealed class CreatedItem
    {
        public long Id;
        public string Label = "";
    }

    public sealed class Finding
    {
        public string Title = "";
        public string Severity = "";
        public List<long> Ids = new List<long>();
        public string? Handle;
        /// <summary>Revit gave the warning error severity: the row's dot is red. Nothing fixes it automatically; the card never says so.</summary>
        public bool Fixable;
        /// <summary>The finding is a failing rule of a rule-set result: the "Rules checked" block draws it, not the findings list.</summary>
        public bool FromRule;
        /// <summary>How many identical warnings were folded into this one (1 = not folded): the row then shows a count chip.</summary>
        public int Repeats = 1;
        /// <summary>The warning's Revit failure definition id when the envelope gave one: part of what makes two warnings the same.</summary>
        public string? DefinitionId;
    }

    public sealed class ResultCardModel
    {
        public ResultKind Kind = ResultKind.None;
        public string Headline = "";
        public string Detail = "";
        public List<TypeGroup> Groups = new List<TypeGroup>();
        public List<CreatedItem> Created = new List<CreatedItem>();
        /// <summary>A Created card's counts by category; null when the envelopes carry none.</summary>
        public CreatedSummary? CreatedBy;
        public List<Finding> Findings = new List<Finding>();
        /// <summary>A write of this turn was rolled back by Revit (error code rolled_back): nothing changed, and the findings are
        /// what Revit refused, not something to fix.</summary>
        public bool RolledBack;
        /// <summary>AI-332: warnings on the committed writes of a Changes or Created card (Revit's
        /// "walls overlap", "identical instances in the same place", the code's expected-count
        /// mismatch). Revit's are deleted to keep the commit dialog-free; the card shows them.</summary>
        public List<Finding> Warnings = new List<Finding>();
        public int Selected;
        public int VisibleInView;
        /// <summary>The turn isolated the result in the active view (a run_revit_code call whose intent or code isolates):
        /// the card then offers "Reset isolate".</summary>
        public bool Isolated;
        /// <summary>The model the result came from (SnapshotService.ActiveDocKey when the turn was sent); "" when not known.
        /// A button that opens a sheet or view checks it against the model that is open then.</summary>
        public string DocKey = "";
        /// <summary>The first type or element group, or null when the card has none ("No net change").</summary>
        public TypeGroup? FirstGroup => Groups.Count > 0 ? Groups[0] : null;

        /// <summary>The type whose values the Changes card's "Written as asked" list shows: the tile clicked last (the first
        /// until one is). Kept on the card, not in its view, so a redraw of the pane keeps it.</summary>
        public int ShownType;
        /// <summary>How long the turn took, as the activity card shows it ("56s"); "" when not known.</summary>
        public string Elapsed = "";
        /// <summary>What the server says to look at (turn_end.result.checks), each with its answer buttons.</summary>
        public List<CheckItem> Checks = new List<CheckItem>();
        /// <summary>Up to three next steps the server suggests (turn_end.result.next).</summary>
        public List<string> Next = new List<string>();
        /// <summary>The rows of what the turn made or found and the Excel mapping (turn_end.result.items / mapping); null when none.</summary>
        public ResultItems? Items;

        /// <summary>Attach the server's own result: the checks with their buttons and the next steps.</summary>
        /// <summary>The server's own one-sentence answer (turn_end.result.reply), "" when it sent none. When set it is the headline
        /// and the model's prose above the card is hidden: the card says it once.</summary>
        public string ServerReply = "";
        /// <summary>What the turn did not look at (result.not_searched: "link: Snowdon Towers (unloaded)"), one line each; the card's footnote says it, not the headline.</summary>
        public List<string> NotSearched = new List<string>();

        /// <summary>True when the headline is the server's reply, so the pane shows no prose above the card.</summary>
        public bool ReplacesProse => Kind != ResultKind.None && ServerReply.Length > 0;

        /// <summary>The server answered with prose: kind "selection" and no counts. The reply is the headline, the pane text goes under
        /// "More detail", and the rows (if any) draw as a tally card.</summary>
        public bool ProseResult;
        /// <summary>What follows the first sentence of the model's text when that sentence became this card's headline
        /// (<see cref="AdoptProseHeadline"/>); "" when none.</summary>
        public string AdoptedRest = "";
        /// <summary>Fewest words the model's text needs before its first sentence may stand in for a missing server reply.</summary>
        public const int AdoptMinWords = 6;

        /// <summary>A Changes or Created card the server sent no reply for would head itself with a count ("29 elements changed")
        /// beside the model's own sentence ("2% slope applied to 14 of the 36 selected pipes"): two competing headlines that can
        /// disagree. The first sentence of the model's plain text becomes the headline instead, the rest goes under "More detail",
        /// and the card's detail line (counts by category) stays below it. Returns true when it did.</summary>
        public bool AdoptProseHeadline(string? text)
        {
            if (ServerReply.Length > 0 || (Kind != ResultKind.Changes && Kind != ResultKind.Created)) return false;
            if (!ProseSplit.IsPlainProse(text, AdoptMinWords)) return false;
            var (headline, rest) = ProseSplit.Split(text);
            if (headline.Length == 0) return false;
            ServerReply = headline;
            Headline = headline;
            AdoptedRest = rest;
            return true;
        }
        /// <summary>Every write of the turn was a no-op: no banner, no issue card, no "Highlight in model" chip, a Done status.</summary>
        public bool NoChange;
        /// <summary>A code no-op that the call's declaration or the server's result contradicts (TurnEvidence.NoopContradictsClaim): the
        /// headline disc is amber, not the green check.</summary>
        public bool ClaimMismatch;
        /// <summary>The server's info lines about the run (the earlier failed attempts), shown muted near "Steps", not as a check card.</summary>
        public List<string> Notes = new List<string>();

        /// <summary>True for a result of kind "selection" that carries no counts at all (a long prose answer).</summary>
        public static bool IsProseResult(BinaVibe.Copilot.Contracts.TurnResultDto? result)
        {
            if (result == null || result.Kind != BinaVibe.Copilot.Contracts.TurnResultKinds.Selection) return false;
            var c = result.Counts;
            if (c == null) return true;
            return !(c.Elements > 0 || c.Types > 0 || c.Values > 0 || c.Written > 0 || c.Reverted > 0 || c.Skipped > 0 || c.Failed > 0 || c.Created > 0 || c.Findings > 0);
        }
        /// <summary>The card follows the server's "changes" result and its rows (turn_end.result kind changes with items),
        /// not the envelopes' added elements: the marks come from those rows, never from Revit's by-products.</summary>
        public bool ServerChanges;

        /// <summary>The type a stored change-type script gave the elements ("Family: Type"), from its own "MADE … changed to"
        /// line; "" when the turn ran no such script or the line is not there.</summary>
        public string ChangedTo = "";

        public ResultCardModel WithServerResult(BinaVibe.Copilot.Contracts.TurnResultDto? result)
        {
            ServerReply = (result?.Reply ?? "").Trim();
            NotSearched = (result?.NotSearched ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            Checks = CheckItems.From(result);
            // A server reply with no card to hang it on (a no-op, a plain answer) is still the headline, with the check disc.
            var bare = Kind == ResultKind.None && result != null && (result.Items?.Count ?? 0) == 0 && Checks.Count == 0 && CheckItems.NextOf(result).Count == 0;
            ProseResult = (IsProseResult(result) || bare) && ServerReply.Length > 0;
            if (ProseResult && IsProseResult(result))
            {
                Notes = Notes.Concat(Checks.Where(c => c.Severity == "info" && c.Actions.Count == 0).Select(c => c.Text)).ToList();
                Checks = Checks.Where(c => !(c.Severity == "info" && c.Actions.Count == 0)).ToList();
            }
            Next = CheckItems.NextOf(result);
            // The server's own count of what the turn selected is the final selection: the receipt may hold an earlier, bigger one
            // (the find's 3051 before the code's 1). Its visibility split is then unknown, so no "not visible" line.
            if (result != null && result.Kind == BinaVibe.Copilot.Contracts.TurnResultKinds.Selection && Kind == ResultKind.Selection
                && (result.Counts?.Elements ?? 0) > 0 && result.Counts!.Elements != Selected)
            {
                Selected = result.Counts.Elements.GetValueOrDefault();
                VisibleInView = 0;
            }
            Items = ResultItems.From(result);
            // Live round 9 (c01): a change-type script changed 20 doors; the envelopes saw only the 2 class-less elements Revit
            // added on the side, so the card read "Created · other 2" and marked those 2. The server's result says the turn
            // CHANGED elements and lists them: the card follows it. The envelopes' own card stays whenever the server says
            // nothing of the kind (no result, another kind, no rows).
            // Not when the server's counts are explicit zeros (a no-op turn that lists the element it selected): that card is the honest
            // "nothing changed" one, with the server's sentence and its chips, not "1 element changed" with a mark.
            if (result != null && result.Kind == BinaVibe.Copilot.Contracts.TurnResultKinds.Changes && !TurnEvidence.CountsAllZero(result.Counts)
                && Items != null && Items.Sections.Any(s => s.Rows.Any(r => r.Ids.Count > 0 || (r.Count ?? 0) > 0))
                && (Kind == ResultKind.Created || Kind == ResultKind.None))
            {
                ServerChanges = true;
                Kind = ResultKind.Changes;
                // Live 14 Nov, E-c01: the server's row names the OLD type only ("<old type> 20"), so the card and the key
                // named the doors by the type they no longer have. Where the new type is known, the row says what changed.
                var to = ChangedTo.Length > 0 ? ChangedTo : NewTypeFromReply(ServerReply, Items.Sections.SelectMany(s => s.Rows).Select(r => r.Title));
                if (to.Length > 0)
                    foreach (var r in Items.Sections.SelectMany(s => s.Rows))
                        if (r.Kind == BinaVibe.Copilot.Contracts.ResultItemKinds.Element && r.Title != to && r.Title.IndexOf(" → ", StringComparison.Ordinal) < 0)
                            r.Title = r.Title + " → " + to;
                Created.Clear();
                CreatedBy = null;
                var total = result.Counts?.Elements ?? Items.Sections.Sum(s => s.Total);
                var ms = PromptLanguage.Card(string.Equals(result.Lang, "ms", StringComparison.OrdinalIgnoreCase) ? ReceiptLanguage.Ms : ReceiptLanguage.En) == ReceiptLanguage.Ms;
                Counts = Detail = "";
                Headline = ServerReply.Length > 0 ? ServerReply
                    : ms ? total.ToString(CultureInfo.InvariantCulture) + " elemen diubah"
                    : total.ToString(CultureInfo.InvariantCulture) + " element" + (total == 1 ? "" : "s") + " changed";
                return this;
            }
            // The envelopes gave no card (a turn that made things through code, no touched ids), but the server's own result
            // has rows, checks or next steps: show those under the server's one-sentence reply rather than dropping them.
            if (Kind == ResultKind.None && result != null && (Items != null || Checks.Count > 0 || Next.Count > 0))
            {
                Kind = ResultKind.Created;
                Headline = ServerReply;
                Detail = "";
            }
            else if (Kind == ResultKind.Selection && !Isolated && Items != null && Items.Sections.Any(TallyText.IsTally))
            {
                // A grouped read answer ("walls by type": counts per row, no ids). The reads behind it may have highlighted what they
                // found, but the answer is the tally under the server's sentence: no "N selected in Revit" lines, no highlight chip.
                Kind = ResultKind.Created;
                Selected = 0; VisibleInView = 0;
                Headline = ServerReply.Length > 0 ? ServerReply : Headline;
                Detail = "";
            }
            else if (Kind != ResultKind.None && ServerReply.Length > 0) Headline = ServerReply;
            return this;
        }
        public int Mismatches;
        /// <summary>The headline's counts without the read-back note (the card shows that as a badge).</summary>
        public string Counts = "";
        /// <summary>True when every group that holds a change is a type (type parameters): the read-back then counts TYPES, the
        /// unit Revit read back, and their instances are only what the types affect.</summary>
        public bool ReadBackInTypes
        {
            get
            {
                var live = Groups.Where(g => !g.ChangedBack).ToList();
                return live.Count > 0 && live.All(g => g.IsType);
            }
        }

        /// <summary>The read-back chip's numbers: how many read back as written of how many were written. A type-parameter card
        /// counts types (live 13 Nov, F-t04: 2 types read back, not "38 of 38" elements of one of them); any other card counts
        /// elements like the headline (not values), a mismatch being one element at most.</summary>
        public (int Match, int Total) ReadBack()
        {
            if (ReadBackInTypes)
            {
                var types = Groups.Where(g => !g.ChangedBack).ToList();
                var badTypes = types.Count(g => g.Rows.Any(r => r.NeedsCheck && !r.ChangedBack));
                if (badTypes == 0 && Mismatches > 0) badTypes = System.Math.Min(Mismatches, types.Count);
                return (types.Count - badTypes, types.Count);
            }
            var total = InstanceCount > 0 ? InstanceCount : ChangeCount;
            var bad = System.Math.Min(Mismatches, total);
            return (total - bad, total);
        }

        /// <summary>What Revit warned about while the turn's COMMITTED writes ran (overlaps, identical instances in the same place, ...):
        /// one entry per message, identical ones folded with a count and the union of their ids. The write's transaction deletes warnings
        /// so no dialog blocks Revit; the card says so instead of leaving the drafter to find overlaps by eye (AI-332). A preview's
        /// warnings are not here: they describe a write that was rolled back.</summary>
        public List<Finding> WriteWarnings = new List<Finding>();
        /// <summary>The read-back chip: "Read back from Revit: 10 of 10 match" (elements), or for type parameters
        /// "Read back from Revit: 2 of 2 types match · affects 52 Lighting Fixtures" (the instances said once, as what the types affect).</summary>
        public string ReadBackText(ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            string N(int n) => n.ToString(CultureInfo.InvariantCulture);
            var (match, total) = ReadBack();
            var head = (ms ? "Dibaca balik dari Revit: " : "Read back from Revit: ") + N(match) + (ms ? " daripada " : " of ") + N(total);
            if (!ReadBackInTypes) return head + (ms ? " sepadan" : " match");
            head += ms ? " jenis sepadan" : total == 1 ? " type matches" : " types match";
            var instances = InstanceCount;
            if (instances <= 0) return head;
            var cats = Groups.Where(g => !g.ChangedBack).Select(g => g.Category).Distinct(StringComparer.Ordinal).ToList();
            var what = cats.Count == 1 && cats[0].Length > 0 ? cats[0] : ms ? "elemen" : instances == 1 ? "element" : "elements";
            return head + (ms ? " · melibatkan " : " · affects ") + N(instances) + " " + what;
        }

        /// <summary>What Revit holds selected after the turn's calls ran, in order: a call's highlight (selected, visible in the view) or
        /// a code run's data.ui.selected (no visibility count: 0 = unknown, so no "not visible" line). The last one wins; (0, 0) when none selected.</summary>
        internal static (int Selected, int VisibleInView) FinalSelection(IEnumerable<Envelope> envelopes)
        {
            var last = (Selected: 0, VisibleInView: 0);
            foreach (var env in envelopes)
            {
                if ((env.Highlight?.Selected ?? 0) > 0) { last = (env.Highlight!.Selected, env.Highlight.VisibleInView); continue; }
                if (env.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data
                    && data.TryGetProperty("ui", out var ui) && ui.ValueKind == System.Text.Json.JsonValueKind.Object
                    && !(ui.TryGetProperty("applied", out var applied) && applied.ValueKind == System.Text.Json.JsonValueKind.False)   // a preview run, or one whose UI hook is missing, changed nothing in Revit
                    && ui.TryGetProperty("selected", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.Number && n.TryGetInt32(out var count))
                    last = count > 0 ? (count, 0) : (0, 0);          // a later run that set an EMPTY selection leaves nothing selected: the card must not claim the earlier find's
            }
            return last;
        }

        /// <summary>A code write with no element ids: what it changed, by category, biggest first. Empty for every other card.</summary>
        public List<(string Category, int Count)> CategoryCounts = new List<(string Category, int Count)>();

        /// <summary>The elements a code write changed, by category (added + modified + deleted), over the commits that reported counts only.
        /// Revit's by-products are not a category the drafter changed (a "class:Element" or "class:View" bucket, a generic "Element", the
        /// "Center Line" a pipe move redraws: <see cref="CreatedSummary.IsByProduct"/>, the same filter the created cards use): the item-2 slope
        /// turn read "Center Line 1 · Pipes 1 · class:Element 1 · class:View 1" under its headline and now reads "Pipes 1". A write that touched
        /// nothing else keeps its raw buckets, so the card still says something changed.</summary>
        internal static List<(string Category, int Count)> CodeWriteCounts(IEnumerable<(ToolCallDto Call, Envelope Env)> commits)
        {
            var sum = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in commits)
            {
                if (c.Call?.Tool != ToolNames.RunRevitCode) continue;
                var by = c.Env.Changes?.TouchedByCategory;
                if (by == null) continue;
                foreach (var dict in new[] { by.AddedByCategory, by.ModifiedByCategory, by.DeletedByCategory })
                    foreach (var kv in dict ?? new Dictionary<string, int>())
                        if (kv.Value > 0) sum[kv.Key] = (sum.TryGetValue(kv.Key, out var have) ? have : 0) + kv.Value;
            }
            var all = sum.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList();
            var real = all.Where(c => !CreatedSummary.IsByProduct(c.Item1)).ToList();
            return real.Count > 0 ? real : all;
        }

        public int CheckCount => Groups.Sum(g => g.Rows.Count(r => r.NeedsCheck));
        /// <summary>Rows that hold a change now (changed-back rows are not changes).</summary>
        public int ChangeCount => Groups.Sum(g => g.Rows.Count(r => !r.ChangedBack));
        public int InstanceCount => Groups.Where(g => !g.ChangedBack).Sum(g => g.Instances);
        /// <summary>Types (or element groups) whose written values were all put back.</summary>
        public int ChangedBackTypes => Groups.Count(g => g.ChangedBack);
        /// <summary>Types whose put-back values the drafter has not yet accepted with "Leave as is".</summary>
        public int ChangedBackOpen => Groups.Count(g => g.ChangedBack && !g.LeftAsIs);
        /// <summary>A batch the card itself added to the turn after the model's own (a value edited on the card, a check's button), kept so "Undo last part"
        /// and Redo can put the card back as Revit is: Row is the edited row (null for a batch the card shows no value for), Before / After its value.</summary>
        public sealed class CardPart
        {
            public ChangeRow? Row;
            public string? Before;
            public string? After;
        }

        private readonly List<CardPart> _parts = new List<CardPart>();
        private readonly Stack<CardPart> _undoneParts = new Stack<CardPart>();

        /// <summary>Counts every change the card's own state went through (an edit, an undone or redone part): the pane draws the card
        /// again when it moves, because the card's panels (the value, the source line under it) are built once from the rows.</summary>
        public int Revision { get; private set; }

        /// <summary>A value edited on the card was written (one more batch of the turn): the row now holds <paramref name="after"/>.</summary>
        public void NoteEdit(ChangeRow row, string? before, string? after, ReceiptLanguage lang)
        {
            if (row == null) return;
            row.After = after;
            FollowInItems(before, after);
            _parts.Add(new CardPart { Row = row, Before = before, After = after });
            _undoneParts.Clear();
            Revision++;
            RefreshHeadline(lang);
        }

        /// <summary>The card wrote one more batch whose value it does not track (the check card's write-again).</summary>
        public void NoteOtherPart()
        {
            _parts.Add(new CardPart());
            _undoneParts.Clear();
            Revision++;
        }

        /// <summary>"Undo last part" took the turn's last batch back: when it was an edit made on this card, the row holds its earlier value
        /// again. False when the last part is not one the card knows (nothing to put back on the card).</summary>
        public bool UndoLastPart(ReceiptLanguage lang)
        {
            if (_parts.Count == 0) return false;
            var part = _parts[_parts.Count - 1];
            _parts.RemoveAt(_parts.Count - 1);
            _undoneParts.Push(part);
            if (part.Row != null) { part.Row.After = part.Before; FollowInItems(part.After, part.Before); }
            Revision++;
            RefreshHeadline(lang);
            return part.Row != null;
        }

        /// <summary>Redo of the part that was undone last: the edited value is back on the card.</summary>
        public bool RedoPart(ReceiptLanguage lang)
        {
            if (_undoneParts.Count == 0) return false;
            var part = _undoneParts.Pop();
            _parts.Add(part);
            if (part.Row != null) { part.Row.After = part.After; FollowInItems(part.Before, part.After); }
            Revision++;
            RefreshHeadline(lang);
            return part.Row != null;
        }

        /// <summary>The server's item rows (the line under the card: "349 · ROUND2-1") carry the value the turn wrote as text. When the card edits the
        /// value, undoes the edit or redoes it, the rows say the new value too: a cell that is the old value becomes the new one, and the old value
        /// standing alone in a longer text ("349 · ROUND2-1") is swapped the same way. Final retest 17 Nov: the card showed ROUND2-EDIT and the line
        /// under it still read "349 · ROUND2-1".</summary>
        private void FollowInItems(string? from, string? to)
        {
            if (Items == null || string.IsNullOrEmpty(from) || from == to) return;
            foreach (var section in Items.Sections)
                foreach (var row in section.Rows)
                {
                    // Only the FIRST place the old value stands in the row is the written value: the facts read "<written> · <earlier value>"
                    // ("ROUND2-1 · 240V" after "was 240V"), and an edit back to 240V must not turn the earlier value into the new one too
                    // (17 Nov, control run c0693d65). The facts come before the title's own words.
                    var done = false;
                    for (var i = 0; i < row.Cells.Count && !done; i++)
                    {
                        var swapped = SwapValue(row.Cells[i].Value, from!, to, firstOnly: true);
                        if (swapped != row.Cells[i].Value) { row.Cells[i] = (row.Cells[i].Key, swapped); done = true; }
                    }
                    if (!done) row.Title = SwapValue(row.Title, from!, to, firstOnly: true);
                }
        }

        internal static string SwapValue(string? text, string from, string? to, bool firstOnly = false)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            if (text == from) return to ?? "";
            var re = new System.Text.RegularExpressions.Regex(@"(?<![\w-])" + System.Text.RegularExpressions.Regex.Escape(from) + @"(?![\w-])");
            return firstOnly ? re.Replace(text!, _ => to ?? "", 1) : re.Replace(text!, _ => to ?? "");
        }

        /// <summary>Redraw the headline after the card's state changed (a type left as is).</summary>
        public void RefreshHeadline(ReceiptLanguage lang) { if (Kind == ResultKind.Changes) SetHeadline(this, lang); }
        public int ChangedBackRows => Groups.Sum(g => g.Rows.Count(r => r.ChangedBack));

        public static ResultCardModel From(TurnEvidence ev, ReceiptLanguage lang, string? attachment = null)
        {
            var m = new ResultCardModel();
            if (ev == null) return m;
            var calls = ev.Calls;
            var source = string.IsNullOrWhiteSpace(attachment) ? "" : attachment!.Trim();

            var commits = calls.Where(c => c.Env.PhaseKind() == EnvelopePhaseKind.Commit
                && (c.Env.StatusKind() == EnvelopeStatusKind.Done || c.Env.StatusKind() == EnvelopeStatusKind.Partial)).ToList();
            // Net change: a value written and then put back (by a later call) is one row, or none.
            var net = new Dictionary<(long, string), (string? Before, string? After)>();
            var lastAt = new Dictionary<(long, string), int>();
            var firstAt = new Dictionary<(long, string), int>();
            var firstAfter = new Dictionary<(long, string), string?>();
            for (var i = 0; i < commits.Count; i++)
                foreach (var r in commits[i].Env.Changes?.Touched?.Modified ?? new List<FieldChangeDto>())
                {
                    var k = (r.Id, r.Field);
                    if (!firstAt.ContainsKey(k)) { firstAt[k] = i; firstAfter[k] = r.After; }
                    net[k] = net.TryGetValue(k, out var first) ? (first.Before, r.After) : (r.Before, r.After);
                    lastAt[k] = i;
                }
            m.Isolated = IsolatedInView(calls);
            m.WriteWarnings = WriteWarningsOf(commits.Select(c => c.Env));
            m.Notes = WriteNotesOf(commits.Select(c => c.Env));   // Copilot's own notes (the fitting note), not counted as Revit's
            m.ChangedTo = ChangedToOf(commits.Where(c => c.Call?.Tool == ToolNames.RunRevitCode).Select(c => c.Env));
            var anyModified = net.Count > 0;
            var emitted = new HashSet<(long, string)>();
            var mismatched = new HashSet<(long, string)>();

            for (var i = 0; i < commits.Count; i++)
            {
                var c = commits[i];
                m.Mismatches += c.Env.Verified?.Mismatches?.Select(x => x.Id).Distinct().Count() ?? 0;
                foreach (var x in c.Env.Verified?.Mismatches ?? new List<MismatchDto>()) mismatched.Add((x.Id, x.Field));
                var touched = c.Env.Changes?.Touched;
                if (touched == null) continue;
                var modified = (touched.Modified ?? new List<FieldChangeDto>())
                    .Where(r => lastAt[(r.Id, r.Field)] == i && emitted.Add((r.Id, r.Field)))
                    .Select(r => new FieldChangeDto { Id = r.Id, Field = r.Field, Before = net[(r.Id, r.Field)].Before, After = r.After, Source = r.Source })
                    .Where(r => (r.Before ?? "") != (r.After ?? ""))
                    .ToList();
                if (modified.Count > 0) AddChanges(m, c.Call, c.Env, modified, source, lang);
                if (touched.Added != null) foreach (var a in touched.Added) m.Created.Add(new CreatedItem { Id = a.Id, Label = Label(c.Env, a.Id) });
            }

            // Written and then put back (by a later call): the type holds its old values. One row each,
            // marked, so the card can say "Changed back" and offer to write them again.
            for (var i = 0; i < commits.Count; i++)
            {
                var back = new List<FieldChangeDto>();
                var wrote = new Dictionary<(long, string), string?>();
                foreach (var kv in net)
                {
                    var k = kv.Key;
                    if (firstAt[k] != i) continue;
                    if ((kv.Value.Before ?? "") != (kv.Value.After ?? "")) continue;          // a net change: shown above
                    if ((firstAfter[k] ?? "") == (kv.Value.Before ?? "")) continue;           // never changed anything
                    back.Add(new FieldChangeDto { Id = k.Item1, Field = k.Item2, Before = kv.Value.Before, After = kv.Value.Before });
                    wrote[k] = firstAfter[k];
                }
                if (back.Count > 0) AddChanges(m, commits[i].Call, commits[i].Env, back, source, lang, wrote);
            }

            LabelTypes(m.Groups);

            // An element group counts the elements that hold a change, not those whose value was put back. Its rows stop at
            // the envelope's 50; when a write's rows were cut, the group counts what was written (the strip's rule, TurnEvidence:
            // the largest of the rows, the change total and the read-back count) and holds the add-in's full ids when it kept
            // them, so select and the marks reach all of them. A group with a value put back keeps the rows' count.
            foreach (var g in m.Groups.Where(g => !g.IsType))
            {
                var live = g.Rows.Where(r => !r.ChangedBack).SelectMany(r => r.Ids).Distinct().Count();
                g.Listed = live > 0 ? live : g.Rows.SelectMany(r => r.Ids).Distinct().Count();
                if (g.Listed == 0) g.Listed = g.Ids.Count;
                g.Instances = g.Listed;
                if (g.AnyChangedBack) continue;
                foreach (var id in g.FullIds) if (!g.Ids.Contains(id)) g.Ids.Add(id);
                g.Instances = Math.Max(g.Listed, Math.Max(g.Ids.Count, g.WrittenTotal));
            }
            // AI-332: a commit that created or changed elements keeps its warnings on the card,
            // so an element placed on top of another is not reported as a clean success.
            foreach (var c in commits)
                foreach (var w in c.Env.Warnings ?? new List<WarningDto>())
                {
                    if (w == null || string.IsNullOrWhiteSpace(w.Text)) continue;
                    var ids = w.Ids?.Distinct().ToList() ?? new List<long>();
                    if (m.Warnings.Any(x => x.Title == w.Text && x.Ids.SequenceEqual(ids))) continue;
                    m.Warnings.Add(new Finding { Title = w.Text, Severity = w.Severity ?? "", Ids = ids, Fixable = false });
                }

            // Rows to check come first; the rest stay in the order they were written.
            foreach (var g in m.Groups)
            {
                foreach (var r in g.Rows) r.NeedsCheck = !r.ChangedBack && r.Ids.Any(id => mismatched.Contains((id, r.Param)));
                g.Rows = g.Rows.OrderByDescending(r => r.NeedsCheck).ToList();
            }

            if (m.Groups.Count > 0) m.Kind = ResultKind.Changes;
            else if (anyModified && m.Created.Count == 0)
            {
                // Every value ended where it started.
                m.Kind = ResultKind.Changes;
                m.Headline = lang == ReceiptLanguage.Ms ? "Tiada perubahan bersih" : "No net change";
                m.Detail = lang == ReceiptLanguage.Ms ? "Nilai yang ditulis telah dipulihkan" : "Values written were put back";
                return m;
            }
            else if (m.Created.Count > 0) { m.Kind = ResultKind.Created; m.CreatedBy = CreatedSummary.From(commits.Select(c => c.Env), source, lang); }
            else if (CodeWriteCounts(commits) is { Count: > 0 } counts)
            {
                // A code write that touched elements but listed no ids (run_revit_code reports counts by category only): still a
                // committed write, so the card says how many and keeps Undo all. (Live test of 0.0.107: 2097 elements, no Undo.)
                m.Kind = ResultKind.Changes;
                m.CategoryCounts = counts;
            }
            else
            {
                foreach (var c in calls)
                {
                    // A no-op (nothing needed changing) raises no finding: its "changed nothing" note is not an issue.
                    if (c.Env.StatusKind() == EnvelopeStatusKind.Noop) continue;
                    if (c.Env.Error?.Code == ErrorCodes.RolledBack) m.RolledBack = true;
                    if (c.Env.Warnings != null)
                        foreach (var w in c.Env.Warnings)
                            if (!string.IsNullOrWhiteSpace(w.Text))
                                m.Findings.Add(new Finding { Title = w.Text, Severity = w.Severity ?? "", Ids = w.Ids?.ToList() ?? new List<long>(), Fixable = w.Severity == WarningSeverity.Error, DefinitionId = w.DefinitionId });
                }
                foreach (var t in ev.Tables.Where(t => t.Kind == ResultTable.CheckKind))
                {
                    var fail = t.Columns.IndexOf("Fail"); var sev = t.Columns.IndexOf("Severity");
                    for (var i = 0; i < t.Rows.Count; i++)
                    {
                        var row = t.Rows[i];
                        if (fail < 0 || fail >= row.Count || !int.TryParse(row[fail], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) continue;
                        m.Findings.Add(new Finding { Title = row[0], Severity = sev >= 0 && sev < row.Count ? row[sev] : "", Handle = t.HandleOf(i), Fixable = false, Ids = new List<long>(), FromRule = true });
                    }
                }
                m.Findings = FoldRepeatedFindings(m.Findings);
                if (m.Findings.Count > 0) m.Kind = ResultKind.Findings;
                else
                {
                    // The selection Revit ends the turn with: the LAST call that selected (a find that selected 3051 pipes, then code that
                    // selected the one asked for, leaves 1). Not the biggest: "Selected in Revit: 3051, 2969 not visible" was the find's.
                    var final = FinalSelection(calls.Select(c => c.Env));
                    if (final.Selected > 0) { m.Kind = ResultKind.Selection; m.Selected = final.Selected; m.VisibleInView = final.VisibleInView; }
                }
            }

            // Writes ran and every one of them was a no-op (a re-run that found nothing left to do): the answer is the server's sentence.
            // (A preview that measured no change ends the batch with no confirm card: that noop envelope is the final result.)
            m.ClaimMismatch = ev.NoopContradictsClaim;
            m.NoChange = commits.Count == 0 && calls.Any(c => c.Env.PhaseKind() != EnvelopePhaseKind.Read && c.Env.StatusKind() == EnvelopeStatusKind.Noop);

            // The turn isolated the view but left nothing else to show (code that only isolates: no selection, no writes):
            // the card is still there, so the drafter can reset the isolate.
            if (m.Kind == ResultKind.None && m.Isolated) m.Kind = ResultKind.Selection;

            // A view the turn's code opened (a sheet was active; the pipe is in a 3D view): the card says where Revit is, not the older snapshot.
            var opened = BinaVibe.Copilot.Code.UiViewReport.NoteOf(calls.Select(c => c.Env));
            if (opened.Length > 0 && !m.Notes.Contains(opened)) m.Notes = m.Notes.Concat(new[] { opened }).ToList();
            // What was in view was counted in the view before the switch: unknown now, so no "not visible" line from the older view.
            if (opened.Length > 0 && m.Kind == ResultKind.Selection) m.VisibleInView = 0;

            SetHeadline(m, lang);
            return m;
        }

        /// <summary>The warnings of the committed envelopes, as findings: blank texts dropped, identical ones folded.</summary>
        internal static List<Finding> WriteWarningsOf(IEnumerable<Envelope> committed)
        {
            var list = new List<Finding>();
            foreach (var env in committed)
                foreach (var w in env.Warnings ?? new List<WarningDto>())
                    if (!string.IsNullOrWhiteSpace(w.Text) && IsRevitWarning(w))
                        list.Add(new Finding { Title = w.Text.Trim(), Severity = w.Severity ?? "", Ids = w.Ids?.ToList() ?? new List<long>(), Fixable = false, DefinitionId = w.DefinitionId });
            return FoldRepeatedFindings(list);
        }

        /// <summary>A warning Revit raised (its failure log: a definition id and/or the elements it names, or an error), as against a note the
        /// add-in wrote itself into the envelope (the fitting note of a pipe type change, "the code made N additions, the assistant expected M"):
        /// those are Copilot's own and are not "Revit warned about N things".</summary>
        internal static bool IsRevitWarning(WarningDto w) =>
            !string.IsNullOrWhiteSpace(w.DefinitionId) || (w.Ids != null && w.Ids.Count > 0) || w.Severity == WarningSeverity.Error;

        /// <summary>The add-in's own notes of the committed envelopes (what is not a Revit warning), trimmed, distinct, at most three: shown as
        /// quiet lines on the card.</summary>
        internal static List<string> WriteNotesOf(IEnumerable<Envelope> committed) =>
            committed.SelectMany(e => e.Warnings ?? new List<WarningDto>())
                .Where(w => !string.IsNullOrWhiteSpace(w.Text) && !IsRevitWarning(w))
                .Select(w => w.Text.Trim()).Distinct(StringComparer.Ordinal).Take(3).ToList();

        /// <summary>The one type the turn's code changed elements to, from the stored change-type script's line
        /// "MADE 20 doors changed to Family: Type in the whole model | type_id=123"; "" when there is none, or several.</summary>
        internal static string ChangedToOf(IEnumerable<Envelope> envelopes)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var env in envelopes)
            {
                if (env?.Data is not { } data || data.ValueKind != JsonValueKind.Object) continue;
                if (!data.TryGetProperty("output", out var o) || o.ValueKind != JsonValueKind.String) continue;
                foreach (var line in (o.GetString() ?? "").Split('\n'))
                    if (NewTypeFromMadeLine(line) is { Length: > 0 } t) found.Add(t);
            }
            return found.Count == 1 ? found.First() : "";
        }

        /// <summary>The new type in a change-type script's MADE line, or null when the line is not one.</summary>
        public static string? NewTypeFromMadeLine(string? line)
        {
            var l = (line ?? "").Trim();
            if (!l.StartsWith("MADE ", StringComparison.Ordinal)) return null;
            var at = l.IndexOf(" changed to ", StringComparison.Ordinal);
            var bar = l.LastIndexOf(" | type_id=", StringComparison.Ordinal);
            if (at < 0 || bar < at) return null;
            var t = WithoutWhere(l.Substring(at + 12, bar - at - 12));
            return t.Length > 0 ? t : null;
        }

        /// <summary>The new type in the server's reply "… changed from &lt;old&gt; to &lt;new&gt; in the whole model.", for one of
        /// the rows' old types; "" when the reply does not say it that way.</summary>
        public static string NewTypeFromReply(string? reply, IEnumerable<string> oldTypes)
        {
            var r = (reply ?? "").Trim();
            foreach (var old in oldTypes)
            {
                if (string.IsNullOrWhiteSpace(old)) continue;
                var key = "from " + old.Trim() + " to ";
                var at = r.IndexOf(key, StringComparison.Ordinal);
                if (at < 0) continue;
                var t = WithoutWhere(r.Substring(at + key.Length).TrimEnd('.', ' '));
                if (t.Length > 0) return t;
            }
            return "";
        }

        /// <summary>A change-type script ends its MADE line with where it worked: "in the whole model", "in the selection",
        /// "in &lt;view&gt;" or "on &lt;level&gt;". What is before it is the type.</summary>
        private static string WithoutWhere(string s)
        {
            s = (s ?? "").Trim();
            foreach (var fixedWhere in new[] { " in the whole model", " in the selection" })
                if (s.EndsWith(fixedWhere, StringComparison.Ordinal)) return s.Substring(0, s.Length - fixedWhere.Length).Trim();
            var at = Math.Max(s.LastIndexOf(" in ", StringComparison.Ordinal), s.LastIndexOf(" on ", StringComparison.Ordinal));
            return at > 0 ? s.Substring(0, at).Trim() : s;
        }

        /// <summary>The one category the call modified, or null when it touched none or several.</summary>
        internal static string? OnlyCategory(Dictionary<string, int>? modifiedByCategory)
        {
            var names = (modifiedByCategory ?? new Dictionary<string, int>()).Where(kv => kv.Value > 0).Select(kv => kv.Key).Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            return names.Count == 1 ? names[0] : null;
        }

        /// <summary>A successful run_revit_code of THIS turn that isolates elements in the view: its code calls IsolateElementsTemporary /
        /// IsolateCategoriesTemporary / EnableTemporaryViewMode, or (when the code names no temporary-view API at all) its intent starts with "isolate". A reset
        /// (DisableTemporaryViewMode, "reset the temporary hide/isolate") or any other turn that merely mentions "isolated" is not one.</summary>
        internal static bool IsolatedInView(IEnumerable<(ToolCallDto Call, Envelope Env)> calls)
        {
            foreach (var c in calls)
            {
                if (c.Call.Tool != ToolNames.RunRevitCode || !c.Env.Ok) continue;
                if (IsolatesView(Str(c.Call.Args, "intent") ?? "", Str(c.Call.Args, "code") ?? "")) return true;
            }
            return false;
        }

        /// <summary>The rule behind <see cref="IsolatedInView"/>, on one call's intent and code.</summary>
        internal static bool IsolatesView(string intent, string code)
        {
            const System.Text.RegularExpressions.RegexOptions I = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\b(Isolate(Elements|Categories)Temporary|EnableTemporaryViewMode)\s*\(", I)) return true;
            // The code names a temporary-view API without isolating (a reset), or other view code: the intent cannot say it isolated.
            if (code.IndexOf("TemporaryViewMode", StringComparison.OrdinalIgnoreCase) >= 0
                || code.IndexOf("HideElementsTemporary", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(intent, @"^\s*(temporarily\s+)?isolat(e|ing)\b", I)
                && !System.Text.RegularExpressions.Regex.IsMatch(intent, @"\b(reset|disable|turn off|switch off|remove|clear|undo|restore|unhide|stop)\b", I);
        }

        private static void AddChanges(ResultCardModel m, ToolCallDto call, Envelope env, List<FieldChangeDto> modified, string source, ReceiptLanguage lang,
            Dictionary<(long, string), string?>? changedBack = null)
        {
            // The one value written to every element of the row, or null when they differed.
            string? Wrote(IEnumerable<FieldChangeDto> rows)
            {
                if (changedBack == null) return null;
                var vals = rows.Select(r => changedBack.TryGetValue((r.Id, r.Field), out var w) ? w ?? "" : "").Distinct().ToList();
                return vals.Count == 1 ? vals[0] : null;
            }
            string? WroteLabel(IEnumerable<FieldChangeDto> rows)
            {
                if (changedBack == null || Wrote(rows) != null) return null;
                var n = rows.Select(r => changedBack.TryGetValue((r.Id, r.Field), out var w) ? w ?? "" : "").Distinct().Count();
                return n.ToString(CultureInfo.InvariantCulture) + " values";
            }

            var write = ToolResults.Write(env);
            var typeParam = write != null && write.TypeParam;
            var types = new Dictionary<long, EditedTypeDto>();
            if (typeParam)
                foreach (var t in write!.Types)
                    if (t.Id is long tid)
                        types[tid] = t;

            if (typeParam && types.Count > 0)
            {
                foreach (var row in modified)
                {
                    // A type is its id: two families' "Standard" are two types (live 13 Nov, F-t04: keyed by name, 38 + 14
                    // instances collapsed into one row of 38).
                    var g = m.Groups.FirstOrDefault(x => x.IsType && x.Ids.Contains(row.Id));
                    if (g == null)
                    {
                        types.TryGetValue(row.Id, out var ti);
                        var name = ti?.Name ?? ti?.Family ?? row.Element ?? row.Id.ToString(CultureInfo.InvariantCulture);
                        m.Groups.Add(g = new TypeGroup
                        {
                            TypeName = name, IsType = true, Instances = ti?.InstancesInModel ?? 0, Ids = new List<long> { row.Id },
                            Family = (ti?.Family ?? "").Trim(), Category = (ti?.Category ?? write!.InstancesCategory ?? "").Trim(),
                        });
                    }
                    g.Rows.Add(new ChangeRow { Param = row.Field, Before = row.Before, After = row.After, Source = SourceLabels.Of(row.Source, source, lang), SourceInfo = row.Source, Ids = new List<long> { row.Id },
                        ChangedBack = changedBack != null, Intended = Wrote(new[] { row }), IntendedLabel = WroteLabel(new[] { row }), ValueKind = KindOf(new[] { row }) });
                }
                return;
            }

            // Instance writes: one group per category (or one "elements" group), one row per field.
            var cat = write?.InstancesCategory;
            if (string.IsNullOrWhiteSpace(cat)) cat = OnlyCategory(env.Changes?.TouchedByCategory?.ModifiedByCategory);
            // Never the parameter's name: it is not what the elements are.
            var groupName = string.IsNullOrWhiteSpace(cat) ? "Elements" : cat!;
            var grp = m.Groups.FirstOrDefault(x => x.TypeName == groupName && !x.IsType);
            if (grp == null) m.Groups.Add(grp = new TypeGroup { TypeName = groupName, IsType = false });
            foreach (var row in modified) if (!grp.Ids.Contains(row.Id)) grp.Ids.Add(row.Id);
            grp.Instances = grp.Ids.Count;
            if (changedBack == null) AddWritten(grp, call, env, groupName == "Elements" ? null : cat);
            foreach (var field in modified.Select(r => r.Field).Distinct())
            {
                var rows = modified.Where(r => r.Field == field).ToList();
                var befores = rows.Select(r => r.Before ?? "").Distinct().ToList();
                var afters = rows.Select(r => r.After ?? "").Distinct().ToList();
                // One source for the row when every changed element of the field has the same one; else the call's own.
                var sources = rows.Select(r => r.Source).Where(x => x != null).GroupBy(x => x!.Kind + "|" + x.Attachment + "|" + x.Page + "|" + x.Row).ToList();
                var rowSource = sources.Count == 1 && rows.All(r => r.Source != null) ? sources[0].First() : null;
                grp.Rows.Add(new ChangeRow
                {
                    Param = field,
                    SourceInfo = rowSource,
                    Before = befores.Count == 1 ? befores[0] : befores.Count.ToString(CultureInfo.InvariantCulture) + " values",
                    After = afters.Count == 1 ? afters[0] : afters.Count.ToString(CultureInfo.InvariantCulture) + " values",
                    Source = SourceLabels.Of(rowSource, source, lang),
                    Ids = rows.Select(r => r.Id).Distinct().ToList(),
                    ChangedBack = changedBack != null,
                    Intended = Wrote(rows),
                    IntendedLabel = WroteLabel(rows),
                    ValueKind = KindOf(rows),
                });
            }
        }

        /// <summary>Text or quantity from the typed rows (AfterRaw, else BeforeRaw): every raw a number is a quantity, every raw a string is text, anything
        /// else (no raw at all, a mix) is unknown.</summary>
        internal static RowValueKind KindOf(IEnumerable<FieldChangeDto> rows)
        {
            int numbers = 0, strings = 0, other = 0;
            foreach (var r in rows ?? Enumerable.Empty<FieldChangeDto>())
            {
                var raw = r.AfterRaw ?? r.BeforeRaw;
                if (raw is System.Text.Json.JsonElement e && e.ValueKind == System.Text.Json.JsonValueKind.Number) numbers++;
                else if (raw is System.Text.Json.JsonElement t && t.ValueKind == System.Text.Json.JsonValueKind.String) strings++;
                else other++;
            }
            return other == 0 && numbers > 0 && strings == 0 ? RowValueKind.Quantity
                : other == 0 && strings > 0 && numbers == 0 ? RowValueKind.Text
                : RowValueKind.Unknown;
        }

        /// <summary>A write whose rows were cut at the envelope's cap: what it really wrote to the group. The largest of its
        /// rows, its count and read-back count (set_parameters: one call, one parameter, every element counted), and its
        /// per-category change count, the full touched set (R78); and the add-in's full id list of it when kept (CommitIds).
        /// A write that lists fewer rows than the cap listed every element: nothing to add.</summary>
        private static void AddWritten(TypeGroup grp, ToolCallDto call, Envelope env, string? category)
        {
            var rows = (env.Changes?.Touched?.Modified ?? new List<FieldChangeDto>()).Select(r => r.Id).Distinct().Count();
            if (rows < EnvelopeLimits.MaxRows) return;
            var total = rows;
            var byCat = env.Changes?.TouchedByCategory?.ModifiedByCategory ?? new Dictionary<string, int>();
            total = Math.Max(total, category == null ? byCat.Values.Where(v => v > 0).Sum()
                : byCat.TryGetValue(category, out var n) ? n : 0);
            if (call?.Tool != ToolNames.RunRevitCode)
            {
                total = Math.Max(total, env.Count ?? 0);
                if (env.Verified != null && env.Verified.Method != VerifyMethods.None && env.Verified.Method != VerifyMethods.CountDelta)
                    total = Math.Max(total, env.Verified.Checked);
            }
            grp.WrittenTotal = Math.Max(grp.WrittenTotal, total);
            var full = BinaVibe.Copilot.Writes.CommitIds.Of(env);
            if (full == null) return;
            foreach (var id in full.Modified)
                if (category == null || !full.CategoryOf.TryGetValue(id, out var c) || string.Equals(c, category, StringComparison.Ordinal))
                    grp.FullIds.Add(id);
        }

        /// <summary>Name each type group so no two read the same: "Family: Type" whenever the family is known and is not the
        /// type's own name; and when two would still read the same (no family given), each gets its id.</summary>
        internal static void LabelTypes(List<TypeGroup> groups)
        {
            var types = groups.Where(g => g.IsType).ToList();
            foreach (var g in types)
                g.Label = g.Family.Length > 0 && !string.Equals(g.Family, g.TypeName, StringComparison.Ordinal) ? g.Family + ": " + g.TypeName : "";
            foreach (var same in types.GroupBy(g => g.Title, StringComparer.Ordinal).Where(x => x.Count() > 1).ToList())
                foreach (var g in same)
                    g.Label = g.Title + " (id " + string.Join(", ", g.Ids.Select(id => id.ToString(CultureInfo.InvariantCulture))) + ")";
        }

        private static void SetHeadline(ResultCardModel m, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            string N(int n) => n.ToString(CultureInfo.InvariantCulture);
            switch (m.Kind)
            {
                case ResultKind.Changes:
                {
                    if (m.Groups.Count == 0 && m.CategoryCounts.Count > 0)
                    {
                        var total = m.CategoryCounts.Sum(c => c.Count);
                        m.Headline = ms ? N(total) + " elemen diubah" : N(total) + " element" + (total == 1 ? "" : "s") + " changed";
                        m.Counts = m.Detail = string.Join(" · ", m.CategoryCounts.Take(4).Select(c => c.Category + " " + N(c.Count)))
                            + (m.CategoryCounts.Count > 4 ? (ms ? " · +" + N(m.CategoryCounts.Count - 4) + " kategori lagi" : " · +" + N(m.CategoryCounts.Count - 4) + " more categories") : "");
                        break;
                    }
                    if (m.ChangeCount == 0 && m.ChangedBackRows > 0)
                    {
                        // Everything written was put back.
                        m.Headline = ms ? "Tiada perubahan bersih" : "No net change";
                        m.Counts = m.Detail = ms ? N(m.ChangedBackTypes) + " dipulihkan balik" : N(m.ChangedBackRows) + " value" + (m.ChangedBackRows == 1 ? "" : "s") + " written, then put back";
                        break;
                    }
                    var typeGroups = m.Groups.Count(g => g.IsType && !g.ChangedBack);
                    m.Headline = ms ? N(m.ChangeCount) + " perubahan diterapkan" : N(m.ChangeCount) + " change" + (m.ChangeCount == 1 ? "" : "s") + " applied";
                    var live = m.Groups.Count(g => !g.ChangedBack);
                    var what = typeGroups > 0
                        ? (ms ? N(live) + " jenis · " + N(m.InstanceCount) + " elemen" : N(live) + " type" + (live == 1 ? "" : "s") + " · " + N(m.InstanceCount) + " elements")
                        : (ms ? N(m.InstanceCount) + " elemen" : N(m.InstanceCount) + " element" + (m.InstanceCount == 1 ? "" : "s"));
                    if (m.ChangedBackOpen > 0) what += ms ? " · " + N(m.ChangedBackOpen) + " dipulihkan balik" : " · " + N(m.ChangedBackOpen) + " changed back";
                    m.Counts = what;
                    m.Detail = what + (ms ? " · dibaca balik dari Revit" : " · read back from Revit");
                    if (m.Mismatches > 0) m.Detail += ms ? " · " + N(m.Mismatches) + " tak sepadan" : " · " + N(m.Mismatches) + " mismatch" + (m.Mismatches == 1 ? "" : "es");
                    m.Detail += WarningsNote(m, ms);
                    break;
                }
                case ResultKind.Created:
                    m.Headline = m.CreatedBy?.Headline(lang) ?? (ms ? N(m.Created.Count) + " dicipta" : N(m.Created.Count) + " created");
                    m.Detail = (ms ? "Undo membuang semuanya dalam satu langkah" : "Undo removes them in one step") + WarningsNote(m, ms);
                    break;
                case ResultKind.Findings:
                {
                    var ids = m.Findings.Sum(f => f.Ids.Count);
                    var kinds = ms ? N(m.Findings.Count) + " jenis isu" : N(m.Findings.Count) + " kind" + (m.Findings.Count == 1 ? "" : "s") + " of issue";
                    // Live R11, E-c07: Revit rolled a type change back and the card read "1 kind of issue · 16 fixable
                    // automatically". The "fixable" count was every element of an error-severity warning, and nothing on the
                    // card (or anywhere in the add-in) fixes one: the rows carry only Show. After a rollback nothing changed and
                    // there is nothing to fix; the line says what Revit refused and how many elements it named. No card claims
                    // an automatic fix any more.
                    if (m.RolledBack)
                    {
                        m.Headline = ms ? "Tiada perubahan: Revit membatalkan perubahan" : "Nothing changed: Revit rolled the change back";
                        m.Detail = (ms ? "Revit menolak: " : "Revit refused: ") + kinds
                            + (ids > 0 ? (ms ? " · " + N(ids) + " elemen dinamakan" : " · " + N(ids) + " element" + (ids == 1 ? "" : "s") + " named") : "");
                        break;
                    }
                    m.Headline = ids > 0
                        ? (ms ? N(ids) + " elemen gagal" : N(ids) + " element" + (ids == 1 ? "" : "s") + " fail")
                        : (ms ? N(m.Findings.Count) + " isu" : N(m.Findings.Count) + " issue" + (m.Findings.Count == 1 ? "" : "s"));
                    m.Detail = kinds;
                    break;
                }
                case ResultKind.Selection:
                    m.Headline = m.Selected == 0 && m.Isolated ? (ms ? "View diasingkan" : "View isolated")
                               : ms ? N(m.Selected) + " dipilih" : N(m.Selected) + " selected";
                    m.Detail = m.VisibleInView > 0 ? (ms ? N(m.VisibleInView) + " kelihatan dalam view" : N(m.VisibleInView) + " visible in view") : "";
                    break;
            }
            // The server's one-sentence reply (turn_end.result.reply) is the headline whenever it sent one.
            if (m.ServerReply.Length > 0 && m.Kind != ResultKind.None) m.Headline = m.ServerReply;
        }

        /// <summary>Warnings that repeat one message with one severity (20 rows of "Changes to groups are allowed only in group edit
        /// mode") become one finding with the union of their ids and a <see cref="Finding.Repeats"/> count. Rule findings (they
        /// carry a Handle) are never folded; first-seen order is kept.</summary>
        public static List<Finding> FoldRepeatedFindings(List<Finding> findings)
        {
            var folded = new List<Finding>();
            foreach (var g in RepeatFold.Fold(findings, f => f.FromRule || f.Handle != null ? null : f.Severity + "\u0001" + (f.DefinitionId ?? "") + "\u0001" + f.Title, f => f.Ids))
            {
                if (!g.IsFolded) { folded.Add(g.First); continue; }
                folded.Add(new Finding
                {
                    Title = g.First.Title, Severity = g.First.Severity, Ids = g.Ids, DefinitionId = g.First.DefinitionId, FromRule = g.First.FromRule,
                    Fixable = g.Members.Any(f => f.Fixable), Repeats = g.Count,
                });
            }
            return folded;
        }

        /// <summary>" · 2 warnings" for a card with commit warnings (AI-332), else "".</summary>
        private static string WarningsNote(ResultCardModel m, bool ms)
        {
            var n = m.Warnings.Count;
            if (n == 0) return "";
            var count = n.ToString(CultureInfo.InvariantCulture);
            return ms ? " · " + count + " amaran" : " · " + count + " warning" + (n == 1 ? "" : "s");
        }

        private static string Label(Envelope env, long id) => CreatedLabel(env, id);

        /// <summary>The name data.created gives a created element, else "Element &lt;id&gt;"
        /// (ids beyond the 50-row cap, and envelopes with no data.created, have no name).</summary>
        public static string CreatedLabel(Envelope? env, long id)
        {
            try
            {
                var name = ToolResults.Write(env)?.Created.FirstOrDefault(c => c.Id == id)?.Name;
                if (!string.IsNullOrWhiteSpace(name)) return name!;
            }
            catch (Exception) { /* fall back to the id */ }
            return "Element " + id.ToString(CultureInfo.InvariantCulture);
        }
        private static string? Str(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
