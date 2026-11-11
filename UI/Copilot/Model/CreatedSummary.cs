#nullable enable
// The "Created" result card grouped by category (boards 8-10): "6 Sheets · 6 Viewports",
// one row per category with its count, a source line, and the text the "Copy list" button
// puts on the clipboard. Counts come from the commit envelopes' added_by_category
// (changes.touched_by_category plus what Revit added beyond the touched elements);
// names, when a code call listed them (data.created), only feed the copied list.
// Per-sheet / per-view rows (with scale) are a later phase.
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

        public List<CreatedGroup> Groups = new List<CreatedGroup>();
        /// <summary>Where the work came from: the attachment's name, or the prompt.</summary>
        public string Source = "";
        public int Total => Groups.Sum(g => g.Count);

        /// <summary>Null when the commits carry no per-category counts (the card keeps its element rows).</summary>
        public static CreatedSummary? From(IEnumerable<Envelope> commits, string? attachment, ReceiptLanguage lang)
        {
            var by = new Dictionary<string, CreatedGroup>(StringComparer.Ordinal);
            CreatedGroup Group(string raw)
            {
                var name = Clean(raw);
                if (!by.TryGetValue(name, out var g)) by[name] = g = new CreatedGroup { Category = name };
                return g;
            }
            foreach (var env in commits ?? Enumerable.Empty<Envelope>())
            {
                if (env == null) continue;
                var counts = new List<Dictionary<string, int>>();
                if (env.Changes?.TouchedByCategory?.AddedByCategory is { } t) counts.Add(t);
                // Added beyond the touched elements (a sheet's viewports, for one) is still created, and Undo removes it.
                if (env.Changes?.SideEffects?.AddedByCategory is { } s) counts.Add(s);
                foreach (var d in counts)
                    foreach (var kv in d)
                        if (kv.Value > 0) Group(kv.Key).Count += kv.Value;
                if (counts.Count == 0) continue;
                foreach (var c in ToolResults.Write(env)?.Created ?? Array.Empty<CreatedItemDto>())
                {
                    if (string.IsNullOrWhiteSpace(c.Category) || string.IsNullOrWhiteSpace(c.Name)) continue;
                    if (by.TryGetValue(Clean(c.Category!), out var g)) g.Names.Add(c.Name!.Trim());
                }
            }
            if (by.Count == 0) return null;
            var summary = new CreatedSummary
            {
                Groups = by.Values.OrderByDescending(g => g.Count).ThenBy(g => g.Category, StringComparer.Ordinal).ToList(),
            };
            var ms = lang == ReceiptLanguage.Ms;
            var at = string.IsNullOrWhiteSpace(attachment) ? "" : attachment!.Trim();
            summary.Source = at.Length > 0 ? at : ms ? "Prompt anda" : "Your prompt";
            return summary;
        }

        /// <summary>"6 Sheets · 6 Viewports"; past three categories, "· + 2 more" ("· + 2 lagi").</summary>
        public string Headline(ReceiptLanguage lang)
        {
            var parts = Groups.Take(HeadlineCategories).Select(g => Label(g)).ToList();
            var more = Groups.Count - HeadlineCategories;
            if (more > 0) parts.Add("+ " + more.ToString(CultureInfo.InvariantCulture) + (lang == ReceiptLanguage.Ms ? " lagi" : " more"));
            return string.Join(" · ", parts);
        }

        /// <summary>"Source: Your prompt" / "Source: brochure.pdf".</summary>
        public string SourceLine(ReceiptLanguage lang) => (lang == ReceiptLanguage.Ms ? "Sumber: " : "Source: ") + Source;

        /// <summary>"6 Sheets" (a count of one reads "1 Sheet").</summary>
        public static string Label(CreatedGroup g) =>
            g.Count.ToString(CultureInfo.InvariantCulture) + " " + (g.Count == 1 ? Singular(g.Category) : g.Category);

        /// <summary>Tab-separated, one line per category: category, count, the names that are known.
        /// Pastes into a spreadsheet or a message.</summary>
        public string CopyText()
        {
            var sb = new StringBuilder("Category\tCount\tNames");
            foreach (var g in Groups)
            {
                sb.Append('\n').Append(g.Category).Append('\t').Append(g.Count.ToString(CultureInfo.InvariantCulture)).Append('\t');
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
            if (plural.EndsWith("s", StringComparison.Ordinal) && !plural.EndsWith("ss", StringComparison.Ordinal) && plural.Length > 1) return plural.Substring(0, plural.Length - 1);
            return plural;
        }
    }
}
