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
using RevitWebAppSync.UI.Jkr.ViewModels;

namespace RevitWebAppSync.UI
{
    public partial class JkrComplianceDashboardPanel : Page
    {
        private UIApplication _uiApp;
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

        // LOD level is selectable via the LOD ComboBox in the hero header.
        // Default is 300; user can change to 100/200/300/400/500 before scanning.
        private int SelectedLodLevel => _vm.SelectedLodLevel;

        // Discipline scope: auto-detected from the model on first scan, overridable
        // by the user via DisciplineCombo. Once the user picks, stop auto-syncing.
        private bool _disciplineUserSet;
        private bool _syncingDiscipline;

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
            // First Re-scan triggers the real pipeline once SetRevitApp() has wired _uiApp.
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

        public void SetRevitApp(UIApplication uiApp) => _uiApp = uiApp;

        // Live UIApplication: prefer the one injected by the ribbon command, but fall
        // back to App.UiApp (captured on Idling) so the pane still works when Revit
        // auto-restores it on startup without the command ever running. Mirrors the
        // Copilot panel's `_uiApp ?? App.UiApp` pattern.
        private UIApplication UiAppLive => _uiApp ?? App.UiApp;

        private string ActiveDocPath => UiAppLive?.ActiveUIDocument?.Document?.PathName ?? "";

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

            // Fix All badge count
            var fc = _vm.FixableCount;
            FixCountText.Text = fc.ToString();
            FixCountBadge.Visibility = fc > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            TabOpenCount.Text = _vm.OpenCount.ToString();
            TabAcceptedCount.Text = _vm.AcceptedCount.ToString();
            TabResolvedCount.Text = _vm.ResolvedCount.ToString();
            TabManualCount.Text = _vm.ManualFixCount.ToString();
            FilteredCountText.Text = $"{_vm.FilteredCount} shown";

            TabOpen.IsChecked = _vm.IsOpenTab;
            TabAccepted.IsChecked = _vm.IsAcceptedTab;
            TabResolved.IsChecked = _vm.IsResolvedTab;
            TabManual.IsChecked = _vm.IsManualTab;

            // Highlight active tab pill, dim others
            var activeBg = JkrTheme.Brush("BrandTint");
            var activeFg = JkrTheme.Brush("BrandDark");
            var inactiveBg = JkrTheme.Brush("Surface.Line2");
            var inactiveFg = JkrTheme.Brush("Ink3");

