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
            var undone = IsUndone(actions);

            // The block, top to bottom (design boards Main/Partial/Selection): the headline, the status chips with the
            // "Steps" toggle, then the card (body only, neutral border), then ONE action row (Undo all, Save as command).
            var block = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            block.Children.Add(HeadlineRow(card));
            var chips = StatusChips(card, ms, actions);
            if (chips != null) block.Children.Add(chips);
            if (card.Kind != ResultKind.Changes && !string.IsNullOrEmpty(card.Detail))
                block.Children.Add(CardKit.Text(card.Detail, 12, "Cp.Muted", margin: new Thickness(0, 6, 0, 0)));
            if (undoOff != null && !undone)
                block.Children.Add(CardKit.Text(undoOff, 11.5, "Cp.AmberFg", margin: new Thickness(0, 6, 0, 0)));
            if (actions.StepsPanel != null)
            {
                actions.StepsPanel.Visibility = Visibility.Collapsed;
                actions.StepsPanel.Margin = new Thickness(0, 10, 0, 0);
                block.Children.Add(actions.StepsPanel);
            }

            var outer = CardKit.Card("Cp.Line", "Cp.Bg", new Thickness(0, 12, 0, 0), 12);
            var root = new StackPanel();
            outer.Child = root;
            // Undone (by the card's button or Revit's own Undo): say so at the top, with what was removed and Redo.
            if (undone) root.Children.Add(UndoneBanner(card, lang, actions));

            switch (card.Kind)
            {
                case ResultKind.Changes: Changes(root, card, lang, actions); break;
                case ResultKind.Created: Created(root, card, lang, actions); break;
                case ResultKind.Findings: Findings(root, card, lang, actions); break;
                case ResultKind.Selection: Selection(root, card, lang, actions); break;
            }
            if (card.Isolated && card.Kind != ResultKind.Selection) IsolatedBlock(root, card, lang, actions);
            ItemsCard.Add(root, card, lang, actions);
            if (actions.RulesBlock != null) root.Children.Add(actions.RulesBlock);
            if (root.Children.Count > 0) block.Children.Add(outer);
            // The check card and Next are siblings of the main card, not inside it (boards Main, SmartTags).
            ChecksAndNext(block, card, lang, actions);

            var acts = ActionRow(card, ms, actions, canUndo, undone);
            if (acts != null) block.Children.Add(acts);
            return block;
        }

        /// <summary>The headline, outside the card: a 22px check disc (amber when something needs a look) and the one
        /// sentence at 17px/600 (design: "Copilot" label above, chips below).</summary>
        private static FrameworkElement HeadlineRow(ResultCardModel card)
        {
            var warn = card.Kind == ResultKind.Findings || card.Mismatches > 0;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
            var disc = new System.Windows.Shapes.Ellipse { Width = 22, Height = 22 };
            disc.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, warn ? "Cp.AmberBg" : "Cp.Tier1Bg");
            icon.Children.Add(disc);
            var glyph = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(warn ? "M11,6 V12 M11,15.2 V15.3" : "M6.5,11.3 L9.6,14.2 L15.6,7.8"),
                Width = 22, Height = 22, StrokeThickness = 2,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            };
            glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, warn ? "Cp.Amber" : "Cp.Green");
            icon.Children.Add(glyph);
            row.Children.Add(icon);
            var text = CardKit.Text(card.Headline, 17, "Cp.Ink", FontWeights.SemiBold);
            text.LineHeight = 22;
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            return row;
        }

        /// <summary>The chips under the headline: the read-back ("Read back from Revit: 10 of 10 match"), the time, and the
        /// "Steps" toggle that opens what the turn did (the activity card and the evidence lines). Null when there is none.</summary>
        private static FrameworkElement? StatusChips(ResultCardModel card, bool ms, TurnEvidenceActions actions)
        {
            var wrap = new WrapPanel { Margin = new Thickness(30, 8, 0, 0) };
            if (card.Kind == ResultKind.Changes && card.ChangeCount > 0)
            {
                var ok = card.Mismatches == 0;
                var total = card.ChangeCount;
                var match = Math.Max(0, total - card.Mismatches).ToString(CultureInfo.InvariantCulture);
                var text = (ms ? "Dibaca balik dari Revit: " : "Read back from Revit: ") + match + (ms ? " daripada " : " of ")
                    + total.ToString(CultureInfo.InvariantCulture) + (ms ? " sepadan" : " match");
                wrap.Children.Add(Chip(ok ? "Cp.Tier1Bg" : "Cp.AmberBg", ok ? "Cp.Green" : "Cp.AmberFg", text,
                    ok ? "M3,7.2 L5.8,10 L11,4.2" : "M7,3.5 V8 M7,10.2 V10.3"));
            }
            if (!string.IsNullOrEmpty(card.Elapsed))
                wrap.Children.Add(Chip("Cp.Raised", "Cp.Faint", card.Elapsed, "M7,2.5 A4.5,4.5 0 1 1 7,11.5 A4.5,4.5 0 1 1 7,2.5 Z M7,4.8 V7 L8.6,8"));
            if (actions.StepsPanel != null) wrap.Children.Add(StepsToggle(actions.StepsPanel, ms));
            return wrap.Children.Count > 0 ? wrap : null;
        }

        /// <summary>The "Steps" text button: opens and closes the panel it is given (the board's replacement for a stock Expander).</summary>
        internal static Button StepsToggle(FrameworkElement panel, bool ms)
        {
            var label = ms ? "Langkah" : "Steps";
            Button toggle = null!;
            toggle = CardKit.Ghost(label + " ▾", () =>
            {
                var open = panel.Visibility != Visibility.Visible;
                panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = label + (open ? " ▴" : " ▾");
            }, compact: true);
            toggle.MinHeight = 24; toggle.Margin = new Thickness(2, 0, 0, 6);
            return toggle;
        }

        /// <summary>The one "Export" ghost button: a click exports the table, or opens a menu when the turn has several; the long
        /// description of each table is only its tooltip. Null when there is nothing to export.</summary>
        internal static Button? ExportButton(TurnEvidenceActions actions, bool ms)
        {
            var tables = actions.ExportTables;
            if (actions.Export == null || tables == null || tables.Count == 0) return null;
            string Describe(ResultTable t) => (ms ? "Eksport ke Excel" : "Export to Excel") + (string.IsNullOrWhiteSpace(t.Title) ? "" : " · " + t.Title.Trim());
            Button button = null!;
            if (tables.Count == 1)
            {
                var only = tables[0];
                button = CardKit.Ghost(RulesBlockText.ExportLabel(ms), () => actions.Export!(only), CardKit.Icons.Grid);
                button.ToolTip = Describe(only);
                return button;
            }
            button = CardKit.Ghost(RulesBlockText.ExportLabel(ms) + " ▾", () =>
            {
                var menu = new ContextMenu { PlacementTarget = button, IsOpen = false };
                foreach (var t in tables)
                {
                    var table = t;
                    var item = new MenuItem { Header = Describe(table), ToolTip = Describe(table) };
                    item.Click += (_, __) => actions.Export!(table);
                    menu.Items.Add(item);
                }
                menu.IsOpen = true;
            }, CardKit.Icons.Grid);
            button.ToolTip = ms ? "Eksport ke Excel" : "Export to Excel";
            return button;
        }

        private static FrameworkElement Chip(string bg, string fg, string text, string icon)
        {
            var b = new Border { Height = 24, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 0, 9, 0), Margin = new Thickness(0, 0, 6, 6) };
            b.SetResourceReference(Border.BackgroundProperty, bg);
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var path = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(icon), Width = 14, Height = 14, Stretch = System.Windows.Media.Stretch.Uniform, StrokeThickness = 1.5,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, fg);
            row.Children.Add(path);
            row.Children.Add(CardKit.Text(text, 11.5, fg, FontWeights.Medium, wrap: false));
            b.Child = row;
            return b;
        }

        /// <summary>The one action row below the card: Undo all (and the last part / Redo when they apply), Save as command.</summary>
        private static FrameworkElement? ActionRow(ResultCardModel card, bool ms, TurnEvidenceActions actions, bool canUndo, bool undone)
        {
            bool Ok(Func<bool>? f) { try { return f?.Invoke() ?? false; } catch { return false; } }
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            if (card.Kind != ResultKind.Findings)
            {
                if (actions.Undo != null && canUndo) row.Children.Add(CardKit.Secondary(ms ? "Buat asal semua" : "Undo all", actions.Undo, CardKit.Icons.Undo));
                if (actions.UndoLast != null && Ok(actions.CanUndoLast)) row.Children.Add(CardKit.Secondary(ms ? "Buat asal bahagian akhir" : "Undo last part", actions.UndoLast));
                if (actions.Redo != null && !undone && Ok(actions.CanRedo)) row.Children.Add(CardKit.Secondary(ms ? "Buat semula" : "Redo", actions.Redo));   // the banner holds Redo once undone
            }
            var export = ExportButton(actions, ms);
            if (export != null) row.Children.Add(export);
            if (actions.SaveAsCommand != null)
                row.Children.Add(CardKit.Secondary(ms ? "Simpan sebagai arahan" : "Save as command", actions.SaveAsCommand, CardKit.Icons.Bookmark));
            return row.Children.Count > 0 ? row : null;
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
                var redo = CardKit.Secondary(ms ? "Buat semula" : "Redo", actions.Redo!);
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
                var keep = CardKit.Ghost(ms ? "Kekalkan semua" : "Keep all as written", () => banner.Visibility = Visibility.Collapsed);
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
            if (actions.ShowInModel != null) acts.Children.Add(CardKit.Secondary(ms ? "Serlahkan dalam model" : "Highlight in model", actions.ShowInModel));
            if (actions.SelectGroup != null) acts.Children.Add(CardKit.Secondary(ms ? "Pilih" : "Select", () => actions.SelectGroup(Current(card, tableHost))));
            if (actions.ZoomGroup != null) acts.Children.Add(CardKit.Secondary("Zoom", () => actions.ZoomGroup(Current(card, tableHost))));
            if (actions.OpenTypeProperties != null && card.Groups.Any(g => g.IsType))
                acts.Children.Add(CardKit.Secondary(ms ? "Buka Type Properties" : "Open Type Properties", () => actions.OpenTypeProperties(Current(card, tableHost))));
            if (actions.EditValue != null && card.Groups.Any(g => g.AnyChangedBack))
            {
                again = CardKit.Primary(ms ? "Tulis nilai ini" : "Write these values", () => WriteAgain(card, tableHost, actions, lang, again!));
                acts.Children.Add(again);
            }
            if (card.Groups.Any(g => g.AnyChangedBack))
            {
                if (actions.SelectGroup != null)
                    acts.Children.Add(CardKit.Ghost(ms ? "Tunjuk dalam Revit" : "Show in Revit", () => actions.SelectGroup(Current(card, tableHost))));
                leave = CardKit.Ghost(ms ? "Biarkan" : "Leave as is", () =>
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
                acts.Children.Add(CardKit.Secondary(ms ? "Buka sebagai grid" : "Open as grid", () =>
                    GridWindow.Show(ms ? "Perubahan Copilot" : "Copilot changes", ChangeTable.Columns(lang), ChangeTable.Rows(card, lang))));
                Button? copy = null;
                copy = CardKit.Secondary(ms ? "Salin jadual" : "Copy table", () =>
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
            CreatedCard.BuildElements(root, card, lang, actions);          // the server's rows and checks, no elements of ours: nothing drawn
        }

        private static void Findings(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions) =>
            FindingsBody.Build(root, card, lang, actions);

        private static void Selection(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions) =>
            SelectionBody.Selection(root, card, lang, actions);

        /// <summary>A result card of a turn that isolated the view, of any kind: the "isolated" line and "Reset isolate".
        /// (A selection card draws its own line with the others.)</summary>
        private static void IsolatedBlock(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions) =>
            SelectionBody.IsolatedBlock(root, card, lang, actions);

        /// <summary>The "Check N things" card and the inline "Next" row (the server's turn_end.result); drawn by ChecksCard.</summary>
        private static void ChecksAndNext(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions) =>
            ChecksCard.Add(root, card, lang, actions);

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
