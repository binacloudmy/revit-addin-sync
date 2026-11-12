#nullable enable
// The rows of a result card that fold a repeated message into one (RepeatFold decides which; this only draws them):
//   the message once (12.5), a mono count chip ("x20", or "20 elements" when the rows carried ids) and, when they carried
//   ids, ONE Show button (h30) that selects the union of the ids. Row look as the other list rows: hairline above, 6 14, 40 high.
// The chip is also used by the Findings list for warnings the model folded.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class RepeatRows
    {
        /// <summary>The most member titles the folded row's tooltip lists.</summary>
        private const int TooltipTitles = 12;

        /// <summary>A count chip: mono 11.5/600, Cp.Sunken fill, hairline border, radius 6.</summary>
        internal static Border CountChip(string text)
        {
            var label = CardKit.Text(text, 11.5, "Cp.Ink", FontWeights.SemiBold, wrap: false, mono: true);
            label.VerticalAlignment = VerticalAlignment.Center;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Child = label,
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            chip.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            return chip;
        }

        /// <summary>One row for several not-done rows that say the same thing. Its Show button stays hidden until Revit vouches for
        /// the ids, like the other item buttons: it is added to <paramref name="buttons"/> as a row of kind element holding the union.</summary>
        internal static FrameworkElement Item(RepeatGroup<ItemRow> g, ReceiptLanguage lang, TurnEvidenceActions actions, List<(ItemRow Row, Button Chip)> buttons, bool top)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var message = ItemsBody.FoldMessage(g.First, lang);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // count chip
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // Show

            var text = CardKit.Text(message, 12.5, "Cp.AmberFg", margin: new Thickness(0, 0, 10, 0));
            text.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(text);

            var chip = CountChip(RepeatFold.CountLabel(g, lang));
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);

            if (g.Ids.Count > 0 && actions.ShowElements != null)
            {
                var ids = g.Ids.ToArray();
                var show = CardKit.Secondary(ms ? "Tunjuk" : "Show", () => actions.ShowElements(ids), compact: true);
                show.Margin = new Thickness(10, 0, 0, 0);
                show.VerticalAlignment = VerticalAlignment.Center;
                show.Visibility = Visibility.Collapsed;   // shown once Revit says the ids exist in the open model
                buttons.Add((new ItemRow { Kind = BinaVibe.Copilot.Contracts.ResultItemKinds.Element, Title = message, Ids = g.Ids }, show));
                Grid.SetColumn(show, 2);
                grid.Children.Add(show);
            }

            var line = ItemsCard.Line(grid, top, new Thickness(14, 6, 14, 6), 40);
            var titles = g.Members.Select(r => r.Title).Where(t => t.Length > 0).Distinct().ToList();
            if (titles.Count > 0)
                line.ToolTip = string.Join("\n", titles.Take(TooltipTitles)) + (titles.Count > TooltipTitles ? "\n" + ItemsBody.MoreLine(titles.Count - TooltipTitles, lang) : "");
            return line;
        }
    }
}
