#nullable enable
// Helpers still shared after the confirm card was deleted (AI-290): the tool
// label and the framed image thumbnail.

using System;
using System.Windows;
using System.Windows.Controls;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    public static class ConfirmCard
    {
        /// <summary>The add-in's own label for a write tool (English; the card
        /// itself uses the drafter's language).</summary>
        public static string ToolLabel(string tool) => ConfirmCardModel.ToolLabelOf(tool, ms: false);

        /// <summary>An envelope image as a framed thumbnail with its caption. With
        /// <paramref name="expandable"/>, a click toggles it to its full size (up to
        /// the 1024 px the envelope allows) and back.</summary>
        internal static FrameworkElement? Thumbnail(BinaVibe.Copilot.Contracts.ImageDto img, bool expandable = false)
        {
            try
            {
                var bytes = Convert.FromBase64String(img.Base64);
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.StreamSource = new System.IO.MemoryStream(bytes);
                bi.EndInit();
                bi.Freeze();
                var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 2) };
                var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
                frame.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                var smallW = Math.Min(360, img.Width > 0 ? img.Width : 360);
                var image = new Image
                {
                    Source = bi,
                    MaxWidth = smallW,
                    MaxHeight = 240,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                };
                frame.Child = image;
                if (expandable)
                {
                    var big = false;
                    frame.Cursor = System.Windows.Input.Cursors.Hand;
                    frame.ToolTip = "Click to enlarge";
                    frame.MouseLeftButtonUp += (_, __) =>
                    {
                        big = !big;
                        image.MaxWidth = big ? Math.Max(smallW, img.Width > 0 ? img.Width : 1024) : smallW;
                        image.MaxHeight = big ? Math.Max(240, img.Height > 0 ? img.Height : 1024) : 240;
                        frame.ToolTip = big ? "Click to shrink" : "Click to enlarge";
                    };
                }
                stack.Children.Add(frame);
                if (!string.IsNullOrWhiteSpace(img.Caption))
                    stack.Children.Add(CardKit.Text(img.Caption, 10.5, "Cp.Muted", margin: new Thickness(0, 2, 0, 0)));
                return stack;
            }
            catch
            {
                return null;   // a broken thumbnail never breaks the card
            }
        }
    }
}
