using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        /// <summary>
        /// Runs the sync when the user confirms. Wired by SyncCommand; invoked
        /// on the UI thread inside ShowDialog's message pump, which is still the
        /// command's Revit API context — the caller may open transactions before
        /// handing the upload to a background task. While it runs the window
        /// shows progress, then the outcome, all without closing.
        /// </summary>
        public Func<System.Threading.Tasks.Task<SyncRunner.Result>> SyncWork { get; set; }

        /// <summary>Outcome of the last sync attempt; null when the user
        /// cancelled before ever clicking Sync.</summary>
        public SyncRunner.Result LastResult { get; private set; }

        /// <summary>What Prepare did to the document ("No unsaved changes — …"),
        /// echoed on the progress and success views.</summary>
        public string PrepareAction { get; set; }

        /// <summary>
        /// Hand this to the sync request so the upload's real steps and bytes
        /// drive the bar. Built on the UI thread, so reports land back on it.
        /// </summary>
        public IProgress<SyncProgress> Progress { get; }

        // Last step the run reported, so a failure can say where it stopped.
        private SyncProgress _lastProgress;

        /// <summary>True from Sync click until the runner returns. Blocks every
        /// way of closing the window — abandoning the upload would leave the
        /// command frozen behind a closed dialog.</summary>
        private bool _uploading;

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
        /// <summary>
        /// One tile in the model picker: either an existing model in the folder,
        /// or the "+ New model" tile that is always first. Picking a tile is the
        /// whole "new model or new version" choice.
        /// </summary>
        private sealed class ModelRow : INotifyPropertyChanged
        {
            public BimDesign Source { get; set; }
            public bool IsNewTile { get; set; }
            public string Name { get; set; }
            public string SubLine { get; set; }
            public string SizeText { get; set; }
            public Visibility NewTileVisibility => IsNewTile ? Visibility.Visible : Visibility.Collapsed;
            public Visibility ModelVisibility => IsNewTile ? Visibility.Collapsed : Visibility.Visible;

            private string _badgeText;
            public string BadgeText
            {
                get { return _badgeText; }
                set { _badgeText = value; OnChanged(nameof(BadgeText)); OnChanged(nameof(BadgeVisibility)); }
            }
            public Visibility BadgeVisibility =>
                string.IsNullOrEmpty(_badgeText) ? Visibility.Collapsed : Visibility.Visible;

            /// <summary>Called once, the first time the tile's picture is bound.</summary>
            public Action<ModelRow> ThumbnailRequested { get; set; }
            private bool _thumbnailRequested;
            private ImageSource _thumbnail;
            public ImageSource Thumbnail
            {
                get
                {
                    if (!_thumbnailRequested)
                    {
                        _thumbnailRequested = true;
                        ThumbnailRequested?.Invoke(this);
                    }
                    return _thumbnail;
                }
                set { _thumbnail = value; OnChanged(nameof(Thumbnail)); }
            }

            private string _placeholderLabel;
            public string PlaceholderLabel
            {
                get { return _placeholderLabel; }
                set { _placeholderLabel = value; OnChanged(nameof(PlaceholderLabel)); }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            private void OnChanged(string name) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>The "+ New model" tile, rebuilt with every list.</summary>
        private ModelRow _newTile = new ModelRow { IsNewTile = true, Name = "New model" };

        // Preview pictures for the picker, the same loader Download Model uses.
        private readonly ThumbnailLoader _thumbnails = new ThumbnailLoader();

        // Where this file already lives in BINA, as far as the add-in can tell:
        // the stamp's server record, else the local record from a download or
        // an earlier sync. Used to pre-select project, discipline, folder and
        // the model this sync should become the next version of.
        private readonly ModelRecord _known;
        private int? _preferFolderId;
        private string _preferLineageId;
        private int? _preferDesignId;

        /// <summary>Why the picker started on the model it did; shown under the summary.</summary>
        private string _matchNote;

        // Grid or list, kept for the session so the next Sync opens the same way.
        private static bool _useListView;

        public SyncOptionsWindow(
            SyncApiClient api,
            string fileName,
            string docGuid,
            int defaultProjectId,
            string defaultProjectName,
            string suggestedDiscipline,
            ModelRecord knownRecord = null)
        {
            InitializeComponent();
            _known = knownRecord;

            Progress = new UiProgress(this);
            _api = api;
            // A file downloaded from Cloud Docs is named "X-v2.rvt"; syncing it
            // back under that name would start a new lineage next to the one it
            // came from, so the suffix comes off the upload name up front.
            _fileName = StripVersionSuffix(fileName);
            _docGuid = docGuid;

            FileNameText.Text = _fileName;
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

            if (_useListView) ListViewButton.IsChecked = true;
            ApplyPickerView();

            Loaded += async (_, __) => await LoadProjectsAsync();
        }

        protected override void OnClosed(EventArgs e)
        {
            _thumbnails.Dispose();
            base.OnClosed(e);
        }

        private async System.Threading.Tasks.Task LoadProjectsAsync()
        {
            try
            {
                SetBusy(true, "Loading projects…");
                var projects = await _api.GetProjectsAsync();
                ProjectCombo.ItemsSource = projects;

                // The model's own place in BINA beats the last project used: a
                // drafter with three jobs open should not have to remember which
                // one this file belongs to.
                string source = await ApplyKnownPlacementAsync(projects);

                var current = projects.FirstOrDefault(p => p.Id == SelectedProjectId);
                ProjectCombo.SelectedItem = current ?? projects.FirstOrDefault();
                var chosen = ProjectCombo.SelectedItem as ProjectInfo;
                if (chosen != null)
                {
                    SelectedProjectId = chosen.Id;
                    SelectedProjectName = chosen.Name;
                }

                ShowProjectSource(current != null ? source : null);

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

        /// <summary>
        /// Where this file already lives in BINA, applied as the starting
        /// project, discipline, folder and model. The stamp's server record is
        /// trusted first (it survives the file being moved), then this machine's
        /// record of the download or last sync. Returns how the project was
        /// found, for the note under the project box, or null when neither knew.
        /// </summary>
        private async System.Threading.Tasks.Task<string> ApplyKnownPlacementAsync(IEnumerable<ProjectInfo> projects)
        {
            ResolvedDesign resolved = null;
            if (!string.IsNullOrEmpty(_docGuid))
            {
                try { resolved = await _api.ResolveDesignAsync(_docGuid); }
                catch { /* an unreachable lookup only costs the pre-selection */ }
            }

            int? project = null;
            string discipline = null;
            string source = null;

            if (resolved != null && resolved.ProjectId > 0)
            {
                project = resolved.ProjectId;
                discipline = resolved.DisciplineType;
                _preferLineageId = resolved.LineageId;
                _preferDesignId = resolved.DesignId;
                // Sync always lands in WIP: a Shared or Published row's folder is
                // not one this version can go into, so only its project counts.
                _preferFolderId = IsWip(resolved.DesignStatus) ? resolved.ParentId : null;
                source = "From this model's record in BINA";
            }
            else if (_known != null && _known.ProjectId > 0)
            {
                project = _known.ProjectId;
                discipline = _known.DisciplineType;
                _preferLineageId = _known.LineageId;
                _preferDesignId = _known.DesignId;
                _preferFolderId = _known.FolderId;
                source = string.Equals(_known.Source, "sync", StringComparison.OrdinalIgnoreCase)
                    ? "Where this file was last synced"
                    : "Where this file was downloaded from";
            }

            if (!project.HasValue)
            {
                // Nothing knows this file: look for a model with its name in the
                // last project used, across every discipline and folder.
                if (!projects.Any(p => p.Id == SelectedProjectId)) return null;
                return await MatchInProjectAsync(SelectedProjectId);
            }

            if (!projects.Any(p => p.Id == project.Value)) return null;

            SelectedProjectId = project.Value;
            SelectDiscipline(discipline);
            return source;
        }

        /// <summary>
        /// Finds this file's model anywhere in a project's WIP area by name (or
        /// document id) and points discipline and folder at it, so a file whose
        /// model sits under Structure is not offered the Architecture folder.
        /// Returns the note for under the project box, or null when no single
        /// model matched.
        /// </summary>
        private async System.Threading.Tasks.Task<string> MatchInProjectAsync(int projectId)
        {
            var rows = await _api.GetProjectDesignsAsync(projectId);
            if (rows.Count == 0) return null;

            var match = ModelMatch.Pick(
                rows.Select(d => new ModelMatch.Candidate
                {
                    Name = d.Name,
                    LineageId = d.LineageId,
                    DesignId = d.DesignId,
                    DocGuid = d.DocGuid,
                }).ToList(),
                new ModelMatch.Known { FileName = _fileName, DocGuid = _docGuid });
            if (match.Index < 0) return null;

            var found = rows[match.Index];
            _preferFolderId = found.FolderId;
            _preferLineageId = found.LineageId;
            _preferDesignId = found.DesignId;
            SelectDiscipline(found.DisciplineType);
            return "This project has a model with this file's name";
        }

        private static bool IsWip(string designStatus) =>
            string.Equals(designStatus, "WorkInProgress", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(designStatus, BimArea.Wip, StringComparison.OrdinalIgnoreCase);

        private void SelectDiscipline(string discipline)
        {
            if (string.IsNullOrWhiteSpace(discipline)) return;
            string apiValue = DisciplineTypes.ToApiValue(discipline);
            var match = (DisciplineCombo.ItemsSource as IEnumerable<DisciplineChoice>)?
                .FirstOrDefault(d => string.Equals(d.ApiValue, apiValue, StringComparison.OrdinalIgnoreCase));
            if (match != null) DisciplineCombo.SelectedItem = match;
        }

        /// <summary>
        /// The line under the project box: green when the project came from the
        /// model itself, grey when it is only the last one used, which is worth
        /// a second look before uploading into the wrong job.
        /// </summary>
        private void ShowProjectSource(string source, bool pickedByUser = false)
        {
            if (source == null && pickedByUser)
            {
                // The user chose this project; "last project you synced to"
                // would no longer be true, and there is nothing else to say.
                ProjectSourceText.Visibility = Visibility.Collapsed;
                return;
            }

            if (source != null)
            {
                ProjectSourceText.Text = "✓ " + source;
                ProjectSourceText.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
            }
            else
            {
                ProjectSourceText.Text = "The last project you synced to. Check it is the right one.";
                ProjectSourceText.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
            }
            ProjectSourceText.Visibility = Visibility.Visible;
        }

        private async void ProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var project = ProjectCombo.SelectedItem as ProjectInfo;
            if (project == null) return;

            SelectedProjectId = project.Id;
            SelectedProjectName = project.Name;

            // A different project has different folders and models: forget the
            // old target and look for this file's model again.
            _preferFolderId = null;
            _preferLineageId = null;
            _preferDesignId = null;

            string found = null;
            SetBusy(true, "Looking for this model in the project…");
            try
            {
                found = await MatchInProjectAsync(project.Id);
            }
            finally
            {
                SetBusy(false, null);
            }
            ShowProjectSource(found, pickedByUser: true);

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
                FolderCombo.SelectedItem =
                    folders.FirstOrDefault(f => _preferFolderId.HasValue && f.Id == _preferFolderId.Value)
                    ?? folders.FirstOrDefault();

                if (!folders.Any())
                {
                    // Uploading would fail server-side; stop here with an
                    // explanation instead of after a long upload.
                    string disciplineLabel = (DisciplineCombo.SelectedItem as DisciplineChoice)?.Label ?? "this discipline";
                    FolderHint.Text =
                        $"No work-in-progress folders under {disciplineLabel} in this project. " +
                        "Create one in BINA (BIM Models → " + disciplineLabel + " → WIP → New), then reopen this dialog.";
                    FolderHint.Visibility = Visibility.Visible;
                    ClearModels("No folder chosen.");
                    _canSync = false;
                    UpdateSummary();
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
            var folder = FolderCombo.SelectedItem as BimFolder;
            PickerTitle.Text = folder == null ? "Models" : "Models in " + folder.Name;
            if (folder == null)
            {
                ClearModels("No folder chosen.");
                UpdateSummary();
                return;
            }

            try
            {
                ClearModels("Loading models…");

                // Area is an assertion: folder ids are unique project-wide, so a
                // mismatch is a 404 rather than a cross-area read.
                var page = await _api.GetDesignsAsync(SelectedProjectId, folder.Id, BimArea.Wip);

                _allModels = (page.Designs ?? new List<BimDesign>()).Select(ToModelRow).ToList();
                _modelsTruncated = page.IsPartial;

                // The search box earns its space only once scrolling would be the
                // alternative; below that it is one more control to ignore.
                SearchArea.Visibility = _allModels.Count > 8 ? Visibility.Visible : Visibility.Collapsed;
                SearchBox.Text = "";

                ApplyModelFilter();
                ModelsMessage.Visibility = Visibility.Collapsed;
                PreselectMatch();
            }
            catch (BinaAccessDeniedException ex)
            {
                ClearModels(ex.Message);
            }
            catch (Exception ex)
            {
                ClearModels("Could not load the models in this folder. " + ex.Message);
            }
            finally
            {
                UpdateCollisionWarning();
                UpdateSummary();
            }
        }

        /// <summary>
        /// Starts the picker on the model this file most likely is: by lineage,
        /// design or document id first, then by name. One clear match is picked;
        /// several equally good name matches are only badged "Suggested".
        /// </summary>
        private void PreselectMatch()
        {
            _matchNote = null;
            var match = ModelMatch.Pick(
                _allModels.Select(m => new ModelMatch.Candidate
                {
                    Name = m.Source.Name,
                    LineageId = m.Source.LineageId,
                    DesignId = m.Source.DesignId,
                    DocGuid = m.Source.DocGuid,
                }).ToList(),
                new ModelMatch.Known
                {
                    FileName = _fileName,
                    LineageId = _preferLineageId,
                    DesignId = _preferDesignId,
                    DocGuid = _docGuid,
                });

            foreach (int i in match.Suggested) _allModels[i].BadgeText = "Suggested";

            if (match.Index < 0)
            {
                ModelsListBox.SelectedItem = _newTile;
                if (match.Suggested.Count > 1)
                    _matchNote = $"{match.Suggested.Count} models here have a similar name. Pick one to add a version, or keep \"New model\".";
                return;
            }

            var row = _allModels[match.Index];
            row.BadgeText = "Match";
            ModelsListBox.SelectedItem = row;
            ModelsListBox.ScrollIntoView(row);

            switch (match.Kind)
            {
                case ModelMatch.Kind.Lineage:
                case ModelMatch.Kind.Design:
                case ModelMatch.Kind.DocGuid:
                    _matchNote = "This file was downloaded from or synced into this model.";
                    break;
                case ModelMatch.Kind.ExactName:
                    _matchNote = "Matched by name: a model with this file's name is already in this folder.";
                    break;
                default:
                    _matchNote = $"Matched by a similar name (\"{row.Name}\"). Pick \"New model\" if this file is something else.";
                    break;
            }
        }

        private ModelRow ToModelRow(BimDesign d)
        {
            string when = d.UploadedAt.HasValue
                ? d.UploadedAt.Value.ToLocalTime().ToString("d MMM yyyy")
                : null;

            var parts = new List<string>();
            if (d.VersionNumber.HasValue) parts.Add("V" + d.VersionNumber.Value);
            if (d.VersionCount.HasValue && d.VersionCount.Value > 1) parts.Add(d.VersionCount.Value + " versions");
            if (when != null) parts.Add(when);
            if (!string.IsNullOrWhiteSpace(d.UploaderName)) parts.Add(d.UploaderName);

            return new ModelRow
            {
                Source = d,
                Name = string.IsNullOrWhiteSpace(d.Name) ? "(unnamed model)" : d.Name,
                SubLine = string.Join(" · ", parts),
                SizeText = FormatSize(d.FileSize),
                PlaceholderLabel = string.IsNullOrWhiteSpace(d.ThumbnailUrl)
                    ? DesignFileRules.DescribeMissingThumbnail(d.OdaViewerState)
                    : "Loading…",
                ThumbnailRequested = RequestThumbnail,
            };
        }

        /// <summary>
        /// Fetches one tile's picture. Runs from the Thumbnail getter, so it
        /// yields first: setting the property while the binding is still reading
        /// it would re-enter the binding engine.
        /// </summary>
        private async void RequestThumbnail(ModelRow row)
        {
            var d = row.Source;
            if (d == null || string.IsNullOrWhiteSpace(d.ThumbnailUrl)) return;

            try
            {
                await System.Windows.Threading.Dispatcher.Yield();
                // 360 px for a 180 px card: sharp on a 200% display.
                var image = await _thumbnails.LoadAsync(d.DesignId, d.ThumbnailUrl, 360);
                if (image != null) row.Thumbnail = image;
                else row.PlaceholderLabel = DesignFileRules.DescribeMissingThumbnail(d.OdaViewerState);
            }
            catch (Exception)
            {
                row.PlaceholderLabel = DesignFileRules.DescribeMissingThumbnail(d.OdaViewerState);
            }
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

            // "+ New model" is always the first tile, search or not: starting a
            // new model must never depend on what is typed in the search box.
            var selected = ModelsListBox.SelectedItem;
            var items = new List<ModelRow> { _newTile };
            items.AddRange(shown);
            ModelsListBox.ItemsSource = items;
            ModelsListBox.SelectedItem = selected is ModelRow kept && items.Contains(kept) ? kept : _newTile;

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
            _matchNote = null;
            _newTile = new ModelRow { IsNewTile = true, Name = "New model" };
            ModelsListBox.ItemsSource = new List<ModelRow> { _newTile };
            ModelsListBox.SelectedItem = _newTile;
            SearchArea.Visibility = Visibility.Collapsed;
            ModelsCount.Text = "";
            ModelsMessage.Text = message;
            ModelsMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ModelsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Never leave the picker without a choice: an empty selection would
            // make "new model or new version" ambiguous.
            if (ModelsListBox.SelectedItem == null && ModelsListBox.Items.Count > 0)
            {
                ModelsListBox.SelectedItem = _newTile;
                return;
            }

            // A pick by the user replaces the explanation of the automatic one.
            if (!_loading && e.AddedItems.Count > 0 && e.RemovedItems.Count > 0) _matchNote = null;

            UpdateNameMismatchWarning();
            UpdateCollisionWarning();
            UpdateSummary();
        }

        /// <summary>True when an existing model is picked rather than "+ New model".</summary>
        private bool IsExistingSelected =>
            ModelsListBox.SelectedItem is ModelRow row && !row.IsNewTile;

        private ModelRow SelectedModel => IsExistingSelected ? (ModelRow)ModelsListBox.SelectedItem : null;

        private void ViewToggle_Checked(object sender, RoutedEventArgs e)
        {
            // Fires during InitializeComponent, before the list exists.
            if (ModelsListBox == null) return;
            _useListView = ListViewButton.IsChecked == true;
            ApplyPickerView();
        }

        private void ApplyPickerView()
        {
            ModelsListBox.ItemsPanel = (ItemsPanelTemplate)FindResource(_useListView ? "ListItemsPanel" : "GridItemsPanel");
            ModelsListBox.ItemTemplate = (DataTemplate)FindResource(_useListView ? "ListItemTemplate" : "GridItemTemplate");
        }

        /// <summary>
        /// The left pane's "Syncing as" card and the button text: what clicking
        /// Sync is about to do, in one line, so the picker never has to be
        /// re-read to find out.
        /// </summary>
        private void UpdateSummary()
        {
            if (SummaryTitle == null) return;

            var folder = FolderCombo.SelectedItem as BimFolder;
            var target = SelectedModel;
            var clash = target == null ? FindClash() : null;

            if (target != null)
            {
                int next = (target.Source.VersionNumber ?? 0) + 1;
                SummaryTitle.Text = $"Version {next} of \"{target.Name}\"";
                SyncButton.Content = $"Sync as v{next}";
            }
            else if (clash != null && !IsOwnChain(clash))
            {
                int next = (clash.Source.VersionNumber ?? 0) + 1;
                SummaryTitle.Text = $"Version {next} of \"{clash.Name}\" (same name)";
                SyncButton.Content = $"Sync as v{next}";
            }
            else
            {
                SummaryTitle.Text = folder == null
                    ? "A new model"
                    : $"A new model in {folder.Name}";
                SyncButton.Content = "Sync as new model";
            }

            SummaryNote.Text = _matchNote ?? "";
            SummaryNote.Visibility = string.IsNullOrEmpty(_matchNote) ? Visibility.Collapsed : Visibility.Visible;
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
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[-_]v\d+$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            return s.Length == 0 ? stem : s;
        }

        /// <summary>"X-v2.rvt" → "X.rvt": the version suffix a Cloud Docs
        /// download carries. Applied to every sync's upload name, not just the
        /// Edit suggestion, so a downloaded model syncs back into its own
        /// lineage instead of starting an "X-v2" one beside it.</summary>
        private static string StripVersionSuffix(string fileName)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            string ext = System.IO.Path.GetExtension(fileName);
            string s = System.Text.RegularExpressions.Regex.Replace(stem, @"[-_]v\d+$", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            return s.Length == 0 ? fileName : s + ext;
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
            UpdateCollisionWarning();
            UpdateNameMismatchWarning();
            UpdateSummary();
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

            var row = SelectedModel;
            bool show = row != null && !SameName(row.Source.Name, _fileName);

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
            bool show = clash != null && !IsExistingSelected && !IsOwnChain(clash);

            if (show)
            {
                CollisionText.Text =
                    $"\"{clash.Name}\" already exists in this folder. Syncing will add " +
                    $"v{(clash.Source.VersionNumber ?? 0) + 1} to that model, not create a new one. " +
                    "Pick that model on the right to confirm, or rename your file first.";
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
            ModelsListBox.IsEnabled = !busy;
            SearchBox.IsEnabled = !busy;
            SyncButton.IsEnabled = !busy && _canSync;
            if (status != null) StatusText.Text = status;
            else if (!busy && StatusText.Text.EndsWith("…")) StatusText.Text = "";
        }

        private async void SyncButton_Click(object sender, RoutedEventArgs e)
        {
            // The button stays IsDefault after the form collapses, so Enter on
            // the outcome view would otherwise start an invisible second sync.
            if (_uploading || OutcomeRoot.Visibility == Visibility.Visible) return;

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

            bool existing = IsExistingSelected;
            ModelRow target = SelectedModel;

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

            if (SyncWork == null)
            {
                // No runner wired (older callers, tests): close-and-let-the-
                // command-sync, exactly the pre-single-modal behavior.
                DialogResult = true;
                Close();
                return;
            }

            ShowUploadingState(folder.Name);

            SyncRunner.Result result;
            try
            {
                result = await SyncWork();
            }
            catch (Exception ex)
            {
                var inner = (ex as AggregateException)?.InnerException ?? ex;
                result = new SyncRunner.Result { Succeeded = false, Message = inner.Message };
            }

            _uploading = false;
            LastResult = result;
            ShowOutcomeState(result);
        }

        // ------------------------------------------------- progress & outcome

        // Explicit Dispatcher, not Progress<T>: that one captures the sync context
        // when built, and this window is built before ShowDialog starts the pump,
        // where Current can be null — reports would then touch the UI from a worker.
        private sealed class UiProgress : IProgress<SyncProgress>
        {
            private readonly SyncOptionsWindow _w;
            public UiProgress(SyncOptionsWindow w) { _w = w; }
            public void Report(SyncProgress p) => _w.Dispatcher.BeginInvoke(new Action(() => _w.OnSyncProgress(p)));
        }

        private void OnSyncProgress(SyncProgress p)
        {
            // A report posted just before the run ended can arrive after the
            // outcome is up; it must not repaint the bar over it.
            if (!_uploading) return;
            _lastProgress = p;
            OutcomeProgress.IsIndeterminate = false;
            OutcomeProgress.Value = p.Percent;
            OutcomeDetail.Text = p.Text + "\n" + KeepOpenHint();
        }

        private string KeepOpenHint()
            => (string.IsNullOrEmpty(PrepareAction) ? "" : PrepareAction + " ")
               + "Keep Revit open — a large model can take a while.";

        private void ShowUploadingState(string folderName)
        {
            _uploading = true;
            _lastProgress = null;
            OutcomeProgress.IsIndeterminate = true;
            OutcomeProgress.Value = 0;
            FormRoot.Visibility = Visibility.Collapsed;
            OutcomeRoot.Visibility = Visibility.Visible;

            OutcomeBadge.Visibility = Visibility.Collapsed;
            OutcomeProgress.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Collapsed;

            OutcomeTitle.Text = "Syncing to BINA";
            OutcomeMessage.Text = $"Uploading \"{_fileName}\" to {SelectedProjectName}"
                + (string.IsNullOrEmpty(folderName) ? "…" : $" → {folderName}…");
            OutcomeDetail.Text = KeepOpenHint();
        }

        private void ShowOutcomeState(SyncRunner.Result result)
        {
            OutcomeProgress.Visibility = Visibility.Collapsed;
            OutcomeBadge.Visibility = Visibility.Visible;
            DoneButton.Visibility = Visibility.Visible;

            if (result.Conflict != null)
            {
                SetBadge("!", "#B54708", "#FFF7ED");
                OutcomeTitle.Text = "Someone else synced first";
                string who = result.Conflict.UploadedAt.HasValue
                    ? result.Conflict.UploadedAt.Value.ToLocalTime().ToString("d MMM HH:mm")
                    : "recently";
                OutcomeMessage.Text = $"BINA now has v{result.Conflict.Version}, uploaded {who}.";
                OutcomeDetail.Text =
                    "Download the latest version before syncing again, so their changes are not lost.";
            }
            else if (!result.Succeeded)
            {
                SetBadge("✕", "#B91C1C", "#FEF2F2");
                OutcomeTitle.Text = "Sync failed";
                OutcomeMessage.Text = result.Message ?? "Unknown error.";
                // Say where it stopped: "failed" alone leaves a 2 GB upload guessing.
                OutcomeDetail.Text = (_lastProgress == null ? "" : "Stopped at: " + _lastProgress.Text + "\n")
                    + "Nothing was published. Go back to try again.";
                BackButton.Visibility = Visibility.Visible;
            }
            else if (result.Unchanged)
            {
                SetBadge("✓", "#16A34A", "#ECFDF5");
                OutcomeTitle.Text = "Already up to date";
                OutcomeMessage.Text =
                    $"This model is identical to v{result.Version} already in BINA, so no new version was created.";
                OutcomeDetail.Text = "";
            }
            else
            {
                SetBadge("✓", "#16A34A", "#ECFDF5");
                OutcomeTitle.Text = "Synced";
                // Name the model whose history this joined — with the chain
                // picked by hand, "is now v8" alone does not say v8 of what.
                string where = string.IsNullOrEmpty(result.TargetName)
                               || SameName(result.TargetName, result.FileName)
                    ? ""
                    : $" of \"{result.TargetName}\"";
                OutcomeMessage.Text = $"{result.FileName} is now v{result.Version}{where} in BINA.";
                OutcomeDetail.Text = PrepareAction ?? "";
            }

            // Enter should close, not re-fire the (hidden, IsDefault) Sync button.
            DoneButton.Focus();
        }

        private void SetBadge(string glyph, string foreground, string background)
        {
            var conv = new System.Windows.Media.BrushConverter();
            OutcomeGlyph.Text = glyph;
            OutcomeGlyph.Foreground = (System.Windows.Media.Brush)conv.ConvertFromString(foreground);
            OutcomeBadge.Background = (System.Windows.Media.Brush)conv.ConvertFromString(background);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            OutcomeRoot.Visibility = Visibility.Collapsed;
            FormRoot.Visibility = Visibility.Visible;
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Mid-upload there is no way to abandon the transfer from here —
            // closing would leave SyncCommand blocked behind a dead window. The
            // title-bar X and Esc both land here; swallow them until the
            // outcome is on screen.
            if (_uploading) { e.Cancel = true; return; }
            base.OnClosing(e);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_uploading) return;
            DialogResult = false;
            Close();
        }
    }
}
