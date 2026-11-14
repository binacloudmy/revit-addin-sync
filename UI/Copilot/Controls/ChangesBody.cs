#nullable enable
// The body of the Changes result card (design boards Main and Partial).
//   banner     "Check N values" when Revit read back something other than what was written (amber)
//   bar        Partial: ten segments, green for the types that hold, hatched amber for those put back
//   tiles      the types in a 2-column grid: ring, name, mono line, element count, a small arrow. One way in (result
//              marks design, rule 7): a tile click shows the type's elements in the model (zoom, bright marks, label);
//              its arrow also selects them in Revit ("Selected")
//   per type   one under the other, in the tiles' order (live 13 Nov, F-t04b: a list switched by a tile click showed one
//              type of two, and the switch did not happen in Revit), each:
//     check    Partial: a "Check N types" card when the type's values were put back:
//              "Changed back" chip, Parameter / Holds now / Written table, Write these values, Show in Revit, Leave as is
//     list     "Written as asked . N" ("For <type>" when there are several): one row per change (104px mono parameter,
//              value, source, "was ...", Change)
//   footnote   what can be clicked, on a Sunken strip, once under the lists
// All words and counts come from ChangesBodyText (pure, tested). Colours are Cp.* tokens only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    public static class ChangesBody
    {
        private const int MaxRows = 12;    // rows of the list before "Show N more"
        private const int MaxTiles = 8;    // tiles before "Show N more"

        private const string CheckIcon = "M3.5,8.5 L6.5,11.5 L12.5,4.5";
        private const string InfoIcon = "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 Z M8,7.3 V11.3 M8,4.9 V5.1";
        private const string WarnIcon = "M8,2.5 L14,13 H2 Z M8,6.5 V9.5 M8,11.3 V11.5";

        /// <summary>Draw the body of a Changes card into <paramref name="root"/> (the card's stack).</summary>
        public static void Build(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            // "No net change" has a headline and no groups: nothing to tabulate, and nothing to index.
            if (card.FirstGroup == null) return;
            new View(card, lang, actions).Into(root);
        }

        private sealed class Tile
        {
            public Border Box = null!;
            public System.Windows.Shapes.Ellipse Ring = null!;
            public TextBlock State = null!;
        }

        private sealed class View
        {
            private readonly ResultCardModel card;
            private readonly ReceiptLanguage lang;
            private readonly bool ms;
            private readonly TurnEvidenceActions actions;
            private readonly ContentControl host = new ContentControl();   // every type's check card and list
            private readonly List<Tile> tiles = new List<Tile>();
            private readonly TextBlock selLine;
            private int index;          // the type Open Type Properties acts on: the last tile clicked
            private int? shown;         // the type shown in the model from its tile (the card's current row)
            private int? selected;      // the type selected in Revit from its tile's arrow

            public View(ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
            {
                this.card = card; this.lang = lang; this.actions = actions; ms = lang == ReceiptLanguage.Ms;
                selLine = CardKit.Text("", 12, "Cp.Faint", margin: new Thickness(0, 2, 0, 0));
            }

            private TypeGroup Current => card.Groups[Math.Min(index, card.Groups.Count - 1)];

            public void Into(StackPanel root)
            {
                if (card.CheckCount > 0) root.Children.Add(Banner());
                if (ChangesBodyText.ShowBar(card.Groups.Count, card.ChangedBackTypes))
                    root.Children.Add(Bar(card.Groups.Count - card.ChangedBackTypes, card.ChangedBackTypes));
                root.Children.Add(TileSection());
                host.Content = Body();
                root.Children.Add(host);
                Buttons(root);
            }

            // ─── The amber banner over rows whose read-back differed ──────────────────────────────────────────────

            private FrameworkElement Banner()
            {
                var (title, hint) = ChangesBodyText.CheckBanner(card.CheckCount, lang);
                var banner = new Border { Padding = new Thickness(14, 8, 8, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
                banner.SetResourceReference(Border.BackgroundProperty, "Cp.AmberBg");
                banner.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                var dock = new DockPanel { LastChildFill = true };
                var keep = CardKit.Ghost(ms ? "Kekalkan semua" : "Keep all as written", () => banner.Visibility = Visibility.Collapsed);
                keep.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(keep, Dock.Right); dock.Children.Add(keep);
                var text = new StackPanel();
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(Icon(WarnIcon, "Cp.Amber", 16, 1.5, new Thickness(0, 0, 8, 0)));
                head.Children.Add(CardKit.Text(title, 13, "Cp.AmberFg", FontWeights.SemiBold));
                text.Children.Add(head);
                text.Children.Add(CardKit.Text(hint, 11.5, "Cp.AmberFg", margin: new Thickness(24, 2, 0, 0)));
                dock.Children.Add(text);
                banner.Child = dock;
                return banner;
            }

            // ─── Partial: the ten-segment bar ─────────────────────────────────────────────────────────────────────

            private FrameworkElement Bar(int held, int back)
            {
                var (green, amber) = ChangesBodyText.Segments(held, back);
                var box = new StackPanel { Margin = new Thickness(14, 12, 14, 0) };
                var grid = new Grid();
                for (var i = 0; i < ChangesBodyText.BarSegments; i++)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var seg = i < green ? Solid("Cp.Green") : Hatched();
                    seg.Margin = new Thickness(i == 0 ? 0 : 1.5, 0, i == ChangesBodyText.BarSegments - 1 ? 0 : 1.5, 0);
                    Grid.SetColumn(seg, i);
                    grid.Children.Add(seg);
                }
                box.Children.Add(grid);

                var (written, backText, total) = ChangesBodyText.BarLines(held, back, card.Groups.All(g => g.IsType), lang);
                var line = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = CardKit.Text("", 12, "Cp.TextSecondary");
                var w = new Run(written); w.SetResourceReference(TextElement.ForegroundProperty, "Cp.TextSecondary");
                var dot = new Run(" · "); dot.SetResourceReference(TextElement.ForegroundProperty, "Cp.Faint");
                var b = new Run(backText); b.SetResourceReference(TextElement.ForegroundProperty, "Cp.Amber");
                left.Inlines.Add(w); left.Inlines.Add(dot); left.Inlines.Add(b);
                line.Children.Add(left);
                var right = CardKit.Text(total, 11, "Cp.Faint", mono: true, wrap: false);
                Grid.SetColumn(right, 1);
                line.Children.Add(right);
                box.Children.Add(line);
                return box;
            }

            private static Border Solid(string brush)
            {
                var seg = new Border { Height = 8, CornerRadius = new CornerRadius(2) };
                seg.SetResourceReference(Border.BackgroundProperty, brush);
                return seg;
            }

            /// <summary>An amber hatch: 45 degree stripes of Cp.Amber on the dark amber wash.</summary>
            private static Border Hatched()
            {
                var seg = new Border { Height = 8, CornerRadius = new CornerRadius(2), ClipToBounds = true };
                seg.SetResourceReference(Border.BackgroundProperty, "Cp.AmberBg");
                var d = new StringBuilder();
                for (var x = -8; x <= 160; x += 4)
                    d.Append("M").Append(x.ToString(CultureInfo.InvariantCulture)).Append(",8 L").Append((x + 8).ToString(CultureInfo.InvariantCulture)).Append(",0 ");
                var stripes = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(d.ToString()), StrokeThickness = 1.6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Width = 160, Height = 8,
                };
                stripes.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Cp.Amber");
                seg.Child = stripes;
                return seg;
            }

            // ─── The type tiles ───────────────────────────────────────────────────────────────────────────────────

            private FrameworkElement TileSection()
            {
                var box = new StackPanel { Margin = new Thickness(14, 12, 14, 0) };
                var types = card.Groups.All(g => g.IsType);

                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
                var total = CardKit.Text(ChangesBodyText.CountLabel(card.InstanceCount, lang), 11.5, "Cp.Faint", wrap: false);
                DockPanel.SetDock(total, Dock.Right);
                head.Children.Add(total);
                head.Children.Add(CardKit.Text(ChangesBodyText.TilesHeader(types, lang).ToUpperInvariant(), 11, "Cp.Faint", FontWeights.SemiBold, wrap: false));
                box.Children.Add(head);

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var overflow = new List<UIElement>();
                var n = card.Groups.Count;
                // Two columns; a lone tile, or one whose name needs more than two lines at half width, takes the whole row.
                var layout = ChangesBodyText.TileLayout(card.Groups.Select(g => ChangesBodyText.TileLabels(g.Title).Title).ToList());
                for (var i = 0; i < n; i++)
                {
                    var (row, col, span) = layout[i];
                    while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var tile = MakeTile(i);
                    tiles.Add(tile);
                    var box1 = tile.Box;
                    Grid.SetRow(box1, row);
                    Grid.SetColumn(box1, col);
                    Grid.SetColumnSpan(box1, span);
                    if (i >= MaxTiles) { box1.Visibility = Visibility.Collapsed; overflow.Add(box1); }
                    grid.Children.Add(box1);
                }
                box.Children.Add(grid);
                if (overflow.Count > 0)
                {
                    var more = CardKit.Text(ms ? "Tunjuk " + overflow.Count + " lagi" : "Show " + overflow.Count + " more", 11, "Cp.Faint", margin: new Thickness(0, 0, 0, 4));
                    more.TextDecorations = TextDecorations.Underline; more.Cursor = System.Windows.Input.Cursors.Hand;
                    more.MouseLeftButtonUp += (_, __) => { foreach (var o in overflow) o.Visibility = Visibility.Visible; more.Visibility = Visibility.Collapsed; };
                    box.Children.Add(more);
                }
                RepaintTiles();
                if (actions.ShowGroup != null || actions.SelectGroup != null) box.Children.Add(selLine);
                return box;
            }

            private Tile MakeTile(int i)
            {
                var g = card.Groups[i];
                var (title, mono) = ChangesBodyText.TileLabels(g.Title);
                var t = new Tile();
                t.Box = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 11), Margin = new Thickness(0, 0, 0, 8), MinWidth = 0 };
                var sp = new StackPanel();
                t.Box.Child = sp;

                var top = new Grid();
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                t.Ring = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
                top.Children.Add(t.Ring);
                // Breaks only after "_", "-", ": " or a space, and keeps the type's own name whole (ChangesBodyText.TileText).
                var name = CardKit.Text(ChangesBodyText.TileText(title), 13, "Cp.Ink", FontWeights.SemiBold);
                name.ToolTip = g.Title;
                Grid.SetColumn(name, 1);
                top.Children.Add(name);
                sp.Children.Add(top);

                if (mono != null)
                {
                    var m = CardKit.Text(mono, 10.5, "Cp.Faint", mono: true, wrap: false, margin: new Thickness(0, 4, 0, 0));
                    m.TextTrimming = TextTrimming.CharacterEllipsis; m.ToolTip = mono;
                    sp.Children.Add(m);
                }

                var bottom = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                bottom.Children.Add(CardKit.Text(ChangesBodyText.TileCount(g, lang), 12, "Cp.Muted"));
                t.State = CardKit.Text("", 11, "Cp.Faint", FontWeights.SemiBold);
                t.State.VerticalAlignment = VerticalAlignment.Center;
                var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                right.Children.Add(t.State);
                var idx = i;
                if (actions.ShowGroup != null) right.Children.Add(CardRows.Arrow(() => Pick(idx, select: true)));
                Grid.SetColumn(right, 1);
                bottom.Children.Add(right);
                sp.Children.Add(bottom);

                if (actions.ShowGroup != null && actions.Rows != null)
                    actions.Rows.Add(t.Box, Highlights.MarkFocus.ForTypeGroup(g), () => Pick(idx, select: false), () => Pick(idx, select: true),
                        on => { if (on) shown = idx; else if (shown == idx) shown = null; RepaintTiles(); },
                        note => { if (idx < tiles.Count) { tiles[idx].State.Text = note.Length > 0 ? note : ChangesBodyText.SelectLabel(selected == idx, lang); } });
                else if (actions.SelectGroup != null)
                {
                    t.Box.Cursor = System.Windows.Input.Cursors.Hand;
                    t.Box.MouseLeftButtonUp += (_, __) => Pick(idx, select: true);
                }
                return t;
            }

            /// <summary>A tile clicked: show that type's elements in the model; its arrow also selects them in Revit. It becomes what
            /// Open Type Properties acts on. The tile shows it first: a failure on the way to Revit never leaves the click unseen.</summary>
            private void Pick(int i, bool select)
            {
                index = i;
                if (select) selected = i;
                RepaintTiles();
                try
                {
                    if (actions.ShowGroup != null) actions.ShowGroup(Highlights.MarkFocus.ForTypeGroup(card.Groups[i]), select);
                    else if (select) actions.SelectGroup?.Invoke(card.Groups[i]);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] show type: " + ex.Message); }
            }

            private static bool Flagged(TypeGroup g) => (g.AnyChangedBack && !g.LeftAsIs) || g.Rows.Any(r => r.NeedsCheck);

            private void RepaintTiles()
            {
                for (var i = 0; i < tiles.Count && i < card.Groups.Count; i++)
                {
                    var t = tiles[i];
                    // Selected in Revit: Revit's blue (it is Revit's selection). Shown in the model: raised, with a darker edge.
                    var on = selected == i; var focus = shown == i;
                    t.Box.SetResourceReference(Border.BorderBrushProperty, on ? "Cp.Run.Info" : focus ? "Cp.Muted" : "Cp.Hair2");
                    t.Box.SetResourceReference(Border.BackgroundProperty, on || focus ? "Cp.Raised" : "Cp.Bg");
                    t.Ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on ? "Cp.Run.Info" : Flagged(card.Groups[i]) ? "Cp.Amber" : "Cp.Green");
                    t.State.Text = on ? ChangesBodyText.SelectLabel(true, lang) : "";
                    t.State.SetResourceReference(TextBlock.ForegroundProperty, on ? "Cp.Run.Info" : "Cp.Faint");
                }
                selLine.Text = ChangesBodyText.SelectLine(selected is int s && s < card.Groups.Count ? card.Groups[s].Selectable : (int?)null, lang);
            }

            // ─── Every type, one under the other: its check card and its list; then the footnote ─────────────────

            private FrameworkElement Body()
            {
                var sp = new StackPanel();
                foreach (var part in ChangesBodyText.Lists(card.Groups))
                {
                    if (part.CheckCard) sp.Children.Add(CheckCard(part.Group));
                    if (part.Written.Count > 0) sp.Children.Add(WrittenList(part.Group, part.Written, part.NamesType));
                }
                var strip = Footnote();
                strip.Margin = new Thickness(0, 12, 0, 0);
                sp.Children.Add(strip);
                return sp;
            }

            // ─── Partial: the "Check N types" card ────────────────────────────────────────────────────────────────

            private FrameworkElement CheckCard(TypeGroup g)
            {
                var attention = !g.LeftAsIs;
                var box = CardKit.Card(attention ? "Cp.AmberLn" : "Cp.Line", "Cp.Bg", new Thickness(12, 12, 12, 0), 10);
                var stack = new StackPanel();
                box.Child = stack;

                var head = new Border { Padding = new Thickness(14, 10, 14, 10) };
                head.SetResourceReference(Border.BackgroundProperty, attention ? "Cp.AmberBg" : "Cp.Raised");
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                title.Children.Add(Icon(attention ? WarnIcon : CheckIcon, attention ? "Cp.Amber" : "Cp.Muted", 16, 1.5, new Thickness(0, 0, 8, 0)));
                title.Children.Add(CardKit.Text(attention
                    ? ChangesBodyText.CheckTitle(Math.Max(1, ChangesBodyText.NeedAttention(card.Groups)), lang)
                    : ChangesBodyText.ChipText(g.ChangedBack, true, lang), 13, attention ? "Cp.AmberFg" : "Cp.Ink", FontWeights.SemiBold));
                head.Child = title;
                stack.Children.Add(head);

                var body = new StackPanel { Margin = new Thickness(14) };
                stack.Children.Add(body);

                var top = new Grid();
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var names = new StackPanel();
                var (name, mono) = ChangesBodyText.TileLabels(g.Title);
                names.Children.Add(CardKit.Text(name, 14, "Cp.Ink", FontWeights.SemiBold));
                if (mono != null) names.Children.Add(CardKit.Text(mono, 11, "Cp.Faint", mono: true));
                top.Children.Add(names);
                if (attention)
                {
                    var chip = new Border { Height = 22, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                    chip.SetResourceReference(Border.BackgroundProperty, "Cp.AmberBg");
                    var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    row.Children.Add(Icon(CardKit.Icons.Undo, "Cp.Amber", 11, 1.8, new Thickness(0, 0, 5, 0)));
                    row.Children.Add(CardKit.Text(ChangesBodyText.ChipText(g.ChangedBack, false, lang), 11.5, "Cp.Amber", FontWeights.Medium, wrap: false));
                    chip.Child = row;
                    Grid.SetColumn(chip, 1);
                    top.Children.Add(chip);
                }
                body.Children.Add(top);

                if (attention) body.Children.Add(CardKit.Text(ChangesBodyText.BackNote(g.ChangedBack, lang), 13, "Cp.TextSecondary", margin: new Thickness(0, 12, 0, 0)));
                body.Children.Add(CheckTable(g));

                if (attention)
                {
                    var acts = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                    if (actions.EditValue != null && g.CanWriteAgain)
                    {
                        Button? again = null;
                        again = CardKit.Secondary(ms ? "Tulis nilai ini" : "Write these values", () => WriteAgain(g, again!));
                        acts.Children.Add(again);
                    }
                    if (actions.SelectGroup != null)
                        acts.Children.Add(CardKit.Secondary(ms ? "Tunjuk dalam Revit" : "Show in Revit", () => actions.SelectGroup(g), CardKit.Icons.Eye));
                    acts.Children.Add(CardKit.Ghost(ms ? "Biarkan" : "Leave as is", () =>
                    {
                        g.LeftAsIs = true;
                        card.RefreshHeadline(lang);      // the "N changed back" count follows
                        RepaintTiles();
                        host.Content = Body();
                        actions.Redraw?.Invoke();        // the header is drawn once: have the pane draw the card again
                    }));
                    body.Children.Add(acts);
                }
                return box;
            }

            /// <summary>Parameter | Holds now | Written (the value the type was given, as the source says it). Click a written
            /// value to open its source under the row.</summary>
            private FrameworkElement CheckTable(TypeGroup g)
            {
                var rows = g.Rows.Where(r => r.ChangedBack).ToList();
                var frame = CardKit.Card("Cp.Line", "Cp.Bg", new Thickness(0, 12, 0, 0), 8);
                var grid = new Grid();
                frame.Child = grid;
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var headers = ChangesBodyText.CheckHeaders(rows, lang);
                for (var c = 0; c < 3; c++)
                {
                    var h = new Border { Padding = new Thickness(10, 7, 10, 7) };
                    h.SetResourceReference(Border.BackgroundProperty, "Cp.Raised");
                    h.Child = CardKit.Text(headers[c].ToUpperInvariant(), 11, "Cp.Faint", FontWeights.SemiBold, wrap: false);
                    Grid.SetRow(h, 0); Grid.SetColumn(h, c); grid.Children.Add(h);
                }

                var r = 1;
                foreach (var row in rows)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var param = CardKit.Text(row.Param, 11, "Cp.Muted", mono: true);
                    TableCell(grid, r, 0, param);

                    var now = string.IsNullOrEmpty(row.After)
                        ? CardKit.Text(ms ? "kosong" : "empty", 12.5, "Cp.Faint", italic: true)
                        : CardKit.Text(row.After!, 12.5, "Cp.Ink", FontWeights.SemiBold);
                    TableCell(grid, r, 1, now);

                    var wrote = CardKit.Text(row.IntendedText, 12, "Cp.Muted");
                    var detail = SourcePanel.Build(row, lang, actions, null);
                    detail.Visibility = Visibility.Collapsed;
                    detail.Margin = new Thickness(8, 0, 8, 6);
                    Grid.SetRow(detail, r + 1); Grid.SetColumnSpan(detail, 3); grid.Children.Add(detail);
                    SourceLink(wrote, row, detail);
                    TableCell(grid, r, 2, wrote);
                    r += 2;
                }
                return frame;
            }

            private static void TableCell(Grid grid, int row, int col, FrameworkElement content)
            {
                var cell = new Border { Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(0, 1, 0, 0), Child = content };
                cell.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                Grid.SetRow(cell, row); Grid.SetColumn(cell, col); grid.Children.Add(cell);
            }

            /// <summary>Make a text open or close the source panel under its row.</summary>
            private void SourceLink(TextBlock text, ChangeRow row, FrameworkElement detail)
            {
                text.Cursor = System.Windows.Input.Cursors.Hand;
                text.Background = System.Windows.Media.Brushes.Transparent;   // the whole line is the click target, not only its glyphs
                text.ToolTip = (ms ? "Sumber: " : "Source: ") + (string.IsNullOrEmpty(row.Source) ? (ms ? "Prompt anda" : "Your prompt") : row.Source);
                text.MouseEnter += (_, __) => text.TextDecorations = TextDecorations.Underline;
                text.MouseLeave += (_, __) => text.TextDecorations = null;
                text.MouseLeftButtonUp += (_, e) =>
                {
                    detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                    if (detail.Visibility == Visibility.Visible) CardKit.Reveal(detail);   // it opens under the row: bring it into view
                    e.Handled = true;
                };
            }

            /// <summary>Write a type's put-back values again: one edit per row, the same path as clicking a value (units
            /// checked, one more batch of the turn). Only rows with one value to write are sent; stops at the first that fails.</summary>
            private async void WriteAgain(TypeGroup g, Button chip)
            {
                try
                {
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
                    RepaintTiles();
                    host.Content = Body();   // the values moved from the check card into the list
                }
                catch (Exception ex)
                {
                    // async void: nothing may escape into the dispatcher.
                    System.Diagnostics.Debug.WriteLine("[BINA] write again: " + ex.Message);
                    try { chip.IsEnabled = true; } catch { /* the card is gone */ }
                }
            }

            // ─── "Written as asked . N": one row per change ───────────────────────────────────────────────────────

            private FrameworkElement WrittenList(TypeGroup g, List<ChangeRow> rows, bool namesType)
            {
                var outer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 12, 0, 0) };
                outer.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                var sp = new StackPanel();
                outer.Child = sp;

                var head = new Border { Padding = new Thickness(14, 10, 14, 10) };
                head.SetResourceReference(Border.BackgroundProperty, "Cp.Raised");
                var dock = new StackPanel();
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                title.Children.Add(Icon(CheckIcon, "Cp.Green", 16, 2, new Thickness(0, 0, 8, 0)));
                title.Children.Add(CardKit.Text(ChangesBodyText.WrittenAsAsked(rows.Count, lang), 13, "Cp.Ink", FontWeights.SemiBold, wrap: false));
                dock.Children.Add(title);
                if (namesType)
                {
                    // Which type these rows were written to, in full and wrapped: two families' types can share a name and
                    // differ only at the end of the family's name, so an ellipsis could make two lists read the same (F-t04).
                    var who = CardKit.Text(ChangesBodyText.WrittenFor(g.Title, lang), 11.5, "Cp.Muted", margin: new Thickness(24, 2, 0, 0));
                    dock.Children.Add(who);
                }
                head.Child = dock;
                sp.Children.Add(head);

                var overflow = new List<UIElement>();
                for (var i = 0; i < rows.Count; i++)
                {
                    var line = Row(g, rows[i]);
                    if (i >= MaxRows) { line.Visibility = Visibility.Collapsed; overflow.Add(line); }
                    sp.Children.Add(line);
                }
                if (overflow.Count > 0)
                {
                    var strip = new Border { Padding = new Thickness(14, 8, 14, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
                    strip.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                    var more = CardKit.Text(ms ? "Tunjuk " + overflow.Count + " lagi" : "Show " + overflow.Count + " more", 11.5, "Cp.Faint");
                    more.TextDecorations = TextDecorations.Underline; more.Cursor = System.Windows.Input.Cursors.Hand;
                    more.MouseLeftButtonUp += (_, __) => { foreach (var o in overflow) o.Visibility = Visibility.Visible; strip.Visibility = Visibility.Collapsed; };
                    strip.Child = more;
                    sp.Children.Add(strip);
                }
                return outer;
            }

            /// <summary>One change: a 104px mono parameter, the value (click to edit), what it replaced, its source, "Change".</summary>
            private FrameworkElement Row(TypeGroup g, ChangeRow row)
            {
                var outer = new Border { BorderThickness = new Thickness(0, 1, 0, 0) };
                outer.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                var sp = new StackPanel();
                outer.Child = sp;

                var grid = new Grid { Margin = new Thickness(14, 10, 8, 10) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var param = CardKit.Text("", 11.5, "Cp.Muted", mono: true, margin: new Thickness(0, 1, 0, 0));
                if (row.NeedsCheck) { var dot = new Run("● "); dot.SetResourceReference(TextElement.ForegroundProperty, "Cp.Amber"); param.Inlines.Add(dot); }
                param.Inlines.Add(new Run(row.Param));
                Grid.SetColumn(param, 0); grid.Children.Add(param);

                var mid = new StackPanel { Margin = new Thickness(10, 0, 10, 0) };
                var value = ValueCell(g, row);
                mid.Children.Add(value);
                var was = ChangesBodyText.Was(row.Before, lang);
                if (was.Length > 0) mid.Children.Add(CardKit.Text(was, 11.5, "Cp.Faint", margin: new Thickness(0, 2, 0, 0)));
                var src = CardKit.Text(string.IsNullOrEmpty(row.Source) ? (ms ? "Prompt anda" : "Your prompt") : Short(row.Source, 40), 11.5, "Cp.Faint", margin: new Thickness(0, 2, 0, 0));
                mid.Children.Add(src);
                Grid.SetColumn(mid, 1); grid.Children.Add(mid);

                var canEdit = actions.EditValue != null && row.Ids.Count > 0;
                if (canEdit)
                {
                    var change = CardKit.Ghost(ChangesBodyText.Change(lang), () => (value.Tag as Action)?.Invoke());
                    change.Content = new TextBlock { Text = ChangesBodyText.Change(lang), TextDecorations = TextDecorations.Underline };
                    change.FontSize = 12; change.MinHeight = 26; change.Padding = new Thickness(8, 0, 8, 0); change.Margin = new Thickness(0);
                    change.VerticalAlignment = VerticalAlignment.Top;
                    Grid.SetColumn(change, 2); grid.Children.Add(change);
                }
                sp.Children.Add(grid);

                // The source viewer: under the row, with the value's trail.
                var detail = SourcePanel.Build(row, lang, actions, canEdit ? () => (value.Tag as Action)?.Invoke() : (Action?)null);
                detail.Visibility = Visibility.Collapsed;
                detail.Margin = new Thickness(14, 0, 14, 10);
                sp.Children.Add(detail);
                SourceLink(src, row, detail);
                return outer;
            }

            /// <summary>The value; click it to type a new one (units checked, then written as one more batch of the turn).</summary>
            private ContentControl ValueCell(TypeGroup g, ChangeRow row)
            {
                var cell = new ContentControl();
                void Show()
                {
                    var v = string.IsNullOrEmpty(row.After)
                        ? CardKit.Text(ms ? "kosong" : "empty", 13, "Cp.Faint", italic: true)
                        : CardKit.Text(row.After!, 13, "Cp.Ink", FontWeights.Medium);
                    if (actions.EditValue != null && row.Ids.Count > 0)
                    {
                        v.Cursor = System.Windows.Input.Cursors.IBeam;
                        v.ToolTip = ms ? "Klik untuk ubah" : "Click to edit";
                        v.MouseLeftButtonUp += (_, __) => Edit(null);
                    }
                    cell.Content = v;
                }
                void Edit(string? err)
                {
                    var box = new TextBox { Text = row.After ?? "", FontSize = 12.5, Padding = new Thickness(3, 1, 3, 1), MinWidth = 60 };
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
                    cell.Content = sp;
                }
                Show();
                cell.Tag = (Action)(() => { if (actions.EditValue != null && row.Ids.Count > 0) Edit(null); });
                return cell;
            }

            // ─── The footnote strip ───────────────────────────────────────────────────────────────────────────────

            private Border Footnote()
            {
                var strip = new Border { Padding = new Thickness(14, 10, 14, 10), BorderThickness = new Thickness(0, 1, 0, 0) };
                strip.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
                strip.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var icon = Icon(InfoIcon, "Cp.Muted", 16, 1.5, new Thickness(0, 1, 0, 0));
                icon.VerticalAlignment = VerticalAlignment.Top;
                grid.Children.Add(icon);
                var text = CardKit.Text(ChangesBodyText.Footnote(actions.EditValue != null, card.Groups.Any(x => x.AnyChangedBack), lang),
                    12, "Cp.Muted", margin: new Thickness(8, 0, 0, 0));
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                strip.Child = grid;
                return strip;
            }

            // ─── The action bar ───────────────────────────────────────────────────────────────────────────────────

            private void Buttons(StackPanel root)
            {
                var allRows = card.Groups.SelectMany(g => g.Rows).ToList();
                ResultCardView.CommitWarnings(root, card, lang, actions);
                var acts = ResultCardView.Actions(root);
                // Show (zoom) and select live on each type's tile; "Hide marks" / "Show in model" is the card's section header.
                if (actions.OpenTypeProperties != null && card.Groups.Any(g => g.IsType))
                    acts.Children.Add(CardKit.Secondary(ms ? "Buka Type Properties" : "Open Type Properties", () => actions.OpenTypeProperties(Current)));
                if (allRows.Count > 0)
                {
                    acts.Children.Add(CardKit.Secondary(ms ? "Buka sebagai grid" : "Open as grid", () =>
                        GridWindow.Show(ms ? "Perubahan Copilot" : "Copilot changes", ChangeTable.Columns(lang), ChangeTable.Rows(card, lang)), CardKit.Icons.Grid));
                    Button? copy = null;
                    copy = CardKit.Secondary(ms ? "Salin jadual" : "Copy table", async () =>
                    {
                        // Built and set off the UI thread: the plain clipboard call held Revit for seconds when another program had the clipboard.
                        var ok = await ClipboardKit.CopyAsync(() => ChangeTable.TabText(card), "copy-table", card.ChangeCount);
                        if (ok && copy != null) ItemsCard.SetLabel(copy, ms ? "Disalin" : "Copied");
                    }, CardKit.Icons.Copy);
                    acts.Children.Add(copy);
                }
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A line icon drawn in a 16x16 box, scaled to <paramref name="size"/>.</summary>
        private static FrameworkElement Icon(string data, string brush, double size, double stroke, Thickness? margin = null)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data), Width = 16, Height = 16, StrokeThickness = stroke,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
            var box = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) };
            if (Math.Abs(size - 16) > 0.01) path.RenderTransform = new ScaleTransform(size / 16, size / 16);
            box.Children.Add(path);
            return box;
        }

        private static string Short(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : "…" + s.Substring(s.Length - n + 1);
    }
}
