using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>One group of selected elements: same category, family and type.
    /// Family and Type are empty for elements without a type (rooms, areas).</summary>
    public class MentionSelectionGroup
    {
        public string Category;
        public string Family;
        public string Type;
        public int Count;

        public MentionSelectionGroup() { }
        public MentionSelectionGroup(string category, string family, string type, int count)
        { Category = category; Family = family; Type = type; Count = count; }
    }

    /// <summary>What is selected in Revit, grouped by type, read by the provider.</summary>
    public class MentionSelection
    {
        public List<MentionSelectionGroup> Groups = new List<MentionSelectionGroup>();
        public int Total => Groups.Sum(g => g.Count);

        public MentionSelection() { }
        public MentionSelection(IEnumerable<MentionSelectionGroup> groups)
        { if (groups != null) Groups.AddRange(groups.Where(g => g != null && g.Count > 0)); }
    }

    /// <summary>
    /// What a picked mention shows in the message box (the chip) and what it sends
    /// in the message text. The backend matches a type by "Family: Type" first and
    /// counts a value as said when its words appear in the message, so the sent
    /// text spells the full, unambiguous form while the chip stays short.
    /// WPF-free and Revit-free so the rules are unit-tested.
    /// </summary>
    public static class MentionText
    {
        /// <summary>"Family: Type", the form Revit's own UI and the backend use.</summary>
        public static string FamilyType(string family, string type) =>
            string.IsNullOrEmpty(family) ? (type ?? "") : family + ": " + type;

        /// <summary>The chip label. A type shows its own name unless another family
        /// in the model has a type of the same name; then "Family: Type" so the two
        /// chips do not look alike.</summary>
        public static string Chip(MentionItem item)
        {
            if (item == null) return "";
            switch (item.Kind)
            {
                case "type": return item.SharedName ? FamilyType(item.Family, item.Name) : item.Name;
                case "selection": return "Selection";
                default: return item.Name ?? "";
            }
        }

        /// <summary>The text a pick puts in the message sent to the backend.</summary>
        public static string Sent(MentionItem item)
        {
            if (item == null) return "";
            switch (item.Kind)
            {
                case "type":
                {
                    var ft = FamilyType(item.Family, item.Name);
                    var word = Singular(item.Category);
                    return string.IsNullOrEmpty(word) || NamesCategory(item.Family, item.Category)
                        ? ft
                        : ft + " (" + word + " type)";
                }
                case "family":
                {
                    var word = Singular(item.Category);
                    return string.IsNullOrEmpty(word) || NamesCategory(item.Name, item.Category)
                        ? item.Name + " family"
                        : item.Name + " (" + word + " family)";
                }
                case "selection": return SelectionSent(item.Selection);
                // Levels, views, sheets and categories: the exact name.
                default: return item.Name ?? "";
            }
        }

        // ── Selection ───────────────────────────────────────────────────────

        /// <summary>The picker row's description: "1 door · Pintu Tunggal: PTh300a",
        /// "12 walls of 3 types", "15 elements: 12 walls, 3 doors".</summary>
        public static string SelectionLabel(MentionSelection sel)
        {
            if (sel == null || sel.Total == 0) return "Nothing selected";
            var cats = ByCategory(sel);
            if (cats.Count == 1)
            {
                var c = cats[0];
                var types = TypesOf(c.Groups);
                string head = Counted(c.Count, c.Category);
                if (types.Count == 1) return head + " · " + types[0];
                if (types.Count == 0) return head;
                return head + " of " + types.Count + " types";
            }
            return Counted(sel.Total, null) + ": " + string.Join(", ", cats.Take(3).Select(c => Counted(c.Count, c.Category)))
                   + (cats.Count > 3 ? ", …" : "");
        }

        /// <summary>The text a Selection chip sends, from the selection at send time.</summary>
        public static string SelectionSent(MentionSelection sel)
        {
            if (sel == null || sel.Total == 0) return "the selected elements (nothing is selected)";
            var cats = ByCategory(sel);
            if (cats.Count == 1)
            {
                var c = cats[0];
                var types = TypesOf(c.Groups);
                string noun = c.Count == 1 ? Singular(c.Category) : Plural(c.Category);
                if (string.IsNullOrEmpty(noun)) noun = c.Count == 1 ? "element" : "elements";
                string head = c.Count == 1 ? "the selected " + noun : "the " + c.Count + " selected " + noun;
                if (types.Count == 1) return head + " of type " + types[0];
                if (types.Count == 0) return head;
                return head + " (" + types.Count + " types: " + ListSome(types, 4) + ")";
            }
            return "the " + sel.Total + " selected elements ("
                   + string.Join(", ", cats.Select(c => Counted(c.Count, c.Category))) + ")";
        }

        private class CatGroup { public string Category; public int Count; public List<MentionSelectionGroup> Groups; }

        private static List<CatGroup> ByCategory(MentionSelection sel) =>
            sel.Groups.GroupBy(g => g.Category ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new CatGroup { Category = g.First().Category, Count = g.Sum(x => x.Count), Groups = g.ToList() })
                .OrderByDescending(c => c.Count).ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static List<string> TypesOf(List<MentionSelectionGroup> groups) =>
            groups.Where(g => !string.IsNullOrEmpty(g.Type))
                .OrderByDescending(g => g.Count)
                .Select(g => FamilyType(g.Family, g.Type))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private static string ListSome(List<string> items, int max) =>
            items.Count <= max ? string.Join(", ", items)
                : string.Join(", ", items.Take(max)) + " and " + (items.Count - max) + " more";

        /// <summary>"1 door", "12 doors", "3 elements" (no category).</summary>
        public static string Counted(int n, string category)
        {
            string noun = n == 1 ? Singular(category) : Plural(category);
            if (string.IsNullOrEmpty(noun)) noun = n == 1 ? "element" : "elements";
            return n + " " + noun;
        }

        // ── Category words ──────────────────────────────────────────────────

        // Revit category names are plural English ("Doors", "Structural Columns");
        // some are mass nouns ("Furniture", "Casework") and stay as they are.
        private static readonly HashSet<string> MassNouns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "furniture", "casework", "site", "topography", "entourage", "planting", "mass", "parking", "equipment", "glass" };

        /// <summary>Lower-case singular of a category name: "Doors" → "door",
        /// "Structural Columns" → "structural column", "Furniture" → "furniture".</summary>
        public static string Singular(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return "";
            var words = category.Trim().ToLowerInvariant().Split(' ');
            words[words.Length - 1] = SingularWord(words[words.Length - 1]);
            return string.Join(" ", words);
        }

        /// <summary>Lower-case plural of a category name ("Doors" → "doors").</summary>
        public static string Plural(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return "";
            return category.Trim().ToLowerInvariant();
        }

        private static string SingularWord(string w)
        {
            if (MassNouns.Contains(w)) return w;
            if (w.EndsWith("ies") && w.Length > 4) return w.Substring(0, w.Length - 3) + "y";
            if (w.EndsWith("sses") || w.EndsWith("xes") || w.EndsWith("ches") || w.EndsWith("shes")) return w.Substring(0, w.Length - 2);
            if (w.EndsWith("ss") || w.EndsWith("us") || w.EndsWith("is")) return w;
            if (w.EndsWith("s") && w.Length > 1) return w.Substring(0, w.Length - 1);
            return w;
        }

        /// <summary>True when a family name already says its category in English
        /// ("Door - Single" for Doors), so the sent text need not add it.</summary>
        public static bool NamesCategory(string familyName, string category)
        {
            if (string.IsNullOrEmpty(familyName) || string.IsNullOrEmpty(category)) return false;
            var fam = " " + MentionSearch.Normalize(familyName) + " ";
            var single = MentionSearch.Normalize(Singular(category));
            var plural = MentionSearch.Normalize(Plural(category));
            return fam.Contains(" " + single + " ") || fam.Contains(" " + plural + " ")
                   || fam.Contains(" " + single + "s ");
        }
    }
}
