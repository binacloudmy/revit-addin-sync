#nullable enable
// The body of a selection / isolate result card (Selection board). From the top:
//   a stacked bar, h10, gap 2: selected and in view (Cp.Run.Info, the blue), selected but not visible (Cp.Amber),
//   the rest the isolate hides (Cp.Run.Raised, grey; only when a count is known), then a legend of dot, label, count (12px);
//   "Selected . N types" header (Cp.Raised) with the type rows (min-height 54: ring, mono type name, count, magnifier;
//   a click zooms to the type) and an info footnote (Cp.Sunken), when the card has type groups;
//   the button Reset isolate (Secondary). No marks in the model: Revit's own selection is the mark.
// The words and the segment counts come from SelectionBarText and SelectionLines (pure, tested); this only draws them.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    public static class SelectionBody
    {
        private const double BarHeight = 10;
        private const double BarGap = 2;
        private const double RowMinHeight = 54;

        // A selection card: the bar and legend, the type rows when there are groups, and the buttons.
        public static void Selection(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var ms = lang == ReceiptLanguage.Ms;
            var types = SelectionBarText.Types(card);

            var top = new StackPanel { Margin = new Thickness(14, 14, 14, 14) };
            var segments = SelectionBarText.Segments(card.Selected, card.VisibleInView, card.Isolated, lang);
            if (segments.Count > 0)
            {
                top.Children.Add(Bar(segments));
                top.Children.Add(Legend(segments));
            }
            if (types.Count == 0)
            {
                // No type groups: the bar and legend above the words the card always had.
                var lines = SelectionLines.For(card, lang);
                if (lines.Count > 0)
                {
                    var body = new StackPanel { Margin = new Thickness(0, segments.Count > 0 ? 12 : 0, 0, 0) };
                    foreach (var l in lines) body.Children.Add(CardKit.Text(l, 11.5, "Cp.Ink2", margin: new Thickness(0, 1, 0, 1)));
                    top.Children.Add(body);
                }
            }
            if (top.Children.Count > 0) root.Children.Add(top);

            if (types.Count > 0) root.Children.Add(TypesBlock(card, types, lang, actions));

            var acts = ResultCardView.Actions(root);
            // No marks for a selection: Revit's own (blue) selection is its mark (result marks design, rule 3).
            AddResetIsolate(acts, card, lang, actions);
        }

        /// <summary>A result card of a turn that isolated the view, of any kind: the "isolated" line and "Reset isolate".
        /// (A selection card draws its own line with the others.)</summary>
        public static void IsolatedBlock(StackPanel root, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var strip = new StackPanel { Margin = new Thickness(14, 10, 14, 8) };
            foreach (var l in SelectionLines.IsolatedLines(lang)) strip.Children.Add(CardKit.Text(l, 11.5, "Cp.Ink2", margin: new Thickness(0, 1, 0, 1)));
            root.Children.Add(strip);
            AddResetIsolate(ResultCardView.Actions(root), card, lang, actions);
        }

        private static void AddResetIsolate(WrapPanel acts, ResultCardModel card, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            if (!card.Isolated || actions.ResetIsolate == null) return;
            Button? reset = null;
            reset = CardKit.Secondary(SelectionLines.ResetLabel(lang), async () =>
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
            }, CardKit.Icons.Undo);
            acts.Children.Add(reset);
        }

        // ── The bar and its legend ────────────────────────────────────────────────────────────────────────────────

        private static string BrushFor(SelectionSegmentKind kind) =>
            kind == SelectionSegmentKind.InView ? "Cp.Run.Info" : kind == SelectionSegmentKind.NotVisible ? "Cp.Amber" : "Cp.Run.Raised";

        private static FrameworkElement Bar(System.Collections.Generic.List<SelectionSegment> segments)
        {
            var grid = new Grid { Height = BarHeight };
            for (var i = 0; i < segments.Count; i++)
            {
                if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarGap) });
                var seg = segments[i];
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(seg.Count, GridUnitType.Star),
                    MinWidth = seg.Kind == SelectionSegmentKind.NotVisible ? 5 : 0,
                });
                var first = i == 0; var last = i == segments.Count - 1;
                var piece = new Border { CornerRadius = new CornerRadius(first ? 3 : 0, last ? 3 : 0, last ? 3 : 0, first ? 3 : 0) };
                piece.SetResourceReference(Border.BackgroundProperty, BrushFor(seg.Kind));
                Grid.SetColumn(piece, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(piece);
            }
            return grid;
        }

        private static FrameworkElement Legend(System.Collections.Generic.List<SelectionSegment> segments)
        {
            var box = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var row = new Grid { Margin = new Thickness(0, i == 0 ? 0 : 5, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Border.BackgroundProperty, BrushFor(seg.Kind));
                if (seg.Kind == SelectionSegmentKind.Rest)
                {
                    // The grey swatch carries a 1px outline (14% white) so it shows on the card.
                    dot.BorderThickness = new Thickness(1);
                    dot.SetResourceReference(Border.BorderBrushProperty, "Cp.Hair2");
                }
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                var label = CardKit.Text(seg.Label, 12, "Cp.Run.Text2");
                Grid.SetColumn(label, 1);
                row.Children.Add(label);

                var count = CardKit.Text(seg.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), 11, "Cp.Muted", wrap: false, mono: true);
                count.Margin = new Thickness(8, 0, 0, 0);
                count.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(count, 2);
                row.Children.Add(count);
                box.Children.Add(row);
            }
            return box;
        }

        // ── The type rows ─────────────────────────────────────────────────────────────────────────────────────────

        private static FrameworkElement TypesBlock(ResultCardModel card, System.Collections.Generic.List<TypeGroup> types, ReceiptLanguage lang, TurnEvidenceActions actions)
        {
            var onClick = ClickFor(actions);
            var block = new StackPanel();

            // Header: dashed square icon, "Selected . N types", the element count at the right.
            var head = new Grid { Margin = new Thickness(14, 10, 14, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = SelectedIcon();
            icon.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(icon, 0);
            head.Children.Add(icon);
            var title = CardKit.Text(SelectionBarText.TypesHeader(types.Count, lang), 13, "Cp.Ink", FontWeights.SemiBold, wrap: false);
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1);
            head.Children.Add(title);
            var total = card.Selected > 0 ? card.Selected : SumOf(types);
            var totalText = CardKit.Text(SelectionBarText.TypesTotal(total, lang), 11.5, "Cp.Muted", wrap: false);
            totalText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(totalText, 2);
            head.Children.Add(totalText);
            var headBox = new Border { Child = head, BorderThickness = new Thickness(0, 1, 0, 0) };
            headBox.SetResourceReference(Border.BackgroundProperty, "Cp.Raised");
            headBox.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            block.Children.Add(headBox);

            foreach (var g in types) block.Children.Add(TypeRow(g, onClick));

            var notes = SelectionBarText.Footnote(card, lang, onClick != null);
            if (notes.Count > 0) block.Children.Add(Footnote(notes));
            return block;
        }

        private static int SumOf(System.Collections.Generic.List<TypeGroup> types)
        {
            var n = 0;
            foreach (var g in types) n += SelectionBarText.RowCount(g);
            return n;
        }

        private static Action<TypeGroup>? ClickFor(TurnEvidenceActions actions) => actions.ZoomGroup ?? actions.SelectGroup;

        private static FrameworkElement TypeRow(TypeGroup g, Action<TypeGroup>? onClick)
        {
            var grid = new Grid { Margin = new Thickness(14, 8, 12, 8), VerticalAlignment = VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });

            // Ring: 12px circle, 2px border in the info blue.
            var ring = new Ellipse { Width = 12, Height = 12, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center };
            ring.SetResourceReference(Shape.StrokeProperty, "Cp.Run.Info");
            Grid.SetColumn(ring, 0);
            grid.Children.Add(ring);

            var name = CardKit.Text(g.TypeName, 12, "Cp.Ink", FontWeights.SemiBold, mono: true, margin: new Thickness(10, 0, 10, 0));
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var count = CardKit.Text(SelectionBarText.RowCount(g).ToString(System.Globalization.CultureInfo.InvariantCulture), 12.5, "Cp.Ink", FontWeights.SemiBold, wrap: false, mono: true);
            count.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(count, 2);
            grid.Children.Add(count);

            // The board's right-hand icon: a magnifier in the faint grey (the row zooms to the type).
            var zoom = Icon(CardKit.Icons.Zoom, 16, "Cp.Faint");
            zoom.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(zoom, 3);
            grid.Children.Add(zoom);

            var row = new Border { Child = grid, MinHeight = RowMinHeight, Background = Brushes.Transparent, BorderThickness = new Thickness(0, 1, 0, 0) };
            row.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            if (onClick != null)
            {
                row.Cursor = System.Windows.Input.Cursors.Hand;
                row.MouseEnter += (_, __) => row.SetResourceReference(Border.BackgroundProperty, "Cp.Hover");
                row.MouseLeave += (_, __) => { row.ClearValue(Border.BackgroundProperty); row.Background = Brushes.Transparent; };
                row.MouseLeftButtonUp += (_, __) => onClick(g);
            }
            return row;
        }

        private static FrameworkElement Footnote(System.Collections.Generic.List<string> lines)
        {
            var grid = new Grid { Margin = new Thickness(14, 10, 14, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = Icon("M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 Z M8,7.3 V11.3 M8,4.9 V5.1", 16, "Cp.Muted");
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 1, 8, 0);
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);
            var text = new StackPanel();
            foreach (var l in lines) text.Children.Add(CardKit.Text(l, 12, "Cp.Muted", margin: new Thickness(0, 0, 0, 2)));
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var box = new Border { Child = grid, BorderThickness = new Thickness(0, 1, 0, 0) };
            box.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            box.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            return box;
        }

        // ── Icons ─────────────────────────────────────────────────────────────────────────────────────────────────

        private static FrameworkElement Icon(string data, double size, string stroke)
        {
            var path = new Path
            {
                Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
            };
            path.SetResourceReference(Shape.StrokeProperty, stroke);
            return path;
        }

        // The header icon: a dashed rounded square (stroke 1.5, dash 2.5 2) with a filled dot, 14px, in the info blue.
        private static FrameworkElement SelectedIcon()
        {
            var box = new Grid { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
            var square = new Path
            {
                Data = Geometry.Parse("M4.5,2.5 H11.5 A2,2 0 0 1 13.5,4.5 V11.5 A2,2 0 0 1 11.5,13.5 H4.5 A2,2 0 0 1 2.5,11.5 V4.5 A2,2 0 0 1 4.5,2.5 Z"),
                Stretch = Stretch.Fill, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 1.67, 1.33 },
            };
            square.SetResourceReference(Shape.StrokeProperty, "Cp.Run.Info");
            box.Children.Add(square);
            var dot = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Shape.FillProperty, "Cp.Run.Info");
            box.Children.Add(dot);
            return box;
        }
    }
}
