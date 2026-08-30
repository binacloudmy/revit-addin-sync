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
    /// Saved Commands J1 — one inline typed-input chip in the prompt bar
    /// (design artboard 4: label + underlined value field on a soft accent
    /// chip). A required chip left empty flags red and blocks send.
    /// </summary>
    public class InputChip : Border
    {
        public SlashInput Input { get; }
        private readonly TextBox _valueBox;
        private readonly TextBlock _label;

        public string Value => _valueBox.Text ?? "";
        public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

        public InputChip(SlashInput input, Action onChanged)
        {
            Input = input;
            CornerRadius = new CornerRadius(8);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(8, 3, 8, 3);
            Margin = new Thickness(0, 0, 6, 4);
            SetResourceReference(BackgroundProperty, "Cp.PurpleSoft");
            SetResourceReference(BorderBrushProperty, "Cp.PurpleLine");

            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            Child = sp;

            _label = new TextBlock
            {
                Text = input.Label ?? input.Name, FontSize = 10.5,
                FontWeight = FontWeight.FromOpenTypeWeight(600),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            };
            _label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.PurpleDeep");
            sp.Children.Add(_label);

            _valueBox = new TextBox
            {
                MinWidth = 52, FontSize = 11.5, FontWeight = FontWeight.FromOpenTypeWeight(600),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _valueBox.SetResourceReference(TextBox.ForegroundProperty, "Cp.Text");
            _valueBox.SetResourceReference(TextBox.BorderBrushProperty, "Cp.PurpleLine");
            _valueBox.SetResourceReference(TextBox.CaretBrushProperty, "Cp.Accent");
            if (input.Type == "number")
            {
                _valueBox.PreviewTextInput += (_, e) =>
                { e.Handled = !e.Text.All(c => char.IsDigit(c) || c == '.' || c == '-'); };
            }
            _valueBox.TextChanged += (_, __) => { FlagRequired(false); onChanged?.Invoke(); };
            sp.Children.Add(_valueBox);
        }

        public void FocusValue() { _valueBox.Focus(); _valueBox.SelectAll(); }

        /// <summary>Red ring + red label while a required value is missing.</summary>
        public void FlagRequired(bool on)
        {
            if (on)
            {
                BorderThickness = new Thickness(1.5);
                SetResourceReference(BorderBrushProperty, "Cp.Red");
                _label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Red");
            }
            else
            {
                BorderThickness = new Thickness(1);
                SetResourceReference(BorderBrushProperty, "Cp.PurpleLine");
                _label.SetResourceReference(TextBlock.ForegroundProperty, "Cp.PurpleDeep");
            }
        }
    }
}
