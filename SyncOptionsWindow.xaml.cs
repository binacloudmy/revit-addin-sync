using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// Everything the user confirms before a single byte is uploaded: project,
    /// discipline, folder, which model's history this version joins, and an
    /// optional note (ClickUp 86d3x42mz).
    ///
    /// The order matters and mirrors BINA's own structure: a folder lives under
    /// a discipline (BIM Models -> Architecture -> WIP -> folder), so discipline
    /// is chosen first and scopes the folder list. Asking for the folder first,
    /// unscoped, offered folders from other disciplines.
    ///
    /// The project is pre-selected from config but always shown, because the
    /// stored value drifts — browser sign-in used to hard-code project 1
    /// ("Demo") for everyone. The folder is mandatory: bina-be rejects
    /// root-level uploads for WorkInProgress, so offering "no folder" would only
    /// fail after the upload finished.
    ///
    /// ---- How a chain is chosen ----
    ///
    /// A picked model is sent as <see cref="TargetLineageId"/>, and bina-be files
    /// the version into that chain whatever the uploaded file is called. The
    /// upload name defaults to the local filename, but the header's ✎ Edit
    /// control can change it (<see cref="UploadFileName"/>) — working copies
    /// arrive as "Copy of X (5).rvt" and that is rarely the wanted model name.
    /// The local file on disk is never renamed.
    ///
    /// Without a target, the server still resolves the lineage the old way, from
    /// `projectId + designStatus + parentId + fileName` (`applyLineageScope`).
    /// That is why "new model" is a promise this dialog cannot keep on its own: a
    /// model of the same name already in the folder will be joined regardless.
    /// The collision is detected here and shown before the upload.
    ///
    /// The document GUID is provenance, never matched on. A sync joining an
    /// existing lineage still sends that chain's head GUID
    /// (<see cref="LineageDocGuid"/>, frequently null) rather than this
    /// document's own: it feeds `lineageKey`
    /// (`sha2(projectId|parentId|status|ifnull(sourceDocumentGuid, name))`), and
    /// a GUID differing from the head's forks that key. The unique indexes over
    /// it were dropped to let `targetLineageId` ship, so nothing rejects a fork
    /// today — but their migration's `down()` refuses to restore them once
    /// duplicates exist. Keeping the head's GUID keeps that door open, and costs
    /// nothing.
    /// </summary>
    public partial class SyncOptionsWindow : Window
    {
        private readonly SyncApiClient _api;
        /// <summary>The name this sync uploads under. Starts as the document's
        /// own filename; the ✎ Edit control in the header can change it.</summary>
        private string _fileName;
        private readonly string _docGuid;
        private bool _loading;
        private bool _nameEditCancelled;
        private bool _nameEditedOnce;

        private List<ModelRow> _allModels = new List<ModelRow>();
        private bool _modelsTruncated;

        /// <summary>
        /// False when this dialog cannot produce a valid sync at all — no
        /// project, no folder. Held separately because SetBusy(false) used to
        /// re-enable the Sync button it had just switched off, leaving "no WIP
        /// folders here" one click away from an upload that could only fail.
        /// </summary>
        private bool _canSync = true;

        /// <summary>The filename this sync uploads under — the document's own
        /// name unless the user edited it in the header.</summary>
        public string UploadFileName => _fileName;

        public int SelectedProjectId { get; private set; }
        public string SelectedProjectName { get; private set; }
        public int? SelectedFolderId { get; private set; }
        public string SelectedDiscipline { get; private set; }
        public string Comment { get; private set; }
        /// <summary>Version this machine is basing its sync on; null when never synced.</summary>
        public int? BaseVersion { get; private set; }

        /// <summary>
        /// True when this sync lands in a chain that already exists — either
        /// because the user picked one, or because a model of the same name is
        /// already in the folder and the server will join it regardless.
        /// </summary>
        public bool JoinsExistingLineage { get; private set; }

        /// <summary>
        /// GUID to send when <see cref="JoinsExistingLineage"/>: the target
        /// head's, which is frequently null (web uploads carry none). Null then
        /// means "send null and inherit the head's" — NOT "fall back to the
        /// document's own stamp", which would fork `lineageKey` and put the
        /// dropped unique indexes permanently out of reach of a restore.
        /// </summary>
        public string LineageDocGuid { get; private set; }

        /// <summary>The chain this sync joins, when one was picked. Sent as `targetLineageId`.</summary>
        public string TargetLineageId { get; private set; }
        /// <summary>Head version's design id of the picked model; for logging and the outcome text.</summary>
        public int? TargetDesignId { get; private set; }
        /// <summary>Picked model's name, for the outcome dialog.</summary>
        public string TargetName { get; private set; }
        /// <summary>
        /// Head file hash of the chain being joined, when the browse reported
        /// one. The runner uses it to answer an identical re-sync without
        /// uploading — bina-be's own unchanged check is switched off.
        /// </summary>
        public string TargetFileHash { get; private set; }

        private sealed class DisciplineChoice
        {
            public string ApiValue { get; set; }
            public string Label { get; set; }
        }

        /// <summary>One model already in the chosen folder — one row per lineage.</summary>
        private sealed class ModelRow
        {
            public BimDesign Source { get; set; }
            public string Name { get; set; }
            public string SubLine { get; set; }
            public string SizeText { get; set; }
            /// <summary>Shown on the row whose name equals this document's filename.</summary>
            public Visibility BadgeVisibility { get; set; }
        }

        public SyncOptionsWindow(
            SyncApiClient api,
            string fileName,
            string docGuid,
            int defaultProjectId,
            string defaultProjectName,
            string suggestedDiscipline)
        {
            InitializeComponent();

            _api = api;
            _fileName = fileName;
            _docGuid = docGuid;

            FileNameText.Text = fileName;
            SelectedProjectId = defaultProjectId;
            SelectedProjectName = defaultProjectName;

            DisciplineCombo.ItemsSource = DisciplineTypes.Selectable
                .Select(d => new DisciplineChoice { ApiValue = d.ApiValue, Label = d.Label })
                .ToList();
            DisciplineCombo.SelectedIndex = Math.Max(
                0,
                DisciplineTypes.Selectable
                    .ToList()
                    .FindIndex(d => d.ApiValue == DisciplineTypes.ToApiValue(suggestedDiscipline)));

            Loaded += async (_, __) => await LoadProjectsAsync();
        }

        private async System.Threading.Tasks.Task LoadProjectsAsync()
        {
            try
            {
                SetBusy(true, "Loading projects…");
                var projects = await _api.GetProjectsAsync();
                ProjectCombo.ItemsSource = projects;

                var current = projects.FirstOrDefault(p => p.Id == SelectedProjectId);
                ProjectCombo.SelectedItem = current ?? projects.FirstOrDefault();
                var chosen = ProjectCombo.SelectedItem as ProjectInfo;
                if (chosen != null)
                {
                    SelectedProjectId = chosen.Id;
                    SelectedProjectName = chosen.Name;
                }

                if (current == null && SelectedProjectId > 0)
                {
                    // The stored project is not one this user can sync into — say so
                    // rather than silently switching them to a different job.
                    StatusText.Text =
                        $"Your last project (#{SelectedProjectId}) is not available to you here. Pick another.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load projects: {ex.Message}";
                _canSync = false;
            }
            finally
            {
                SetBusy(false, null);
            }

            // Load folders explicitly. Setting SelectedItem above fires
            // SelectionChanged while _loading is still true, and that handler
            // bails out — which left the folder list permanently empty, with no
            // way to retry short of picking a different project and back.
            await LoadFoldersAndHeadAsync();
        }

        private async void ProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var project = ProjectCombo.SelectedItem as ProjectInfo;
            if (project == null) return;

            SelectedProjectId = project.Id;
            SelectedProjectName = project.Name;

            await LoadFoldersAndHeadAsync();
        }

        /// <summary>
        /// Folders belong to a project, and both the model list and the head
        /// belong to a folder, so all three are refetched whenever the project
        /// changes.
        /// </summary>
        private async System.Threading.Tasks.Task LoadFoldersAndHeadAsync()
        {
            try
            {
                SetBusy(true, "Loading folders…");
                FolderHint.Visibility = Visibility.Collapsed;
                NameMismatchWarning.Visibility = Visibility.Collapsed;

                // Folders live under a discipline in BINA (BIM Models ->
                // Architecture -> WIP -> folder), so the list is scoped to the
                // discipline chosen above rather than showing every folder in
                // the project.
                string discipline = (DisciplineCombo.SelectedItem as DisciplineChoice)?.ApiValue;
                var folders = await _api.GetFoldersAsync(SelectedProjectId, BimArea.Wip, discipline);
                FolderCombo.ItemsSource = folders;
                FolderCombo.SelectedItem = folders.FirstOrDefault();

                if (!folders.Any())
                {
                    // Uploading would fail server-side; stop here with an
                    // explanation instead of after a long upload.
                    string disciplineLabel = (DisciplineCombo.SelectedItem as DisciplineChoice)?.Label ?? "this discipline";
                    FolderHint.Text =
                        $"No work-in-progress folders under {disciplineLabel} in this project. " +
                        "Create one in BINA Cloud Docs (BIM Models → " + disciplineLabel + " → WIP → New), then reopen this dialog.";
                    FolderHint.Visibility = Visibility.Visible;
                    ClearModels("No folder chosen.");
                    ExistingModelRadio.IsEnabled = false;
                    NewModelRadio.IsChecked = true;
                    _canSync = false;
                    return;
                }

                _canSync = true;

                await LoadFolderModelsAsync();
                await RefreshHeadAsync();
                UpdateNameMismatchWarning();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load folders: {ex.Message}";
                _canSync = false;
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async void DisciplineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            await LoadFoldersAndHeadAsync();
        }

        private async void FolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            // Lineage is scoped to the folder, so the pickable models and their
            // warnings follow the folder choice.
            await LoadFolderModelsAsync();
            await RefreshHeadAsync();
            UpdateNameMismatchWarning();
        }

        // --------------------------------------------------------------- models

        /// <summary>
        /// The models already in the chosen folder — one row per lineage, the
        /// same browse endpoint the WIP browser uses.
        /// </summary>
        private async System.Threading.Tasks.Task LoadFolderModelsAsync()
        {
            int? folderId = (FolderCombo.SelectedItem as BimFolder)?.Id;
            if (!folderId.HasValue)
            {
                ClearModels("No folder chosen.");
                return;
            }

            try
            {
                ClearModels("Loading models…");

                // Area is an assertion: folder ids are unique project-wide, so a
                // mismatch is a 404 rather than a cross-area read.
                var page = await _api.GetDesignsAsync(SelectedProjectId, folderId.Value, BimArea.Wip);

                _allModels = (page.Designs ?? new List<BimDesign>()).Select(ToModelRow).ToList();
                _modelsTruncated = page.IsPartial;

                if (_allModels.Count == 0)
                {
                    // Nothing to join — the choice collapses to "new model".
                    ClearModels("No models in this folder yet.");
                    ExistingModelRadio.IsEnabled = false;
                    NewModelRadio.IsChecked = true;
                    return;
                }

                ExistingModelRadio.IsEnabled = true;

                // The search box earns its space only once scrolling would be the
                // alternative; below that it is one more control to ignore.
                SearchArea.Visibility = _allModels.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
                SearchBox.Text = "";

                ApplyModelFilter();
                ModelsMessage.Visibility = Visibility.Collapsed;

                // A model already synced from this document is the one the user
                // almost always means, so it starts selected.
                var preferred = _allModels.FirstOrDefault(m => SameName(m.Source.Name, _fileName));
                if (preferred != null) ModelsListBox.SelectedItem = preferred;
            }
            catch (BinaAccessDeniedException ex)
            {
                ClearModels(ex.Message);
                ExistingModelRadio.IsEnabled = false;
            }
            catch (Exception ex)
            {
                ClearModels("Could not load the models in this folder. " + ex.Message);
                ExistingModelRadio.IsEnabled = false;
            }
            finally
            {
                UpdateCollisionWarning();
            }
        }

        private ModelRow ToModelRow(BimDesign d)
        {
            string when = d.UploadedAt.HasValue
                ? d.UploadedAt.Value.ToLocalTime().ToString("d MMM yyyy")
                : null;

            var parts = new List<string>();
            if (d.VersionNumber.HasValue) parts.Add("V" + d.VersionNumber.Value + " latest");
            if (d.VersionCount.HasValue && d.VersionCount.Value > 0)
                parts.Add(d.VersionCount.Value == 1 ? "1 version" : d.VersionCount.Value + " versions");
            if (when != null) parts.Add(when);
            if (!string.IsNullOrWhiteSpace(d.UploaderName)) parts.Add(d.UploaderName);

            return new ModelRow
            {
                Source = d,
                Name = string.IsNullOrWhiteSpace(d.Name) ? "(unnamed model)" : d.Name,
                SubLine = string.Join(" · ", parts),
                SizeText = FormatSize(d.FileSize),
                BadgeVisibility = SameName(d.Name, _fileName) ? Visibility.Visible : Visibility.Collapsed
            };
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (_loading) return;
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
            ModelsCount.Text = _modelsTruncated
                ? shown.Count + " of many models — narrow with the search box"
                : (shown.Count == 1 ? "1 model" : shown.Count + " models");
        }

        private void ClearModels(string message)
        {
            _allModels = new List<ModelRow>();
            _modelsTruncated = false;
            ModelsListBox.ItemsSource = null;
            SearchArea.Visibility = Visibility.Collapsed;
            ModelsCount.Text = "";
            ModelsMessage.Text = message;
            ModelsMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ModelsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateNameMismatchWarning();
        }

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            // Fires during InitializeComponent, before the panels exist.
            if (ModelsPanel == null) return;

            bool existing = ExistingModelRadio.IsChecked == true;
            ModelsPanel.Visibility = existing ? Visibility.Visible : Visibility.Collapsed;

            UpdateNameMismatchWarning();
            UpdateCollisionWarning();
        }

        // ------------------------------------------------------- upload name

        private void EditName_Click(object sender, RoutedEventArgs e)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(_fileName);

            // First open only: suggest the cleaned name ("Copy of X (5)" -> "X").
            // Later opens show whatever the user last committed — re-cleaning
            // would fight an accepted name.
            if (!_nameEditedOnce) stem = CleanCopyName(stem);

            NameEditBox.Text = stem;
            _nameEditCancelled = false;
            NameDisplayPanel.Visibility = Visibility.Collapsed;
            NameEditPanel.Visibility = Visibility.Visible;
            NameEditBox.Focus();
            NameEditBox.SelectAll();
        }

        /// <summary>"Copy of Copy of X (5)" → "X": the noise Revit/Explorer/ACC
        /// put on a duplicated file, not part of any real model name.</summary>
        private static string CleanCopyName(string stem)
        {
            string s = stem.Trim();
            while (s.StartsWith("Copy of ", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("Copy of ".Length).Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[-–]?\s*Copy$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(\d+\)$", "").Trim();
            return s.Length == 0 ? stem : s;
        }

        private void NameEditBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                // Without this, Enter would also fire the default Sync button.
                e.Handled = true;
                CommitNameEdit();
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                _nameEditCancelled = true;
                CloseNameEditor();
            }
        }

        private void NameEditBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (NameEditPanel.Visibility != Visibility.Visible || _nameEditCancelled) return;
            CommitNameEdit();
        }

        private void CloseNameEditor()
        {
            NameEditPanel.Visibility = Visibility.Collapsed;
            NameDisplayPanel.Visibility = Visibility.Visible;
        }

        private void CommitNameEdit()
        {
            string stem = (NameEditBox.Text ?? "").Trim();

            // The user may type the extension out of habit; the label supplies it.
            if (stem.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - 4).TrimEnd();

            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                stem = stem.Replace(c.ToString(), "");

            CloseNameEditor();

            if (stem.Length == 0) return; // blank = keep the current name

            string newName = stem + ".rvt";
            if (SameName(newName, _fileName)) return;

            _fileName = newName;
            _nameEditedOnce = true;
            FileNameText.Text = newName;

            // Everything keyed on the filename follows it: the "matches this
            // file" badges, both warnings, and the server head the commit's
            // conflict check is based on.
            var selected = (ModelsListBox.SelectedItem as ModelRow)?.Source;
            _allModels = _allModels.Select(m => ToModelRow(m.Source)).ToList();
            ApplyModelFilter();
            if (selected != null)
                ModelsListBox.SelectedItem = _allModels.FirstOrDefault(m => m.Source == selected);

            UpdateCollisionWarning();
            UpdateNameMismatchWarning();
            _ = RefreshHeadAsync();
        }

        // ---------------------------------------------------------- warnings

        /// <summary>Server's current head for this document's own filename, in
        /// this folder. No longer shown, but it still bases the commit's
        /// conflict check (BaseVersion / TargetFileHash) in SyncButton_Click.</summary>
        private SyncHead _head;

        private async System.Threading.Tasks.Task RefreshHeadAsync()
        {
            try
            {
                _head = await _api.GetHeadAsync(
                    SelectedProjectId, _docGuid, _fileName,
                    (FolderCombo.SelectedItem as BimFolder)?.Id);
            }
            catch
            {
                // A head we cannot read is not worth blocking the sync over; the
                // server re-checks the version on commit regardless.
                _head = null;
            }
        }

        /// <summary>
        /// "Existing model" mode with a picked chain of a different name: the
        /// version keeps this file's own name, so the chain's history will read
        /// under two names from here on. Worth saying: a drafter looking for
        /// "ARC-Tower-A-Model.rvt" in Cloud Docs will find the newest version
        /// listed under something else.
        /// </summary>
        private void UpdateNameMismatchWarning()
        {
            if (NameMismatchWarning == null) return;

            var row = ModelsListBox.SelectedItem as ModelRow;
            bool show = ExistingModelRadio.IsChecked == true &&
                        row != null && !SameName(row.Source.Name, _fileName);

            if (show)
            {
                int next = (row.Source.VersionNumber ?? 0) + 1;
                NameMismatchText.Text =
                    $"Your file is named differently, so v{next} will appear in this model's history " +
                    $"as \"{_fileName}\".";
            }

            NameMismatchWarning.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// The warning under "New model": a same-named model in this folder means
        /// the server will file this sync into that chain no matter what is
        /// chosen here, because the filename is the identity.
        /// </summary>
        private void UpdateCollisionWarning()
        {
            if (CollisionWarning == null) return;

            var clash = FindClash();
            bool show = clash != null && NewModelRadio.IsChecked == true && !IsOwnChain(clash);

            if (show)
            {
                CollisionText.Text =
                    $"\"{clash.Name}\" already exists in this folder. Syncing will add " +
                    $"v{(clash.Source.VersionNumber ?? 0) + 1} to that model, not create a new one. " +
                    "Pick \"New version of an existing model\" to confirm, or rename your file first.";
            }

            CollisionWarning.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // -------------------------------------------------------------- plumbing

        /// <summary>The model in this folder already carrying this file's name, if any.</summary>
        private ModelRow FindClash() =>
            _allModels.FirstOrDefault(m => SameName(m.Source.Name, _fileName));

        /// <summary>
        /// True when a row is the chain this very document was last synced into
        /// — same provenance GUID. Syncing into it is the ordinary next version,
        /// not the name collision the warning is for.
        /// </summary>
        private bool IsOwnChain(ModelRow row) =>
            row != null && !string.IsNullOrEmpty(_docGuid) &&
            string.Equals(row.Source.DocGuid, _docGuid, StringComparison.OrdinalIgnoreCase);

        private static bool SameName(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string FormatSize(long? bytes)
        {
            if (!bytes.HasValue || bytes.Value <= 0) return "";
            double mb = bytes.Value / 1024d / 1024d;
            return mb >= 1024 ? (mb / 1024d).ToString("0.0") + " GB" : mb.ToString("0") + " MB";
        }

        private void SetBusy(bool busy, string status)
        {
            _loading = busy;
            ProjectCombo.IsEnabled = !busy;
            FolderCombo.IsEnabled = !busy;
            DisciplineCombo.IsEnabled = !busy;
            // The radios are left alone: "existing model" is enabled by whether
            // the folder holds anything, and a busy flag must not override that.
            ModelsListBox.IsEnabled = !busy;
            SearchBox.IsEnabled = !busy;
            SyncButton.IsEnabled = !busy && _canSync;
            if (status != null) StatusText.Text = status;
            else if (!busy && StatusText.Text.EndsWith("…")) StatusText.Text = "";
        }

        private void SyncButton_Click(object sender, RoutedEventArgs e)
        {
            var folder = FolderCombo.SelectedItem as BimFolder;
            if (folder == null)
            {
                StatusText.Text = "Choose a folder for this model.";
                return;
            }

            var discipline = DisciplineCombo.SelectedItem as DisciplineChoice;
            if (discipline == null)
            {
                StatusText.Text = "Choose a discipline.";
                return;
            }

            bool existing = ExistingModelRadio.IsChecked == true;
            ModelRow target = existing ? ModelsListBox.SelectedItem as ModelRow : null;

            if (existing && target == null)
            {
                StatusText.Text = "Choose which model this file is a new version of.";
                return;
            }

            SelectedFolderId = folder.Id;
            SelectedDiscipline = discipline.ApiValue;   // already a backend value
            Comment = string.IsNullOrWhiteSpace(CommentBox.Text) ? null : CommentBox.Text.Trim();

            if (existing)
            {
                // The file uploads under its own name; the chain is named
                // explicitly. The GUID follows the head — sending this document's
                // own would fork `lineageKey` and blind the unique indexes on
                // that chain.
                if (string.IsNullOrEmpty(target.Source.LineageId))
                {
                    // No lineage id means nothing to target with, and filing by
                    // name would put the version somewhere else entirely.
                    StatusText.Text =
                        $"BINA did not report a model id for \"{target.Name}\", so it cannot be targeted. " +
                        "Pick another model, or sync as a new model.";
                    return;
                }

                JoinsExistingLineage = true;
                LineageDocGuid = target.Source.DocGuid;
                TargetLineageId = target.Source.LineageId;
                TargetDesignId = target.Source.DesignId;
                TargetName = target.Source.Name;
                TargetFileHash = target.Source.FileHash;
                BaseVersion = target.Source.VersionNumber;
            }
            else
            {
                // No target: the server resolves by filename, so a same-named
                // model here is the chain this lands in whatever the radio said.
                // Send its GUID for the same lineageKey reason, and base the
                // version on it so the conflict check still applies.
                var clash = FindClash();
                if (clash != null)
                {
                    JoinsExistingLineage = true;
                    LineageDocGuid = clash.Source.DocGuid;
                    TargetLineageId = null;
                    TargetDesignId = clash.Source.DesignId;
                    TargetName = clash.Source.Name;
                    TargetFileHash = clash.Source.FileHash;
                    BaseVersion = clash.Source.VersionNumber ?? _head?.Version;
                }
                else
                {
                    JoinsExistingLineage = false;
                    LineageDocGuid = null;
                    TargetLineageId = null;
                    TargetDesignId = null;
                    TargetName = null;
                    TargetFileHash = _head?.FileHash;
                    BaseVersion = _head?.Version;
                }
            }

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
