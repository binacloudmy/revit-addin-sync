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
            };
            b.SetResourceReference(Border.BorderBrushProperty, border);
            b.SetResourceReference(Border.BackgroundProperty, background);
            // A rounded Border does not clip its children to the rounding (ClipToBounds clips to the rectangle), so a
            // header or footer with its own fill paints square corners over the card's. Clip to the rounded outline.
            b.SizeChanged += (_, e) =>
                b.Clip = e.NewSize.Width <= 0 || e.NewSize.Height <= 0 ? null
                    : new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
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

        // ── The three buttons of the design (Main/Partial/Selection boards) ────────────────────────────────────────
        //   Secondary  h32 radius 8, fill #1B1F27 (Cp.Raised), 1px border at 14% white (Cp.Hair2), 12.5/500, 14px icon.
        //   Ghost      no fill, no border, muted text; for "Leave as is", "Keep all as written", "Show in Revit".
        //   Primary    lime fill (Cp.Accent), dark text, 600; one per card at most.
        // plus Chip, the pill, for filters and counts only. Rows of them wrap (margin right 8, bottom 6).

        private enum Kit { Secondary, Ghost, Primary }

        private static readonly System.Collections.Generic.Dictionary<Kit, ControlTemplate> _templates = new System.Collections.Generic.Dictionary<Kit, ControlTemplate>();

        private static ControlTemplate TemplateFor(Kit kit)
        {
            if (_templates.TryGetValue(kit, out var t)) return t;
            var hover = kit == Kit.Secondary ? "<Setter TargetName='bd' Property='BorderBrush' Value='{DynamicResource Cp.Faint}'/>"
                : kit == Kit.Ghost ? "<Setter TargetName='bd' Property='Background' Value='{DynamicResource Cp.Hover}'/>"
                : "<Setter TargetName='bd' Property='Background' Value='{DynamicResource Cp.BlueHover}'/>";
            var focus = kit == Kit.Primary ? "Cp.Ink" : "Cp.Accent";
            t = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "<Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='8' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>" +
                // A Grid, not a horizontal StackPanel: a label must be able to WRAP (a StackPanel measures its content without a width limit,
                // so a long option label was cut off instead of wrapping onto a second line).
                "<Grid HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'>" +
                "<Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>" +
                "<Path x:Name='spin' Grid.Column='0' Visibility='Collapsed' Width='12' Height='12' Margin='0,0,6,0' Stretch='Uniform' StrokeThickness='1.8' StrokeStartLineCap='Round' StrokeEndLineCap='Round' Data='M8,2.5 A5.5,5.5 0 1 1 2.5,8' Stroke='{TemplateBinding Foreground}' RenderTransformOrigin='0.5,0.5'><Path.RenderTransform><RotateTransform/></Path.RenderTransform></Path>" +
                "<ContentPresenter Grid.Column='1' VerticalAlignment='Center' RecognizesAccessKey='False'/></Grid></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'>" + hover + "</Trigger>" +
                "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='{DynamicResource " + focus + "}'/></Trigger>" +
                "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
            _templates[kit] = t;
            return t;
        }

        /// <summary>The 14px line icons the design uses on result buttons (16x16 viewbox, 1.5 stroke).</summary>
        public static class Icons
        {
            public const string Undo = "M3.5,6.5 H10 A3,3 0 0 1 10,12.5 H6 M3.5,6.5 L6,4 M3.5,6.5 L6,9";
            public const string Copy = "M5.5,5.5 H12 V12 H5.5 Z M3.5,10.5 V3.5 H10";
            public const string Bookmark = "M4.5,2.5 H11.5 V13.5 L8,11 L4.5,13.5 Z";
            public const string Target = "M8,2.5 A5.5,5.5 0 1 1 8,13.5 A5.5,5.5 0 1 1 8,2.5 Z M8,6.4 A1.6,1.6 0 1 1 8,9.6 A1.6,1.6 0 1 1 8,6.4 Z";
            public const string Select = "M3.5,2.5 L12.5,7 L8,8.2 L6.8,12.5 Z";
            public const string Zoom = "M7,2.5 A4.5,4.5 0 1 1 7,11.5 A4.5,4.5 0 1 1 7,2.5 Z M10.4,10.4 L13.5,13.5";
            public const string Grid = "M2.5,3.5 H13.5 V12.5 H2.5 Z M2.5,7.5 H13.5 M6.5,3.5 V12.5";
            public const string Eye = "M1.5,8 C1.5,8 4,3.5 8,3.5 C12,3.5 14.5,8 14.5,8 C14.5,8 12,12.5 8,12.5 C4,12.5 1.5,8 1.5,8 Z M10,8 A2,2 0 1 1 6,8 A2,2 0 1 1 10,8 Z";
        }

        private static Button Make(Kit kit, string label, Action onClick, string? icon, bool compact)
        {
            var fg = kit == Kit.Primary ? "Cp.AccentContrast" : kit == Kit.Ghost ? "Cp.Muted" : "Cp.Ink";
            var b = new Button
            {
                FontSize = 12.5,
                FontWeight = kit == Kit.Primary ? FontWeights.SemiBold : FontWeights.Medium,
                MinHeight = compact ? 30 : 32,
                Padding = kit == Kit.Ghost ? new Thickness(10, 0, 10, 0) : new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 6),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null,
                Template = TemplateFor(kit),
            };
            if (string.IsNullOrEmpty(icon)) b.Content = label;
            else
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var path = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(icon), Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, fg);
                row.Children.Add(path);
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                b.Content = row;
                b.ToolTip = label;
            }
            b.SetResourceReference(Control.ForegroundProperty, fg);
            if (kit == Kit.Ghost) b.Background = Brushes.Transparent;
            else b.SetResourceReference(Control.BackgroundProperty, kit == Kit.Secondary ? "Cp.Raised" : "Cp.Accent");
            if (kit == Kit.Secondary) b.SetResourceReference(Control.BorderBrushProperty, "Cp.Hair2");
            else b.BorderBrush = Brushes.Transparent;
            b.Click += (_, __) => ClickGuard.Run(b, onClick);   // a spinner while Revit works, repeat clicks ignored
            return b;
        }

        /// <summary>Let a button's label wrap onto as many lines as it needs, left aligned, at least 32 high: a long option label
        /// ("(t193c) sc1-100 tx1.8mm r2.5mm KKP (most used, 340 doors)") is never cut off. Call it on a full-width button.</summary>
        public static Button WrapLabel(Button button, string label)
        {
            button.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left };
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Padding = new Thickness(12, 7, 12, 7);
            button.ToolTip = label;
            return button;
        }

        public static Button Primary(string label, Action onClick, string? icon = null, bool compact = false) => Make(Kit.Primary, label, onClick, icon, compact);

        public static Button Secondary(string label, Action onClick, string? icon = null, bool compact = false) => Make(Kit.Secondary, label, onClick, icon, compact);

        public static Button Ghost(string label, Action onClick, string? icon = null, bool compact = false) => Make(Kit.Ghost, label, onClick, icon, compact);

        /// <summary>Scroll an element into view once layout has placed it. A panel that opens under a row (the source viewer, the
        /// steps) grows below the visible part of the thread when the pane is scrolled up; without this it looks like the click did
        /// nothing (live test of 0.0.105).</summary>
        public static void Reveal(FrameworkElement element)
        {
            if (element == null) return;
            element.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { element.BringIntoView(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] reveal: " + ex.Message); }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>The pill: a filter or a count ("Doors . 12"). Not an action; actions are Secondary, Ghost or Primary.</summary>
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
