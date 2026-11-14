#nullable enable
// Body of the "Created" result card. When the commits carried counts by category (boards 8-10): one row per category
// (name 12.5 left, count mono 12.5/600 right, hairline above, 40 high), a "+ N more" line past 12, the source line as the
// board's footnote (Cp.Sunken fill, info icon, 12 muted), and Copy list.
// Without counts (BuildElements): one row per element the commit added.
// One way in (result marks design, rule 7): each row is the button that shows its elements in the model, its small arrow
// selects them in Revit; "Hide marks" / "Show in model" is the card's section header (ResultCardView.MarksHeader).
// Undo is on the card's headline row (ResultCardView). Per-sheet and per-view rows come from the server's items (ItemsCard).

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class CreatedCard
    {
        /// <summary>Adds the body and the action row; false (nothing added) when the card has no category counts.</summary>
        public static bool TryBuild(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var by = card.CreatedBy;
            if (by == null || by.Groups.Count == 0) return false;
            var ms = lang == ReceiptLanguage.Ms;

            var box = new StackPanel();
            var below = root.Children.Count > 0;
            var drawn = Math.Min(by.Groups.Count, ItemsBody.MaxListRows);
            for (var i = 0; i < drawn; i++)
            {
                var g = by.Groups[i];
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = CardKit.Text(CreatedSummary.DisplayName(g.Category, lang), 12.5, "Cp.Ink", margin: new Thickness(0, 0, 10, 0));
                name.VerticalAlignment = VerticalAlignment.Center;
                var n = ItemsCard.Count(g.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), 12.5);
                Grid.SetColumn(n, 1);
                grid.Children.Add(name); grid.Children.Add(n);
                var line = ItemsCard.Line(grid, below || i > 0, new Thickness(14, 6, 14, 6), 40);
                actions.Rows?.Link(line, Highlights.MarkFocus.ForCategory(g.Category, g.Count));
                box.Children.Add(line);
            }
            if (by.Groups.Count > drawn) box.Children.Add(ItemsCard.MoreRow(ItemsBody.MoreLine(by.Groups.Count - drawn, lang), true));
            // What Revit made along (viewports of a sheet, sketch planes, ...): one quiet line, never rows (live R7).
            if (by.SupportingLine(lang) is string supporting)
                box.Children.Add(CardKit.Text(supporting, 11.5, "Cp.Faint", margin: new Thickness(14, 2, 14, 8)));
            box.Children.Add(Footnote(by.SourceLine(lang)));
            root.Children.Add(box);

            ResultCardView.CommitWarnings(root, card, lang, actions);
            var acts = ItemsCard.ActionBar(root);
            Button? copy = null;
            copy = CardKit.Secondary(ms ? "Salin senarai" : "Copy list", async () =>
            {
                var ok = await ClipboardKit.CopyAsync(() => by.CopyText(), "copy-list", card.Created.Count);
                if (ok && copy != null) ItemsCard.SetLabel(copy, ms ? "Disalin" : "Copied");
            }, CardKit.Icons.Copy);
            acts.Children.Add(copy);
            return true;
        }

        /// <summary>A card with created elements but no category counts (the server's rows and checks carry the rest): one row per
        /// element, the row the button that shows it in the model. Nothing is added when no element was created.</summary>
        public static void BuildElements(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            if (card.Created.Count == 0) return;
            var box = new StackPanel();
            var below = root.Children.Count > 0;
            var drawn = Math.Min(card.Created.Count, ItemsBody.MaxListRows);
            for (var i = 0; i < drawn; i++)
            {
                var c = card.Created[i];
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = CardKit.Text(c.Label, 12.5, "Cp.Ink", margin: new Thickness(0, 0, 10, 0));
                label.VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(label);
                var line = ItemsCard.Line(grid, below || i > 0, new Thickness(14, 6, 14, 6), 40);
                actions.Rows?.Link(line, Highlights.MarkFocus.ForElements(c.Label, new[] { c.Id }));
                box.Children.Add(line);
            }
            if (card.Created.Count > drawn) box.Children.Add(ItemsCard.MoreRow(ItemsBody.MoreLine(card.Created.Count - drawn, lang), true));
            root.Children.Add(box);

            ResultCardView.CommitWarnings(root, card, lang, actions);
        }

        /// <summary>The board's footnote row: Cp.Sunken fill, hairline above, a 16px info icon and 12px muted text.</summary>
        private static FrameworkElement Footnote(string text)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });   // icon 16 + gap 8
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 Z M8,7.3 V11.3 M8,4.9 V5.1"),
                Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
            };
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Cp.Muted");
            var label = CardKit.Text(text, 12, "Cp.Muted");
            Grid.SetColumn(label, 1);
            grid.Children.Add(icon); grid.Children.Add(label);
            var row = ItemsCard.Line(grid, true, new Thickness(14, 10, 14, 10));
            row.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            return row;
        }
    }
}
