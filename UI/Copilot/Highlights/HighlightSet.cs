#nullable enable
// What a turn's result marks in the model, as mark groups (result marks design, Nov 2026).
// Revit-free.
//
//   Kinds  one group per outcome and kind of element ("New door tag", 58 ids): what is
//          drawn and what the key lists, one line each. The ids are the add-in's own
//          full commit lists (CommitIds) when it kept them, so the count is the true one
//          (58, not the 50 the envelope's rows allow); else the envelope's rows.
//   Rows   one group per row of the result card (a server item row such as a door type,
//          a type group, an element group), with that row's own ids and count. Step 2
//          (a row click focuses its marks) uses them; nothing draws them yet.
//
// Colour is the outcome: made (green), changed (violet), needs a look (amber, dashed);
// skipped (grey) is listed only. Never blue: blue is Revit's own selection. A selection
// result therefore draws no marks (Revit's selection is its mark), and a deletion has
// nothing left in the model to mark.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BinaVibe.Copilot.Contracts;
using BinaVibe.Copilot.Writes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>Why a mark is there; the colour says it and the key names the colour. Found (r12): a find's results and a card's
    /// Show, in magenta (never Revit's selection blue, never the bitmap's teal clear colour).</summary>
    public enum MarkOutcome { Made, Changed, NeedsLook, Skipped, Found }

    /// <summary>What the mark is drawn on: an annotation gets a frame, an element a ring, a type its instances.</summary>
    public enum MarkKind { Element, Annotation, Dimension, TypeInstances }

    /// <summary>What a card's Show click draws (<see cref="HighlightSet.FoundClickFor"/>).</summary>
    public enum FoundClick { KeepTurnMarks, ShowTurnResult, ShowFound }

    public sealed class MarkGroup
    {
        /// <summary>The kind's category ("Door Tags") or the card row's title ("(PTh300a) 750 x 2100 sd-d").</summary>
        public string Title = "";
        /// <summary>The Revit category the ids belong to, "" when not known.</summary>
        public string Category = "";
        public MarkOutcome Outcome;
        public MarkKind Kind;
        /// <summary>Element ids; for <see cref="MarkKind.TypeInstances"/>, type ids whose instances carry the mark.</summary>
        public List<long> Ids = new List<long>();
        /// <summary>The true count: the ids, or a row's or type's own count when it is larger than the ids it carries.</summary>
        public int Count;
        /// <summary>Why it was skipped or could not be done (step 3, from the result's outcome); null otherwise.</summary>
        public string? Reason;
        /// <summary>The title is a name from the model (a server row: "Door A: 750 → Door B: 900"), not a category: the key
        /// shows it as written, never lower-cased or made singular.</summary>
        public bool Named;
    }

    public sealed class HighlightSet
    {
        public List<MarkGroup> Kinds = new List<MarkGroup>();
        public List<MarkGroup> Rows = new List<MarkGroup>();
        public ResultCardModel? Card;
        public string Headline = "";
        /// <summary>The turn whose result this is (the card's "Hide marks" / "Show in model" ask about it).</summary>
        public string TurnId = "";
        /// <summary>When the turn ended, as the pane shows it ("2:35 PM").</summary>
        public string Time = "";
        /// <summary>True when the counts came from the add-in's own full lists (CommitIds).</summary>
        public bool FullLists;

        /// <summary>r12: the marks are Revit's selection when the set is drawn (a find with highlight:true selected its results; the
        /// envelope carries no ids). InModelResult reads the selection in Revit's context and fills the one Found group.</summary>
        public bool FromSelection;

        public bool IsEmpty => !FromSelection && Kinds.All(g => g.Ids.Count == 0);

        /// <summary>Found marks for these element ids (a card's Show / "Highlight in model"): no transaction, no Undo entry.</summary>
        public static HighlightSet Found(IEnumerable<long> ids, string turnId = "")
        {
            var list = (ids ?? Array.Empty<long>()).Where(id => id > 0).Distinct().ToList();
            var set = new HighlightSet { TurnId = turnId ?? "" };
            set.Kinds.Add(new MarkGroup { Title = "Found", Outcome = MarkOutcome.Found, Kind = MarkKind.Element, Ids = list, Count = list.Count });
            set.Headline = MarkKey.Title(set.Kinds);
            return set;
        }

        /// <summary>What a card's Show / "Highlight in model" click draws (r12 audit): a turn with its own result marks keeps them
        /// (shown: left as they are, the click only selects; not shown: that result is drawn, with its Undo); only a turn with
        /// nothing of its own to mark gets Found marks, under its own turn id so the card's Hide / Show follows them.</summary>
        public static FoundClick FoundClickFor(bool turnHasOwnMarks, MarksState turnState)
        {
            if (!turnHasOwnMarks) return FoundClick.ShowFound;
            return turnState == MarksState.None ? FoundClick.ShowTurnResult : FoundClick.KeepTurnMarks;
        }

        /// <summary>Found marks for what Revit holds selected when the set is drawn (a find's highlight at the end of its turn).</summary>
        public static HighlightSet FoundInSelection(string turnId = "")
        {
            var set = Found(Array.Empty<long>(), turnId);
            set.FromSelection = true;
            return set;
        }

        /// <summary>r13 (0.0.148): no result set selects at the end of its turn. Live fb713fc2 on 0.0.147 (frame R7-18): after the section
        /// box was made, 3 of its elements stayed selected in the new view ("Modify | Multi-Select"); a write turn draws its Made and Changes
        /// marks only. A find selects through its own tool (FindExecutor, read path) and a run whose code asks to select does so through its
        /// UI requests, so what the drafter asked for is still selected. Select all on the card, a row click and a mark click still select.</summary>
        public bool SelectsAtTheEnd => false;

        /// <summary>The turn changed or made elements (Made or Changes marks with ids): what G1 used to select at the end.</summary>
        public bool HasWriteMarks => Kinds.Any(k => (k.Outcome == MarkOutcome.Made || k.Outcome == MarkOutcome.Changed) && k.Ids.Count > 0);

        public int Total(MarkOutcome o) => Kinds.Where(g => g.Outcome == o).Sum(g => g.Count);

        /// <summary>The token of an outcome's colour (Cp.Mark.*): light value for a white canvas, dark for a dark one.</summary>
        public static string TokenOf(MarkOutcome o)
        {
            switch (o)
            {
                case MarkOutcome.Made: return "Cp.Mark.Made";
                case MarkOutcome.Changed: return "Cp.Mark.Changed";
                case MarkOutcome.NeedsLook: return "Cp.Mark.Attn";
                case MarkOutcome.Found: return "Cp.Mark.Found";
                default: return "Cp.Mark.Skip";
            }
        }

        /// <summary>The outcome's colour on a dark or a white canvas.</summary>
        public static string ColorOf(MarkOutcome o, bool darkCanvas) => ThemeTokens.Hex(TokenOf(o), darkCanvas)!;

        /// <summary>Dashed: the mark is a question, not a result.</summary>
        public static bool Dashed(MarkOutcome o) => o == MarkOutcome.NeedsLook || o == MarkOutcome.Skipped;

        /// <summary>Which outcome a point shows when its element is in several groups: a question first, then a change.</summary>
        public static int Priority(MarkOutcome o) => o == MarkOutcome.NeedsLook ? 3 : o == MarkOutcome.Changed ? 2 : o == MarkOutcome.Made || o == MarkOutcome.Found ? 1 : 0;

        /// <summary>The mark an element of this category gets.</summary>
        public static MarkKind KindOf(string? category)
        {
            var c = (category ?? "").Trim();
            if (c.IndexOf("Dimension", StringComparison.OrdinalIgnoreCase) >= 0) return MarkKind.Dimension;
            if (c.EndsWith("Tags", StringComparison.OrdinalIgnoreCase) || c.EndsWith("Tag", StringComparison.OrdinalIgnoreCase)
                || c.IndexOf("Text Notes", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("Keynote", StringComparison.OrdinalIgnoreCase) >= 0
                || c.IndexOf("Spot ", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("Generic Annotations", StringComparison.OrdinalIgnoreCase) >= 0)
                return MarkKind.Annotation;
            return MarkKind.Element;
        }

        public static HighlightSet From(ResultCardModel card, IReadOnlyList<Envelope> envelopes)
        {
            var set = new HighlightSet();
            if (card == null) return set;
            set.Card = card; set.Headline = card.Headline;
            if (card.Kind == ResultKind.None) return set;
            // r12 (live 19 Nov, 46d35bc5): a find's card with element rows draws Found marks on those rows' ids. A "Selection" card,
            // or a grouped answer that WithServerResult turned into a Created tally (Selected = 0), had no marks, no Show button.
            var readOnly = ReadOnlyTurn(envelopes);
            if (card.Kind == ResultKind.Selection) { if (readOnly) AddFoundRows(set, card); set.Rows = RowsOf(card, set.Kinds); return set; }

            var commits = (envelopes ?? Array.Empty<Envelope>()).Where(e => e != null && e.PhaseKind() == EnvelopePhaseKind.Commit
                && (e.StatusKind() == EnvelopeStatusKind.Done || e.StatusKind() == EnvelopeStatusKind.Partial)).ToList();

            // Types whose instances carry a change (type parameters): one group per type group of the card.
            var typeIds = new HashSet<long>();
            foreach (var g in card.Groups.Where(g => g.IsType && !g.ChangedBack))
            {
                set.Kinds.Add(new MarkGroup
                {
                    Title = g.Title, Outcome = MarkOutcome.Changed, Kind = MarkKind.TypeInstances,
                    Ids = g.Ids.ToList(), Count = g.Instances > 0 ? g.Instances : g.Ids.Count,
                });
                foreach (var id in g.Ids) typeIds.Add(id);
            }
            // Elements whose every change was put back hold no change.
            var putBack = new HashSet<long>(card.Groups.SelectMany(g => g.Rows).Where(r => r.ChangedBack).SelectMany(r => r.Ids));
            var stillChanged = new HashSet<long>(card.Groups.SelectMany(g => g.Rows).Where(r => !r.ChangedBack).SelectMany(r => r.Ids));
            putBack.ExceptWith(stillChanged);

            // Made and changed, by category, from the full lists.
            var made = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            var changed = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            var seenMade = new HashSet<long>(); var seenChanged = new HashSet<long>();
            var madeAlong = new HashSet<long>();   // added with a listed creation (its camera, section box, ...): neither made nor changed
            var anyFull = false;
            foreach (var env in commits)
            {
                var full = CommitIds.Of(env);
                anyFull |= full != null;
                var t = env.Changes?.Touched;
                var added = full?.Added ?? (t?.Added ?? new List<ElementRef>()).Where(r => r.Link == null).Select(r => r.Id).ToList();
                var modified = full?.Modified ?? (t?.Modified ?? new List<FieldChangeDto>()).Where(r => r.Scope != ResultTargetScopes.Type).Select(r => r.Id).Distinct().ToList();
                string CategoryOf(long id, Dictionary<string, int>? byCat)
                {
                    if (full != null && full.CategoryOf.TryGetValue(id, out var c)) return c;
                    var only = (byCat ?? new Dictionary<string, int>()).Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
                    return only.Count == 1 ? only[0] : "";
                }
                // r13 (0.0.148), live fb713fc2: the commit's own data.created (what the card's rows count, "Views 1 and 10 supporting")
                // leads the Made marks. What Revit made along with a listed view or sheet (its camera, its section box, a viewport;
                // an id of no known category) is not a mark ("3 elements made" for one view was wrong). Any other addition (walls or
                // tags the same run placed, which data.created never lists) stays a Made mark as before.
                var listed = ListedCreated(env);
                if (listed != null)
                {
                    var listedIds = new HashSet<long>(listed.Select(c => c.Id));
                    var rest = new List<long>();
                    foreach (var id in added)
                    {
                        if (listedIds.Contains(id)) continue;
                        var cat = CategoryOf(id, env.Changes?.TouchedByCategory?.AddedByCategory);
                        if (MadeAlongWithCreated(cat)) madeAlong.Add(id); else rest.Add(id);
                    }
                    foreach (var (id, listedCat) in listed)
                    {
                        if (!seenMade.Add(id)) continue;
                        Bucket(made, full != null && full.CategoryOf.TryGetValue(id, out var fc) ? fc : listedCat).Add(id);
                    }
                    added = rest;
                }
                foreach (var id in added)
                {
                    var cat = CategoryOf(id, env.Changes?.TouchedByCategory?.AddedByCategory);
                    if (CreatedSummary.IsByProduct(cat) && !CreatedSummary.IsGeneric(cat) && cat.Length > 0) continue;   // sketch planes, graphics styles…
                    // A change the server lists (round 9, c01): what Revit added on the side is never a mark of it.
                    if (card.ServerChanges && CreatedSummary.IsByProduct(cat)) continue;
                    if (!seenMade.Add(id)) continue;
                    Bucket(made, cat).Add(id);
                }
                foreach (var id in modified)
                {
                    if (typeIds.Contains(id) || seenMade.Contains(id) || madeAlong.Contains(id) || putBack.Contains(id) || !seenChanged.Add(id)) continue;
                    Bucket(changed, CategoryOf(id, env.Changes?.TouchedByCategory?.ModifiedByCategory)).Add(id);
                }
            }
            set.FullLists = anyFull;
            // The server's rows of a change (round 9, c01: 20 doors of the old type, every id): changed, each row its own group,
            // for the ids no envelope list already marked. The row's title is a name (live 14 Nov, E-c01: it was taken for a
            // category and lower-cased), so the group keeps it as written and has no category.
            var named = new List<MarkGroup>();
            if (card.ServerChanges)
                foreach (var r in (card.Items?.Sections ?? new List<ItemSection>()).SelectMany(s => s.Rows))
                {
                    var ids = r.Ids.Where(id => !seenMade.Contains(id) && seenChanged.Add(id)).ToList();
                    if (ids.Count > 0)
                        named.Add(new MarkGroup { Title = r.Title.Trim(), Outcome = MarkOutcome.Changed, Kind = MarkKind.Element, Ids = ids, Count = ids.Count, Named = true });
                }
            foreach (var kv in made.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                set.Kinds.Add(Group(kv.Key, MarkOutcome.Made, kv.Value));
            foreach (var kv in changed.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var g = Group(kv.Key, MarkOutcome.Changed, kv.Value);
                // No full list kept (a card from history): the envelope's rows stop at 50, and the key counts what the card
                // counts as written (live 14 Nov, SP-4: 60 doors), the rest "not visible here", never a smaller total.
                if (!anyFull)
                    g.Count = Math.Max(g.Count, card.Groups.Where(x => !x.IsType && !x.AnyChangedBack && Clean(x.TypeName) == g.Category)
                        .Select(x => x.Instances).DefaultIfEmpty(0).Max());
                set.Kinds.Add(g);
            }
            set.Kinds.AddRange(named.OrderByDescending(g => g.Count));
            // A card built only from the envelopes' rows (no commit list): its created items.
            if (commits.Count == 0 && card.Created.Count > 0)
                set.Kinds.Add(Group("", MarkOutcome.Made, card.Created.Select(c => c.Id).Distinct().ToList()));

            // Needs a look: values that read back different, then the findings of a check.
            var mismatch = card.Groups.SelectMany(g => g.Rows).Where(r => r.NeedsCheck).SelectMany(r => r.Ids).Distinct().ToList();
            if (mismatch.Count > 0)
                set.Kinds.Add(new MarkGroup { Title = "Read back different", Outcome = MarkOutcome.NeedsLook, Kind = MarkKind.Element, Ids = mismatch, Count = mismatch.Count });
            foreach (var f in card.Findings.Where(f => f.Ids.Count > 0))
                set.Kinds.Add(new MarkGroup { Title = f.Title, Outcome = MarkOutcome.NeedsLook, Kind = MarkKind.Element, Ids = f.Ids.Distinct().ToList(), Count = f.Ids.Distinct().Count() });

            // A read-only turn (every call a read: no preview, no failed write) with nothing else to mark: its rows are what it found.
            if (readOnly && set.Kinds.Count == 0) AddFoundRows(set, card);

            set.Rows = RowsOf(card, set.Kinds);
            return set;
        }

        /// <summary>r13 (0.0.148): a commit's data.created as (id, category), the add-in's own list of what the run made for the drafter
        /// (RevitWriteEngine.CreatedItems). Null when it lists no id, or when it is cut at its cap (CreatedSummary.CreatedListCap): then
        /// the marks come from every id the commit added, as before.</summary>
        internal static List<(long Id, string Category)>? ListedCreated(Envelope env)
        {
            var created = ToolResults.Write(env)?.Created;
            if (created == null || created.Count == 0 || created.Count >= CreatedSummary.CreatedListCap) return null;
            var list = created.Where(c => c.Id != null).Select(c => (c.Id!.Value, (c.Category ?? "").Trim())).Distinct().ToList();
            return list.Count == 0 ? null : list;
        }

        /// <summary>r13 (0.0.148): what Revit makes along with a view or sheet the run listed in data.created: never a Made mark of that
        /// run. Local to the marks of a listed creation; CreatedSummary.IsByProduct (also used for edits) is unchanged.</summary>
        internal static bool MadeAlongWithCreated(string? category)
        {
            var c = (category ?? "").Trim();
            return c.Length == 0 || CreatedSummary.IsByProduct(c) || AlongWithCreated.Contains(c);
        }

        private static readonly HashSet<string> AlongWithCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cameras", "Section Boxes", "Viewports", "Elevations", "Sections", "Crop Boundaries",
        };

        /// <summary>r12 audit: a turn that only read. At least one envelope, and every one ok, done (or partial) and phase read: a failed
        /// write's validation (ToolContracts.Failure) and a "changed the model although classified read-only" rejection both carry
        /// phase read, and must never pass for a find.</summary>
        internal static bool ReadOnlyTurn(IReadOnlyList<Envelope>? envelopes)
        {
            var list = (envelopes ?? Array.Empty<Envelope>()).Where(e => e != null).ToList();
            return list.Count > 0 && list.All(e => e.Ok && e.PhaseKind() == EnvelopePhaseKind.Read
                && (e.StatusKind() == EnvelopeStatusKind.Done || e.StatusKind() == EnvelopeStatusKind.Partial));
        }

        /// <summary>r12: Found groups for the ids of a card's element and tag rows (what a find listed), at most one group per mark kind.
        /// No transaction, no Undo entry; the turn's find selected them already, so the set never reselects (SelectsAtTheEnd).</summary>
        private static void AddFoundRows(HighlightSet set, ResultCardModel card)
        {
            if (card.NoChange || card.Failed) return;   // a failed turn found nothing to show   // a write that changed nothing lists the element it looked at: nothing was found, nothing to mark
            var rows = (card.Items?.Sections ?? new List<ItemSection>()).SelectMany(s => s.Rows).Where(r => r.Button == ItemButton.Select).ToList();
            foreach (var kind in new[] { MarkKind.Element, MarkKind.Annotation })
            {
                var ids = rows.Where(r => (r.Kind == ResultItemKinds.Tag ? MarkKind.Annotation : MarkKind.Element) == kind)
                    .SelectMany(r => r.Ids).Where(id => id > 0).Distinct().ToList();
                if (ids.Count > 0) set.Kinds.Add(new MarkGroup { Title = "Found", Outcome = MarkOutcome.Found, Kind = kind, Ids = ids, Count = ids.Count });
            }
        }

        /// <summary>One group per row of the card that names marked elements: the server's item rows (a door type with its
        /// tags), then the card's type and element groups. A row whose ids are none of the marked ones (the view row) is not one.</summary>
        internal static List<MarkGroup> RowsOf(ResultCardModel card, IReadOnlyList<MarkGroup> kinds)
        {
            var rows = new List<MarkGroup>();
            var outcomeOf = new Dictionary<long, MarkGroup>();
            foreach (var k in kinds.Where(k => k.Kind != MarkKind.TypeInstances))
                foreach (var id in k.Ids)
                    if (!outcomeOf.TryGetValue(id, out var have) || Priority(k.Outcome) > Priority(have.Outcome)) outcomeOf[id] = k;
            foreach (var section in card.Items?.Sections ?? new List<ItemSection>())
                foreach (var r in section.Rows)
                {
                    var hit = r.Ids.Where(outcomeOf.ContainsKey).ToList();
                    // A row whose ids the server did not keep (it keeps only ids a tool result showed) still names marked
                    // elements when it has a count; a row whose ids are something else (the view the tags went into) does not.
                    var k = hit.Count > 0 ? outcomeOf[hit[0]]
                          : r.Ids.Count == 0 && (r.Count ?? 0) > 0 ? kinds.FirstOrDefault(x => x.Kind != MarkKind.TypeInstances) : null;
                    if (k == null) continue;
                    rows.Add(new MarkGroup
                    {
                        Title = r.Title, Category = k.Category, Outcome = k.Outcome, Kind = k.Kind,
                        Ids = hit, Count = Math.Max(r.Count ?? 0, hit.Count),
                    });
                }
            foreach (var g in card.Groups.Where(g => !g.ChangedBack))
                rows.Add(new MarkGroup
                {
                    Title = g.Title, Category = g.IsType ? "" : g.TypeName, Outcome = MarkOutcome.Changed,
                    Kind = g.IsType ? MarkKind.TypeInstances : KindOf(g.TypeName),
                    Ids = g.Ids.ToList(), Count = g.Instances > 0 ? g.Instances : g.Ids.Count,
                });
            return rows;
        }

        /// <summary>A type's instances among its dependents: the elements whose type is one of <paramref name="typeIds"/>, each
        /// once, in order. A type's dependents also hold its instances' tags and legend components, which are not instances.</summary>
        public static List<long> InstancesOnly(IEnumerable<(long Id, long TypeId)> dependents, IReadOnlyCollection<long> typeIds)
        {
            var want = new HashSet<long>(typeIds ?? Array.Empty<long>());
            var seen = new HashSet<long>();
            return (dependents ?? Array.Empty<(long, long)>()).Where(d => want.Contains(d.TypeId) && seen.Add(d.Id)).Select(d => d.Id).ToList();
        }

        private static List<long> Bucket(Dictionary<string, List<long>> into, string cat)
        {
            var key = Clean(cat);
            if (!into.TryGetValue(key, out var list)) into[key] = list = new List<long>();
            return list;
        }

        private static MarkGroup Group(string category, MarkOutcome outcome, List<long> ids)
        {
            var cat = Clean(category);
            return new MarkGroup { Title = cat, Category = cat, Outcome = outcome, Kind = KindOf(cat), Ids = ids, Count = ids.Count };
        }

        /// <summary>"Door Tags"; "class:Foo" → "Foo"; generic buckets and "" → "Elements".</summary>
        internal static string Clean(string? raw)
        {
            var c = (raw ?? "").Trim();
            if (c.StartsWith("class:", StringComparison.Ordinal)) c = c.Substring(6);
            return c.Length == 0 || CreatedSummary.IsGeneric(c) ? "Elements" : c;
        }

        internal static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
    }
}
