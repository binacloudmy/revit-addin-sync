#nullable enable
// One row of a grouped read answer: the type's code in mono with its description muted (wrapped, never run on), the count right-aligned,
// a share bar under the name (the row's share of the section), and a ghost Select only when the row has ids and Revit vouches for them.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class TallyRows
    {
        public static FrameworkElement Row(ItemRow row, int total, ReceiptLanguage lang, TurnEvidenceActions actions,
            List<(ItemRow Row, Button Chip)> buttons, bool top)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // count
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // button
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var (code, description) = TallyText.SplitTitle(row.Title);
            var name = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, LineHeight = 18 };
            if (code.Length > 0)
            {
                var c = new Run(code) { FontSize = 12 };
                c.SetResourceReference(TextElement.ForegroundProperty, "Cp.Ink");
                c.SetResourceReference(TextElement.FontFamilyProperty, "Cp.FontMono");
                name.Inlines.Add(c);
                if (description.Length > 0)
                {
                    var d = new Run("  " + description) { FontSize = 12.5 };
                    d.SetResourceReference(TextElement.ForegroundProperty, "Cp.Muted");
                    name.Inlines.Add(d);
                }
            }
            else
            {
                var d = new Run(description) { FontSize = 12.5 };
                d.SetResourceReference(TextElement.ForegroundProperty, "Cp.Ink");
                name.Inlines.Add(d);
            }
            name.ToolTip = row.Title;
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var count = ItemsCard.Count((row.Count ?? 0).ToString("N0", CultureInfo.InvariantCulture), 12.5);
            count.VerticalAlignment = VerticalAlignment.Top;
            count.Margin = new Thickness(0, 1, 0, 0);
            Grid.SetColumn(count, 1);
            grid.Children.Add(count);

            // the share bar: the row's part of the whole, under the name
            var share = TallyText.Share(row.Count ?? 0, total);
            var bar = new Grid { Height = 3, Margin = new Thickness(0, 6, 12, 0), ToolTip = TallyText.SharePercent(row.Count ?? 0, total) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.0001), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - share, 0.0001), GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(1.5) };
            track.SetResourceReference(Border.BackgroundProperty, "Cp.Raised");
            Grid.SetColumnSpan(track, 2);
            bar.Children.Add(track);
            var fill = new Border { CornerRadius = new CornerRadius(1.5) };
            fill.SetResourceReference(Border.BackgroundProperty, "Cp.Run.Info");
            bar.Children.Add(fill);
            Grid.SetRow(bar, 1);
            grid.Children.Add(bar);

            if (row.Ids.Count > 0)
            {
                var ids = row.Ids.ToArray();
                var select = CardKit.Ghost(TallyText.SelectLabel(lang), () => actions.ShowElements?.Invoke(ids), CardKit.Icons.Select, compact: true);
                select.Margin = new Thickness(0);
                select.VerticalAlignment = VerticalAlignment.Top;
                select.Visibility = Visibility.Collapsed;   // shown once Revit says these ids are elements of the open model
                // Revit vouches for the ids as for any element row (ClassifyIds), so the row is judged as an element row.
                buttons.Add((new ItemRow { Kind = BinaVibe.Copilot.Contracts.ResultItemKinds.Element, Title = row.Title, Count = row.Count, Ids = row.Ids }, select));
                Grid.SetColumn(select, 2);
                grid.Children.Add(select);
            }

            return ItemsCard.Line(grid, top, new Thickness(14, 9, 10, 9), 44);
        }
    }
}
