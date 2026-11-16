#nullable enable
// The "Needs your OK" card: the server's question before a wide or destructive write (options write_all / write_cancel).
// Code-built, theme-aware (every colour is a Cp.* token). WideWriteModel holds what it shows; this file only draws it.
//
// Drawn in the decision card's design (AskCard), so the two questions read as one family: the same bordered card
// (CardKit.Card, radius 12, Cp.Line on Cp.Bg), header padding 14/13/14/11 with the title at 14 SemiBold, sections split by
// 1 px Cp.LineSoft dividers with 14/10 padding, 12.5 rows, uppercase faint kickers, and a footer row of kit buttons at
// min-height 34 (the primary for the approving answer, Secondary for the others), left-aligned and wrapping. The amber
// "Needs your OK" chip is this card's identity: top right where the decision card has its progress segments, above the
// title when the card is narrow. The footnote is small faint text under the buttons.
//
// Keys: none. No key handler, no Focus() call, no default or cancel button; the buttons are the kit's ordinary
// focusable buttons (Tab reaches them), as the team built them. LineIcon (the board's 16x16 line icons) lives here too.

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
        /// <summary>Below this inner width the chip goes above the title, so the headline keeps the card's width.</summary>
        private const double ChipBesideTitleFrom = 400;

        /// <param name="pick">Called with the button the drafter pressed (its <see cref="WideWriteModel.AnswerFor"/> is the answer).</param>
        public static FrameworkElement Build(WideWriteModel m, Action<WideButtonModel> pick)
        {
            var outer = CardKit.Card("Cp.Line", "Cp.Bg");
            var sp = new StackPanel();
            outer.Child = sp;

            sp.Children.Add(Header(m));
            if (m.Groups.Count > 0) sp.Children.Add(Scope(m));
            if (m.Params.Count > 0) sp.Children.Add(ParamsBlock(m));
            sp.Children.Add(Footer(m, pick));
            return outer;
        }

        // ── Header: the headline as the card's title, the second sentence, the chip ───────────────────────────────

        private static FrameworkElement Header(WideWriteModel m)
        {
            var grid = new Grid { Margin = new Thickness(14, 13, 14, 11) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(CardKit.Text(m.Headline, 14, "Cp.Ink", FontWeights.SemiBold));
            if (!string.IsNullOrWhiteSpace(m.Detail))
                left.Children.Add(CardKit.Text(m.Detail, 12, "Cp.Muted", margin: new Thickness(0, 2, 0, 0)));
            grid.Children.Add(left);

            var chip = Eyebrow(m.Eyebrow);
            chip.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(chip);

            void Place(bool beside)
            {
                Grid.SetRow(chip, 0);
                Grid.SetColumn(chip, beside ? 1 : 0);
                Grid.SetColumnSpan(chip, beside ? 1 : 2);
                chip.Margin = beside ? new Thickness(12, 0, 0, 0) : new Thickness(0, 0, 0, 8);
                Grid.SetRow(left, beside ? 0 : 1);
                Grid.SetColumnSpan(left, beside ? 1 : 2);
            }
            Place(beside: false);
            var placed = false;
            grid.SizeChanged += (_, e) =>
            {
                if (!e.WidthChanged || e.NewSize.Width <= 0) return;
                var beside = e.NewSize.Width >= ChipBesideTitleFrom;
                if (placed && beside == (Grid.GetColumn(chip) == 1)) return;
                placed = true;
                Place(beside);
            };
            return grid;
        }

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

        // ── Scope: what the write would reach ─────────────────────────────────────────────────────────────────────

        private static Border Section(string? background = null)
        {
            var b = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 10, 14, 10) };
            b.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            if (background != null) b.SetResourceReference(Border.BackgroundProperty, background);
            return b;
        }

        private static FrameworkElement Scope(WideWriteModel m)
        {
            var block = Section();
            var sp = new StackPanel();
            block.Child = sp;

            // The kicker and the totals, as the decision card's "What you will get" heading and its count.
            var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
            if (!string.IsNullOrEmpty(m.ReachText) && m.TotalTypes > 0)
            {
                var reach = CardKit.Text(m.ReachText, 12, "Cp.Muted", wrap: false);
                reach.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(reach, Dock.Right);
                head.Children.Add(reach);
            }
            head.Children.Add(AskCard.Kicker(m.ReachTitle, new Thickness(0)));
            sp.Children.Add(head);

            // One unlabeled group (the server's plain "name: count" list): the rows only. Labeled groups (WideWriteModel.From)
            // keep the proportion strip and its legend: they show how much of the write is the part the prompt did not mean.
            var plain = m.Groups.Count == 1 && string.IsNullOrEmpty(m.Groups[0].Label);
            if (!plain)
            {
                sp.Children.Add(ProportionBar(m));
                sp.Children.Add(Legend(m));
            }
            for (var i = 0; i < m.Groups.Count; i++)
            {
                var g = m.Groups[i];
                if (!plain) sp.Children.Add(GroupHeader(g, first: i == 0));
                sp.Children.Add(GroupRows(g));
            }
            return block;
        }

        private static FrameworkElement ProportionBar(WideWriteModel m)
        {
            // Segments weighted by fixture count, 2px apart; the outer corners are rounded (radius 3) by clipping the strip.
            var strip = new Grid { Height = 8, Margin = new Thickness(0, 2, 0, 0) };
            var n = 0;
            foreach (var g in m.Groups)
            {
                if (n > 0) strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(g.FixtureCount, 0.0001), GridUnitType.Star) });
                var seg = g.IsWide ? Hatch(8, 6, 3) : Solid("Cp.TextSecondary");
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
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var g in m.Groups)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 2) };
                var sw = g.IsWide ? Hatch(8, 4, 2) : Solid("Cp.TextSecondary");
                var swatch = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Child = sw, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                swatch.SizeChanged += (_, e) => swatch.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 2, 2);
                item.Children.Add(swatch);
                var label = CardKit.Text(g.Label + " ", 12, "Cp.Muted", wrap: false);
                label.VerticalAlignment = VerticalAlignment.Center;
                item.Children.Add(label);
                var n = CardKit.Text(g.FixtureCount.ToString(System.Globalization.CultureInfo.InvariantCulture), 11.5, "Cp.Faint", mono: true, wrap: false);
                n.VerticalAlignment = VerticalAlignment.Center;
                item.Children.Add(n);
                wrap.Children.Add(item);
            }
            return wrap;
        }

        private static FrameworkElement GroupHeader(WideGroupModel g, bool first)
        {
            var grid = new Grid { Margin = new Thickness(0, first ? 10 : 12, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = g.IsWide ? LineIcon.Make(LineIcon.Warn, 13, "Cp.AmberFg", 1.5) : LineIcon.Make(LineIcon.Check, 13, "Cp.Green", 2);
            icon.Margin = new Thickness(0, 1, 7, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(icon);
            var t = CardKit.Text(g.Title, 12.5, g.IsWide ? "Cp.AmberFg" : "Cp.Ink", FontWeights.SemiBold);
            Grid.SetColumn(t, 1);
            grid.Children.Add(t);
            var count = CardKit.Text(g.CountText, 12, "Cp.Muted", wrap: false);
            count.VerticalAlignment = VerticalAlignment.Top;
            count.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(count, 2);
            grid.Children.Add(count);
            return grid;
        }

        private static FrameworkElement GroupRows(WideGroupModel g)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var r = 0;
            void AddRow(FrameworkElement name, FrameworkElement count)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                name.Margin = new Thickness(0, 2, 0, 2);
                count.Margin = new Thickness(12, 2, 0, 2);
                count.HorizontalAlignment = HorizontalAlignment.Right;
                count.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(name, r);
                Grid.SetRow(count, r);
                Grid.SetColumn(count, 1);
                grid.Children.Add(name);
                grid.Children.Add(count);
                r++;
            }
            foreach (var row in g.Rows)
                AddRow(CardKit.Text(row.Name, 12.5, "Cp.Ink"),
                       CardKit.Text(row.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), 12, "Cp.Muted", wrap: false));
            if (g.MoreTypes > 0)
                AddRow(CardKit.Text(g.MoreText, 12, "Cp.Faint"),
                       CardKit.Text(g.MoreFixtures < 0 ? "" : g.MoreFixtures.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), 12, "Cp.Faint", wrap: false));
            return grid;
        }

        private static FrameworkElement ParamsBlock(WideWriteModel m)
        {
            var box = Section("Cp.Sunken");
            var col = new StackPanel();
            col.Children.Add(CardKit.Text(m.ParamsTitle, 12, "Cp.Muted", margin: new Thickness(0, 0, 0, 6)));
            var wrap = new WrapPanel();
            foreach (var p in m.Params)
            {
                // The decision card's "Ready" pill: radius 10 on Cp.Hover.
                var chip = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4) };
                chip.SetResourceReference(Border.BackgroundProperty, "Cp.Hover");
                chip.Child = CardKit.Text(p, 11.5, "Cp.Ink", mono: true, wrap: false);
                wrap.Children.Add(chip);
            }
            col.Children.Add(wrap);
            box.Child = col;
            return box;
        }

        // ── Footer: one row of buttons, then the footnote ─────────────────────────────────────────────────────────

        private static FrameworkElement Footer(WideWriteModel m, Action<WideButtonModel> pick)
        {
            var bar = Section();
            bar.Padding = new Thickness(14, 12, 14, 11);
            var sp = new StackPanel();
            bar.Child = sp;
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var spec in m.Buttons)
                row.Children.Add(ActionButton(spec, () => pick(spec)));
            sp.Children.Add(row);
            var foot = Footnote(m.Footnote);
            foot.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(foot);
            return bar;
        }

        /// <summary>The team's order and emphasis: the Primary role (the approving answer, or the narrow write when the model has
        /// one) is the kit's primary; every other answer, the cancel included, is the kit's Secondary, as on the decision card. The
        /// button is the decision card's footer size (min-height 34); its label wraps, so a long label never clips at 360 px.
        /// The model's grey second line (WideWriteModel.From only) becomes the tooltip.</summary>
        private static Button ActionButton(WideButtonModel spec, Action onClick)
        {
            var b = spec.Role == WideRole.Primary ? CardKit.Primary(spec.Label, onClick) : CardKit.Secondary(spec.Label, onClick);
            b.MinHeight = 34;
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Content = new TextBlock { Text = spec.Label ?? "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 7) };
            b.ToolTip = string.IsNullOrWhiteSpace(spec.Detail) ? null : spec.Detail;
            return b;
        }

        /// <summary>The footnote: a 12px shield-check, then the sentence, 10.5 in Cp.Faint (the decision card's small print).</summary>
        internal static FrameworkElement Footnote(string text)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = LineIcon.Make(LineIcon.ShieldCheck, 12, "Cp.Faint", 1.4);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 1, 0, 0);
            g.Children.Add(icon);
            var t = CardKit.Text(text, 10.5, "Cp.Faint", margin: new Thickness(6, 0, 0, 0));
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            return g;
        }
    }
}
