#nullable enable
// Rows and marks point at each other (result marks design, rule 7; build step 2). Every decision that needs no Revit:
//   focus    a card row's group: which marks turn bright and which dim, the label outside the group
//            ("(DA20) 750 x 2100 single · 20 door tags") and where it goes, the key's focus line
//            ("Showing (DA20) · 20 of 58", with Show all).
//   click    a mark clicked in the model: the row it belongs to and its "3 of 8", the line the card row flashes
//            ("Tag 3 of 8 selected in Revit"), the label next to the mark ("Door tag P3 · (DC08) … · 3 of 8").
//   targets  what a card row shows: an item row, a type group, a created category, a finding's elements.
// InModelResult does the Revit side (zoom, selection, opening a view, the images); the cards only pass a target.
// Revit-free and WPF-free. Every word is English (rule 11).

using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>How a mark is drawn while a group is focused: as usual, bright (in the group) or dim (the others).</summary>
    public enum MarkTone { Normal, Bright, Dim }

    /// <summary>A mark clicked in the model, as the card hears it: the element, the row it belongs to, and the words.</summary>
    public sealed class MarkClick
    {
        public string TurnId = "";
        public long Id;
        /// <summary>The element's type id (-1 when it has none): a type group's row matches its instances by it.</summary>
        public long TypeId = -1;
        /// <summary>The element's category ("Door Tags"), "" when not known.</summary>
        public string Category = "";
        /// <summary>The row (mark group) the element was found in, "" when none.</summary>
        public string RowTitle = "";
        public int N, M;
        /// <summary>"Tag 3 of 8 selected in Revit": what the card row says when it flashes.</summary>
        public string Line = "";
        /// <summary>"Door tag P3 · (DC08) 900 x 2100 single · 3 of 8": the label next to the mark.</summary>
        public string Label = "";
    }

    /// <summary>The label the overlay draws outside a focused group or next to a clicked mark.</summary>
    public sealed class MarkLabel
    {
        /// <summary>The part drawn bold (the row's title, or "Door tag P3").</summary>
        public string Bold = "";
        /// <summary>The rest, muted (" · 20 door tags").</summary>
        public string Rest = "";
        public string Text => Bold + Rest;
        /// <summary>Where the group's marks are, as percent of the view (the label goes outside their box).</summary>
        public List<(double XPct, double YPct)> Points = new List<(double, double)>();
        /// <summary>Half a mark's size in pixels: the box round the points is this much bigger on each side.</summary>
        public double PadPx = 11;
        /// <summary>Under the box (a clicked mark), not above it (a group).</summary>
        public bool Below;
    }

    public static class MarkFocus
    {
        /// <summary>Gap between the label and the marks' box, and the least margin to the view's edge.</summary>
        public const double LabelGap = 8, LabelMargin = 8;

        // ─── Targets: what a card row shows ───────────────────────────────────

        /// <summary>A server item row (a door type with its tags, an element group).</summary>
        public static MarkGroup ForItemRow(ItemRow row)
        {
            var ids = (row?.Ids ?? new List<long>()).Distinct().ToList();
            return new MarkGroup
            {
                Title = (row?.Title ?? "").Trim(), Ids = ids, Count = Math.Max(row?.Count ?? 0, ids.Count), Named = true,
                Kind = row != null && row.Kind == BinaVibe.Copilot.Contracts.ResultItemKinds.Tag ? MarkKind.Annotation : MarkKind.Element,
            };
        }

        /// <summary>A type group (its instances carry the change) or an element group of a change.</summary>
        public static MarkGroup ForTypeGroup(TypeGroup g) => new MarkGroup
        {
            Title = g.Title, Category = g.Category ?? "", Outcome = MarkOutcome.Changed,
            Kind = g.IsType ? MarkKind.TypeInstances : HighlightSet.KindOf(g.TypeName),
            Ids = g.Ids.ToList(), Count = g.Instances > 0 ? g.Instances : g.Ids.Count, Named = true,
        };

        /// <summary>A created category row ("Door Tags 58"): its elements are the result's made group of that category.</summary>
        public static MarkGroup ForCategory(string category, int count)
        {
            var cat = HighlightSet.Clean(category);
            return new MarkGroup { Title = cat, Category = cat, Kind = HighlightSet.KindOf(cat), Count = count };
        }

        /// <summary>A row of named elements (a created element, a finding).</summary>
        public static MarkGroup ForElements(string title, IEnumerable<long> ids)
        {
            var list = (ids ?? Array.Empty<long>()).Distinct().ToList();
            return new MarkGroup { Title = (title ?? "").Trim(), Ids = list, Count = list.Count, Kind = MarkKind.Element, Named = true };
        }

        /// <summary>The row can show something in the model: it has ids, or it names a category the result made.</summary>
        public static bool CanShow(MarkGroup? target) =>
            target != null && (target.Ids.Count > 0 || (target.Kind != MarkKind.TypeInstances && target.Category.Length > 0));

        /// <summary>The elements a target stands for, without Revit: its ids; a category row's ids are those of the result's
        /// groups of that category. Null for a type group: Revit finds its instances.</summary>
        public static List<long>? ElementIds(MarkGroup target, HighlightSet? set)
        {
            if (target == null) return new List<long>();
            if (target.Kind == MarkKind.TypeInstances) return null;
            if (target.Ids.Count > 0) return target.Ids.Distinct().ToList();
            if (target.Category.Length == 0 || set == null) return new List<long>();
            var cat = HighlightSet.Clean(target.Category);
            return set.Kinds.Where(k => k.Kind != MarkKind.TypeInstances && string.Equals(HighlightSet.Clean(k.Category), cat, StringComparison.Ordinal))
                .SelectMany(k => k.Ids).Distinct().ToList();
        }

        /// <summary>The category whose name the label counts in: the target's own, else the result group its first id is in.</summary>
        public static string CategoryOf(MarkGroup target, HighlightSet? set)
        {
            if (target == null) return "";
            if (target.Category.Length > 0) return target.Category;
            if (set == null || target.Ids.Count == 0) return "";
            var first = new HashSet<long>(target.Ids);
            return set.Kinds.FirstOrDefault(k => k.Kind != MarkKind.TypeInstances && k.Ids.Any(first.Contains))?.Category ?? "";
        }

        // ─── Focus: bright, dim, the label and the key's line ─────────────────

        /// <summary>No focus: every mark as usual; with one, the group's marks bright and the others dim.</summary>
        public static MarkTone ToneOf(long id, ICollection<long>? focused) =>
            focused == null ? MarkTone.Normal : focused.Contains(id) ? MarkTone.Bright : MarkTone.Dim;

        /// <summary>The part of a mark image's file name that names its tone ("" for the usual one, so step 1's files stay).</summary>
        public static string ToneSuffix(MarkTone t) => t == MarkTone.Bright ? "-bright" : t == MarkTone.Dim ? "-dim" : "";

        /// <summary>"door tags" / "door tag" / "elements" / "element".</summary>
        public static string Noun(int n, string? category)
        {
            var c = HighlightSet.Clean(category);
            if (c == "Elements") return n == 1 ? "element" : "elements";
            return MarkKey.Lower(n == 1 ? MarkKey.Singular(c) : c);
        }

        /// <summary>The row's title as the label and the key show it: a long name cut in its middle (its end tells types apart).</summary>
        public static string LabelTitle(string? title)
        {
            var t = (title ?? "").Trim();
            return t.Contains(" → ") ? MarkKey.ShortChange(t, 44) : MarkKey.ShortName(t, 44);
        }

        /// <summary>The key's short name for a row: its leading code ("(DA20)") when it has one, else the name, shortened.</summary>
        public static string ShortTitle(string? title)
        {
            var t = (title ?? "").Trim();
            if (t.StartsWith("(", StringComparison.Ordinal))
            {
                var close = t.IndexOf(')');
                if (close > 1 && close <= 16 && close < t.Length - 1) return t.Substring(0, close + 1);
            }
            return t.Contains(" → ") ? MarkKey.ShortChange(t, 28) : MarkKey.ShortName(t, 28);
        }

        /// <summary>The label outside a focused group: "(DA20) 750 x 2100 single · 20 door tags"; when Revit found fewer than
        /// the row counts (the server kept only some ids): "· 1 of 4 door tags".</summary>
        public static MarkLabel GroupLabel(MarkGroup target, int found, string? category)
        {
            var count = Math.Max(target?.Count ?? 0, found);
            var rest = found < count
                ? " · " + HighlightSet.N(found) + " of " + HighlightSet.N(count) + " " + Noun(count, category)
                : " · " + HighlightSet.N(found) + " " + Noun(found, category);
            return new MarkLabel { Bold = LabelTitle(target?.Title), Rest = rest };
        }

        /// <summary>The key's focus line with a group focused: "Showing (DA20) · 20 of 58". The bold part is the short title.</summary>
        public static (string Line, string Bold) FocusLine(MarkGroup target, int found, int total)
        {
            var name = ShortTitle(target?.Title);
            if (name.Length == 0) name = "this group";
            return ("Showing " + name + " · " + HighlightSet.N(found) + " of " + HighlightSet.N(Math.Max(total, found)), name);
        }

        /// <summary>The key's focus line after a mark click: "Showing (DC08) · tag 3 of 8 selected".</summary>
        public static (string Line, string Bold) ClickFocusLine(MarkClick click)
        {
            var word = Word(click.Category).ToLowerInvariant();
            if (click.RowTitle.Length == 0 || click.M <= 1)
                return ("Showing " + MarkKey.Lower(Full(click.Category)) + " selected", "");
            var name = ShortTitle(click.RowTitle);
            return ("Showing " + name + " · " + word + " " + HighlightSet.N(click.N) + " of " + HighlightSet.N(click.M) + " selected", name);
        }

        /// <summary>Put the focus into the key's words: the focus line, its bold part, and Show all.</summary>
        public static void Apply(KeyText key, MarkGroup? target, int found, MarkClick? click)
        {
            if (key == null || (target == null && click == null)) return;
            var (line, bold) = click != null ? ClickFocusLine(click) : FocusLine(target!, found, key.Total);
            key.Focus = line; key.FocusBold = bold.Length > 0 ? bold : null;
            key.ShowAll = true;
        }

        /// <summary>Where the label goes: centred over the box of the marks (under it for a clicked mark), outside it, inside
        /// the view; on the other side when there is no room; clamped to the view's edges.</summary>
        public static (double X, double Y) LabelAt(double left, double top, double right, double bottom, double w, double h, double viewW, double viewH, bool below)
        {
            var cx = (left + right) / 2;
            var x = cx - w / 2;
            double above = top - LabelGap - h, under = bottom + LabelGap;
            var fitsAbove = above >= LabelMargin; var fitsUnder = under + h <= viewH - LabelMargin;
            var y = below ? (fitsUnder || !fitsAbove ? under : above) : (fitsAbove || !fitsUnder ? above : under);
            x = Math.Max(LabelMargin, Math.Min(x, viewW - w - LabelMargin));
            y = Math.Max(LabelMargin, Math.Min(y, viewH - h - LabelMargin));
            return (x, y);
        }

        /// <summary>The box round points in pixels, <paramref name="pad"/> bigger on each side; null when there is none.</summary>
        public static (double L, double T, double R, double B)? Box(IEnumerable<(double X, double Y)> px, double pad)
        {
            var list = (px ?? Array.Empty<(double, double)>()).ToList();
            if (list.Count == 0) return null;
            return (list.Min(p => p.X) - pad, list.Min(p => p.Y) - pad, list.Max(p => p.X) + pad, list.Max(p => p.Y) + pad);
        }

        // ─── A mark clicked ───────────────────────────────────────────────────

        /// <summary>The first group (in card order: rows, then kinds) whose elements hold the clicked one; its 1-based place in
        /// that group and the group's count. Null when no group has it.</summary>
        public static (int Index, int N, int M)? Locate(long id, IReadOnlyList<(MarkGroup Group, IReadOnlyList<long> Elements)> groups)
        {
            if (groups == null) return null;
            for (var i = 0; i < groups.Count; i++)
            {
                var els = groups[i].Elements ?? Array.Empty<long>();
                var at = IndexOf(els, id);
                if (at < 0) continue;
                return (i, at + 1, Math.Max(groups[i].Group?.Count ?? 0, els.Count));
            }
            return null;
        }

        private static int IndexOf(IReadOnlyList<long> list, long id)
        {
            for (var i = 0; i < list.Count; i++) if (list[i] == id) return i;
            return -1;
        }

        /// <summary>The short word for one element: "Tag" for "Door Tags", "Wall" for "Walls", "Element" when not known.</summary>
        public static string Word(string? category)
        {
            var c = HighlightSet.Clean(category);
            if (c == "Elements") return "Element";
            var last = MarkKey.Singular(c.Split(' ').Last());
            return last.Length == 0 ? "Element" : char.ToUpperInvariant(last[0]) + last.Substring(1).ToLowerInvariant();
        }

        /// <summary>The whole name for one element: "Door tag", "Lighting fixture", "Element".</summary>
        public static string Full(string? category)
        {
            var c = HighlightSet.Clean(category);
            if (c == "Elements") return "Element";
            var s = MarkKey.Lower(MarkKey.Singular(c));
            return s.Length == 0 ? "Element" : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>What the card and the label say about a clicked mark. <paramref name="tagText"/> is what the tag reads ("P3"),
        /// null when it is not a tag; <paramref name="row"/> is the group it was found in, null when none.</summary>
        public static MarkClick Click(string turnId, long id, long typeId, string? category, string? tagText, MarkGroup? row, int n, int m)
        {
            var c = new MarkClick { TurnId = turnId ?? "", Id = id, TypeId = typeId, Category = category ?? "", RowTitle = row?.Title?.Trim() ?? "", N = n, M = m };
            var word = Word(category);
            c.Line = row != null && m > 1 ? word + " " + HighlightSet.N(n) + " of " + HighlightSet.N(m) + " selected in Revit" : word + " selected in Revit";
            var name = Full(category) + (string.IsNullOrWhiteSpace(tagText) ? "" : " " + tagText!.Trim());
            c.Label = name;
            if (row != null && c.RowTitle.Length > 0) c.Label += " · " + LabelTitle(c.RowTitle);
            if (row != null && m > 1) c.Label += " · " + HighlightSet.N(n) + " of " + HighlightSet.N(m);
            return c;
        }

        /// <summary>The label drawn next to a clicked mark: its name bold, the row and place muted, under the mark.</summary>
        public static MarkLabel ClickLabel(MarkClick click)
        {
            var at = click.Label.IndexOf(" · ", StringComparison.Ordinal);
            return at < 0
                ? new MarkLabel { Bold = click.Label, Below = true }
                : new MarkLabel { Bold = click.Label.Substring(0, at), Rest = click.Label.Substring(at), Below = true };
        }

        /// <summary>Does a card row stand for the clicked element: its ids hold it, a type row holds its type, a category row
        /// names its category.</summary>
        public static bool RowHas(MarkGroup target, MarkClick click)
        {
            if (target == null || click == null) return false;
            if (target.Kind == MarkKind.TypeInstances) return click.TypeId >= 0 && target.Ids.Contains(click.TypeId);
            if (target.Ids.Count > 0) return target.Ids.Contains(click.Id);
            return target.Category.Length > 0 && click.Category.Length > 0
                   && string.Equals(HighlightSet.Clean(target.Category), HighlightSet.Clean(click.Category), StringComparison.Ordinal);
        }

        /// <summary>The card row a click scrolls to: the first that has the element and carries the title of the group it was
        /// found in; else the first that has it; -1 when none.</summary>
        public static int PickRow(IReadOnlyList<MarkGroup?> rows, MarkClick click)
        {
            if (rows == null || click == null) return -1;
            var first = -1;
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r == null || !RowHas(r, click)) continue;
                if (click.RowTitle.Length > 0 && string.Equals(r.Title.Trim(), click.RowTitle, StringComparison.Ordinal)) return i;
                if (first < 0) first = i;
            }
            return first;
        }
    }
}
