using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Signed-out gate. Replaces the entire chat body (tabs are hidden alongside
    /// it) until a session exists. Geometry, type sizes and spacing come from the
    /// design's signed-out section in docs/design/copilot-panel-slate.dc.html —
    /// sparkle cluster, 48px gradient CTA, hairline divider, three benefit rows,
    /// centered Terms link.
    ///
    /// Code-built rather than XAML for the same reason as BlockedView: the sparkle
    /// cluster needs overlapping absolute placement and per-element glow, and the
    /// waiting spinner has to be driven by a DispatcherTimer (WPF Storyboards
    /// inside a Revit dockable pane crash Revit).
    /// </summary>
    public sealed class SignInView : UserControl
    {
        private readonly ContentControl _ctaHost = new ContentControl();
        private DispatcherTimer _spin;
        private RotateTransform _spinRotate;

        /// <summary>Sign-in CTA clicked — host starts the browser flow.</summary>
        public event Action SignInRequested;
        /// <summary>Cancel link clicked while waiting on the browser.</summary>
        public event Action CancelRequested;
        /// <summary>Terms &amp; Conditions clicked — opens in the default browser.</summary>
        public event Action TermsRequested;

        public SignInView()
        {
            // Stretch, not Center: a centered StackPanel sizes to its widest child,
            // so anything wider than the pane overflows and clips. Children are
            // centered individually instead.
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };

            stack.Children.Add(SparkleCluster());

            var heading = new TextBlock
            {
                Text = "Sign in to get started",
                FontSize = 16, FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260, Margin = new Thickness(0, 8, 0, 6), LineHeight = 21,
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            stack.Children.Add(heading);

            var sub = new TextBlock
            {
                Text = "Copilot uses your BINAXONE ID — the same account as your other "
                     + "BINA plugins. Sign-in opens in your browser.",
                FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                MaxWidth = 270, Margin = new Thickness(0, 0, 0, 15), LineHeight = 17,
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            stack.Children.Add(sub);

            // Design caps the action column at 280px; it still shrinks with the
            // pane, which is what keeps the gate usable at narrow dock widths.
            var column = new StackPanel { Width = 280, HorizontalAlignment = HorizontalAlignment.Center };
            column.Children.Add(_ctaHost);
            column.Children.Add(Benefits());
            stack.Children.Add(column);

            var terms = new TextBlock
            {
                Margin = new Thickness(0, 34, 0, 0), FontSize = 10.5,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
            };
            terms.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            var link = new System.Windows.Documents.Run("Terms & Conditions")
            {
                TextDecorations = TextDecorations.Underline,
            };
            terms.Inlines.Add(link);
            terms.MouseLeftButtonUp += (_, __) => TermsRequested?.Invoke();
            stack.Children.Add(terms);

            // Scrolls rather than clipping when the pane is short (the design's
            // overflow-y:auto) — the cluster + rows exceed a stubby dock height.
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(26, 18, 26, 20),
                Content = stack,
            };

            // The design's max-widths (260 / 270 / 280) are caps, not fixed widths.
            // A centered child whose desired width exceeds the viewport overflows to
            // one side and gets clipped, which is exactly what a narrow dock does —
            // so clamp each cap to what's actually available.
            scroller.SizeChanged += (_, e) =>
            {
                double avail = e.NewSize.Width - 52;   // scroller's 26px side padding
                if (avail <= 0) return;
                heading.MaxWidth = Math.Min(260, avail);
                sub.MaxWidth = Math.Min(270, avail);
                column.Width = Math.Min(280, avail);
            };

            Content = scroller;
            ShowIdle();
            Unloaded += (_, __) => StopSpinner();
        }

        /// <summary>Idle: the gradient "Sign in with BINAXONE" CTA.</summary>
        public void ShowIdle()
        {
            StopSpinner();
            _ctaHost.Content = SignInButton();
        }

        /// <summary>Waiting on the browser round-trip: spinner row + Cancel link.</summary>
        public void ShowWaiting()
        {
            _ctaHost.Content = WaitingBlock();
        }

        // ── CTA states ──────────────────────────────────────────────────
        private Button SignInButton()
        {
            var b = new Button
            {
                Height = 48, Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            b.SetResourceReference(Control.BackgroundProperty, "Cp.AccentGrad");

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            b.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            // Design's accent-tinted lift under the button.
            b.Effect = new DropShadowEffect
            {
                BlurRadius = 22, ShadowDepth = 8, Direction = 270,
                Color = Color.FromRgb(0x1d, 0x4e, 0xd8), Opacity = 0.34,
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new TextBlock
            {
                Text = "Sign in with BINAXONE", FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.AccentContrast");
            row.Children.Add(label);

            // External-link arrow (design: M7 17 17 7M8 7h9v9) — signals that the
            // sign-in leaves Revit for the browser.
            var arrow = new Path
            {
                Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Data = Geometry.Parse("M7,17 L17,7 M8,7 h9 v9"),
            };
            arrow.SetResourceReference(Shape.StrokeProperty, "Cp.AccentContrast");
            row.Children.Add(arrow);

            b.Content = row;
            b.Click += (_, __) => SignInRequested?.Invoke();
            return b;
        }

        private FrameworkElement WaitingBlock()
        {
            var wrap = new StackPanel();

            var box = new Border
            {
                Height = 44, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 0, 10, 0),
            };
            box.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            box.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(Spinner());
            var t = new TextBlock
            {
                Text = "Waiting for sign-in in your browser…",
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            row.Children.Add(t);
            box.Child = row;
            wrap.Children.Add(box);

            var cancel = new Button
            {
                Content = "Cancel", Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            cancel.SetResourceReference(Control.ForegroundProperty, "Cp.Faint");
            cancel.Click += (_, __) => CancelRequested?.Invoke();
            wrap.Children.Add(cancel);
            return wrap;
        }

        /// <summary>15px ring with an accent cap, rotated by a DispatcherTimer.
        /// NOT a Storyboard — those crash Revit inside a dockable pane.</summary>
        private FrameworkElement Spinner()
        {
            var ring = new Ellipse { Width = 15, Height = 15, StrokeThickness = 2 };
            ring.SetResourceReference(Shape.StrokeProperty, "Cp.Hair2");

            var cap = new Path
            {
                Width = 15, Height = 15, StrokeThickness = 2, Stretch = Stretch.None,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Fill = Brushes.Transparent,
                // Top quarter-arc — the moving part of the design's border-top
                // spinner. Authored directly in the ring's 15×15 space (centre 7.5,
                // r 6.5) with Stretch.None: uniform-stretching an arc whose bounds
                // are only the swept quadrant squashes it off the ring.
                Data = Geometry.Parse("M7.5,1 A6.5,6.5 0 0 1 14,7.5"),
            };
            cap.SetResourceReference(Shape.StrokeProperty, "Cp.Accent");
            _spinRotate = new RotateTransform(0);
            cap.RenderTransformOrigin = new Point(0.5, 0.5);
            cap.RenderTransform = _spinRotate;

            var host = new Grid { Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center };
            host.Children.Add(ring);
            host.Children.Add(cap);

            StopSpinner();
            _spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _spin.Tick += (_, __) =>
            {
                if (_spinRotate == null) return;
                _spinRotate.Angle = (_spinRotate.Angle + 27) % 360;
            };
            _spin.Start();
            return host;
        }

        private void StopSpinner()
        {
            if (_spin == null) return;
            _spin.Stop();
            _spin = null;
        }

        // ── Sparkle cluster (design: 38px star + 14px + 11px satellites) ─────
        private FrameworkElement SparkleCluster()
        {
            var canvas = new Canvas { Width = 52, Height = 50, HorizontalAlignment = HorizontalAlignment.Center };

            var main = Star(38);
            Canvas.SetLeft(main, 4); Canvas.SetTop(main, 7);
            canvas.Children.Add(main);

            var topRight = Star(14);
            Canvas.SetLeft(topRight, 35); Canvas.SetTop(topRight, 1);
            canvas.Children.Add(topRight);

            var bottomRight = Star(11);
            Canvas.SetLeft(bottomRight, 36); Canvas.SetTop(bottomRight, 32);
            canvas.Children.Add(bottomRight);

            return new Border
            {
                Child = canvas, Width = 52, Height = 50,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
            };
        }

        private static Path Star(double size)
        {
            var p = new Path { Width = size, Height = size, Stretch = Stretch.Uniform };
            p.SetResourceReference(Shape.FillProperty, "Cp.BotGradient");
            p.SetResourceReference(Path.DataProperty, "Cp.StarPath");
            // Design's two-layer glow, flattened to one shadow (rgba(59,142,247,.55)).
            p.Effect = new DropShadowEffect
            {
                BlurRadius = size * 0.5, ShadowDepth = 0,
                Color = Color.FromRgb(0x3b, 0x8e, 0xf7), Opacity = 0.55,
            };
            return p;
        }

        // ── Benefit rows ────────────────────────────────────────────────
        private FrameworkElement Benefits()
        {
            var wrap = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(0, 15, 0, 0),
            };
            wrap.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");

            var rows = new StackPanel();
            rows.Children.Add(BenefitRow(CommandIcon(), "Type commands in English or Bahasa Malaysia", true));
            rows.Children.Add(BenefitRow(StackIcon(), "Automation tools for walls, schedules, CAD conversion", false));
            rows.Children.Add(BenefitRow(TagIcon(), "One ID for every BINA plugin", false));
            wrap.Child = rows;
            return wrap;
        }

        private static FrameworkElement BenefitRow(Path icon, string label, bool first)
        {
            var grid = new Grid { Margin = new Thickness(0, first ? 0 : 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var tile = new Border
            {
                Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            };
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.SetResourceReference(Shape.StrokeProperty, "Cp.Ink");
            tile.Child = icon;
            grid.Children.Add(tile);

            var text = new TextBlock
            {
                Text = label, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, LineHeight = 16,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            return grid;
        }

        private static Path Icon(string data, double thickness) => new Path
        {
            Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent,
            Data = Geometry.Parse(data),
        };

        // Tabler ti-command / ti-stack-2 / ti-tag, as referenced by the design's
        // benefitRows.
        private static Path CommandIcon() => Icon(
            "M18,3 a3,3 0 0 0 -3,3 v12 a3,3 0 1 0 3,-3 H6 a3,3 0 1 0 3,3 V6 a3,3 0 1 0 -3,3 h12 a3,3 0 1 0 -3,-3 Z", 1.7);

        private static Path StackIcon() => Icon(
            "M12.83,2.18 a2,2 0 0 0 -1.66,0 L2.6,6.08 a1,1 0 0 0 0,1.83 l8.58,3.91 a2,2 0 0 0 1.66,0 l8.58,-3.9 a1,1 0 0 0 0,-1.83 Z "
          + "M22,17.65 l-9.17,4.16 a2,2 0 0 1 -1.66,0 L2,17.65 "
          + "M22,12.65 l-9.17,4.16 a2,2 0 0 1 -1.66,0 L2,12.65", 1.7);

        private static Path TagIcon() => Icon(
            "M12.59,2.59 a2,2 0 0 1 1.41,-0.59 H20 a2,2 0 0 1 2,2 v6 a2,2 0 0 1 -0.59,1.41 l-9,9 a2,2 0 0 1 -2.82,0 l-6,-6 a2,2 0 0 1 0,-2.82 Z "
          + "M17.7,7.5 a1.2,1.2 0 1 1 -2.4,0 a1.2,1.2 0 1 1 2.4,0 Z", 1.7);
    }
}
