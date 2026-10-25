#nullable enable
// Revit-free find_elements argument parser (plan §5 W2 Task 3): args JSON ->
// a normalized FindPlan, or an invalid_args message the model can act on.
//
// Free text is folded and read with the BM/EN synonyms:
//   "pintu kalis api aras 3" -> category OST_Doors (inferred), level hint
//   "aras 3", one text group {kalis api, fire rated, fire resistant, ...}.
// Stop words go; a category word right after a code ("CL-02A grid") stays text,
// because it is part of a type name. An explicit category or level wins.
//
// FindText words the plan for searched.filter and the summary.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BinaVibe.Copilot.Read
{
    public enum FindSourceKind { Host, All, Link }

    public sealed class FindSource
    {
        public FindSourceKind Kind { get; set; } = FindSourceKind.Host;

        /// <summary>Display name of the link ("STR_v3") when Kind is Link.</summary>
        public string? LinkName { get; set; }

        public override string ToString() => Kind switch
        {
            FindSourceKind.All => "all",
            FindSourceKind.Link => "link:" + LinkName,
            _ => "host",
        };
    }

    /// <summary>One AND-ed text condition: any of its folded variants must appear.</summary>
    public sealed class TermGroup
    {
        public TermGroup(string label, List<string> variants, string? alt = null)
        {
            Label = label;
            Variants = variants;
            Alt = alt;
        }

        /// <summary>The words as the drafter wrote them (folded).</summary>
        public string Label { get; }

        public List<string> Variants { get; }

        /// <summary>The other-language word, for the filter text ("fire rated").</summary>
        public string? Alt { get; }
    }

    public sealed class FindQuery
    {
        public int Index;
        public string? Text;
        /// <summary>The category as given in args.</summary>
        public string? CategoryText;
        /// <summary>BuiltInCategory name from the synonyms (given or inferred).</summary>
        public string? CategoryOst;
        public bool CategoryInferred;
        /// <summary>The text word the category was inferred from.</summary>
        public string? CategoryWord;
        public string? Level;
        /// <summary>A level phrase found in the text ("aras 3"), matched with LevelMatcher.</summary>
        public string? LevelHint;
        public string? Type;
        public string? Room;
        public ParamCondition? Parameter;
        public bool InView;
        public FindSource Source = new();
        public List<TermGroup> Terms = new();

        public bool HasCategory => CategoryOst != null || CategoryText != null;

        public FindQuery Clone()
        {
            var c = (FindQuery)MemberwiseClone();
            c.Terms = new List<TermGroup>(Terms);
            c.Source = new FindSource { Kind = Source.Kind, LinkName = Source.LinkName };
            return c;
        }

        public bool IsEmpty =>
            !HasCategory && Level == null && LevelHint == null && Type == null && Room == null
            && Parameter == null && !InView && Terms.Count == 0;
    }

    public sealed class GroupBySpec
    {
        /// <summary>level | type | category | room | param.</summary>
        public string Kind = "";
        public string? Param;

        public override string ToString() => Kind == "param" ? "param:" + Param : Kind;
    }

    public sealed class FindPlan
    {
        public List<FindQuery> Queries = new();
        public GroupBySpec? GroupBy;
        public string? Handle;
        public bool Highlight;
        public bool Zoom;
        public int Limit = QueryParser.DefaultLimit;
    }

    public static class QueryParser
    {
        public const int MaxQueries = 5;
        public const int DefaultLimit = 50;
        public const int MaxLimit = 50;

        private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {
            // English
            "the", "an", "all", "any", "of", "in", "on", "at", "with", "and", "or", "for", "to", "find", "show",
            "list", "count", "how", "many", "which", "what", "where", "are", "is", "there", "me", "please",
            "every", "each", "that", "this", "these", "those", "from", "by", "model", "element", "elements", "my",
            // Malay
            "semua", "yang", "di", "pada", "dalam", "dan", "atau", "ada", "berapa", "banyak", "cari", "carikan",
            "tunjuk", "tunjukkan", "senarai", "senaraikan", "kira", "mana", "apa", "ini", "itu", "tersebut",
            "untuk", "dengan", "setiap", "bagi", "ke", "kat", "dekat", "sila", "tolong", "je", "jer", "saya",
        };

        internal static readonly HashSet<string> LevelWords = new(StringComparer.Ordinal)
        {
            "aras", "level", "lvl", "tingkat", "storey", "story", "floor", "lantai",
        };

        private static readonly HashSet<string> LevelValueWords = new(StringComparer.Ordinal)
        {
            "bawah", "ground", "g", "gf", "bumbung", "roof", "atap",
        };

        private static readonly Regex LevelValue = new("^[0-9]+[a-z]?$", RegexOptions.CultureInvariant);

        public static bool TryParse(JsonElement args, out FindPlan plan, out string error)
        {
            plan = new FindPlan();
            error = "";
            if (!ReadArgs.IsObject(args) || !args.TryGetProperty("queries", out var qs) || qs.ValueKind != JsonValueKind.Array
                || qs.GetArrayLength() < 1 || qs.GetArrayLength() > MaxQueries)
            {
                error = "Give 1-5 queries, e.g. {\"queries\":[{\"category\":\"Doors\",\"level\":\"Level 2\"}]}.";
                return false;
            }

            plan.Handle = ReadArgs.String(args, "handle");
            if (plan.Handle != null && !HandleStore.IsHandle(plan.Handle.ToLowerInvariant()))
            {
                error = "handle must look like h_1a2b3c4d (from an earlier find_elements result).";
                return false;
            }
            if (plan.Handle != null) plan.Handle = plan.Handle.ToLowerInvariant();

            var i = 0;
            foreach (var q in qs.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object)
                {
                    error = "query " + (i + 1) + " must be an object.";
                    return false;
                }
                if (!TryParseQuery(q, i, out var query, out error)) return false;
                if (query.IsEmpty && plan.Handle == null)
                {
                    error = "query " + (i + 1) + " has nothing to search for: give text, category, level, type, room, parameter or in_view (or a handle).";
                    return false;
                }
                plan.Queries.Add(query);
                i++;
            }

            var groupBy = ReadArgs.String(args, "group_by");
            if (groupBy != null)
            {
                var g = groupBy.Trim();
                var lower = g.ToLowerInvariant();
                if (lower is "level" or "type" or "category" or "room") plan.GroupBy = new GroupBySpec { Kind = lower };
                else if (lower.StartsWith("param:", StringComparison.Ordinal) && g.Substring(6).Trim().Length > 0)
                    plan.GroupBy = new GroupBySpec { Kind = "param", Param = g.Substring(6).Trim() };
                else
                {
                    error = "group_by must be level, type, category, room or param:<parameter name>.";
                    return false;
                }
            }

            plan.Highlight = ReadArgs.Bool(args, "highlight") ?? false;
            plan.Zoom = ReadArgs.Bool(args, "zoom") ?? false;
            plan.Limit = ReadArgs.Int(args, "limit", DefaultLimit, 1, MaxLimit);
            return true;
        }

        private static bool TryParseQuery(JsonElement q, int index, out FindQuery query, out string error)
        {
            error = "";
            query = new FindQuery
            {
                Index = index,
                Text = ReadArgs.String(q, "text"),
                CategoryText = ReadArgs.String(q, "category"),
                Level = ReadArgs.String(q, "level"),
                Type = ReadArgs.String(q, "type"),
                Room = ReadArgs.String(q, "room"),
                InView = ReadArgs.Bool(q, "in_view") ?? false,
            };
            if (query.CategoryText != null) query.CategoryOst = Synonyms.CategoryFor(query.CategoryText);

            var source = ReadArgs.String(q, "source");
            if (source != null)
            {
                var s = source.Trim();
                if (s.Equals("host", StringComparison.OrdinalIgnoreCase)) query.Source = new FindSource { Kind = FindSourceKind.Host };
                else if (s.Equals("all", StringComparison.OrdinalIgnoreCase)) query.Source = new FindSource { Kind = FindSourceKind.All };
                else if (s.StartsWith("link:", StringComparison.OrdinalIgnoreCase) && s.Substring(5).Trim().Length > 0)
                    query.Source = new FindSource { Kind = FindSourceKind.Link, LinkName = LinkNames.Display(s.Substring(5)) };
                else
                {
                    error = "query " + (index + 1) + ": source must be host, all or link:<link name>.";
                    return false;
                }
            }

            var p = ReadArgs.Object(q, "parameter");
            if (p.HasValue)
            {
                var name = ReadArgs.String(p.Value, "name");
                var op = ReadArgs.String(p.Value, "op");
                var value = ReadArgs.String(p.Value, "value");
                if (!ParamCondition.TryCreate(name, op, value, out var cond, out var why))
                {
                    error = "query " + (index + 1) + ": parameter " + why;
                    return false;
                }
                query.Parameter = cond;
            }

            if (query.Text != null) ReadText(query);
            return true;
        }

        private static void ReadText(FindQuery query)
        {
            var words = Synonyms.Words(query.Text);

            // A level phrase ("aras 3", "level 2", "tingkat bawah") is a level filter, not text.
            for (var i = 0; i + 1 < words.Count; i++)
            {
                if (!LevelWords.Contains(words[i])) continue;
                var v = words[i + 1];
                if (!LevelValue.IsMatch(v) && !LevelValueWords.Contains(v)) continue;
                if (query.Level == null) query.LevelHint = words[i] + " " + v;
                words.RemoveRange(i, 2);
                break;
            }

            var scan = Synonyms.Scan(words);
            var plain = new List<string>();
            string? previous = null;

            void FlushPlain()
            {
                if (plain.Count == 0) return;
                var phrase = string.Join(" ", plain);
                var variants = new List<string> { phrase };
                if (plain.Count == 1 && phrase.Length > 3 && phrase.EndsWith("s", StringComparison.Ordinal) && !phrase.Any(char.IsDigit))
                    variants.Add(phrase.Substring(0, phrase.Length - 1));
                query.Terms.Add(new TermGroup(phrase, variants));
                plain.Clear();
            }

            foreach (var (text, entry) in scan.Segments)
            {
                if (entry == null)
                {
                    if (StopWords.Contains(text)) { FlushPlain(); previous = text; continue; }
                    plain.Add(text);
                    previous = text;
                    continue;
                }
                FlushPlain();
                bool afterCode = previous != null && previous.Any(char.IsDigit);
                if (entry.Category != null)
                {
                    if (query.CategoryOst != null && string.Equals(query.CategoryOst, entry.Category, StringComparison.Ordinal))
                    {
                        previous = text;
                        continue;                                   // repeats the category
                    }
                    if (!query.HasCategory && !afterCode)
                    {
                        query.CategoryOst = entry.Category;
                        query.CategoryInferred = true;
                        query.CategoryWord = text;
                        previous = text;
                        continue;
                    }
                }
                query.Terms.Add(Group(text, entry));
                previous = text;
            }
            FlushPlain();
        }

        private static TermGroup Group(string phrase, SynonymEntry entry)
        {
            var variants = entry.Variants.Select(Fuzzy.Fold).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (!variants.Contains(phrase)) variants.Insert(0, phrase);
            var ms = Fuzzy.Fold(entry.Ms);
            var en = Fuzzy.Fold(entry.En);
            var alt = phrase == en ? entry.Ms : phrase == ms ? entry.En : entry.En;
            return new TermGroup(phrase, variants, alt == phrase ? null : alt);
        }
    }

    /// <summary>Words for a parsed query: searched.filter and the summary subject.</summary>
    public static class FindText
    {
        private static string Levels(FindQuery q, IReadOnlyList<string>? levelNames, bool filter)
        {
            if (levelNames != null && levelNames.Count > 0)
                return filter
                    ? (levelNames.Count == 1 ? "level = '" + levelNames[0] + "'" : "level in (" + string.Join(", ", levelNames.Select(n => "'" + n + "'")) + ")")
                    : "on " + string.Join(" or ", levelNames);
            if (q.Level != null) return filter ? "level = '" + q.Level + "'" : "on " + q.Level;
            if (q.LevelHint != null) return filter ? "level ~ '" + q.LevelHint + "'" : "on " + q.LevelHint;
            return "";
        }

        private static string Terms(FindQuery q) =>
            string.Join(" and ", q.Terms.Select(t => "'" + t.Label + "'" + (t.Alt == null ? "" : " (" + t.Alt + ")")));

        public static string Filter(FindQuery q, string? categoryLabel, IReadOnlyList<string>? levelNames)
        {
            var parts = new List<string>();
            var cat = categoryLabel ?? q.CategoryText;
            if (cat != null) parts.Add("category = " + cat);
            var lv = Levels(q, levelNames, filter: true);
            if (lv.Length > 0) parts.Add(lv);
            if (q.Type != null) parts.Add("type ~ '" + q.Type + "'");
            if (q.Room != null) parts.Add("room ~ '" + q.Room + "'");
            if (q.Parameter != null) parts.Add(q.Parameter.Describe());
            parts.AddRange(q.Terms.Select(t => "text ~ '" + t.Label + "'" + (t.Alt == null ? "" : " (" + t.Alt + ")")));
            if (q.InView) parts.Add("visible in the active view");
            return parts.Count == 0 ? "(no filter)" : string.Join(" AND ", parts);
        }

        public static string Subject(FindQuery q, string? categoryLabel, IReadOnlyList<string>? levelNames)
        {
            var s = categoryLabel ?? q.CategoryText ?? "elements";
            if (q.Type != null) s += " of type '" + q.Type + "'";
            var lv = Levels(q, levelNames, filter: false);
            if (lv.Length > 0) s += " " + lv;
            if (q.Room != null) s += " in room '" + q.Room + "'";
            if (q.Parameter != null) s += " where " + q.Parameter.Describe();
            if (q.Terms.Count > 0) s += " matching " + Terms(q);
            if (q.InView) s += " visible in the active view";
            return s;
        }
    }
}
