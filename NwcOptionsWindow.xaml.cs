using System;
using System.IO;
using System.Windows;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// What to export and where (ClickUp 86d49v9ak).
    ///
    /// Coordinates lead because that is the one choice with a wrong answer — a
    /// cache exported on project internal coordinates lands somewhere else when
    /// the coordinator federates it, and nothing about the file says so. Purge
    /// comes second and off, because it is the only control here that edits the
    /// model rather than reading it.
    /// </summary>
    public partial class NwcOptionsWindow : Window
    {
        private string _outputFolder;
        private readonly string _fileName;

        /// <summary>Set once the user clicks Export.</summary>
        public NwcExportSettings Settings { get; private set; }

        /// <param name="modelFileName">The open document's file name.</param>
        /// <param name="defaultFolder">The document's own folder.</param>
        /// <param name="purgeSupported">
        /// False on Revit 2023/2024, where the purge API is not in the payload at
        /// all — see <see cref="NwcPurgeSupport"/>. The tick is disabled and says
        /// why rather than being hidden, so the option does not appear to vanish
        /// between machines.
        /// </param>
        public NwcOptionsWindow(string modelFileName, string defaultFolder, bool purgeSupported)
        {
            InitializeComponent();

            _fileName = NwcFileName.FromModelName(modelFileName);
            _outputFolder = defaultFolder;

            SubtitleText.Text =
                $"Exports {_fileName} from a temporary 3D view. Your own views are not changed.";

            if (!purgeSupported)
            {
                PurgeCheck.IsChecked = false;
                PurgeCheck.IsEnabled = false;
                PurgeNote.Text = NwcPurgeSupport.UnsupportedNote;
                PurgeNote.Visibility = Visibility.Visible;
                PurgeHint.Visibility = Visibility.Collapsed;
            }

            UpdateDestinationText();
        }

        private void UpdateDestinationText()
        {
            DestinationText.Text = string.IsNullOrEmpty(_outputFolder)
                ? _fileName
                : Path.Combine(_outputFolder, _fileName);
        }

        /// <summary>
        /// A save dialog rather than a folder picker: .NET Framework 4.8 has no
        /// WPF folder dialog, and the add-in ships to Revit 2023 on net48. Same
        /// choice the download destination control makes. Only the directory is
        /// kept — the file name follows the model's, so that a re-export replaces
        /// the previous link instead of adding a second one.
        /// </summary>
        private void ChangeDestinationButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export NWC to",
                FileName = _fileName,
                DefaultExt = ".nwc",
                Filter = "Navisworks cache (*.nwc)|*.nwc|All files (*.*)|*.*",
                OverwritePrompt = false
            };

            try
            {
                if (!string.IsNullOrEmpty(_outputFolder) && Directory.Exists(_outputFolder))
                    dialog.InitialDirectory = _outputFolder;
            }
            catch
            {
                // The dialog opens wherever Windows last left it; not worth a stop.
            }

            if (dialog.ShowDialog(this) != true) return;

            string chosen = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(chosen)) _outputFolder = chosen;

            UpdateDestinationText();
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            Settings = new NwcExportSettings
            {
                Coordinates = CoordinatesCombo.SelectedIndex == 1
                    ? NwcCoordinates.Internal
                    : NwcCoordinates.Shared,
                PurgeUnused = PurgeCheck.IsChecked == true,
                OutputFolder = _outputFolder
            };

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
