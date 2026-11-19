#nullable enable
// One row of a grouped read answer: the type's code in mono with its description muted (wrapped, never run on), the count right-aligned,
// a share bar under the name (the row's share of the section); a row with ids links to its marks in the model.
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
        public static FrameworkElement Row(ItemRow row, int total, ReceiptLanguage lang, TurnEvidenceActions actions, bool top)
        {
            var grid = new Grid();
            // r13 (0.0.150): the name keeps a minimum width; the count / facts column takes what is left (TallyText.FactsUnder).
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = TallyText.MinTitleWidth });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // count
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // name
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // facts, when the row is narrow
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // share bar

            var (code, description) = TallyText.SplitTitle(row.Title);
            var name = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, LineHeight = 18 };
            if (code.Length > 0)
            {
                var c = new Run(TallyText.Breakable(code)) { FontSize = 12 };   // breaks after "_", ")" and "-", never per character
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

            // The row's facts as a second line under the name ("Type Mark FD1 · 1 HOUR"), shown only when they do not fit on the right.
            var factsLine = TallyText.FactsLine(row, lang);
            var facts = CardKit.Text(factsLine, 11.5, "Cp.Muted", margin: new Thickness(0, 2, 12, 0));
            facts.ToolTip = factsLine;
            Grid.SetRow(facts, 1);
            grid.Children.Add(facts);

            var count = ItemsCard.Count(TallyText.RightText(row, lang), 12.5);
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
            Grid.SetRow(bar, 2);
            grid.Children.Add(bar);

            if (!TallyText.ShowsShareBar(row)) bar.Visibility = Visibility.Collapsed;
            var line = ItemsCard.Line(grid, top, new Thickness(14, 9, 10, 9), 44);

            // Wide or narrow: the right column says the facts and the count, or the count alone with the facts under the name.
            var wide = TallyText.RightText(row, lang);
            var narrow = TallyText.CountOnly(row);
            void Fit(double width)
            {
                var under = TallyText.FactsUnder(width, row, lang);
                facts.Visibility = under ? Visibility.Visible : Visibility.Collapsed;
                count.Text = under ? narrow : wide;
                count.Visibility = count.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            Fit(0);   // before the first layout: the pane's usual narrow width
            line.SizeChanged += (_, e) => { if (e.WidthChanged) Fit(e.NewSize.Width); };   // only the width decides: no loop on the height
            // A row with ids is a mark group like any element row: a click focuses its marks in the model (the in-model result view).
            if (row.Button == ItemButton.Select) actions.Rows?.Link(line, Highlights.MarkFocus.ForItemRow(row));
            return line;
        }
    }
}
