using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI.Jkr.Modals
{
    /// <summary>
    /// Confirm dialog before every model write (prototype .cf, frames f-confirm and
    /// f-paid). Lists each change as "item · title / actual → proposed". The paid
    /// Generate action also needs the consent box, which starts unticked. From 500
    /// affected elements it says Revit may pause.
    ///
    /// Esc, Cancel and a click on the scrim cancel; Enter confirms when the confirm
    /// button is enabled. The scrim covers the window that owns the pane.
    /// </summary>
    public partial class JkrConfirmWindow : Window
    {
        private readonly JkrConfirmModel _model;

        private JkrConfirmWindow(JkrConfirmModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            InitializeComponent();

            TitleText.Text = model.Title;
            SubText.Text = model.Sub;
            RowsList.ItemsSource = model.Rows;
            PauseBox.Visibility = model.ShowPauseWarning ? Visibility.Visible : Visibility.Collapsed;
            PauseText.Text = model.PauseWarning;
            ConsentBox.Visibility = model.NeedsConsent ? Visibility.Visible : Visibility.Collapsed;
            ConsentText.Text = model.ConsentText;
            ConsentCheck.IsChecked = model.Consent;
            CancelBtn.Content = model.CancelLabel;
            ConfirmBtn.Content = model.ConfirmLabel;
            AutomationPropertiesHelper(model.Title);
            Refresh();

            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (_, __) =>
            {
                // Paid action: focus the consent box first, so Enter can't skip it.
                if (model.NeedsConsent) ConsentCheck.Focus();
                else ConfirmBtn.Focus();
            };
        }

        /// <summary>Show the dialog modally over Revit's main window. Returns true
        /// only when the user confirmed; the caller writes only then.</summary>
        public static bool Ask(JkrConfirmModel model, FrameworkElement anchor)
        {
            var w = new JkrConfirmWindow(model);
            if (!w.CoverRevitWindow(anchor))
            {
                // Fallback: a card-sized window (SizeToContent) centred on the pane's
                // window; the scrim margin leaves room for the shadow.
                Window owner = null;
                try { owner = anchor != null ? Window.GetWindow(anchor) : null; } catch { }
                if (owner != null && owner != w) w.Owner = owner;
                w.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
                w.Card.MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 120);
            }
            w.ShowDialog();
            return model.Confirmed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        /// <summary>Own the dialog by Revit's main window and cover it with the scrim
        /// (prototype: the scrim covers the whole app). Revit's main window is not
        /// reliably a WPF Window (Window.GetWindow on the docked pane gave nothing,
        /// so the card was clipped in a 520×480 window), so it is found by handle.
        /// The card is capped to the window height; its change list scrolls.</summary>
        private bool CoverRevitWindow(FrameworkElement anchor)
        {
            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
                if (r.Right - r.Left < 300 || r.Bottom - r.Top < 300) return false;

                // Pixels → WPF units with the pane's DPI (the pane sits in Revit's window).
                var source = anchor != null ? PresentationSource.FromVisual(anchor) : null;
                var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var topLeft = toDip.Transform(new Point(r.Left, r.Top));
                var bottomRight = toDip.Transform(new Point(r.Right, r.Bottom));

                new WindowInteropHelper(this).Owner = hwnd;
                SizeToContent = SizeToContent.Manual;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = topLeft.X;
                Top = topLeft.Y;
                Width = bottomRight.X - topLeft.X;
                Height = bottomRight.Y - topLeft.Y;
                Card.MaxHeight = Math.Max(240, Height - Card.Margin.Top - Card.Margin.Bottom);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void AutomationPropertiesHelper(string name)
        {
            System.Windows.Automation.AutomationProperties.SetName(Card, name);
            Title = name;
        }

        private void Refresh() => ConfirmBtn.IsEnabled = _model.CanConfirm;

        private void Consent_Changed(object sender, RoutedEventArgs e)
        {
            _model.Consent = ConsentCheck.IsChecked == true;
            Refresh();
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_model.TryConfirm()) Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void Scrim_MouseDown(object sender, MouseButtonEventArgs e) => Close();

        // Clicks inside the card must not reach the scrim.
        private void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            if (e.Key == Key.Enter)
            {
                // Enter on Cancel cancels; otherwise Enter confirms when allowed.
                if (Keyboard.FocusedElement == CancelBtn) { Close(); e.Handled = true; return; }
                if (_model.TryConfirm()) Close();
                e.Handled = true;
            }
        }
    }
}
