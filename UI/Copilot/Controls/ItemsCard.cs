#nullable enable
// Boards 8-10 under the result card: the rows of what the turn made or found (sheets with their number, scale
// and fill; Excel rows with "row N"; views with their tag counts; tag types and tagged elements) under their
// sections, the Excel columns the turn matched, and the one button each row's kind and ids allow. The words
// and the button choice come from ResultItems / ItemText; this only draws them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class ItemsCard
    {
        private const int MaxRowsPerSection = 30;

        public static void Add(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var items = card.Items;
            if (items == null || items.IsEmpty) return;
            var ms = lang == ReceiptLanguage.Ms;
            var box = new StackPanel { Margin = new Thickness(12, 8, 12, 4) };

            if (items.Mapping.Count > 0)
            {
                box.Children.Add(CardKit.Text(ms ? "Lajur digunakan" : "Columns used", 10.5, "Cp.Faint", FontWeights.Medium, margin: new Thickness(0, 0, 0, 3)));
                var pills = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                foreach (var m in items.Mapping)
                {
                    var pill = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 6, 4), Child = CardKit.Text(ItemText.MappingLine(m), 11, "Cp.Ink2", mono: true, wrap: false) };
                    pill.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
                    pills.Children.Add(pill);
                }
                box.Children.Add(pills);
            }

            var buttons = new List<(ItemRow Row, Button Chip)>();
            var headers = ResultItems.ShowHeaders(items);
            foreach (var section in items.Sections)
            {
                if (headers) box.Children.Add(CardKit.Text(ItemText.SectionHeader(section, lang), 11, "Cp.Ink", FontWeights.SemiBold, margin: new Thickness(0, 4, 0, 2)));
                var shown = 0;
                foreach (var row in section.Rows)
                {
                    if (shown++ >= MaxRowsPerSection) break;
                    box.Children.Add(Row(row, lang, actions, buttons));
                }
                if (section.Rows.Count > MaxRowsPerSection)
                    box.Children.Add(CardKit.Text("+ " + (section.Rows.Count - MaxRowsPerSection) + (ms ? " lagi" : " more"), 11, "Cp.Faint", margin: new Thickness(0, 0, 0, 4)));
            }

            var strip = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = box };
            strip.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            root.Children.Add(strip);
            Classify(buttons, actions);
        }

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

        private static FrameworkElement Row(ItemRow row, ReceiptLanguage lang, TurnEvidenceActions actions, List<(ItemRow Row, Button Chip)> buttons)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Margin = new Thickness(0, 2, 8, 2) };
            text.Children.Add(CardKit.Text(row.Title, 11.5, "Cp.Ink"));
            var facts = ItemText.FactsLine(row, lang);
            if (facts.Length > 0) text.Children.Add(CardKit.Text(facts, 10.5, "Cp.Muted"));
            Grid.SetColumn(text, 0); grid.Children.Add(text);

            if (row.Count != null)
            {
                var n = CardKit.Text(row.Count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), 11.5, "Cp.Ink", wrap: false, mono: true, margin: new Thickness(0, 3, 8, 3));
                Grid.SetColumn(n, 1); grid.Children.Add(n);
            }

            var button = row.Button;
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
                });
                chip.Margin = new Thickness(0, 0, 0, 0);
                chip.VerticalAlignment = VerticalAlignment.Center;
                chip.Visibility = Visibility.Collapsed;   // shown once Revit says the id is what the row says
                buttons.Add((row, chip));
                Grid.SetColumn(chip, 2); grid.Children.Add(chip);
            }
            return grid;
        }
    }
}
