using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// Browses a project — area, then folder, then model, then version — and
    /// saves the chosen version to disk. WIP, Shared and Published are the three
    /// browsable areas; InReview and Archive are not.
    ///
    /// Everything listed comes from the server already filtered to the caller's
    /// role (docs/wip-browse-backend-spec.md §3). This window filters nothing of
    /// its own: it has no copy of the permission model, so what it renders IS
    /// what the user may see. A 403 anywhere in the chain is therefore reported
    /// as "you do not have access", not as an empty folder — the two send a
    /// drafter to completely different people.
    ///
    /// Unlike the rollback picker this replaces, nothing here touches the open
    /// document, so there is no ExternalEvent to marshal and no reason to close
    /// before the bytes land. The window stays up through the download and
    /// reports the destination back to the command.
    /// </summary>
    public partial class ModelBrowserWindow : Window
    {
        private readonly SyncApiClient _api;
        private readonly int _projectId;
        private readonly string _downloadRoot;

        private CancellationTokenSource _cancellation;
        private bool _downloading;

        /// <summary>Which area the folder column is showing. WIP until switched.</summary>
        private string _area = BimArea.Wip;

        // The full page as loaded, kept aside so the search box can filter
        // without a round trip per keystroke.
        private List<ModelRow> _allModels = new List<ModelRow>();
        private bool _modelsTruncated;

        /// <summary>Where the chosen version landed. Null unless DialogResult is true.</summary>
        public string DownloadedPath { get; private set; }

        public ModelBrowserWindow(SyncApiClient api, int projectId, string projectName, string downloadRoot)
        {
            InitializeComponent();

            _api = api;
            _projectId = projectId;
            _downloadRoot = downloadRoot;

            if (!string.IsNullOrWhiteSpace(projectName))
                Title = "Download a Model — " + projectName;

            Loaded += async (_, __) => await LoadFoldersAsync();
        }

        // ---------------------------------------------------------------- rows

        private sealed class FolderRow
        {
            public BimFolder Source { get; set; }
            public string Name { get; set; }
            public string DisciplineLine { get; set; }
            public Visibility DisciplineVisibility { get; set; }
        }

        private sealed class ModelRow
        {
            public BimDesign Source { get; set; }
            public string Name { get; set; }
            public string SubLine { get; set; }
            public string SizeText { get; set; }
            public Visibility LockedVisibility { get; set; }
            public Brush NameBrush { get; set; }
            public string PromotedLine { get; set; }
            public Visibility PromotedVisibility { get; set; }
        }

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
            public string PromotedLine { get; set; }
            public Visibility PromotedVisibility { get; set; }
        }

        // ------------------------------------------------------------- folders

        private async System.Threading.Tasks.Task LoadFoldersAsync()
        {
            try
            {
                string requested = _area;
                var folders = await _api.GetFoldersAsync(_projectId, requested);

                // A slow area switch must not overwrite the column with the
                // previous area's folders — the user would pick a folder that no
                // longer matches the toggle they are looking at.
                if (!string.Equals(requested, _area, StringComparison.OrdinalIgnoreCase)) return;

                if (folders == null || folders.Count == 0)
                {
                    FoldersMessage.Text = "No " + BimArea.Label(_area)
                        + " folders in this project that your role can see.";
                    FoldersMessage.Visibility = Visibility.Visible;
                    return;
                }

                FoldersListBox.ItemsSource = folders.Select(f => new FolderRow
                {
                    Source = f,
                    Name = string.IsNullOrWhiteSpace(f.Name) ? "(unnamed folder)" : f.Name,
                    DisciplineLine = f.DisciplineType,
                    DisciplineVisibility = string.IsNullOrWhiteSpace(f.DisciplineType)
                        ? Visibility.Collapsed
                        : Visibility.Visible
                }).ToList();

                FoldersMessage.Visibility = Visibility.Collapsed;
            }
            catch (BinaAccessDeniedException ex)
            {
                FoldersMessage.Text = ex.Message;
                FoldersMessage.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                FoldersMessage.Text = "Could not load folders.";
                FoldersMessage.Visibility = Visibility.Visible;
                ShowError("Could not load " + BimArea.Label(_area) + " folders. " + ex.Message);
            }
        }

        /// <summary>
        /// Switching area invalidates everything downstream: different folders,
        /// different models, different versions. All three columns reset rather
        /// than leaving the previous area's selection visible next to the new
        /// area's folder list.
        /// </summary>
        private async void AreaRadio_Checked(object sender, RoutedEventArgs e)
        {
            var radio = sender as System.Windows.Controls.RadioButton;
            if (radio == null || !IsLoaded) return;

            string area = radio.Tag as string;
            if (string.IsNullOrEmpty(area) || string.Equals(area, _area, StringComparison.OrdinalIgnoreCase))
                return;

            // A download in flight owns the window; the toggle is disabled for its
            // duration, but a queued click could still land here.
            if (_downloading) return;

            _area = area;

            FoldersListBox.ItemsSource = null;
            FoldersMessage.Text = "Loading " + BimArea.Label(_area) + " folders…";
            FoldersMessage.Visibility = Visibility.Visible;
            ClearModels("Pick a folder to see its models.");
            ClearVersions("Pick a model to see its versions.");
            ResetDestination();
            ErrorBox.Visibility = Visibility.Collapsed;

            await LoadFoldersAsync();
        }

        private async void FoldersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_downloading) return;

            var folder = FoldersListBox.SelectedItem as FolderRow;
            if (folder == null) return;

            ClearModels("Loading models…");
            ClearVersions("Pick a model to see its versions.");
            ResetDestination();
            ErrorBox.Visibility = Visibility.Collapsed;

            await LoadModelsAsync(folder.Source.Id);
        }

        // -------------------------------------------------------------- models

        private async System.Threading.Tasks.Task LoadModelsAsync(int folderId)
        {
            try
            {
                // Area is an assertion here: folder ids are unique project-wide,
                // so the server derives the area itself and answers 404 if this
                // folder is not in the area the toggle claims.
                var page = await _api.GetDesignsAsync(_projectId, folderId, _area);

                _allModels = (page.Designs ?? new List<BimDesign>()).Select(ToModelRow).ToList();
                // hasMore, not "the cursor is set": bina-be always sends it, and a
                // full last page with no cursor is still the whole folder.
                _modelsTruncated = page.IsPartial;

                if (_allModels.Count == 0)
                {
                    ClearModels("No models in this folder that your role can see.");
                    return;
                }

                // The search box earns its space only once scrolling would be the
                // alternative; below that it is one more control to ignore.
                SearchBox.Visibility = _allModels.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
                SearchBox.Text = "";

                ApplyModelFilter();
                ModelsMessage.Visibility = Visibility.Collapsed;
            }
            catch (BinaAccessDeniedException ex)
            {
                ClearModels(ex.Message);
            }
            catch (Exception ex)
            {
                ClearModels("Could not load models.");
                ShowError("Could not load the models in this folder. " + ex.Message);
            }
        }

        private static ModelRow ToModelRow(BimDesign d)
        {
            string when = d.UploadedAt.HasValue
                ? d.UploadedAt.Value.ToLocalTime().ToString("d MMM yyyy")
                : null;
            string who = string.IsNullOrWhiteSpace(d.UploaderName) ? null : d.UploaderName;

            var parts = new List<string>();
            if (d.VersionNumber.HasValue) parts.Add("V" + d.VersionNumber.Value + " latest");
            if (d.VersionCount.HasValue && d.VersionCount.Value > 0)
                parts.Add(d.VersionCount.Value == 1 ? "1 version" : d.VersionCount.Value + " versions");
            if (when != null) parts.Add(when);
            if (who != null) parts.Add(who);

            return new ModelRow
            {
                Source = d,
                Name = string.IsNullOrWhiteSpace(d.Name) ? "(unnamed model)" : d.Name,
                SubLine = string.Join(" · ", parts),
                SizeText = FormatSize(d.FileSize),
                LockedVisibility = d.IsDownloadable ? Visibility.Collapsed : Visibility.Visible,
                PromotedLine = DescribePromotion(d.PromotedFromArea, d.PromotedFromVersionNumber),
                PromotedVisibility = d.HasPromotionMismatch ? Visibility.Visible : Visibility.Collapsed,
                NameBrush = d.IsDownloadable
                    ? new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37))
                    : new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2))
            };
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_downloading) return;
            ApplyModelFilter();
        }

        private void ApplyModelFilter()
        {
            string needle = SearchBox.Text == null ? "" : SearchBox.Text.Trim();

            var shown = string.IsNullOrEmpty(needle)
                ? _allModels
                : _allModels.Where(m => m.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            ModelsListBox.ItemsSource = shown;

            if (shown.Count == 0 && !string.IsNullOrEmpty(needle))
            {
                ModelsMessage.Text = "No model here matches \"" + needle + "\".";
                ModelsMessage.Visibility = Visibility.Visible;
            }
            else
            {
                ModelsMessage.Visibility = Visibility.Collapsed;
            }

            // Say so when the server sent a partial page, rather than letting the
            // count read as the whole folder.
            CountText.Text = _modelsTruncated
                ? shown.Count + " of many models — narrow with the search box"
                : (shown.Count == 1 ? "1 model" : shown.Count + " models");
        }

        private async void ModelsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_downloading) return;

            var model = ModelsListBox.SelectedItem as ModelRow;
            if (model == null) return;

            ClearVersions("Loading versions…");
            ResetDestination();
            ErrorBox.Visibility = Visibility.Collapsed;

            await LoadVersionsAsync(model.Source);
        }

        // ------------------------------------------------------------ versions

        private async System.Threading.Tasks.Task LoadVersionsAsync(BimDesign design)
        {
            try
            {
                // designId is the right key everywhere except a WIP row that
                // actually has a docGuid. Promoted Shared/Published rows carry no
                // GUID at all (the copy path does not copy it), and neither do
                // web uploads — for those, designId is the only lookup that
                // resolves. The area rides along either way so a GUID that ever
                // does get copied cannot resolve to the wrong area's history.
                string area = string.IsNullOrWhiteSpace(design.Area) ? _area : design.Area;

                bool useDocGuid = !string.IsNullOrEmpty(design.DocGuid)
                                  && string.Equals(area, BimArea.Wip, StringComparison.OrdinalIgnoreCase);

                var versions = useDocGuid
                    ? await _api.GetVersionsAsync(_projectId, design.DocGuid, area)
                    : await _api.GetVersionsByDesignAsync(_projectId, design.DesignId, area);

                if (versions == null || versions.Count == 0)
                {
                    ClearVersions("This model has no version history yet.");
                    return;
                }

                var rows = versions.Select(ToVersionRow).ToList();
                LabelRestoredFrom(rows);
                VersionsListBox.ItemsSource = rows;
                VersionsMessage.Visibility = Visibility.Collapsed;

                // The latest version is what a drafter wants most of the time, so
                // it is pre-selected — the older ones are one click away.
                VersionsListBox.SelectedItem = rows.FirstOrDefault(r => r.Source.IsActive) ?? rows[0];
            }
            catch (BinaAccessDeniedException ex)
            {
                ClearVersions(ex.Message);
            }
            catch (Exception ex)
            {
                ClearVersions("Could not load versions.");
                ShowError("Could not load this model's versions. " + ex.Message);
            }
        }

        private static VersionRow ToVersionRow(DesignVersion v)
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
                RestoredFromLine = null,
                RestoredFromVisibility = v.RolledBackFromDesignId.HasValue
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                SizeText = FormatSize(v.FileSize),
                TranslationText = DescribeTranslation(v),
                CurrentBadgeVisibility = v.IsActive ? Visibility.Visible : Visibility.Collapsed,
                PromotedLine = DescribePromotion(v.PromotedFromArea, v.PromotedFromVersionNumber),
                PromotedVisibility = v.HasPromotionMismatch ? Visibility.Visible : Visibility.Collapsed
            };
        }

        /// <summary>
        /// Names the version a rollback started from, resolving the design id
        /// against the rest of the list — the API returns an id, and "V3" is what
        /// a human can act on. Carried over from the rollback picker: history
        /// published before this feature still carries those markers.
        /// </summary>
        private static void LabelRestoredFrom(List<VersionRow> rows)
        {
            foreach (var row in rows)
            {
                int? fromId = row.Source.RolledBackFromDesignId;
                if (!fromId.HasValue) continue;

                var source = rows.FirstOrDefault(r => r.Source.DesignId == fromId.Value);

                row.RestoredFromLine = source != null
                    ? "based on " + source.VersionLabel
                    : "based on an earlier version";
            }
        }

        private void VersionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_downloading) return;

            var version = VersionsListBox.SelectedItem as VersionRow;
            var model = ModelsListBox.SelectedItem as ModelRow;

            if (version == null || model == null)
            {
                ResetDestination();
                return;
            }

            DestinationText.Text = BuildDefaultDestination(model, version.Source);
            ChangeDestinationButton.IsEnabled = true;

            // A browse-only role is told why the button is dead, rather than being
            // left to click a control that silently does nothing.
            if (!model.Source.IsDownloadable)
            {
                DownloadButton.IsEnabled = false;
                ShowError("Your role can browse this model but not download it. Ask the project administrator for download access.");
                return;
            }

            ErrorBox.Visibility = Visibility.Collapsed;
            DownloadButton.IsEnabled = true;
        }

        // ------------------------------------------------------------ download

        private void ChangeDestinationButton_Click(object sender, RoutedEventArgs e)
        {
            string current = DestinationText.Text;
            if (string.IsNullOrWhiteSpace(current)) return;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save this version as",
                FileName = Path.GetFileName(current),
                DefaultExt = ".rvt",
                Filter = "Revit model (*.rvt)|*.rvt|All files (*.*)|*.*",
                OverwritePrompt = false   // asked once, at download time
            };

            try
            {
                string dir = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    dialog.InitialDirectory = dir;
                }
            }
            catch
            {
                // A directory we cannot pre-create is not fatal: the dialog opens
                // wherever Windows last left it and the user picks somewhere else.
            }

            if (dialog.ShowDialog(this) == true)
                DestinationText.Text = dialog.FileName;
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var version = VersionsListBox.SelectedItem as VersionRow;
            var model = ModelsListBox.SelectedItem as ModelRow;
            if (version == null || model == null || !model.Source.IsDownloadable) return;

            string destination = DestinationText.Text;
            if (string.IsNullOrWhiteSpace(destination)) return;

            if (File.Exists(destination))
            {
                var answer = MessageBox.Show(this,
                    "A file already exists at:\n\n" + destination + "\n\nReplace it with " + version.VersionLabel + "?",
                    "Replace file?", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

                if (answer != MessageBoxResult.OK) return;
            }

            await DownloadAsync(version, destination);
        }

        private async System.Threading.Tasks.Task DownloadAsync(VersionRow row, string destination)
        {
            _downloading = true;

            if (_cancellation != null) _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();

            SetBrowsingEnabled(false);
            CancelButton.Content = "Cancel download";
            ErrorBox.Visibility = Visibility.Collapsed;
            ProgressBox.Visibility = Visibility.Visible;
            ProgressText.Text = "Preparing…";
            ProgressBar.Value = 0;

            // Stream to a sibling .part and swap on success. Downloading straight
            // onto the destination would let a failed transfer delete a good file
            // the user already had — DownloadAsync cleans up its partial file.
            string staging = destination + ".part";

            var progress = new Progress<(double Fraction, string Message)>(p =>
            {
                ProgressText.Text = p.Message;
                if (p.Fraction > 0) ProgressBar.Value = Math.Min(1.0, p.Fraction);
            });

            try
            {
                string directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                await _api.DownloadAsync(row.Source.DesignId, staging, progress, _cancellation.Token);

                if (File.Exists(destination)) File.Delete(destination);
                File.Move(staging, destination);

                DownloadedPath = destination;
                CloseWithSuccess();
            }
            catch (OperationCanceledException)
            {
                CleanUpStaging(staging);
                ResetAfterFailedDownload();
            }
            catch (BinaAccessDeniedException ex)
            {
                CleanUpStaging(staging);
                ResetAfterFailedDownload();
                ShowError(ex.Message);
            }
            catch (Exception ex)
            {
                CleanUpStaging(staging);
                ResetAfterFailedDownload();
                ShowError("Download failed. " + ex.Message);
            }
        }

        /// <summary>
        /// Setting DialogResult is only legal on a window opened with ShowDialog,
        /// which is how the command opens this one. The UI harness opens it with
        /// Show() instead, and there the setter throws — a failure that would be
        /// reported to the user as a failed download despite the bytes being on
        /// disk. Close() is the right outcome either way.
        /// </summary>
        private void CloseWithSuccess()
        {
            try
            {
                DialogResult = true;
            }
            catch (InvalidOperationException)
            {
                Close();
            }
        }

        private static void CleanUpStaging(string staging)
        {
            try { if (File.Exists(staging)) File.Delete(staging); } catch { }
        }

        private void ResetAfterFailedDownload()
        {
            _downloading = false;
            SetBrowsingEnabled(true);
            CancelButton.Content = "Close";
            ProgressBox.Visibility = Visibility.Collapsed;

            var model = ModelsListBox.SelectedItem as ModelRow;
            DownloadButton.IsEnabled = VersionsListBox.SelectedItem != null
                                       && model != null && model.Source.IsDownloadable;
        }

        private void SetBrowsingEnabled(bool enabled)
        {
            WipRadio.IsEnabled = enabled;
            SharedRadio.IsEnabled = enabled;
            PublishedRadio.IsEnabled = enabled;
            FoldersListBox.IsEnabled = enabled;
            ModelsListBox.IsEnabled = enabled;
            VersionsListBox.IsEnabled = enabled;
            SearchBox.IsEnabled = enabled;
            ChangeDestinationButton.IsEnabled = enabled && !string.IsNullOrWhiteSpace(DestinationText.Text);
            DownloadButton.IsEnabled = false;
        }

        /// <summary>
        /// Folder and model names appear in the path, so a drafter can find the
        /// file later without remembering which BINA folder it came from.
        /// </summary>
        private string BuildDefaultDestination(ModelRow model, DesignVersion version)
        {
            var folder = FoldersListBox.SelectedItem as FolderRow;
            string folderName = folder != null ? SafeName(folder.Name) : "wip";

            string stem = Path.GetFileNameWithoutExtension(model.Name);
            if (string.IsNullOrWhiteSpace(stem)) stem = "model";
            stem = SafeName(stem);

            string versionTag = version.VersionNumber.HasValue
                ? "v" + version.VersionNumber.Value
                : "v" + version.DesignId;

            return Path.Combine(
                _downloadRoot,
                "project-" + _projectId,
                BimArea.Label(_area),
                folderName,
                stem,
                stem + "-" + versionTag + ".rvt");
        }

        private void ResetDestination()
        {
            DestinationText.Text = "Pick a version first.";
            ChangeDestinationButton.IsEnabled = false;
            DownloadButton.IsEnabled = false;
        }

        // --------------------------------------------------------------- misc

        private void ClearModels(string message)
        {
            ModelsListBox.ItemsSource = null;
            _allModels = new List<ModelRow>();
            _modelsTruncated = false;
            SearchBox.Visibility = Visibility.Collapsed;
            ModelsMessage.Text = message;
            ModelsMessage.Visibility = Visibility.Visible;
            CountText.Text = "";
        }

        private void ClearVersions(string message)
        {
            VersionsListBox.ItemsSource = null;
            VersionsMessage.Text = message;
            VersionsMessage.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Named only when promotion did NOT mirror the version number. Promotion
        /// normally carries the number across — WIP V6 becomes Shared V6 — and
        /// saying "promoted from WIP V6" on a row already labelled V6 is noise.
        /// It steps up only when the number was already taken in the target
        /// folder by different content, and that is the case a drafter needs
        /// told, because the number they know the file by is no longer the
        /// number in front of them.
        /// </summary>
        private static string DescribePromotion(string fromArea, int? fromVersion)
        {
            if (!fromVersion.HasValue) return null;

            string area = string.IsNullOrWhiteSpace(fromArea) ? "" : BimArea.Label(fromArea) + " ";
            return "promoted from " + area + "V" + fromVersion.Value;
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
        /// Whether the version is viewable in the web app. Not a download
        /// blocker — the .rvt downloads regardless — but a user choosing between
        /// two versions wants to know which one they can preview first.
        /// </summary>
        private static string DescribeTranslation(DesignVersion v)
        {
            if (!string.IsNullOrEmpty(v.UrnInBase64)) return "viewable";
            if (string.Equals(v.XktConversionStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                return "viewable";
            return "not translated";
        }

        private static string SafeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars())
                raw = raw.Replace(c, '_');
            return raw;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_downloading && _cancellation != null)
            {
                // First Cancel stops the transfer; the window stays open so the
                // user can pick a different version rather than starting over.
                _cancellation.Cancel();
                return;
            }

            DialogResult = false;
        }

        /// <summary>
        /// Closing mid-download (title-bar X, Esc) must stop the transfer: the
        /// command disposes the API client as soon as ShowDialog returns, and a
        /// download left running would write on into a disposed client.
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
