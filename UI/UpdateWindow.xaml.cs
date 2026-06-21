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

        private void ShowRestartState()
        {
            Progress.Visibility = Visibility.Collapsed;
            StatusText.Text = "Update installed. Restart Revit to finish.";
            UpdateButton.Content = "Close — restart Revit to apply";
            UpdateButton.IsEnabled = true;
        }
    }
}
