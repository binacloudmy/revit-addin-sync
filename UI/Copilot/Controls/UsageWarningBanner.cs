using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Near-limit notice that sits directly above the composer, before the quota is
    /// actually exhausted (the exhausted case is BlockedView, which replaces the
    /// composer entirely — the two never show together).
    ///
    /// Two bands, per the design:
    ///   80–94  amber, warning triangle, "You've used N% of your usage" — dismissible.
    ///   ≥95    red, bolt, "Running low" + an Upgrade button — NOT dismissible, since
    ///          at that point silently hiding it is how a drafter gets surprised
    ///          mid-command.
    ///
    /// Colours are tinted from the shared Cp.Amber / Cp.Red brushes via
    /// SetResourceReference rather than the Cp.AmberBg/IssueBg family, because those
    /// soft tokens are light-only (absent from CopilotTheme's dark palette) and would
    /// render a cream card on the Slate dark background.
    /// </summary>
    public static class UsageWarningBanner
    {
        public static FrameworkElement Build(UsageState u, Action openUpgrade, Action dismiss)
        {
            bool critical = u.Pct >= UsageState.CriticalPct;
            string severity = UsageState.MeterColorKey(u.Pct);

            var root = new Grid { Margin = new Thickness(14, 0, 14, 10) };

            // Tint + stroke as separate layers so the content stays full-opacity
            // while the fill sits at ~12% of the live severity brush.
            var tint = new Border { CornerRadius = new CornerRadius(11), Opacity = 0.12 };
            tint.SetResourceReference(Border.BackgroundProperty, severity);
            root.Children.Add(tint);

            var stroke = new Border
            {
                CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(1),
                Opacity = 0.35,
            };
            stroke.SetResourceReference(Border.BorderBrushProperty, severity);
            root.Children.Add(stroke);

            var row = new Grid { Margin = new Thickness(11, 9, 10, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon tile
            var tile = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                Opacity = 0.9,
            };
            var tileTint = new Border { CornerRadius = new CornerRadius(8), Opacity = 0.22 };
            tileTint.SetResourceReference(Border.BackgroundProperty, severity);
            var icon = critical ? BoltIcon() : WarnIcon();
            icon.SetResourceReference(Shape.StrokeProperty, severity);
            var tileGrid = new Grid();
            tileGrid.Children.Add(tileTint);
            tileGrid.Children.Add(icon);
            tile.Child = tileGrid;
            row.Children.Add(tile);

            // Copy
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock
            {
                Text = critical ? "Running low" : $"You've used {u.Pct}% of your usage",
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            text.Children.Add(title);

            var sub = new TextBlock
            {
                Text = critical
                    ? $"Only {Math.Max(0, 100 - u.Pct)}% of your usage left."
                    : "Upgrade soon to avoid interruptions.",
                FontSize = 10.5, Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            text.Children.Add(sub);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            // Trailing affordance: CTA when critical, dismiss otherwise.
            FrameworkElement trailing = critical
                ? UpgradeButton(openUpgrade)
                : DismissButton(dismiss);
            trailing.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);

            root.Children.Add(row);
            return root;
        }

        private static Button UpgradeButton(Action openUpgrade)
        {
            var b = new Button
            {
                Height = 28, Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
                Margin = new Thickness(8, 0, 0, 0), FocusVisualStyle = null,
            };
            b.SetResourceReference(Control.BackgroundProperty, "Cp.AccentGrad");

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new Thickness(12, 0, 12, 0));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            b.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            var t = new TextBlock { Text = "Upgrade", FontSize = 11.5, FontWeight = FontWeights.SemiBold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.AccentContrast");
            b.Content = t;
            b.Click += (_, __) => openUpgrade?.Invoke();
            return b;
        }

        private static Button DismissButton(Action dismiss)
        {
            var b = new Button
            {
                Width = 24, Height = 24, Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, FocusVisualStyle = null, ToolTip = "Dismiss",
            };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            b.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            var x = new Path
            {
                Width = 10, Height = 10, Stretch = Stretch.Uniform, StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M6,6 L18,18 M18,6 L6,18"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            x.SetResourceReference(Shape.StrokeProperty, "Cp.Faint");
            b.Content = x;
            b.Click += (_, __) => dismiss?.Invoke();
            return b;
        }

        private static Path WarnIcon() => new Path
        {
            Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.9,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M12,3 L22,20 H2 Z M12,9 V14 M12,17 V17.5"),
        };

        private static Path BoltIcon() => new Path
        {
            Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 2.1,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M13,2 4.5,13.5 H11 l-1,8.5 8.5,-11.5 H12 Z"),
        };
    }
}
