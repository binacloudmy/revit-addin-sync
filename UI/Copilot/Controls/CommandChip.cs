using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// The slash-command chip — ONE shared style used in both places so they can
    /// never drift: a clean WHITE pill with a subtle hairline, blue icon + "/name"
    /// (design isCmdUser chip, line 170). Reads as a white token, not a blue-on-blue
    /// blob. The ONLY difference: pass <paramref name="onRemove"/> for the staged
    /// composer chip to add a flat × remove button; omit it in the sent bubble.
    /// </summary>
    internal static class CommandChip
    {
        // White-pill bg (design rgba(255,255,255,.9)) + a subtle neutral hairline
        // (design uses a soft shadow, but a shadow would blur the chip text — a
        // hairline gives the same crisp edge). Constant in both themes: the design
        // keeps the chip white on the gray/dark bubble either way.
        private static readonly Brush WhiteFill = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Hairline = Frozen(Color.FromArgb(0x22, 0x0F, 0x17, 0x2A));

        public static Border Build(SlashTool tool, Action onRemove = null)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                // Trim the right padding when the × follows so it sits snug.
                Padding = new Thickness(7, 3, onRemove != null ? 4 : 9, 3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                SnapsToDevicePixels = true,
                Background = WhiteFill,
                BorderBrush = Hairline,
            };

            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(Icon(tool.IconKey, 13, "Cp.Accent"));

            // "/" and the name both full-strength accent (mockup: "icon / Name"), with a
            // little air on each side of the slash so it reads as a separator, not glued on.
            var slash = new TextBlock { Text = "/", FontSize = 11.5, FontWeight = FontWeights.ExtraBold, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            slash.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Accent");
            sp.Children.Add(slash);
            var name = new TextBlock { Text = tool.Name, FontSize = 11.5, FontWeight = FontWeight.FromOpenTypeWeight(680), Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Accent");
            sp.Children.Add(name);

            if (onRemove != null)
            {
                var x = new Button
                {
                    Width = 18, Height = 18, Margin = new Thickness(5, 0, 0, 0),
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
                // Same flat icon-button chrome as the thumbs/copy buttons.
                FlatButton.Apply(x, 5);
                x.Click += (_, e) => { e.Handled = true; onRemove(); };
                sp.Children.Add(x);
            }

            chip.Child = sp;
            return chip;
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
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
    }
}
