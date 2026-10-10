using System;
using System.Windows;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// About: the running BINA Sync version, and one button that checks the
    /// feed and installs what it finds. The download itself is the same
    /// UpdateService.StageAsync the forced-update window uses, so a build
    /// found here applies the same way — at the next Revit start.
    /// </summary>
    public partial class AboutWindow : Window
    {
        private enum ButtonAction { Check, Update, Close, CloseRevit }

        private ButtonAction _action = ButtonAction.Check;
        private bool _busy;

        /// <summary>The drafter clicked "Close Revit now" once the update was
        /// staged. The caller posts Revit's Exit after this window has closed.</summary>
        public bool CloseRevitRequested { get; private set; }

        public AboutWindow()
        {
            InitializeComponent();

            VersionText.Text = "Version " + Pretty(UpdateService.CurrentVersion);
            DetailsText.Text = Details();
            ShowState(justChecked: false);
        }

        /// <summary>"0.0.82", not "0.0.82.0": the folder a build runs from names
        /// three parts, a dev build's assembly version four.</summary>
        private static string Pretty(Version v) =>
            v.Build < 0 || v.Revision > 0 ? v.ToString() : v.ToString(3);

        private static string Details()
        {
            var host = string.IsNullOrWhiteSpace(UpdateService.HostVersion) ? "" : "Revit " + UpdateService.HostVersion;
            // Drafters run Release; only a tester needs to see which channel this is.
            var channel = BinaConfig.Channel.StartsWith("Release", StringComparison.Ordinal)
                ? ""
                : BinaConfig.Channel.Split(' ')[0] + " build";
            return host.Length > 0 && channel.Length > 0 ? host + " · " + channel : host + channel;
        }

        /// <summary>Show what UpdateService knows right now.</summary>
        private void ShowState(bool justChecked)
        {
            Progress.Visibility = Visibility.Collapsed;
            var pending = UpdateService.Pending;

            if (!UpdateService.IsEnabled)
            {
                StatusText.Text = "Updates are not set up for this build.";
                Set(ButtonAction.Close, "Close");
            }
            else if (pending == null)
            {
                NotesText.Text = "";
                StatusText.Text = justChecked ? "You are on the latest version." : "";
                Set(ButtonAction.Check, "Check for updates");
            }
            else if (UpdateService.IsStaged)
            {
                NotesText.Text = pending.Notes ?? "";
                // Revit's own exit asks to save every open model first.
                if (UpdateService.CanCloseRevit)
                {
                    StatusText.Text = $"Version {pending.Version} is installed. Close Revit and open it again to finish the update.";
                    Set(ButtonAction.CloseRevit, "Close Revit now");
                }
                else
                {
                    StatusText.Text = $"Version {pending.Version} is installed. Restart Revit to finish the update.";
                    Set(ButtonAction.Close, "Close — restart Revit to apply");
                }
            }
            else
            {
                NotesText.Text = pending.Notes ?? "";
                StatusText.Text = $"Version {pending.Version} is available.";
                Set(ButtonAction.Update, "Update now");
            }
        }

        private void Set(ButtonAction action, string label)
        {
            _action = action;
            ActionButton.Content = label;
            ActionButton.IsEnabled = true;
        }

        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_action == ButtonAction.Close || _action == ButtonAction.CloseRevit)
            {
                CloseRevitRequested = _action == ButtonAction.CloseRevit;
                Close();
                return;
            }

            if (_busy)
                return;
            _busy = true;

            ActionButton.IsEnabled = false;
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;

            try
            {
                if (_action == ButtonAction.Check)
                {
                    StatusText.Text = "Checking for updates…";
                    if (await UpdateService.CheckNowAsync())
                        ShowState(justChecked: true);
                    else
                    {
                        Progress.Visibility = Visibility.Collapsed;
                        StatusText.Text = "Could not reach the update server. Check your connection and try again.";
                        Set(ButtonAction.Check, "Try again");
                    }
                }
                else
                {
                    StatusText.Text = "Starting download…";
                    var progress = new Progress<(double Fraction, string Status)>(p =>
                    {
                        Progress.IsIndeterminate = false;
                        Progress.Value = p.Fraction;
                        StatusText.Text = p.Status;
                    });
                    await UpdateService.StageAsync(progress);
                    ShowState(justChecked: false);
                }
            }
            catch (Exception ex)
            {
                Progress.Visibility = Visibility.Collapsed;
                StatusText.Text = $"Update failed: {ex.Message}";
                Set(_action, "Try again");
            }
            finally
            {
                _busy = false;
            }
        }
    }
}
