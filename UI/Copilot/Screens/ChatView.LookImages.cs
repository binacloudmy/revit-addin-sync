// ChatView: the `look` tool's view images under the reply (row G-03).
// Kept in its own partial file so the look feature does not grow ChatView.xaml.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Copilot.Controls;

namespace RevitWebAppSync.UI.Copilot.Screens
{
    public partial class ChatView
    {
        /// <summary>The image(s) the copilot looked at, each with its view name,
        /// so the drafter can check the comments against the same picture.
        /// Decoded from memory; nothing is written to disk.</summary>
        private FrameworkElement LookImagesRow(List<LookImageStash.LookImage> images, double maxWidth)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 4) };
            double width = double.IsNaN(maxWidth) || double.IsInfinity(maxWidth) || maxWidth <= 0
                ? 360 : Math.Min(maxWidth, 520);
            foreach (var img in images)
            {
                if (img == null || string.IsNullOrEmpty(img.Base64)) continue;
                try
                {
                    var bi = new BitmapImage();
                    using (var ms = new MemoryStream(Convert.FromBase64String(img.Base64)))
                    {
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;   // decode now; stream can close
                        bi.StreamSource = ms;
                        bi.DecodePixelWidth = (int)Math.Max(200, width * 1.5);
                        bi.EndInit();
                    }
                    bi.Freeze();
                    panel.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(1),
                        BorderBrush = CopilotColors.From("#140F1B2D"),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new Image { Source = bi, Width = width, Stretch = Stretch.Uniform },
                        ToolTip = img.ViewName,
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrEmpty(img.ViewName) ? "Pandangan yang dilihat" : "Pandangan yang dilihat: " + img.ViewName,
                        FontSize = 10, Foreground = CopilotColors.From("#586273"),
                        Margin = new Thickness(0, 2, 0, 6), TextWrapping = TextWrapping.Wrap,
                    });
                }
                catch { /* a broken image never breaks the reply */ }
            }
            return panel;
        }
    }
}
