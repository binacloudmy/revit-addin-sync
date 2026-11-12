#nullable enable
// Home "<Category> in view" card (board 1): the dominant category of the active
// view's elements, its element count, and the split by type as a proportional bar
// (top 4 types plus "N more types") with a two-column legend. Pure: the Revit read
// (ChatView.Home.cs, on Revit's thread) only hands over (category, type) pairs.
//
// Revit-free: compiles in Tests.csproj.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class ViewSummarySegment
    {
        public string Label = "";
        public int Count;
        /// <summary>The "N more types" bucket.</summary>
        public bool IsMore;
        /// <summary>Share of the category's elements, 0..1.</summary>
        public double Fraction;
    }

    public sealed class ViewSummary : IEquatable<ViewSummary>
    {
        public const int MaxTypes = 4;

        public string Category = "";
        public int Total;
        public int TypeCount;
        public List<ViewSummarySegment> Segments = new List<ViewSummarySegment>();

        /// <summary>"Lighting Fixtures in view".</summary>
        public string Title => Category + " in view";

        /// <summary>"235 · 10 types".</summary>
        public string Right =>
            Total.ToString(CultureInfo.InvariantCulture) + " · " + TypeCount.ToString(CultureInfo.InvariantCulture) + (TypeCount == 1 ? " type" : " types");

        /// <summary>The bar's accessible name: "235 Lighting Fixtures by type: A 126, B 38, 6 more types 35".</summary>
        public string Description =>
            Total.ToString(CultureInfo.InvariantCulture) + " " + Category + " by type: "
            + string.Join(", ", Segments.Select(s => s.Label + " " + s.Count.ToString(CultureInfo.InvariantCulture)));

        /// <summary>Builds the summary from one (category, type) pair per element of the view. Null when there
        /// are no elements, so Home hides the card. The dominant category is the one with the most elements
        /// (ties: first by name); a blank type name counts as "No type".</summary>
        public static ViewSummary? Build(IEnumerable<(string category, string type)>? elements)
        {
            if (elements == null) return null;
            var byCategory = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (category, type) in elements)
            {
                if (string.IsNullOrWhiteSpace(category)) continue;
                var cat = category.Trim();
                var t = string.IsNullOrWhiteSpace(type) ? "No type" : type.Trim();
                if (!byCategory.TryGetValue(cat, out var types)) byCategory[cat] = types = new Dictionary<string, int>(StringComparer.Ordinal);
                types[t] = types.TryGetValue(t, out var n) ? n + 1 : 1;
                totals[cat] = totals.TryGetValue(cat, out var c) ? c + 1 : 1;
            }
            if (totals.Count == 0) return null;

            var dominant = totals.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First();
            var ordered = byCategory[dominant.Key].OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();

            var s = new ViewSummary { Category = dominant.Key, Total = dominant.Value, TypeCount = ordered.Count };
            foreach (var kv in ordered.Take(MaxTypes))
                s.Segments.Add(new ViewSummarySegment { Label = kv.Key, Count = kv.Value, Fraction = (double)kv.Value / s.Total });
            var rest = ordered.Skip(MaxTypes).ToList();
            if (rest.Count > 0)
            {
                var count = rest.Sum(kv => kv.Value);
                s.Segments.Add(new ViewSummarySegment
                {
                    Label = rest.Count.ToString(CultureInfo.InvariantCulture) + (rest.Count == 1 ? " more type" : " more types"),
                    Count = count, IsMore = true, Fraction = (double)count / s.Total,
                });
            }
            return s;
        }

        // Value equality: Home compares the scope it just read with the one it shows, and
        // redraws only when they differ (a reference compare would redraw on every read).
        public bool Equals(ViewSummary? o) =>
            o != null && Category == o.Category && Total == o.Total && TypeCount == o.TypeCount
            && Segments.Count == o.Segments.Count
            && Segments.Zip(o.Segments, (a, b) => a.Label == b.Label && a.Count == b.Count && a.IsMore == b.IsMore).All(x => x);

        public override bool Equals(object? obj) => Equals(obj as ViewSummary);

        public override int GetHashCode() => (Category, Total, TypeCount, Segments.Count).GetHashCode();

        /// <summary>True when two reads show the same card (both hidden counts as the same).</summary>
        public static bool Same(ViewSummary? a, ViewSummary? b) => a == null ? b == null : a.Equals(b);
    }
}
