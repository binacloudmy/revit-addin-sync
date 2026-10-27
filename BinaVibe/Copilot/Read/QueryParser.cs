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
        /// <summary>BuiltInCategory names from the synonyms (given or inferred);
        /// several when the text names several ("pintu tingkap", "tiang").</summary>
        public List<string> CategoryOsts = new();
        public bool CategoryInferred;
        /// <summary>The text words the categories were inferred from.</summary>
        public string? CategoryWord;

        /// <summary>The first category, or null.</summary>
        public string? CategoryOst => CategoryOsts.Count > 0 ? CategoryOsts[0] : null;
        public string? Level;
        /// <summary>A level phrase found in the text ("aras 3"), matched with LevelMatcher.</summary>
        public string? LevelHint;
        public string? Type;
        public string? Room;
        public ParamCondition? Parameter;
        public bool InView;
        public FindSource Source = new();
        public List<TermGroup> Terms = new();

        public bool HasCategory => CategoryOsts.Count > 0 || CategoryText != null;

        public FindQuery Clone()
        {
            var c = (FindQuery)MemberwiseClone();
            c.Terms = new List<TermGroup>(Terms);
            c.CategoryOsts = new List<string>(CategoryOsts);
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
            var r = new ArgReader();
            if (!r.IsObject(args, "find_elements arguments")) { error = r.Error; return false; }
            r.Known(args, "", "queries", "group_by", "handle", "highlight", "zoom", "limit");
            if (!args.TryGetProperty("queries", out var qs) || qs.ValueKind != JsonValueKind.Array
                || qs.GetArrayLength() < 1 || qs.GetArrayLength() > MaxQueries)
            {
                error = "Give 1-5 queries, e.g. {\"queries\":[{\"category\":\"Doors\",\"level\":\"Level 2\"}]}.";
                return false;
            }

            plan.Handle = r.Str(args, "handle")?.ToLowerInvariant();
            if (plan.Handle != null && !HandleStore.IsHandle(plan.Handle))
                r.Fail("handle must look like h_1a2b3c4d (from an earlier find_elements result).");

            var groupBy = r.Str(args, "group_by");
            if (groupBy != null)
            {
                var lower = groupBy.ToLowerInvariant();
                if (lower is "level" or "type" or "category" or "room") plan.GroupBy = new GroupBySpec { Kind = lower };
                else if (lower.StartsWith("param:", StringComparison.Ordinal) && groupBy.Substring(6).Trim().Length > 0)
                    plan.GroupBy = new GroupBySpec { Kind = "param", Param = groupBy.Substring(6).Trim() };
                else r.Fail("group_by must be level, type, category, room or param:<parameter name>.");
            }
            plan.Highlight = r.Bool(args, "highlight") ?? false;
            plan.Zoom = r.Bool(args, "zoom") ?? false;
            plan.Limit = r.Int(args, "limit", DefaultLimit, 1, MaxLimit);

            var i = 0;
            foreach (var q in qs.EnumerateArray())
            {
                var path = "queries[" + i + "]";
                if (q.ValueKind != JsonValueKind.Object) { r.Fail("query " + (i + 1) + " must be an object."); i++; continue; }
                var query = ParseQuery(q, i, path, r);
                if (query.IsEmpty && plan.Handle == null)
                    r.Fail("query " + (i + 1) + " has nothing to search for: give text, category, level, type, room, parameter or in_view (or a handle).");
                // A parameter is read per element: never on the whole model (review I1).
                if (query.Parameter != null && !query.HasCategory && plan.Handle == null)
                    r.Fail("query " + (i + 1) + ": a parameter condition needs a category (or a handle), e.g. category 'Doors'.");
                plan.Queries.Add(query);
                i++;
            }

            if (!r.Ok) { error = r.Error; return false; }
            return true;
        }

        private static FindQuery ParseQuery(JsonElement q, int index, string path, ArgReader r)
        {
            r.Known(q, path, "text", "category", "level", "type", "room", "parameter", "in_view", "source");
            var query = new FindQuery
            {
                Index = index,
                Text = r.Str(q, "text", path),
                CategoryText = r.Str(q, "category", path),
                Level = r.Str(q, "level", path),
                Type = r.Str(q, "type", path),
                Room = r.Str(q, "room", path),
                InView = r.Bool(q, "in_view", path) ?? false,
            };
            if (query.CategoryText != null)
            {
                var entry = Synonyms.EntryFor(query.CategoryText);
                if (entry != null && entry.Categories.Count > 0)
                {
                    query.CategoryOsts.AddRange(entry.Categories);
                    if (entry.KeepText) query.Terms.Add(Group(Fuzzy.Fold(query.CategoryText), entry));
                }
            }

            var source = r.Str(q, "source", path);
            if (source != null)
            {
                if (source.Equals("host", StringComparison.OrdinalIgnoreCase)) query.Source = new FindSource { Kind = FindSourceKind.Host };
                else if (source.Equals("all", StringComparison.OrdinalIgnoreCase)) query.Source = new FindSource { Kind = FindSourceKind.All };
                else if (source.StartsWith("link:", StringComparison.OrdinalIgnoreCase) && source.Substring(5).Trim().Length > 0)
                    query.Source = new FindSource { Kind = FindSourceKind.Link, LinkName = LinkNames.Display(source.Substring(5)) };
                else r.Fail("query " + (index + 1) + ": source must be host, all or link:<link name>.");
            }

            var p = r.Obj(q, "parameter", path);
            if (p.HasValue)
            {
                var pp = path + ".parameter";
                r.Known(p.Value, pp, "name", "op", "value");
                var name = r.Str(p.Value, "name", pp);
                var op = r.Str(p.Value, "op", pp);
                var value = r.Scalar(p.Value, "value", pp);
                if (ParamCondition.TryCreate(name, op, value, out var cond, out var why)) query.Parameter = cond;
                else r.Fail("query " + (index + 1) + ": parameter " + why);
            }

            if (query.Text != null) ReadText(query);
            return query;
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

            var segments = Synonyms.Scan(words).Segments;
            bool AfterCode(int k) => k > 0 && segments[k - 1].Entry == null && segments[k - 1].Text.Any(char.IsDigit);
            bool IsLevels(SynonymEntry e) => e.Categories.Count == 1 && e.Categories[0] == LevelsCategory;

            // Categories named by the text: every category word not glued to a code;
            // Levels only when it is the only one ("aras" alone lists levels).
            var inferred = new HashSet<int>();
            if (!query.HasCategory)
            {
                for (var k = 0; k < segments.Count; k++)
                    if (segments[k].Entry is { } e && e.Categories.Count > 0 && !AfterCode(k)) inferred.Add(k);
                if (inferred.Any(k => !IsLevels(segments[k].Entry!)))
                    inferred.RemoveWhere(k => IsLevels(segments[k].Entry!));
                foreach (var k in inferred)
                    foreach (var c in segments[k].Entry!.Categories)
                        if (!query.CategoryOsts.Contains(c)) query.CategoryOsts.Add(c);
                if (inferred.Count > 0)
                {
                    query.CategoryInferred = true;
                    query.CategoryWord = string.Join(" ", inferred.OrderBy(k => k).Select(k => segments[k].Text));
                }
            }
            bool narrowOnly = inferred.Count > 0 && inferred.All(k => segments[k].Entry!.KeepText);
            TermGroup? narrow = null;

            var plain = new List<string>();
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

            for (var k = 0; k < segments.Count; k++)
            {
                var (text, entry) = segments[k];
                if (entry == null)
                {
                    if (StopWords.Contains(text)) { FlushPlain(); continue; }
                    plain.Add(text);
                    continue;
                }
                FlushPlain();
                if (inferred.Contains(k))
                {
                    // Narrow words (sinki, curtain wall) keep their text, OR-ed in one group.
                    if (narrowOnly) narrow = narrow == null ? Group(text, entry) : Merge(narrow, Group(text, entry));
                    continue;
                }
                if (entry.Categories.Count > 0)
                {
                    if (IsLevels(entry)) continue;                                   // "pintu aras": not a required word
                    if (entry.Categories.Any(query.CategoryOsts.Contains))
                    {
                        if (entry.KeepText) query.Terms.Add(Group(text, entry));    // "curtain wall" with category Walls
                        continue;                                                    // repeats the category
                    }
                }
                query.Terms.Add(Group(text, entry));
            }
            FlushPlain();
            if (narrow != null) query.Terms.Insert(0, narrow);
        }

        private const string LevelsCategory = "OST_Levels";

        private static TermGroup Merge(TermGroup a, TermGroup b) =>
            new TermGroup(a.Label + " / " + b.Label, a.Variants.Concat(b.Variants).Distinct(StringComparer.Ordinal).ToList(), a.Alt);

        internal static TermGroup Group(string phrase, SynonymEntry entry)
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
