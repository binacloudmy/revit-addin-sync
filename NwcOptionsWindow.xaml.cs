using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// The standalone "Export NWC" dialog (ClickUp 86d49v9ak).
    ///
    /// Local export only: this is the drafter's own copy on disk, so nothing here talks
    /// to BINA and no Cloud Docs sign-in is needed. The version-attached export lives in
    /// the sync dialog, which is the only place with a version to attach to.
    ///
    /// The window follows SyncOptionsWindow's shape on purpose — form, then progress,
    /// then outcome in one modal — so the two flows read the same way.
    /// </summary>
    public partial class NwcOptionsWindow : Window
    {
        private readonly int _revitYear;
        private bool _busy;

        public NwcOptionsWindow(string sourcePath, int revitYear)
        {
            InitializeComponent();

            _revitYear = revitYear;

            string fileName = string.IsNullOrEmpty(sourcePath) ? "this model" : Path.GetFileName(sourcePath);
            SourceText.Text = $"Exports {fileName} to Navisworks (.nwc), using the temporary clean 3D view " +
                              "the checklist describes. Your own views are not touched.";

            CoordinatesCombo.ItemsSource = new List<CoordinateChoice>
            {
                new CoordinateChoice { Value = NwcCoordinates.Shared, Label = "Shared (default)" },
                new CoordinateChoice { Value = NwcCoordinates.Internal, Label = "Project internal" }
            };
            CoordinatesCombo.SelectedIndex = 0;

            try
            {
                FolderBox.Text = string.IsNullOrEmpty(sourcePath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    : Path.GetDirectoryName(sourcePath);
            }
            catch
            {
                FolderBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            FolderNote.Text = "The NWC is named after the model, without a version suffix.";

            if (!NwcExportSettings.IsPurgeAvailable(revitYear))
            {
                // The export still works here; only the optional purge step is missing.
                PurgeCheck.IsEnabled = false;
                PurgeNote.Visibility = Visibility.Visible;
                PurgeNote.Text = "Purge needs Revit 2024 or newer — not available in this version.";
            }
        }

        /// <summary>
        /// The export to run when the user clicks Export.
        ///
        /// A delegate rather than a call so this window stays free of Revit types: the
        /// command owns the document and runs the export on the UI thread, inside the
        /// dialog's pump — the same arrangement SyncOptionsWindow uses for its sync.
        /// </summary>
        public Func<NwcExportResult> ExportWork { get; set; }

        /// <summary>The choices as an export settings object. Read on the UI thread.</summary>
        public NwcExportSettings Settings
        {
            get
            {
                var choice = CoordinatesCombo.SelectedItem as CoordinateChoice;
                return new NwcExportSettings
                {
                    Coordinates = choice?.Value ?? NwcCoordinates.Shared,
                    PurgeUnused = PurgeCheck.IsEnabled && PurgeCheck.IsChecked == true
                };
            }
        }

        public string OutputFolder => (FolderBox.Text ?? string.Empty).Trim();

        /// <summary>True once an export finished successfully — what the command reports back to Revit.</summary>
        public bool ExportSucceeded { get; private set; }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string current = OutputFolder;

#if NET8_0_OR_GREATER
                // WPF's own folder picker (net8+). Revit 2023/2024 load the net48 build,
                // which predates it and needs the shell trick below.
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Where should the NWC be written?"
                };
                if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) dialog.InitialDirectory = current;
                if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
#else
                // net48 has no folder picker in WPF, and pulling in WinForms for one
                // dialog is not worth the reference: the file dialog doubles as a folder
                // chooser when it is told not to check the name, which is what the
                // shell's own "select folder" affordance does underneath.
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Where should the NWC be written?",
                    CheckFileExists = false,
                    CheckPathExists = true,
                    ValidateNames = false,
                    FileName = "Select this folder"
                };
                if (!string.IsNullOrEmpty(current) && Directory.Exists(current)) dialog.InitialDirectory = current;
                if (dialog.ShowDialog(this) == true)
                {
                    string picked = Path.GetDirectoryName(dialog.FileName);
                    if (!string.IsNullOrEmpty(picked)) FolderBox.Text = picked;
                }
