using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Modals
{
    /// <summary>
    /// Fix outcome (Clear Sheet "fix outcome"): what was written as one undo step
    /// and every fix that was not, grouped by reason. Replaces the plain Revit
    /// message box. Centred over Revit like the other pop-ups.
    ///
    /// Esc, Done and a click on the scrim close it. Locate closes it and returns
    /// the element, so the pane can show it in Revit.
    /// </summary>
    public partial class FixResultWindow : Window
    {
        private readonly FixResultModel _model;
        private long _locate;
        private DispatcherTimer _copiedTimer;

        private FixResultWindow(FixResultModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            InitializeComponent();
            DataContext = model;
            Title = PaneText.DialogTitle;
            System.Windows.Automation.AutomationProperties.SetName(Card, model.Title);
            CopyText.Text = model.CopyLabel;

            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (_, __) => CloseBtn.Focus();
            Closed += (_, __) => _copiedTimer?.Stop();
        }

        /// <summary>Show the result modally over Revit. Returns the element to locate
        /// when the user pressed Locate on a row, else 0.</summary>
        public static long Show(FixResultModel model, FrameworkElement anchor)
        {
            var w = new FixResultWindow(model);
            var covered = RevitWindowCover.Cover(w, anchor, h =>
            {
                var room = h - w.Card.Margin.Top - w.Card.Margin.Bottom;
                w.Card.MaxHeight = Math.Max(300, room);
                // Headline, undo line, stay-open line and footer take about 220; the groups scroll in the rest.
                w.GroupsScroll.MaxHeight = Math.Max(160, room - 220);
            });
            if (!covered)
            {
                Window owner = null;
                try { owner = anchor != null ? Window.GetWindow(anchor) : null; } catch { }
                if (owner != null && owner != w) w.Owner = owner;
                w.WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
                var room = Math.Max(300, SystemParameters.WorkArea.Height - 120);
                w.Card.MaxHeight = room;
                w.GroupsScroll.MaxHeight = Math.Max(160, room - 220);
            }
            w.ShowDialog();
            return w._locate;
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is FixResultGroup g) g.ShowingAll = true;
        }

        private void Locate_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is FixResultItem item && item.CanLocate)
            {
                _locate = item.ElementId;
                Close();
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(_model.CopyText()); }
            catch (Exception ex)
            {
                // The clipboard can be held by another app for a moment.
                System.Diagnostics.Debug.WriteLine($"[BINA FixResult] copy failed: {ex.Message}");
                return;
            }
            CopyText.Text = _model.CopiedLabel;
            _copiedTimer?.Stop();
            _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _copiedTimer.Tick += (_, __) => { _copiedTimer.Stop(); CopyText.Text = _model.CopyLabel; };
            _copiedTimer.Start();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Scrim_MouseDown(object sender, MouseButtonEventArgs e) => Close();

        // Clicks inside the card must not reach the scrim.
        private void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        }
    }
}
