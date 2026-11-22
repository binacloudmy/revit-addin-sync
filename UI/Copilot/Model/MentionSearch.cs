using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Read;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>One pickable thing in the flat @ search: a type, level, view,
    /// sheet, family, category or the current selection.</summary>
    public class MentionItem
    {
        public string Kind;        // type | level | view | sheet | family | category | selection
        public string Name;        // the element's own name (a type's name, not "Family: Type")
        public string Family;      // type
        public string Category;    // type / family / category ("Doors")
        public string CategoryKey; // BuiltInCategory name when known ("OST_Doors"); for Malay words in the message
        public string TypeMark;
        public int? Count;         // instances in the model, null = not counted
        public int? InView;        // instances in the active view, null = not counted
        public bool IsActive;      // the active view
        public bool SharedName;    // another family has a type of the same name
        public MentionSelection Selection;
        public MentionNode Node;   // the tree node it came from

        // Folded search text, filled by MentionSearch.Index.
        internal string NameF = "", FamilyF = "", MarkF = "", CategoryF = "";

        /// <summary>The faint second line of a picker row: "Pintu Tunggal · Doors · Mark D1".</summary>
        public string Detail
        {
            get
            {
                switch (Kind)
                {
                    case "type":
                    {
                        var parts = new List<string>();
                        if (!string.IsNullOrEmpty(Family)) parts.Add(Family);
                        if (!string.IsNullOrEmpty(Category)) parts.Add(Category);
                        if (!string.IsNullOrEmpty(TypeMark)) parts.Add("Mark " + TypeMark);
                        return string.Join(" · ", parts);
                    }
                    case "family": return string.IsNullOrEmpty(Category) ? "Family" : Category + " family";
                    case "selection": return MentionText.SelectionLabel(Selection);
                    case "view": return IsActive ? "Active view" : "";
                    default: return "";
                }
            }
        }

        /// <summary>The right-hand count: "24 · 3 here" (model · active view), "24", or "".</summary>
        public string CountText
        {
            get
            {
                if (Kind != "type" || Count == null) return "";
                return InView > 0 ? Count + " · " + InView + " here" : Count.ToString();
            }
        }

        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case "level": return "Level";
                    case "category": return "Category";
                    case "family": return "Family";
                    case "type": return "Type";
                    case "view": return "View";
                    case "sheet": return "Sheet";
                    case "selection": return "Selection";
                    default: return "";
                }
            }
        }
    }

    /// <summary>
    /// The flat @ search: one ranked list across kinds, built once per picker
    /// opening from the provider's tree. Forgiving match (case, accents and
    /// punctuation ignored; each word of the query anywhere, in any order; the
    /// family, Type Mark and category count too). WPF-free and Revit-free.
    /// </summary>
    public static class MentionSearch
    {
        /// <summary>Most rows the picker shows for a query.</summary>
        public const int MaxResults = 40;

        /// <summary>Lower case, accents dropped, every run of punctuation one space.</summary>
        public static string Normalize(string s) => Fuzzy.Fold(s);

        /// <summary>Every pickable node of the tree as a search item. A type's family
        /// and category come from the node, else from its ancestors in the tree.
        /// Duplicates (the active view listed twice) merge.</summary>
        public static List<MentionItem> Index(IEnumerable<MentionNode> tree)
        {
            var items = new List<MentionItem>();
            var seen = new Dictionary<string, MentionItem>(StringComparer.OrdinalIgnoreCase);
            Walk(tree, null, null, null, items, seen);

            foreach (var g in items.Where(i => i.Kind == "type").GroupBy(i => i.NameF))
                if (g.Select(i => i.FamilyF).Distinct().Count() > 1)
                    foreach (var i in g) i.SharedName = true;
            return items;
        }

        private static void Walk(IEnumerable<MentionNode> nodes, string family, string category, string categoryKey,
                                 List<MentionItem> items, Dictionary<string, MentionItem> seen)
        {
            if (nodes == null) return;
            foreach (var n in nodes)
            {
                if (n == null) continue;
                string fam = family, cat = category, key = n.CategoryKey ?? categoryKey;
                if (n.Kind == "category") { cat = n.Name; key = n.CategoryKey; }
                if (n.Kind == "family") { fam = n.Name; cat = n.Category ?? cat; }

                if (n.Pickable && n.Kind != "group")
                {
                    var item = new MentionItem
                    {
                        Kind = n.Kind, Name = n.Name, Node = n,
                        Family = n.Kind == "type" ? (n.Family ?? family) : null,
                        Category = n.Kind == "category" ? n.Name : (n.Category ?? (n.Kind == "type" || n.Kind == "family" ? category : null)),
                        CategoryKey = n.Kind == "category" || n.Kind == "type" || n.Kind == "family" ? key : null,
                        TypeMark = n.TypeMark, Count = n.Count, InView = n.InView,
                        IsActive = n.IsActive, Selection = n.Selection,
                    };
                    item.NameF = Normalize(item.Name);
                    item.FamilyF = Normalize(item.Family);
                    item.MarkF = Normalize(item.TypeMark);
                    item.CategoryF = Normalize(item.Category);

                    string dedupe = item.Kind + "\u001f" + item.FamilyF + "\u001f" + item.CategoryF + "\u001f" + item.NameF;
                    if (seen.TryGetValue(dedupe, out var prior))
                        prior.IsActive |= item.IsActive;
                    else { seen[dedupe] = item; items.Add(item); }
                }
                Walk(n.Children, fam, cat, key, items, seen);
            }
        }

        /// <summary>The items matching the query, best first. An empty query gives
        /// no rows (the picker shows the browse tree instead).
        /// <paramref name="message"/> is the rest of the message: a category it
        /// names ("swap the door …", "tukar pintu …") ranks that category first.</summary>
        public static List<MentionItem> Search(IList<MentionItem> items, string query, string message = null, int max = MaxResults)
        {
            var tokens = Normalize(query).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (items == null || tokens.Length == 0) return new List<MentionItem>();
            string q = string.Join(" ", tokens);
            var named = NamedCategories(message);

            var hits = new List<(MentionItem item, int tier)>();
            foreach (var it in items)
            {
                int tier = Tier(it, q, tokens);
                if (tier >= 0) hits.Add((it, tier));
            }

            return hits
                .OrderBy(h => h.tier)
                .ThenBy(h => InActiveView(h.item) ? 0 : 1)
                .ThenBy(h => IsNamed(h.item, named) ? 0 : 1)
                .ThenBy(h => KindOrder(h.item.Kind))
                .ThenByDescending(h => h.item.Count ?? 0)
                .ThenBy(h => h.item.NameF.Length)
                .ThenBy(h => h.item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(h => h.item)
                .ToList();
        }

        /// <summary>
        /// How well an item matches, lower is better, -1 = no match.
        /// 0 = its name (or "family type", or its Type Mark) starts with the query;
        /// 1 = every query word starts a word of its name, family or mark;
        /// 2 = every query word is somewhere in its name, family or mark;
        /// 3 = it matches only with its category ("door 900").
        /// </summary>
        public static int Tier(MentionItem it, string q, string[] tokens)
        {
            if (it == null) return -1;
            string primary = Join(it.NameF, it.FamilyF, it.MarkF, it.Kind == "selection" ? "selection selected" : "");
            if (it.NameF.StartsWith(q, StringComparison.Ordinal)
                || (it.FamilyF.Length > 0 && Join(it.FamilyF, it.NameF).StartsWith(q, StringComparison.Ordinal))
                || (it.MarkF.Length > 0 && it.MarkF.StartsWith(q, StringComparison.Ordinal))
                || (it.Kind == "selection" && "selection".StartsWith(q, StringComparison.Ordinal)))
                return 0;

            string padded = " " + primary;
            if (tokens.All(t => padded.Contains(" " + t))) return 1;
            if (tokens.All(t => primary.Contains(t))) return 2;

            string withCat = Join(primary, it.CategoryF, MentionText.Singular(it.Category));
            if (tokens.All(t => withCat.Contains(t) || TokenNamesCategory(t, it))) return 3;
            return -1;
        }

        private static string Join(params string[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));

        private static bool InActiveView(MentionItem it) => it.IsActive || (it.InView ?? 0) > 0;

        private static int KindOrder(string kind)
        {
            switch (kind)
            {
                case "selection": return 0;
                case "type": return 1;
                case "level": return 2;
                case "view": return 3;
                case "sheet": return 4;
                case "family": return 5;
                case "category": return 6;
                default: return 7;
            }
        }

        // ── Categories the message names ─────────────────────────────────────

        private class Named
        {
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // OST_*
            public string Words = "";                                                                     // folded message
        }

        private static Named NamedCategories(string message)
        {
            var n = new Named();
            if (string.IsNullOrWhiteSpace(message)) return n;
            n.Words = " " + Normalize(message) + " ";
            try
            {
                foreach (var m in Synonyms.Scan(message).Matches)
                    foreach (var c in m.Entry.Categories) n.Keys.Add(c);
            }
            catch { /* the synonym table is a convenience */ }
            return n;
        }

        private static bool IsNamed(MentionItem it, Named named)
        {
            if (string.IsNullOrEmpty(it.Category) || named.Words.Length == 0) return false;
            if (!string.IsNullOrEmpty(it.CategoryKey) && named.Keys.Contains(it.CategoryKey)) return true;
            string single = Normalize(MentionText.Singular(it.Category));
            string plural = Normalize(MentionText.Plural(it.Category));
            return (single.Length > 0 && named.Words.Contains(" " + single + " "))
                   || (plural.Length > 0 && named.Words.Contains(" " + plural + " "));
        }

        private static bool TokenNamesCategory(string token, MentionItem it)
        {
            if (string.IsNullOrEmpty(it.CategoryKey) || token.Length < 3) return false;
            try { return Synonyms.CategoriesFor(token).Contains(it.CategoryKey, StringComparer.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}
