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

            // The block, top to bottom (design boards Main/Partial/Selection): the headline with Undo beside it (result marks
            // design, rule 8: Undo next to what it undoes), the status chips with the "Steps" toggle, then the card (body only,
            // neutral border), then ONE action row (Undo last part, Redo, Save as command).
            var block = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            block.Children.Add(HeadlineRow(card, actions, canUndo, undone, undoOff));
            var chips = StatusChips(card, ms, actions);
            if (chips != null) block.Children.Add(chips);
            // A rule-set result's "4 kinds of issue" line is the Rules block's own header: not repeated above it.
            if ((card.Kind != ResultKind.Changes || card.Groups.Count == 0) && !string.IsNullOrEmpty(card.Detail) && !(card.Kind == ResultKind.Findings && actions.RulesBlock != null))
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

            var acts = ActionRow(card, ms, actions, canUndo, undone, undoOff);
            if (acts != null) block.Children.Add(acts);
            return block;
        }

        /// <summary>The headline, outside the card: a 22px check disc (amber when something needs a look), the one
        /// sentence at 17px/600 (design: "Copilot" label above, chips below), and Undo on the right: it undoes the whole
        /// turn the sentence names. When Undo is off (the model changed after the turn) it stays, greyed, and says why.</summary>
        private static FrameworkElement HeadlineRow(ResultCardModel card, TurnEvidenceActions actions, bool canUndo, bool undone, string? undoOff)
        {
            var warn = card.Kind == ResultKind.Findings || card.Mismatches > 0;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button? undo = null;
            if (actions.Undo != null && canUndo && !undone)
            {
                undo = CardKit.Secondary("Undo", actions.Undo, CardKit.Icons.Undo, compact: true);
                undo.ToolTip = "Undo this turn: " + CardText.Headline(card.Headline);
            }
            // A write committed but Undo is off (something else changed the model after it): the button is still there, greyed, and says why.
            else if (actions.Undo != null && undoOff != null && !undone)
            {
                var off = CardKit.Secondary("Undo", () => { }, CardKit.Icons.Undo, compact: true);
                off.IsEnabled = false;
                off.ToolTip = undoOff;
                ToolTipService.SetShowOnDisabled(off, true);
                undo = off;
            }
            if (undo != null)
            {
                undo.Margin = new Thickness(10, 0, 0, 0);
                undo.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(undo, 2);
                row.Children.Add(undo);
            }
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
            // The whole sentence, wrapped over as many lines as it needs: no line limit, no ellipsis (live R7, C3).
            var text = CardKit.Text(CardText.Headline(card.Headline), 17, "Cp.Ink", FontWeights.SemiBold);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            if (card.Headline.Length > CardText.Headline(card.Headline).Length) text.ToolTip = card.Headline;
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
                // "Read back from Revit: 2 of 2 types match · affects 52 Lighting Fixtures": in the unit that was written.
                var text = card.ReadBackText(ms ? ReceiptLanguage.Ms : ReceiptLanguage.En);
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
                if (open) CardKit.Reveal(panel);
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

        /// <summary>A status chip: icon and words. The words wrap onto a second line when the pane is narrower than they are,
        /// never clipped at its edge (live 13 Nov, F-t04b: "… affects 52 Lig" at about 400 px). The WrapPanel measures the chip
        /// at the pane's width, the star column hands that width to the text, and the chip grows to the lines it needs.</summary>
        private static FrameworkElement Chip(string bg, string fg, string text, string icon)
        {
            var b = new Border { MinHeight = 24, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 9, 4), Margin = new Thickness(0, 0, 6, 6) };
            b.SetResourceReference(Border.BackgroundProperty, bg);
            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var path = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(icon), Width = 14, Height = 14, Stretch = System.Windows.Media.Stretch.Uniform, StrokeThickness = 1.5,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                Margin = new Thickness(0, 1, 5, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, fg);
            row.Children.Add(path);
            var words = CardKit.Text(text, 11.5, fg, FontWeights.Medium, wrap: true);
            words.TextTrimming = TextTrimming.None;
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            b.Child = row;
            return b;
        }

        /// <summary>The one action row below the card: the last part / Redo when they apply, Export, Save as command. Undo of
        /// the whole turn is on the headline row.</summary>
        private static FrameworkElement? ActionRow(ResultCardModel card, bool ms, TurnEvidenceActions actions, bool canUndo, bool undone, string? undoOff)
        {
            bool Ok(Func<bool>? f) { try { return f?.Invoke() ?? false; } catch { return false; } }
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            {
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
            ChangesBody.Build(root, card, lang, actions);
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

        /// <summary>AI-332: Revit's warnings on the commit (walls overlap, identical instances in the
        /// same place…) under an amber banner, one row per warning with Show for its elements.</summary>
        internal static void CommitWarnings(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            if (card.Warnings.Count == 0) return;
            var ms = lang == ReceiptLanguage.Ms;
            var n = card.Warnings.Count.ToString(CultureInfo.InvariantCulture);
            var banner = new Border { Padding = new Thickness(12, 6, 10, 6), BorderThickness = new Thickness(0, 1, 0, 1) };
            banner.SetResourceReference(Border.BackgroundProperty, "Cp.AmberBg");
            banner.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            var sp = new StackPanel();
            sp.Children.Add(CardKit.Text(ms ? "Revit memberi " + n + " amaran" : "Revit raised " + n + (card.Warnings.Count == 1 ? " warning" : " warnings"), 12, "Cp.AmberFg", FontWeights.SemiBold));
            sp.Children.Add(CardKit.Text(ms ? "Perubahan disimpan. Semak amaran di bawah dalam model."
                                            : "The change was saved. Check what Revit flagged below in the model.", 11, "Cp.AmberFg"));
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var r = 0;
            foreach (var w in card.Warnings.Take(MaxRows))
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var title = CardKit.Text("", 11.5, "Cp.Ink", margin: new Thickness(0, 3, 10, 3));
                var dot = new Run("● "); dot.SetResourceReference(TextElement.ForegroundProperty, "Cp.Amber");
                title.Inlines.Add(dot); title.Inlines.Add(new Run(w.Title));
                Cell(grid, r, 0, title);
                Cell(grid, r, 1, CardKit.Text(w.Ids.Count > 0 ? w.Ids.Count.ToString(CultureInfo.InvariantCulture) : "", 11.5, "Cp.Ink", wrap: false, margin: new Thickness(0, 3, 10, 3)));
                if (w.Ids.Count > 0 && actions.ShowElements != null)
                {
                    var ids = w.Ids.ToArray(); var show = CardKit.Ghost(ms ? "Tunjuk" : "Show", () => actions.ShowElements(ids), compact: true);
                    show.Margin = new Thickness(0, 0, 0, 2); Cell(grid, r, 2, show);
                }
                r++;
            }
            if (card.Warnings.Count > MaxRows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var more = CardKit.Text("+ " + (card.Warnings.Count - MaxRows) + (ms ? " lagi" : " more"), 11, "Cp.Faint");
                Grid.SetRow(more, r); Grid.SetColumnSpan(more, 3); grid.Children.Add(more);
            }
            sp.Children.Add(grid);
            banner.Child = sp;
            root.Children.Add(banner);
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

        private static void Cell(Grid grid, int row, int col, FrameworkElement el)
        {
            Grid.SetRow(el, row); Grid.SetColumn(el, col); grid.Children.Add(el);
        }

        private static string Short(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : "…" + s.Substring(s.Length - n + 1);
    }
}
