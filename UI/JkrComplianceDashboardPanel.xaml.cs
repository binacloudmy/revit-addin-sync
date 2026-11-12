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
        private Jkr.Modals.IssueFocusWindow _focusWindow;
        private Jkr.Modals.ExportWindow _exportWindow;

        // Guards against rapid double-click between Fix All and per-issue Apply Fix.
        // Both paths reassign JkrRenameHandler.OnCompleted; if the second click lands
        // before the first ExternalEvent fires, the first batch's completion callback
        // is silently overwritten and the progress bar / button-disabled state desyncs.
        private bool _fixInFlight;

        // Each successful auto-fix is tracked here with its old value so Reset can
        // explicitly reverse it. We don't rely on Revit's PostCommand(ID_REVIT_UNDO)
        // anymore — testing showed it doesn't reliably revert TransactionGroup-
        // assimilated batches in this context (only 1 of N fixes would undo, even
        // with Idling/DocumentChanged-driven waits). Reverse-fix runs through the
        // same JkrRenameHandler path with values swapped, so it's symmetric and
        // appears in Revit's undo history as a single "JKR Reset" step.
        private readonly List<JkrFixAction> _appliedFixes = new List<JkrFixAction>();

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

        public JkrComplianceDashboardPanel()
        {
            JkrTheme.EnsureLoaded();
            InitializeComponent();

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
        }

        // ────────────────────────────────────────────────
        // Event plumbing
        // ────────────────────────────────────────────────

        private bool _renderQueued;

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
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

            // Focus modal
            if (_vm.FocusOpen && _vm.ActiveIssue != null)
            {
                OpenFocusWindow();
            }
            else
            {
                CloseFocusWindow();
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

        private async void FixAll_Click(object sender, RoutedEventArgs e)
        {
            if (App.JkrRenameHandler == null || App.JkrRenameEvent == null)
            {
                TaskDialog.Show("BINA JKR Compliance", "Auto-fix unavailable — JkrRenameHandler not initialised.");
                return;
            }
            if (_fixInFlight)
            {
                _vm.ShowToast("A fix is already running — please wait.");
                return;
            }

            // Manual entries are retried too: a fix that failed for an
            // environmental reason (e.g. shared-param definition missing before
            // the definition-creation applicator landed) succeeds on a later
            // attempt. A fix that fails again just returns to Manual.
            bool Retryable(IssueVm i) =>
                i.IsActionable || i.Status == IssueStatus.ManualFixNeeded;

            var fixable = _vm.Issues
                .Where(i => Retryable(i) && i.AutoFixable && !string.IsNullOrEmpty(i.FixAction)
                            && !IsEmptyRename(i))
                .ToList();

            // Issues without a deterministic fix: ask the backend's AI fixer
            // (/jkr-autofix) for validated FixActions before the batch runs, so
            // one Fix All click covers both. Failure here degrades to the
            // deterministic-only set — it never blocks Fix All.
            var aiCandidates = _vm.Issues
                .Where(i => Retryable(i) && !i.AutoFixable && i.RevitElementId > 0
                            && !string.IsNullOrEmpty(i.CheckId))
                .ToList();

            if (aiCandidates.Count > 0 && _lastScanRequest != null)
            {
                _fixInFlight = true;   // block re-entry while the AI request is in flight
                FixAllBtn.IsEnabled = false;
                FixProgressPanel.Visibility = System.Windows.Visibility.Visible;
                FixProgressLabel.Text = $"AI analysing {aiCandidates.Count} issue{(aiCandidates.Count == 1 ? "" : "s")}…";
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
                    _vm.ShowToast("AI fixes unavailable — applying standard fixes only.");
                }
                finally
                {
                    _fixInFlight = false;   // ApplyFixBatch re-takes the lock below
                    FixAllBtn.IsEnabled = true;
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
                _vm.ShowToast("No auto-fixable issues found.");
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
                TaskDialog.Show("BINA JKR Compliance", $"Fix All error:\n\n{ex.Message}");
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
            var request = new JkrAutoFixRequest
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
            };

            var response = await _jkrService.RequestAiFixesAsync(request);
            if (!string.IsNullOrEmpty(response?.Error))
            {
                System.Diagnostics.Debug.WriteLine($"[BINA AiFix] backend error: {response.Error}");
                _vm.ShowToast("AI fixes unavailable — applying standard fixes only.");
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
            FixProgressPanel.Visibility = System.Windows.Visibility.Visible;
            FixProgressLabel.Text = label;
            FixProgressCount.Text = "";
            FixProgressBar.Width = 0;
            SetWriteButtonsEnabled(false);
        }

        private void HideApplying()
        {
            FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
            SetWriteButtonsEnabled(true);
        }

        /// <summary>Let WPF lay out and render the busy state, then hand the queued
        /// batch to Revit. Returns false (and clears the busy state) if Revit refused
        /// the event, so the pane never stays stuck in "Applying…".</summary>
        private async Task<bool> RaiseWriteAsync()
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            var request = App.JkrRenameEvent.Raise();
            if (request == ExternalEventRequest.Accepted || request == ExternalEventRequest.Pending)
                return true;

            App.JkrRenameHandler.RenameQueue.Clear();
            App.JkrRenameHandler.ParamFixQueue.Clear();
            App.JkrRenameHandler.OnCompleted = null;
            _fixInFlight = false;
            HideApplying();
            _vm.ShowToast("Revit is busy. Close any open dialog or command and try again.");
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

                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
                        TaskDialog.Show("BINA JKR Compliance", $"Fix All error:\n\n{result.Error}");
                        return;
                    }

                    var total = result.Renamed + result.ParamFixed;

                    // Use FailedFixKeys (per-fix), not FailedElementIds (per-element).
                    // When element X has 3 fixes (rename + 2 paramfixes) and only the
                    // Kod_Jenis paramfix fails, only that one fix is in FailedFixKeys.
                    // The element-id-based check would over-flag the rename and other
                    // paramfix as failed, taint Resolved/Manual classification, and
                    // exclude truly-successful fixes from _appliedFixes — which then
                    // can't reverse them on Reset, leaving them stuck in the model.
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
                        _vm.ShowToast("No fixes applied. Nothing was written to the model.");
                        ShowFailedFixes(result, applied: 0);
                        return;
                    }

                    // Track each successful fix so Reset can explicitly reverse it later.
                    // Skip per fix-key (not element-id) so that successful fixes on an
                    // element which had ONE failed fix elsewhere still get tracked.
                    foreach (var i in fixable)
                    {
                        if (IsFixedFailed(i)) continue;
                        _appliedFixes.Add(new JkrFixAction
                        {
                            Action = i.FixAction,
                            ElementId = i.RevitElementId,
                            ParameterName = i.FixParameterName,
                            Value = i.FixValue,
                            OldValue = i.FixOldValue,
                            Priority = i.FixPriority,
                            Target = string.IsNullOrEmpty(i.FixTarget) ? "instance" : i.FixTarget,
                        });
                    }

                    // Show completion in progress bar
                    FixProgressLabel.Text = $"Applied {total} fixes. Re-scanning...";
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
                    var summary = $"Fixed {total} of {totalToFix}";
                    if (failCount > 0)
                        summary += $" ({failCount} failed)";

                    // Re-scan to verify — the re-scan will show fewer issues now.
                    _ = RunScanAfterFix(summary, failCount, preservedAfterFix, openBeforeFix, result);
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

        /// <summary>Ask before writing these fixes: "Modify the model?" with one row
        /// per change, and the "Revit may pause" line from 500 affected elements.
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

        /// <summary>Ask before Reset reverts the add-in's fixes ("Revert the model?").</summary>
        private Task<bool> ConfirmRevertAsync()
        {
            var reverts = _appliedFixes.AsEnumerable().Reverse()
                .Where(af => !string.Equals(af.Action, "rename_type", StringComparison.OrdinalIgnoreCase)
                             || !string.IsNullOrEmpty(af.OldValue))
                .ToList();
            var rows = reverts.Select(af => new JkrConfirmRow(
                string.Equals(af.Action, "rename_type", StringComparison.OrdinalIgnoreCase) ? "Type name" : af.ParameterName,
                $"Element {af.ElementId}", af.Value, af.OldValue)).ToList();
            var renames = reverts.Where(af => string.Equals(af.Action, "rename_type", StringComparison.OrdinalIgnoreCase))
                                 .Select(af => af.ElementId).ToList();
            var fixes = reverts.Where(af => !string.Equals(af.Action, "rename_type", StringComparison.OrdinalIgnoreCase)).ToList();
            return ConfirmWriteAsync(JkrConfirmKind.Revert, rows, renames, fixes);
        }

        /// <summary>Count the affected elements in Revit (instances of renamed types
        /// included, stopping at 500), then show the confirm dialog. The in-flight
        /// lock is held while counting and while the dialog is open, so a second
        /// click can't start another write. Nothing is written here.</summary>
        private async Task<bool> ConfirmWriteAsync(JkrConfirmKind kind, List<JkrConfirmRow> rows,
                                                   List<long> renames, List<JkrFixAction> fixes)
        {
            if (rows.Count == 0) return false;
            if (_fixInFlight) { _vm.ShowToast("A fix is already running — please wait."); return false; }

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

                var model = new JkrConfirmModel(kind, rows, affected, lang: "en");
                return JkrConfirmWindow.Ask(model, this);
            }
            finally
            {
                _fixInFlight = false;
            }
        }

        /// <summary>After a batch, list every fix that failed (what and why) in one
        /// dialog, so nothing fails silently. The same reasons stay on the issues'
        /// rows and in their detail views. No dialog when nothing failed.</summary>
        private static void ShowFailedFixes(RenameResult result, int applied)
        {
            var failed = result.Failed + result.Skipped;
            if (failed == 0) return;
            var head = applied > 0
                ? $"{applied} change{(applied == 1 ? "" : "s")} applied as one undo step. " +
                  $"{failed} fix{(failed == 1 ? "" : "es")} failed and {(failed == 1 ? "was" : "were")} not written:"
                : $"{failed} fix{(failed == 1 ? "" : "es")} failed. Nothing was written to the model:";
            TaskDialog.Show("BINA JKR Compliance",
                head + "\n\n" + result.FailDetails +
                "\n\nThese issues stay Open, with the reason shown on each row.");
        }

        /// <summary>
        /// Re-scan after Fix All and show results with context about what was fixed.
        /// Uses the dedicated /jkr-recheck endpoint for clearer telemetry.
        /// </summary>
        private async Task RunScanAfterFix(string fixSummary, int failCount,
                                            List<IssueVm> preservedAfterFix = null, int openBeforeFix = 0,
                                            RenameResult fixResult = null)
        {
            try
            {
                FixProgressLabel.Text = "Re-scanning model...";
                await RunScanInner(isRecheck: true, preservedAfterFix: preservedAfterFix);
                var afterOpen = _vm.OpenCount;
                var resolved = openBeforeFix - afterOpen;

                FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;

                // One toast covers the batch outcome. Failed fixes stay Open with their
                // reason on the row; they are also listed once in a dialog.
                var msg = $"{fixSummary}. Open: {openBeforeFix} → {afterOpen}";
                if (resolved > 0)
                    msg += $" · {resolved} resolved";
                if (failCount > 0)
                    msg += $" · {failCount} failed, still Open";
                _vm.ShowToast(msg);
                if (fixResult != null)
                    ShowFailedFixes(fixResult, applied: fixResult.Renamed + fixResult.ParamFixed);
            }
            catch (Exception ex)
            {
                FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
                TaskDialog.Show("BINA JKR Compliance", $"Re-scan after fix failed:\n\n{ex.Message}");
            }
        }

        private void IgnoreAll_Click(object sender, RoutedEventArgs e)
        {
            var ignorable = _vm.Issues
                .Where(i => i.Status == IssueStatus.Open && i.CanIgnore)
                .ToList();

            if (ignorable.Count == 0)
            {
                _vm.ShowToast("No Medium/Low issues to ignore.");
                return;
            }

            foreach (var issue in ignorable)
                _vm.ApplyAction(issue, IssueStatus.Ignored, advance: false);

            // Persist all accepted decisions to audit file
            var docPath = _scannedDocPath;
            foreach (var issue in ignorable)
                JkrAuditStore.Save(docPath, issue);

            _vm.ShowToast($"Ignored {ignorable.Count} Medium/Low issues.");
        }

        private async Task RunScanAsync()
        {
            if (_vm.Scanning) return;

            _vm.Scanning = true;
            try
            {
                if (!await RunScanInner())
                    TaskDialog.Show("BINA JKR Compliance", "No active Revit document. Open a model first.");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA JKR Compliance", $"Scan error:\n\n{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _vm.Scanning = false;
            }
        }

        /// <summary>Core scan logic — shared by RunScanAsync and RunScanAfterFix.</summary>
        /// <param name="clearAudit">If true, wipe persisted Ignore/Approve decisions before loading results.</param>
        /// <param name="isRecheck">If true, hit /jkr-recheck (post-fix verification) instead of /jkr-check-v2.</param>
        /// <param name="preservedAfterFix">Issues just attempted in an auto-fix batch — both successes
        /// (Status=Fixed, land in Resolved) and failures (Status=Open, stay visible). Re-injected after
        /// the rescan so neither group silently vanishes when the recheck omits them.</param>
        /// <returns>False when no document is open (nothing was scanned).</returns>
        private async Task<bool> RunScanInner(bool clearAudit = false, bool isRecheck = false, List<IssueVm> preservedAfterFix = null)
        {
            // Model read on Revit's thread. The extraction is plain data, so the rest
            // of the scan (HTTP, mapping, rendering) runs here on the pane.
            var snapshot = await Revit.InvokeAsync(app =>
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null) return null;
                return new ScanSnapshot
                {
                    DocPath = doc.PathName ?? "",
                    Extraction = JkrBuildingInfoExtractor.Extract(doc),
                };
            });
            if (snapshot == null) return false;
            _scannedDocPath = snapshot.DocPath;

            // Optionally clear the audit file so everything starts fresh
            if (clearAudit)
            {
                var auditPath = JkrAuditStore.AuditPath(_scannedDocPath);
                try { if (System.IO.File.Exists(auditPath)) System.IO.File.Delete(auditPath); } catch { }
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

            var response = isRecheck
                ? await _jkrService.RecheckJkrComplianceAsync(request)
                : await _jkrService.CheckJkrComplianceV2Async(request, skipAi: true);
            if (!string.IsNullOrEmpty(response?.Error))
            {
                if (response.Error == JkrComplianceService.LoginRequiredMessage)
                {
                    // Persistent state, not a dismissable dialog: a TaskDialog here
                    // would leave the empty-state banner behind it showing "All clear"
                    // once the user dismisses it (see _authRequired's doc comment).
                    _authRequired = true;
                    RenderAll();
                    return true;
                }
                TaskDialog.Show("BINA JKR Compliance", $"Scan failed:\n\n{response.Error}");
                return true;
            }
            _authRequired = false;

            var issues = IssueMapper.MapAll(response);

            // Only merge persisted decisions if we didn't just clear them
            if (!clearAudit)
            {
                var audit = JkrAuditStore.LoadFor(_scannedDocPath);
                JkrAuditStore.MergeInto(issues, audit);

                // Re-inject snapshot-backed entries from the audit. The fresh check
                // can fail to return them under their original Id for two reasons:
                //   - Fixed: the rule passes now (success — keep in Resolved tab).
                //   - Manual: the rule string shifted (e.g. LOD-level rewording) so
                //     the audit Id no longer matches the fresh row, even though the
                //     underlying problem still exists in the model.
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
                        issues.Add(fixedVm);
                }
                foreach (var manualVm in JkrAuditStore.LoadManualFor(_scannedDocPath))
                {
                    if (string.IsNullOrEmpty(manualVm.Id)) continue;
                    if (!freshIds.Contains(manualVm.Id))
                        issues.Add(manualVm);
                }
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
                        issues.Add(pf);
                }
            }

            _vm.ReplaceIssues(issues);
            return true;
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
                _vm.FocusOpen = true;
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

        private enum LocateOutcome { Shown, NoDocument, Missing }

        /// <summary>Deep-link the current issue into the Revit 3D view. The element
        /// lookup, ShowElements and selection run inside a Revit job.</summary>
        internal async Task LocateInRevitAsync(IssueVm issue)
        {
            if (issue == null || issue.RevitElementId <= 0)
            {
                _vm.ShowToast("This issue has no linked element to locate.");
                return;
            }
            var elementId = issue.RevitElementId;
            try
            {
                var outcome = await Revit.InvokeAsync(app =>
                {
                    var uiDoc = app.ActiveUIDocument;
                    if (uiDoc == null) return LocateOutcome.NoDocument;
                    var id = ElemIds.From(elementId);
                    if (uiDoc.Document.GetElement(id) == null) return LocateOutcome.Missing;
                    var ids = new List<ElementId> { id };
                    uiDoc.ShowElements(ids);
                    uiDoc.Selection.SetElementIds(ids);
                    return LocateOutcome.Shown;
                });
                if (outcome == LocateOutcome.NoDocument)
                    _vm.ShowToast("No active Revit document — open a model and try again.");
                else if (outcome == LocateOutcome.Missing)
                    _vm.ShowToast("Element no longer exists in the model — try re-scanning.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] locate failed: {ex.Message}");
                _vm.ShowToast($"Couldn't locate element in view — {ex.Message}");
            }
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
                TaskDialog.Show("BINA JKR Compliance", "Auto-fix unavailable — JkrRenameHandler not initialised.");
                return;
            }
            if (string.IsNullOrEmpty(issue.FixAction))
            {
                TaskDialog.Show("BINA JKR Compliance", "No machine-readable fix attached to this issue.");
                return;
            }
            if (_fixInFlight)
            {
                _vm.ShowToast("A fix is already running — please wait.");
                return;
            }

            var isRename = issue.FixAction.Equals("rename_type", StringComparison.OrdinalIgnoreCase);
            if (isRename && string.IsNullOrEmpty(issue.FixValue))
            {
                // Checked before taking the in-flight lock: returning after taking it
                // used to block every later fix until Revit restarted.
                TaskDialog.Show("BINA JKR Compliance", "Rename target is empty.");
                return;
            }

            // No write without the confirm dialog.
            if (!await ConfirmFixesAsync(new List<IssueVm> { issue }))
                return;
            if (_fixInFlight)
            {
                _vm.ShowToast("A fix is already running — please wait.");
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
                        TaskDialog.Show("BINA JKR Compliance", $"Auto-fix error:\n\n{result.Error}");
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
                        _vm.ShowToast("Fix failed. Nothing was written to the model.");
                        RenderAll();
                        return;
                    }
                    // Success — flip the VM (advances queue + shows toast).
                    issue.FixError = "";
                    _vm.ApplyAction(issue, IssueStatus.Fixed, advance);
                    try
                    {
                        JkrAuditStore.Save(_scannedDocPath, issue);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BINA] audit save failed: {ex.Message}");
                    }
                    // Track for Reset reversal — same shape as the Fix All loop above.
                    _appliedFixes.Add(new JkrFixAction
                    {
                        Action = issue.FixAction,
                        ElementId = issue.RevitElementId,
                        ParameterName = issue.FixParameterName,
                        Value = issue.FixValue,
                        OldValue = issue.FixOldValue,
                        Priority = issue.FixPriority,
                        Target = string.IsNullOrEmpty(issue.FixTarget) ? "instance" : issue.FixTarget,
                    });
                });
            };

            await RaiseWriteAsync();
        }

        private void Export_Click(object s, RoutedEventArgs e) => _vm.ExportOpen = true;

        private async void Reset_Click(object s, RoutedEventArgs e)
        {
            int fixCount = _appliedFixes.Count;

            // Reverting fixes writes to the model, so it goes through the confirm
            // dialog. With nothing to revert, Reset only clears decisions: no dialog.
            if (fixCount > 0)
            {
                if (_fixInFlight)
                {
                    _vm.ShowToast("A fix is still running — please wait and try Reset again.");
                    return;
                }
                if (!await ConfirmRevertAsync())
                    return;
            }

            IssueMapper.ClearBlocklist();
            _vm.ShowToast(fixCount > 0
                ? $"Resetting and reverting {fixCount} fix{(fixCount == 1 ? "" : "es")}..."
                : "Resetting all decisions and re-scanning...");
            _ = ResetAndRescan(fixCount);
        }

        private async Task ResetAndRescan(int fixCount = 0)
        {
            if (_vm.Scanning) return;
            _vm.Scanning = true;
            try
            {
                if (fixCount > 0 && App.JkrRenameHandler != null && App.JkrRenameEvent != null)
                {
                    await ReverseAppliedFixesAsync();
                }
                _appliedFixes.Clear();

                // No auto re-scan: the reverse already put the model back to its pre-fix
                // state, so restore the UI locally (clear the audit + flip everything to
                // Open) and let the user press Re-scan for fresh backend verification.
                // Avoids a full model extraction + backend round-trip on every Reset.
                RestoreToOpenLocally();
                _vm.ShowToast(fixCount > 0
                    ? $"Reset complete — reverted {fixCount} fix{(fixCount == 1 ? "" : "es")}. Click Re-scan to refresh."
                    : "Reset complete — decisions cleared. Click Re-scan to refresh.");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BINA JKR Compliance", $"Reset error:\n\n{ex.Message}");
            }
            finally
            {
                _vm.Scanning = false;
            }
        }

        /// <summary>Restore the panel to a clean "all Open" state after a Reset without
        /// hitting the backend: drop the persisted audit and flip every issue back to
        /// Open (Reset clears all decisions and reverts all fixes). The user clicks
        /// Re-scan when they want the model re-verified against the backend.</summary>
        private void RestoreToOpenLocally()
        {
            // The audit file belongs to the scanned model; no scan yet means nothing to drop.
            if (!string.IsNullOrEmpty(_scannedDocPath))
            {
                var auditPath = JkrAuditStore.AuditPath(_scannedDocPath);
                try { if (System.IO.File.Exists(auditPath)) System.IO.File.Delete(auditPath); } catch { }
            }

            var restored = _vm.Issues.ToList();
            foreach (var i in restored) i.Status = IssueStatus.Open;
            _vm.ReplaceIssues(restored);
        }

        /// <summary>
        /// Run a reverse transaction through JkrRenameHandler that undoes each tracked
        /// auto-fix by setting names/parameters back to their original values. Pumps
        /// through the same ExternalEvent path as Fix All so all the existing safety
        /// (TransactionGroup, read-only handling, type-vs-instance routing) applies.
        /// Iterates in reverse insertion order so renames-after-param-set unwind in
        /// the correct dependency order.
        /// </summary>
        private Task ReverseAppliedFixesAsync()
        {
            var tcs = new TaskCompletionSource<bool>();

            if (_fixInFlight)
            {
                _vm.ShowToast("A fix is still running — please wait and try Reset again.");
                tcs.TrySetResult(false);
                return tcs.Task;
            }

            var handler = App.JkrRenameHandler;
            handler.RenameQueue.Clear();
            handler.ParamFixQueue.Clear();
            // Override the label so this batch shows up in Revit's undo history as
            // "JKR Reset" rather than "JKR Fix all" — matches the dialog text
            // and lets the user tell Reset apart from Fix All in Edit > Undo.
            handler.TransactionGroupName = "JKR Reset";
            _fixInFlight = true;

            foreach (var af in _appliedFixes.AsEnumerable().Reverse())
            {
                if (string.Equals(af.Action, "rename_type", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(af.OldValue))
                        handler.RenameQueue.Add((af.ElementId, af.OldValue));
                }
                else
                {
                    // Swap Value and OldValue so the applicator sets the param back to
                    // its pre-fix state. Preserve Target so type-bound params still
                    // route to ElementType.
                    handler.ParamFixQueue.Add(new JkrFixAction
                    {
                        Action = af.Action,
                        ElementId = af.ElementId,
                        ParameterName = af.ParameterName,
                        Value = af.OldValue,
                        OldValue = af.Value,
                        Priority = af.Priority,
                        Target = af.Target,
                    });
                }
            }

            handler.OnCompleted = (result) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _fixInFlight = false;
                    HideApplying();
                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[BINA Reset] reverse-fix error: {result.Error}");
                    }
                    System.Diagnostics.Debug.WriteLine(
                        $"[BINA Reset] reverse-fix completed: " +
                        $"reverted {result.Renamed + result.ParamFixed} of {_appliedFixes.Count}, " +
                        $"failed={result.Failed}, skipped={result.Skipped}");
                    tcs.TrySetResult(true);
                });
            };

            // Same busy state as Fix All; the reverse batch is one "JKR Reset" undo step.
            var totalToRevert = handler.RenameQueue.Count + handler.ParamFixQueue.Count;
            ShowApplying($"Reverting {totalToRevert} change{(totalToRevert == 1 ? "" : "s")}…");

            _ = RaiseRevertAsync(tcs);
            return tcs.Task;
        }

        private async Task RaiseRevertAsync(TaskCompletionSource<bool> tcs)
        {
            // Refused by Revit: RaiseWriteAsync already cleared the busy state.
            if (!await RaiseWriteAsync()) tcs.TrySetResult(false);
        }

        private void Undo_Click(object s, RoutedEventArgs e) => _vm.Undo();

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
                    if (_vm.FocusOpen) { _vm.FocusOpen = false; e.Handled = true; }
                    else if (_vm.ExportOpen) { _vm.ExportOpen = false; e.Handled = true; }
                    break;
                case Key.Enter:
                    if (!_vm.FocusOpen && active != null) { _vm.FocusOpen = true; e.Handled = true; }
                    break;
                case Key.J:
                case Key.Down:
                case Key.Right when _vm.FocusOpen:
                    if (idx + 1 < _vm.Filtered.Count) { _vm.ActiveIssue = _vm.Filtered[idx + 1]; e.Handled = true; }
                    break;
                case Key.K:
                case Key.Up:
                case Key.Left when _vm.FocusOpen:
                    if (idx > 0) { _vm.ActiveIssue = _vm.Filtered[idx - 1]; e.Handled = true; }
                    break;
                case Key.F:
                    if (active != null && active.IsActionable && active.AutoFixable)
                    {
                        DispatchAction(active, IssueStatus.Fixed, _vm.FocusOpen);
                        e.Handled = true;
                    }
                    break;
                case Key.A:
                    if (active != null && active.IsOpen && active.CanIgnore)
                    {
                        DispatchAction(active, IssueStatus.Ignored, _vm.FocusOpen);
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
        // Focus / Export windows
        // ────────────────────────────────────────────────

        private void OpenFocusWindow()
        {
            if (_focusWindow != null && _focusWindow.IsLoaded)
            {
                try { _focusWindow.SetContext(_vm); _focusWindow.Activate(); } catch { }
                return;
            }
            try
            {
                _focusWindow = new Jkr.Modals.IssueFocusWindow();
                _focusWindow.HostPanel = this;
                _focusWindow.SetContext(_vm);
                _focusWindow.Closed += (_, __) => { _vm.FocusOpen = false; _focusWindow = null; };
                try
                {
                    var owner = Window.GetWindow(this);
                    if (owner != null) _focusWindow.Owner = owner;
                }
                catch { }
                _focusWindow.Show();
            }
            catch (Exception ex)
            {
                _focusWindow = null;
                _vm.FocusOpen = false;
                TaskDialog.Show("BINA JKR Compliance — Modal Error",
                    $"Failed to open issue detail:\n\n{ex.GetType().Name}: {ex.Message}\n\nInner: {ex.InnerException?.Message}");
            }
        }

        private void CloseFocusWindow()
        {
            if (_focusWindow != null)
            {
                var w = _focusWindow; _focusWindow = null;
                try { w.Close(); } catch { }
            }
        }

        private void OpenExportWindow()
        {
            if (_exportWindow != null && _exportWindow.IsLoaded) { _exportWindow.Activate(); return; }
            _exportWindow = new Jkr.Modals.ExportWindow(_vm, _jkrService);
            _exportWindow.Closed += (_, __) => { _vm.ExportOpen = false; _exportWindow = null; };
            try { _exportWindow.Owner = Window.GetWindow(this); } catch { }
            _exportWindow.ShowDialog();
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
