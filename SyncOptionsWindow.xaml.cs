using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using RevitWebAppSync.Services;

namespace RevitWebAppSync
{
    /// <summary>
    /// Everything the user confirms before a single byte is uploaded: project,
    /// discipline, folder and an optional note (ClickUp 86d3x42mz).
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
    /// It also shows what BINA already holds for this model, so a user about to
    /// overwrite someone else's work sees that before they commit to it.
    /// </summary>
    public partial class SyncOptionsWindow : Window
    {
        private readonly SyncApiClient _api;
        private readonly string _fileName;
        private readonly string _docGuid;
        private bool _loading;

        public int SelectedProjectId { get; private set; }
        public string SelectedProjectName { get; private set; }
        public int? SelectedFolderId { get; private set; }
        public string SelectedDiscipline { get; private set; }
        public string Comment { get; private set; }
        /// <summary>Version this machine is basing its sync on; null when never synced.</summary>
        public int? BaseVersion { get; private set; }

        /// <summary>
        /// The chain this sync joins, or null to let the filename decide as before.
        ///
        /// This is what makes a model called anything at all the next version of an
        /// existing file: without it the server matches on the filename, so a rename
        /// — or a version downloaded as "model-v7.rvt" — starts a second history.
        /// </summary>
        public int? TargetDesignId { get; private set; }

        /// <summary>A row in the "Sync into" list. A null DesignId is "start a new file".</summary>
        private sealed class TargetChoice
        {
            public int? DesignId { get; set; }
            public string Label { get; set; }
            public string Name { get; set; }
        }

        private sealed class DisciplineChoice
        {
            public string ApiValue { get; set; }
            public string Label { get; set; }
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
                SyncButton.IsEnabled = false;
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

        private async void ProjectCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var project = ProjectCombo.SelectedItem as ProjectInfo;
            if (project == null) return;

            SelectedProjectId = project.Id;
            SelectedProjectName = project.Name;

            await LoadFoldersAndHeadAsync();
        }

        /// <summary>
        /// Folders belong to a project, and the head belongs to a folder, so both
        /// are refetched whenever the project changes.
        /// </summary>
        private async System.Threading.Tasks.Task LoadFoldersAndHeadAsync()
        {
            try
            {
                SetBusy(true, "Loading folders…");
                FolderHint.Visibility = Visibility.Collapsed;
                HeadPanel.Visibility = Visibility.Collapsed;

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
                    SyncButton.IsEnabled = false;
                    return;
                }

                SyncButton.IsEnabled = true;

                await LoadTargetsAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Could not load folders: {ex.Message}";
                SyncButton.IsEnabled = false;
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private async void DisciplineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            await LoadFoldersAndHeadAsync();
        }

        private async void FolderCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            // Both the target list and the head belong to a folder, so both follow it.
            await LoadTargetsAsync();
        }

        private async void TargetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            await RefreshHeadAsync();
        }

        /// <summary>
        /// The files already in the chosen folder, plus "new file".
        ///
        /// Defaulting matters more than the list: a drafter who never opens this
        /// control must still land where they always did, so the default is the file
        /// matching this document's name, and only "new file" when there is none.
        /// </summary>
        private async System.Threading.Tasks.Task LoadTargetsAsync()
        {
            var folder = FolderCombo.SelectedItem as BimFolder;
            var choices = new List<TargetChoice>
            {
                new TargetChoice { DesignId = null, Label = "New file — " + _fileName, Name = _fileName }
            };

            if (folder != null)
            {
                try
                {
                    var page = await _api.GetDesignsAsync(SelectedProjectId, folder.Id, BimArea.Wip);
                    var designs = page?.Designs ?? new List<BimDesign>();

                    foreach (var design in designs)
                    {
                        if (design == null || string.IsNullOrEmpty(design.Name)) continue;
                        choices.Add(new TargetChoice
                        {
                            DesignId = design.DesignId,
                            Label = design.VersionNumber.HasValue
                                ? design.Name + "  (v" + design.VersionNumber.Value + ")"
                                : design.Name,
                            Name = design.Name
                        });
                    }
                }
                catch
                {
                    // A list we cannot read must not block a sync: "new file" alone still
                    // reproduces the behaviour every earlier version of this plugin had.
                }
            }

            _loading = true;
            try
            {
                TargetCombo.ItemsSource = choices;

                // Same file name as this document => same file, which is what the
                // server would have concluded on its own.
                var match = choices.FirstOrDefault(choice =>
                    choice.DesignId.HasValue &&
                    string.Equals(choice.Name, _fileName, StringComparison.OrdinalIgnoreCase));

                TargetCombo.SelectedItem = match ?? choices[0];
            }
            finally
            {
                _loading = false;
            }

            await RefreshHeadAsync();
        }

        private async System.Threading.Tasks.Task RefreshHeadAsync()
        {
            // Cleared BEFORE the call, not just after a failure. BaseVersion is the
            // number the server compares the head against on commit, and it belongs
            // to one folder: carrying the previous folder's number into a new one
            // makes the server answer "someone else synced first" when nobody did.
            // Until this call succeeds we have no basis for that claim.
            BaseVersion = null;

            var target = TargetCombo.SelectedItem as TargetChoice;
            TargetDesignId = target?.DesignId;

            // Adding to an existing file under a different local name is the case the
            // filename can never express, so say so plainly before they commit.
            if (target != null && target.DesignId.HasValue &&
                !string.Equals(target.Name, _fileName, StringComparison.OrdinalIgnoreCase))
            {
                TargetHint.Text =
                    "This model will be added to \"" + target.Name +
                    "\" as its next version. BINA keeps the file under the name you upload.";
            }
            else if (target != null && !target.DesignId.HasValue)
            {
                TargetHint.Text = "Starts a new file and its own version history.";
            }
            else
            {
                TargetHint.Text = string.Empty;
            }

            try
            {
                var head = await _api.GetHeadAsync(
                    SelectedProjectId, _docGuid, _fileName,
                    (FolderCombo.SelectedItem as BimFolder)?.Id,
                    TargetDesignId);
                BaseVersion = head?.Version;
                ShowHead(head);
            }
            catch
            {
                // A head we cannot read is not worth blocking the sync over, and
                // the server re-checks on commit regardless — but it checks against
                // BaseVersion, so that has to stay null rather than keep a stale
                // number. Null means "do not assert a base", which the server
                // honours; the version is still computed server-side and the
                // uniqueness indexes still refuse a genuine double-write.
                HeadPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowHead(SyncHead head)
        {
            if (head == null)
            {
                HeadTitle.Text = "First sync";
                HeadDetail.Text = "BINA has no version of this model yet — this will be v1.";
            }
            else
            {
                HeadTitle.Text = $"BINA currently has v{head.Version}";
                string when = head.UploadedAt.HasValue
                    ? head.UploadedAt.Value.ToLocalTime().ToString("d MMM yyyy HH:mm")
                    : "an earlier date";
                HeadDetail.Text = $"\"{head.Name}\" uploaded {when}. Syncing will create v{(head.Version ?? 0) + 1}.";
            }

            HeadPanel.Visibility = Visibility.Visible;
        }

        private void SetBusy(bool busy, string status)
        {
            _loading = busy;
            ProjectCombo.IsEnabled = !busy;
            FolderCombo.IsEnabled = !busy;
            DisciplineCombo.IsEnabled = !busy;
            SyncButton.IsEnabled = !busy;
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

            SelectedFolderId = folder.Id;
            SelectedDiscipline = discipline.ApiValue;   // already a backend value
            Comment = string.IsNullOrWhiteSpace(CommentBox.Text) ? null : CommentBox.Text.Trim();

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
