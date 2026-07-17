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
        // Both point at the plugin landing page's pricing/checkout (subscription.astro
        // on https://revit.bina.cloud) — the canonical source for tiers + purchase,
        // matching this sheet's plan cards. Same host the addin uses for OAuth.
        private const string UpgradeUrl = "https://revit.bina.cloud/subscription/";
        private const string PricingUrl = "https://revit.bina.cloud/subscription/";
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
            new PlanDef { Name = "Plus", Price = "$20", IncLabel = "WHAT'S INCLUDED", CtaLabel = "Upgrade to Plus", Solid = true, Recommended = true,
                Features = new[] { "Everything in Free", "Full daily usage allowance", "All-day use + checks throughout", "Bring-your-own family library", "Email support" } },
            new PlanDef { Name = "Power", Price = "$99", IncLabel = "EVERYTHING IN PLUS, AND", CtaLabel = "Upgrade to Power", Solid = true,
                Features = new[] { "Everything in Plus", "6× the usage of Plus", "Full single-source family library", "Largest models", "Priority support" } },
        };

        /// <summary>Build the sheet BODY (the panel wraps it in its sheet chrome).</summary>
        public static FrameworkElement Build()
        {
            var root = new StackPanel();

            int active = 1; // Plus starts centered (design planIdx: 1)
            var cards = new FrameworkElement[Plans.Length];   // card wrappers (shadow + card)
            var shadows = new Border[Plans.Length];           // soft lift, shown only under the active card
            var ctas = new Button[Plans.Length];
            var dots = new Border[Plans.Length];

            // ── viewport (cards overlaid + centred; neighbours offset by translate) ──
            // Deterministic centring: the active card is centred by HorizontalAlignment,
            // so it does NOT depend on viewport.ActualWidth (flaky mid slide-up). Only the
            // card WIDTH tracks the viewport, re-applied on resize.
            var viewport = new Grid
            {
                ClipToBounds = true,
                Margin = new Thickness(-5, 9, -5, 11),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };

            double CardW() => Math.Max(180, Math.Round(viewport.ActualWidth * 0.82));
            double dragDx = 0;

            for (int i = 0; i < Plans.Length; i++)
            {
                var card = BuildCard(Plans[i], out var cta, out var shadow);
                card.HorizontalAlignment = HorizontalAlignment.Center;
                card.VerticalAlignment = VerticalAlignment.Top;
                card.RenderTransformOrigin = new Point(0.5, 0.5);
                card.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform(0, 0) } };
                cards[i] = card;
                shadows[i] = shadow;
                ctas[i] = cta;
                viewport.Children.Add(card);
            }

            // ── motion ───────────────────────────────────────────────────────
            void Relayout(bool animate)
            {
                if (viewport.ActualWidth <= 0) return;
                double w = CardW();
                double step = w * 0.95 + Gap;   // centre-to-centre: active half + neighbour(0.9) half + gap
                for (int i = 0; i < cards.Length; i++)
                {
                    cards[i].Width = w;
                    int delta = i - active;
                    bool on = delta == 0;
                    var tg = (TransformGroup)cards[i].RenderTransform;
                    var scale = (ScaleTransform)tg.Children[0];
                    var tr = (TranslateTransform)tg.Children[1];
                    double tx = delta * step + dragDx;
                    double sTo = on ? 1.0 : 0.9;
                    double oTo = on ? 1.0 : (Math.Abs(delta) == 1 ? 0.45 : 0.0);
                    Panel.SetZIndex(cards[i], on ? 2 : 1);
                    cards[i].IsHitTestVisible = on;
                    double shTo = on ? 1.0 : 0.0;   // lift only under the active card
                    if (animate)
                    {
                        var dur = new Duration(TimeSpan.FromMilliseconds(320));
                        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                        tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(tx, dur) { EasingFunction = ease });
                        var sa = new DoubleAnimation(sTo, dur) { EasingFunction = ease };
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sa);
                        cards[i].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(oTo, dur) { EasingFunction = ease });
                        shadows[i]?.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(shTo, dur) { EasingFunction = ease });
                    }
                    else
                    {
                        tr.BeginAnimation(TranslateTransform.XProperty, null); tr.X = tx;
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        scale.ScaleX = scale.ScaleY = sTo;
                        cards[i].BeginAnimation(UIElement.OpacityProperty, null); cards[i].Opacity = oTo;
                        if (shadows[i] != null) { shadows[i].BeginAnimation(UIElement.OpacityProperty, null); shadows[i].Opacity = shTo; }
                    }
                    StyleCta(ctas[i], Plans[i], on);
                    if (dots[i] != null) StyleDot(dots[i], on);
                }
            }

            void Go(int idx, bool animate = true)
            {
                active = Math.Max(0, Math.Min(Plans.Length - 1, idx));
                dragDx = 0;
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
                dragDx = e.GetPosition(viewport).X - dragX0;
                Relayout(false);
            };
            void EndDrag(MouseEventArgs e)
            {
                if (!dragging) return;
                dragging = false;
                viewport.ReleaseMouseCapture();
                double dx = e.GetPosition(viewport).X - dragX0;
                double thresh = Math.Max(40, viewport.ActualWidth * 0.16);
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

            root.Loaded += (_, __) =>
            {
                Go(active, animate: false);
                // Re-centre after layout fully settles: viewport.ActualWidth can still
                // be 0/transient at Loaded (esp. during the sheet's slide-up), so a
                // single pass lands off-centre and clips the active card. A deferred
                // pass at Loaded priority guarantees a correct measurement.
                root.Dispatcher.BeginInvoke(new Action(() => Relayout(false)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            };
            return root;
        }

        // ── pieces ───────────────────────────────────────────────────────────

        private static FrameworkElement BuildCard(PlanDef p, out Button cta, out Border shadow)
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
                // Rounded-rectangle badge (mockup shape): taller band with a moderate
                // radius that stays well below half-height, so the ends read as rounded
                // corners — not a fully-rounded pill/ellipse.
                var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 6, 11, 6), VerticalAlignment = VerticalAlignment.Center };
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
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = body,
            };
            card.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");
            card.SetResourceReference(Border.BorderBrushProperty, p.Recommended ? "Cp.Accent" : "Cp.Line");

            // Soft lift for the selected card. The shadow lives on a SIBLING border
            // BEHIND the card (identical rounded silhouette), never on the card itself
            // — an Effect on the text-bearing border would rasterise/blur the text
            // (same pattern as the palette + kebab menu). The card is opaque (Cp.Bg)
            // so it fully covers the shadow border's face; only the blur peeks out.
            shadow = new Border
            {
                CornerRadius = new CornerRadius(15),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Opacity = 0,   // Relayout raises it to 1 only for the active card
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.17, Color = Colors.Black,
                },
            };
            shadow.SetResourceReference(Border.BackgroundProperty, "Cp.Bg");

            // Wrapper carries the peek transform (applied by the caller) so shadow +
            // card scale/translate together. Bottom margin gives the clipped viewport
            // room for the downward shadow so it isn't sheared off.
            var wrap = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            wrap.Children.Add(shadow);
            wrap.Children.Add(card);
            return wrap;
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
                FocusVisualStyle = null,   // no dotted focus rectangle on click
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
