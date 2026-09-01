using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// "Update required" wall that replaces the composer when the running build
    /// is below the feed's minAddinVersion floor.
    ///
    /// Deliberately built like <see cref="BlockedView"/> (same theme keys, same
    /// GradientCta, same transparent-wrap hit-test trick) so the two walls read
    /// as one family — but unlike the usage wall this one has no dismissal and
    /// no escape hatch: the only way past it is to update and restart Revit.
    /// </summary>
    public static class UpdateRequiredView
    {
        /// <summary>Build the wall. `centered` fills the empty thread area;
        /// otherwise it renders as a composer-height bottom section. `onUpdate`
        /// stages the payload and reports (0..1, status); null in the states
        /// where staging cannot help (manual install).</summary>
        public static FrameworkElement Build(
            UpdateGate gate,
            Func<IProgress<(double Fraction, string Status)>, Task> onUpdate,
            bool centered)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(Badge());

            var title = new TextBlock
            {
                Text = gate.Reason == GateReason.Staged
                    ? "Restart Revit to finish updating"
                    : "Update required",
                FontSize = 14, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 248, Margin = new Thickness(0, 9, 0, 0),
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
            stack.Children.Add(title);

            var sub = new TextBlock
            {
                Text = SubText(gate),
                FontSize = 12, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 248, Margin = new Thickness(0, 9, 0, 0),
            };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
            stack.Children.Add(sub);

            // Manual install and "already staged" have no useful action here —
            // one needs a reinstall, the other a restart. Offering "Update now"
            // in either state would download a payload nothing will read.
            if (onUpdate != null &&
                (gate.Reason == GateReason.UpdateAvailable ||
                 gate.Reason == GateReason.NoPayload))
            {
                var bar = new ProgressBar
                {
                    Height = 6, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed,
                    Margin = new Thickness(0, 13, 0, 0), MinWidth = 248,
                };
                var status = new TextBlock
                {
                    FontSize = 11.5, TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 248,
                    Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed,
                };
                status.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");

                var cta = GradientCta("Update now", DownloadIcon());
                cta.Margin = new Thickness(0, 13, 0, 0);

                bool busy = false;
                cta.Click += async (_, __) =>
                {
                    if (busy) return;
                    busy = true;
                    cta.IsEnabled = false;
                    bar.IsIndeterminate = true;
                    bar.Visibility = Visibility.Visible;
                    status.Text = "Starting download…";
                    status.Visibility = Visibility.Visible;

                    var progress = new Progress<(double Fraction, string Status)>(p =>
                    {
                        bar.IsIndeterminate = false;
                        bar.Value = p.Fraction;
                        status.Text = p.Status;
                    });

                    try
                    {
                        await onUpdate(progress);
                        // Success raises GateChanged, which tears this view down and
                        // rebuilds it in the Staged state. Nothing to do here.
                    }
                    catch (Exception ex)
                    {
                        bar.Visibility = Visibility.Collapsed;
                        status.Text = "Update failed: " + ex.Message;
                        CtaLabel(cta, "Try again");
                        cta.IsEnabled = true;
                    }
                    finally
                    {
                        busy = false;
                    }
                };

                stack.Children.Add(cta);
                stack.Children.Add(bar);
                stack.Children.Add(status);
            }

            // Transparent (not null) so the whole wall hit-tests — a null Background
            // only raises mouse events over the child glyphs, not the gaps.
            var wrap = new Border { Padding = new Thickness(18), Background = Brushes.Transparent };
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

        private static string SubText(UpdateGate gate)
        {
            var required = gate.Required != null ? gate.Required.ToString() : "a newer version";
            switch (gate.Reason)
            {
                case GateReason.Staged:
                    return "The update is installed. Restart Revit to keep using Copilot.";
                case GateReason.ManualInstall:
                    return $"Copilot {gate.Current} is running from a manual install, so version {required} " +
                           "cannot be applied automatically. Reinstall BINA Sync, then restart Revit.";
                case GateReason.NoPayload:
                    return $"Copilot {gate.Current} is no longer supported. Version {required} is required — " +
                           "check your connection, then try again.";
                default:
                    return $"Copilot {gate.Current} is no longer supported. " +
                           $"Update to {required} to keep using it.";
            }
        }

        // 68px 3-D download badge — same gradient family and drop shadow as
        // BlockedView's padlock, different glyph so the two walls are not
        // mistaken for each other at a glance.
        private static FrameworkElement Badge()
        {
            var canvas = new Canvas { Width = 64, Height = 64 };

            var disc = new Ellipse
            {
                Width = 46, Height = 46,
                Fill = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x7c, 0xb4, 0xff), 0),
                    new GradientStop(Color.FromRgb(0x4a, 0x8b, 0xf0), 0.55),
                    new GradientStop(Color.FromRgb(0x27, 0x66, 0xd6), 1),
                }, new Point(0, 0), new Point(0.6, 1)),
            };
            Canvas.SetLeft(disc, 9); Canvas.SetTop(disc, 9);
            canvas.Children.Add(disc);

            var sheen = new Ellipse
            {
                Width = 34, Height = 13,
                Fill = new SolidColorBrush(Color.FromArgb(0x66, 0xff, 0xff, 0xff)),
            };
            Canvas.SetLeft(sheen, 15); Canvas.SetTop(sheen, 14);
            canvas.Children.Add(sheen);

            var arrowBrush = new LinearGradientBrush(
                Colors.White, Color.FromRgb(0xdb, 0xe9, 0xff), 90);
            var arrow = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M32,18 V36 M24,29 L32,37 L40,29"),
                Stroke = arrowBrush, StrokeThickness = 4.4,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            canvas.Children.Add(arrow);

            var tray = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M22,42 H42"),
                Stroke = arrowBrush, StrokeThickness = 4.4, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            canvas.Children.Add(tray);

            return new Border
            {
                Width = 74, Height = 74, HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Viewbox { Child = canvas, Width = 68, Height = 68 },
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16, ShadowDepth = 12, Direction = 270,
                    Color = Color.FromRgb(0x3b, 0x82, 0xf6), Opacity = 0.32,
                },
            };
        }

        private static void CtaLabel(Button cta, string label)
        {
            if (cta.Content is StackPanel row)
                foreach (var child in row.Children)
                    if (child is TextBlock t) { t.Text = label; return; }
        }

        // Same shape as BlockedView.GradientCta — kept local rather than made
        // public there, so the usage wall's visual contract stays its own.
        private static Button GradientCta(string label, System.Windows.Shapes.Path icon)
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

        private static System.Windows.Shapes.Path DownloadIcon() => new System.Windows.Shapes.Path
        {
            Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Data = Geometry.Parse("M12,3 V15 M6,10 L12,16 L18,10 M4,20 H20"),
            Fill = Brushes.Transparent,
        };
    }
}
