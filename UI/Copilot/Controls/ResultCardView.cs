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
                if (actions.Undo != null && canUndo) chips.Add(CardKit.Chip(ms ? "Buat asal semua" : "Undo all", actions.Undo));
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
            if (card.Kind == ResultKind.Changes && card.Counts.Length > 0) titles.Children.Add(ReadBack(card, ms));
            else if (!string.IsNullOrEmpty(card.Detail)) titles.Children.Add(CardKit.Text(card.Detail, 11, "Cp.Muted"));
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

        /// <summary>Counts, then a badge: green when Revit read every value back, amber when some differ.</summary>
        private static FrameworkElement ReadBack(ResultCardModel card, bool ms)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            row.Children.Add(CardKit.Text(card.Counts, 11, "Cp.Muted", margin: new Thickness(0, 0, 8, 0)));
            var ok = card.Mismatches == 0;
            var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 1) };
            badge.SetResourceReference(Border.BackgroundProperty, ok ? "Cp.OkBg" : "Cp.AmberBg");
            var n = card.Mismatches.ToString(CultureInfo.InvariantCulture);
            var text = ok ? (ms ? "✓ Dibaca balik dari Revit" : "✓ Read back from Revit")
                          : (ms ? n + " tak sepadan dalam bacaan balik" : n + " mismatch" + (card.Mismatches == 1 ? "" : "es") + " in the read-back");
            badge.Child = CardKit.Text(text, 11, ok ? "Cp.Ink" : "Cp.AmberFg", FontWeights.Medium);
            row.Children.Add(badge);
            return row;
        }

        // ─── Bodies ───────────────────────────────────────────────────────────

        private static void Changes(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var allRows = card.Groups.SelectMany(g => g.Rows).ToList();

            // Rows that need checking come first (the model orders them) behind one banner.
            if (card.CheckCount > 0)
            {
                var n = card.CheckCount.ToString(CultureInfo.InvariantCulture);
                var banner = new Border { Padding = new Thickness(12, 6, 10, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
                banner.SetResourceReference(Border.BackgroundProperty, "Cp.AmberBg");
                banner.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                var bg = new DockPanel { LastChildFill = true };
                var keep = CardKit.Chip(ms ? "Kekalkan semua" : "Keep all as written", () => banner.Visibility = Visibility.Collapsed);
                keep.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(keep, Dock.Right); bg.Children.Add(keep);
                var bt = new StackPanel();
                bt.Children.Add(CardKit.Text(ms ? "Semak " + n + " nilai" : "Check " + n + (card.CheckCount == 1 ? " value" : " values"), 12, "Cp.AmberFg", FontWeights.SemiBold));
                bt.Children.Add(CardKit.Text(ms ? "Bacaan balik Revit berbeza daripada nilai yang ditulis. Klik nilai Selepas untuk betulkan." : "Revit read back something other than what was written. Click an After value to correct it.", 11, "Cp.AmberFg"));
                bg.Children.Add(bt);
                banner.Child = bg;
                root.Children.Add(banner);
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
                    var flagged = g.Rows.Any(r => r.NeedsCheck) ? "● " : "";
                    var b = CardKit.Chip(flagged + Short(g.TypeName, 26) + " · " + g.Instances.ToString(CultureInfo.InvariantCulture), () => { });
                    b.Click += (_, __) => { tableHost.Content = Table(card.Groups[idx], lang, actions); Paint(buttons, idx); if (actions.SelectGroup != null) actions.SelectGroup(card.Groups[idx]); };
                    buttons.Add(b); tabs.Children.Add(b);
                }
                Paint(buttons, 0);
                body.Children.Add(tabs);
            }
            tableHost.Content = Table(card.Groups[0], lang, actions);
            body.Children.Add(tableHost);

            var acts = Actions(root);
            if (actions.ShowInModel != null) acts.Children.Add(CardKit.Chip(ms ? "Serlahkan dalam model" : "Highlight in model", actions.ShowInModel));
            if (actions.SelectGroup != null) acts.Children.Add(CardKit.Chip(ms ? "Pilih" : "Select", () => actions.SelectGroup(Current(card, tableHost))));
            if (actions.ZoomGroup != null) acts.Children.Add(CardKit.Chip("Zoom", () => actions.ZoomGroup(Current(card, tableHost))));
            if (actions.OpenTypeProperties != null && card.Groups.Any(g => g.IsType))
                acts.Children.Add(CardKit.Chip(ms ? "Buka Type Properties" : "Open Type Properties", () => actions.OpenTypeProperties(Current(card, tableHost))));
            if (allRows.Count > 0)
            {
                Button? copy = null;
                copy = CardKit.Chip(ms ? "Salin jadual" : "Copy table", () =>
                {
                    try { Clipboard.SetText(TableText(card)); if (copy != null) copy.Content = ms ? "Disalin" : "Copied"; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] copy table: " + ex.Message); }
                });
                acts.Children.Add(copy);
            }
        }

        /// <summary>The whole result as tab-separated text, ready to paste into Excel.</summary>
        internal static string TableText(ResultCardModel card)
        {
            var sb = new System.Text.StringBuilder("Type\tParameter\tBefore\tAfter\tSource");
            foreach (var g in card.Groups)
                foreach (var r in g.Rows)
                    sb.Append('\n').Append(g.TypeName).Append('\t').Append(r.Param).Append('\t').Append(r.Before ?? "").Append('\t').Append(r.After ?? "").Append('\t').Append(r.Source);
            return sb.ToString();
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

        // Parameter | Before | After (click to edit) | Source (click for where it came from).
        // Rows past MaxRows stay collapsed behind one line.
        private static FrameworkElement Table(TypeGroup g, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var grid = new Grid { Tag = g };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Header(grid, 0, "Parameter", ms ? "Sebelum" : "Before", ms ? "Selepas" : "After", ms ? "Sumber" : "Source");
            var r = 1;
            var overflow = new List<UIElement>();
            for (var i = 0; i < g.Rows.Count; i++)
            {
                var row = g.Rows[i];
                var extra = i >= MaxRows;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var cells = new List<FrameworkElement>();

                var param = CardKit.Text("", 11.5, "Cp.Muted", wrap: false, margin: new Thickness(0, 3, 10, 3));
                if (row.NeedsCheck) { var dot = new Run("● "); dot.SetResourceReference(TextElement.ForegroundProperty, "Cp.Amber"); param.Inlines.Add(dot); }
                param.Inlines.Add(new Run(row.Param));
                cells.Add(param); Cell(grid, r, 0, param);

                var before = CardKit.Text(string.IsNullOrEmpty(row.Before) ? (ms ? "kosong" : "empty") : row.Before!, 11.5, "Cp.Faint", mono: true, margin: new Thickness(0, 3, 10, 3));
                cells.Add(before); Cell(grid, r, 1, before);

                var after = AfterCell(g, row, actions, lang);
                cells.Add(after); Cell(grid, r, 2, after);

                // The source viewer: one line under the row, with the value's trail.
                var detail = SourceDetail(row, lang);
                detail.Visibility = Visibility.Collapsed;
                Grid.SetRow(detail, r + 1); Grid.SetColumnSpan(detail, 4); grid.Children.Add(detail);
                var srcText = string.IsNullOrEmpty(row.Source) ? (ms ? "Prompt anda" : "Your prompt") : Short(row.Source, 22);
                var src = CardKit.Text(srcText, 11, "Cp.Faint", wrap: false, margin: new Thickness(0, 3, 0, 3));
                src.TextDecorations = TextDecorations.Underline;
                src.Cursor = System.Windows.Input.Cursors.Hand;
                src.MouseLeftButtonUp += (_, __) => detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                cells.Add(src); Cell(grid, r, 3, src);

                if (extra) { foreach (var c in cells) { c.Visibility = Visibility.Collapsed; overflow.Add(c); } }
                r += 2;
            }
            if (overflow.Count > 0)
            {
                var hiddenRows = g.Rows.Count - MaxRows;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var more = CardKit.Text(ms ? "Tunjuk " + hiddenRows + " lagi" : "Show " + hiddenRows + " more", 11, "Cp.Faint", margin: new Thickness(0, 2, 0, 0));
                more.TextDecorations = TextDecorations.Underline; more.Cursor = System.Windows.Input.Cursors.Hand;
                more.MouseLeftButtonUp += (_, __) => { foreach (var c in overflow) c.Visibility = Visibility.Visible; more.Visibility = Visibility.Collapsed; };
                Grid.SetRow(more, r); Grid.SetColumnSpan(more, 4); grid.Children.Add(more);
            }
            return grid;
        }

        private static FrameworkElement SourceDetail(ChangeRow row, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var b = new Border { Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6) };
            b.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            var sp = new StackPanel();
            string Or(string? s) => string.IsNullOrEmpty(s) ? (ms ? "kosong" : "empty") : s!;
            sp.Children.Add(CardKit.Text((ms ? "Sumber · " : "Source · ") + (string.IsNullOrEmpty(row.Source) ? (ms ? "Prompt anda" : "Your prompt") : row.Source), 11, "Cp.Ink2", FontWeights.Medium));
            sp.Children.Add(CardKit.Text((ms ? "Sebelum · " : "Before · ") + Or(row.Before) + "   " + (ms ? "Selepas · " : "After · ") + Or(row.After), 11, "Cp.Muted", mono: true));
            b.Child = sp;
            return b;
        }

        /// <summary>The After value; click it to type a new one (units checked, then written as one more batch of the turn).</summary>
        private static FrameworkElement AfterCell(TypeGroup g, ChangeRow row, TurnEvidenceActions actions, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var host = new ContentControl { Margin = new Thickness(0, 0, 10, 0) };
            void Show()
            {
                var v = CardKit.Text(row.After ?? "", 11.5, "Cp.Ink", FontWeights.SemiBold, mono: true, margin: new Thickness(0, 3, 0, 3));
                if (actions.EditValue != null && row.Ids.Count > 0)
                {
                    v.Cursor = System.Windows.Input.Cursors.IBeam;
                    v.ToolTip = ms ? "Klik untuk ubah" : "Click to edit";
                    v.MouseLeftButtonUp += (_, __) => Edit(null);
                }
                host.Content = v;
            }
            void Edit(string? err)
            {
                var box = new TextBox { Text = row.After ?? "", FontSize = 11.5, Padding = new Thickness(3, 1, 3, 1), MinWidth = 60 };
                box.SetResourceReference(Control.FontFamilyProperty, "Cp.FontMono");
                box.SetResourceReference(Control.BackgroundProperty, "Cp.Bg");
                box.SetResourceReference(Control.ForegroundProperty, "Cp.Ink");
                box.SetResourceReference(Control.BorderBrushProperty, "Cp.Blue");
                var msg = CardKit.Text(err ?? "", 10.5, "Cp.Red");
                msg.Visibility = err == null ? Visibility.Collapsed : Visibility.Visible;
                var busy = false;
                async void Commit()
                {
                    var norm = ValueInput.Normalize(row.After, box.Text, out var e);
                    if (norm == null) { msg.Text = e; msg.Visibility = Visibility.Visible; return; }
                    if (norm == (row.After ?? "")) { Show(); return; }
                    busy = true; box.IsEnabled = false;
                    string? fail;
                    try { fail = await actions.EditValue!(g, row, norm); }
                    catch (Exception ex) { fail = ex.Message; }
                    busy = false;
                    if (fail == null) { row.After = norm; row.NeedsCheck = false; Show(); actions.Refresh?.Invoke(); }
                    else { box.IsEnabled = true; msg.Text = fail; msg.Visibility = Visibility.Visible; box.Focus(); }
                }
                box.KeyDown += (_, k) =>
                {
                    if (k.Key == System.Windows.Input.Key.Enter) { k.Handled = true; Commit(); }
                    else if (k.Key == System.Windows.Input.Key.Escape) { k.Handled = true; Show(); }
                };
                // A typed-but-uncommitted value stays: the card is retained across thread rebuilds, which drop focus.
                box.LostKeyboardFocus += (_, __) => { if (!busy && box.IsEnabled && msg.Visibility != Visibility.Visible && box.Text == (row.After ?? "")) Show(); };
                box.Loaded += (_, __) => { box.Focus(); box.SelectAll(); };
                var sp = new StackPanel();
                sp.Children.Add(box); sp.Children.Add(msg);
                host.Content = sp;
            }
            Show();
            return host;
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
