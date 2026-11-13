using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>
    /// One line of text cut in the MIDDLE ("1_Arch…_Walls") when it does not fit the width it
    /// is given, measured with its own font (FormattedText). Put it in the stretching slot of a
    /// row (last child of a DockPanel, a * grid column) and give it the full name as a tooltip:
    /// <code>
    /// &lt;cad:MiddleEllipsisText Style="{StaticResource CadToBim.RowName}"
    ///     Text="{Binding Name}" ToolTip="{Binding Name}"/&gt;
    /// </code>
    /// A Control (not a TextBlock, whose MeasureOverride is sealed) so FontWeight, FontSize,
    /// Foreground and Padding set on it reach the inner TextBlock. The cut is
    /// <see cref="MiddleEllipsis"/>; <see cref="Shown"/> is what is on screen.
    /// </summary>
    public class MiddleEllipsisText : Control
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(MiddleEllipsisText),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));

        private static readonly DependencyPropertyKey IsCutKey = DependencyProperty.RegisterReadOnly(
            nameof(IsCut), typeof(bool), typeof(MiddleEllipsisText), new PropertyMetadata(false));

        public static readonly DependencyProperty IsCutProperty = IsCutKey.DependencyProperty;

        private readonly TextBlock _block = new TextBlock();

        static MiddleEllipsisText()
        {
            FocusableProperty.OverrideMetadata(typeof(MiddleEllipsisText), new FrameworkPropertyMetadata(false));
            IsTabStopProperty.OverrideMetadata(typeof(MiddleEllipsisText), new FrameworkPropertyMetadata(false));
        }

        public MiddleEllipsisText()
        {
            AddVisualChild(_block);
        }

        /// <summary>The whole text.</summary>
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>True while the text on screen is shortened.</summary>
        public bool IsCut => (bool)GetValue(IsCutProperty);

        /// <summary>The text on screen (Text, or its middle cut).</summary>
        public string Shown => _block.Text;

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index)
        {
            if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
            return _block;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            Thickness pad = Padding;
            string full = Text ?? "";
            string shown = full;
            double room = constraint.Width - pad.Left - pad.Right;
            if (!double.IsInfinity(constraint.Width) && full.Length > 0)
            {
                // Half a pixel of slack: FormattedText and TextBlock round differently.
                shown = MiddleEllipsis.Fit(full, s => WidthOf(s) <= room + 0.5);
            }

            if (!string.Equals(_block.Text, shown, StringComparison.Ordinal)) _block.Text = shown;
            SetValue(IsCutKey, !string.Equals(shown, full, StringComparison.Ordinal));

            var inner = new Size(Math.Max(0, constraint.Width - pad.Left - pad.Right),
                                 Math.Max(0, constraint.Height - pad.Top - pad.Bottom));
            _block.Measure(inner);
            return new Size(_block.DesiredSize.Width + pad.Left + pad.Right,
                            _block.DesiredSize.Height + pad.Top + pad.Bottom);
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            Thickness pad = Padding;
            _block.Arrange(new Rect(pad.Left, pad.Top,
                                    Math.Max(0, arrangeSize.Width - pad.Left - pad.Right),
                                    Math.Max(0, arrangeSize.Height - pad.Top - pad.Bottom)));
            return arrangeSize;
        }

        private double WidthOf(string text)
        {
            var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection, typeface, FontSize,
                                              Brushes.Black, null, TextOptions.GetTextFormattingMode(this), pixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }
    }
}
