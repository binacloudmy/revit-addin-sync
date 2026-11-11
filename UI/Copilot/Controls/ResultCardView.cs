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

            // Undone (by the card's button or Revit's own Undo): say so at the top, with what was removed and Redo.
            var undone = IsUndone(actions);
            if (undone) root.Children.Add(UndoneBanner(card, lang, actions));

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
                if (actions.Redo != null && !undone && Ok(actions.CanRedo)) chips.Add(CardKit.Chip(ms ? "Buat semula" : "Redo", actions.Redo));   // the banner holds Redo once undone
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
            if (undoOff != null && !undone) titles.Children.Add(CardKit.Text(undoOff, 10.5, "Cp.AmberFg", margin: new Thickness(0, 3, 0, 0)));
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
            ItemsCard.Add(root, card, lang, actions);
            ChecksAndNext(root, card, lang, actions);
            return outer;
        }

        private static bool IsUndone(TurnEvidenceActions actions)
        {
            try { return actions.WhyNotUndo?.Invoke() == BinaVibe.Copilot.Undo.UndoBlock.AlreadyUndone; }
            catch (Exception) { return false; }
        }

        /// <summary>"Undone. The model is back to how it was." · what was removed · Redo.</summary>
        private static FrameworkElement UndoneBanner(ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var (title, detail) = UndoBanner.For(card, lang);
            var banner = new Border { Padding = new Thickness(12, 8, 10, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
            banner.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            banner.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            var dock = new DockPanel { LastChildFill = true };
            bool canRedo;
            try { canRedo = actions.Redo != null && (actions.CanRedo?.Invoke() ?? false); } catch (Exception) { canRedo = false; }
            if (canRedo)
            {
                var redo = CardKit.Chip(ms ? "Buat semula" : "Redo", actions.Redo!);
                redo.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(redo, Dock.Right); dock.Children.Add(redo);
            }
            var text = new StackPanel();
            text.Children.Add(CardKit.Text(title, 12.5, "Cp.Ink", FontWeights.SemiBold));
            text.Children.Add(CardKit.Text(detail, 11, "Cp.Muted", margin: new Thickness(0, 2, 0, 0)));
            dock.Children.Add(text);
            banner.Child = dock;
            return banner;
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
            if (!string.IsNullOrEmpty(card.Elapsed)) row.Children.Add(CardKit.Text(card.Elapsed, 11, "Cp.Faint", margin: new Thickness(8, 0, 0, 0)));
            return row;
        }

        // ─── Bodies ───────────────────────────────────────────────────────────

        private static void Changes(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            // "No net change" has a headline and no groups: nothing to tabulate, and nothing to index.
            if (card.FirstGroup == null) return;
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
            Button? again = null;   // "Write these values": shown for a type whose put-back rows can be written again
            Button? leave = null;   // "Leave as is": the drafter accepts the put-back values of a type
            void SyncPutBack(TypeGroup g)
            {
                if (again != null) again.Visibility = g.CanWriteAgain ? Visibility.Visible : Visibility.Collapsed;
                if (leave != null) leave.Visibility = g.AnyChangedBack && !g.LeftAsIs ? Visibility.Visible : Visibility.Collapsed;
            }
            if (card.Groups.Count > 1)
            {
                var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                var buttons = new List<Button>();
                for (var i = 0; i < card.Groups.Count; i++)
                {
                    var g = card.Groups[i]; var idx = i;
                    var flagged = g.ChangedBack && !g.LeftAsIs ? "↺ " : g.Rows.Any(r => r.NeedsCheck) ? "● " : "";
                    var b = CardKit.Chip(flagged + Short(g.TypeName, 26) + " · " + g.Instances.ToString(CultureInfo.InvariantCulture), () => { });
                    b.Click += (_, __) => { tableHost.Content = Table(card.Groups[idx], lang, actions); Paint(buttons, idx); SyncPutBack(card.Groups[idx]); if (actions.SelectGroup != null) actions.SelectGroup(card.Groups[idx]); };
                    buttons.Add(b); tabs.Children.Add(b);
                }
                Paint(buttons, 0);
                body.Children.Add(tabs);
            }
            tableHost.Content = Table(card.FirstGroup!, lang, actions);
            body.Children.Add(tableHost);

            var acts = Actions(root);
            if (actions.ShowInModel != null) acts.Children.Add(CardKit.Chip(ms ? "Serlahkan dalam model" : "Highlight in model", actions.ShowInModel));
            if (actions.SelectGroup != null) acts.Children.Add(CardKit.Chip(ms ? "Pilih" : "Select", () => actions.SelectGroup(Current(card, tableHost))));
            if (actions.ZoomGroup != null) acts.Children.Add(CardKit.Chip("Zoom", () => actions.ZoomGroup(Current(card, tableHost))));
            if (actions.OpenTypeProperties != null && card.Groups.Any(g => g.IsType))
                acts.Children.Add(CardKit.Chip(ms ? "Buka Type Properties" : "Open Type Properties", () => actions.OpenTypeProperties(Current(card, tableHost))));
            if (actions.EditValue != null && card.Groups.Any(g => g.AnyChangedBack))
            {
                again = CardKit.Chip(ms ? "Tulis nilai ini" : "Write these values", () => WriteAgain(card, tableHost, actions, lang, again!));
                acts.Children.Add(again);
            }
            if (card.Groups.Any(g => g.AnyChangedBack))
            {
                if (actions.SelectGroup != null)
                    acts.Children.Add(CardKit.Chip(ms ? "Tunjuk dalam Revit" : "Show in Revit", () => actions.SelectGroup(Current(card, tableHost))));
                leave = CardKit.Chip(ms ? "Biarkan" : "Leave as is", () =>
                {
                    var g = Current(card, tableHost);
                    g.LeftAsIs = true;
                    card.RefreshHeadline(lang);      // the "N changed back" count and the tab marks follow
                    tableHost.Content = Table(g, lang, actions);
                    SyncPutBack(g);
                    actions.Redraw?.Invoke();        // the header and tabs are drawn once: have the pane draw the card again
                });
                acts.Children.Add(leave);
            }
            SyncPutBack(card.FirstGroup!);
            if (allRows.Count > 0)
            {
                acts.Children.Add(CardKit.Chip(ms ? "Buka sebagai grid" : "Open as grid", () =>
                    GridWindow.Show(ms ? "Perubahan Copilot" : "Copilot changes", ChangeTable.Columns(lang), ChangeTable.Rows(card, lang))));
                Button? copy = null;
                copy = CardKit.Chip(ms ? "Salin jadual" : "Copy table", () =>
                {
                    try { Clipboard.SetText(TableText(card)); if (copy != null) copy.Content = ms ? "Disalin" : "Copied"; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] copy table: " + ex.Message); }
                });
                acts.Children.Add(copy);
            }
        }

        /// <summary>Write a type's put-back values again: one edit per row, the same path as clicking an After cell (units
        /// checked, one more batch of the turn). Only rows with one value to write are sent; stops at the first that fails.</summary>
        private static async void WriteAgain(ResultCardModel card, ContentControl host, TurnEvidenceActions actions, ReceiptLanguage lang, Button chip)
        {
            var ms = lang == ReceiptLanguage.Ms;
            try
            {
                var g = Current(card, host);
                var rows = g.Rows.Where(r => r.ChangedBack && !string.IsNullOrEmpty(r.Intended)).ToList();
                if (rows.Count == 0 || actions.EditValue == null || !g.CanWriteAgain) return;
                chip.IsEnabled = false;
                foreach (var row in rows)
                {
                    var norm = ValueInput.Normalize(row.After, row.Intended!, out var e);
                    string? fail;
                    if (norm == null) fail = e;
                    else
                    {
                        try { fail = await actions.EditValue(g, row, norm); }
                        catch (Exception ex) { fail = ex.Message; }
                    }
                    if (fail != null)
                    {
                        chip.IsEnabled = true;
                        chip.Content = (ms ? "Gagal: " : "Could not write: ") + Short(fail, 40);
                        return;
                    }
                    row.After = norm; row.ChangedBack = false; row.Intended = null; row.IntendedLabel = null; row.NeedsCheck = false;
                }
                chip.Content = ms ? "Ditulis" : "Written";
                host.Content = Table(g, lang, actions);
            }
            catch (Exception ex)
            {
                // async void: nothing may escape into the dispatcher.
                System.Diagnostics.Debug.WriteLine("[BINA] write again: " + ex.Message);
                try { chip.IsEnabled = true; } catch { /* the card is gone */ }
            }
        }

        /// <summary>The whole result as tab-separated text, ready to paste into Excel.</summary>
        internal static string TableText(ResultCardModel card) => ChangeTable.TabText(card);

        private static TypeGroup Current(ResultCardModel card, ContentControl host) =>
            host.Content is FrameworkElement fe && fe.Tag is TypeGroup g ? g : card.FirstGroup ?? new TypeGroup();

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
            var back = g.ChangedBack;
            var noted = g.AnyChangedBack && !g.LeftAsIs;
            if (noted)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var note = CardKit.Text(back
                    ? (ms ? "Nilai ditulis, kemudian dipulihkan kepada nilai asal. Jenis ini kini menyimpan nilai lamanya."
                          : "Values were written, then put back to what they were. This type holds its old values now.")
                    : (ms ? "Sesetengah nilai ditulis, kemudian dipulihkan (dicoret). Nilai itu kekal nilai lama."
                          : "Some values were written, then put back (struck through). Those keep their old values."), 11, "Cp.AmberFg", margin: new Thickness(0, 0, 0, 6));
                Grid.SetRow(note, 0); Grid.SetColumnSpan(note, 4); grid.Children.Add(note);
            }
            var top = noted ? 1 : 0;
            Header(grid, top, "Parameter", back ? (ms ? "Kini" : "Holds now") : ms ? "Sebelum" : "Before", back ? (ms ? "Ditulis" : "Wrote") : ms ? "Selepas" : "After", ms ? "Sumber" : "Source");
            var r = top + 1;
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
                var canEdit = actions.EditValue != null && row.Ids.Count > 0 && !row.ChangedBack;
                var detail = SourcePanel.Build(row, lang, actions, canEdit ? () => (after.Tag as Action)?.Invoke() : (Action?)null);
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

        /// <summary>The After value; click it to type a new one (units checked, then written as one more batch of the turn).</summary>
        private static FrameworkElement AfterCell(TypeGroup g, ChangeRow row, TurnEvidenceActions actions, ReceiptLanguage lang)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var host = new ContentControl { Margin = new Thickness(0, 0, 10, 0) };
            if (row.ChangedBack)
            {
                // Written, then put back: the intended value is shown, struck through; nothing to edit until written again.
                var was = CardKit.Text(row.IntendedText, 11.5, "Cp.Faint", mono: true, margin: new Thickness(0, 3, 0, 3));
                was.TextDecorations = TextDecorations.Strikethrough;
                host.Content = was;
                return host;
            }
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
                    if (fail == null) { row.After = norm; row.NeedsCheck = false; Show(); }
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
            host.Tag = (Action)(() => { if (actions.EditValue != null && row.Ids.Count > 0) Edit(null); });
            return host;
        }

        private static void Created(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            if (CreatedCard.TryBuild(root, card, lang, actions)) return;   // grouped by category (boards 8-10)
            if (card.Created.Count == 0) return;                            // the server's rows and checks, no elements of ours
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
            var lines = SelectionLines.For(card, lang);
            if (lines.Count > 0)
            {
                var body = new StackPanel { Margin = new Thickness(12, 8, 12, 4) };
                foreach (var l in lines) body.Children.Add(CardKit.Text(l, 11.5, "Cp.Ink2", margin: new Thickness(0, 1, 0, 1)));
                root.Children.Add(body);
            }
            var acts = Actions(root);
            if (actions.ShowInModel != null) acts.Children.Add(CardKit.Chip(ms ? "Serlahkan dalam model" : "Highlight in model", actions.ShowInModel));
            if (card.Isolated && actions.ResetIsolate != null)
            {
                Button? reset = null;
                reset = CardKit.Chip(SelectionLines.ResetLabel(lang), async () =>
                {
                    reset!.IsEnabled = false;
                    string? why;
                    try { why = await actions.ResetIsolate(); } catch (Exception ex) { why = ex.Message; }
                    if (why == null || why == "none")
                        reset.Content = why == null ? SelectionLines.ResetDone(lang) : SelectionLines.ResetNothing(lang);
                    else
                    {
                        // The whole reason, wrapped, not clipped; the same text as a tooltip.
                        var full = SelectionLines.ResetFailure(why, lang);
                        reset.Content = new TextBlock { Text = full, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
                        reset.ToolTip = full;
                        reset.IsEnabled = true;
                    }
                });
                acts.Children.Add(reset);
            }
        }

        /// <summary>"Check N things" with their answer buttons, then the "Next" chips (the server's turn_end.result).</summary>
        private static void ChecksAndNext(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            if (card.Checks.Count > 0)
            {
                var box = new StackPanel { Margin = new Thickness(12, 8, 12, 4) };
                var n = card.Checks.Count.ToString(CultureInfo.InvariantCulture);
                box.Children.Add(CardKit.Text(ms ? "Semak " + n + (card.Checks.Count == 1 ? " perkara" : " perkara") : "Check " + n + (card.Checks.Count == 1 ? " thing" : " things"), 12, "Cp.AmberFg", FontWeights.SemiBold, margin: new Thickness(0, 0, 0, 4)));
                foreach (var c in card.Checks) box.Children.Add(CheckRow(c, lang, actions));
                var strip = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = box };
                strip.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                root.Children.Add(strip);
            }
            if (card.Next.Count > 0 && actions.SendPrompt != null)
            {
                var bar = new Border { Padding = new Thickness(12, 6, 12, 4), BorderThickness = new Thickness(0, 1, 0, 0) };
                bar.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                var wrap = new WrapPanel();
                wrap.Children.Add(CardKit.Text(ms ? "Seterusnya" : "Next", 10.5, "Cp.Faint", FontWeights.Medium, margin: new Thickness(0, 5, 8, 0)));
                foreach (var next in card.Next)
                {
                    var text = next;
                    wrap.Children.Add(CardKit.Chip(text, () => actions.SendPrompt(text)));
                }
                bar.Child = wrap;
                root.Children.Add(bar);
            }
        }

        private static FrameworkElement CheckRow(CheckItem c, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
            var line = CardKit.Text("", 11.5, "Cp.Ink");
            var dot = new Run("● "); dot.SetResourceReference(TextElement.ForegroundProperty, c.Severity == "error" ? "Cp.Red" : c.Severity == "warning" ? "Cp.Amber" : "Cp.Blue");
            line.Inlines.Add(dot); line.Inlines.Add(new Run(c.Text));
            row.Children.Add(line);
            if (c.Actions.Count > 0)
            {
                var status = CardKit.Text("", 10.5, "Cp.Muted", margin: new Thickness(0, 0, 0, 2));
                status.Visibility = Visibility.Collapsed;
                var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                var chips = new List<Button>();
                foreach (var a in c.Actions)
                {
                    var action = a;
                    var chip = CardKit.Chip(action.Label, () => { });
                    chip.Click += async (_, __) =>
                    {
                        if (action.Kind == BinaVibe.Copilot.Contracts.ResultActionKinds.Dismiss) { row.Visibility = Visibility.Collapsed; return; }
                        if (actions.RunCheckAction == null) return;
                        foreach (var b in chips) b.IsEnabled = false;
                        string? why;
                        try { why = await actions.RunCheckAction(action); } catch (Exception ex) { why = ex.Message; }
                        status.Visibility = Visibility.Visible;
                        if (why == null) status.Text = action.Kind == BinaVibe.Copilot.Contracts.ResultActionKinds.Prompt ? (ms ? "Dihantar." : "Sent.") : (ms ? "Ditulis. Undo membuangnya." : "Written. Undo takes it back.");
                        else { status.Text = (ms ? "Tidak dapat: " : "Could not: ") + Short(why, 80); foreach (var b in chips) b.IsEnabled = true; }
                    };
                    chips.Add(chip); bar.Children.Add(chip);
                }
                row.Children.Add(bar); row.Children.Add(status);
            }
            return row;
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        internal static WrapPanel Actions(StackPanel root)
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
