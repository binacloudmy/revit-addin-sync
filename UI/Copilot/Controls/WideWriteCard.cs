#nullable enable
// The "Needs your OK" card of board Confirm: a write that reaches more than the prompt read. Code-built, theme-aware
// (every colour is a Cp.* token). WideWriteModel holds what it shows; this file only draws it.
//
// Also home of the pieces the ask card shares for its choice look: the amber eyebrow, the three full-width buttons
// (built from CardKit.Primary / Secondary / Ghost, then given the board's heights, full-width content and radius),
// the footnote, and LineIcon (the board's 16x16 line icons).
//
// Board sizes: eyebrow chip h24 radius 12; headline 17/600; card radius 12 on the card fill with a hairline;
// group header rows padding 9/14; type rows mono 11.5; chips h22 radius 5 mono 11; buttons min-height 56 (two
// lines) or 44, radius 10, gap 8; ghost min-height 44; footnote 12 with a 16px icon; blocks 16 apart.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>The board's line icons: a 16x16 box drawn with a stroke, scaled to <c>size</c> (stroke scales with it).</summary>
    internal static class LineIcon
    {
        public const string Shield = "M8,2 L13,4 L13,8 C13,11 10.8,13.2 8,14 C5.2,13.2 3,11 3,8 L3,4 Z";
        public const string ShieldCheck = "M8,2 L13,4 L13,8 C13,11 10.8,13.2 8,14 C5.2,13.2 3,11 3,8 L3,4 Z M5.8,8.2 L7.4,9.8 L10.3,6.7";
        public const string Check = "M3.5,8.5 L6.5,11.5 L12.5,4.5";
        public const string Warn = "M8,2.5 L14,13 L2,13 Z M8,6.5 V9.5 M8,11.3 V11.5";
        public const string Arrow = "M3,8 H13 M9,4 L13,8 L9,12";
        public const string Chevron = "M4,6 L8,10 L12,6";

        public static FrameworkElement Make(string data, double size, string strokeToken, double thickness)
        {
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = Brushes.Transparent,
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, strokeToken);
            var box = new Canvas { Width = 16, Height = 16 };
            box.Children.Add(path);
            return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = box, VerticalAlignment = VerticalAlignment.Center };
        }
    }

    public static class WideWriteCard
    {
        /// <param name="pick">Called with the button the drafter pressed (its <see cref="WideWriteModel.AnswerFor"/> is the answer).</param>
        public static FrameworkElement Build(WideWriteModel m, Action<WideButtonModel> pick)
        {
            var root = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };

            // The sentence: eyebrow, headline, optional second sentence (gap 10).
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            head.Children.Add(Eyebrow(m.Eyebrow));
            var h = CardKit.Text(m.Headline, 17, "Cp.Ink", FontWeights.SemiBold, margin: new Thickness(0, 10, 0, 0));
            h.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            h.LineHeight = 22;
            head.Children.Add(h);
            if (!string.IsNullOrWhiteSpace(m.Detail))
            {
                var d = CardKit.Text(m.Detail, 13, "Cp.Muted", margin: new Thickness(0, 10, 0, 0));
                d.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                d.LineHeight = 19.5;
                head.Children.Add(d);
            }
            root.Children.Add(head);

            if (m.Groups.Count > 0) root.Children.Add(ReachCard(m));

            var buttons = new StackPanel { Margin = new Thickness(0, 16, 0, 16) };
            for (var i = 0; i < m.Buttons.Count; i++)
            {
                var spec = m.Buttons[i];
                var b = FullWidthButton(spec.Role, spec.Label, spec.Detail, () => pick(spec));
                b.Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
                buttons.Children.Add(b);
            }
            root.Children.Add(buttons);

            root.Children.Add(Footnote(m.Footnote));
            return root;
        }

        // ── Shared with the ask card's choice look ────────────────────────────────────────────────────────────────

        /// <summary>The amber "Needs your OK" chip: h24, radius 12, shield icon, 11.5 / 600.</summary>
        internal static FrameworkElement Eyebrow(string text)
        {
            var chip = new Border
            {
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(7, 0, 9, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Cp.AmberBg");
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var icon = LineIcon.Make(LineIcon.Shield, 12, "Cp.AmberFg", 1.6);
            icon.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(icon);
            var t = CardKit.Text(text, 11.5, "Cp.AmberFg", FontWeights.SemiBold, wrap: false);
            t.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(t);
            chip.Child = row;
            return chip;
        }

        /// <summary>One of the board's three stacked buttons: full width, left-aligned text, an optional grey second line, an arrow
        /// on the right (not on the ghost). Built from the kit's own button (its fill, border, hover and focus), then sized to the
        /// board: min-height 56 with a second line, else 44; radius 10; padding 9/14.</summary>
        internal static Button FullWidthButton(WideRole role, string label, string? detail, Action onClick)
        {
            var b = role == WideRole.Primary ? CardKit.Primary(label, onClick)
                : role == WideRole.Secondary ? CardKit.Secondary(label, onClick)
                : CardKit.Ghost(label, onClick);
            var hasDetail = !string.IsNullOrWhiteSpace(detail);
            b.Margin = new Thickness(0);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Padding = role == WideRole.Ghost ? new Thickness(14, 0, 14, 0) : new Thickness(14, 9, 14, 9);
            b.MinHeight = role != WideRole.Ghost && hasDetail ? 56 : 44;
            b.ToolTip = null;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = label ?? "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = role == WideRole.Ghost ? 13 : 13.5,
                FontWeight = role == WideRole.Primary ? FontWeights.SemiBold : FontWeights.Medium,
                TextAlignment = TextAlignment.Left,
            });
            if (hasDetail)
            {
                var d = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 1, 0, 0), TextAlignment = TextAlignment.Left };
                if (role == WideRole.Primary) d.Opacity = 0.75;                                        // dark text, softer than the title
                else d.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                text.Children.Add(d);
            }
            grid.Children.Add(text);
            if (role != WideRole.Ghost)
            {
                var arrow = LineIcon.Make(LineIcon.Arrow, 16, role == WideRole.Primary ? "Cp.AccentContrast" : "Cp.Faint", role == WideRole.Primary ? 1.8 : 1.6);
                arrow.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(arrow, 1);
                grid.Children.Add(arrow);
            }
            b.Content = grid;

            // The kit's template centres its content; give the grid the button's inner width so the text sits left and the arrow right.
            b.SizeChanged += (_, e) =>
                grid.Width = Math.Max(0, e.NewSize.Width - b.Padding.Left - b.Padding.Right - b.BorderThickness.Left - b.BorderThickness.Right);
            // The board's buttons are radius 10; the kit's template is 8.
            b.Loaded += (_, __) =>
            {
                if (b.Template?.FindName("bd", b) is Border bd) bd.CornerRadius = new CornerRadius(10);
            };
            return b;
        }

        /// <summary>The footnote: a 16px shield-check, then the sentence, 12 in Cp.Faint.</summary>
        internal static FrameworkElement Footnote(string text)
        {
            var g = new Grid { Margin = new Thickness(2, 0, 2, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = LineIcon.Make(LineIcon.ShieldCheck, 16, "Cp.Faint", 1.4);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 1, 0, 0);
            g.Children.Add(icon);
            var t = CardKit.Text(text, 12, "Cp.Faint", margin: new Thickness(8, 0, 0, 0));
            t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            t.LineHeight = 18;
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }

        // ── The "The write would reach" card ──────────────────────────────────────────────────────────────────────

        private static FrameworkElement ReachCard(WideWriteModel m)
        {
            var card = CardKit.Card("Cp.Line", "Cp.Bg", margin: new Thickness(0));
            var col = new StackPanel();

            // Totals, the proportion bar, the legend.
            var top = new StackPanel { Margin = new Thickness(14) };
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = CardKit.Text(m.ReachTitle, 12.5, "Cp.Ink", FontWeights.SemiBold);
            var reach = CardKit.Text(m.ReachText, 11.5, "Cp.Muted", mono: true, wrap: false);
            reach.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(reach, 1);
            line.Children.Add(title);
            line.Children.Add(reach);
            top.Children.Add(line);
            // One unlabeled group (the server's plain "name: count" list, no tiles): the title and the rows only.
            var plain = m.Groups.Count == 1 && string.IsNullOrEmpty(m.Groups[0].Label);
            if (plain) top.Margin = new Thickness(14, 14, 14, 0);
            else
            {
                top.Children.Add(ProportionBar(m));
                top.Children.Add(Legend(m));
            }
            col.Children.Add(top);

            foreach (var g in m.Groups)
            {
                if (!plain) col.Children.Add(GroupHeader(g));
                col.Children.Add(GroupRows(g));
            }

            if (m.Params.Count > 0) col.Children.Add(ParamsBlock(m));

            card.Child = col;
            return card;
        }

        private static FrameworkElement ProportionBar(WideWriteModel m)
        {
            // Segments weighted by fixture count, 2px apart; the outer corners are rounded (radius 3) by clipping the strip.
            var strip = new Grid { Height = 10, Margin = new Thickness(0, 10, 0, 0) };
            var n = 0;
            foreach (var g in m.Groups)
            {
                if (n > 0) strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(g.FixtureCount, 0.0001), GridUnitType.Star) });
                var seg = g.IsWide ? Hatch(10, 6, 3) : Solid("Cp.TextSecondary");
                Grid.SetColumn(seg, strip.ColumnDefinitions.Count - 1);
                strip.Children.Add(seg);
                n++;
            }
            strip.SizeChanged += (_, e) =>
                strip.Clip = e.NewSize.Width <= 0 || e.NewSize.Height <= 0 ? null
                    : new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 3, 3);
            return strip;
        }

        private static FrameworkElement Solid(string token)
        {
            var b = new Border();
            b.SetResourceReference(Border.BackgroundProperty, token);
            return b;
        }

        /// <summary>Amber diagonal stripes on a darker amber ground, as the board's wide-group swatch.</summary>
        private static FrameworkElement Hatch(double height, double step, double stroke)
        {
            var host = new Grid { ClipToBounds = true };
            var ground = new Border { Opacity = 0.45 };
            ground.SetResourceReference(Border.BackgroundProperty, "Cp.Amber");
            host.Children.Add(ground);
            var lines = new System.Windows.Shapes.Path { StrokeThickness = stroke, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            lines.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Cp.Amber");
            host.SizeChanged += (_, e) =>
            {
                var w = e.NewSize.Width;
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    for (var x = -height; x < w; x += step)
                    {
                        c.BeginFigure(new Point(x, height), false, false);
                        c.LineTo(new Point(x + height, 0), true, false);
                    }
                }
                geo.Freeze();
                lines.Data = geo;
            };
            host.Children.Add(lines);
            return host;
        }

        private static FrameworkElement Legend(WideWriteModel m)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var g in m.Groups)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                var sw = g.IsWide ? Hatch(8, 4, 2) : Solid("Cp.TextSecondary");
                var swatch = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Child = sw, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
                swatch.SizeChanged += (_, e) => swatch.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 2, 2);
                item.Children.Add(swatch);
                var label = CardKit.Text(g.Label + " ", 12, "Cp.TextSecondary", wrap: false);
                label.VerticalAlignment = VerticalAlignment.Center;
                item.Children.Add(label);
                var n = CardKit.Text(g.FixtureCount.ToString(System.Globalization.CultureInfo.InvariantCulture), 11, "Cp.Muted", mono: true, wrap: false);
                n.VerticalAlignment = VerticalAlignment.Center;
                item.Children.Add(n);
                wrap.Children.Add(item);
            }
            return wrap;
        }

        private static FrameworkElement GroupHeader(WideGroupModel g)
        {
            var bar = new Border { Padding = new Thickness(14, 9, 14, 9), BorderThickness = new Thickness(0, 1, 0, 0) };
            bar.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            bar.SetResourceReference(Border.BackgroundProperty, g.IsWide ? "Cp.AmberBg" : "Cp.Raised");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var icon = g.IsWide ? LineIcon.Make(LineIcon.Warn, 14, "Cp.AmberFg", 1.5) : LineIcon.Make(LineIcon.Check, 14, "Cp.Green", 2);
            icon.Margin = new Thickness(0, 0, 7, 0);
            left.Children.Add(icon);
            var t = CardKit.Text(g.Title, 13, g.IsWide ? "Cp.AmberFg" : "Cp.Ink", FontWeights.SemiBold, wrap: false);
            t.VerticalAlignment = VerticalAlignment.Center;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            left.Children.Add(t);
            grid.Children.Add(left);
            var count = CardKit.Text(g.CountText, 11.5, "Cp.Muted", wrap: false);
            count.VerticalAlignment = VerticalAlignment.Center;
            count.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(count, 1);
            grid.Children.Add(count);
            bar.Child = grid;
            return bar;
        }

        private static FrameworkElement GroupRows(WideGroupModel g)
        {
            var grid = new Grid { Margin = new Thickness(14, 10, 14, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var r = 0;
            void AddRow(FrameworkElement name, FrameworkElement count)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                name.Margin = new Thickness(0, r == 0 ? 0 : 5, 0, 0);
                count.Margin = new Thickness(12, r == 0 ? 0 : 5, 0, 0);
                count.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetRow(name, r);
                Grid.SetRow(count, r);
                Grid.SetColumn(count, 1);
                grid.Children.Add(name);
                grid.Children.Add(count);
                r++;
            }
            foreach (var row in g.Rows)
                AddRow(CardKit.Text(row.Name, 11.5, "Cp.Ink", mono: true),
                       CardKit.Text(row.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), 11.5, "Cp.Ink", mono: true, wrap: false));
            if (g.MoreTypes > 0)
                AddRow(CardKit.Text(g.MoreText, 12, "Cp.Muted"),
                       CardKit.Text(g.MoreFixtures < 0 ? "" : g.MoreFixtures.ToString(System.Globalization.CultureInfo.InvariantCulture), 11.5, "Cp.Muted", mono: true, wrap: false));
            return grid;
        }

        private static FrameworkElement ParamsBlock(WideWriteModel m)
        {
            var box = new Border { Padding = new Thickness(14, 12, 14, 9), BorderThickness = new Thickness(0, 1, 0, 0) };
            box.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            box.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            var col = new StackPanel();
            col.Children.Add(CardKit.Text(m.ParamsTitle, 12, "Cp.Muted", margin: new Thickness(0, 0, 0, 8)));
            var wrap = new WrapPanel();
            foreach (var p in m.Params)
            {
                var chip = new Border
                {
                    Height = 22,
                    CornerRadius = new CornerRadius(5),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(7, 0, 7, 0),
                    Margin = new Thickness(0, 0, 5, 5),
                };
                chip.SetResourceReference(Border.BackgroundProperty, "Cp.Raised");
                chip.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                var t = CardKit.Text(p, 11, "Cp.TextSecondary", mono: true, wrap: false);
                t.VerticalAlignment = VerticalAlignment.Center;
                chip.Child = t;
                wrap.Children.Add(chip);
            }
            col.Children.Add(wrap);
            box.Child = col;
            return box;
        }
    }
}
