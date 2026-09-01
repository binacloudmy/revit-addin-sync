using System;
using System.Windows;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Blocking update gate. Shown by UpdateService when the feed marks a
    /// newer build mandatory, or when the running build is below the feed's
    /// minAddinVersion floor — commands stay disabled (EnsureUpToDate) until
    /// the user downloads it here and restarts Revit. Closing the window
    /// without updating just leaves the gate in place; the next ribbon click
    /// re-opens it, and a floor-blocked Copilot pane stays walled.
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

            // A floor is a hard stop; a merely-mandatory update is not phrased as
            // one, so the drafter can tell the two apart.
            var gate = UpdateService.Gate;
            if (gate.Blocked)
            {
                HeadingText.Text = "Update required";
                BlurbText.Text =
                    $"BINA Sync {gate.Current} is no longer supported. Version {gate.Required} or newer " +
                    "must be installed before the plugin can be used.";
            }
            else
            {
                HeadingText.Text = "Update available";
                BlurbText.Text =
                    "A new version of BINA Sync must be installed before the plugin can be used.";
            }

            if (UpdateService.IsStaged)
                ShowRestartState();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            // Closing with the title-bar X is not a way out — nudge every gate
            // surface (the Copilot wall) to re-read the state we may have just
            // moved to "staged".
            UpdateService.NotifyGateChanged();
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
