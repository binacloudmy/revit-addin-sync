using System;
using System.Windows;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Blocking update gate. Shown by UpdateService when the feed marks a
    /// newer build mandatory — commands stay disabled (EnsureUpToDate) until
    /// the user downloads it here and restarts Revit. Closing the window
    /// without updating just leaves the gate in place; the next ribbon click
    /// re-opens it.
    /// </summary>
    public partial class UpdateWindow : Window
    {
        private bool _busy;

        /// <summary>The drafter clicked "Close Revit now" once the update was
        /// staged. The caller posts Revit's Exit after this window has closed.</summary>
        public bool CloseRevitRequested { get; private set; }

        public UpdateWindow()
        {
            InitializeComponent();

            var pending = UpdateService.Pending;
            VersionText.Text =
                $"BINA Sync {UpdateService.CurrentVersion} → {pending?.Version}";
            NotesText.Text = pending?.Notes ?? "";

            if (UpdateService.IsStaged)
                ShowRestartState();
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (UpdateService.IsStaged)
            {
                CloseRevitRequested = UpdateService.CanCloseRevit;
                Close();
                return;
            }

            if (_busy)
                return;
            _busy = true;

            UpdateButton.IsEnabled = false;
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            StatusText.Text = "Starting download…";

            var progress = new Progress<(double Fraction, string Status)>(p =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = p.Fraction;
                StatusText.Text = p.Status;
            });

            try
            {
                await UpdateService.StageAsync(progress);
                ShowRestartState();
            }
            catch (Exception ex)
            {
                Progress.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Update failed: {ex.Message}";
                UpdateButton.Content = "Try again";
                UpdateButton.IsEnabled = true;
            }
            finally
            {
                _busy = false;
            }
        }

        private async void DiagnosticsLink_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsLink.IsEnabled = false;
            try { MessageBox.Show(this, await DiagnosticsAction.SendAsync(), "BINA Sync"); }
            finally { DiagnosticsLink.IsEnabled = true; }
        }

        private void ShowRestartState()
        {
            Progress.Visibility = Visibility.Collapsed;
            // Revit's own exit asks to save every open model first.
            var canClose = UpdateService.CanCloseRevit;
            StatusText.Text = canClose
                ? "Update installed. Close Revit and open it again to finish."
                : "Update installed. Restart Revit to finish.";
            UpdateButton.Content = canClose ? "Close Revit now" : "Close — restart Revit to apply";
            UpdateButton.IsEnabled = true;
        }
    }
}
