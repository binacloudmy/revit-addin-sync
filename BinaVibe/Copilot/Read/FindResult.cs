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
        /// <summary>group_by keys of this element, one per plan.GroupKeys entry.</summary>
        public List<string?> GroupKeys = new();

        /// <summary>The first group_by key (setting it replaces the keys).</summary>
        public string? GroupKey
        {
            get => GroupKeys.Count > 0 ? GroupKeys[0] : null;
            set => GroupKeys = new List<string?> { value };
        }

        /// <summary>Rooms only (live test L9): placed | not placed | not enclosed | redundant.</summary>
        public string? RoomStatus;

        /// <summary>Rooms only: Room.Area in Revit's internal unit (ft²).</summary>
        public double? AreaInternal;
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
        /// <summary>Selected elements whose visibility in the view could not be checked
        /// (linked elements on Revit 2023/2024): never counted as "not visible".</summary>
        public int HighlightUnknown;
        /// <summary>The scan budget stopped the search: counts are a lower bound.</summary>
        public bool Partial;
        public int Scanned;
        /// <summary>The project's area display symbol, for room area sums.</summary>
        public string AreaUnit = "m²";
        /// <summary>Caveats the model must see (e.g. a highlight that could not zoom).</summary>
        public List<string> Notes = new();
        /// <summary>Closed worksets of the host model: one searched.not_searched entry, never the summary.</summary>
        public List<string> ClosedWorksets = new();
        /// <summary>The first find of the turn to meet the closed worksets: only it mentions them in next.</summary>
        public bool FirstClosedWorksetNotice;
        /// <summary>Loaded links a host-only search left out (data.retry searches them).</summary>
        public int LoadedLinksOutside;
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
                ["rows"] = shown.Select(r => RowData(r, o.AreaUnit)).ToList(),
            };

            List<Grouped>? groups = null;
            if (plan.GroupKeys.Count > 0)
            {
                groups = GroupRows(o.Rows, 0, plan.GroupKeys.Count);
                data["group_by"] = plan.GroupKeys.Count == 1
                    ? (object)plan.GroupKeys[0].ToString()
                    : plan.GroupKeys.Select(k => k.ToString()).ToList();
                data["groups"] = groups.Take(MaxGroups).Select(g => GroupData(g, o.AreaUnit)).ToList();
                if (groups.Count > MaxGroups) data["groups_total"] = groups.Count;
            }

            var rooms = o.Rows.Where(r => r.RoomStatus != null).ToList();
            if (rooms.Count > 0) data["rooms"] = RoomTotals(rooms, o.AreaUnit);

            if (plan.Queries.Count > 1)
                data["queries"] = o.Queries.Select((q, i) =>
                {
                    var d = new Dictionary<string, object?> { ["query"] = i + 1, ["count"] = q.Count, ["filter"] = q.Filter };
                    if (q.Handle != null) d["handle"] = q.Handle;
                    return d;
                }).ToList();

            if (count == 0) data["near_matches"] = o.NearMatches.Take(5).ToList();
            // The exact retry is a structured field, never query syntax in prose.
            if (count == 0 && o.LoadedLinksOutside > 0)
                data["retry"] = new Dictionary<string, object?> { ["source"] = "all", ["why"] = "also search the loaded links" };
            if (o.Notes.Count > 0) data["notes"] = o.Notes.ToList();

            var searched = new SearchedDto
            {
                Sources = o.Sources.Distinct().ToList(),
                Categories = o.Categories.Distinct().ToList(),
                Filter = o.Queries.Count == 0 ? null : string.Join(" | ", o.Queries.Select(q => q.Filter)),
                NotSearched = o.NotSearched.Distinct().ToList(),
            };
            if (o.ClosedWorksets.Count > 0) searched.NotSearched.Add(ClosedWorksetsEntry(o.ClosedWorksets));

            if (o.Partial)
            {
                searched.NotSearched.Add(FindScope.BudgetNote(o.Scanned));
                data["partial"] = true;
            }
            return ReadEnvelope.Done(
                (o.Partial ? "At least: " : "") + Summary(plan, o, groups) + RoomSummary(rooms, o.AreaUnit),
                data,
                count: count,
                handle: count > 0 ? o.Handle : null,
                searched: searched,
                next: o.Partial
                    ? "The search stopped after " + o.Scanned + " elements and found at least " + count + ". Add a category or level to narrow, then search again."
                    : Next(plan, o, count, shown.Count),
                highlight: o.Highlight,
                status: o.Partial ? EnvelopeStatus.Partial : EnvelopeStatus.Done);
        }

        private static Dictionary<string, object?> RowData(FoundRow r, string areaUnit)
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
            if (r.RoomStatus != null)
            {
                d["status"] = r.RoomStatus;
                if (r.RoomStatus == RoomStatus.Placed && r.AreaInternal.HasValue) d["area"] = AreaText(r.AreaInternal.Value, areaUnit);
            }
            foreach (var kv in r.Fields) d[kv.Key] = kv.Value;
            return d;
        }

        // ── groups (one or two keys) and room totals ─────────────────────────

        internal sealed class Grouped
        {
            public string Key = "";
            public List<FoundRow> Rows = new();
            public List<Grouped>? Inner;
        }

        private static List<Grouped> GroupRows(IEnumerable<FoundRow> rows, int level, int depth) =>
            rows.GroupBy(r => r.GroupKeys.Count > level && !string.IsNullOrWhiteSpace(r.GroupKeys[level]) ? r.GroupKeys[level]! : "(none)")
                .Select(g =>
                {
                    var list = g.ToList();
                    return new Grouped { Key = g.Key, Rows = list, Inner = level + 1 < depth ? GroupRows(list, level + 1, depth) : null };
                })
                .OrderByDescending(g => g.Rows.Count).ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

        public const int MaxInnerGroups = 20;

        private static Dictionary<string, object?> GroupData(Grouped g, string areaUnit)
        {
            var d = new Dictionary<string, object?> { ["key"] = g.Key, ["count"] = g.Rows.Count };
            var rooms = g.Rows.Where(r => r.RoomStatus != null).ToList();
            if (rooms.Count > 0)
            {
                var placed = rooms.Where(r => r.RoomStatus == RoomStatus.Placed).ToList();
                d["area"] = AreaText(placed.Sum(r => r.AreaInternal ?? 0), areaUnit);
                d["placed"] = placed.Count;
                var apart = rooms.Where(r => r.RoomStatus != RoomStatus.Placed)
                    .GroupBy(r => r.RoomStatus!).ToDictionary(x => x.Key, x => x.Count());
                if (apart.Count > 0) d["not_in_area"] = apart;
            }
            if (g.Inner != null)
            {
                d["groups"] = g.Inner.Take(MaxInnerGroups).Select(x => GroupData(x, areaUnit)).ToList();
                if (g.Inner.Count > MaxInnerGroups) d["groups_total"] = g.Inner.Count;
            }
            return d;
        }

        private static Dictionary<string, object?> RoomTotals(List<FoundRow> rooms, string areaUnit)
        {
            int Count(string s) => rooms.Count(r => r.RoomStatus == s);
            return new Dictionary<string, object?>
            {
                ["placed"] = Count(RoomStatus.Placed),
                ["not_placed"] = Count(RoomStatus.NotPlaced),
                ["not_enclosed"] = Count(RoomStatus.NotEnclosed),
                ["redundant"] = Count(RoomStatus.Redundant),
                ["area"] = AreaText(rooms.Where(r => r.RoomStatus == RoomStatus.Placed).Sum(r => r.AreaInternal ?? 0), areaUnit),
                ["note"] = "Area sums count placed rooms only; not placed, not enclosed and redundant rooms are counted apart.",
            };
        }

        private static string RoomSummary(List<FoundRow> rooms, string areaUnit)
        {
            if (rooms.Count == 0) return "";
            var placed = rooms.Where(r => r.RoomStatus == RoomStatus.Placed).ToList();
            var apart = rooms.Where(r => r.RoomStatus != RoomStatus.Placed).GroupBy(r => r.RoomStatus!)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Count() + " " + g.Key).ToList();
            return " Area of placed rooms: " + AreaText(placed.Sum(r => r.AreaInternal ?? 0), areaUnit)
                   + (apart.Count > 0 ? "; " + (rooms.Count - placed.Count) + " not in the area (" + string.Join(", ", apart) + ")" : "") + ".";
        }

        /// <summary>ft² as the project's area unit ("41.75 m²").</summary>
        internal static string AreaText(double squareFeet, string areaUnit)
        {
            var unit = string.IsNullOrEmpty(areaUnit) ? "m²" : areaUnit;
            var v = DisplayUnits.Convert(squareFeet, "ft²", unit) ?? squareFeet * 0.09290304;
            if (DisplayUnits.Convert(1, "ft²", unit) == null) unit = "m²";
            return Math.Round(v, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + unit;
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

        internal static string ClosedWorksetsEntry(IReadOnlyList<string> names)
        {
            var list = names.Distinct().ToList();
            return "worksets: " + string.Join(", ", list.Take(5)) + (list.Count > 5 ? " and " + (list.Count - 5) + " more" : "")
                   + " (closed, so their elements were not searched)";
        }

        private static string NotSearchedPhrase(FindOutcome o) =>
            o.NotSearched.Count == 0 ? "" : " Not searched: " + string.Join(", ", o.NotSearched.Take(4).Select(s => s.Replace(": ", " ")))
                + (o.NotSearched.Count > 4 ? " and " + (o.NotSearched.Count - 4) + " more" : "") + ".";

        private static string Summary(FindPlan plan, FindOutcome o, List<Grouped>? groups)
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
                    // Several categories (a categoryless text search): break down by category, else by type.
                    var byCategory = groups == null && o.Rows.Select(r => r.Category).Distinct().Count() > 1;
                    Func<FoundRow, string?> key = byCategory ? r => r.Category : r => r.Type;
                    var parts = groups != null
                        ? groups.Take(3).Select(GroupPhrase).ToList()
                        : o.Rows.Where(r => key(r) != null).GroupBy(r => key(r)!)
                            .Select(g => (g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal)
                            .Take(3).Select(g => g.Count + " " + g.Key).ToList();
                    var more = (groups?.Count ?? o.Rows.Where(r => key(r) != null).Select(key).Distinct().Count()) > 3 ? ", …" : "";
                    var sep = groups != null && groups.Any(g => g.Inner != null) ? "; " : ", ";
                    text = o.Rows.Count + " " + subject + " in " + where
                        + (parts.Count > 1 || groups != null ? ": " + string.Join(sep, parts) + more : "") + ".";
                }
            }
            return text + NotSearchedPhrase(o);
        }

        /// <summary>"9 Level 2" (one key) or "Level 2: 9 (5 C1, 4 C2)" (two keys).</summary>
        private static string GroupPhrase(Grouped g)
        {
            if (g.Inner == null) return g.Rows.Count + " " + g.Key;
            return g.Key + ": " + g.Rows.Count + " (" + string.Join(", ", g.Inner.Take(3).Select(i => i.Rows.Count + " " + i.Key))
                   + (g.Inner.Count > 3 ? ", …" : "") + ")";
        }

        private static string Next(FindPlan plan, FindOutcome o, int count, int shown)
        {
            string next;
            if (count == 0)
            {
                next = EmptyNext(o);
            }
            else if (shown < count)
                next = shown + " of " + count + " rows shown (limit " + plan.Limit + "). Use the handle to inspect or edit all " + count + ".";
            else
                next = "Use the handle to inspect, look at or edit these " + count + ".";
            // The handle itself is in the envelope's handle field: prose never repeats it.

            if (o.Highlight != null && o.Highlight.Selected != count)
                next += " Selected " + o.Highlight.Selected + " of the " + count + " found (the rest could not be selected).";
            if (o.Highlight != null)
            {
                var hidden = o.Highlight.Selected - o.Highlight.VisibleInView - o.HighlightUnknown;
                if (hidden > 0)
                    next += hidden == 1
                        ? " 1 selected element is not visible in the active view."
                        : " " + hidden + " selected elements are not visible in the active view.";
                if (o.HighlightUnknown > 0)
                    next += " Whether " + o.HighlightUnknown + " linked element" + (o.HighlightUnknown == 1 ? " is" : "s are")
                            + " visible could not be checked on this Revit version.";
            }
            if (count > 0 && o.FirstClosedWorksetNotice)
                next += " Some worksets are closed; their elements are not counted.";
            return next;
        }

        /// <summary>next for an empty result: why each source was not searched, in plain words.
        /// "Load it" only for a link that is not loaded.</summary>
        private static string EmptyNext(FindOutcome o)
        {
            static string LinkName(string entry) =>
                entry.Substring(entry.IndexOf(':') + 1, entry.IndexOf(" (", StringComparison.Ordinal) - entry.IndexOf(':') - 1).Trim();
            var unloaded = o.NotSearched.Where(s => s.StartsWith("link: ", StringComparison.Ordinal) && s.Contains("(unloaded"))
                .Select(LinkName).Distinct().ToList();
            var parts = new List<string>();
            if (unloaded.Count > 0)
                parts.Add((unloaded.Count == 1 ? "Link " + unloaded[0] + " is" : unloaded.Count + " links are")
                          + " not loaded, so not searched; the drafter can reload it in Manage Links.");
            if (o.LoadedLinksOutside > 0)
                parts.Add("Only the host model was searched; to include the loaded links, search again with data.retry.");
            if (o.FirstClosedWorksetNotice)
                parts.Add("Closed worksets were not searched.");
            parts.Add(o.NearMatches.Count > 0 ? "Else offer the near matches." : "Else say nothing matched in that scope.");

            // Whole sentences only: drop the lower-priority ones rather than cut one mid-word.
            var next = "";
            foreach (var p in parts)
            {
                var candidate = next.Length == 0 ? p : next + " " + p;
                if (EnvelopeInvariants.CodePoints(candidate) <= EnvelopeLimits.NextMaxChars) next = candidate;
            }
            return next.Length > 0 ? next : parts[0];
        }
    }

    public sealed class Relaxation
    {
        public Relaxation(string dropped, FindQuery query)
        {
            Dropped = dropped;
            Query = query;
        }

        /// <summary>level | text | type | parameter | room | in_view | source.</summary>
        public string Dropped { get; }
        public FindQuery Query { get; }
    }

    public static class NearMatchPlanner
    {
        /// <summary>One relaxed copy per condition, each dropping exactly one, most
        /// telling first. hostOnlyWithLinks adds a step that searches the links too.
        /// The category is never dropped (review C1): a near match stays in the same
        /// categories, so it can never become a whole-model walk.</summary>
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

    /// <summary>Room status for find and inspect rows (live test L9). Only placed rooms
    /// count towards area totals.</summary>
    public static class RoomStatus
    {
        public const string Placed = "placed";
        public const string NotPlaced = "not placed";
        public const string NotEnclosed = "not enclosed";
        public const string Redundant = "redundant";

        /// <param name="located">Room.Location is set (the room is placed in a plan).</param>
        /// <param name="area">Room.Area (any unit; 0 when Revit computes none).</param>
        /// <param name="redundantWarning">Revit's "multiple rooms in the same enclosed region" warning names it.</param>
        /// <param name="notEnclosedWarning">Revit's "room is not in a properly enclosed region" warning names it.</param>
        public static string Classify(bool located, double area, bool redundantWarning, bool notEnclosedWarning)
        {
            if (!located) return NotPlaced;
            if (redundantWarning) return Redundant;
            if (area <= 0 || notEnclosedWarning) return NotEnclosed;
            return Placed;
        }
    }
}
