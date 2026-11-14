#nullable enable
// Body of the "Findings" result card, drawn as the boards' list rows: a hairline above each, padding 6 14, 40 high;
// a severity dot (red for Revit's error severity, amber for the rest), the title (12.5) with its severity word under
// it (11.5 faint), the count of elements (mono 12.5/600, right-aligned; a mono chip for repeated warnings folded into one
// finding). One way in (result marks design, rule 7): a row with elements is the button that shows them in the model,
// its small arrow selects them in Revit; a row with only a handle selects the handle's elements when clicked. Past 12
// rows a "+ N more" line. "Hide marks" / "Show in model" is the card's section header (ResultCardView.MarksHeader).

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class FindingsBody
    {
        public static void Build(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var box = new StackPanel();
            var below = root.Children.Count > 0;
            // The failing rules of a rule-set result are drawn by the "Rules checked" block (RulesBlock), not here.
            var findings = card.Findings.Where(f => !(f.FromRule && actions.RulesBlock != null)).ToList();
            var drawn = Math.Min(findings.Count, ItemsBody.MaxListRows);
            for (var i = 0; i < drawn; i++) box.Children.Add(Row(findings[i], lang, actions, below || i > 0));
            if (findings.Count > drawn) box.Children.Add(ItemsCard.MoreRow(ItemsBody.MoreLine(findings.Count - drawn, lang), true));
            if (box.Children.Count > 0) root.Children.Add(box);
        }

        private static FrameworkElement Row(Finding f, ReceiptLanguage lang, TurnEvidenceActions actions, bool top)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });                    // dot 8 + gap 10
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // count

            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, ItemsBody.FindingDotToken(f));
            grid.Children.Add(dot);

            var text = new StackPanel { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(CardKit.Text(f.Title, 12.5, "Cp.Ink"));
            if (!string.IsNullOrEmpty(f.Severity)) text.Children.Add(CardKit.Text(f.Severity, 11.5, "Cp.Faint", margin: new Thickness(0, 1, 0, 0)));
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            // Warnings that repeated one message arrive folded into this one finding (Repeats > 1): the message once and a count chip
            // ("x20", or "20 elements" when they carried ids); its one Show button below selects the union of the ids.
            var repeat = ItemsBody.FindingRepeatLabel(f, lang);
            var count = ItemsBody.FindingCount(f);
            if (repeat.Length > 0)
            {
                var chip = RepeatRows.CountChip(repeat);
                chip.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetColumn(chip, 2);
                grid.Children.Add(chip);
            }
            else if (count.Length > 0)
            {
                var n = ItemsCard.Count(count, 12.5);
                n.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetColumn(n, 2);
                grid.Children.Add(n);
            }

            var line = ItemsCard.Line(grid, top, new Thickness(14, 6, 14, 6), 40);
            if (f.Ids.Count > 0) actions.Rows?.Link(line, Highlights.MarkFocus.ForElements(f.Title, f.Ids));
            else if (f.Handle != null && actions.Highlight != null)
            {
                var h = f.Handle;
                actions.Rows?.Button(line, () => actions.Highlight(h), CardButtons.ArrowTip);
            }
            return line;
        }
    }
}
