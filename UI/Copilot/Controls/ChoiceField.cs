#nullable enable
// The ask form's inputs in the pane's dark look, the same as the composer: fill Cp.Run.Composer, a 1px Cp.Run.LineStrong
// border that turns Cp.Accent on focus, 12.5 text in Cp.Ink, padding 10/8, radius 8, min-height 34.
//
//   ChoiceField     a Choice field. Not a native ComboBox: a bordered box that shows the picked label (or "Choose..." in
//                   Cp.Faint, nothing preselected) with a chevron, and opens a Popup list on the card fill (Cp.Bg, 1px Cp.Hair2),
//                   rows h32 with a hover wash (Cp.Hover) and a check on the picked row.
//                   Keyboard: Enter or Space (or Down) opens; Up / Down move; Enter or Space picks; Esc closes.
//   FormTextField   a text or number field with the same look around a plain TextBox.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal sealed class ChoiceField : Border
    {
        private readonly List<AskOptionModel> _choices;
        private readonly string _placeholder;
        private readonly TextBlock _label;
        private readonly Popup _popup;
        private readonly Border _list;
        private readonly List<Border> _rows = new List<Border>();
        private readonly List<FrameworkElement> _checks = new List<FrameworkElement>();
        private readonly List<TextBlock> _rowText = new List<TextBlock>();
        private int _selected = -1;
        private int _hot = -1;
        private int _closedTick = Environment.TickCount - 1000;   // TickCount may be negative after weeks of uptime: compare by difference only

        /// <summary>Raised after the drafter picks a row.</summary>
        public event Action? Changed;

        /// <summary>The picked choice's id, or "" while nothing is picked.</summary>
        public string Value => _selected < 0 ? "" : _choices[_selected].Id;

        /// <summary>The value getter the ask card keeps per field.</summary>
        public Func<string> Getter => () => Value;

        public ChoiceField(IEnumerable<AskOptionModel> choices, bool ms, string? accessibleName = null)
        {
            _choices = choices.ToList();
            _placeholder = AskCardModel.ChoosePlaceholder(ms);

            Focusable = true;
            FocusVisualStyle = null;
            Cursor = Cursors.Hand;
            MinHeight = 34;
            CornerRadius = new CornerRadius(8);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(10, 8, 10, 8);
            SnapsToDevicePixels = true;
            SetResourceReference(BackgroundProperty, "Cp.Run.Composer");
            SetResourceReference(BorderBrushProperty, "Cp.Run.LineStrong");
            if (!string.IsNullOrEmpty(accessibleName)) AutomationProperties.SetName(this, accessibleName);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _label = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(_label);
            var chevron = LineIcon.Make(LineIcon.Chevron, 14, "Cp.Faint", 1.6);
            chevron.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(chevron, 1);
            grid.Children.Add(chevron);

            _list = BuildList();
            _popup = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 4,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None,
                Child = _list,
            };
            _popup.Opened += (_, __) => UpdateBorder();
            _popup.Closed += (_, __) => { _closedTick = Environment.TickCount; UpdateBorder(); };
            grid.Children.Add(_popup);          // in the logical tree, so the list resolves the Cp.* resources
            Child = grid;

            IsKeyboardFocusWithinChanged += (_, __) => UpdateBorder();
            ShowSelection();
        }

        // ── Look ─────────────────────────────────────────────────────────────────────────────────────────────────

        private Border BuildList()
        {
            var rows = new StackPanel();
            for (var i = 0; i < _choices.Count; i++)
            {
                var idx = i;
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var t = CardKit.Text(_choices[i].Label, 12.5, "Cp.Ink", wrap: false);
                t.VerticalAlignment = VerticalAlignment.Center;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                g.Children.Add(t);
                var check = LineIcon.Make(LineIcon.Check, 14, "Cp.Accent", 1.8);
                check.Margin = new Thickness(8, 0, 0, 0);
                check.Visibility = Visibility.Collapsed;
                Grid.SetColumn(check, 1);
                g.Children.Add(check);

                var row = new Border
                {
                    Height = 32,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 0, 8, 0),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    Child = g,
                };
                row.MouseEnter += (_, __) => { _hot = idx; PaintRows(); };
                row.MouseLeftButtonUp += (_, e) => { Pick(idx); e.Handled = true; };
                rows.Children.Add(row);
                _rows.Add(row);
                _checks.Add(check);
                _rowText.Add(t);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 7 * 32,
                Content = rows,
            };
            var list = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                Child = scroll,
            };
            list.SetResourceReference(BackgroundProperty, "Cp.Bg");
            list.SetResourceReference(BorderBrushProperty, "Cp.Hair2");
            return list;
        }

        private void UpdateBorder() =>
            SetResourceReference(BorderBrushProperty, IsKeyboardFocusWithin || _popup.IsOpen ? "Cp.Accent" : "Cp.Run.LineStrong");

        private void ShowSelection()
        {
            var none = _selected < 0;
            _label.Text = none ? _placeholder : _choices[_selected].Label;
            _label.SetResourceReference(TextBlock.ForegroundProperty, none ? "Cp.Faint" : "Cp.Ink");
            PaintRows();
        }

        private void PaintRows()
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (i == _hot) _rows[i].SetResourceReference(BackgroundProperty, "Cp.Hover");
                else _rows[i].Background = Brushes.Transparent;
                _checks[i].Visibility = i == _selected ? Visibility.Visible : Visibility.Collapsed;
                _rowText[i].FontWeight = i == _selected ? FontWeights.Medium : FontWeights.Normal;
            }
        }

        // ── Behaviour ────────────────────────────────────────────────────────────────────────────────────────────

        private void Open()
        {
            if (_choices.Count == 0 || _popup.IsOpen) return;
            _list.Width = Math.Max(120, ActualWidth);
            _hot = _selected;
            PaintRows();
            _popup.IsOpen = true;
            if (_selected >= 0) _rows[_selected].BringIntoView();
        }

        private void Close()
        {
            if (_popup.IsOpen) _popup.IsOpen = false;
        }

        private void Pick(int index)
        {
            if (index < 0 || index >= _choices.Count) return;
            var changed = index != _selected;
            _selected = index;
            ShowSelection();
            Close();
            Focus();
            if (changed) Changed?.Invoke();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Focus();
            // A click on the box while the list is open already closed it (light dismiss) before this runs: do not reopen.
            if (Environment.TickCount - _closedTick > 250)
            {
                if (_popup.IsOpen) Close(); else Open();
            }
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            switch (e.Key)
            {
                case Key.Enter:
                case Key.Space:
                    if (!_popup.IsOpen) Open();
                    else if (_hot >= 0) Pick(_hot);
                    else Close();
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.Up:
                    if (!_popup.IsOpen) Open();
                    else
                    {
                        _hot = AskCardModel.MoveChoice(_hot, _choices.Count, e.Key == Key.Down ? 1 : -1);
                        PaintRows();
                        if (_hot >= 0) _rows[_hot].BringIntoView();
                    }
                    e.Handled = true;
                    break;
                case Key.Escape:
                    if (_popup.IsOpen) { Close(); e.Handled = true; }
                    break;
            }
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            Close();
        }
    }

    /// <summary>A text or number field in the same look as <see cref="ChoiceField"/>.</summary>
    internal sealed class FormTextField : Border
    {
        public TextBox Box { get; }

        public string Text => Box.Text;

        /// <summary>Raised when the text changes.</summary>
        public event Action? Changed;

        public FormTextField(string? accessibleName = null)
        {
            MinHeight = 34;
            CornerRadius = new CornerRadius(8);
            BorderThickness = new Thickness(1);
            SnapsToDevicePixels = true;
            SetResourceReference(BackgroundProperty, "Cp.Run.Composer");
            SetResourceReference(BorderBrushProperty, "Cp.Run.LineStrong");

            Box = new TextBox
            {
                FontSize = 12.5,
                Padding = new Thickness(10, 8, 10, 8),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Box.SetResourceReference(Control.ForegroundProperty, "Cp.Ink");
            Box.SetResourceReference(TextBoxBase.CaretBrushProperty, "Cp.Ink");
            if (!string.IsNullOrEmpty(accessibleName)) AutomationProperties.SetName(Box, accessibleName);
            Box.TextChanged += (_, __) => Changed?.Invoke();
            Child = Box;

            IsKeyboardFocusWithinChanged += (_, __) =>
                SetResourceReference(BorderBrushProperty, IsKeyboardFocusWithin ? "Cp.Accent" : "Cp.Run.LineStrong");
        }
    }
}
