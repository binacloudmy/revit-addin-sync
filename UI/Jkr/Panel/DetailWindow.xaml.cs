using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using RevitWebAppSync.UI.Jkr.Modals;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>
    /// The issue detail over Revit's window (prototype detail dialog). Shows the
    /// current card of <see cref="DetailScreenModel"/>; ‹ ›, the dots, ← → / J K
    /// and sideways scrolling (tilt wheel, touchpad, Shift + wheel) move between
    /// issues. Esc, × or a click on the scrim close it. Modeless, so the confirm
    /// dialog and Revit's own work keep running while it is open.
    /// </summary>
    public partial class DetailWindow : Window
    {
        private const int WmMouseHWheel = 0x020E;
        /// <summary>One sideways scroll moves one issue at most this often.</summary>
        private static readonly TimeSpan SwipeGap = TimeSpan.FromMilliseconds(350);

        public static readonly IValueConverter StepNumber = new StepNumberConverter();

        private readonly DetailScreenModel _model;
        private string _shownId;
        private int _shownIndex;
        private double _swipe;
        private DateTime _lastSwipe = DateTime.MinValue;
        private bool _closing;

        /// <summary>The toast's Undo was clicked.</summary>
        public event Action UndoRequested;

        private DetailWindow(DetailScreenModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            InitializeComponent();
            DataContext = model;
            _shownId = model.Card?.Id;
            _shownIndex = model.Index;
            model.PropertyChanged += OnModelChanged;
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewMouseWheel += OnPreviewMouseWheel;
            SourceInitialized += (_, __) => (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WndProc);
            Loaded += (_, __) => { Activate(); CloseBtn.Focus(); };
            Closed += (_, __) =>
            {
                model.PropertyChanged -= OnModelChanged;
                _closing = true;
                if (model.IsOpen) model.Close();
            };
        }

        /// <summary>Open over Revit's window. <paramref name="anchor"/> is an element
        /// in the pane (for the DPI and the fallback owner).</summary>
        public static DetailWindow Open(DetailScreenModel model, FrameworkElement anchor)
        {
            var w = new DetailWindow(model);
            if (RevitWindowCover.Cover(w, anchor))
            {
                var room = Math.Max(240, w.Height - w.Card.Margin.Top - w.Card.Margin.Bottom);
                w.Card.MaxHeight = room;
                // A steady height while moving between short and long issues.
                w.Card.MinHeight = Math.Min(room, 560);
            }
            else
            {
                Window owner = null;
                try { owner = anchor != null ? Window.GetWindow(anchor) : null; } catch { }
                if (owner != null) w.Owner = owner;
                w.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
                w.Height = Math.Min(w.Height, SystemParameters.WorkArea.Height - 40);
                w.Card.MaxHeight = w.Height - 48;
            }
            w.Show();
            return w;
        }

        private void OnModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_closing) return;
            if (!_model.IsOpen) { _closing = true; Close(); return; }
            var id = _model.Card?.Id;
            if (id == _shownId) return;
            var forward = _model.Index >= _shownIndex;
            _shownId = id;
            _shownIndex = _model.Index;
            Body.ScrollToTop();
            SlideIn(forward);
        }

        /// <summary>The next card slides in from the side it came from (prototype scroll-snap).</summary>
        private void SlideIn(bool forward)
        {
            try
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var time = TimeSpan.FromMilliseconds(180);
                Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                    new DoubleAnimation(forward ? 40 : -40, 0, time) { EasingFunction = ease });
                CardBody.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, time) { EasingFunction = ease });
            }
            catch { /* cosmetic */ }
        }

        /// <summary>Show a toast over the scrim (the pane's toast is behind it); null hides it.</summary>
        public void ShowToast(string message, bool canUndo, string undoLabel)
        {
            if (string.IsNullOrEmpty(message)) { ToastBox.Visibility = Visibility.Collapsed; return; }
            ToastText.Text = message;
            ToastUndo.Tag = undoLabel;
            ToastUndo.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
            ToastBox.Visibility = Visibility.Visible;
        }

        // ── clicks ──
        private void Scrim_MouseDown(object sender, MouseButtonEventArgs e) => Close();
        private void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Prev_Click(object sender, RoutedEventArgs e) => _model.Prev();
        private void Next_Click(object sender, RoutedEventArgs e) => _model.Next();
        private void Fix_Click(object sender, RoutedEventArgs e) => _model.Fix();
        private void Ignore_Click(object sender, RoutedEventArgs e) => _model.Ignore();
        private void Review_Click(object sender, RoutedEventArgs e) => _model.ToggleReviewed();
        private void Locate_Click(object sender, RoutedEventArgs e) => _model.Locate();
        private void Source_Click(object sender, MouseButtonEventArgs e) => _model.OpenSource();
        private void ToastUndo_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();

        private void Dot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is int j) _model.GoTo(j);
        }

        // ── keyboard (prototype handleKey while the detail is open) ──
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
            PanelKey key;
            switch (e.Key)
            {
                case Key.Escape: key = PanelKey.Escape; break;
                case Key.Left: key = PanelKey.Left; break;
                case Key.Right: key = PanelKey.Right; break;
                case Key.J: key = PanelKey.J; break;
                case Key.K: key = PanelKey.K; break;
                case Key.F: key = PanelKey.F; break;
                case Key.A: key = PanelKey.A; break;
                default: return;
            }
            if (_model.HandleKey(key) == KeyOutcome.Handled) e.Handled = true;
        }

        // ── sideways scrolling ──
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;   // plain wheel scrolls the card
            Swipe(-e.Delta);
            e.Handled = true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmMouseHWheel)
            {
                var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                Swipe(delta);
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>Positive = towards the next issue. Small touchpad steps add up to one notch.</summary>
        private void Swipe(int delta)
        {
            var now = DateTime.UtcNow;
            if (now - _lastSwipe < SwipeGap) { _swipe = 0; return; }
            _swipe += delta;
            if (Math.Abs(_swipe) < Mouse.MouseWheelDeltaForOneLine) return;
            if (_swipe > 0) _model.Next(); else _model.Prev();
            _swipe = 0;
            _lastSwipe = now;
        }

        private sealed class StepNumberConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
                value is int i ? (i + 1) + "." : "";
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
                throw new NotSupportedException();
        }
    }
}
