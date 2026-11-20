using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Handlers;
using RevitWebAppSync.Services;
using RevitWebAppSync.UI.Jkr;
using RevitWebAppSync.UI.Jkr.Controls;
using RevitWebAppSync.UI.Jkr.Modals;
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI
{
    public partial class JkrComplianceDashboardPanel : UserControl
    {
        // Path of the document the current issues were scanned from. Audit decisions
        // (ignore / fix / manual) are saved against this file, so they stay with the
        // model the issues came from even if the user switches documents afterwards.
        // Read inside the scan's Revit job; WPF handlers never touch the document.
        private string _scannedDocPath = "";
        private readonly PanelVm _vm = new PanelVm();
        private readonly JkrComplianceService _jkrService = new JkrComplianceService();
        private Storyboard _rescanSpin;
        private Jkr.Modals.ExportWindow _exportWindow;

        // Guards against rapid double-click between Fix All and per-issue Apply Fix.
        // Both paths reassign JkrRenameHandler.OnCompleted; if the second click lands
        // before the first ExternalEvent fires, the first batch's completion callback
        // is silently overwritten and the progress bar / button-disabled state desyncs.
        private bool _fixInFlight;

        // Snapshot of the last scan request — AI Fix reuses its project/model
        // metadata and element data so the backend fixer sees exactly what the
        // failing checks were computed from.
        private JkrComplianceRequestV2 _lastScanRequest;

        // LOD level is selectable via the LOD ComboBox in the hero header.
        // Default is 300; user can change to 100/200/300/400/500 before scanning.
        private int SelectedLodLevel => _vm.SelectedLodLevel;

        // Discipline scope: auto-detected from the model on first scan, overridable
        // by the user via DisciplineCombo. Once the user picks, stop auto-syncing.
        private bool _disciplineUserSet;
        private bool _syncingDiscipline;

        // Set when a scan comes back 401 (JkrComplianceService.LoginRequiredMessage).
        // RenderAll() checks this BEFORE the generic empty-state branch — without it, a
        // 401 leaves _vm.Filtered at its prior count (often 0 on first scan) and
        // RenderAll's default empty copy is "All clear — no open issues.", which would
        // tell a logged-out drafter their model passed compliance. Cleared on the next
        // successful scan so a later login+rescan removes the banner.
        private bool _authRequired;

        /// <summary>Raised when a scan answers 401 (true) or succeeds again (false);
        /// the pane shell shows its login-required state.</summary>
        public event Action<bool> AuthRequiredChanged;

        private void SetAuthRequired(bool value)
        {
            if (_authRequired == value) return;
            _authRequired = value;
            AuthRequiredChanged?.Invoke(value);
        }

        /// <summary>The Binazone shell has its own title bar (step 7).</summary>
        public void HideOwnTitleBar()
        {
            OwnTitleBar.Visibility = System.Windows.Visibility.Collapsed;
            if (OwnTitleBar.Parent is System.Windows.Controls.Grid g && g.RowDefinitions.Count > 0)
                g.RowDefinitions[0].Height = new GridLength(0);
        }

        /// <summary>Re-scan from outside the panel (the shell's "Try again").</summary>
        public void Rescan() => _ = RunScanAsync();

        /// <summary>Raised after Reset cleared the results: the shell shows Start.</summary>
        public event Action ResetDone;

        // ── Results screen (step 10) ──────────────────────────────────────────
        // The Binazone Results screen is a view over this panel: the panel still
        // owns the issues, their statuses, the confirm dialogs, the Revit writes and
        // the audit file. These members let the screen read that state and ask for
        // the same actions the old buttons did.

        /// <summary>Raised after every render, i.e. after any state change.</summary>
        public event Action StateChanged;

        /// <summary>"Applying n changes…" while a batch is written; null when done.</summary>
        public event Action<string> BusyChanged;

        /// <summary>A toast appeared (message) or went (null).</summary>
        public event Action<string> ToastChanged;

        public IReadOnlyList<IssueVm> CurrentIssues => _vm.Issues.ToList();
        public BinaVibe.Mcp.Tools.Audit.Bim010Summary CurrentSummary => _vm.Summary;

        /// <summary>The last run against the JKR Model Audit spec (null before a run).</summary>
        public Jkr.Panel.Core.SpecReport CurrentSpecReport => _vm.Spec;

        private IssueVm FindIssue(string id) => _vm.Issues.FirstOrDefault(i => i.Id == id);

        /// <summary>The issue with this id, or null (read by the detail view).</summary>
        public IssueVm Issue(string id) => FindIssue(id);

        /// <summary>How to fix an issue by hand (the detail's steps): never empty for
        /// a known issue, so the block is never blank.</summary>
        public IReadOnlyList<string> HowToFixSteps(string id) =>
            (IReadOnlyList<string>)FindIssue(id)?.ManualFixSteps ?? Array.Empty<string>();

        /// <summary>Locate in 3D from the detail view.</summary>
        public Task LocateIssueAsync(string id)
        {
            var issue = FindIssue(id);
            return issue == null ? Task.CompletedTask : LocateInRevitAsync(issue);
        }

        /// <summary>Open the spec PDF the issue cites.</summary>
        public void OpenIssueSource(string id)
        {
            var doc = FindIssue(id)?.Spec?.Doc;
            if (!string.IsNullOrEmpty(doc)) RevitWebAppSync.Services.JkrSpecPdfBootstrap.Open(doc);
        }

        /// <summary>Fix these issues: one goes through the single auto-fix, several
        /// through the batch (confirm dialog, one "JKR Fix all" undo step, re-check).</summary>
        public async Task FixIssuesAsync(IReadOnlyList<string> ids)
        {
            var issues = (ids ?? Array.Empty<string>()).Select(FindIssue)
                .Where(i => i != null && !i.NeedsPerson && i.AutoFixable && !string.IsNullOrEmpty(i.FixAction) && !IsEmptyRename(i))
                .ToList();
            if (issues.Count == 0) { _vm.ShowToast(Jkr.Panel.Core.PaneText.NoAutoFixHere()); return; }
            if (_fixInFlight) { _vm.ShowToast(Jkr.Panel.Core.PaneText.FixRunning()); return; }
            if (App.JkrRenameHandler == null || App.JkrRenameEvent == null)
            {
                TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.AutoFixUnavailable());
                return;
            }
            if (issues.Count == 1)
            {
                await ApplyAutoFixAsync(issues[0], advance: false);
                return;
            }
            var fixable = issues.OrderBy(i => i.FixPriority).ToList();
            if (!await ConfirmFixesAsync(fixable)) return;
            try
            {
                await ApplyFixBatch(fixable);
            }
            catch (Exception ex)
            {
                _fixInFlight = false;
                HideApplying();
                TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.FixAllError(ex.Message));
            }
        }

        /// <summary>Ignore these issues: one plain toast, saved to the audit file.</summary>
        public void IgnoreIssues(IReadOnlyList<string> ids)
        {
            var issues = (ids ?? Array.Empty<string>()).Select(FindIssue)
                .Where(i => i != null && i.IsAiOpen).ToList();
            if (issues.Count == 0) return;
            _vm.ApplyDecisions(issues, IssueStatus.Ignored,
                Jkr.Panel.Core.PaneText.IssuesIgnored(issues.Count));
            foreach (var issue in issues)
            {
                try { JkrAuditStore.Save(_scannedDocPath, issue); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BINA] audit save failed: {ex.Message}"); }
            }
            RenderAll();
        }

        /// <summary>"Mark as Manual" (option b) on an open AI issue, or back to Open with
        /// <paramref name="manual"/> false. The issue stays open and still counts as a fail;
        /// it is left out of Fix all and the AI fixer. Saved to the audit file.</summary>
        public void SetManual(string id, bool manual)
        {
            if (!manual) { Reopen(id); return; }
            var issue = FindIssue(id);
            if (issue == null || !issue.IsAiOpen || !issue.CanMarkManual) return;
            _vm.ApplyDecisions(new[] { issue }, IssueStatus.ManualFixNeeded,
                Jkr.Panel.Core.PaneText.Decision("ManualFixNeeded", Jkr.Panel.Core.ResultsAdapter.TitleIn(issue)));
            SaveAudit(issue);
            RenderAll();
        }

        /// <summary>Reopen an ignored, approved or manually marked issue (status Open; the
        /// audit store drops its entry).</summary>
        public void Reopen(string id)
        {
            var issue = FindIssue(id);
            if (issue == null || issue.NeedsPerson || issue.Status == IssueStatus.Open || issue.Status == IssueStatus.Fixed) return;
            _vm.ApplyDecisions(new[] { issue }, IssueStatus.Open,
                Jkr.Panel.Core.PaneText.Decision("Open", Jkr.Panel.Core.ResultsAdapter.TitleIn(issue)));
            SaveAudit(issue);
            RenderAll();
        }

        private void SaveAudit(IssueVm issue)
        {
            try { JkrAuditStore.Save(_scannedDocPath, issue); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BINA] audit save failed: {ex.Message}"); }
        }

        /// <summary>"Mark as reviewed" on an item that needs a person (or back to not
        /// reviewed). Saved to the audit file so it survives a re-scan and Revit
        /// restarts. Not a verdict: the item stays in Manual and out of the %.</summary>
        public void SetReviewed(string id, bool reviewed)
        {
            var issue = FindIssue(id);
            if (issue == null || !issue.NeedsPerson || issue.Reviewed == reviewed) return;
            issue.Reviewed = reviewed;
            try { JkrAuditStore.Save(_scannedDocPath, issue); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BINA] audit save failed: {ex.Message}"); }
            RenderAll();
        }


        /// <summary>Reset from the Results screen (back to Start; writes nothing).</summary>
        public void ResetToStart() => Reset_Click(this, new RoutedEventArgs());

        /// <summary>"Export issues (Excel/CSV)": the issue-list export window (the
        /// secondary export next to the Borang PDF).</summary>
        /// <param name="owner">The window it opens from (the Borang preview): the dialog is
        /// owned by it, so closing the dialog returns there (live F13).</param>
        /// <returns>What happened ("Issues exported: …", or why not); null when cancelled.</returns>
        public string OpenExport(Jkr.Export.IssueExportFormat format = Jkr.Export.IssueExportFormat.Excel, Window owner = null)
        {
            _exportFormat = format;
            _exportOwner = owner;
            _exportOutcome = null;
            _vm.ExportOpen = true;
            OpenExportWindow();          // modal: returns once the dialog is closed
            return _exportOutcome;
        }

        private Jkr.Export.IssueExportFormat _exportFormat;
        private Window _exportOwner;
        private string _exportOutcome;

        /// <summary>How many issues the export writes with the dialog's default options
        /// (what the Borang preview's footer shows).</summary>
        public int IssueExportCount => Jkr.Export.IssueExportRows.DefaultCount(_vm.Issues);

        /// <summary>The Borang BIM010 for the last run: every row marked from the run's
        /// BIM010 summary, remarks from the issues, header from the model. Null before
        /// a scan.</summary>
        public async Task<Jkr.Export.BorangDoc> BuildBorangAsync(string lod, string disc)
        {
            var summary = _vm.Summary;
            if (summary == null) return null;
            var path = _scannedDocPath ?? "";
            var file = System.IO.Path.GetFileName(path);
            string projectName = null;
            try
            {
                projectName = await Revit.InvokeAsync(app => app.ActiveUIDocument?.Document?.ProjectInformation?.Name);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BINA] project name: {ex.Message}"); }
            if (string.IsNullOrEmpty(file)) file = Jkr.Panel.Core.ActiveModelSource.Current?.Title ?? "";
            return Jkr.Export.BorangDoc.Build(summary, _vm.Issues.ToList(), new Jkr.Export.BorangHeader
            {
                Project = Jkr.Export.BorangDoc.ProjectName(projectName, file),
                Model = Jkr.Export.BorangDoc.ModelLine(disc, lod),
                File = file,
                Date = Jkr.Export.BorangDoc.DateOf(DateTime.Now),
            });
        }

        /// <summary>Folder of the audited model (where the PDF is offered first).</summary>
        public string ModelFolder
        {
            get { try { return System.IO.Path.GetDirectoryName(_scannedDocPath ?? "") ?? ""; } catch { return ""; } }
        }

        /// <summary>A short notice in the pane (toast).</summary>
        public void Notify(string message) => _vm.ShowToast(message);

        /// <summary>A user-started scan began (Run audit, Re-scan, Try again) with this
        /// LOD and discipline: the shell shows the Running screen. The automatic
        /// re-check after Fix all does not raise it.</summary>
        public event Action<int, string> ScanStarted;

        /// <summary>That scan ended: the BIM010 summary, or why it failed (null = ok).</summary>
        public event Action<BinaVibe.Mcp.Tools.Audit.Bim010Summary, string> ScanCompleted;

        /// <summary>The Binazone shell shows scan progress and errors on its Running
        /// screen, so the panel does not also raise error dialogs for them.</summary>
        public bool HostShowsScanProgress { get; set; }

        private string _scanError;

        /// <summary>Run audit from the Start screen (step 8): use the LOD and discipline
        /// chosen there, then scan. The discipline counts as the user's choice, so the
        /// scan does not switch it to the model's detected one.</summary>
        public void RunFromStart(int lod, string discipline)
        {
            if (_vm.Scanning) return;
            if (_vm.LodLevels.Contains(lod))
            {
                _vm.SelectedLodLevel = lod;
                LodCombo.SelectedItem = lod;
            }
            var d = _vm.Disciplines.FirstOrDefault(x => x.Code == discipline) ?? _vm.Disciplines[0];
            _syncingDiscipline = true;
            DisciplineCombo.SelectedItem = d;
            _syncingDiscipline = false;
            _vm.SelectedDiscipline = d.Code;
            _disciplineUserSet = true;
            _ = RunScanAsync();
        }

        /// <summary>TEST ONLY: BINA_DEV_UNLOCK / BINA_JKR_FIXTURE (Services\JkrDevMode.cs).
        /// Off unless those user variables are set.</summary>
        private static JkrDevMode DevMode => JkrDevMode.Current;

        public JkrComplianceDashboardPanel()
        {
            JkrTheme.EnsureLoaded();
            InitializeComponent();

            if (DevMode.Unlocked)
            {
                TestModeStrip.Visibility = System.Windows.Visibility.Visible;
                TestModeText.Text = DevMode.UsesFixture
                    ? $"TEST MODE · fixture: {System.IO.Path.GetFileName(DevMode.FixturePath)} · no backend, no login"
                    : "TEST MODE · ribbon unlocked · scans use the backend (set BINA_JKR_FIXTURE to use a fixture)";
                TestModeText.ToolTip = DevMode.FixturePath;
            }

            DataContext = _vm;
            CategoriesItems.ItemsSource = _vm.Categories;
            IssuesItems.ItemsSource = _vm.Filtered;

            // Wire LodCombo in code, not XAML — matches the pattern used by Categories/Issues
            // above and avoids the binding-order race that blanked the dropdown when bindings
            // evaluated before DataContext propagated.
            LodCombo.ItemsSource = _vm.LodLevels;
            LodCombo.SelectedItem = _vm.SelectedLodLevel;
            LodCombo.SelectionChanged += (_, __) =>
            {
                if (LodCombo.SelectedItem is int v) _vm.SelectedLodLevel = v;
            };

            DisciplineCombo.ItemsSource = _vm.Disciplines;
            DisciplineCombo.SelectedItem =
                _vm.Disciplines.FirstOrDefault(d => d.Code == _vm.SelectedDiscipline) ?? _vm.Disciplines[0];
            DisciplineCombo.SelectionChanged += (_, __) =>
            {
                if (_syncingDiscipline) return;
                if (DisciplineCombo.SelectedItem is DisciplineOption d)
                {
                    _vm.SelectedDiscipline = d.Code;
                    _disciplineUserSet = true;  // stop auto-following the model's detected discipline
                }
            };

            // Start empty — the panel is created at startup before a Revit doc is available.
            // First Re-scan reads the active model through App.RevitDispatcher.
            _vm.Filename = "(click Re-scan to analyse the active model)";
            _vm.PropertyChanged += Vm_PropertyChanged;

            Loaded += (_, __) => { Keyboard.Focus(this); RenderAll(); };
            BinaVibe.DocState.DocumentRevisionTracker.UndoRedoApplied += OnUndoRedo;
            KeyDown += OnKeyDown;

            PreviewKeyDown += (_, e) =>
            {
                if (e.OriginalSource is System.Windows.Controls.TextBox) return;
                OnKeyDown(this, e);
            };
        }

        // Every Revit API call from this pane (model reads, Locate, hiding the pane)
        // runs inside an ExternalEvent via App.RevitDispatcher, never directly in a WPF
        // handler. Writes (fixes, reset) go through App.JkrRenameHandler's own event.
        private static RevitDispatcher Revit =>
            App.RevitDispatcher ?? throw new InvalidOperationException(
                "BINA is still loading. Wait for Revit to finish starting and try again.");

        private sealed class ScanSnapshot
        {
            public string DocPath = "";
            public JkrExtractionResult Extraction;
            /// <summary>fill_audit over the built-in BIM010 rows (no PDF).</summary>
            public List<BinaVibe.Mcp.Tools.Audit.AuditRecord> AuditRecords = new List<BinaVibe.Mcp.Tools.Audit.AuditRecord>();
            public string AuditError;
            /// <summary>The model as the spec evaluators read it (Services/JkrSpec); null when the read failed.</summary>
            public RevitWebAppSync.Services.JkrSpec.SpecModel SpecModel;
            public string SpecModelError;
        }

        // ────────────────────────────────────────────────
        // Event plumbing
        // ────────────────────────────────────────────────

        private bool _renderQueued;

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PanelVm.Toast))
                ToastChanged?.Invoke(_vm.Toast?.Message);

            // Batch multiple rapid-fire PropertyChanged events into a single RenderAll().
            // Without this, switching tabs fires 4x Raise() → 4x full visual tree walks.
            if (_renderQueued) return;
            _renderQueued = true;
            Dispatcher.InvokeAsync(() =>
            {
                _renderQueued = false;
                RenderAll();
            }, System.Windows.Threading.DispatcherPriority.DataBind);
        }

        private void RenderAll()
        {
            FilenameText.Text = string.IsNullOrEmpty(_vm.Filename) ? "(no model)" : _vm.Filename;
            ResolvedCountText.Text = _vm.ResolvedCount.ToString();
            OfTotalText.Text = $" of {_vm.Total} resolved";
            SessionLine.Text = _vm.SessionLine;
            Ring.Percent = _vm.Percent;
            HiPill.Count = _vm.HighOpen;
            MdPill.Count = _vm.MedOpen;
            LoPill.Count = _vm.LowOpen;
            // Dim the non-selected severity pills when a severity filter is active.
            var sev = _vm.ActiveSeverity;
            HiPill.Opacity = (sev == null || sev == IssuePriority.High) ? 1.0 : 0.4;
            MdPill.Opacity = (sev == null || sev == IssuePriority.Medium) ? 1.0 : 0.4;
            LoPill.Opacity = (sev == null || sev == IssuePriority.Low) ? 1.0 : 0.4;

            // Fix All badge count — deterministic fixes plus issues the AI fixer
            // can attempt (Fix All fetches those from /jkr-autofix before the batch).
            var fc = _vm.FixableCount + _vm.AiFixableCount;
            FixCountText.Text = fc.ToString();
            FixCountBadge.Visibility = fc > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            TabOpenCount.Text = _vm.OpenCount.ToString();
            TabIgnoreCount.Text = _vm.IgnoredCount.ToString();
            TabResolvedCount.Text = _vm.ResolvedCount.ToString();
            TabManualCount.Text = _vm.ManualFixCount.ToString();
            FilteredCountText.Text = $"{_vm.FilteredCount} shown";

            TabOpen.IsChecked = _vm.IsOpenTab;
            TabIgnore.IsChecked = _vm.IsIgnoredTab;
            TabResolved.IsChecked = _vm.IsResolvedTab;
            TabManual.IsChecked = _vm.IsManualTab;

            // Highlight active tab pill, dim others
            var activeBg = JkrTheme.Brush("BrandTint");
            var activeFg = JkrTheme.Brush("BrandDark");
            var inactiveBg = JkrTheme.Brush("Surface.Line2");
            var inactiveFg = JkrTheme.Brush("Ink3");

            TabOpenPill.Background = _vm.IsOpenTab ? activeBg : inactiveBg;
            TabOpenCount.Foreground = _vm.IsOpenTab ? activeFg : inactiveFg;
            TabIgnorePill.Background = _vm.IsIgnoredTab ? activeBg : inactiveBg;
            TabIgnoreCount.Foreground = _vm.IsIgnoredTab ? activeFg : inactiveFg;
            TabResolvedPill.Background = _vm.IsResolvedTab ? activeBg : inactiveBg;
            TabResolvedCount.Foreground = _vm.IsResolvedTab ? activeFg : inactiveFg;
            TabManualPill.Background = _vm.IsManualTab ? activeBg : inactiveBg;
            TabManualCount.Foreground = _vm.IsManualTab ? activeFg : inactiveFg;

            // Scanning spinner
            if (_vm.Scanning) StartRescanSpin(); else StopRescanSpin();
            RescanLabel.Text = _vm.RescanLabel;

            // Search clear visibility
            SearchClear.Visibility = string.IsNullOrEmpty(SearchInput.Text) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

            // Empty state
            if (_authRequired)
            {
                // Not logged in (last scan came back 401) — takes priority over the
                // generic empty-state branch below so this never gets mistaken for
                // "All clear", "no results match this filter", etc.
                EmptyState.Visibility = System.Windows.Visibility.Visible;
                IssuesScroll.Visibility = System.Windows.Visibility.Collapsed;
                EmptyIcon.Glyph = "warning";
                EmptyMessage.Text = JkrComplianceService.LoginRequiredMessage;
                EmptyClearFilters.Visibility = System.Windows.Visibility.Collapsed;
            }
            else if (_vm.Filtered.Count == 0)
            {
                EmptyState.Visibility = System.Windows.Visibility.Visible;
                IssuesScroll.Visibility = System.Windows.Visibility.Collapsed;

                // A zero-result list can mean "genuinely clean" OR "a filter zeroed it
                // out" — those need different copy so the drafter isn't told the model
                // is clean when it's really their search/severity/category chip hiding
                // everything. Priority mirrors how Refresh() applies filters: tab first,
                // then search, then severity, then category.
                bool hasSearch = _vm.HasSearch;
                bool hasSeverity = _vm.ActiveSeverity != null;
                bool hasCategory = _vm.ActiveCategory != null;

                if (!_vm.IsOpenTab)
                {
                    EmptyIcon.Glyph = "clipboard";
                    EmptyMessage.Text = "No resolved issues yet.";
                }
                else if (hasSearch)
                {
                    EmptyIcon.Glyph = "search";
                    EmptyMessage.Text = $"No open issues match \"{_vm.Search}\".";
                }
                else if (hasSeverity)
                {
                    EmptyIcon.Glyph = "search";
                    EmptyMessage.Text = $"No open {_vm.ActiveSeverity} issues.";
                }
                else if (hasCategory)
                {
                    EmptyIcon.Glyph = "search";
                    EmptyMessage.Text = $"No open {_vm.ActiveCategory.ToLower()} issues — nice!";
                }
                else
                {
                    EmptyIcon.Glyph = "check";
                    EmptyMessage.Text = "All clear — no open issues.";
                }

                // "Clear filters" is only meaningful when a filter is actually
                // responsible for the empty state.
                EmptyClearFilters.Visibility = (hasSearch || hasSeverity || hasCategory)
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            }
            else
            {
                EmptyState.Visibility = System.Windows.Visibility.Collapsed;
                IssuesScroll.Visibility = System.Windows.Visibility.Visible;
            }

            // Row active highlight
            foreach (var container in EnumerateContainers(IssuesItems))
            {
                if (container is ContentPresenter cp)
                {
                    var row = FindDescendant<IssueRow>(cp);
                    if (row != null) row.IsActive = ReferenceEquals(row.DataContext, _vm.ActiveIssue);
                }
            }

            // Toast
            if (_vm.Toast != null)
            {
                ToastMsg.Text = _vm.Toast.Message;
                Toast.Visibility = System.Windows.Visibility.Visible;
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase() };
                Toast.BeginAnimation(OpacityProperty, fade);
                var slide = new ThicknessAnimation(new Thickness(12, 0, 12, 4), new Thickness(12, 0, 12, 12),
                                                   TimeSpan.FromMilliseconds(250)) { EasingFunction = new CubicEase() };
                Toast.BeginAnimation(Border.MarginProperty, slide);
            }
            else
            {
                Toast.Visibility = System.Windows.Visibility.Collapsed;
            }

            // Export modal
            if (_vm.ExportOpen)
            {
                OpenExportWindow();
            }
            else
            {
                CloseExportWindow();
            }

            StateChanged?.Invoke();
        }

        private static System.Collections.Generic.IEnumerable<DependencyObject> EnumerateContainers(ItemsControl ic)
        {
            if (ic?.Items == null) yield break;
            for (int i = 0; i < ic.Items.Count; i++)
            {
                var c = ic.ItemContainerGenerator.ContainerFromIndex(i);
                if (c != null) yield return c;
            }
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                var deep = FindDescendant<T>(child);
                if (deep != null) return deep;
            }
            return null;
        }

        // ────────────────────────────────────────────────
        // Actions
        // ────────────────────────────────────────────────

        private void Rescan_Click(object sender, RoutedEventArgs e) => _ = RunScanAsync();

        // Severity filter — clicking a High/Med/Low pill toggles that severity on the
        // issue list (composes with category chip, status tab, and search).
        private void HiPill_Click(object s, RoutedEventArgs e) => _vm.ToggleSeverity(IssuePriority.High);
        private void MdPill_Click(object s, RoutedEventArgs e) => _vm.ToggleSeverity(IssuePriority.Medium);
        private void LoPill_Click(object s, RoutedEventArgs e) => _vm.ToggleSeverity(IssuePriority.Low);

        private void FixAll_Click(object sender, RoutedEventArgs e) => _ = FixAllAsync(null, openOnly: false);

        /// <summary>
        /// Fix all: the ready-made fixes, plus fixes the backend AI fixer proposes for
        /// issues without one, then one confirm and one "JKR Fix all" undo step.
        /// <paramref name="section"/> limits it to one Borang section (null = all);
        /// <paramref name="openOnly"/> takes Open issues only, as the prototype's
        /// Fix all (n) does (the old button also retried Ignored / Manual ones).
        /// </summary>
        /// <param name="onlyIds">Limit it to these issues (a spec check's "Fix n"); null = no limit.</param>
        public async Task FixAllAsync(string section, bool openOnly, IReadOnlyCollection<string> onlyIds = null)
        {
            var only = onlyIds == null ? null : new HashSet<string>(onlyIds);
            if (App.JkrRenameHandler == null || App.JkrRenameEvent == null)
            {
                TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.AutoFixUnavailable());
                return;
            }
            if (_fixInFlight)
            {
                _vm.ShowToast(Jkr.Panel.Core.PaneText.FixRunning());
                return;
            }

            // Manual entries are retried too: a fix that failed for an
            // environmental reason (e.g. shared-param definition missing before
            // the definition-creation applicator landed) succeeds on a later
            // attempt. A fix that fails again just returns to Manual.
            // Never items that need a person (cannot_verify): no AI verdict to fix.
            bool Retryable(IssueVm i) =>
                !i.NeedsPerson
                && (openOnly ? i.IsAiOpen : (i.IsActionable || i.Status == IssueStatus.ManualFixNeeded))
                && (section == null || i.BorangSection == section)
                && (only == null || only.Contains(i.Id));

            var fixable = _vm.Issues
                .Where(i => Retryable(i) && i.AutoFixable && !string.IsNullOrEmpty(i.FixAction)
                            && !IsEmptyRename(i))
                .ToList();

            // Issues without a deterministic fix: ask the backend's AI fixer
            // (/jkr-autofix) for validated FixActions before the batch runs, so
            // one Fix All click covers both. Failure here degrades to the
            // deterministic-only set — it never blocks Fix All.
            var aiCandidates = _vm.Issues
                .Where(i => Retryable(i) && i.AiFixCandidate)
                .ToList();

            // Fixture test mode has no backend: every fixture issue carries its own fix.
            if (aiCandidates.Count > 0 && _lastScanRequest != null && !DevMode.UsesFixture)
            {
                _fixInFlight = true;   // block re-entry while the AI request is in flight
                FixAllBtn.IsEnabled = false;
                FixProgressPanel.Visibility = System.Windows.Visibility.Visible;
                FixProgressLabel.Text = Jkr.Panel.Core.PaneText.AiAnalysing(aiCandidates.Count);
                BusyChanged?.Invoke(FixProgressLabel.Text);
                FixProgressCount.Text = "";
                FixProgressBar.Width = 0;
                try
                {
                    var aiFixed = await FetchAiFixesAsync(aiCandidates);
                    fixable.AddRange(aiFixed);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA AiFix] failed: {ex.Message}");
                    _vm.ShowToast(Jkr.Panel.Core.PaneText.AiUnavailable());
                }
                finally
                {
                    _fixInFlight = false;   // ApplyFixBatch re-takes the lock below
                    FixAllBtn.IsEnabled = true;
                    BusyChanged?.Invoke(null);
                }
            }

            fixable = fixable.OrderBy(i => i.FixPriority).ToList();

            // Diagnostic: confirm what the badge said vs. what got queued. If these
            // don't match the user's expectation, we know the drop is upstream.
            System.Diagnostics.Debug.WriteLine(
                $"[BINA FixAll] queueing — total Issues={_vm.Issues.Count} " +
                $"actionable={_vm.Issues.Count(i => i.IsActionable)} " +
                $"autoFixable={_vm.Issues.Count(i => i.IsActionable && i.AutoFixable)} " +
                $"aiCandidates={aiCandidates.Count} withFixAction={fixable.Count}");

            if (fixable.Count == 0)
            {
                FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
                _vm.ShowToast(Jkr.Panel.Core.PaneText.NoAutoFixFound());
                return;
            }

            // No write without the confirm dialog (prototype askFix → doConfirm).
            FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
            if (!await ConfirmFixesAsync(fixable))
                return;

            try
            {
                await ApplyFixBatch(fixable);
            }
            catch (Exception ex)
            {
                // ApplyFixBatch takes the in-flight lock before raising the event;
                // if it throws mid-way, release the lock and UI state it left behind.
                _fixInFlight = false;
                HideApplying();
                TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.FixAllError(ex.Message));
            }
        }

        /// <summary>
        /// Ask /v1/compliance/jkr-autofix for fixes on issues the scan couldn't fix
        /// deterministically, and map the returned (backend-validated) FixActions
        /// onto the issues so they ride the normal Fix All batch. Returns the
        /// issues that received a fix; declined ones are left untouched.
        /// </summary>
        private async Task<List<IssueVm>> FetchAiFixesAsync(List<IssueVm> candidates)
        {
            var elementIds = new HashSet<long>(candidates.Select(c => c.RevitElementId));
            // Trimmed to what the backend fixer's prompt reads (Services/JkrAutoFixPayload):
            // ~48% of the prompt tokens on a typical request; builds a copy, so
            // _lastScanRequest stays whole for the next fix.
            var request = JkrAutoFixPayload.Trim(new JkrAutoFixRequest
            {
                Project = _lastScanRequest.Project,
                Model = _lastScanRequest.Model,
                Elements = _lastScanRequest.Elements
                    .Where(el => elementIds.Contains(el.ElementId)).ToList(),
                Issues = candidates.Select(c => new JkrAutoFixIssue
                {
                    CheckId = c.CheckId,
                    Rule = string.IsNullOrEmpty(c.RawRule) ? c.Title : c.RawRule,
                    Domain = string.IsNullOrEmpty(c.Domain) ? "element" : c.Domain,
                    Status = "fail",
                    ElementId = c.RevitElementId,
                    Category = c.Category,
                    TypeName = c.Element?.Name ?? "",
                    ActualValue = c.Actual,
                    ExpectedValue = c.Required,
                    Reason = c.Description,
                }).ToList(),
            });

            var response = await _jkrService.RequestAiFixesAsync(request);
            if (!string.IsNullOrEmpty(response?.Error))
            {
                System.Diagnostics.Debug.WriteLine($"[BINA AiFix] backend error: {response.Error}");
                _vm.ShowToast(Jkr.Panel.Core.PaneText.AiUnavailable());
                return new List<IssueVm>();
            }

            // Map validated fixes back onto their issues by check_id.
            var byId = (response.Fixes ?? new List<JkrAutoFixProposal>())
                .Where(f => f.FixAction != null && !string.IsNullOrEmpty(f.CheckId))
                .GroupBy(f => f.CheckId)
                .ToDictionary(g => g.Key, g => g.First());

            var aiFixed = new List<IssueVm>();
            foreach (var issue in candidates)
            {
                if (!byId.TryGetValue(issue.CheckId, out var proposal)) continue;
                var fix = proposal.FixAction;
                issue.FixAction = fix.Action ?? "";
                issue.FixParameterName = fix.ParameterName ?? "";
                issue.FixValue = fix.Value ?? "";
                issue.FixOldValue = fix.OldValue ?? "";
                issue.FixPriority = fix.Priority;
                issue.FixTarget = string.IsNullOrEmpty(fix.Target) ? "instance" : fix.Target;
                issue.FixReference = fix.Reference ?? "";
                issue.Example = fix.Value ?? "";
                issue.AutoFixable = true;
                issue.FixProposedByAi = true;
                aiFixed.Add(issue);
            }

            System.Diagnostics.Debug.WriteLine(
                $"[BINA AiFix] model={response.ModelUsed} fixes={aiFixed.Count} " +
                $"unfixed={response.UnfixedCheckIds?.Count ?? 0}");
            return aiFixed;
        }

        // ── Busy state for model writes ──────────────────────────────────────
        // A write batch runs synchronously on Revit's UI thread, which this pane
        // shares, so nothing redraws until it finishes. The busy state is therefore
        // drawn BEFORE the event is raised (RaiseWriteAsync), and every write button
        // stays disabled until the batch's OnCompleted runs.

        private void SetWriteButtonsEnabled(bool enabled)
        {
            FixAllBtn.IsEnabled = enabled;
            IgnoreAllBtn.IsEnabled = enabled;
            RescanBtn.IsEnabled = enabled;
            ResetBtn.IsEnabled = enabled;
            ExportBtn.IsEnabled = enabled;
        }

        private void ShowApplying(string label)
        {
            BusyChanged?.Invoke(label);
            FixProgressPanel.Visibility = System.Windows.Visibility.Visible;
            FixProgressLabel.Text = label;
            FixProgressCount.Text = "";
            FixProgressBar.Width = 0;
            SetWriteButtonsEnabled(false);
        }

        private void HideApplying()
        {
            BusyChanged?.Invoke(null);
            FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
            SetWriteButtonsEnabled(true);
        }

        /// <summary>Let WPF lay out and render the busy state, then hand the queued
        /// batch to Revit. Returns false (and clears the busy state) if Revit refused
        /// the event, so the pane never stays stuck in "Applying…".</summary>
        private async Task<bool> RaiseWriteAsync()
        {
            ExternalEventRequest request;
            try
            {
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                // The fixes carry element ids from the scanned model: the handler refuses
                // to write them into any other open model.
                App.JkrRenameHandler.ExpectedDocPath = _scannedDocPath ?? "";
                request = App.JkrRenameEvent.Raise();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] fix raise failed: {ex}");
                request = ExternalEventRequest.Denied;
            }
            if (request == ExternalEventRequest.Accepted || request == ExternalEventRequest.Pending)
                return true;

            App.JkrRenameHandler.RenameQueue.Clear();
            App.JkrRenameHandler.ParamFixQueue.Clear();
            App.JkrRenameHandler.OnCompleted = null;
            _fixInFlight = false;
            HideApplying();
            _vm.ShowToast(Jkr.Panel.Core.PaneText.RevitBusy());
            return false;
        }

        /// <summary>
        /// Queue a batch of fix-carrying issues into JkrRenameHandler and fire the
        /// ExternalEvent. Fix All calls this with the merged set: deterministic
        /// fixes from the scan plus AI fixes fetched from /jkr-autofix. The whole
        /// batch is one undo step in Revit ("JKR Fix all").
        /// Caller guarantees: handler initialised, !_fixInFlight, fixable non-empty.
        /// </summary>
        private async Task ApplyFixBatch(List<IssueVm> fixable)
        {
            // Captured before any status mutations so the post-fix toast can report a truthful delta.
            var openBeforeFix = _vm.OpenCount;

            var totalToFix = fixable.Count;
            _fixInFlight = true;

            var handler = App.JkrRenameHandler;
            handler.RenameQueue.Clear();
            handler.ParamFixQueue.Clear();

            foreach (var issue in fixable)
            {
                if (issue.FixAction.Equals("rename_type", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(issue.FixValue))
                        handler.RenameQueue.Add((issue.RevitElementId, issue.FixValue));
                }
                else
                {
                    handler.ParamFixQueue.Add(new JkrFixAction
                    {
                        Action = issue.FixAction,
                        ElementId = issue.RevitElementId,
                        ParameterName = issue.FixParameterName,
                        Value = issue.FixValue,
                        OldValue = issue.FixOldValue,
                        Priority = issue.FixPriority,
                        Target = string.IsNullOrEmpty(issue.FixTarget) ? "instance" : issue.FixTarget,
                    });
                }
            }

            handler.TransactionGroupName = JkrRenameHandler.DefaultUndoName;
            ShowApplying(JkrFixBatch.BusyLabel(handler.RenameQueue.Count + handler.ParamFixQueue.Count));

            handler.OnCompleted = (result) =>
            {
                Dispatcher.Invoke(() =>
                {
                    SetWriteButtonsEnabled(true);
                    _fixInFlight = false;
                    // The write is over: clear "Applying n changes…" on the Results screen.
                    // A successful batch shows "Re-scanning…" until its re-check ends.
                    BusyChanged?.Invoke(null);

                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
                        TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.FixAllError(result.Error));
                        return;
                    }

                    var total = result.Renamed + result.ParamFixed;

                    // Use FailedFixKeys (per-fix), not FailedElementIds (per-element).
                    // When element X has 3 fixes (rename + 2 paramfixes) and only the
                    // Kod_Jenis paramfix fails, only that one fix is in FailedFixKeys.
                    // The element-id-based check would over-flag the rename and the
                    // other paramfix as failed.
                    bool IsFixedFailed(IssueVm iv)
                    {
                        return result.FailedFixKeys.Contains(
                            RenameResult.MakeFixKey(iv.FixAction, iv.RevitElementId, iv.FixParameterName));
                    }

                    // A failed fix stays Open and not compliant, with its reason on the
                    // row and in the detail view. It is NOT moved to Manual (Manual is
                    // only for checks the AI cannot judge). ALWAYS blocklist failed fixes
                    // with their reason — even when total==0 — so the re-scan keeps the
                    // reason on the fresh Open row and Fix all doesn't retry them.
                    var failedFixes = new HashSet<IssueVm>();
                    foreach (var i in fixable)
                    {
                        if (IsFixedFailed(i))
                        {
                            var why = result.ReasonFor(i.FixAction, i.RevitElementId, i.FixParameterName);
                            IssueMapper.BlockFix(i.RevitElementId, i.FixAction, i.FixParameterName, why);
                            i.AutoFixable = false;
                            i.FixError = why ?? RenameResult.UnknownFailureReason;
                            if (i.Status == IssueStatus.ManualFixNeeded)
                                i.Status = IssueStatus.Open;
                            failedFixes.Add(i);
                        }
                        else
                        {
                            i.FixError = "";
                            i.Status = IssueStatus.Fixed;
                        }
                    }

                    // Only successes are carried across the re-scan (they land in
                    // Resolved: the recheck omits passing checks). A failure that still
                    // fires comes back as a fresh Open row with its reason from the
                    // blocklist; one that no longer fires has passed and is dropped.
                    var preservedAfterFix = fixable.Where(i => i.Status == IssueStatus.Fixed).ToList();

                    // Persist Fixed (with snapshot) so it survives a cold Re-scan. A failed
                    // fix that was ManualFixNeeded in the audit file is saved back as Open.
                    var docPath = _scannedDocPath;
                    foreach (var i in fixable)
                    {
                        if (i.Status != IssueStatus.Fixed && !failedFixes.Contains(i))
                            continue;
                        try { JkrAuditStore.Save(docPath, i); }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[BINA] audit save failed: {ex.Message}");
                        }
                    }

                    if (total == 0)
                    {
                        FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
                        RenderAll(); // refresh badge after blocklist changes
                        // Nothing was written, so the group was rolled back: no undo entry.
                        _vm.ShowToast(Jkr.Panel.Core.PaneText.NothingWritten());
                        ShowFailedFixes(result, applied: 0, fixAll: fixable.Count > 1);
                        return;
                    }

                    // Show completion in progress bar
                    FixProgressLabel.Text = Jkr.Panel.Core.PaneText.AppliedRescanning(total);
                    FixProgressCount.Text = $"{total}/{totalToFix}";
                    try
                    {
                        var barParent = FixProgressBar.Parent as FrameworkElement;
                        if (barParent != null && barParent.ActualWidth > 0)
                            FixProgressBar.Width = barParent.ActualWidth * ((double)total / totalToFix);
                    }
                    catch { }

                    // Show summary of what was fixed vs failed
                    var failCount = result.Failed + result.Skipped;

                    // Re-scan to verify — the re-scan will show fewer issues now.
                    _ = RunScanAfterFix(total, totalToFix, failCount, preservedAfterFix, openBeforeFix, result);
                });
            };

            await RaiseWriteAsync();
        }

        private static bool IsEmptyRename(IssueVm i) =>
            string.Equals(i.FixAction, "rename_type", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(i.FixValue);

        private static JkrFixAction ToFixAction(IssueVm i) => new JkrFixAction
        {
            Action = i.FixAction,
            ElementId = i.RevitElementId,
            ParameterName = i.FixParameterName,
            Value = i.FixValue,
            OldValue = i.FixOldValue,
            Priority = i.FixPriority,
            Target = string.IsNullOrEmpty(i.FixTarget) ? "instance" : i.FixTarget,
        };

        /// <summary>Ask before writing these fixes: "Write these changes to the model?"
        /// with one row per change (ready-made fixes, then AI fixer proposals), and the
        /// "Revit may pause" line from 500 affected elements.
        /// True only when the user confirmed.</summary>
        private Task<bool> ConfirmFixesAsync(List<IssueVm> issues)
        {
            var rows = issues.Select(JkrConfirmRow.FromIssue).ToList();
            var renames = issues.Where(i => string.Equals(i.FixAction, "rename_type", StringComparison.OrdinalIgnoreCase))
                                .Select(i => i.RevitElementId).ToList();
            var fixes = issues.Where(i => !string.Equals(i.FixAction, "rename_type", StringComparison.OrdinalIgnoreCase))
                              .Select(ToFixAction).ToList();
            return ConfirmWriteAsync(JkrConfirmKind.Fix, rows, renames, fixes);
        }

        /// <summary>Count the affected elements in Revit (instances of renamed types
        /// included, stopping at 500), then show the confirm dialog. The in-flight
        /// lock is held while counting and while the dialog is open, so a second
        /// click can't start another write. Nothing is written here.</summary>
        private async Task<bool> ConfirmWriteAsync(JkrConfirmKind kind, List<JkrConfirmRow> rows,
                                                   List<long> renames, List<JkrFixAction> fixes)
        {
            if (rows.Count == 0) return false;
            if (_fixInFlight) { _vm.ShowToast(Jkr.Panel.Core.PaneText.FixRunning()); return false; }

            _fixInFlight = true;
            try
            {
                // Fallback when Revit can't count: distinct elements named in the batch.
                var affected = renames.Concat(fixes.Select(f => f.ElementId)).Distinct().Count();
                try
                {
                    var counted = await Revit.InvokeAsync(app =>
                    {
                        var doc = app.ActiveUIDocument?.Document;
                        return doc == null ? -1 : JkrAffectedElements.Count(doc, renames, fixes);
                    });
                    if (counted >= 0) affected = counted;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA Confirm] affected count failed, using distinct elements: {ex.Message}");
                }

                var model = new JkrConfirmModel(kind, rows, affected, lang: Jkr.Panel.Core.PaneLanguage.Current);
                return JkrConfirmWindow.Ask(model, this);
            }
            finally
            {
                _fixInFlight = false;
            }
        }

        /// <summary>After a batch, show the Fix all result window: what was written and
        /// every fix that was not, grouped by reason, so nothing fails silently. The
        /// same reasons stay on the issues' rows and in their detail views. Nothing
        /// opens when nothing failed. Locate on a row closes it and shows the element.</summary>
        private void ShowFailedFixes(RenameResult result, int applied, bool fixAll)
        {
            if (result.Failed + result.Skipped == 0) return;
            long locate;
            try
            {
                var labels = ElementLabels();
                var model = new Jkr.Panel.Core.FixResultModel(result, applied, fixAll,
                    id => labels.TryGetValue(id, out var l) ? l : null);
                locate = Jkr.Modals.FixResultWindow.Show(model, this);
            }
            catch (Exception ex)
            {
                // Never lose the list: fall back to Revit's plain box.
                System.Diagnostics.Debug.WriteLine($"[BINA] fix result window failed: {ex}");
                TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle,
                    Jkr.Panel.Core.PaneText.FailedFixes(applied, result.Failed + result.Skipped,
                        result.BuildFailDetails(Handlers.JkrRenameHandler.MaxListedFailures)));
                return;
            }
            if (locate > 0)
            {
                var issue = _vm.Issues.FirstOrDefault(i => i.RevitElementId == locate)
                            ?? new IssueVm { RevitElementId = locate };
                _ = LocateInRevitAsync(issue);
            }
        }

        /// <summary>"Generic Models · Name" per element ID, from the scanned issues.</summary>
        private Dictionary<long, string> ElementLabels()
        {
            var map = new Dictionary<long, string>();
            foreach (var i in _vm.Issues)
            {
                if (i.RevitElementId <= 0 || map.ContainsKey(i.RevitElementId)) continue;
                var name = i.Element?.Name;
                if (name == "—" || name == "Element" || name == "Model") name = null;
                var parts = new[] { i.RevitCategory, name }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
                if (parts.Count > 0) map[i.RevitElementId] = string.Join(" · ", parts);
            }
            return map;
        }

        /// <summary>
        /// Re-scan after Fix All and show results with context about what was fixed.
        /// Uses the dedicated /jkr-recheck endpoint for clearer telemetry.
        /// </summary>
        private async Task RunScanAfterFix(int fixedCount, int toFixCount, int failCount,
                                            List<IssueVm> preservedAfterFix = null, int openBeforeFix = 0,
                                            RenameResult fixResult = null)
        {
            // The re-check is a scan: Reset, Run and Re-scan wait for it (a second scan
            // would race it; a Reset would be undone when it lands).
            _vm.Scanning = true;
            BusyChanged?.Invoke(Jkr.Panel.Core.PaneText.Rescanning());
            try
            {
                FixProgressLabel.Text = Jkr.Panel.Core.PaneText.Rescanning();
                await RunScanInner(isRecheck: true, preservedAfterFix: preservedAfterFix);

                FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;

                // One toast covers the batch outcome. Failed fixes stay Open with their
                // reason on the row; they are also listed once in a dialog.
                _vm.ShowToast(Jkr.Panel.Core.PaneText.ChangesWritten(fixedCount, failCount));
                if (fixResult != null)
                    ShowFailedFixes(fixResult, applied: fixResult.Renamed + fixResult.ParamFixed, fixAll: toFixCount > 1);
            }
            catch (Exception ex)
            {
                FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
                TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.RescanAfterFixFailed(ex.Message));
            }
            finally
            {
                _vm.Scanning = false;
                BusyChanged?.Invoke(null);
            }
        }

        private void IgnoreAll_Click(object sender, RoutedEventArgs e)
        {
            var ignorable = _vm.Issues
                .Where(i => i.IsAiOpen && i.CanIgnore)
                .ToList();

            if (ignorable.Count == 0)
            {
                _vm.ShowToast(Jkr.Panel.Core.PaneText.NoMedLowToIgnore());
                return;
            }

            // One plain toast for all of them.
            _vm.ApplyDecisions(ignorable, IssueStatus.Ignored,
                Jkr.Panel.Core.PaneText.IgnoredMedLow(ignorable.Count));

            // Persist all accepted decisions to audit file
            var docPath = _scannedDocPath;
            foreach (var issue in ignorable)
                JkrAuditStore.Save(docPath, issue);
        }

        private async Task RunScanAsync()
        {
            if (_vm.Scanning) return;

            _vm.Scanning = true;
            _scanError = null;
            ScanStarted?.Invoke(SelectedLodLevel, _vm.SelectedDiscipline);
            try
            {
                if (!await RunScanInner())
                {
                    _scanError = Jkr.Panel.Core.PaneText.NoDocument();
                    if (!HostShowsScanProgress) TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, _scanError);
                }
            }
            catch (Exception ex)
            {
                _scanError = $"{ex.GetType().Name}: {ex.Message}";
                if (!HostShowsScanProgress)
                    TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.ScanError(_scanError));
            }
            finally
            {
                _vm.Scanning = false;
                ScanCompleted?.Invoke(_vm.Summary, _scanError);
            }
        }

        /// <summary>Core scan logic — shared by RunScanAsync and RunScanAfterFix.</summary>
        /// <param name="clearAudit">If true, wipe persisted Ignore/Approve decisions before loading results.</param>
        /// <param name="isRecheck">If true, hit /jkr-recheck (post-fix verification) instead of /jkr-check-v2.</param>
        /// <param name="preservedAfterFix">Issues just attempted in an auto-fix batch — both successes
        /// (Status=Fixed, land in Resolved) and failures (Status=Open, stay visible). Re-injected after
        /// the rescan so neither group silently vanishes when the recheck omits them.</param>
        /// <param name="onlyElements">AI-427 targeted re-check: only rows on these Revit elements change (see
        /// JkrRecheck.Reconcile); every other row stays. The result is left in _lastRecheck.</param>
        /// <param name="justWritten">With onlyElements: rows the caller just wrote; reported, not resolved here.</param>
        /// <returns>False when no document is open (nothing was scanned).</returns>
        private async Task<bool> RunScanInner(bool clearAudit = false, bool isRecheck = false, List<IssueVm> preservedAfterFix = null,
                                              IReadOnlyCollection<long> onlyElements = null, IReadOnlyCollection<IssueVm> justWritten = null)
        {
            // The scan's discipline and LOD, read here (not on Revit's thread): the
            // user's pick, else the model's detected discipline (same rule as below).
            var scanLod = SelectedLodLevel;
            var userDiscipline = _disciplineUserSet ? _vm.SelectedDiscipline : null;
            var disciplineCodes = _vm.Disciplines.Select(d => d.Code).ToList();

            // Model read on Revit's thread. The extraction is plain data, so the rest
            // of the scan (HTTP, mapping, rendering) runs here on the pane.
            var snapshot = await Revit.InvokeAsync(app =>
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) return null;
                BinaVibe.DocState.DocumentRevisionTracker.EnsureSubscribed(app);   // AI-427: undo / redo re-checks
                var snap = new ScanSnapshot
                {
                    DocPath = doc.PathName ?? "",
                    Extraction = JkrBuildingInfoExtractor.Extract(doc),
                };
                // BIM010 rows: the fill_audit checkers over the built-in form, in
                // the same Revit job. A failure here never blocks the scan.
                try
                {
                    snap.AuditRecords = BinaVibe.Mcp.Tools.Audit.AuditTools.EvaluateRows(
                        doc, BinaVibe.Mcp.Tools.Audit.Bim010Form.ToFormRows());
                }
                catch (Exception auditEx)
                {
                    snap.AuditError = auditEx.Message;
                    System.Diagnostics.Debug.WriteLine($"[BINA BIM010] fill_audit failed: {auditEx}");
                }
                // The spec evaluators' snapshot, same job. A failure never blocks the scan.
                try
                {
                    var detected = snap.Extraction?.Discipline;
                    var disc = userDiscipline
                               ?? (detected != null && disciplineCodes.Contains(detected) ? detected : disciplineCodes.FirstOrDefault());
                    snap.SpecModel = RevitWebAppSync.Services.JkrSpec.SpecModelReader.Read(doc, disc, scanLod);
                }
                catch (Exception specEx)
                {
                    snap.SpecModelError = specEx.Message;
                    System.Diagnostics.Debug.WriteLine($"[BINA SPEC] model read failed: {specEx}");
                }
                return snap;
            });
            if (snapshot == null) return false;
            // A targeted re-check of another model's elements would corrupt this pane's rows.
            if (onlyElements != null && snapshot.DocPath != _scannedDocPath) return false;
            _scannedDocPath = snapshot.DocPath;
            // Failed fixes are remembered per model (element ids repeat across models).
            IssueMapper.BlockScope = _scannedDocPath ?? "";

            // Optionally clear the audit file so everything starts fresh
            if (clearAudit)
            {
                JkrAuditStore.Clear(_scannedDocPath);
            }

            var extraction = snapshot.Extraction;
            _vm.Filename = string.IsNullOrEmpty(extraction.FileName) ? "(unsaved model)" : extraction.FileName;

            // Until the user picks a discipline, follow the model's detected one
            // (mapped onto the 5 selectable codes; LD/unknown fall back to AR).
            if (!_disciplineUserSet)
            {
                var detected = _vm.Disciplines.FirstOrDefault(d => d.Code == extraction.Discipline) ?? _vm.Disciplines[0];
                _vm.SelectedDiscipline = detected.Code;
                _syncingDiscipline = true;
                DisciplineCombo.SelectedItem = detected;
                _syncingDiscipline = false;
            }

            var request = extraction.ToV2Request(lodLevel: SelectedLodLevel);
            request.Project.Discipline = _vm.SelectedDiscipline;  // scope the scan to the chosen discipline
            _lastScanRequest = request;  // AI Fix reuses this snapshot

            // The add-in's spec evaluators: pure, off Revit's thread, alongside the
            // backend call. A failure gives no model results, never a pass.
            var specModel = snapshot.SpecModel;
            var modelTask = specModel == null
                ? Task.FromResult(new List<Jkr.Panel.Core.SpecResult>())
                : Task.Run(() =>
                {
                    try { return RevitWebAppSync.Services.JkrSpec.SpecChecks.Run(specModel) ?? new List<Jkr.Panel.Core.SpecResult>(); }
                    catch (Exception specEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BINA SPEC] evaluators failed: {specEx}");
                        return new List<Jkr.Panel.Core.SpecResult>();
                    }
                });

            ModelCheckResponse response;
            if (DevMode.UsesFixture)
            {
                // TEST ONLY: no backend call; issues come from the fixture checked
                // against the live model.
                response = await ScanFixtureAsync();
                if (response == null)
                {
                    _scanError = "The test fixture could not be used.";
                    return true;
                }
            }
            else
            {
                response = isRecheck
                    ? await _jkrService.RecheckJkrComplianceAsync(request)
                    : await _jkrService.CheckJkrComplianceV2Async(request, skipAi: true);
            }
            // The backend failed or timed out (not 401): the run still completes with
            // the add-in's own results (BIM010 checkers + spec evaluators). Checks only
            // the backend answers stay "not checked" (SpecReport.BackendError).
            string backendError = null;
            if (response == null || !string.IsNullOrEmpty(response.Error))
            {
                if (response?.Error == JkrComplianceService.LoginRequiredMessage)
                {
                    // Persistent state, not a dismissable dialog: a TaskDialog here
                    // would leave the empty-state banner behind it showing "All clear"
                    // once the user dismisses it (see _authRequired's doc comment).
                    SetAuthRequired(true);
                    _scanError = JkrComplianceService.LoginRequiredMessage;
                    RenderAll();
                    return true;
                }
                backendError = response?.Error ?? "No response from the JKR check server.";
                System.Diagnostics.Debug.WriteLine($"[BINA JKR] backend check failed, local results only: {backendError}");
                response = new ModelCheckResponse();
            }
            SetAuthRequired(false);

            var issues = IssueMapper.MapAll(response);

            // BIM010 rows from fill_audit join the list (before the audit-store
            // merge, so Ignore / Manual decisions apply to them too), and the
            // section-and-cell summary is built from both sources.
            var auditIssues = IssueMapper.FromAuditRecords(snapshot.AuditRecords);
            var knownIds = new HashSet<string>(issues.Select(i => i.Id));
            issues.AddRange(auditIssues.Where(i => knownIds.Add(i.Id)));
            // The spec evaluators' findings join the same way (stable ids, so the
            // audit-store merge below keeps their Ignore / Reviewed decisions).
            var modelResults = await modelTask;
            issues.AddRange(IssueMapper.FromModelResults(modelResults, Jkr.Panel.Core.PaneLanguage.Current)
                .Where(i => !string.IsNullOrEmpty(i.Id) && knownIds.Add(i.Id)));
            // The same run against the spec's 61 checks (Checklist tab). Built from
            // every result — MapAll above drops passes — plus the BIM010 records and
            // the spec evaluators' results.
            var specResults = IssueMapper.ToSpecResults(response, snapshot.AuditRecords, modelResults);
            _vm.Spec = Jkr.Panel.Core.SpecReport.Build(specResults, backendError);
            // The Borang summary (Running screen %, Borang PDF marks and summary box)
            // follows the spec report, so all three screens show the Results numbers.
            // The BIM010-cell summary is only the fallback when there is no spec report.
            _vm.Summary = Jkr.Panel.Core.SpecBorang.SummaryFor(_vm.Spec, specResults, () =>
            {
                var unmapped = new List<string>();
                var cells = BinaVibe.Mcp.Tools.Audit.AuditSummary.CellsFromRecords(snapshot.AuditRecords);
                cells.AddRange(IssueMapper.ToCells(response, unmapped));
                return BinaVibe.Mcp.Tools.Audit.AuditSummary.Build(cells, unmapped);
            });
            if (backendError != null)
                _vm.ShowToast(BackendUnavailableToast(backendError));
            if (DevMode.Unlocked)
                WriteTestSummary(_vm.Summary, snapshot);

            // Only merge persisted decisions if we didn't just clear them
            if (!clearAudit)
            {
                var audit = JkrAuditStore.LoadFor(_scannedDocPath);
                JkrAuditStore.MergeInto(issues, audit);
                JkrAuditStore.MergeReviewed(issues, JkrAuditStore.LoadReviewedFor(_scannedDocPath));

                // Re-inject snapshot-backed entries from the audit. The fresh check
                // can fail to return them under their original Id for two reasons:
                //   - Fixed: the rule passes now (success — keep in Resolved tab).
                // If a Fixed Id IS in the fresh check it's a regression — drop the
                // audit entry and let the fresh Open row stand. Manual entries that
                // reappear under the same Id are already overlaid by LoadFor above,
                // so we just skip the Id-in-fresh case here.
                var freshIds = new HashSet<string>(
                    issues.Where(i => !string.IsNullOrEmpty(i.Id)).Select(i => i.Id));
                foreach (var fixedVm in JkrAuditStore.LoadFixedFor(_scannedDocPath))
                {
                    if (string.IsNullOrEmpty(fixedVm.Id)) continue;
                    if (freshIds.Contains(fixedVm.Id))
                        JkrAuditStore.Remove(_scannedDocPath, fixedVm.Id);
                    else
                    {
                        fixedVm.FixVerified = backendError == null;   // absent from a full scan: it passes now; a partial scan proves nothing
                        issues.Add(fixedVm);
                    }
                }
                // A marked item the fresh scan no longer returns was fixed in Revit: it
                // passes as normal, so its entry is dropped (still-present ids keep the overlay).
                // Only when the backend answered: after an error the scan is partial and a
                // backend-only check's marked item was never re-checked.
                if (backendError == null) JkrAuditStore.PruneMissing(_scannedDocPath, freshIds);
            }

            // Carry attempted-fix issues through the rescan boundary. Dedup by Id so when
            // the recheck still returns one (rule didn't actually pass, or fix failed and
            // the rule still flags it), the recheck version wins — no double-listing.
            if (preservedAfterFix != null && preservedAfterFix.Count > 0)
            {
                var existingIds = new HashSet<string>(
                    issues.Where(i => !string.IsNullOrEmpty(i.Id)).Select(i => i.Id));
                foreach (var pf in preservedAfterFix)
                {
                    if (string.IsNullOrEmpty(pf.Id) || !existingIds.Contains(pf.Id))
                    {
                        if (pf.Status == IssueStatus.Fixed) pf.FixVerified = backendError == null;   // the re-check passed it (only if the scan was complete)
                        issues.Add(pf);
                    }
                }
            }

            if (onlyElements != null)
            {
                var outcome = JkrRecheck.Reconcile(_vm.Issues.ToList(), onlyElements, issues, backendError == null, justWritten);
                _vm.ReplaceIssues(outcome.Issues);
                _lastRecheck = outcome;
                return true;
            }

            _vm.ReplaceIssues(issues);
            return true;
        }

        // ────────────────────────────────────────────────
        // AI-427: the one write → re-check mechanism
        // ────────────────────────────────────────────────

        private JkrRecheckOutcome<IssueVm> _lastRecheck;
        private readonly System.Threading.SemaphoreSlim _recheckGate = new System.Threading.SemaphoreSlim(1, 1);
        private readonly HashSet<long> _undoIds = new HashSet<long>();
        private System.Windows.Threading.DispatcherTimer _undoTimer;

        /// <summary>
        /// THE entry point for "did that change really fix the rule?". Re-runs the check
        /// (the /jkr-recheck pass, like Fix all's) and awaits it, then changes ONLY the
        /// rows on <paramref name="elementIds"/> by whether their rule passes now
        /// (JkrRecheck.Reconcile): Resolved rows whose rule fails again reopen, Open
        /// rows whose rule passes leave the list, a partial scan proves no pass.
        /// Pass <paramref name="justWritten"/> for rows the caller just wrote: they are
        /// left as they are and come back in Verified / StillFailing, so the caller
        /// resolves them only when the rule re-passed. Returns null when nothing could
        /// be checked (no scan yet, another scan running, other model, or an error);
        /// callers then keep their old behaviour. Must be called on the pane's thread.
        /// The full Re-scan button and the pass after Fix all do not use this.
        /// </summary>
        public async Task<JkrRecheckOutcome<IssueVm>> RecheckAsync(
            IReadOnlyCollection<long> elementIds, IReadOnlyCollection<IssueVm> justWritten = null)
        {
            if (elementIds == null || elementIds.Count == 0) return null;
            await _recheckGate.WaitAsync();
            try
            {
                if (_vm.Scanning || _vm.Spec == null) return null;   // a full scan covers it / nothing scanned yet
                _vm.Scanning = true;   // Re-scan and Reset wait, like for the pass after Fix all
                BusyChanged?.Invoke(Jkr.Panel.Core.PaneText.Rescanning());
                _lastRecheck = null;
                try
                {
                    var ran = await RunScanInner(isRecheck: true, onlyElements: elementIds, justWritten: justWritten);
                    return ran ? _lastRecheck : null;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA JKR] targeted re-check failed: {ex.Message}");
                    return null;
                }
                finally
                {
                    _vm.Scanning = false;
                    BusyChanged?.Invoke(null);
                }
            }
            finally { _recheckGate.Release(); }
        }

        /// <summary>Revit thread, inside DocumentChanged: record the ids and hand off.</summary>
        private void OnUndoRedo(Autodesk.Revit.DB.Document doc, IReadOnlyList<long> ids)
        {
            try
            {
                if (doc == null || ids == null || ids.Count == 0) return;
                if ((doc.PathName ?? "") != _scannedDocPath) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var id in ids) _undoIds.Add(id);
                    ArmUndoRecheck();
                }));
            }
            catch { /* a recorder never breaks a commit */ }
        }

        private void ArmUndoRecheck()
        {
            if (_undoTimer == null)
            {
                _undoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _undoTimer.Tick += async (_, __) =>
                {
                    _undoTimer.Stop();
                    if (_vm.Scanning) { _undoTimer.Start(); return; }   // wait out a running scan
                    var rowElements = new HashSet<long>(_vm.Issues.Select(i => i.RevitElementId));
                    var ids = _undoIds.Where(rowElements.Contains).ToList();
                    _undoIds.Clear();
                    if (ids.Count == 0) return;
                    var outcome = await RecheckAsync(ids);
                    if (outcome != null && outcome.Reopened.Count > 0)
                        _vm.ShowToast(Jkr.Panel.Core.PaneText.FixUndone(outcome.Reopened.Count));
                };
            }
            _undoTimer.Stop();
            _undoTimer.Start();
        }

        /// <summary>After a single auto-fix wrote: resolve the row only if its rule re-passed.</summary>
        private async Task FinishSingleFixAsync(IssueVm issue, bool advance)
        {
            var outcome = await RecheckAsync(new[] { issue.RevitElementId }, new[] { issue });
            if (outcome != null && outcome.StillFailing.Contains(issue))
            {
                // Written, but the rule still fails: the row stays Open.
                _vm.ShowToast(Jkr.Panel.Core.PaneText.FixStillFails());
                return;
            }
            // Verified, or nothing could be checked (busy / no backend): resolve as before;
            // FixVerified says whether a scan proved it.
            issue.FixVerified = outcome != null && outcome.Verified.Contains(issue);
            _vm.ApplyAction(issue, IssueStatus.Fixed, advance);
            try { JkrAuditStore.Save(_scannedDocPath, issue); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[BINA] audit save failed: {ex.Message}"); }
        }

        /// <summary>Toast when the backend check failed and the run shows the add-in's own results only.</summary>
        private static string BackendUnavailableToast(string error)
        {
            var why = (error ?? "").Trim();
            if (why.Length > 120) why = why.Substring(0, 120).TrimEnd() + "…";
            return Jkr.Panel.Core.PaneLanguage.Pick(
                "Semakan backend JKR tidak tersedia — hanya semakan add-in ditunjukkan. " + why,
                "The JKR backend check is unavailable — showing the add-in's own checks only. " + why).Trim();
        }

        /// <summary>TEST ONLY (BINA_DEV_UNLOCK): write the BIM010 summary of the last
        /// scan as JSON next to the fixture (or in %TEMP%), so the section %s, row
        /// marks and unmapped checks can be checked before the Results view exists.</summary>
        private void WriteTestSummary(BinaVibe.Mcp.Tools.Audit.Bim010Summary summary, ScanSnapshot snapshot)
        {
            try
            {
                var dir = DevMode.UsesFixture ? System.IO.Path.GetDirectoryName(DevMode.FixturePath) : null;
                if (string.IsNullOrEmpty(dir)) dir = System.IO.Path.GetTempPath();
                var path = System.IO.Path.Combine(dir, "bim010-summary.json");
                var payload = new
                {
                    model = _vm.Filename,
                    scanned = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    source = summary.SpecBased ? "spec" : "bim010_cells",
                    audit_error = snapshot.AuditError,
                    headline = new { pct = summary.Pct, ai_pass = summary.Pass, ai_total = summary.AiTotal, manual = summary.Pending },
                    sections = summary.Sections.Select(s => new { s.Section, s.Pass, s.Fail, s.Pending, s.Pct }),
                    rows = summary.Rows.Values.Select(r => new { r.Key, r.Mark, columns = r.Columns.Count > 0 ? r.Columns : null, r.Notes }),
                    unmapped = summary.Unmapped,
                    audit_rows = snapshot.AuditRecords.Select(r => new
                    {
                        key = r.Row.Section + r.Row.RowRef, r.Compliance, r.CheckerId,
                        reason = r.Evidence != null && r.Evidence.TryGetValue("not_verifiable_reason", out var why) ? why : null,
                        r.Remark,
                    }),
                };
                System.IO.File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(payload, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA BIM010] test summary not written: {ex.Message}");
            }
        }

        /// <summary>TEST ONLY (BINA_JKR_FIXTURE): load the fixture — generating it
        /// from the open model the first time — and check it against the live
        /// model, read-only, inside a Revit job. Null when it can't be used (the
        /// reason has been shown).</summary>
        private async Task<ModelCheckResponse> ScanFixtureAsync()
        {
            var path = DevMode.FixturePath;
            (ModelCheckResponse Response, int Count, bool Generated, List<string> Errors) outcome;
            try
            {
                outcome = await Revit.InvokeAsync(app =>
                {
                    var doc = app.ActiveUIDocument?.Document;
                    if (doc == null) return ((ModelCheckResponse)null, 0, false, new List<string> { "No active document." });
                    var generated = false;
                    JkrFixture fixture;
                    if (!System.IO.File.Exists(path))
                    {
                        fixture = JkrFixtureScanner.Generate(doc);
                        JkrFixtureFile.Save(path, fixture);
                        generated = true;
                    }
                    else
                    {
                        fixture = JkrFixtureFile.Load(path);
                    }
                    var errors = JkrFixtureFile.Validate(fixture);
                    if (errors.Count > 0) return ((ModelCheckResponse)null, fixture.Issues.Count, generated, errors);
                    return (JkrFixtureScanner.Scan(doc, fixture), fixture.Issues.Count, generated, errors);
                });
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA JKR Compliance — TEST MODE", Jkr.Panel.Core.PaneText.FixtureUnusable(path, ex.Message));
                return null;
            }

            if (outcome.Errors.Count > 0)
            {
                TaskDialog.Show("BINA JKR Compliance — TEST MODE",
                    Jkr.Panel.Core.PaneLanguage.Pick("Fail fixture ada masalah:", "Fixture has problems:") + $"\n{path}\n\n" + string.Join("\n", outcome.Errors.Take(15)));
                return null;
            }
            if (outcome.Generated)
                _vm.ShowToast(Jkr.Panel.Core.PaneText.FixtureWritten(outcome.Count, path));
            return outcome.Response;
        }

        private void StartRescanSpin()
        {
            if (_rescanSpin != null) return;
            var rt = RescanIconRoot.RenderTransform as RotateTransform;
            if (rt == null)
            {
                rt = new RotateTransform(0);
                RescanIconRoot.RenderTransform = rt;
                RescanIconRoot.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            }
            var anim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1000))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            _rescanSpin = new Storyboard();
            Storyboard.SetTarget(anim, RescanIconRoot);
            Storyboard.SetTargetProperty(anim, new PropertyPath("RenderTransform.Angle"));
            _rescanSpin.Children.Add(anim);
            _rescanSpin.Begin();
        }
        private void StopRescanSpin()
        {
            if (_rescanSpin == null) return;
            _rescanSpin.Stop(); _rescanSpin = null;
            var rt = RescanIconRoot.RenderTransform as RotateTransform;
            if (rt != null) rt.Angle = 0;
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            _vm.Search = SearchInput.Text;
        }
        private void SearchClear_Click(object sender, MouseButtonEventArgs e)
        {
            SearchInput.Text = "";
            _vm.Search = "";
        }

        /// <summary>
        /// "Clear filters" link in the filtered-empty state (see RenderAll). Resets
        /// search/severity/category through the VM setters — not private fields —
        /// so Refresh()/RebuildAll() fire and the list/category chips resync.
        /// </summary>
        private void EmptyClearFilters_Click(object sender, MouseButtonEventArgs e)
        {
            SearchInput.Text = "";
            _vm.Search = "";
            _vm.ActiveSeverity = null;
            _vm.ActiveCategory = null;
        }

        private void Pill_Picked(object sender, EventArgs e)
        {
            if (sender is CategoryPill p && p.DataContext is CategoryVm c)
                _vm.ActiveCategory = c.IsAll ? null : c.Label;
        }

        private void TabOpen_Click(object s, RoutedEventArgs e) => SetTab(TabKind.Open);
        private void TabIgnore_Click(object s, RoutedEventArgs e) => SetTab(TabKind.Ignored);
        private void TabResolved_Click(object s, RoutedEventArgs e) => SetTab(TabKind.Resolved);
        private void TabManual_Click(object s, RoutedEventArgs e) => SetTab(TabKind.Manual);

        private void SetTab(TabKind tab)
        {
            _vm.Tab = tab;
            TabOpen.IsChecked = tab == TabKind.Open;
            TabIgnore.IsChecked = tab == TabKind.Ignored;
            TabResolved.IsChecked = tab == TabKind.Resolved;
            TabManual.IsChecked = tab == TabKind.Manual;
        }

        private void Row_Clicked(object sender, EventArgs e)
        {
            if (sender is IssueRow r && r.DataContext is IssueVm v)
            {
                _vm.ActiveIssue = v;
            }
        }
        private void Row_Action(object sender, IssueRowActionArgs e)
        {
            _vm.ActiveIssue = e.Issue;
            DispatchAction(e.Issue, e.NewStatus, advance: false);
        }

        /// <summary>
        /// Public entry point for the Focus modal (ApplyFix/Ignore/Approve/Reopen/Locate).
        /// Keeps all side-effect routing (backend fix, audit persistence) in one place.
        /// </summary>
        internal void InvokeAction(IssueVm issue, IssueStatus newStatus, bool advance)
            => DispatchAction(issue, newStatus, advance);

        /// <summary>Deep-link the current issue into Revit: show and select its
        /// element inside a Revit job. Every case that can't simply be shown says
        /// why in a toast:
        ///  - the issue is on a TYPE (no geometry): its placed instances are shown,
        ///    or "no placed instances";
        ///  - Revit finds no view that shows it (ShowElements throws): still selected;
        ///  - it is hidden in the view Revit picked (element or category hidden).</summary>
        internal async Task LocateInRevitAsync(IssueVm issue)
        {
            if (issue == null || issue.RevitElementId <= 0)
            {
                _vm.ShowToast(new JkrLocateResult(JkrLocateKind.NoElement).Message);
                return;
            }
            var elementId = issue.RevitElementId;
            var locateCount = issue.LocateCount;
            var label = Jkr.Panel.Core.ResultsAdapter.ElementLabel(issue);
            try
            {
                var result = await Revit.InvokeAsync(app =>
                {
                    var uiDoc = app.ActiveUIDocument;
                    if (uiDoc == null) return new JkrLocateResult(JkrLocateKind.NoDocument);
                    var doc = uiDoc.Document;
                    var id = ElemIds.From(elementId);
                    var elem = doc.GetElement(id);
                    if (elem == null) return new JkrLocateResult(JkrLocateKind.Missing);

                    // A view (BIM010 rows such as C1.1 list the views themselves):
                    // open it. ShowElements on a view raises Revit's own "No good
                    // view could be found". A view template can't be opened.
                    if (elem is View issueView)
                    {
                        if (issueView.IsTemplate)
                            return new JkrLocateResult(JkrLocateKind.ViewTemplate, 1, issueView.Name);
                        try { uiDoc.ActiveView = issueView; }
                        catch (Exception viewEx)
                        {
                            return new JkrLocateResult(JkrLocateKind.NotShown, 1, viewEx.Message);
                        }
                        return new JkrLocateResult(JkrLocateKind.OpenedView, locateCount, issueView.Name);
                    }

                    // Nothing to draw (e.g. Project Information, a material): select
                    // it and say so, instead of Revit's "No good view" box. Levels,
                    // grids and view-specific elements still go through ShowElements.
                    if (!(elem is ElementType) && !(elem is DatumPlane)
                        && (elem.OwnerViewId == null || elem.OwnerViewId == ElementId.InvalidElementId)
                        && elem.get_BoundingBox(null) == null)
                    {
                        try { uiDoc.Selection.SetElementIds(new List<ElementId> { id }); } catch { }
                        return new JkrLocateResult(JkrLocateKind.NoGeometry, 1, string.IsNullOrEmpty(elem.Name) ? elem.Category?.Name ?? "This element" : elem.Name);
                    }

                    List<ElementId> targets;
                    var shownKind = JkrLocateKind.Shown;
                    if (elem is ElementType)
                    {
                        targets = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                            .Where(e => e.GetTypeId() == id).Select(e => e.Id).Take(1000).ToList();
                        if (targets.Count == 0)
                            return new JkrLocateResult(JkrLocateKind.TypeNotPlaced, 0, elem.Name);
                        shownKind = JkrLocateKind.ShownTypeInstances;
                    }
                    else
                    {
                        targets = new List<ElementId> { id };
                    }

                    // No open view shows it: ShowElements would raise Revit's own modal boxes
                    // ("There is no open view that shows…", then "No good view could be
                    // found"). Select it only, and say so in the pane (live F12).
                    if (!AnyOpenViewShows(uiDoc, targets))
                    {
                        try { uiDoc.Selection.SetElementIds(targets); } catch { }
                        return new JkrLocateResult(JkrLocateKind.NotVisible, targets.Count, label);
                    }

                    try
                    {
                        uiDoc.ShowElements(targets);
                    }
                    catch (Exception showEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BINA] locate: ShowElements failed for {elementId}: {showEx.Message}");
                        try { uiDoc.Selection.SetElementIds(targets); } catch { }
                        return new JkrLocateResult(JkrLocateKind.NotShown, targets.Count, showEx.Message);
                    }
                    uiDoc.Selection.SetElementIds(targets);

                    if (shownKind == JkrLocateKind.Shown)
                    {
                        var view = uiDoc.ActiveView;
                        bool hidden = false;
                        try
                        {
                            hidden = elem.IsHidden(view)
                                     || (elem.Category != null && view.GetCategoryHidden(elem.Category.Id));
                        }
                        catch { }
                        if (hidden) return new JkrLocateResult(JkrLocateKind.HiddenInView, 1, view.Name);
                    }
                    return new JkrLocateResult(shownKind, targets.Count, elem.Name);
                });
                // prototype: "Selected in model: <element>"; otherwise what Locate managed.
                _vm.ShowToast(result.Message ?? Jkr.Panel.Core.PaneText.SelectedInModel(Jkr.Panel.Core.ResultsAdapter.ElementLabel(issue)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] locate failed: {ex.Message}");
                _vm.ShowToast(Jkr.Panel.Core.PaneText.LocateFailed(ex.Message));
            }
        }

        /// <summary>Whether a view open in Revit shows any of <paramref name="targets"/>
        /// (visible in it: not hidden, category on). Sheets and schedules are skipped. When
        /// Revit cannot tell (no open views listed), true: Locate keeps ShowElements.</summary>
        private static bool AnyOpenViewShows(UIDocument uiDoc, ICollection<ElementId> targets)
        {
            var doc = uiDoc.Document;
            IList<UIView> open;
            try { open = uiDoc.GetOpenUIViews(); }
            catch { return true; }
            if (open == null || open.Count == 0) return true;
            foreach (var uv in open)
            {
                try
                {
                    if (!(doc.GetElement(uv.ViewId) is View v) || v.IsTemplate || v is ViewSheet || v is ViewSchedule) continue;
                    var n = new FilteredElementCollector(doc, v.Id)
                        .WherePasses(new ElementIdSetFilter(targets))
                        .GetElementCount();
                    if (n > 0) return true;
                }
                catch { /* a view that cannot be searched (e.g. a legend): next */ }
            }
            return false;
        }

        /// <summary>
        /// Route a status transition to the correct side-effect:
        ///   Fixed        → queue into App.JkrRenameHandler + fire ExternalEvent; flip VM on success only.
        ///   Ignored/Approved → in-memory flip + persist to .jkr_audit.json.
        ///   Open (reopen)     → in-memory flip + remove from audit file.
        /// </summary>
        private void DispatchAction(IssueVm issue, IssueStatus newStatus, bool advance)
        {
            if (issue == null) return;

            // An item that needs a person has no AI verdict: no fix, ignore,
            // approve or mark-manual (keyboard F / A included). "Mark as
            // reviewed" arrives with the Manual view.
            if (issue.NeedsPerson && newStatus != IssueStatus.Open)
            {
                _vm.ShowToast(Jkr.Panel.Core.PaneText.NeedsPerson());
                return;
            }

            if (newStatus == IssueStatus.Fixed)
            {
                _ = ApplyAutoFixAsync(issue, advance);
                return;
            }

            // Ignore / Approve / Reopen are in-memory plus audit persistence.
            _vm.ApplyAction(issue, newStatus, advance);
            try
            {
                JkrAuditStore.Save(_scannedDocPath, issue);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] audit save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Queue the backend-supplied fix into the JkrRenameHandler and fire the
        /// ExternalEvent so Revit runs the edit on its main thread. Only flip the
        /// VM status to Fixed if the transaction reports success. One undo step in
        /// Revit ("JKR Auto-fix").
        /// </summary>
        private async Task ApplyAutoFixAsync(IssueVm issue, bool advance)
        {
            if (App.JkrRenameHandler == null || App.JkrRenameEvent == null)
            {
                TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.AutoFixUnavailable());
                return;
            }
            if (string.IsNullOrEmpty(issue.FixAction))
            {
                TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.NoFixData());
                return;
            }
            if (_fixInFlight)
            {
                _vm.ShowToast(Jkr.Panel.Core.PaneText.FixRunning());
                return;
            }

            var isRename = issue.FixAction.Equals("rename_type", StringComparison.OrdinalIgnoreCase);
            if (isRename && string.IsNullOrEmpty(issue.FixValue))
            {
                // Checked before taking the in-flight lock: returning after taking it
                // used to block every later fix until Revit restarted.
                TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.RenameTargetEmpty());
                return;
            }

            // No write without the confirm dialog.
            if (!await ConfirmFixesAsync(new List<IssueVm> { issue }))
                return;
            if (_fixInFlight)
            {
                _vm.ShowToast(Jkr.Panel.Core.PaneText.FixRunning());
                return;
            }

            // Build the queue for the existing rename/param-fix pipeline.
            var handler = App.JkrRenameHandler;
            handler.RenameQueue.Clear();
            handler.ParamFixQueue.Clear();
            handler.TransactionGroupName = "JKR Auto-fix";
            _fixInFlight = true;

            if (isRename)
            {
                handler.RenameQueue.Add((issue.RevitElementId, issue.FixValue));
            }
            else
            {
                // set_parameter or set_jkr_code — use the generic ParamFix path.
                handler.ParamFixQueue.Add(new JkrFixAction
                {
                    Action = issue.FixAction,
                    ElementId = issue.RevitElementId,
                    ParameterName = issue.FixParameterName,
                    Value = issue.FixValue,
                    OldValue = issue.FixOldValue,
                    Priority = issue.FixPriority,
                    Target = string.IsNullOrEmpty(issue.FixTarget) ? "instance" : issue.FixTarget,
                });
            }

            ShowApplying(JkrFixBatch.BusyLabel(1));

            // Callback runs on the main Revit thread inside the ExternalEvent completion.
            handler.OnCompleted = (result) =>
            {
                // Marshal back to the UI dispatcher — ExternalEvent callbacks return on Revit's thread.
                Dispatcher.Invoke(() =>
                {
                    _fixInFlight = false;
                    HideApplying();

                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        TaskDialog.Show(Jkr.Panel.Core.PaneText.DialogTitle, Jkr.Panel.Core.PaneText.AutoFixError(result.Error));
                        return;
                    }
                    if (result.Renamed == 0 && result.ParamFixed == 0)
                    {
                        // Auto-fix couldn't apply — most often a read-only Revit param.
                        // The issue stays Open and not compliant, with the reason on its
                        // row and in the detail view; the blocklist keeps that reason
                        // across re-scans and hides the Fix button. Not moved to Manual.
                        var why = result.ReasonFor(issue.FixAction, issue.RevitElementId, issue.FixParameterName)
                                  ?? RenameResult.UnknownFailureReason;
                        IssueMapper.BlockFix(issue.RevitElementId, issue.FixAction, issue.FixParameterName, why);
                        issue.AutoFixable = false;
                        issue.FixError = why;
                        _vm.ShowToast(Jkr.Panel.Core.PaneText.FixFailedNothingWritten());
                        RenderAll();
                        return;
                    }
                    // Written. The row is Fixed only once the rule re-passes (AI-427).
                    issue.FixError = "";
                    _ = FinishSingleFixAsync(issue, advance);
                });
            };

            await RaiseWriteAsync();
        }

        private void Export_Click(object s, RoutedEventArgs e) => _vm.ExportOpen = true;

        /// <summary>
        /// Reset = back to Start (prototype reset; agreed for step 6): clears the
        /// results, the Ignore / Manual decisions (audit file) and the failed-fix
        /// memory. It writes NOTHING to the model — fixes are undone in Revit with
        /// Ctrl+Z ("JKR Fix all" / "JKR Auto-fix"). Until the Start screen arrives
        /// (step 8) the pane shows an empty list and the toast says what to do.
        /// </summary>
        private void Reset_Click(object s, RoutedEventArgs e)
        {
            if (_vm.Scanning || _fixInFlight)
            {
                _vm.ShowToast(Jkr.Panel.Core.PaneText.WaitThenReset());
                return;
            }

            IssueMapper.ClearBlocklist();
            if (!string.IsNullOrEmpty(_scannedDocPath))
            {
                JkrAuditStore.Clear(_scannedDocPath);
            }
            _vm.Summary = null;
            _vm.Spec = null;
            _vm.ReplaceIssues(new List<IssueVm>());
            ResetDone?.Invoke();
            _vm.ShowToast(Jkr.Panel.Core.PaneText.ResetDone());
        }

        private async void CloseBtn_Click(object s, RoutedEventArgs e)
        {
            try
            {
                await Revit.InvokeAsync(app =>
                    app.GetDockablePane(JkrComplianceDashboardHost.PaneId)?.Hide());
            }
            catch { /* pane may not be visible, or BINA is still loading */ }
        }

        // ────────────────────────────────────────────────
        // Keyboard
        // ────────────────────────────────────────────────

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.TextBox)
            {
                if (e.Key == Key.Escape)
                {
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
                return;
            }

            var active = _vm.ActiveIssue;
            int idx = active == null ? -1 : _vm.Filtered.IndexOf(active);

            switch (e.Key)
            {
                case Key.Escape:
                    if (_vm.ExportOpen) { _vm.ExportOpen = false; e.Handled = true; }
                    break;
                case Key.J:
                case Key.Down:
                    if (idx + 1 < _vm.Filtered.Count) { _vm.ActiveIssue = _vm.Filtered[idx + 1]; e.Handled = true; }
                    break;
                case Key.K:
                case Key.Up:
                    if (idx > 0) { _vm.ActiveIssue = _vm.Filtered[idx - 1]; e.Handled = true; }
                    break;
                case Key.F:
                    if (active != null && active.IsActionable && active.AutoFixable)
                    {
                        DispatchAction(active, IssueStatus.Fixed, false);
                        e.Handled = true;
                    }
                    break;
                case Key.A:
                    if (active != null && active.IsOpen && active.CanIgnore)
                    {
                        DispatchAction(active, IssueStatus.Ignored, false);
                        e.Handled = true;
                    }
                    break;
                // Key.X (approve) removed — Ignore and Approve are merged per user feedback.
                case Key.OemQuestion: // '/' key
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                    {
                        SearchInput.Focus(); Keyboard.Focus(SearchInput);
                        e.Handled = true;
                    }
                    break;
                case Key.R:
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
                    {
                        _ = RunScanAsync(); e.Handled = true;
                    }
                    break;
            }
        }

        // ────────────────────────────────────────────────
        // Export window
        // ────────────────────────────────────────────────

        private void OpenExportWindow()
        {
            // One dialog at a time: OpenExport opens it directly, and the RenderAll that
            // ExportOpen queues runs inside its ShowDialog, before it is loaded (live R4).
            if (_exportWindow != null) { try { _exportWindow.Activate(); } catch { } return; }
            // The preview's Excel / CSV choice applies to this opening only: the pane's own
            // Export button then opens the dialog with the Excel default.
            var format = _exportFormat;
            _exportFormat = Jkr.Export.IssueExportFormat.Excel;
            var owner = _exportOwner;
            _exportOwner = null;
            var w = new Jkr.Modals.ExportWindow(_vm, format);
            _exportWindow = w;
            w.Closed += (_, __) => { _vm.ExportOpen = false; if (ReferenceEquals(_exportWindow, w)) _exportWindow = null; };
            if (!Jkr.Modals.RevitWindowCover.Cover(w, this))
                Jkr.Modals.RevitWindowCover.CenterOnRevit(w, this);
            // From the Borang preview: owned by it (over it, and back to it on close).
            if (owner != null && owner.IsLoaded)
            {
                try { new System.Windows.Interop.WindowInteropHelper(w).Owner = new System.Windows.Interop.WindowInteropHelper(owner).Handle; }
                catch { /* stays owned by Revit's window */ }
            }
            Jkr.Modals.RevitWindowCover.ReturnToOwnerOnClose(w);
            try { w.ShowDialog(); }
            finally
            {
                // never left set by a dialog that did not open (the guard above would block every later one)
                if (ReferenceEquals(_exportWindow, w)) { _exportWindow = null; _vm.ExportOpen = false; }
            }
            // The file it wrote, or why not (live F14).
            _exportOutcome = w.Outcome;
            if (w.Outcome != null) _vm.ShowToast(w.Outcome);
        }

        private void CloseExportWindow()
        {
            if (_exportWindow != null)
            {
                var w = _exportWindow; _exportWindow = null;
                try { w.Close(); } catch { }
            }
        }
    }
}
