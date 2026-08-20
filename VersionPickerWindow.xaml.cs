using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// Lists a model's previously synced versions and downloads the one the user
    /// picks (ClickUp 86d3ut47q).
    ///
    /// The download runs HERE, inside the modal dialog, and the window closes only
    /// once the bytes are on disk. That is deliberate: swapping the document needs
    /// an ExternalEvent, and ExternalEvents are serviced from Revit's Idling loop,
    /// which does not run while a modal window is open. Downloading first and
    /// closing before the caller raises the event keeps the two from deadlocking.
    /// The download itself touches no Revit API, so it is safe to run off-thread
    /// while the dialog is up.
    /// </summary>
    public partial class VersionPickerWindow : Window
    {
        private readonly SyncApiClient _api;
        private readonly int _projectId;
        private readonly string _docGuid;
        private readonly string _cacheDirectory;

        private CancellationTokenSource _cancellation;
        private bool _downloading;

        /// <summary>Where the chosen version's bytes landed. Null unless DialogResult is true.</summary>
        public string DownloadedPath { get; private set; }

        /// <summary>Design id the downloaded bytes came from.</summary>
        public int SelectedDesignId { get; private set; }

        /// <summary>Version number the downloaded bytes came from.</summary>
        public int SelectedVersionNumber { get; private set; }

        public VersionPickerWindow(SyncApiClient api, int projectId, string docGuid, string cacheDirectory)
        {
            InitializeComponent();

            _api = api;
            _projectId = projectId;
            _docGuid = docGuid;
            _cacheDirectory = cacheDirectory;

            Loaded += async (_, __) => await LoadVersionsAsync();
        }

        /// <summary>
        /// A row as the list renders it. Everything the template binds is computed
        /// here rather than in XAML converters — the same approach
        /// DownloadResultsWindow takes.
        /// </summary>
        private sealed class VersionRow
        {
            public DesignVersion Source { get; set; }

            public string VersionLabel { get; set; }
            public string SyncedByLine { get; set; }
            public string CommentLine { get; set; }
            public string RestoredFromLine { get; set; }
            public string SizeText { get; set; }
            public string TranslationText { get; set; }

            public Visibility CurrentBadgeVisibility { get; set; }
            public Visibility CommentVisibility { get; set; }
            public Visibility RestoredFromVisibility { get; set; }
        }

        private async System.Threading.Tasks.Task LoadVersionsAsync()
        {
            try
            {
                var versions = await _api.GetVersionsAsync(_projectId, _docGuid);
                LoadingPanel.Visibility = Visibility.Collapsed;

                if (versions == null || versions.Count == 0)
                {
                    EmptyPanel.Visibility = Visibility.Visible;
                    CountText.Text = "";
                    return;
                }

                var rows = versions.Select(ToRow).ToList();
                LabelRestoredFrom(rows);
                VersionsListBox.ItemsSource = rows;

                CountText.Text = rows.Count == 1
                    ? "1 version"
                    : string.Format("{0} versions", rows.Count);

                // Pre-select the newest version that is NOT current: rolling back
                // to the version you already have is the one choice nobody wants.
                var firstRestorable = rows.FirstOrDefault(r => !r.Source.IsActive);
                if (firstRestorable != null)
                    VersionsListBox.SelectedItem = firstRestorable;
            }
            catch (Exception ex)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                ShowError("Could not load previous versions. " + ex.Message);
            }
        }

        private static VersionRow ToRow(DesignVersion v)
        {
            string who = string.IsNullOrWhiteSpace(v.UploaderName) ? "someone" : v.UploaderName;
            string when = v.UploadedAt.HasValue
                ? v.UploadedAt.Value.ToLocalTime().ToString("d MMM yyyy, HH:mm")
                : "date unknown";

            return new VersionRow
            {
                Source = v,
                VersionLabel = v.VersionNumber.HasValue ? "V" + v.VersionNumber.Value : "V?",
                SyncedByLine = string.Format("{0} · {1}", when, who),
                CommentLine = v.SyncComment,
                CommentVisibility = string.IsNullOrWhiteSpace(v.SyncComment)
                    ? Visibility.Collapsed
                    : Visibility.Visible,
                // Filled by LabelRestoredFrom once the whole list is known.
                RestoredFromLine = null,
                RestoredFromVisibility = v.RolledBackFromDesignId.HasValue
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                SizeText = FormatSize(v.FileSize),
                TranslationText = DescribeTranslation(v),
                CurrentBadgeVisibility = v.IsActive ? Visibility.Visible : Visibility.Collapsed
            };
        }

        /// <summary>
        /// Names the version a rollback started from, resolving the design id
        /// against the rest of the list — the API returns an id, and "V3" is what
        /// a human can act on.
        ///
        /// "based on", not "restored from": the user may have edited before
        /// syncing, so the bytes are not necessarily V3's. What is always true is
        /// that V3 is where this version started.
        /// </summary>
        private static void LabelRestoredFrom(List<VersionRow> rows)
        {
            foreach (var row in rows)
            {
                int? fromId = row.Source.RolledBackFromDesignId;
                if (!fromId.HasValue) continue;

                var source = rows.FirstOrDefault(r => r.Source.DesignId == fromId.Value);

                // The referenced version can be outside this list — a different
                // status, or soft-deleted. Say what is known rather than nothing.
                row.RestoredFromLine = source != null
                    ? "based on " + source.VersionLabel
                    : "based on an earlier version";
            }
        }

        private static string FormatSize(long? bytes)
        {
            if (!bytes.HasValue || bytes.Value <= 0) return "";
            double mb = bytes.Value / 1048576.0;
            return mb >= 1024
                ? string.Format("{0:F1} GB", mb / 1024.0)
                : string.Format("{0:F1} MB", mb);
        }

        /// <summary>
        /// Whether the version is viewable in the web app. Not a rollback
        /// blocker — the .rvt downloads regardless — but a user picking between
        /// two versions wants to know which one they can preview first.
        /// </summary>
        private static string DescribeTranslation(DesignVersion v)
        {
            if (!string.IsNullOrEmpty(v.UrnInBase64)) return "viewable";
            if (string.Equals(v.XktConversionStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                return "viewable";
            return "not translated";
        }

        private void VersionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_downloading) return;

            var row = VersionsListBox.SelectedItem as VersionRow;

            // Restoring the current version is a no-op that still costs a download
            // and a document swap, so it is refused rather than merely discouraged.
            RestoreButton.IsEnabled = row != null && !row.Source.IsActive;
        }

        private async void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            var row = VersionsListBox.SelectedItem as VersionRow;
            if (row == null || row.Source.IsActive) return;

            if (!Confirm(row)) return;

            await DownloadAsync(row);
        }

        /// <summary>
        /// Spells out what is about to happen in terms of versions, because
        /// "rollback" means destructive in most tools and does not here.
        /// </summary>
        private bool Confirm(VersionRow row)
        {
            string target = row.VersionLabel;
            var rows = VersionsListBox.ItemsSource as List<VersionRow>;
            var current = rows != null ? rows.FirstOrDefault(r => r.Source.IsActive) : null;
            string currentLabel = current != null ? current.VersionLabel : "the current version";

            string message =
                "Restore " + target + "?\n\n"
                + "Your open model will be replaced with the " + target + " file.\n"
                + "Nothing is deleted in BINA — " + currentLabel + " and everything before it stay exactly as they are.\n\n"
                + "Your next sync will publish this as a new version, marked as restored from " + target + ".";

            var answer = MessageBox.Show(this, message, "Roll back to " + target,
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);

            return answer == MessageBoxResult.OK;
        }

        private async System.Threading.Tasks.Task DownloadAsync(VersionRow row)
        {
            _downloading = true;

            // A retry after a cancelled download replaces this; dispose the old
            // one rather than leaking a handle per attempt.
            if (_cancellation != null) _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();

            RestoreButton.IsEnabled = false;
            VersionsListBox.IsEnabled = false;
            CancelButton.Content = "Cancel download";
            ErrorBox.Visibility = Visibility.Collapsed;
            ProgressBox.Visibility = Visibility.Visible;
            ProgressText.Text = "Preparing…";
            ProgressBar.Value = 0;

            string destination = BuildDestinationPath(row.Source);

            var progress = new Progress<(double Fraction, string Message)>(p =>
            {
                ProgressText.Text = p.Message;
                if (p.Fraction > 0) ProgressBar.Value = Math.Min(1.0, p.Fraction);
            });

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));

                await _api.DownloadAsync(row.Source.DesignId, destination, progress, _cancellation.Token);

                DownloadedPath = destination;
                SelectedDesignId = row.Source.DesignId;
                SelectedVersionNumber = row.Source.VersionNumber ?? 0;

                // Setting DialogResult closes a ShowDialog window, which is what
                // lets the caller raise its ExternalEvent — see the class remarks.
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                ResetAfterFailedDownload();
                ProgressBox.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                ResetAfterFailedDownload();
                ProgressBox.Visibility = Visibility.Collapsed;
                ShowError("Download failed, and your model is untouched. " + ex.Message);
            }
        }

        private void ResetAfterFailedDownload()
        {
            _downloading = false;
            VersionsListBox.IsEnabled = true;
            CancelButton.Content = "Cancel";

            var row = VersionsListBox.SelectedItem as VersionRow;
            RestoreButton.IsEnabled = row != null && !row.Source.IsActive;
        }

        /// <summary>
        /// Per-project, per-version path under the cache directory. Versions are
        /// kept apart so a failed swap never leaves the bytes of one version under
        /// the name of another.
        /// </summary>
        private string BuildDestinationPath(DesignVersion v)
        {
            string versionFolder = "v" + (v.VersionNumber.HasValue ? v.VersionNumber.Value.ToString() : "unknown")
                                 + "-" + v.DesignId;

            string fileName = SafeName(string.IsNullOrWhiteSpace(v.Name) ? "model.rvt" : v.Name);
            if (!fileName.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                fileName += ".rvt";

            return Path.Combine(_cacheDirectory, "project-" + _projectId, versionFolder, fileName);
        }

        private static string SafeName(string raw)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                raw = raw.Replace(c, '_');
            return raw;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_downloading && _cancellation != null)
            {
                // First Cancel stops the transfer; the dialog stays open so the
                // user can pick a different version rather than starting over.
                _cancellation.Cancel();
                return;
            }

            DialogResult = false;
        }

        /// <summary>
        /// Closing the window mid-download (title-bar X, Esc) must stop the
        /// transfer: the caller disposes the API client as soon as ShowDialog
        /// returns, so a download left running writes on into a disposed client.
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            if (_cancellation != null)
            {
                try { _cancellation.Cancel(); } catch { }
                _cancellation.Dispose();
                _cancellation = null;
            }

            base.OnClosed(e);
        }

        private void ShowError(string message)
        {
            ErrorMessage.Text = message;
            ErrorBox.Visibility = Visibility.Visible;
        }
    }
}
