#nullable enable
// Revit-free result shaping for find_elements (spec §10, §11 #2). The
// executor collects FoundRows and what it could and could not search; this
// builds the envelope:
//   - count is exact (every match), rows stop at limit, a handle covers all;
//   - group_by counts every match, not just the rows shown;
//   - searched lists sources, categories, the filter in words and not_searched
//     (unloaded links, closed worksets); an empty result is never a bare "none":
//     it carries searched, near matches and what to do next (Review Focus 2).
//
// NearMatchPlanner decides which single condition to drop to find near matches.

using System;
using System.Collections.Generic;
using System.Linq;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    public sealed class FoundRow
    {
        public ElementRef Ref = new(0, null);
        public string? Name;
        public string? Category;
        public string? Type;
        public string? Family;
        public string? Level;
        public string? Room;
        public string? Mark;
        /// <summary>Display name of the link the element lives in; null for the host.</summary>
        public string? LinkName;
        /// <summary>group_by key of this element.</summary>
        public string? GroupKey;
        /// <summary>Extra columns (e.g. the value of the queried parameter).</summary>
        public Dictionary<string, string?> Fields = new();
        /// <summary>Indexes of the queries that matched it.</summary>
        public HashSet<int> Queries = new();
    }

    public sealed class QueryOutcome
    {
        public int Count;
        public string Filter = "";
        /// <summary>What was looked for, in words ("Doors on Level 3 matching 'kalis api'").</summary>
        public string Subject = "";
        public string? Handle;
    }

    public sealed class FindOutcome
    {
        /// <summary>Every match (union of the queries, no duplicates), in display order.</summary>
        public List<FoundRow> Rows = new();
        public List<QueryOutcome> Queries = new();
        public List<string> Sources = new();
        public List<string> Categories = new();
        public List<string> NotSearched = new();
        public List<string> NearMatches = new();
        public string? Handle;
        public HighlightDto? Highlight;
        /// <summary>Caveats the model must see (e.g. a highlight that could not zoom).</summary>
        public List<string> Notes = new();
    }

    public static class FindResultBuilder
    {
        public const int MaxGroups = 50;

        public static Envelope Build(FindPlan plan, FindOutcome o)
        {
            var count = o.Rows.Count;
            var shown = o.Rows.Take(plan.Limit).ToList();
            var data = new Dictionary<string, object?>
            {
                ["rows"] = shown.Select(RowData).ToList(),
            };

            List<(string Key, int Count)>? groups = null;
            if (plan.GroupBy != null)
            {
                groups = o.Rows
                    .GroupBy(r => string.IsNullOrWhiteSpace(r.GroupKey) ? "(none)" : r.GroupKey!)
                    .Select(g => (g.Key, g.Count()))
                    .OrderByDescending(g => g.Item2).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .ToList();
                data["group_by"] = plan.GroupBy.ToString();
                data["groups"] = groups.Take(MaxGroups).Select(g => new Dictionary<string, object?> { ["key"] = g.Key, ["count"] = g.Count }).ToList();
                if (groups.Count > MaxGroups) data["groups_total"] = groups.Count;
            }

            if (plan.Queries.Count > 1)
                data["queries"] = o.Queries.Select((q, i) =>
                {
                    var d = new Dictionary<string, object?> { ["query"] = i + 1, ["count"] = q.Count, ["filter"] = q.Filter };
                    if (q.Handle != null) d["handle"] = q.Handle;
                    return d;
                }).ToList();

            if (count == 0) data["near_matches"] = o.NearMatches.Take(5).ToList();
            if (o.Notes.Count > 0) data["notes"] = o.Notes.ToList();

            var searched = new SearchedDto
            {
                Sources = o.Sources.Distinct().ToList(),
                Categories = o.Categories.Distinct().ToList(),
                Filter = o.Queries.Count == 0 ? null : string.Join(" | ", o.Queries.Select(q => q.Filter)),
                NotSearched = o.NotSearched.Distinct().ToList(),
            };

            return ReadEnvelope.Done(
                Summary(plan, o, groups),
                data,
                count: count,
                handle: count > 0 ? o.Handle : null,
                searched: searched,
                next: Next(plan, o, count, shown.Count),
                highlight: o.Highlight);
        }

        private static Dictionary<string, object?> RowData(FoundRow r)
        {
            var d = new Dictionary<string, object?> { ["ref"] = r.Ref };
            if (!string.IsNullOrEmpty(r.Name) && !string.Equals(r.Name, r.Type, StringComparison.Ordinal)) d["name"] = r.Name;
            if (r.Category != null) d["category"] = r.Category;
            if (r.Type != null) d["type"] = r.Type;
            if (r.Family != null && !string.Equals(r.Family, r.Type, StringComparison.Ordinal)) d["family"] = r.Family;
            if (r.Level != null) d["level"] = r.Level;
            if (r.Room != null) d["room"] = r.Room;
            if (!string.IsNullOrEmpty(r.Mark)) d["mark"] = r.Mark;
            if (r.LinkName != null) d["link"] = r.LinkName;
            foreach (var kv in r.Fields) d[kv.Key] = kv.Value;
            return d;
        }

        internal static string SourcesPhrase(IReadOnlyList<string> sources)
        {
            if (sources.Count == 0) return "no source that could be searched";
            var links = sources.Where(s => s.StartsWith("link", StringComparison.Ordinal)).Select(s => "link " + s.Substring(s.IndexOf(':') + 1).Trim()).ToList();
            var host = sources.Contains("host");
            if (host && links.Count == 0) return "the host model";
            if (!host && links.Count == 1) return links[0];
            if (!host) return links.Count + " links";
            return links.Count == 1 ? "the host model and " + links[0] : "the host model and " + links.Count + " links";
        }

        private static string NotSearchedPhrase(FindOutcome o) =>
            o.NotSearched.Count == 0 ? "" : " Not searched: " + string.Join(", ", o.NotSearched.Take(4).Select(s => s.Replace(": ", " ")))
                + (o.NotSearched.Count > 4 ? " and " + (o.NotSearched.Count - 4) + " more" : "") + ".";

        private static string Summary(FindPlan plan, FindOutcome o, List<(string Key, int Count)>? groups)
        {
            var where = SourcesPhrase(o.Sources);
            string text;
            if (o.Queries.Count > 1)
            {
                text = o.Rows.Count + " elements from " + o.Queries.Count + " queries in " + where + ": "
                    + string.Join("; ", o.Queries.Select(q => q.Count + " " + q.Subject)) + ".";
            }
            else
            {
                var subject = o.Queries.Count == 1 ? o.Queries[0].Subject : "elements";
                if (o.Rows.Count == 0)
                    text = o.Sources.Count == 0 ? "Nothing could be searched for " + subject + "." : "No " + subject + " in " + where + ".";
                else
                {
                    var parts = groups != null
                        ? groups.Take(3).Select(g => g.Count + " " + g.Key).ToList()
                        : o.Rows.Where(r => r.Type != null).GroupBy(r => r.Type!)
                            .Select(g => (g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal)
                            .Take(3).Select(g => g.Count + " " + g.Key).ToList();
                    var more = (groups?.Count ?? o.Rows.Where(r => r.Type != null).Select(r => r.Type).Distinct().Count()) > 3 ? ", …" : "";
                    text = o.Rows.Count + " " + subject + " in " + where
                        + (parts.Count > 1 || groups != null ? ": " + string.Join(", ", parts) + more : "") + ".";
                }
            }
            return text + NotSearchedPhrase(o);
        }

        private static string Next(FindPlan plan, FindOutcome o, int count, int shown)
        {
            string next;
            if (count == 0)
            {
                next = o.NotSearched.Count > 0
                    ? "Not searched: " + string.Join(", ", o.NotSearched.Take(2).Select(s => s.Replace(": ", " ")))
                      + ". Load or open it and search again, or offer the near matches."
                    : o.NearMatches.Count > 0
                        ? "Nothing matched. Offer the near matches or drop a filter."
                        : "Nothing matched in the searched scope; say so with that scope.";
            }
            else if (shown < count)
                next = shown + " of " + count + " rows shown (limit " + plan.Limit + "). Use handle " + o.Handle + " to inspect or edit all " + count + ".";
            else
                next = "Use handle " + o.Handle + " to inspect, look at or edit these " + count + ".";

            if (o.Highlight != null && o.Highlight.VisibleInView < o.Highlight.Selected)
            {
                var hidden = o.Highlight.Selected - o.Highlight.VisibleInView;
                next += hidden == 1
                    ? " 1 selected element is not visible in the active view."
                    : " " + hidden + " selected elements are not visible in the active view.";
            }
            return next;
        }
    }

    public sealed class Relaxation
    {
        public Relaxation(string dropped, FindQuery query)
        {
            Dropped = dropped;
            Query = query;
        }

        /// <summary>level | text | type | parameter | room | in_view | category | source.</summary>
        public string Dropped { get; }
        public FindQuery Query { get; }
    }

    public static class NearMatchPlanner
    {
        /// <summary>One relaxed copy per condition, each dropping exactly one, most
        /// telling first. hostOnlyWithLinks adds a step that searches the links too.</summary>
        public static IReadOnlyList<Relaxation> Relaxations(FindQuery q, bool hostOnlyWithLinks)
        {
            var steps = new List<Relaxation>();
            if (q.Level != null || q.LevelHint != null)
            {
                var c = q.Clone();
                c.Level = null;
                c.LevelHint = null;
                steps.Add(new Relaxation("level", c));
            }
            if (q.Terms.Count > 0)
            {
                var c = q.Clone();
                c.Terms = new List<TermGroup>();
                if (!c.IsEmpty) steps.Add(new Relaxation("text", c));
            }
            if (q.Type != null)
            {
                var c = q.Clone();
                c.Type = null;
                steps.Add(new Relaxation("type", c));
            }
            if (q.Parameter != null)
            {
                var c = q.Clone();
                c.Parameter = null;
                steps.Add(new Relaxation("parameter", c));
            }
            if (q.Room != null)
            {
                var c = q.Clone();
                c.Room = null;
                steps.Add(new Relaxation("room", c));
            }
            if (q.InView)
            {
                var c = q.Clone();
                c.InView = false;
                steps.Add(new Relaxation("in_view", c));
            }
            if (q.CategoryInferred && q.CategoryWord != null)
            {
                // The word may have been part of a name ("Grid" in a ceiling type): search it as text.
                var c = q.Clone();
                c.CategoryOst = null;
                c.CategoryInferred = false;
                var entry = Synonyms.EntryFor(q.CategoryWord);
                var variants = entry == null
                    ? new List<string> { q.CategoryWord }
                    : entry.Variants.Select(Fuzzy.Fold).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                c.Terms.Add(new TermGroup(q.CategoryWord, variants, entry?.En));
                steps.Add(new Relaxation("category", c));
            }
            if (hostOnlyWithLinks && q.Source.Kind == FindSourceKind.Host)
            {
                var c = q.Clone();
                c.Source = new FindSource { Kind = FindSourceKind.All };
                steps.Add(new Relaxation("source", c));
            }
            return steps;
        }

        /// <summary>"Door - Fire Rated 60 min: 14 on Level 2" / "C1: 8 in link STR_v3".</summary>
        public static string Format(string label, int count, string? level, string? link)
        {
            var s = label + ": " + count;
            if (link != null) return s + " in link " + link;
            if (level != null) return s + " on " + level;
            return s;
        }
    }
}
