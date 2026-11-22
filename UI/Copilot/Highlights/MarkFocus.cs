#nullable enable
// Rows and marks point at each other (result marks design, rule 7). Every decision that needs no Revit:
//   focus    a card row's group: its marks as usual, the others dim (Revit's selection shows the focused ones).
//   click    a mark clicked in the model: the row it belongs to and its "3 of 8", and the line the card row flashes
//            ("Tag 3 of 8 selected in Revit").
//   targets  what a card row shows: an item row, a type group, a created category, a finding's elements.
// InModelResult does the Revit side (zoom, selection, opening a view, the images); the cards only pass a target.
// Revit-free and WPF-free. Every word is English (rule 11).

using System;
using System.Collections.Generic;
using System.Linq;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Highlights
{
    /// <summary>How a mark is drawn while a group is focused: as usual (in the group, or no focus) or dim (the others). There
    /// is no bright tone: Revit's own selection shows the focused ones.</summary>
    public enum MarkTone { Normal, Dim }

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
    }

    public static class MarkFocus
    {
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

        // ─── Focus: as usual and dim ──────────────────────────────────────────

        /// <summary>No focus: every mark as usual; with one, the group's marks as usual and the others dim.</summary>
        public static MarkTone ToneOf(long id, ICollection<long>? focused) =>
            focused == null || focused.Contains(id) ? MarkTone.Normal : MarkTone.Dim;

        /// <summary>The part of a mark image's file name that names its tone ("" for the usual one).</summary>
        public static string ToneSuffix(MarkTone t) => t == MarkTone.Dim ? "-dim" : "";

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

        /// <summary>What the card says about a clicked mark. <paramref name="row"/> is the group it was found in, null when none.</summary>
        public static MarkClick Click(string turnId, long id, long typeId, string? category, MarkGroup? row, int n, int m)
        {
            var c = new MarkClick { TurnId = turnId ?? "", Id = id, TypeId = typeId, Category = category ?? "", RowTitle = row?.Title?.Trim() ?? "", N = n, M = m };
            var word = Word(category);
            c.Line = row != null && m > 1 ? word + " " + HighlightSet.N(n) + " of " + HighlightSet.N(m) + " selected in Revit" : word + " selected in Revit";
            return c;
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
