using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// The slash-command chip (design's binacmd-chip, line 1532): a rounded accent
    /// pill — tool icon + "/ Tool name" — shown in the composer once a tool is
    /// picked and again inside the sent user bubble. Accent@12% fill, accent@40%
    /// hairline, all theme-aware. Pass <paramref name="onRemove"/> for the composer
    /// variant (adds an × to clear the pending command); omit it in the bubble.
    /// </summary>
    internal static class CommandChip
    {
        public static Border Build(SlashTool tool, Action onRemove = null)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 4, onRemove != null ? 4 : 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Cp.BlueSoft");
            chip.SetResourceReference(Border.BorderBrushProperty, "Cp.PurpleLine");

            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(Icon(tool.IconKey, 13, "Cp.Accent"));

            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), FontSize = 11.5, FontWeight = FontWeights.SemiBold };
            var slash = new Run("/ ") { FontWeight = FontWeights.Bold };
            slash.SetResourceReference(Run.ForegroundProperty, "Cp.Accent");
            var name = new Run(tool.Name);
            label.Inlines.Add(slash);
            label.Inlines.Add(name);
            label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Accent");
            sp.Children.Add(label);

            if (onRemove != null)
            {
                var x = new Button
                {
                    Width = 18, Height = 18, Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), FocusVisualStyle = null,
                    VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove command",
                };
                var xIcon = new Path
                {
                    Width = 9, Height = 9, Stretch = Stretch.Uniform, StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M5,5 L15,15 M15,5 L5,15"),
                };
                xIcon.SetResourceReference(Shape.StrokeProperty, "Cp.Accent");
                x.Content = xIcon;
                x.Template = GhostButtonTemplate();
                x.Click += (_, e) => { e.Handled = true; onRemove(); };
                sp.Children.Add(x);
            }

            chip.Child = sp;
            return chip;
        }

        // Icon rendered in a 24×24 canvas inside a Viewbox → consistent framing.
        private static FrameworkElement Icon(string key, double size, string strokeKey)
        {
            var path = new Path
            {
                Data = ToolCatalog.Icon(key),
                StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            if (ToolCatalog.IconFilled(key)) path.SetResourceReference(Shape.FillProperty, strokeKey);
            else path.SetResourceReference(Shape.StrokeProperty, strokeKey);
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(path);
            return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
        }

        private static ControlTemplate GhostButtonTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            return t;
        }
    }
}
