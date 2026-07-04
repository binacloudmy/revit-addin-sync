using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// "Choose your plan" bottom-sheet body: a peek carousel of the three plan
    /// cards (Free / Basic / Pro) with drag, arrows and animated dots — the
    /// design's upgrade sheet. Animations use BeginAnimation on transforms
    /// (never XAML Storyboards, which crash in Revit dockable panes).
    /// </summary>
    public static class UpgradeSheet
    {
        private const string UpgradeUrl = "https://billing.bina.cloud/upgrade";
        private const string PricingUrl = "https://bina.cloud/pricing";
        private const double Gap = 12;

        private class PlanDef
        {
            public string Name, Price, IncLabel, CtaLabel;
            public string[] Features;
            public bool Recommended, Solid;
        }

        private static readonly PlanDef[] Plans =
        {
            new PlanDef { Name = "Free", Price = "$0", IncLabel = "WHAT'S INCLUDED", CtaLabel = "Get started", Solid = false,
                Features = new[] { "Limited usage", "Core Revit commands", "Chat history" } },
            new PlanDef { Name = "Basic", Price = "$20", IncLabel = "WHAT'S INCLUDED", CtaLabel = "Upgrade to Basic", Solid = true, Recommended = true,
                Features = new[] { "10× higher usage limit", "Faster responses", "Full Revit command library", "Chat history & exports", "Email support" } },
            new PlanDef { Name = "Pro", Price = "$40", IncLabel = "EVERYTHING IN BASIC, PLUS", CtaLabel = "Upgrade to Pro", Solid = true,
                Features = new[] { "Everything in Basic", "5× higher usage limit", "Priority responses", "Batch commands & automation", "Priority support" } },
        };

        /// <summary>Build the sheet BODY (the panel wraps it in its sheet chrome).</summary>
        public static FrameworkElement Build()
        {
            var root = new StackPanel();

            int active = 1; // Basic starts centered (design planIdx: 1)
            var cards = new Border[Plans.Length];
            var ctas = new Button[Plans.Length];
            var dots = new Border[Plans.Length];

            // ── viewport + track ─────────────────────────────────────────────
            var track = new StackPanel { Orientation = Orientation.Horizontal };
            var trackShift = new TranslateTransform();
            track.RenderTransform = trackShift;

            var viewport = new Border
            {
                ClipToBounds = true,
                Margin = new Thickness(-5, 9, -5, 11),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            viewport.Child = track;

            double CardW() => Math.Max(180, Math.Round(viewport.ActualWidth * 0.82));

            for (int i = 0; i < Plans.Length; i++)
            {
                var card = BuildCard(Plans[i], out var cta);
                cards[i] = card;
                ctas[i] = cta;
                track.Children.Add(card);
            }

            // ── motion ───────────────────────────────────────────────────────
            void Relayout(bool animate)
            {
                double w = CardW();
                if (viewport.ActualWidth <= 0) return;
                for (int i = 0; i < cards.Length; i++)
                {
                    cards[i].Width = w;
                    cards[i].Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0);
                }
                double target = viewport.ActualWidth / 2 - w / 2 - active * (w + Gap);
                if (animate)
                    trackShift.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(320)))
                        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                else
                {
                    trackShift.BeginAnimation(TranslateTransform.XProperty, null);
                    trackShift.X = target;
                }

                for (int i = 0; i < cards.Length; i++)
                {
                    bool on = i == active;
                    var scale = (ScaleTransform)cards[i].LayoutTransform;
                    var sTo = on ? 1.0 : 0.9;
                    if (animate)
                    {
                        var anim = new DoubleAnimation(sTo, new Duration(TimeSpan.FromMilliseconds(320)))
                        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                        cards[i].BeginAnimation(UIElement.OpacityProperty,
                            new DoubleAnimation(on ? 1.0 : 0.45, new Duration(TimeSpan.FromMilliseconds(320))));
                    }
                    else
                    {
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        scale.ScaleX = scale.ScaleY = sTo;
                        cards[i].BeginAnimation(UIElement.OpacityProperty, null);
                        cards[i].Opacity = on ? 1.0 : 0.45;
                    }
                    StyleCta(ctas[i], Plans[i], on);
                    if (dots[i] != null) StyleDot(dots[i], on);
                }
            }

            void Go(int idx, bool animate = true)
            {
                active = Math.Max(0, Math.Min(Plans.Length - 1, idx));
                Relayout(animate);
            }

            viewport.SizeChanged += (_, __) => Relayout(false);

            // drag-to-swipe
            double dragX0 = 0; bool dragging = false;
            viewport.MouseLeftButtonDown += (s, e) =>
            {
                dragging = true; dragX0 = e.GetPosition(viewport).X;
                viewport.CaptureMouse();
            };
            viewport.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                double dx = e.GetPosition(viewport).X - dragX0;
                double w = CardW();
                trackShift.BeginAnimation(TranslateTransform.XProperty, null);
                trackShift.X = viewport.ActualWidth / 2 - w / 2 - active * (w + Gap) + dx;
            };
            void EndDrag(MouseEventArgs e)
            {
                if (!dragging) return;
                dragging = false;
                viewport.ReleaseMouseCapture();
                double dx = e.GetPosition(viewport).X - dragX0;
                double thresh = viewport.ActualWidth * 0.16;
                if (dx <= -thresh) Go(active + 1);
                else if (dx >= thresh) Go(active - 1);
                else Go(active);
            }
            viewport.MouseLeftButtonUp += (s, e) => EndDrag(e);
            viewport.MouseLeave += (s, e) => EndDrag(e);

            root.Children.Add(viewport);

            // ── controls: ‹ dots › ───────────────────────────────────────────
            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 15, 0, 0),
            };
            var prev = ArrowButton("M15,18 L9,12 L15,6", () => Go(active - 1));
            controls.Children.Add(prev);
            var dotsRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
            for (int i = 0; i < Plans.Length; i++)
            {
                int captured = i;
                var dot = new Border
                {
                    Height = 6, CornerRadius = new CornerRadius(99), Cursor = Cursors.Hand,
                    Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                dot.MouseLeftButtonDown += (_, __) => Go(captured);
                dots[i] = dot;
                dotsRow.Children.Add(dot);
            }
            controls.Children.Add(dotsRow);
            var next = ArrowButton("M9,18 L15,12 L9,6", () => Go(active + 1));
            controls.Children.Add(next);
            root.Children.Add(controls);

            // ── "See all plans" ──────────────────────────────────────────────
            var seeAll = new TextBlock
            {
                Text = "See all plans", FontSize = 11.5, FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0),
                Cursor = Cursors.Hand,
            };
            seeAll.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            seeAll.MouseLeftButtonDown += (_, __) => OpenUrl(PricingUrl);
            root.Children.Add(seeAll);

            root.Loaded += (_, __) => Go(active, animate: false);
            return root;
        }

        // ── pieces ───────────────────────────────────────────────────────────

        private static Border BuildCard(PlanDef p, out Button cta)
        {
            var body = new StackPanel();

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = p.Name, FontSize = 14, FontWeight = FontWeights.Bold };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            head.Children.Add(name);
            if (p.Recommended)
            {
                var pill = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(9, 3, 9, 3), VerticalAlignment = VerticalAlignment.Center };
                pill.SetResourceReference(Border.BackgroundProperty, "Cp.AccentGrad");
                var pt = new TextBlock { Text = "RECOMMENDED", FontSize = 8.5, FontWeight = FontWeights.Bold };
                pt.SetResourceReference(TextBlock.ForegroundProperty, "Cp.AccentContrast");
                pill.Child = pt;
                Grid.SetColumn(pill, 1);
                head.Children.Add(pill);
            }
            body.Children.Add(head);

            var priceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 13) };
            var price = new TextBlock { Text = p.Price, FontSize = 25, FontWeight = FontWeights.ExtraBold };
            price.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            priceRow.Children.Add(price);
            var per = new TextBlock { Text = "/month", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 4) };
            per.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            priceRow.Children.Add(per);
            body.Children.Add(priceRow);

            var inc = new TextBlock { Text = p.IncLabel, FontSize = 9.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) };
            inc.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            body.Children.Add(inc);

            foreach (var f in p.Features)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
                var check = new Path
                {
                    Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 3,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    Data = Geometry.Parse("M20,6 L9,17 L4,12"), VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                check.SetResourceReference(Shape.StrokeProperty, p.Recommended ? "Cp.Accent" : "Cp.Faint");
                row.Children.Add(check);
                var ft = new TextBlock
                {
                    Text = f, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = p.Recommended ? FontWeights.Medium : FontWeights.Normal,
                };
                ft.SetResourceReference(TextBlock.ForegroundProperty, p.Recommended ? "Cp.Ink" : "Cp.Muted");
                row.Children.Add(ft);
                body.Children.Add(row);
            }

            cta = new Button
            {
                Height = 38, Margin = new Thickness(0, 14, 0, 0), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0),
            };
            var ctaLocal = cta;
            cta.Click += (_, __) => { if (ctaLocal.IsEnabled) OpenUrl(UpgradeUrl); };
            SetCtaContent(cta, p, arrow: p.Solid);
            body.Children.Add(cta);

            var card = new Border
            {
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(15),
                BorderThickness = new Thickness(p.Recommended ? 1.5 : 1),
                LayoutTransform = new ScaleTransform(1, 1),
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = body,
            };
            card.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            card.SetResourceReference(Border.BorderBrushProperty, p.Recommended ? "Cp.Accent" : "Cp.Line");
            return card;
        }

        private static void SetCtaContent(Button cta, PlanDef p, bool arrow)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = p.CtaLabel, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(label);
            if (arrow)
            {
                var a = new Path
                {
                    Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 2.2,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M7,17 L17,7 M9,7 h8 v8"), Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(a);
            }
            cta.Content = row;
            cta.Template = CtaTemplate();
        }

        private static ControlTemplate CtaTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        /// <summary>Active solid → gradient; active outline → accent border;
        /// inactive → sunken disabled look (design ctaStyle).</summary>
        private static void StyleCta(Button cta, PlanDef p, bool activeCard)
        {
            TextBlock Label()
            {
                var row = (StackPanel)cta.Content;
                return (TextBlock)row.Children[0];
            }
            var label = Label();
            var row2 = (StackPanel)cta.Content;
            var arrow = row2.Children.Count > 1 ? row2.Children[1] as Path : null;

            cta.IsEnabled = activeCard;
            if (!activeCard)
            {
                cta.BorderThickness = new Thickness(1);
                cta.SetResourceReference(Control.BorderBrushProperty, "Cp.Line");
                cta.SetResourceReference(Control.BackgroundProperty, "Cp.Sunken");
                label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
                arrow?.SetResourceReference(Shape.StrokeProperty, "Cp.Faint");
                return;
            }
            if (p.Solid)
            {
                cta.BorderThickness = new Thickness(0);
                cta.SetResourceReference(Control.BackgroundProperty, "Cp.AccentGrad");
                label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.AccentContrast");
                arrow?.SetResourceReference(Shape.StrokeProperty, "Cp.AccentContrast");
            }
            else
            {
                cta.BorderThickness = new Thickness(1);
                cta.SetResourceReference(Control.BorderBrushProperty, "Cp.Accent");
                cta.Background = Brushes.Transparent;
                label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Accent");
                arrow?.SetResourceReference(Shape.StrokeProperty, "Cp.Accent");
            }
        }

        private static void StyleDot(Border dot, bool on)
        {
            var anim = new DoubleAnimation(on ? 18 : 6, new Duration(TimeSpan.FromMilliseconds(280)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            if (double.IsNaN(dot.Width)) dot.Width = 6;
            dot.BeginAnimation(FrameworkElement.WidthProperty, anim);
            dot.SetResourceReference(Border.BackgroundProperty, on ? "Cp.Accent" : "Cp.Hair2");
        }

        private static Button ArrowButton(string pathData, Action onClick)
        {
            var b = new Button
            {
                Width = 30, Height = 30, Cursor = Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var p = new Path
            {
                Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 2.3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse(pathData),
            };
            p.SetResourceReference(Shape.StrokeProperty, "Cp.Muted");
            b.Content = p;
            b.Template = CtaTemplate();
            b.Click += (_, __) => onClick();
            return b;
        }

        private static void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { /* browser launch is best-effort */ }
        }
    }
}
