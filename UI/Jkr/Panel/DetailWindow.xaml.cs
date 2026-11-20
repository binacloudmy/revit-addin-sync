using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using RevitWebAppSync.UI.Jkr.Modals;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>
    /// The issue detail over Revit's window (prototype detail dialog). Shows the
    /// current card of <see cref="DetailScreenModel"/>; ‹ ›, ← → / J K
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
        /// <summary>Why it closes, for the pane log (scrim click, ×, model closed, Esc…).</summary>
        private string _closeReason;
        /// <summary>The scrim closes on a press AND release on it, not on a press alone: a press
        /// that began before the window existed (the row click that opened it) never closes it.</summary>
        private bool _scrimPressed;
        /// <summary>Environment.TickCount at the window's last activation.</summary>
        private int _activatedAt = Environment.TickCount;
        /// <summary>A scrim press closer than this to the window's activation is not trusted:
        /// in Revit a press nobody made reached the scrim about 0.8 s after Show (live R1).</summary>
        internal const int ScrimQuietMs = 400;
        private const int VkLButton = 0x01;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

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
            Loaded += (_, __) => { JkrPaneLog.Write("DetailWindow loaded"); Activate(); CloseBtn.Focus(); };
            Activated += (_, __) => { _activatedAt = Environment.TickCount; JkrPaneLog.Write("DetailWindow activated"); };
            Deactivated += (_, __) => JkrPaneLog.Write("DetailWindow deactivated");
            Closing += (_, e) => JkrPaneLog.Write("DetailWindow closing: " + (_closeReason ?? "not by the pane (owner closed, system, or Close() elsewhere)")
                                                   + (_closeReason == null ? " <- " + JkrPaneLog.Callers(1, 8) : "") + (e.Cancel ? " (cancelled)" : ""));
            Closed += (_, __) =>
            {
                JkrPaneLog.Write("DetailWindow closed (model open=" + model.IsOpen + ")");
                model.PropertyChanged -= OnModelChanged;
                _closing = true;
                if (model.IsOpen) model.Close();
            };
        }

        /// <summary>Open over the screen Revit is on, card centred. <paramref name="anchor"/>
        /// is an element in the pane (finds Revit's window; fallback owner).</summary>
        public static DetailWindow Open(DetailScreenModel model, FrameworkElement anchor)
        {
            var w = new DetailWindow(model);
            try
            {
                return Place(w, anchor);
            }
            catch (Exception ex)
            {
                JkrPaneLog.Write("DetailWindow.Open failed in Place: " + ex);
                w._closeReason = "Place failed";
                model.PropertyChanged -= w.OnModelChanged;
                try { w.Close(); } catch { }
                throw;
            }
        }

        private static DetailWindow Place(DetailWindow w, FrameworkElement anchor)
        {
            var covered = RevitWindowCover.Cover(w, anchor, h =>
            {
                var room = Math.Max(240, h - w.Card.Margin.Top - w.Card.Margin.Bottom);
                w.Card.MaxHeight = room;
                // A steady height while moving between short and long issues.
                w.Card.MinHeight = Math.Min(room, 560);
            });
            JkrPaneLog.Write("DetailWindow.Place: covered Revit's screen=" + covered);
            if (!covered)
            {
                Window owner = null;
                try { owner = anchor != null ? Window.GetWindow(anchor) : null; } catch { }
                if (owner != null) w.Owner = owner;
                w.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
                w.Height = Math.Min(w.Height, SystemParameters.WorkArea.Height - 40);
                w.Card.MaxHeight = w.Height - 48;
            }
            w.Show();
            JkrPaneLog.Write($"DetailWindow.Place: Show called, IsVisible={w.IsVisible}");
            return w;
        }

        private void OnModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_closing) return;
            if (!_model.IsOpen)
            {
                _closing = true;
                _closeReason = "the model closed (DetailId gone) on " + (e.PropertyName ?? "all properties");
                Close();
                return;
            }
            var id = _model.Card?.Id;
            if (id == _shownId) return;
            // No slide / fade: in this layered window owned by Revit, an animation's
            // last frame was sometimes never painted, leaving the card shifted and faded.
            _shownId = id;
            _shownIndex = _model.Index;
            Body.ScrollToTop();
        }

        /// <summary>Show a toast over the scrim (the pane's toast is behind it); null hides it.</summary>
        public void ShowToast(string message)
        {
            if (string.IsNullOrEmpty(message)) { ToastBox.Visibility = Visibility.Collapsed; return; }
            ToastText.Text = message;
            ToastBox.Visibility = Visibility.Visible;
        }

        // ── clicks ──
        /// <summary>
        /// The scrim closes the detail only on a click it saw start for real (controller ruling,
        /// live R1): (a) a left press at least <see cref="ScrimQuietMs"/> after the window's last
        /// activation (the press's own timestamp), (b) with the left button physically down when
        /// the press is handled, and (c) the release on the scrim too (<see cref="Scrim_MouseUp"/>).
        /// Esc and × close as before.
        /// </summary>
        private void Scrim_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _scrimPressed = false;
            if (e.ChangedButton != MouseButton.Left) { JkrPaneLog.Write("DetailWindow scrim press ignored: " + e.ChangedButton + " button"); return; }
            var sinceActivated = unchecked(e.Timestamp - _activatedAt);
            if (sinceActivated < ScrimQuietMs)
            {
                JkrPaneLog.Write($"DetailWindow scrim press ignored: {sinceActivated} ms after the window was activated (< {ScrimQuietMs})");
                return;
            }
            var down = (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
            if (!down)
            {
                JkrPaneLog.Write("DetailWindow scrim press ignored: the left button is not physically down");
                return;
            }
            _scrimPressed = true;
            JkrPaneLog.Write($"DetailWindow scrim pressed ({sinceActivated} ms after activation, button down)");
        }
        private void Scrim_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_scrimPressed) { JkrPaneLog.Write("DetailWindow scrim released without a press on it: ignored"); return; }
            _scrimPressed = false;
            _closeReason = "scrim click";
            Close();
        }
        private void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void Close_Click(object sender, RoutedEventArgs e) { _closeReason = "× button"; Close(); }
        private void Prev_Click(object sender, RoutedEventArgs e) => _model.Prev();
        private void Next_Click(object sender, RoutedEventArgs e) => _model.Next();
        private void Fix_Click(object sender, RoutedEventArgs e) => _model.Fix();
        private void Fixer_Ask() => _model.AskAi();
        private void Fixer_Accept() => _model.Fix();
        private void Fixer_Write() => _model.WriteManual();
        private void Ignore_Click(object sender, RoutedEventArgs e) => _model.Ignore();
        private void Review_Click(object sender, RoutedEventArgs e) => _model.ToggleReviewed();
        private void MarkManual_Click(object sender, RoutedEventArgs e) => _model.MarkManual();
        private void Reopen_Click(object sender, RoutedEventArgs e) => _model.Reopen();
        private void Locate_Click(object sender, RoutedEventArgs e) => _model.Locate();
        private void Source_Click(object sender, MouseButtonEventArgs e) => _model.OpenSource();

        /// <summary>A letter shortcut caught by the pane's key hook (J / K / F / A).</summary>
        internal bool HandleHookKey(PanelKey key) => _model.HandleKey(key) == KeyOutcome.Handled;

        // ── keyboard (prototype handleKey while the detail is open) ──
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
            // Typing a value in the fixer row (AI-435): J / K / F / A / arrows are text there; only Esc still closes.
            if (e.Key != Key.Escape && (e.OriginalSource is TextBox || e.OriginalSource is ComboBox || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)) return;
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