#endif
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not open the folder picker: {ex.Message}";
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            if (string.IsNullOrEmpty(OutputFolder))
            {
                StatusText.Text = "Choose a folder to write the NWC to.";
                return;
            }

            if (ExportWork == null)
            {
                // No export wired: nothing sensible to do, and silently closing would
                // look like the export succeeded.
                StatusText.Text = "Nothing is wired to export this model.";
                return;
            }

            ShowExportingState();

            // The export is a Revit API call, so it HAS to run on this thread and the
            // window cannot repaint while it does. Yielding to the dispatcher once lets
            // the progress state paint before the freeze, so a multi-minute export on a
            // big model looks like work rather than a hung dialog.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            NwcExportResult result = null;
            Exception failure = null;

            try
            {
                result = ExportWork();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            _busy = false;
            ShowOutcomeState(result, failure);
        }

        private void ShowExportingState()
        {
            _busy = true;
            FormRoot.Visibility = Visibility.Collapsed;
            OutcomeRoot.Visibility = Visibility.Visible;

            SetBadge("…", "#666666", "#F5F5F5");
            OutcomeTitle.Text = "Exporting";
            OutcomeProgress.Visibility = Visibility.Visible;
            OpenFolderButton.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Collapsed;

            OutcomeMessage.Text = "Writing the NWC…";
            OutcomeDetail.Text = "Keep Revit open — a large model can take a few minutes.";
        }

        private void ShowOutcomeState(NwcExportResult result, Exception failure)
        {
            OutcomeProgress.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Visible;

            if (failure != null)
            {
                SetBadge("✕", "#B91C1C", "#FEF2F2");
                OutcomeTitle.Text = "Export failed";

                // A missing exporter is the one failure with a fix the user can act on
                // immediately, so it gets the link rather than a bare message.
                var typed = failure as NwcExportException;
                OutcomeMessage.Text = typed != null && typed.Kind == NwcExportFailure.ExporterMissing
                    ? NwcExporter.MissingExporterMessage(_revitYear)
                    : failure.Message;
                OutcomeDetail.Text = "Nothing was written.";
                BackButton.Visibility = Visibility.Visible;
                BackButton.Focus();
                return;
            }

            SetBadge("✓", "#16A34A", "#ECFDF5");
            OutcomeTitle.Text = "Exported";
            ExportSucceeded = result != null;
            OutcomeMessage.Text = $"{(result?.FileName ?? "The NWC")} was written to {Path.GetDirectoryName(result?.FilePath ?? "")}.";
            OutcomeDetail.Text = string.IsNullOrEmpty(result?.Action) ? "" : result.Action;

            // The standalone export is local-only: say so, because a coordinator who
            // expected the file to appear in Cloud Docs would otherwise go looking.
            OutcomeDetail.Text = string.IsNullOrEmpty(OutcomeDetail.Text)
                ? "Saved to disk only. Tick \"Also export and link NWC\" in the sync dialog to attach it to a version in BINA."
                : OutcomeDetail.Text + Environment.NewLine +
                  "Saved to disk only. Tick \"Also export and link NWC\" in the sync dialog to attach it to a version in BINA.";

            OpenFolderButton.Visibility = result != null && File.Exists(result.FilePath)
                ? Visibility.Visible
                : Visibility.Collapsed;
            DoneButton.Focus();
        }

        private void SetBadge(string glyph, string foreground, string background)
        {
            var conv = new System.Windows.Media.BrushConverter();
            OutcomeGlyph.Text = glyph;
            OutcomeGlyph.Foreground = (System.Windows.Media.Brush)conv.ConvertFromString(foreground);
            OutcomeBadge.Background = (System.Windows.Media.Brush)conv.ConvertFromString(background);
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = OutputFolder;
                if (Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not open the folder: {ex.Message}";
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            OutcomeRoot.Visibility = Visibility.Collapsed;
            FormRoot.Visibility = Visibility.Visible;
            OutcomeProgress.Visibility = Visibility.Collapsed;
            StatusText.Text = string.Empty;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private sealed class CoordinateChoice
        {
            public NwcCoordinates Value { get; set; }
            public string Label { get; set; }
        }
    }
}
