using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Saved Commands J1 — one inline typed-input chip in the composer
    /// (design pendingChips): accent-tinted pill, 10.5px/600 accent label,
    /// a 78px transparent value field with only a bottom rule, placeholder
    /// "type here"/"0". A required chip left empty flags danger (tinted bg,
    /// danger border/label, "required" placeholder) and blocks send.
    /// </summary>
    public class InputChip : Border
    {
        public SlashInput Input { get; }
        private readonly TextBox _valueBox;
        private readonly TextBlock _label;
        private readonly TextBlock _placeholder;
        private bool _flagged;

        // Design tints (accent #2a69c6 / danger #d95757 at the mock's mixes).
        private static readonly Brush AccentBg = Frozen(Color.FromArgb(0x17, 0x2A, 0x69, 0xC6));   // 9%
        private static readonly Brush AccentLine = Frozen(Color.FromArgb(0x4D, 0x2A, 0x69, 0xC6)); // 30%
        private static readonly Brush DangerBg = Frozen(Color.FromArgb(0x1A, 0xD9, 0x57, 0x57));   // 10%

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public string Value => _valueBox.Text ?? "";
        public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

        public InputChip(SlashInput input, Action onChanged)
        {
            Input = input;
            CornerRadius = new CornerRadius(8);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(9, 4, 9, 4);
            Margin = new Thickness(0, 0, 6, 4);
            VerticalAlignment = VerticalAlignment.Center;
            Background = AccentBg;
            BorderBrush = AccentLine;

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            Child = sp;

            _label = new TextBlock
            {
                Text = input.Label ?? input.Name, FontSize = 10.5,
                FontWeight = FontWeight.FromOpenTypeWeight(600),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
            };
            _label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
            sp.Children.Add(_label);

            // Value field: transparent, bottom rule only, fixed 78px like the
            // design; the placeholder is an overlay TextBlock (WPF TextBox has
            // no native watermark).
            var fieldHost = new Grid { Width = 78, VerticalAlignment = VerticalAlignment.Center };
            _valueBox = new TextBox
            {
                FontSize = 11.5, FontWeight = FontWeight.FromOpenTypeWeight(600),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = AccentLine,
                Padding = new Thickness(0, 1, 0, 1), VerticalContentAlignment = VerticalAlignment.Center,
            };
            _valueBox.SetResourceReference(TextBox.ForegroundProperty, "Cp.Text");
            _valueBox.SetResourceReference(TextBox.CaretBrushProperty, "Cp.Accent");
            if (input.Type == "number")
            {
                _valueBox.PreviewTextInput += (_, e) =>
                { e.Handled = !e.Text.All(c => char.IsDigit(c) || c == '.' || c == '-'); };
            }
            _placeholder = new TextBlock
            {
                Text = input.Type == "number" ? "0" : "type here",
                FontSize = 11.5, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 0, 0),
            };
            _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            _valueBox.TextChanged += (_, __) =>
            {
                _placeholder.Visibility = IsEmpty ? Visibility.Visible : Visibility.Collapsed;
                if (_flagged) FlagRequired(false);
                onChanged?.Invoke();
            };
            fieldHost.Children.Add(_valueBox);
            fieldHost.Children.Add(_placeholder);
            sp.Children.Add(fieldHost);
        }

        public void FocusValue() { _valueBox.Focus(); _valueBox.SelectAll(); }

        /// <summary>Danger tint + "required" placeholder while a required value
        /// is missing (design flagged state).</summary>
        public void FlagRequired(bool on)
        {
            _flagged = on;
            if (on)
            {
                Background = DangerBg;
                SetResourceReference(BorderBrushProperty, "Cp.Red");
                _label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Red");
                _valueBox.SetResourceReference(TextBox.BorderBrushProperty, "Cp.Red");
                _placeholder.Text = "required";
                _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Red");
            }
            else
            {
                Background = AccentBg;
                BorderBrush = AccentLine;
                _label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
                _valueBox.BorderBrush = AccentLine;
                _placeholder.Text = Input.Type == "number" ? "0" : "type here";
                _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Faint");
            }
        }
    }
}
