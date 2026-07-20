using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// "You've reached your usage limit" state that replaces the composer at
    /// 100% usage. Admin plan → Upgrade CTA; member plan → workspace-admin card
    /// with a "Notify admin" flow (design's blocked/keyhole section).
    /// </summary>
    public static class BlockedView
    {
        /// <summary>Build the blocked section. `centered` fills the empty thread
        /// area; otherwise it renders as a composer-height bottom section.</summary>
        public static FrameworkElement Build(UsageState u, Action openUpgrade, Func<Task> notifyAdmin, bool centered)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(Padlock());

            var title = new TextBlock
            {
                Text = "You've reached your usage limit",
                FontSize = 14, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 248, Margin = new Thickness(0, 9, 0, 0),
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            stack.Children.Add(title);

            var sub = new TextBlock
            {
                Text = "Upgrade your plan to keep using Copilot.",
                FontSize = 12, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 248, Margin = new Thickness(0, 9, 0, 0),
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            stack.Children.Add(sub);

            if (u.IsAdmin)
            {
                var upgrade = GradientCta("Upgrade plan", BoltIcon());
                upgrade.Margin = new Thickness(0, 13, 0, 0);
                upgrade.Click += (_, __) => openUpgrade();
                stack.Children.Add(upgrade);
            }
            else
            {
                var managed = new TextBlock
                {
                    Text = "Your plan is managed by your workspace admin.",
                    FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
                };
                managed.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                stack.Children.Add(managed);
                stack.Children.Add(AdminCard(u));

                var host = new ContentControl { Margin = new Thickness(0, 10, 0, 0) };
                var notify = GradientCta("Notify admin to upgrade", BellIcon());
                notify.Click += async (_, __) =>
                {
                    notify.IsEnabled = false;
                    try { await notifyAdmin(); } catch { /* stub is best-effort */ }
                    host.Content = SentNote(u);
                };
                host.Content = notify;
                stack.Children.Add(host);
            }

            var wrap = new Border { Padding = new Thickness(18) };
            if (centered)
            {
                wrap.VerticalAlignment = VerticalAlignment.Center;
                wrap.Padding = new Thickness(22, 22, 22, 90);
            }
            else
            {
                wrap.BorderThickness = new Thickness(0, 1, 0, 0);
                wrap.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
            }
            wrap.Child = stack;
            return wrap;
        }

        // 68px 3-D padlock, drawn from the design's SVG geometry + gradients.
        private static FrameworkElement Padlock()
        {
            var canvas = new Canvas { Width = 64, Height = 64 };

            var shackleUnder = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M21,31 V22 A11,11 0 0 1 43,22 V31"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x3f, 0x74, 0xd6)),
                StrokeThickness = 7.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            canvas.Children.Add(shackleUnder);

            var shackle = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M21,31 V22 A11,11 0 0 1 43,22 V31"),
                Stroke = new LinearGradientBrush(
                    Color.FromRgb(0x9c, 0xc6, 0xff), Color.FromRgb(0x4f, 0x8e, 0xf0), new Point(0.2, 0), new Point(0.8, 1)),
                StrokeThickness = 6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            canvas.Children.Add(shackle);

            var body = new Rectangle
            {
                Width = 36, Height = 29, RadiusX = 8.5, RadiusY = 8.5,
                Fill = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x7c, 0xb4, 0xff), 0),
                    new GradientStop(Color.FromRgb(0x4a, 0x8b, 0xf0), 0.55),
                    new GradientStop(Color.FromRgb(0x27, 0x66, 0xd6), 1),
                }, new Point(0, 0), new Point(0.6, 1)),
            };
            Canvas.SetLeft(body, 14); Canvas.SetTop(body, 29);
            canvas.Children.Add(body);

            var sheen = new Rectangle
            {
                Width = 36, Height = 14, RadiusX = 8.5, RadiusY = 8.5,
                Fill = new LinearGradientBrush(
                    Color.FromArgb(0x8c, 0xff, 0xff, 0xff), Color.FromArgb(0x00, 0xff, 0xff, 0xff), 90),
            };
            Canvas.SetLeft(sheen, 14); Canvas.SetTop(sheen, 29);
            canvas.Children.Add(sheen);

            var gleam = new Ellipse
            {
                Width = 11, Height = 4.8,
                Fill = new SolidColorBrush(Color.FromArgb(0x80, 0xff, 0xff, 0xff)),
            };
            Canvas.SetLeft(gleam, 17.5); Canvas.SetTop(gleam, 32.6);
            canvas.Children.Add(gleam);

            var keyBrush = new LinearGradientBrush(
                Colors.White, Color.FromRgb(0xdb, 0xe9, 0xff), 90);
            var keyHead = new Ellipse { Width = 9.2, Height = 9.2, Fill = keyBrush };
            Canvas.SetLeft(keyHead, 27.4); Canvas.SetTop(keyHead, 36.4);
            canvas.Children.Add(keyHead);
            var keyStem = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M29.7,43 L34.3,43 L33.1,51 L30.9,51 Z"),
                Fill = keyBrush,
            };
            canvas.Children.Add(keyStem);

            var box = new Border
            {
                Width = 74, Height = 74, HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Viewbox { Child = canvas, Width = 68, Height = 68 },
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16, ShadowDepth = 12, Direction = 270,
                    Color = Color.FromRgb(0x3b, 0x82, 0xf6), Opacity = 0.32,
                },
            };
            return box;
        }

        private static Border AdminCard(UsageState u)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 9, 11, 9), Margin = new Thickness(0, 10, 0, 0),
            };
            card.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
            card.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var initials = string.Concat((u.AdminName ?? "?").Split(' ')
                .Where(w => w.Length > 0).Take(2).Select(w => char.ToUpperInvariant(w[0])));
            var avatar = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            avatar.SetResourceReference(Border.BackgroundProperty, "Cp.AccentGrad");
            var it = new TextBlock
            {
                Text = initials, FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            it.SetResourceReference(TextBlock.ForegroundProperty, "Cp.AccentContrast");
            avatar.Child = it;
            row.Children.Add(avatar);

            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = u.AdminName, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            col.Children.Add(name);
            var mail = new TextBlock { Text = "Workspace admin · " + u.AdminEmail, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis };
            mail.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            col.Children.Add(mail);
            Grid.SetColumn(col, 1);
            row.Children.Add(col);

            card.Child = row;
            return card;
        }

        private static Border SentNote(UsageState u)
        {
            var firstName = (u.AdminName ?? "Your admin").Split(' ')[0];
            var note = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(13, 11, 13, 11),
            };
            note.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 9, 0),
            };
            badge.SetResourceReference(Border.BackgroundProperty, "Cp.AccentGrad");
            var check = new System.Windows.Shapes.Path
            {
                Width = 9, Height = 9, Stretch = Stretch.Uniform, StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Data = Geometry.Parse("M20,6 L9,17 L4,12"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            check.SetResourceReference(Shape.StrokeProperty, "Cp.AccentContrast");
            badge.Child = check;
            row.Children.Add(badge);

            var text = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            var bold = new System.Windows.Documents.Run("Request sent. ") { FontWeight = FontWeights.SemiBold };
            text.Inlines.Add(bold);
            text.Inlines.Add(new System.Windows.Documents.Run(firstName + " has been asked to upgrade your plan."));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            note.Child = row;
            return note;
        }

        private static Button GradientCta(string label, Path icon)
        {
            var b = new Button
            {
                Height = 38, Cursor = Cursors.Hand, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 248,
            };
            b.SetResourceReference(Control.BackgroundProperty, "Cp.AccentGrad");
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            b.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            icon.Margin = new Thickness(0, 0, 7, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.SetResourceReference(Shape.StrokeProperty, "Cp.AccentContrast");
            row.Children.Add(icon);
            var t = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Cp.AccentContrast");
            row.Children.Add(t);
            b.Content = row;
            return b;
        }

        private static Path BoltIcon() => new Path
        {
            Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 2.1,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M13,2 4.5,13.5 H11 L10,22 18.5,10.5 H12 Z"), Fill = Brushes.Transparent,
        };

        private static Path BellIcon() => new Path
        {
            Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.9,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M18,8 A6,6 0 0 0 6,8 C6,15 3,17 3,17 H21 C21,17 18,15 18,8 M13.7,21 A2,2 0 0 1 10.3,21"),
            Fill = Brushes.Transparent,
        };
    }
}
