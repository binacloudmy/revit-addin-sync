#nullable enable
// Boards 8-10: the rows of what a turn made or found (turn_result.items) and the Excel columns it matched
// (turn_result.mapping), ready to draw: grouped under their section, each row with its own title, count,
// short facts and the buttons its kind and ids allow. The server keeps an item only when its numbers are
// backed by a tool result and an id only when a tool result showed it, so the pane shows what it is given.
// Pure, so a test pins it against the contract's example JSON.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BinaVibe.Copilot.Contracts;

namespace RevitWebAppSync.UI.Copilot.Model
{
    /// <summary>What a row's button does.</summary>
    public enum ItemButton
    {
        None,
        /// <summary>Make the sheet the active view in Revit (kind sheet, first id).</summary>
        OpenSheet,
        /// <summary>Make the view the active view in Revit (kind view, first id).</summary>
        OpenView,
        /// <summary>Select the ids in Revit and zoom to them (kind tag: the tags; kind element: the tagged elements).</summary>
        Select,
    }

    public sealed class ItemRow
    {
        public string Kind = "";
        public string Title = "";
        public int? Count;
        public List<long> Ids = new List<long>();
        /// <summary>The short facts, in the order they are shown.</summary>
        public List<(string Key, string Value)> Cells = new List<(string, string)>();

        public string Cell(string key) => Cells.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Value ?? "";

        /// <summary>The button this row gets: from its kind, and only when it has ids to act on.</summary>
        public ItemButton Button =>
            Ids.Count == 0 ? ItemButton.None
            : Kind == ResultItemKinds.Sheet ? ItemButton.OpenSheet
            : Kind == ResultItemKinds.View ? ItemButton.OpenView
            : Kind == ResultItemKinds.Tag || Kind == ResultItemKinds.Element ? ItemButton.Select
            : ItemButton.None;
    }

    public sealed class ItemSection
    {
        /// <summary>The group's name; "" for items that have none.</summary>
        public string Name = "";
        public List<ItemRow> Rows = new List<ItemRow>();
        /// <summary>Sum of the rows' counts when every row has one, else the number of rows.</summary>
        public int Total => Rows.All(r => r.Count != null) ? Rows.Sum(r => r.Count!.Value) : Rows.Count;
    }

    public sealed class MappingRow
    {
        public string Col = "";
        public string Param = "";
    }

    public sealed class ResultItems
    {
        public List<ItemSection> Sections = new List<ItemSection>();
        public List<MappingRow> Mapping = new List<MappingRow>();
        public bool IsEmpty => Sections.Count == 0 && Mapping.Count == 0;
        public int ItemCount => Sections.Sum(s => s.Rows.Count);

        /// <summary>Null when the result carries no items and no mapping.</summary>
        public static ResultItems? From(TurnResultDto? result)
        {
            if (result == null) return null;
            var list = new ResultItems();
            foreach (var it in result.Items ?? new List<ResultItemDto>())
            {
                if (it == null || string.IsNullOrWhiteSpace(it.Title)) continue;
                var row = new ItemRow { Kind = it.Kind ?? "", Title = it.Title.Trim(), Count = it.Count is >= 0 ? it.Count : null };
                foreach (var id in it.Ids ?? new List<JsonElement>())
                    if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n)) row.Ids.Add(n);
                // Facts: the ones the boards name come first, in that order; any others follow as given.
                var cells = it.Cells ?? new Dictionary<string, string>();
                foreach (var key in KnownCellOrder)
                    foreach (var kv in cells.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)))
                        row.Cells.Add((kv.Key, kv.Value));
                foreach (var kv in cells)
                    if (!row.Cells.Any(c => c.Key == kv.Key)) row.Cells.Add((kv.Key, kv.Value));
                var name = string.IsNullOrWhiteSpace(it.Group) ? "" : it.Group!.Trim();
                var section = list.Sections.FirstOrDefault(s => s.Name == name);
                if (section == null) list.Sections.Add(section = new ItemSection { Name = name });
                section.Rows.Add(row);
            }
            foreach (var m in result.Mapping ?? new List<ResultMappingDto>())
                if (m != null && !string.IsNullOrWhiteSpace(m.Col) && !string.IsNullOrWhiteSpace(m.Param))
                    list.Mapping.Add(new MappingRow { Col = m.Col.Trim(), Param = m.Param.Trim() });
            return list.IsEmpty ? null : list;
        }

        private static readonly string[] KnownCellOrder = { "number", "scale", "fill", "row", "state" };
    }

    /// <summary>The words of the rows, in the pane's language.</summary>
    public static class ItemText
    {
        /// <summary>One fact as it reads in a row: "fills 72%", "row 7", the scale and everything else as given.</summary>
        public static string Fact(string key, string value, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            switch ((key ?? "").ToLowerInvariant())
            {
                case "fill": return (ms ? "memenuhi " : "fills ") + value;
                case "row": return (ms ? "baris " : "row ") + value;
                default: return value;
            }
        }

        /// <summary>The row's facts as one line, "EL-101 · 1:100 · fills 72%", without the number when the title already shows it.</summary>
        public static string FactsLine(ItemRow row, ReceiptLanguage lang, bool withNumber = true) =>
            string.Join(" · ", row.Cells.Where(c => withNumber || !string.Equals(c.Key, "number", StringComparison.OrdinalIgnoreCase))
                                         .Select(c => Fact(c.Key, c.Value, lang)));

        public static string ButtonLabel(ItemButton b, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            switch (b)
            {
                case ItemButton.OpenSheet: return ms ? "Buka sheet dalam Revit" : "Open sheet in Revit";
                case ItemButton.OpenView: return ms ? "Buka view" : "Open view";
                case ItemButton.Select: return ms ? "Pilih dalam Revit" : "Select them in Revit";
                default: return "";
            }
        }

        /// <summary>The section header: "Lighting plans · 3", the count being the rows' total.</summary>
        public static string SectionTitle(ItemSection s) =>
            (s.Name.Length == 0 ? "" : s.Name + " · ") + s.Total.ToString(CultureInfo.InvariantCulture);

        /// <summary>What a kind of row is called, in plural, for a section that has no group name.</summary>
        public static string KindLabel(string? kind, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            switch (kind)
            {
                case ResultItemKinds.Sheet: return ms ? "Sheet" : "Sheets";
                case ResultItemKinds.View: return ms ? "View" : "Views";
                case ResultItemKinds.Tag: return ms ? "Tag" : "Tags";
                case ResultItemKinds.Row: return ms ? "Baris" : "Rows";
                case ResultItemKinds.Element: return ms ? "Elemen" : "Elements";
                default: return ms ? "Senarai" : "Items";
            }
        }

        /// <summary>The header of a section: its group's name, else what its rows are ("Views · 82").</summary>
        public static string SectionHeader(ItemSection s, ReceiptLanguage lang) =>
            (s.Name.Length > 0 ? s.Name : KindLabel(s.Rows.FirstOrDefault()?.Kind, lang)) + " · " + s.Total.ToString(CultureInfo.InvariantCulture);

        /// <summary>"Sheet No → Sheet Number".</summary>
        public static string MappingLine(MappingRow m) => m.Col + " → " + m.Param;
    }
}
