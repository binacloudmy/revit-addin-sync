#nullable enable
// Boards SheetGen, SheetsExcel and SmartTags under the result card: the rows of what the turn made or found (sheets with
// their number and scale; Excel rows with "row N"; views with their tag counts; tag types and tagged elements) under
// their group bands, the Excel columns the turn matched, and the one button each row's kind and ids allow.
//   band     Cp.Raised fill, hairline above, 12/600 muted: the group's name left, its count right (mono).
//   row      hairline above, padding 6 14, min height 40: number (mono 12) | name 12.5 | count (mono 12.5/600) | scale 11.5 faint.
//   table    the Excel list: Sheet | Name | Excel header band, rows 36 high with "row N" right-aligned.
//   fold     past 8 rows a "Show all N" line (accent text, 38 high); past 30 rows a plain "+ N more".
// The words and the button choice come from ResultItems / ItemText / ItemsBody; this only draws them.
// The row helpers are shared with CreatedCard and FindingsBody.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class ItemsCard
    {
        public static void Add(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var items = card.Items;
            if (items == null || items.IsEmpty) return;
            var ms = lang == ReceiptLanguage.Ms;
            var box = new StackPanel();
            var below = root.Children.Count > 0;           // something is above: the first band or row gets its hairline
            bool Top() => below || box.Children.Count > 0;

            if (items.Mapping.Count > 0)
            {
                box.Children.Add(Band(CardKit.Text(ms ? "Lajur digunakan" : "Columns used", 12, "Cp.Muted", FontWeights.SemiBold), Top(), new Thickness(14, 9, 8, 9)));
                var chips = new WrapPanel { Margin = new Thickness(14, 10, 8, 6) };
                foreach (var m in items.Mapping) chips.Children.Add(MappingChip(m));
                box.Children.Add(chips);
            }

            var buttons = new List<(ItemRow Row, Button Chip)>();
            var headers = ResultItems.ShowHeaders(items);
            foreach (var section in items.Sections)
            {
                if (headers) box.Children.Add(SectionBand(section, lang, Top()));
                var table = ItemsBody.IsExcelTable(section);
                if (table) box.Children.Add(TableHead(lang, Top()));
                var drawn = ItemsBody.DrawnRows(section.Rows.Count);
                var folded = ItemsBody.FoldedRows(section.Rows.Count);
                var behindFold = new List<FrameworkElement>();
                for (var i = 0; i < drawn; i++)
                {
                    var row = Row(section.Rows[i], lang, actions, buttons, table, Top());
                    if (i >= folded) { row.Visibility = Visibility.Collapsed; behindFold.Add(row); }
                    box.Children.Add(row);
                }
                if (behindFold.Count > 0) box.Children.Add(FoldToggle(section, behindFold, lang));
                if (section.Rows.Count > drawn) box.Children.Add(MoreRow(ItemsBody.MoreLine(section.Rows.Count - drawn, lang), true));
            }

            root.Children.Add(box);
            Classify(buttons, actions);
        }

        // ── Shared row pieces (CreatedCard, FindingsBody) ───────────────────────────────────────────────────────

        /// <summary>A row of a card body: its content, a hairline above it (not on the first row), padding, and a minimum height.</summary>
        internal static Border Line(UIElement child, bool top, Thickness padding, double minHeight = 0)
        {
            var b = new Border { BorderThickness = new Thickness(0, top ? 1 : 0, 0, 0), Padding = padding, MinHeight = minHeight, Child = child };
            b.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            return b;
        }

        /// <summary>A group band: Cp.Raised fill with a hairline above.</summary>
        internal static Border Band(UIElement child, bool top, Thickness padding)
        {
            var b = Line(child, top, padding);
            b.SetResourceReference(Border.BackgroundProperty, "Cp.Raised");
            return b;
        }

        /// <summary>A count: mono, right-aligned, never wrapped.</summary>
        internal static TextBlock Count(string text, double size, bool bold = true)
        {
            var t = CardKit.Text(text, size, "Cp.Ink", bold ? FontWeights.SemiBold : FontWeights.Normal, wrap: false, mono: true);
            t.HorizontalAlignment = HorizontalAlignment.Right;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.TextAlignment = TextAlignment.Right;
            return t;
        }

        /// <summary>The "+ N more" line under a capped list.</summary>
        internal static FrameworkElement MoreRow(string text, bool top)
        {
            var t = CardKit.Text(text, 12, "Cp.Faint", wrap: false);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            return Line(t, top, new Thickness(14, 9, 14, 9));
        }

        /// <summary>The action bar at the foot of a card body (the board's: Cp.PanelBg fill, hairline above, wrapping row of buttons).</summary>
        internal static WrapPanel ActionBar(StackPanel root)
        {
            var bar = new Border { Padding = new Thickness(12, 10, 12, 4), BorderThickness = new Thickness(0, 1, 0, 0) };
            bar.SetResourceReference(Border.BackgroundProperty, "Cp.PanelBg");
            bar.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            var wrap = new WrapPanel();
            bar.Child = wrap;
            root.Children.Add(bar);
            return wrap;
        }

        /// <summary>Change a button's words and keep its icon (a button with an icon holds a row of the icon and a TextBlock).</summary>
        internal static void SetLabel(Button button, string text)
        {
            if (button.Content is StackPanel row)
                foreach (var c in row.Children)
                    if (c is TextBlock tb) { tb.Text = text; return; }
            button.Content = text;
        }

        // ── Bands, table head, chips, fold ──────────────────────────────────────────────────────────────────────

        private static FrameworkElement SectionBand(ItemSection section, ReceiptLanguage lang, bool top)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = CardKit.Text(ItemsBody.SectionName(section, lang), 12, "Cp.Muted", FontWeights.SemiBold, margin: new Thickness(0, 0, 8, 0));
            var count = CardKit.Text(ItemsBody.SectionCount(section, lang), 12, "Cp.Muted", FontWeights.SemiBold, wrap: false, mono: true);
            count.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(count, 1);
            grid.Children.Add(name); grid.Children.Add(count);
            return Band(grid, top, new Thickness(14, 8, 14, 8));
        }

        /// <summary>Sheet | Name | Excel: 62 / star / 44 columns, 10 apart; the last one right-aligned.</summary>
        private static FrameworkElement TableHead(ReceiptLanguage lang, bool top)
        {
            var titles = ItemsBody.TableHeaders(lang);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            for (var c = 0; c < 3; c++)
            {
                var t = CardKit.Text(titles[c], 11.5, "Cp.Muted", FontWeights.SemiBold, wrap: false, margin: c < 2 ? new Thickness(0, 0, 10, 0) : new Thickness(0));
                if (c == 2) { t.HorizontalAlignment = HorizontalAlignment.Right; t.TextAlignment = TextAlignment.Right; }
                Grid.SetColumn(t, c);
                grid.Children.Add(t);
            }
            return Band(grid, top, new Thickness(14, 8, 14, 8));
        }

        /// <summary>"Sheet No → Sheet Number": h24, radius 6, Cp.PanelBg fill and hairline; the column muted, the arrow faint, the parameter mono.</summary>
        private static FrameworkElement MappingChip(MappingRow m)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var col = CardKit.Text(m.Col, 11.5, "Cp.Muted", wrap: false);
            var arrow = CardKit.Text("→", 11.5, "Cp.Faint", wrap: false, margin: new Thickness(5, 0, 5, 0));
            var param = CardKit.Text(m.Param, 11.5, "Cp.Ink", wrap: false, mono: true);
            foreach (var t in new[] { col, arrow, param }) { t.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(t); }
            var chip = new Border
            {
                Height = 24, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 6), Child = row,
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Cp.PanelBg");
            chip.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            return chip;
        }

        /// <summary>"Show all 24 sheets" / "Show fewer": accent text, a hairline above, 38 high.</summary>
        private static FrameworkElement FoldToggle(ItemSection section, List<FrameworkElement> behindFold, ReceiptLanguage lang)
        {
            var all = ItemsBody.ShowAllLabel(section, lang);
            var fewer = ItemsBody.ShowFewerLabel(lang);
            var open = false;
            Button? toggle = null;
            toggle = CardKit.Ghost(all, () =>
            {
                open = !open;
                foreach (var e in behindFold) e.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                toggle!.Content = open ? fewer : all;
            });
            toggle.SetResourceReference(Control.ForegroundProperty, "Cp.Accent");
            toggle.Margin = new Thickness(0, 4, 0, 4);
            toggle.HorizontalAlignment = HorizontalAlignment.Center;
            return Line(toggle, true, new Thickness(0), 38);
        }

        // ── Buttons: shown only once Revit vouches for the id ───────────────────────────────────────────────────

        /// <summary>The server only knows an id appeared in some tool result. Each button stays hidden until Revit says the id is what
        /// the row claims (a sheet, a view, an element that exists in the open model); a button Revit cannot vouch for never shows.</summary>
        private static async void Classify(List<(ItemRow Row, Button Chip)> buttons, TurnEvidenceActions actions)
        {
            try
            {
                if (buttons.Count == 0) return;
                if (actions.ClassifyIds == null) { foreach (var b in buttons) b.Chip.Visibility = Visibility.Visible; return; }
                var ids = buttons.SelectMany(b => ResultItems.SampleIds(b.Row)).Distinct().Take(ResultItems.MaxClassified).ToList();
                var map = await actions.ClassifyIds(ids);
                if (map == null) return;
                foreach (var (row, chip) in buttons)
                    chip.Visibility = ResultItems.ShowButton(row, map) ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the dispatcher; the buttons stay hidden.
                System.Diagnostics.Debug.WriteLine("[BINA] item classify: " + ex.Message);
            }
        }

        // ── One row ─────────────────────────────────────────────────────────────────────────────────────────────

        private static FrameworkElement Row(ItemRow row, ReceiptLanguage lang, TurnEvidenceActions actions, List<(ItemRow Row, Button Chip)> buttons, bool table, bool top)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var parts = ItemsBody.Parts(row, lang);
            var hasLead = parts.Lead.Length > 0;
            var button = row.Button;
            // The Excel list keeps its "row N" column under the table head: its button goes under the name instead of beside it.
            var beside = !table && button != ItemButton.None;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(hasLead ? 72 : 0) });   // number 62 + gap 10
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // count
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // scale / Excel row
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // button
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (hasLead)
            {
                var lead = CardKit.Text(parts.Lead, 12, "Cp.Ink", wrap: false, mono: true);
                lead.VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(lead);
            }

            var text = new StackPanel { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(CardKit.Text(parts.Title, 12.5, "Cp.Ink", row.Count != null ? FontWeights.Medium : FontWeights.Normal));
            if (parts.Detail.Length > 0)
                text.Children.Add(CardKit.Text(parts.Detail, 11.5, row.Kind == BinaVibe.Copilot.Contracts.ResultItemKinds.Row ? "Cp.AmberFg" : "Cp.Muted", margin: new Thickness(0, 1, 0, 0)));
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (row.Count != null)
            {
                var n = Count(row.Count.Value.ToString(CultureInfo.InvariantCulture), 12.5);
                if (beside || parts.Trail.Length > 0) n.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetColumn(n, 2);
                grid.Children.Add(n);
            }

            if (parts.Trail.Length > 0)
            {
                var trail = CardKit.Text(parts.Trail, 11.5, "Cp.Faint", wrap: false);
                trail.HorizontalAlignment = HorizontalAlignment.Right;
                trail.VerticalAlignment = VerticalAlignment.Center;
                trail.TextAlignment = TextAlignment.Right;
                if (table) trail.MinWidth = 44;
                if (beside) trail.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetColumn(trail, 3);
                grid.Children.Add(trail);
            }

            if (button != ItemButton.None)
            {
                var ids = row.Ids.ToArray();
                Button? chip = null;
                var label = ItemText.ButtonLabel(button, lang);
                chip = CardKit.Secondary(label, async () =>
                {
                    try
                    {
                        if (button == ItemButton.Select) { actions.ShowElements?.Invoke(ids); return; }
                        if (actions.OpenView == null) return;
                        chip!.IsEnabled = false;
                        var why = await actions.OpenView(ids[0], button == ItemButton.OpenSheet);
                        chip.Content = why == null ? label : (ms ? "Tidak dapat dibuka: " : "Could not open: ") + (why.Length > 50 ? why.Substring(0, 49) + "…" : why);
                        chip.IsEnabled = true;
                    }
                    catch (Exception ex)
                    {
                        // async void: nothing may escape into the dispatcher.
                        System.Diagnostics.Debug.WriteLine("[BINA] item button: " + ex.Message);
                        try { chip!.IsEnabled = true; } catch { /* the card is gone */ }
                    }
                }, compact: true);
                chip.Margin = beside ? new Thickness(0) : new Thickness(0, 4, 0, 0);
                chip.VerticalAlignment = VerticalAlignment.Center;
                chip.Visibility = Visibility.Collapsed;   // shown once Revit says the id is what the row says
                buttons.Add((row, chip));
                if (beside) Grid.SetColumn(chip, 4);
                else
                {
                    chip.HorizontalAlignment = HorizontalAlignment.Left;
                    Grid.SetRow(chip, 1); Grid.SetColumn(chip, 1); Grid.SetColumnSpan(chip, 4);
                }
                grid.Children.Add(chip);
            }

            return Line(grid, top, table ? new Thickness(14, 5, 14, 5) : new Thickness(14, 6, 14, 6), table ? 36 : row.Count != null ? 42 : 40);
        }
    }
}
