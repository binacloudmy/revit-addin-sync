#nullable enable
// Body of the "Created" result card when the commits carried counts by category
// (boards 8-10): one row per category with its count, a source line, and the buttons
// Show them / Copy list. "Undo all" is the card header's chip (ResultCardView).
// Per-sheet and per-view rows are a later phase.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class CreatedCard
    {
        private const int MaxRows = 12;

        /// <summary>Adds the body and the action row; false (nothing added) when the card has no category counts.</summary>
        public static bool TryBuild(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var by = card.CreatedBy;
            if (by == null || by.Groups.Count == 0) return false;
            var ms = lang == ReceiptLanguage.Ms;

            var grid = new Grid { Margin = new Thickness(12, 8, 12, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var r = 0;
            foreach (var g in by.Groups)
            {
                if (r >= MaxRows) break;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = CardKit.Text(g.Category, 11.5, "Cp.Ink", margin: new Thickness(0, 3, 10, 3));
                Grid.SetRow(name, r); grid.Children.Add(name);
                var n = CardKit.Text(g.Count.ToString(CultureInfo.InvariantCulture), 11.5, "Cp.Ink", wrap: false, margin: new Thickness(0, 3, 0, 3), mono: true);
                Grid.SetRow(n, r); Grid.SetColumn(n, 1); grid.Children.Add(n);
                r++;
            }
            if (by.Groups.Count > MaxRows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var more = CardKit.Text("+ " + (by.Groups.Count - MaxRows) + (ms ? " lagi" : " more"), 11, "Cp.Faint");
                Grid.SetRow(more, r); Grid.SetColumnSpan(more, 2); grid.Children.Add(more);
            }
            root.Children.Add(grid);
            root.Children.Add(CardKit.Text(by.SourceLine(lang), 11, "Cp.Muted", margin: new Thickness(12, 0, 12, 8)));

            var acts = ResultCardView.Actions(root);
            if (actions.ShowElements != null && card.Created.Count > 0)
                acts.Children.Add(CardKit.Chip(ms ? "Tunjuk semuanya" : "Show them", () => actions.ShowElements(card.Created.ConvertAll(c => c.Id).ToArray())));
            Button? copy = null;
            copy = CardKit.Chip(ms ? "Salin senarai" : "Copy list", () =>
            {
                try { Clipboard.SetText(by.CopyText()); if (copy != null) copy.Content = ms ? "Disalin" : "Copied"; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] copy created list: " + ex.Message); }
            });
            acts.Children.Add(copy);
            if (actions.ShowInModel != null) acts.Children.Add(CardKit.Chip(ms ? "Serlahkan dalam model" : "Highlight in model", actions.ShowInModel));
            return true;
        }
    }
}
