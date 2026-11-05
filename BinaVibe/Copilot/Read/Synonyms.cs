#nullable enable
// Revit-free Malay (BM) <-> English synonyms for find_elements free text
// (plan §5 W2 Task 3). The table is SynonymTable.g.cs, generated from
// Resources/copilot/synonyms-ms-en.json (SynonymTests keeps them in step).
//
//   CategoryFor("pintu")   -> "OST_Doors"      (category words)
//   Variants("tandas")     -> tandas, toilet, wc, lavatory, ... (folded)
//   Scan("pintu kalis api blok A") -> matches [pintu, kalis api] + leftover [blok, a]
//
// Matching folds case, accents and punctuation, collapses Malay
// reduplication ("pintu-pintu"), takes the longest phrase first, and accepts
// a trailing English plural "s".

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Copilot.Read
{
    public sealed class SynonymEntry
    {
        public SynonymEntry(string ms, string en, string[]? categories, string[] also, bool keepText = false)
        {
            Ms = ms;
            En = en;
            Categories = categories ?? Array.Empty<string>();
            Also = also ?? Array.Empty<string>();
            KeepText = keepText;
        }

        public string Ms { get; }
        public string En { get; }

        /// <summary>BuiltInCategory names (OST_*) the word names; empty for plain words.
        /// "tiang" names both Columns and Structural Columns.</summary>
        public IReadOnlyList<string> Categories { get; }

        /// <summary>The first category, or null.</summary>
        public string? Category => Categories.Count > 0 ? Categories[0] : null;

        /// <summary>The word is narrower than its category ("sinki" is one kind of plumbing
        /// fixture): a query sets the category AND keeps the word as a text condition.</summary>
        public bool KeepText { get; }

        public IReadOnlyList<string> Also { get; }

        public IEnumerable<string> Variants
        {
            get
            {
                yield return Ms;
                yield return En;
                foreach (var a in Also) yield return a;
            }
        }
    }

    public sealed class SynonymMatch
    {
        public SynonymMatch(string phrase, SynonymEntry entry)
        {
            Phrase = phrase;
            Entry = entry;
        }

        /// <summary>The folded words of the text that matched.</summary>
        public string Phrase { get; }

        public SynonymEntry Entry { get; }
    }

    public sealed class SynonymScan
    {
        /// <summary>The text in order: each segment is a matched phrase (Entry set) or one plain word.</summary>
        public List<(string Text, SynonymEntry? Entry)> Segments { get; } = new();

        public List<SynonymMatch> Matches => Segments.Where(s => s.Entry != null).Select(s => new SynonymMatch(s.Text, s.Entry!)).ToList();

        /// <summary>Folded words no entry matched, in text order.</summary>
        public List<string> Leftover => Segments.Where(s => s.Entry == null).Select(s => s.Text).ToList();
    }

    public static class Synonyms
    {
        private static readonly Lazy<Dictionary<string, SynonymEntry>> Index = new(BuildIndex);
        private static readonly Lazy<int> MaxWords = new(() => Index.Value.Keys.Max(k => k.Split(' ').Length));

        public static IReadOnlyList<SynonymEntry> All => SynonymTable.Entries;

        private static Dictionary<string, SynonymEntry> BuildIndex()
        {
            var index = new Dictionary<string, SynonymEntry>(StringComparer.Ordinal);
            foreach (var e in SynonymTable.Entries)
                foreach (var v in e.Variants)
                {
                    var f = Fuzzy.Fold(v);
                    if (f.Length > 0 && !index.ContainsKey(f)) index[f] = e;
                }
            return index;
        }

        /// <summary>Folded, with Malay reduplication collapsed ("pintu pintu" -> "pintu").</summary>
        internal static List<string> Words(string? text)
        {
            var words = new List<string>();
            foreach (var w in Fuzzy.Fold(text).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (words.Count == 0 || words[words.Count - 1] != w) words.Add(w);
            return words;
        }

        private static SynonymEntry? Lookup(string folded)
        {
            if (Index.Value.TryGetValue(folded, out var e)) return e;
            // English plural: "toilets" -> "toilet", "switches" -> "switch".
            if (folded.Length > 3 && folded.EndsWith("s", StringComparison.Ordinal))
            {
                if (Index.Value.TryGetValue(folded.Substring(0, folded.Length - 1), out e)) return e;
                if (folded.EndsWith("es", StringComparison.Ordinal) && Index.Value.TryGetValue(folded.Substring(0, folded.Length - 2), out e)) return e;
            }
            return null;
        }

        /// <summary>The entry a whole term names (any variant), or null.</summary>
        public static SynonymEntry? EntryFor(string? term)
        {
            var words = Words(term);
            return words.Count == 0 ? null : Lookup(string.Join(" ", words));
        }

        /// <summary>BuiltInCategory name (OST_*) a word names, e.g. "pintu" -> "OST_Doors"; null otherwise.</summary>
        public static string? CategoryFor(string? term) => EntryFor(term)?.Category;

        /// <summary>Every category a word names ("tiang" -> Columns, Structural Columns).</summary>
        public static IReadOnlyList<string> CategoriesFor(string? term) =>
            EntryFor(term)?.Categories ?? (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>Every folded variant equivalent to the term (the term itself when unknown).</summary>
        public static IReadOnlyList<string> Variants(string? term)
        {
            var e = EntryFor(term);
            if (e == null)
            {
                var f = Fuzzy.Fold(term);
                return f.Length == 0 ? Array.Empty<string>() : new[] { f };
            }
            return e.Variants.Select(Fuzzy.Fold).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>Greedy longest-phrase scan of free text.</summary>
        public static SynonymScan Scan(string? text) => Scan(Words(text));

        /// <summary>Scan of already folded words.</summary>
        public static SynonymScan Scan(IReadOnlyList<string> words)
        {
            var scan = new SynonymScan();
            var i = 0;
            while (i < words.Count)
            {
                SynonymMatch? hit = null;
                var take = 0;
                for (var n = Math.Min(MaxWords.Value, words.Count - i); n >= 1; n--)
                {
                    var phrase = string.Join(" ", words.Skip(i).Take(n));
                    if (phrase.Length == 0) continue;
                    var e = Lookup(phrase);
                    if (e == null) continue;
                    hit = new SynonymMatch(phrase, e);
                    take = n;
                    break;
                }
                if (hit != null)
                {
                    scan.Segments.Add((hit.Phrase, hit.Entry));
                    i += take;
                }
                else
                {
                    scan.Segments.Add((words[i], null));
                    i++;
                }
            }
            return scan;
        }
    }
}
