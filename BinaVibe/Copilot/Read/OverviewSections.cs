#nullable enable
// Revit-free half of model_overview (spec §11 #1): argument parsing, the
// per-section narrowing and cut at `limit`, and the envelope. OverviewService
// (Revit-bound) fills OverviewSection rows from the existing inspectors and
// caches them; this file decides what the model sees.
//
// Each list section reports total/shown and, when cut, "N more; narrow with
// category=…" so the model never reads 50 rows as the whole model.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace BinaVibe.Copilot.Read
{
    /// <summary>One model_overview section as computed (before narrowing and cutting).</summary>
    public sealed class OverviewSection
    {
        public string Name = "";

        /// <summary>False for "project": no rows, only Extra fields.</summary>
        public bool IsList = true;

        public List<Dictionary<string, object?>> Rows = new();

        /// <summary>Section-level fields (e.g. by_level, templates, workshared).</summary>
        public Dictionary<string, object?> Extra = new();

        /// <summary>When each row groups several items (warnings grouped by text): the true
        /// number of items. The cut is always on rows; this is reported separately as
        /// "<ItemLabel>_total" and in the summary.</summary>
        public int? ItemTotal;

        /// <summary>What the grouped items are called ("warnings").</summary>
        public string? ItemLabel;

        /// <summary>Row field holding the items in each row ("count"), summed when narrowed.</summary>
        public string? ItemCountField;

        /// <summary>Row field(s), comma-separated, that `category` narrows by (contains,
        /// case/space-insensitive); null when the section was computed for the category already (types).</summary>
        public string? NarrowKey = "name";

        /// <summary>What to pass as category to narrow this section, for the hint.</summary>
        public string NarrowHint = "part of a name";
    }

    public static class OverviewSections
    {
        public const int DefaultLimit = 50;
        public const int MaxLimit = 200;

        public static IReadOnlyList<string> All { get; } = new[]
        {
            "levels", "grids", "rooms", "types", "views", "sheets", "links", "worksets", "warnings", "project",
        };

        public sealed class Args
        {
            public List<string> Sections = new();
            public string? Category;
            public int Limit = DefaultLimit;
        }

        public static bool TryParse(JsonElement args, out Args parsed, out string error)
        {
            parsed = new Args();
            error = "";
            var allowed = string.Join(", ", All);
            var r = new ArgReader();
            if (!r.IsObject(args, "model_overview arguments")) { error = r.Error + " Give sections: one or more of " + allowed + "."; return false; }
            r.Known(args, "", "sections", "category", "limit");
            var sections = r.Strs(args, "sections");
            if (r.Ok && (sections == null || sections.Count == 0))
                r.Fail("Give sections: a list with one or more of " + allowed + ".");
            foreach (var raw in sections ?? new List<string>())
            {
                var s = raw.Trim().ToLowerInvariant();
                if (!All.Contains(s)) { r.Fail("Unknown section '" + raw + "'. Use one or more of " + allowed + "."); continue; }
                if (!parsed.Sections.Contains(s)) parsed.Sections.Add(s);
            }
            parsed.Category = r.Str(args, "category");
            parsed.Limit = r.Int(args, "limit", DefaultLimit, 1, MaxLimit);
            if (!r.Ok)
            {
                error = r.Error.Contains(allowed) ? r.Error : r.Error + " sections: one or more of " + allowed + ".";
                return false;
            }
            return true;
        }

        /// <summary>Case-, space- and punctuation-insensitive form for narrowing.</summary>
        internal static string Fold(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s!.Length);
            foreach (var c in s.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>The section as the model sees it: narrowed by category, cut at limit.</summary>
        public static Dictionary<string, object?> Shape(OverviewSection s, int limit, string? category, out int total, out int more) =>
            Shape(s, limit, category, out total, out more, out _);

        public static Dictionary<string, object?> Shape(OverviewSection s, int limit, string? category, out int total, out int more, out int? items)
        {
            var shaped = new Dictionary<string, object?>();
            total = 0;
            more = 0;
            items = null;
            if (!s.IsList)
            {
                foreach (var kv in s.Extra) shaped[kv.Key] = kv.Value;
                return shaped;
            }

            IEnumerable<Dictionary<string, object?>> rows = s.Rows;
            bool narrowed = false;
            if (!string.IsNullOrWhiteSpace(category) && s.NarrowKey != null)
            {
                var want = Fold(category);
                var keys = s.NarrowKey.Split(',');
                rows = rows.Where(r => keys.Any(k =>
                    r.TryGetValue(k.Trim(), out var v) && Fold(Convert.ToString(v, CultureInfo.InvariantCulture)).Contains(want)));
                narrowed = true;
            }
            var list = rows.ToList();
            total = list.Count;
            var shown = list.Take(limit).ToList();
            more = Math.Max(0, total - shown.Count);

            shaped["rows"] = shown;
            shaped["total"] = total;
            shaped["shown"] = shown.Count;
            if (s.ItemLabel != null)
            {
                items = narrowed || !s.ItemTotal.HasValue
                    ? list.Sum(r => s.ItemCountField != null && r.TryGetValue(s.ItemCountField, out var c) && c != null
                        ? Convert.ToInt32(c, CultureInfo.InvariantCulture) : 1)
                    : s.ItemTotal.Value;
                shaped[s.ItemLabel + "_total"] = items;
            }
            if (narrowed) shaped["narrowed_by"] = category;
            if (more > 0)
                shaped["hint"] = more + " more" + (s.ItemLabel != null ? " groups" : "")
                                 + "; narrow with category=<" + s.NarrowHint + "> or raise limit (max " + MaxLimit + ").";
            if (more > 0) shaped["more"] = more;
            foreach (var kv in s.Extra) shaped[kv.Key] = kv.Value;
            return shaped;
        }

        public static Envelope Build(string docTitle, IReadOnlyList<OverviewSection> sections, Args args)
        {
            var data = new Dictionary<string, object?>();
            var counts = new List<string>();
            var cut = new List<string>();
            foreach (var s in sections)
            {
                data[s.Name] = Shape(s, args.Limit, args.Category, out var total, out var more, out var items);
                if (!s.IsList) continue;
                counts.Add(items.HasValue ? items + " " + s.ItemLabel + " in " + total + " groups" : total + " " + s.Name);
                if (more > 0) cut.Add(s.Name + ": " + more + " more");
            }

            var summary = "Overview of '" + ReadEnvelope.Clip(docTitle ?? "", 80) + "'"
                + (counts.Count > 0 ? ": " + string.Join(", ", counts) : "")
                + (string.IsNullOrWhiteSpace(args.Category) ? "" : " (narrowed by '" + args.Category + "')")
                + ".";
            var next = cut.Count == 0 ? null
                : string.Join("; ", cut) + ". Narrow with category=… or raise limit (max " + MaxLimit + ").";
            return ReadEnvelope.Done(summary, data, next: next);
        }
    }

    /// <summary>The overview for one document state: every section computed once per
    /// key. A new key (save, edit, another document) drops everything.</summary>
    public sealed class OverviewCache
    {
        private readonly object _gate = new();
        private OverviewCacheKey? _key;
        private readonly Dictionary<string, OverviewSection> _sections = new(StringComparer.Ordinal);

        public bool TryGet(OverviewCacheKey key, string section, out OverviewSection value)
        {
            lock (_gate)
            {
                if (_key.HasValue && _key.Value.Equals(key) && _sections.TryGetValue(section, out var s))
                {
                    value = s;
                    return true;
                }
                value = null!;
                return false;
            }
        }

        public void Put(OverviewCacheKey key, string section, OverviewSection value)
        {
            lock (_gate)
            {
                if (!_key.HasValue || !_key.Value.Equals(key))
                {
                    _sections.Clear();
                    _key = key;
                }
                _sections[section] = value;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _sections.Clear();
                _key = null;
            }
        }
    }
}
