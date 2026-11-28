#nullable enable
// The panel behind "Steps" on an answer that has no result card: what ran (each finished step with the 18px status ring of the running
// card, its label with the code part in mono, its count at the right), then ONE muted line for the attempts that failed ("2 earlier
// attempts failed, then it worked"), then what was read and searched (the evidence lines that are not failures).
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class StepsListView
    {
        public static FrameworkElement? Build(IEnumerable<ProgressStep>? steps, int failedEvidence, IEnumerable<string> evidenceLines, ReceiptLanguage lang, IReadOnlyList<string>? serverNotes = null)
        {
            var rows = StepsSummary.WorkedRows(steps);
            var failed = StepsSummary.Failed(steps, failedEvidence);
            var lines = evidenceLines.Where(l => l.Length > 0).ToList();
            var notes = (serverNotes ?? new string[0]).Where(n => n.Length > 0).ToList();
            if (rows.Count == 0 && failed == 0 && lines.Count == 0 && notes.Count == 0) return null;

            var col = new StackPanel();
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var grid = new Grid { Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = RunningCardView.Icon(r.State);
                icon.VerticalAlignment = VerticalAlignment.Top;
                grid.Children.Add(icon);

                var label = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
                label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink2");
                foreach (var (text, mono) in RunningCard.LabelParts(r.Text))
                {
                    var run = new System.Windows.Documents.Run(text);
                    if (mono) { run.FontSize = 12; run.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "Cp.FontMono"); }
                    label.Inlines.Add(run);
                }
                Grid.SetColumn(label, 2);
                grid.Children.Add(label);

                if (r.Outcome.Length > 0)
                {
                    var outcome = CardKit.Text(r.Outcome, 11, "Cp.Faint", wrap: false, mono: true, margin: new Thickness(10, 2, 0, 0));
                    Grid.SetColumn(outcome, 3);
                    grid.Children.Add(outcome);
                }
                col.Children.Add(grid);
            }

            // The server's own line about the failed attempts (exact causes) replaces the one the pane would compute.
            var failedLines = notes.Count > 0 ? notes : new List<string> { StepsSummary.FailedLine(failed, rows.Count > 0, lang) };
            foreach (var failedLine in failedLines.Where(l => l.Length > 0))
                col.Children.Add(CardKit.Text(failedLine, 12, "Cp.Faint", margin: new Thickness(0, rows.Count > 0 ? 10 : 0, 0, 0)));
            foreach (var l in lines)
                col.Children.Add(CardKit.Text(l, 11.5, "Cp.Muted", margin: new Thickness(0, 6, 0, 0)));

            var card = CardKit.Card("Cp.Line", "Cp.Bg", new Thickness(0, 6, 0, 4), 12);
            card.Padding = new Thickness(14, 12, 14, 12);
            card.Child = col;
            return card;
        }
    }
}
