#nullable enable
// Small code-built WPF helpers shared by the protocol-3 cards (ConfirmCard,
// AskCard, TurnEvidenceView). Theme-aware: every colour is a Cp.* resource.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal static class CardKit
    {
        public static TextBlock Text(string text, double size = 12, string brush = "Cp.Ink",
            FontWeight? weight = null, bool wrap = true, Thickness? margin = null, bool italic = false, bool mono = false)
        {
            var t = new TextBlock
            {
                Text = text ?? "",
                FontSize = size,
                FontWeight = weight ?? FontWeights.Normal,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Margin = margin ?? new Thickness(0),
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            if (mono) t.SetResourceReference(TextBlock.FontFamilyProperty, "Cp.FontMono");
            return t;
        }

        public static Border Card(string border, string background, Thickness? margin = null, double radius = 12)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(radius),
                BorderThickness = new Thickness(1),
                Margin = margin ?? new Thickness(0, 4, 0, 6),
                ClipToBounds = true,
            };
            b.SetResourceReference(Border.BorderBrushProperty, border);
            b.SetResourceReference(Border.BackgroundProperty, background);
            return b;
        }

        public static Border Header(string background, UIElement content, double radius = 12)
        {
            var h = new Border
            {
                Padding = new Thickness(13, 9, 13, 9),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(radius, radius, 0, 0),
                Child = content,
            };
            h.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
            h.SetResourceReference(Border.BackgroundProperty, background);
            return h;
        }

        public static Button Primary(string label, Action onClick)
        {
            var b = new Button
            {
                Content = label,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.SetResourceReference(FrameworkElement.StyleProperty, "Cp.RunButton");
            b.Click += (_, __) => onClick();
            return b;
        }

        public static Button Secondary(string label, Action onClick)
        {
            var b = new Button
            {
                Content = label,
                FontSize = 12.5,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.SetResourceReference(Control.ForegroundProperty, "Cp.Ink2");
            b.SetResourceReference(Control.BorderBrushProperty, "Cp.Line");
            FlatButton.Apply(b, 9, withBorder: true);
            b.Click += (_, __) => onClick();
            return b;
        }

        public static Button Chip(string label, Action onClick)
        {
            var b = new Button
            {
                Content = label,
                FontSize = 11.5,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 4),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.SetResourceReference(Control.ForegroundProperty, "Cp.Ink2");
            b.SetResourceReference(Control.BorderBrushProperty, "Cp.Line");
            FlatButton.Apply(b, 999, withBorder: true);
            b.Click += (_, __) => onClick();
            return b;
        }

        /// <summary>A read-only, selectable, scrollable mono block (code shown in full).</summary>
        public static FrameworkElement Code(string code, double maxHeight = 260)
        {
            var box = new TextBox
            {
                Text = code ?? "",
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                BorderThickness = new Thickness(0),
                FontSize = 11,
                Padding = new Thickness(8, 6, 8, 6),
                MaxHeight = maxHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            box.SetResourceReference(Control.BackgroundProperty, "Cp.CodeBg");
            box.SetResourceReference(Control.ForegroundProperty, "Cp.CodeFg");
            box.SetResourceReference(Control.FontFamilyProperty, "Cp.FontMono");
            var frame = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(0, 6, 0, 6), Child = box };
            frame.SetResourceReference(Border.BackgroundProperty, "Cp.CodeBg");
            return frame;
        }
    }
}
