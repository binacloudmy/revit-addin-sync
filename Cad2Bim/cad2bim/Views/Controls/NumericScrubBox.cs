using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cad2Bim.Views.Controls {
    using Point = System.Windows.Point; // Cad2Bim.Point (model record) would shadow it here

    /// <summary>
    /// Numeric field that supports horizontal click-and-drag scrubbing and, on a plain
    /// click, typed entry. Esc cancels either mode; Enter or focus loss commits typing.
    /// </summary>
    [TemplatePart(Name = ValueTextPartName, Type = typeof(TextBlock))]
    [TemplatePart(Name = EditorPartName, Type = typeof(TextBox))]
    public sealed class NumericScrubBox : Control {
        public const string DisplayPartName = "PART_Display";
        public const string ValueTextPartName = "PART_ValueText";
        public const string EditorPartName = "PART_Editor";

        private const double DragThresholdPx = 3.0;

        // Dependency Properties
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(NumericScrubBox),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, _) => ((NumericScrubBox)d).UpdateValueText(),
                (d, value) => ((NumericScrubBox)d).Clamp((double)value)));

        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
            nameof(Minimum), typeof(double), typeof(NumericScrubBox),
            new PropertyMetadata(double.MinValue, OnRangeChanged));

        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
            nameof(Maximum), typeof(double), typeof(NumericScrubBox),
            new PropertyMetadata(double.MaxValue, OnRangeChanged));

        public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
            nameof(Step), typeof(double), typeof(NumericScrubBox),
            new PropertyMetadata(1.0));

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(NumericScrubBox),
            new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
            nameof(Format), typeof(string), typeof(NumericScrubBox),
            new PropertyMetadata("0.##", (d, _) => ((NumericScrubBox)d).UpdateValueText()));

        private TextBlock? valueText;
        private TextBox? editor;

        private bool mouseDown;
        private bool dragging;
        private Point dragStart;
        private double dragStartValue;

        static NumericScrubBox() {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(NumericScrubBox),
                new FrameworkPropertyMetadata(typeof(NumericScrubBox)));
        }

        public double Value {
            get => (double)this.GetValue(ValueProperty);
            set => this.SetValue(ValueProperty, value);
        }

        public double Minimum {
            get => (double)this.GetValue(MinimumProperty);
            set => this.SetValue(MinimumProperty, value);
        }

        public double Maximum {
            get => (double)this.GetValue(MaximumProperty);
            set => this.SetValue(MaximumProperty, value);
        }

        /// <summary>Value change per pixel of horizontal drag.</summary>
        public double Step {
            get => (double)this.GetValue(StepProperty);
            set => this.SetValue(StepProperty, value);
        }

        public string Label {
            get => (string)this.GetValue(LabelProperty);
            set => this.SetValue(LabelProperty, value);
        }

        public string Format {
            get => (string)this.GetValue(FormatProperty);
            set => this.SetValue(FormatProperty, value);
        }

        private bool IsEditing => this.editor?.Visibility == Visibility.Visible;

        public override void OnApplyTemplate() {
            base.OnApplyTemplate();

            if (this.editor != null) {
                this.editor.KeyDown -= this.OnEditorKeyDown;
                this.editor.LostKeyboardFocus -= this.OnEditorLostFocus;
            }

            this.valueText = this.GetTemplateChild(ValueTextPartName) as TextBlock;
            this.editor = this.GetTemplateChild(EditorPartName) as TextBox;

            if (this.editor != null) {
                this.editor.KeyDown += this.OnEditorKeyDown;
                this.editor.LostKeyboardFocus += this.OnEditorLostFocus;
            }

            this.UpdateValueText();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) {
            if (this.IsEditing) {
                base.OnMouseLeftButtonDown(e);
                return;
            }

            this.mouseDown = true;
            this.dragging = false;
            this.dragStart = e.GetPosition(this);
            this.dragStartValue = this.Value;
            this.CaptureMouse();
            this.Focus();
            e.Handled = true;

            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e) {
            if (this.mouseDown) {
                double deltaX = e.GetPosition(this).X - this.dragStart.X;

                if (!this.dragging && Math.Abs(deltaX) >= DragThresholdPx) {
                    this.dragging = true;
                }

                if (this.dragging) {
                    this.Value = this.Clamp(this.dragStartValue + (deltaX * this.Step));
                }
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {
            if (this.mouseDown) {
                bool wasClick = !this.dragging;
                this.mouseDown = false;
                this.dragging = false;
                this.ReleaseMouseCapture();
                e.Handled = true;

                if (wasClick) {
                    this.BeginEdit();
                }
            }

            base.OnMouseLeftButtonUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e) {
            // Esc mid-drag restores the pre-drag value.
            if (e.Key == Key.Escape && this.mouseDown) {
                this.Value = this.dragStartValue;
                this.mouseDown = false;
                this.dragging = false;
                this.ReleaseMouseCapture();
                e.Handled = true;
            }

            base.OnKeyDown(e);
        }

        private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
            var box = (NumericScrubBox)d;
            box.CoerceValue(ValueProperty);
        }

        private void BeginEdit() {
            if (this.editor == null) {
                return;
            }

            this.editor.Text = this.Value.ToString(CultureInfo.CurrentCulture);
            this.editor.Visibility = Visibility.Visible;
            this.editor.Focus();
            this.editor.SelectAll();
        }

        private void EndEdit(bool commit) {
            if (this.editor == null || !this.IsEditing) {
                return;
            }

            if (commit && TryParse(this.editor.Text, out double parsed)) {
                this.Value = this.Clamp(parsed);
            }

            this.editor.Visibility = Visibility.Collapsed;
        }

        private void OnEditorKeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter) {
                this.EndEdit(commit: true);
                e.Handled = true;
            } else if (e.Key == Key.Escape) {
                this.EndEdit(commit: false);
                e.Handled = true;
            }
        }

        private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
            => this.EndEdit(commit: true);

        private static bool TryParse(string text, out double value)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private double Clamp(double value)
            => Math.Clamp(value, this.Minimum, this.Maximum);

        private void UpdateValueText() {
            if (this.valueText != null) {
                this.valueText.Text = this.Value.ToString(this.Format, CultureInfo.CurrentCulture);
            }
        }
    }
}
