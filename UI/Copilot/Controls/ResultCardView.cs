#nullable enable
// The result card (result-pattern spec, Nov 2026): header with the outcome and
// Undo, a body shaped by the result kind, and a contextual action row.
//   changes   type tabs → Parameter | Before → After | Source; a Source block
//             when the turn had an attachment
//   created   number · label · Open
//   findings  issue · count · rule · Show
//   selection type · count · Select · Zoom
// All text comes from ResultCardModel (envelopes), never from the reply.

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
    public static class ResultCardView
    {
        private const int MaxRows = 12;

        public static FrameworkElement? Build(ResultCardModel? card, ReceiptLanguage lang, TurnEvidenceActions actions, bool canUndo, string? undoOff)
        {
            if (card == null || card.Kind == ResultKind.None) return null;
            var ms = lang == ReceiptLanguage.Ms;
            string line, bg;
            switch (card.Kind)
            {
                case ResultKind.Changes: line = "Cp.SuccessLn"; bg = "Cp.OkBg"; break;
                case ResultKind.Findings: line = card.Findings.Any(f => f.Fixable) ? "Cp.Red" : "Cp.AmberLn"; bg = "Cp.IssueBg"; break;
                default: line = "Cp.Blue"; bg = "Cp.BlueSoft"; break;
            }
            var outer = CardKit.Card(line, "Cp.Bg", new Thickness(0, 6, 0, 6), 10);
            var root = new StackPanel();
            outer.Child = root;

            // Header: outcome · detail · Undo
            var head = new Border { Padding = new Thickness(12, 8, 10, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
            head.SetResourceReference(Border.BackgroundProperty, bg);
            head.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            var hg = new DockPanel { LastChildFill = true };
            if (card.Kind != ResultKind.Findings)
            {
                bool Ok(Func<bool>? f) { try { return f?.Invoke() ?? false; } catch { return false; } }
                var chips = new List<FrameworkElement>();
                if (actions.Undo != null && canUndo) chips.Add(CardKit.Chip(ms ? "Buat asal" : "Undo", actions.Undo));
                if (actions.UndoLast != null && Ok(actions.CanUndoLast)) chips.Add(CardKit.Chip(ms ? "Buat asal bahagian akhir" : "Undo last part", actions.UndoLast));
                if (actions.Redo != null && Ok(actions.CanRedo)) chips.Add(CardKit.Chip(ms ? "Buat semula" : "Redo", actions.Redo));
                if (chips.Count > 0)
                {
                    var bar = new WrapPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    foreach (var c in chips) bar.Children.Add(c);
                    DockPanel.SetDock(bar, Dock.Right); hg.Children.Add(bar);
                }
            }
            var titles = new StackPanel();
            titles.Children.Add(CardKit.Text(card.Headline, 13, "Cp.Ink", FontWeights.SemiBold));
            if (!string.IsNullOrEmpty(card.Detail)) titles.Children.Add(CardKit.Text(card.Detail, 11, "Cp.Muted"));
            if (undoOff != null) titles.Children.Add(CardKit.Text(undoOff, 10.5, "Cp.AmberFg", margin: new Thickness(0, 3, 0, 0)));
            hg.Children.Add(titles);
            head.Child = hg;
            root.Children.Add(head);

            switch (card.Kind)
            {
                case ResultKind.Changes: Changes(root, card, lang, actions); break;
                case ResultKind.Created: Created(root, card, lang, actions); break;
                case ResultKind.Findings: Findings(root, card, lang, actions); break;
                case ResultKind.Selection: Selection(root, card, lang, actions); break;
            }
            return outer;
        }

        // ─── Bodies ───────────────────────────────────────────────────────────

        private static void Changes(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var source = card.Groups.SelectMany(g => g.Rows).Select(r => r.Source).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (!string.IsNullOrEmpty(source))
            {
                var src = new Border { Padding = new Thickness(12, 6, 12, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
                src.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
                src.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                var t = CardKit.Text("", 11, "Cp.Muted");
                t.Inlines.Add(new Run((ms ? "Sumber · " : "Source · ")));
                t.Inlines.Add(new Run(source) { FontWeight = FontWeights.SemiBold });
                src.Child = t;
                root.Children.Add(src);
            }

            var body = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
            root.Children.Add(body);
            var tableHost = new ContentControl();
            if (card.Groups.Count > 1)
            {
                var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                var buttons = new List<Button>();
                for (var i = 0; i < card.Groups.Count; i++)
                {
                    var g = card.Groups[i]; var idx = i;
                    var b = CardKit.Chip(Short(g.TypeName, 26) + " · " + g.Instances.ToString(CultureInfo.InvariantCulture), () => { });
                    b.Click += (_, __) => { tableHost.Content = Table(card.Groups[idx], lang); Paint(buttons, idx); if (actions.SelectGroup != null) actions.SelectGroup(card.Groups[idx]); };
                    buttons.Add(b); tabs.Children.Add(b);
                }
                Paint(buttons, 0);
                body.Children.Add(tabs);
            }
            tableHost.Content = Table(card.Groups[0], lang);
            body.Children.Add(tableHost);

            var acts = Actions(root);
            if (actions.ShowInModel != null) acts.Children.Add(CardKit.Chip(ms ? "Serlahkan dalam model" : "Highlight in model", actions.ShowInModel));
            if (actions.SelectGroup != null) acts.Children.Add(CardKit.Chip(ms ? "Pilih" : "Select", () => actions.SelectGroup(Current(card, tableHost))));
            if (actions.ZoomGroup != null) acts.Children.Add(CardKit.Chip("Zoom", () => actions.ZoomGroup(Current(card, tableHost))));
            if (actions.OpenTypeProperties != null && card.Groups.Any(g => g.IsType))
                acts.Children.Add(CardKit.Chip(ms ? "Buka Type Properties" : "Open Type Properties", () => actions.OpenTypeProperties(Current(card, tableHost))));
        }

        private static TypeGroup Current(ResultCardModel card, ContentControl host) =>
            host.Content is FrameworkElement fe && fe.Tag is TypeGroup g ? g : card.Groups[0];

        private static void Paint(List<Button> buttons, int on)
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                buttons[i].SetResourceReference(Control.BackgroundProperty, i == on ? "Cp.Ink" : "Cp.Bg");
                buttons[i].SetResourceReference(Control.ForegroundProperty, i == on ? "Cp.Bg" : "Cp.Ink2");
            }
        }

        private static FrameworkElement Table(TypeGroup g, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var grid = new Grid { Tag = g };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Header(grid, 0, ms ? "Parameter" : "Parameter", ms ? "Sebelum → Selepas" : "Before → After", ms ? "Sumber" : "Source");
            var r = 1;
            foreach (var row in g.Rows.Take(MaxRows))
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Cell(grid, r, 0, CardKit.Text(row.Param, 11.5, "Cp.Muted", wrap: false, margin: new Thickness(0, 3, 10, 3)));
                var v = CardKit.Text("", 11.5, "Cp.Ink", mono: true, margin: new Thickness(0, 3, 10, 3));
                var before = string.IsNullOrEmpty(row.Before) ? (ms ? "kosong" : "empty") : row.Before;
                var faint = new Run(before + " → "); faint.SetResourceReference(TextElement.ForegroundProperty, "Cp.Faint");
                v.Inlines.Add(faint);
                v.Inlines.Add(new Run(row.After ?? ""));
                Cell(grid, r, 1, v);
                Cell(grid, r, 2, CardKit.Text(string.IsNullOrEmpty(row.Source) ? "" : Short(row.Source, 22), 11, "Cp.Faint", wrap: false, margin: new Thickness(0, 3, 0, 3)));
                r++;
            }
            if (g.Rows.Count > MaxRows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var more = CardKit.Text("+ " + (g.Rows.Count - MaxRows) + (ms ? " lagi" : " more"), 11, "Cp.Faint");
                Grid.SetRow(more, r); Grid.SetColumnSpan(more, 3); grid.Children.Add(more);
            }
            return grid;
        }

        private static void Created(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var grid = new Grid { Margin = new Thickness(12, 8, 12, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var r = 0;
            foreach (var c in card.Created.Take(MaxRows))
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Cell(grid, r, 0, CardKit.Text(c.Label, 11.5, "Cp.Ink", margin: new Thickness(0, 3, 10, 3)));
                if (actions.ShowElements != null)
                {
                    var id = c.Id; var open = CardKit.Chip(ms ? "Buka" : "Open", () => actions.ShowElements(new[] { id }));
                    open.Margin = new Thickness(0, 0, 0, 2); Cell(grid, r, 1, open);
                }
                r++;
            }
            if (card.Created.Count > MaxRows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var more = CardKit.Text("+ " + (card.Created.Count - MaxRows) + (ms ? " lagi" : " more"), 11, "Cp.Faint");
                Grid.SetRow(more, r); Grid.SetColumnSpan(more, 2); grid.Children.Add(more);
            }
            root.Children.Add(grid);
            var acts = Actions(root);
            if (actions.ShowInModel != null) acts.Children.Add(CardKit.Chip(ms ? "Serlahkan dalam model" : "Highlight in model", actions.ShowInModel));
            if (actions.ShowElements != null) acts.Children.Add(CardKit.Chip(ms ? "Tunjuk semua" : "Show all", () => actions.ShowElements(card.Created.Select(c => c.Id).ToArray())));
        }

        private static void Findings(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var grid = new Grid { Margin = new Thickness(12, 8, 12, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var r = 0;
            foreach (var f in card.Findings.Take(MaxRows))
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var title = CardKit.Text("", 11.5, "Cp.Ink", margin: new Thickness(0, 3, 10, 3));
                var dot = new Run("● ") ; dot.SetResourceReference(TextElement.ForegroundProperty, f.Fixable ? "Cp.Red" : "Cp.Amber");
                title.Inlines.Add(dot); title.Inlines.Add(new Run(f.Title));
                if (!string.IsNullOrEmpty(f.Severity)) { var sev = new Run("  " + f.Severity); sev.SetResourceReference(TextElement.ForegroundProperty, "Cp.Faint"); title.Inlines.Add(sev); }
                Cell(grid, r, 0, title);
                Cell(grid, r, 1, CardKit.Text(f.Ids.Count > 0 ? f.Ids.Count.ToString(CultureInfo.InvariantCulture) : "", 11.5, "Cp.Ink", wrap: false, margin: new Thickness(0, 3, 10, 3)));
                if (f.Ids.Count > 0 && actions.ShowElements != null)
                {
                    var ids = f.Ids.ToArray(); var show = CardKit.Chip(ms ? "Tunjuk" : "Show", () => actions.ShowElements(ids));
                    show.Margin = new Thickness(0, 0, 0, 2); Cell(grid, r, 2, show);
                }
                else if (f.Handle != null && actions.Highlight != null)
                {
                    var h = f.Handle; var show = CardKit.Chip(ReceiptTemplates.HighlightLabel(lang), () => actions.Highlight(h));
                    show.Margin = new Thickness(0, 0, 0, 2); Cell(grid, r, 2, show);
                }
                r++;
            }
            root.Children.Add(grid);
            var acts = Actions(root);
            if (actions.ShowInModel != null) acts.Children.Add(CardKit.Chip(ms ? "Serlahkan dalam model" : "Highlight in model", actions.ShowInModel));
        }

        private static void Selection(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var acts = Actions(root);
            if (actions.ShowInModel != null) acts.Children.Add(CardKit.Chip(ms ? "Serlahkan dalam model" : "Highlight in model", actions.ShowInModel));
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private static WrapPanel Actions(StackPanel root)
        {
            var bar = new Border { Padding = new Thickness(12, 6, 12, 4), BorderThickness = new Thickness(0, 1, 0, 0) };
            bar.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            bar.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            var wrap = new WrapPanel();
            bar.Child = wrap;
            root.Children.Add(bar);
            return wrap;
        }

        private static void Header(Grid grid, int row, params string[] labels)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < labels.Length; c++)
                Cell(grid, row, c, CardKit.Text(labels[c], 10.5, "Cp.Faint", FontWeights.Medium, wrap: false, margin: new Thickness(0, 0, 10, 2)));
        }

        private static void Cell(Grid grid, int row, int col, FrameworkElement el)
        {
            Grid.SetRow(el, row); Grid.SetColumn(el, col); grid.Children.Add(el);
        }

        private static string Short(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : "…" + s.Substring(s.Length - n + 1);
    }
}
