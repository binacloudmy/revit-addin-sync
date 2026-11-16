#nullable enable
// The "Created" result card grouped by category (boards 8-10): "5 Sheets · 1 View",
// one row per category with its count, a source line, and the text the "Copy list" button
// puts on the clipboard.
//
// The rows are what the drafter got (live R7: for 5 sheets the card listed "other 33, Viewports 16,
// SketchPlane 7, GraphicsStyle 5, Schedule Graphics 5, Sheets 5, ..."). They are led by the commits'
// data.created: the add-in's own list of the visible, non-benign elements a code call created, by
// name (RevitWriteEngine.CreatedItems: sheets, views, levels, grids, rooms, types; never a viewport,
// sketch plane or graphics style). Only when no commit listed any (an older add-in, a typed tool) do
// the rows come from changes.touched_by_category, without Revit's by-products ("class:" buckets such
// as SketchPlane and GraphicsStyle, Sun Path, Work Plane Grid, Schedule Graphics, "other"). What
// Revit added beyond the touched elements (side_effects) is never a row. Everything else the commits
// added is one quiet line, "and 74 supporting elements"; Undo still removes all of it (unchanged).
//
// Revit-free: compiles in Tests.csproj.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    public sealed class CreatedGroup
    {
        public string Category = "";
        public int Count;
        /// <summary>Names of some of them (data.created lists at most 50 per call); may be empty.</summary>
        public List<string> Names = new List<string>();
    }

    public sealed class CreatedSummary
    {
        /// <summary>Headline names at most this many categories; the rest are "+ N more".</summary>
        public const int HeadlineCategories = 3;

        /// <summary>data.created lists at most this many items per call (EnvelopeLimits.MaxRows); past it the category's
        /// count from touched_by_category is used when larger.</summary>
        public const int CreatedListCap = 50;

        public List<CreatedGroup> Groups = new List<CreatedGroup>();
        /// <summary>Where the work came from: the attachment's name, or the prompt.</summary>
        public string Source = "";
        /// <summary>What the rows count (what the drafter got).</summary>
        public int Total => Groups.Sum(g => g.Count);
        /// <summary>Everything else the commits added (viewports of a sheet, sketch planes, graphics styles, ...): one quiet
        /// line, never rows. Undo removes them all the same.</summary>
        public int Supporting;

        /// <summary>Null when the commits carry no per-category counts (the card keeps its element rows).</summary>
        public static CreatedSummary? From(IEnumerable<Envelope> commits, string? attachment, ReceiptLanguage lang)
        {
            var named = new Dictionary<string, CreatedGroup>(StringComparer.Ordinal);     // from data.created
            var touched = new Dictionary<string, CreatedGroup>(StringComparer.Ordinal);   // from touched_by_category
            var added = 0;                                                                // every element the commits added
            var anyCounts = false;
            foreach (var env in commits ?? Enumerable.Empty<Envelope>())
            {
                if (env == null) continue;
                var t = env.Changes?.TouchedByCategory?.AddedByCategory;
                var s = env.Changes?.SideEffects?.AddedByCategory;
                if (t == null && s == null) continue;
                anyCounts = true;
                foreach (var kv in (t ?? new Dictionary<string, int>()).Concat(s ?? new Dictionary<string, int>()))
                    if (kv.Value > 0) added += kv.Value;
                var envTouched = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var kv in t ?? new Dictionary<string, int>())
                {
                    if (kv.Value <= 0) continue;
                    var key = Clean(kv.Key);
                    envTouched[key] = (envTouched.TryGetValue(key, out var have) ? have : 0) + kv.Value;
                    if (IsByProduct(kv.Key)) continue;
                    if (!touched.TryGetValue(key, out var tg)) touched[key] = tg = new CreatedGroup { Category = key };
                    tg.Count += kv.Value;
                }
                var listed = (ToolResults.Write(env)?.Created ?? Array.Empty<CreatedItemDto>())
                    .Where(c => !string.IsNullOrWhiteSpace(c.Category) && !string.IsNullOrWhiteSpace(c.Name)).ToList();
                foreach (var cat in listed.GroupBy(c => Clean(c.Category!)))
                {
                    if (!named.TryGetValue(cat.Key, out var g)) named[cat.Key] = g = new CreatedGroup { Category = cat.Key };
                    var n = cat.Count();
                    // A list cut at its cap: the category's own count, when the envelope has a larger one.
                    if (listed.Count >= CreatedListCap && envTouched.TryGetValue(cat.Key, out var whole) && whole > n) n = whole;
                    g.Count += n;
                    g.Names.AddRange(cat.Select(c => c.Name!.Trim()));
                }
            }
            if (!anyCounts) return null;
            var rows = named.Count > 0 ? named : touched;
            if (rows.Count == 0) return null;
            var summary = new CreatedSummary
            {
                Groups = rows.Values.OrderByDescending(g => g.Count).ThenBy(g => g.Category, StringComparer.Ordinal).ToList(),
            };
            summary.Supporting = Math.Max(0, added - summary.Total);
            var ms = lang == ReceiptLanguage.Ms;
            var at = string.IsNullOrWhiteSpace(attachment) ? "" : attachment!.Trim();
            summary.Source = at.Length > 0 ? at : ms ? "Prompt anda" : "Your prompt";
            return summary;
        }

        /// <summary>"6 Sheets · 6 Viewports"; past three categories, "· + 2 more" ("· + 2 lagi").</summary>
        public string Headline(ReceiptLanguage lang)
        {
            var parts = Groups.Take(HeadlineCategories).Select(g => Label(g, lang)).ToList();
            var more = Groups.Count - HeadlineCategories;
            if (more > 0) parts.Add("+ " + more.ToString(CultureInfo.InvariantCulture) + (lang == ReceiptLanguage.Ms ? " lagi" : " more"));
            return string.Join(" · ", parts);
        }

        /// <summary>The one quiet line under the rows: "and 74 supporting elements" ("dan 74 elemen sokongan"); null when
        /// the commits added nothing beyond the rows.</summary>
        public string? SupportingLine(ReceiptLanguage lang)
        {
            if (Supporting <= 0) return null;
            var n = Supporting.ToString(CultureInfo.InvariantCulture);
            return lang == ReceiptLanguage.Ms ? "dan " + n + " elemen sokongan" : "and " + n + " supporting element" + (Supporting == 1 ? "" : "s");
        }

        /// <summary>Revit's by-products of a creation, never a row: a "class:" bucket (an element with no category, such as
        /// SketchPlane or GraphicsStyle), a generic bucket ("Element", "Other"), and the categories Revit makes along with a
        /// view or a sheet (Sun Path, Work Plane Grid, Schedule Graphics).</summary>
        public static bool IsByProduct(string? rawCategory)
        {
            var c = (rawCategory ?? "").Trim();
            if (c.Length == 0 || c.StartsWith("class:", StringComparison.Ordinal) || IsGeneric(c)) return true;
            return ByProductCategories.Contains(c);
        }

        private static readonly HashSet<string> ByProductCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Sun Path", "Work Plane Grid", "Schedule Graphics", "SketchPlane", "GraphicsStyle",
        };

        /// <summary>"Source: Your prompt" / "Source: brochure.pdf".</summary>
        public string SourceLine(ReceiptLanguage lang) => (lang == ReceiptLanguage.Ms ? "Sumber: " : "Source: ") + Source;

        /// <summary>"6 Sheets" (a count of one reads "1 Sheet").</summary>
        public static string Label(CreatedGroup g, ReceiptLanguage lang = ReceiptLanguage.En) =>
            IsGeneric(g.Category)
                ? g.Count.ToString(CultureInfo.InvariantCulture) + " " + DisplayName(g.Category, lang)
                : g.Count.ToString(CultureInfo.InvariantCulture) + " " + (g.Count == 1 ? Singular(g.Category) : g.Category);

        /// <summary>Revit's catch-all buckets ("Element" for a class with no category, "Other") are no name for a drafter.</summary>
        public static bool IsGeneric(string? category) =>
            string.Equals(category, "Element", StringComparison.OrdinalIgnoreCase) || string.Equals(category, "Elements", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "Other", StringComparison.OrdinalIgnoreCase);

        /// <summary>The name to show for a category: "other" for a generic bucket (so "4 other", not "4 Element").</summary>
        public static string DisplayName(string category, ReceiptLanguage lang) =>
            IsGeneric(category) ? (lang == ReceiptLanguage.Ms ? "lain-lain" : "other") : category;

        /// <summary>Tab-separated, one line per category: category, count, the names that are known.
        /// Pastes into a spreadsheet or a message.</summary>
        public string CopyText()
        {
            var sb = new StringBuilder("Category\tCount\tNames");
            foreach (var g in Groups)
            {
                sb.Append('\n').Append(DisplayName(g.Category, ReceiptLanguage.En)).Append('\t').Append(g.Count.ToString(CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(string.Join("; ", g.Names.Select(n => n.Replace('\t', ' ').Replace('\n', ' '))));
            }
            return sb.ToString();
        }

        // Revit's own category names are plural ("Sheets", "Lighting Fixtures"); "class:Foo" marks one it has none for.
        private static string Clean(string raw)
        {
            var c = (raw ?? "").Trim();
            if (c.StartsWith("class:", StringComparison.Ordinal)) c = c.Substring(6);
            return c.Length == 0 ? "Other" : c;
        }

        private static string Singular(string plural)
        {
            if (plural.EndsWith("ies", StringComparison.Ordinal) && plural.Length > 3) return plural.Substring(0, plural.Length - 3) + "y";
            // A name that only looks plural keeps Revit's own spelling: Analysis, Status, Glass.
            if (plural.EndsWith("is", StringComparison.OrdinalIgnoreCase) || plural.EndsWith("us", StringComparison.OrdinalIgnoreCase)) return plural;
            if (plural.EndsWith("s", StringComparison.Ordinal) && !plural.EndsWith("ss", StringComparison.Ordinal) && plural.Length > 1) return plural.Substring(0, plural.Length - 1);
            return plural;
        }
    }
}