            TabOpenPill.Background = _vm.IsOpenTab ? activeBg : inactiveBg;
            TabOpenCount.Foreground = _vm.IsOpenTab ? activeFg : inactiveFg;
            TabAcceptedPill.Background = _vm.IsAcceptedTab ? activeBg : inactiveBg;
            TabAcceptedCount.Foreground = _vm.IsAcceptedTab ? activeFg : inactiveFg;
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
            if (_vm.Filtered.Count == 0)
            {
                EmptyState.Visibility = System.Windows.Visibility.Visible;
                IssuesScroll.Visibility = System.Windows.Visibility.Collapsed;
                EmptyIcon.Glyph = _vm.IsOpenTab ? "check" : "clipboard";
                EmptyMessage.Text = _vm.IsOpenTab && _vm.ActiveCategory != null
                    ? $"No open {_vm.ActiveCategory.ToLower()} issues — nice!"
                    : _vm.IsOpenTab ? "All clear — no open issues." : "No resolved issues yet.";
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

        private void FixAll_Click(object sender, RoutedEventArgs e)
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

            var fixable = _vm.Issues
                .Where(i => i.IsActionable && i.AutoFixable && !string.IsNullOrEmpty(i.FixAction))
                .OrderBy(i => i.FixPriority)
                .ToList();

            // Diagnostic: confirm what the badge said vs. what got queued. If these
            // don't match the user's expectation, we know the drop is upstream.
            System.Diagnostics.Debug.WriteLine(
                $"[BINA FixAll] queueing — total Issues={_vm.Issues.Count} " +
                $"actionable={_vm.Issues.Count(i => i.IsActionable)} " +
                $"autoFixable={_vm.Issues.Count(i => i.IsActionable && i.AutoFixable)} " +
                $"withFixAction={fixable.Count}");

            if (fixable.Count == 0)
            {
                _vm.ShowToast("No auto-fixable issues found.");
                return;
            }

            // Captured before any status mutations so the post-fix toast can report a truthful delta.
            var openBeforeFix = _vm.OpenCount;

            // Show progress bar
            var totalToFix = fixable.Count;
            FixProgressPanel.Visibility = System.Windows.Visibility.Visible;
            FixProgressLabel.Text = "Preparing fixes...";
            FixProgressCount.Text = $"0/{totalToFix}";
            FixProgressBar.Width = 0;
            FixAllBtn.IsEnabled = false;
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

            // Update progress to "applying"
            Dispatcher.InvokeAsync(() =>
            {
                FixProgressLabel.Text = "Applying fixes in Revit...";
                FixProgressCount.Text = $"0/{totalToFix}";
            });

            handler.OnCompleted = (result) =>
            {
                Dispatcher.Invoke(() =>
                {
                    FixAllBtn.IsEnabled = true;
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

                    // ALWAYS blocklist failed fixes — even when total==0.
                    // This prevents re-scan from re-marking unfixable issues.
                    if (result.FailedFixKeys.Count > 0)
                    {
                        foreach (var issue in fixable)
                        {
                            if (IsFixedFailed(issue))
                            {
                                issue.AutoFixable = false;
                                IssueMapper.BlockFix(issue.RevitElementId, issue.FixAction, issue.FixParameterName);
                            }
                        }
                    }

                    // Mark successes as Fixed; failures move to ManualFixNeeded (own tab,
                    // hidden Fix button, persisted so they don't relapse to Open on rescan).
                    // Both groups get preserved across the rescan:
                    //   - Successes → land in Resolved (recheck omits passing checks).
                    //   - Failures   → land in Manual; if the rule still fires, audit-merge
                    //                  overlays ManualFixNeeded onto the fresh Open row.
                    foreach (var i in fixable)
                    {
                        if (IsFixedFailed(i))
                            i.Status = IssueStatus.ManualFixNeeded;
                        else
                            i.Status = IssueStatus.Fixed;
                    }
                    var preservedAfterFix = fixable.ToList();

                    // Persist Fixed (with snapshot) and ManualFixNeeded (status only) so
                    // both survive a cold Re-scan — JkrAuditStore.Save handles the routing.
                    var docPath = UiAppLive?.ActiveUIDocument?.Document?.PathName ?? "";
                    foreach (var i in preservedAfterFix)
                    {
                        if (i.Status != IssueStatus.Fixed && i.Status != IssueStatus.ManualFixNeeded)
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
                        // No undo counter increment — TransactionGroup wasn't committed.
                        // Failures already routed to Manual via preservedAfterFix above;
                        // single non-blocking toast is enough — no need for a modal dialog
                        // (the leftover detail lives on each Manual-tab row's focus modal).
                        var msg = result.Failed > 0
                            ? $"No fixes applied — {result.Failed} → Manual tab"
                            : $"No fixes applied (skipped={result.Skipped} failed={result.Failed})";
                        _vm.ShowToast(msg);
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
                    var summary = $"Fixed {total} of {totalToFix}";
                    if (result.Failed > 0)
                        summary += $" ({result.Failed} failed)";

                    // Re-scan to verify — the re-scan will show fewer issues now.
                    _ = RunScanAfterFix(summary, result.Failed, preservedAfterFix, openBeforeFix);
                });
            };

            App.JkrRenameEvent.Raise();
        }

        /// <summary>
        /// Re-scan after Fix All and show results with context about what was fixed.
        /// Uses the dedicated /jkr-recheck endpoint for clearer telemetry.
        /// </summary>
        private async Task RunScanAfterFix(string fixSummary, int failCount,
                                            List<IssueVm> preservedAfterFix = null, int openBeforeFix = 0)
        {
            try
            {
                FixProgressLabel.Text = "Re-scanning model...";
                await RunScanInner(isRecheck: true, preservedAfterFix: preservedAfterFix);
                var afterOpen = _vm.OpenCount;
                var resolved = openBeforeFix - afterOpen;

                FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;

                // Single non-blocking toast covers the whole batch outcome. Failures
                // are already routed to the Manual tab with persisted state, so the
                // user can browse them at their own pace instead of dismissing a modal.
                var msg = $"{fixSummary}. Open: {openBeforeFix} → {afterOpen}";
                if (resolved > 0)
                    msg += $" · {resolved} resolved";
                if (failCount > 0)
                    msg += $" · {failCount} → Manual tab";
                _vm.ShowToast(msg);
            }
            catch (Exception ex)
            {
                FixProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
                TaskDialog.Show("BINA JKR Compliance", $"Re-scan after fix failed:\n\n{ex.Message}");
            }
        }

        private void AcceptAll_Click(object sender, RoutedEventArgs e)
        {
            var acceptable = _vm.Issues
                .Where(i => i.Status == IssueStatus.Open && i.CanAccept)
                .ToList();

            if (acceptable.Count == 0)
            {
                _vm.ShowToast("No Medium/Low issues to accept.");
                return;
            }

            foreach (var issue in acceptable)
                _vm.ApplyAction(issue, IssueStatus.Accepted, advance: false);

            // Persist all accepted decisions to audit file
            var doc = UiAppLive?.ActiveUIDocument?.Document;
            var docPath = doc?.PathName ?? "";
            foreach (var issue in acceptable)
                JkrAuditStore.Save(docPath, issue);

            _vm.ShowToast($"Accepted {acceptable.Count} Medium/Low issues.");
        }

        private async Task RunScanAsync()
        {
            if (_vm.Scanning) return;

            var doc = UiAppLive?.ActiveUIDocument?.Document;
            if (doc == null)
            {
                TaskDialog.Show("BINA JKR Compliance", "No active Revit document. Open a model first.");
                return;
            }

            _vm.Scanning = true;
            try
            {
                await RunScanInner();
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
        /// <param name="clearAudit">If true, wipe persisted Accept/Approve decisions before loading results.</param>
        /// <param name="isRecheck">If true, hit /jkr-recheck (post-fix verification) instead of /jkr-check-v2.</param>
        /// <param name="preservedAfterFix">Issues just attempted in an auto-fix batch — both successes
        /// (Status=Fixed, land in Resolved) and failures (Status=Open, stay visible). Re-injected after
        /// the rescan so neither group silently vanishes when the recheck omits them.</param>
        private async Task RunScanInner(bool clearAudit = false, bool isRecheck = false, List<IssueVm> preservedAfterFix = null)
        {
            var doc = UiAppLive?.ActiveUIDocument?.Document;
            if (doc == null) return;

            // Optionally clear the audit file so everything starts fresh
            if (clearAudit)
            {
                var auditPath = JkrAuditStore.AuditPath(doc.PathName ?? "");
                try { if (System.IO.File.Exists(auditPath)) System.IO.File.Delete(auditPath); } catch { }
            }

            var extraction = JkrBuildingInfoExtractor.Extract(doc);
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

            var response = isRecheck
                ? await _jkrService.RecheckJkrComplianceAsync(request)
                : await _jkrService.CheckJkrComplianceV2Async(request, skipAi: true);
            if (!string.IsNullOrEmpty(response?.Error))
            {
                TaskDialog.Show("BINA JKR Compliance", $"Scan failed:\n\n{response.Error}");
                return;
            }

            var issues = IssueMapper.MapAll(response);

            // Only merge persisted decisions if we didn't just clear them
            if (!clearAudit)
            {
                var audit = JkrAuditStore.LoadFor(doc.PathName);
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
                foreach (var fixedVm in JkrAuditStore.LoadFixedFor(doc.PathName))
                {
                    if (string.IsNullOrEmpty(fixedVm.Id)) continue;
                    if (freshIds.Contains(fixedVm.Id))
                        JkrAuditStore.Remove(doc.PathName, fixedVm.Id);
                    else
                        issues.Add(fixedVm);
                }
                foreach (var manualVm in JkrAuditStore.LoadManualFor(doc.PathName))
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

        private void Pill_Picked(object sender, EventArgs e)
        {
            if (sender is CategoryPill p && p.DataContext is CategoryVm c)
                _vm.ActiveCategory = c.IsAll ? null : c.Label;
        }

        private void TabOpen_Click(object s, RoutedEventArgs e) => SetTab(TabKind.Open);
        private void TabAccepted_Click(object s, RoutedEventArgs e) => SetTab(TabKind.Accepted);
        private void TabResolved_Click(object s, RoutedEventArgs e) => SetTab(TabKind.Resolved);
        private void TabManual_Click(object s, RoutedEventArgs e) => SetTab(TabKind.Manual);

        private void SetTab(TabKind tab)
        {
            _vm.Tab = tab;
            TabOpen.IsChecked = tab == TabKind.Open;
            TabAccepted.IsChecked = tab == TabKind.Accepted;
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
        /// Public entry point for the Focus modal (ApplyFix/Accept/Approve/Reopen/Locate).
        /// Keeps all side-effect routing (backend fix, audit persistence) in one place.
        /// </summary>
        internal void InvokeAction(IssueVm issue, IssueStatus newStatus, bool advance)
            => DispatchAction(issue, newStatus, advance);

        /// <summary>Deep-link the current issue into the Revit 3D view.</summary>
        internal void LocateInRevit(IssueVm issue)
        {
            if (issue == null || issue.RevitElementId <= 0) return;
            var uiDoc = UiAppLive?.ActiveUIDocument;
            if (uiDoc == null) return;
            try
            {
                var ids = new List<ElementId> { new ElementId(issue.RevitElementId) };
                uiDoc.ShowElements(ids);
                uiDoc.Selection.SetElementIds(ids);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] locate failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Route a status transition to the correct side-effect:
        ///   Fixed        → queue into App.JkrRenameHandler + fire ExternalEvent; flip VM on success only.
        ///   Accepted/Approved → in-memory flip + persist to .jkr_audit.json.
        ///   Open (reopen)     → in-memory flip + remove from audit file.
        /// </summary>
        private void DispatchAction(IssueVm issue, IssueStatus newStatus, bool advance)
        {
            if (issue == null) return;

            if (newStatus == IssueStatus.Fixed)
            {
                ApplyAutoFix(issue, advance);
                return;
            }

            // Accept / Approve / Reopen are in-memory plus audit persistence.
            _vm.ApplyAction(issue, newStatus, advance);
            try
            {
                JkrAuditStore.Save(ActiveDocPath, issue);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] audit save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Queue the backend-supplied fix into the JkrRenameHandler and fire the
        /// ExternalEvent so Revit runs the edit on its main thread. Only flip the
        /// VM status to Fixed if the transaction reports success.
        /// </summary>
        private void ApplyAutoFix(IssueVm issue, bool advance)
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

            // Build the queue for the existing rename/param-fix pipeline.
            var handler = App.JkrRenameHandler;
            handler.RenameQueue.Clear();
            handler.ParamFixQueue.Clear();
            _fixInFlight = true;

            if (issue.FixAction.Equals("rename_type", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(issue.FixValue))
                {
                    TaskDialog.Show("BINA JKR Compliance", "Rename target is empty.");
                    return;
                }
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

            // Callback runs on the main Revit thread inside the ExternalEvent completion.
            handler.OnCompleted = (result) =>
            {
                // Marshal back to the UI dispatcher — ExternalEvent callbacks return on Revit's thread.
                Dispatcher.Invoke(() =>
                {
                    _fixInFlight = false;

                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        TaskDialog.Show("BINA JKR Compliance", $"Auto-fix error:\n\n{result.Error}");
                        return;
                    }
                    if (result.Renamed == 0 && result.ParamFixed == 0)
                    {
                        // Auto-fix couldn't apply — most often a read-only Revit param.
                        // Move the issue to ManualFixNeeded (Manual tab) and persist so
                        // the next rescan doesn't relapse it to Open. Show the dialog
                        // once with the underlying failure so the user knows what to
                        // fix manually; subsequent rescans won't re-prompt because the
                        // Fix button is hidden on ManualFixNeeded.
                        IssueMapper.BlockFix(issue.RevitElementId, issue.FixAction, issue.FixParameterName);
                        _vm.ApplyAction(issue, IssueStatus.ManualFixNeeded, advance);
                        try { JkrAuditStore.Save(ActiveDocPath, issue); }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[BINA] audit save failed: {ex.Message}");
                        }
                        var detail = result.Skipped + result.Failed > 0
                            ? $"skipped={result.Skipped} failed={result.Failed}"
                            : "no changes applied";
                        var msg = $"Auto-fix did not apply ({detail}). Moved to Manual tab.";
                        if (!string.IsNullOrEmpty(result.FailDetails))
                            msg += $"\n\nDetails:\n{result.FailDetails}";
                        TaskDialog.Show("BINA JKR Compliance", msg);
                        return;
                    }
                    // Success — flip the VM (advances queue + shows toast).
                    _vm.ApplyAction(issue, IssueStatus.Fixed, advance);
                    try
                    {
                        JkrAuditStore.Save(ActiveDocPath, issue);
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

            App.JkrRenameEvent.Raise();
        }

        private void Export_Click(object s, RoutedEventArgs e) => _vm.ExportOpen = true;

        private void Reset_Click(object s, RoutedEventArgs e)
        {
            int fixCount = _appliedFixes.Count;

            if (fixCount > 0)
            {
                var td = new TaskDialog("BINA JKR Compliance — Reset")
                {
                    MainInstruction = $"Reset will revert {fixCount} auto-fix" +
                                      $"{(fixCount == 1 ? "" : "es")} in the Revit model.",
                    MainContent = "The addin will run a reverse transaction that restores each " +
                                  "fixed element's original name or parameter value. This appears " +
                                  "in Revit's undo history as a single 'JKR Reset' step you can " +
                                  "undo manually if needed.\n\n" +
                                  "Any model edits you've made manually since the last fix are " +
                                  "left untouched — only the addin's auto-fixes are reversed.\n\n" +
                                  "Continue?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                if (td.Show() != TaskDialogResult.Yes) return;
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

                await RunScanInner(clearAudit: true);
                _vm.ShowToast(fixCount > 0
                    ? $"Reset complete — reverted {fixCount} fix{(fixCount == 1 ? "" : "es")} and cleared all decisions."
                    : "Reset complete — all decisions cleared.");
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
            // "JKR Reset" rather than "JKR Quick Fix All" — matches the dialog text
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

            App.JkrRenameEvent.Raise();
            return tcs.Task;
        }

        private void Undo_Click(object s, RoutedEventArgs e) => _vm.Undo();

        private void CloseBtn_Click(object s, RoutedEventArgs e)
        {
            try
            {
                var uiApp = UiAppLive;
                if (uiApp != null)
                {
                    var pane = uiApp.GetDockablePane(JkrComplianceDashboardHost.PaneId);
                    pane?.Hide();
                }
            }
            catch { /* pane may not be visible */ }
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
                    if (active != null && active.IsOpen && active.CanAccept)
                    {
                        DispatchAction(active, IssueStatus.Accepted, _vm.FocusOpen);
                        e.Handled = true;
                    }
                    break;
                // Key.X (approve) removed — Accept and Approve are merged per user feedback.
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
