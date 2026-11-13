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
    /// <summary>Why a mark is there; the colour says it and the key names the colour.</summary>
    public enum MarkOutcome { Made, Changed, NeedsLook, Skipped }

    /// <summary>What the mark is drawn on: an annotation gets a frame, an element a ring, a type its instances.</summary>
    public enum MarkKind { Element, Annotation, Dimension, TypeInstances }

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

        public bool IsEmpty => Kinds.All(g => g.Ids.Count == 0);

        public int Total(MarkOutcome o) => Kinds.Where(g => g.Outcome == o).Sum(g => g.Count);

        /// <summary>The token of an outcome's colour (Cp.Mark.*): light value for a white canvas, dark for a dark one.</summary>
        public static string TokenOf(MarkOutcome o)
        {
            switch (o)
            {
                case MarkOutcome.Made: return "Cp.Mark.Made";
                case MarkOutcome.Changed: return "Cp.Mark.Changed";
                case MarkOutcome.NeedsLook: return "Cp.Mark.Attn";
                default: return "Cp.Mark.Skip";
            }
        }

        /// <summary>The outcome's colour on a dark or a white canvas.</summary>
        public static string ColorOf(MarkOutcome o, bool darkCanvas) => ThemeTokens.Hex(TokenOf(o), darkCanvas)!;

        /// <summary>Dashed: the mark is a question, not a result.</summary>
        public static bool Dashed(MarkOutcome o) => o == MarkOutcome.NeedsLook || o == MarkOutcome.Skipped;

        /// <summary>Which outcome a point shows when its element is in several groups: a question first, then a change.</summary>
        public static int Priority(MarkOutcome o) => o == MarkOutcome.NeedsLook ? 3 : o == MarkOutcome.Changed ? 2 : o == MarkOutcome.Made ? 1 : 0;

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
            if (card.Kind == ResultKind.None || card.Kind == ResultKind.Selection) return set;

            var commits = (envelopes ?? Array.Empty<Envelope>()).Where(e => e != null && e.PhaseKind() == EnvelopePhaseKind.Commit
                && (e.StatusKind() == EnvelopeStatusKind.Done || e.StatusKind() == EnvelopeStatusKind.Partial)).ToList();

            // Types whose instances carry a change (type parameters): one group per type group of the card.
            var typeIds = new HashSet<long>();
            foreach (var g in card.Groups.Where(g => g.IsType && !g.ChangedBack))
            {
                set.Kinds.Add(new MarkGroup
                {
                    Title = g.TypeName, Outcome = MarkOutcome.Changed, Kind = MarkKind.TypeInstances,
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
                foreach (var id in added)
                {
                    var cat = CategoryOf(id, env.Changes?.TouchedByCategory?.AddedByCategory);
                    if (CreatedSummary.IsByProduct(cat) && !CreatedSummary.IsGeneric(cat) && cat.Length > 0) continue;   // sketch planes, graphics styles…
                    if (!seenMade.Add(id)) continue;
                    Bucket(made, cat).Add(id);
                }
                foreach (var id in modified)
                {
                    if (typeIds.Contains(id) || seenMade.Contains(id) || putBack.Contains(id) || !seenChanged.Add(id)) continue;
                    Bucket(changed, CategoryOf(id, env.Changes?.TouchedByCategory?.ModifiedByCategory)).Add(id);
                }
            }
            set.FullLists = anyFull;
            foreach (var kv in made.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                set.Kinds.Add(Group(kv.Key, MarkOutcome.Made, kv.Value));
            foreach (var kv in changed.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                set.Kinds.Add(Group(kv.Key, MarkOutcome.Changed, kv.Value));
            // A card built only from the envelopes' rows (no commit list): its created items.
            if (commits.Count == 0 && card.Created.Count > 0)
                set.Kinds.Add(Group("", MarkOutcome.Made, card.Created.Select(c => c.Id).Distinct().ToList()));

            // Needs a look: values that read back different, then the findings of a check.
            var mismatch = card.Groups.SelectMany(g => g.Rows).Where(r => r.NeedsCheck).SelectMany(r => r.Ids).Distinct().ToList();
            if (mismatch.Count > 0)
                set.Kinds.Add(new MarkGroup { Title = "Read back different", Outcome = MarkOutcome.NeedsLook, Kind = MarkKind.Element, Ids = mismatch, Count = mismatch.Count });
            foreach (var f in card.Findings.Where(f => f.Ids.Count > 0))
                set.Kinds.Add(new MarkGroup { Title = f.Title, Outcome = MarkOutcome.NeedsLook, Kind = MarkKind.Element, Ids = f.Ids.Distinct().ToList(), Count = f.Ids.Distinct().Count() });

            set.Rows = RowsOf(card, set.Kinds);
            return set;
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
                    Title = g.TypeName, Category = g.IsType ? "" : g.TypeName, Outcome = MarkOutcome.Changed,
                    Kind = g.IsType ? MarkKind.TypeInstances : KindOf(g.TypeName),
                    Ids = g.Ids.ToList(), Count = g.Instances > 0 ? g.Instances : g.Ids.Count,
                });
            return rows;
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
