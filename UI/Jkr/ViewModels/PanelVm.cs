using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using System.Threading.Tasks;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Jkr.ViewModels
{
    public class ToastVm : INotifyPropertyChanged
    {
        public string Message { get; set; } = "";
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class UndoSnapshot
    {
        public string IssueId;
        public IssueStatus PreviousStatus;
    }

    public enum TabKind { Open, Ignored, Resolved, Manual }

    // Copilot run screens. S4 (inline detail) is an overlay on S3/S5, not a whole screen.
    public enum CopilotScreen { S1, S2, S3, S5, S6 }
    public enum CopilotTab { Open, Manual, Ignored, Resolved }

    /// <summary>A section filter chip (All, or one of A..E).</summary>
    public sealed class SectionOption
    {
        public SectionOption(string code, string label) { Code = code; Label = label; }
        public string Code { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    /// <summary>A selectable discipline for the compliance scan: backend code + UI label.</summary>
    public sealed class DisciplineOption
    {
        public DisciplineOption(string code, string label) { Code = code; Label = label; }
        public string Code { get; }
        public string Label { get; }
        public override string ToString() => Label;  // shown in the ComboBox
    }

    public class PanelVm : INotifyPropertyChanged
    {
        public ObservableCollection<IssueVm> Issues { get; } = new ObservableCollection<IssueVm>();
        public ObservableCollection<CategoryVm> Categories { get; } = new ObservableCollection<CategoryVm>();
        public ObservableCollection<IssueVm> Filtered { get; } = new ObservableCollection<IssueVm>();

        private UndoSnapshot _undo;
        private DispatcherTimer _toastTimer;

        // ─── State ───
        private string _activeCategory; // null = All
        public string ActiveCategory
        {
            get => _activeCategory;
            set
            {
                if (_activeCategory == value) return;
                _activeCategory = value;
                Refresh();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }
        }
        public string ActiveCategoryLabel => _activeCategory ?? "All";

        private string _search = "";
        public string Search
        {
            get => _search;
            set
            {
                var v = value ?? "";
                if (_search == v) return;
                _search = v;
                Refresh();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }
        }
        public bool HasSearch => !string.IsNullOrEmpty(_search);

        private TabKind _tab = TabKind.Open;
        public TabKind Tab
        {
            get => _tab;
            set
            {
                if (_tab == value) return;
                _tab = value;
                Refresh();
                // Single null-name notification covers Tab + IsOpenTab + IsIgnoredTab + IsResolvedTab.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }
        }
        public bool IsOpenTab => _tab == TabKind.Open;
        public bool IsIgnoredTab => _tab == TabKind.Ignored;
        public bool IsResolvedTab => _tab == TabKind.Resolved;
        public bool IsManualTab => _tab == TabKind.Manual;

        private IssueVm _activeIssue;
        public IssueVm ActiveIssue
        {
            get => _activeIssue;
            set { _activeIssue = value; Raise(); Raise(nameof(ActiveIndexDisplay)); Raise(nameof(QueueProgress)); }
        }

        private int? _selectedLodLevel;
        /// <summary>Detail level for the copilot run. Nullable (D5): the run is
        /// gated on a LOD having been chosen — there is no silent default.</summary>
        public int? SelectedLodLevel
        {
            get => _selectedLodLevel;
            set
            {
                if (_selectedLodLevel != value)
                {
                    _selectedLodLevel = value;
                    Raise();
                    Raise(nameof(NoLod));
                    Raise(nameof(Ready));
                    Raise(nameof(RunLabel));
                    Raise(nameof(CanRun));
                }
            }
        }
        public bool NoLod => !_selectedLodLevel.HasValue;
        public bool Ready => _selectedLodLevel.HasValue;

        /// <summary>Available LOD levels for the dropdown.</summary>
        public int[] LodLevels { get; } = { 100, 200, 300, 400, 500 };

        private string _selectedDiscipline = "AR";
        /// <summary>Discipline code (AR/CD/EL/ME/ST) the scan is scoped to.
        /// Sent to the backend as project.discipline.</summary>
        public string SelectedDiscipline
        {
            get => _selectedDiscipline;
            set { if (_selectedDiscipline != value) { _selectedDiscipline = value; Raise(); } }
        }

        /// <summary>Disciplines the user can scope a scan to (code + display label).
        /// Landscape (LD) is intentionally not offered.</summary>
        public DisciplineOption[] Disciplines { get; } =
        {
            new DisciplineOption("AR", "Architecture"),
            new DisciplineOption("CD", "Civil"),
            new DisciplineOption("EL", "Electrical"),
            new DisciplineOption("ME", "Mechanical"),
            new DisciplineOption("ST", "Structure"),
        };

        private bool _scanning;
        public bool Scanning
        {
            get => _scanning;
            set { _scanning = value; Raise(); Raise(nameof(RescanLabel)); }
        }
        public string RescanLabel => _scanning ? "Scanning…" : "Re-scan";

        private bool _focusOpen;
        public bool FocusOpen { get => _focusOpen; set { _focusOpen = value; Raise(); } }

        private ToastVm _toast;
        public ToastVm Toast { get => _toast; set { _toast = value; Raise(); Raise(nameof(HasToast)); } }
        public bool HasToast => _toast != null;

        private bool _exportOpen;
        public bool ExportOpen { get => _exportOpen; set { _exportOpen = value; Raise(); } }

        // ─── Derived ───
        public string Filename { get; set; } = "";
        public int Total => Issues.Count;
        public int OpenCount => Issues.Count(i => i.Status == IssueStatus.Open);
        public int IgnoredCount => Issues.Count(i => i.Status == IssueStatus.Ignored);
        public int ResolvedCount => Issues.Count(i => i.Status == IssueStatus.Fixed || i.Status == IssueStatus.Approved);
        public int ManualFixCount => Issues.Count(i => i.Status == IssueStatus.ManualFixNeeded);
        // Compliance progress — Manual is deliberately excluded. The user has
        // triaged it but the model is still non-compliant, so it shouldn't
        // inflate the "% resolved" indicator.
        public int NonOpenCount => IgnoredCount + ResolvedCount;
        public int Percent => Total == 0 ? 0 : (int)Math.Round(NonOpenCount * 100.0 / Total);

        public int HighOpen => Issues.Count(i => i.IsOpen && i.Priority == IssuePriority.High);
        public int MedOpen  => Issues.Count(i => i.IsOpen && i.Priority == IssuePriority.Medium);
        public int LowOpen  => Issues.Count(i => i.IsOpen && i.Priority == IssuePriority.Low);

        // Active severity filter (null = all severities). Composes with the category
        // chip, status tab, and search — all AND-ed together in Refresh().
        private IssuePriority? _activeSeverity;
        public IssuePriority? ActiveSeverity
        {
            get => _activeSeverity;
            set { _activeSeverity = value; Raise(nameof(ActiveSeverity)); RebuildAll(); }
        }
        public void ToggleSeverity(IssuePriority p) =>
            ActiveSeverity = (_activeSeverity == p) ? (IssuePriority?)null : p;

        /// <summary>Retryable by Fix All: Open/Ignored, plus Manual — a fix that
        /// failed for an environmental reason (e.g. missing shared-param
        /// definition) can succeed on a later attempt.</summary>
        private static bool FixRetryable(IssueVm i) =>
            i.IsActionable || i.Status == IssueStatus.ManualFixNeeded;

        /// <summary>Count of auto-fixable issues (Open + Ignored + Manual with a fix_action).</summary>
        public int FixableCount => Issues.Count(i => FixRetryable(i) && i.AutoFixable && !string.IsNullOrEmpty(i.FixAction));

        /// <summary>Count of issues Fix All's AI phase can send to the backend:
        /// retryable, no deterministic fix, tied to a real element, and carrying
        /// a backend check_id for the round-trip.</summary>
        public int AiFixableCount => Issues.Count(i =>
            FixRetryable(i) && !i.AutoFixable && i.RevitElementId > 0 && !string.IsNullOrEmpty(i.CheckId));

        public string SessionLine
        {
            get
            {
                if (OpenCount > 0 && ManualFixCount > 0)
                    return $"{OpenCount} open · {ManualFixCount} manual";
                if (OpenCount > 0)
                    return $"{OpenCount} issue{(OpenCount == 1 ? "" : "s")} to go";
                if (ManualFixCount > 0)
                    return $"{ManualFixCount} manual fix{(ManualFixCount == 1 ? "" : "es")} pending";
                return "All clear — nice work.";
            }
        }

        public int ActiveIndexDisplay
        {
            get
            {
                if (_activeIssue == null) return 0;
                int idx = Filtered.IndexOf(_activeIssue);
                return idx < 0 ? 0 : idx + 1;
            }
        }
        public double QueueProgress => Filtered.Count == 0 ? 0 : (double)ActiveIndexDisplay / Filtered.Count;

        public int FilteredCount => Filtered.Count;
        public bool HasFiltered => Filtered.Count > 0;

        // ─── Ctor ───
        public PanelVm()
        {
            Issues.CollectionChanged += OnIssuesChanged;
        }

        public void ReplaceIssues(IEnumerable<IssueVm> items)
        {
            Issues.CollectionChanged -= OnIssuesChanged;
            Issues.Clear();
            foreach (var i in items) Issues.Add(i);
            Issues.CollectionChanged += OnIssuesChanged;
            RebuildAll();
            if (_activeIssue == null || !Issues.Contains(_activeIssue))
                ActiveIssue = Filtered.FirstOrDefault();
        }

        private void OnIssuesChanged(object s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RebuildAll();

        private void RebuildAll()
        {
            RebuildCategories();
            Refresh();
            RaiseCounts();
        }

        private void RebuildCategories()
        {
            Categories.Clear();
            Categories.Add(new CategoryVm
            {
                Label = "All", Icon = "diamond", IsAll = true,
                OpenCount = OpenCount, TotalCount = Total,
                IsActive = _activeCategory == null,
            });
            foreach (var cat in CategoryVm.Order.Concat(
                         Issues.Select(i => i.Category)
                               .Where(c => !CategoryVm.Order.Contains(c))
                               .Distinct()))
            {
                var items = Issues.Where(i => i.Category == cat).ToList();
                if (items.Count == 0 && CategoryVm.Order.Contains(cat)) continue;
                Categories.Add(new CategoryVm
                {
                    Label = cat,
                    Icon = CategoryVm.IconMap.TryGetValue(cat, out var ic) ? ic : "diamond",
                    OpenCount = items.Count(i => i.IsOpen),
                    TotalCount = items.Count,
                    IsActive = _activeCategory == cat,
                });
            }
        }

        public void Refresh()
        {
            var q = (Search ?? "").ToLowerInvariant();

            // Build the new filtered set first, then swap — avoids per-item
            // CollectionChanged events from Clear() + Add() one-by-one.
            var next = new List<IssueVm>();
            foreach (var i in Issues)
            {
                if (ActiveCategory != null && i.Category != ActiveCategory) continue;
                if (_activeSeverity != null && i.Priority != _activeSeverity) continue;
                if (Tab == TabKind.Open && i.Status != IssueStatus.Open) continue;
                if (Tab == TabKind.Ignored && i.Status != IssueStatus.Ignored) continue;
                if (Tab == TabKind.Resolved && i.Status != IssueStatus.Fixed && i.Status != IssueStatus.Approved) continue;
                if (Tab == TabKind.Manual && i.Status != IssueStatus.ManualFixNeeded) continue;
                if (!string.IsNullOrEmpty(q)
                    && i.Title.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0
                    && i.Description.ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) < 0) continue;
                next.Add(i);
            }

            // Only touch the ObservableCollection if contents actually changed.
            bool same = next.Count == Filtered.Count;
            if (same)
            {
                for (int j = 0; j < next.Count; j++)
                {
                    if (!ReferenceEquals(next[j], Filtered[j])) { same = false; break; }
                }
            }
            if (!same)
            {
                Filtered.Clear();
                foreach (var i in next) Filtered.Add(i);
            }

            Raise(nameof(FilteredCount));
            Raise(nameof(HasFiltered));
            if (_activeIssue == null || !Filtered.Contains(_activeIssue))
                ActiveIssue = Filtered.FirstOrDefault();

            // Sync IsActive on categories
            foreach (var c in Categories)
                c.IsActive = c.IsAll ? _activeCategory == null : c.Label == _activeCategory;
        }

        public void ApplyAction(IssueVm issue, IssueStatus newStatus, bool advance)
        {
            if (issue == null) return;
            var prev = issue.Status;
            _undo = new UndoSnapshot { IssueId = issue.Id, PreviousStatus = prev };

            // capture queue neighbour before status change flips filtering
            IssueVm nextTarget = null;
            if (advance)
            {
                int idx = Filtered.IndexOf(issue);
                for (int j = idx + 1; j < Filtered.Count; j++)
                    if (Filtered[j].IsOpen) { nextTarget = Filtered[j]; break; }
                if (nextTarget == null)
                {
                    for (int j = 0; j < idx; j++)
                        if (Filtered[j].IsOpen) { nextTarget = Filtered[j]; break; }
                }
            }

            issue.Status = newStatus;

            // Toast
            var label = newStatus == IssueStatus.Open ? "Reopened" : StatusToLabel(newStatus);
            var titleShort = issue.Title.Length > 40 ? issue.Title.Substring(0, 40) + "…" : issue.Title;
            ShowToast($"{label} · {titleShort}");

            // Recompute filtered list (may drop issue from current tab)
            Refresh();
            RaiseCounts();

            if (advance)
            {
                if (nextTarget != null && Filtered.Contains(nextTarget))
                {
                    ActiveIssue = nextTarget;
                }
                else
                {
                    FocusOpen = false;
                }
            }
        }

        public void Undo()
        {
            if (_undo == null) return;
            var issue = Issues.FirstOrDefault(i => i.Id == _undo.IssueId);
            if (issue != null) issue.Status = _undo.PreviousStatus;
            _undo = null;
            Toast = null;
            Refresh();
            RaiseCounts();
        }

        internal void ShowToast(string msg)
        {
            Toast = new ToastVm { Message = msg };
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _toastTimer.Tick += (_, __) => { _toastTimer.Stop(); Toast = null; };
            _toastTimer.Start();
        }

        private static string StatusToLabel(IssueStatus s)
        {
            switch (s)
            {
                case IssueStatus.Fixed: return "Fixed";
                case IssueStatus.Ignored: return "Ignored";
                case IssueStatus.Approved: return "Approved";
                case IssueStatus.ManualFixNeeded: return "Manual fix needed";
                default: return s.ToString();
            }
        }

        private void RaiseCounts()
        {
            Raise(nameof(OpenCount)); Raise(nameof(IgnoredCount)); Raise(nameof(ResolvedCount)); Raise(nameof(ManualFixCount)); Raise(nameof(NonOpenCount)); Raise(nameof(Total));
            Raise(nameof(Percent)); Raise(nameof(HighOpen)); Raise(nameof(MedOpen)); Raise(nameof(LowOpen));
            Raise(nameof(FixableCount)); Raise(nameof(AiFixableCount)); Raise(nameof(SessionLine));
            foreach (var c in Categories)
            {
                if (c.IsAll)
                {
                    c.OpenCount = OpenCount; c.TotalCount = Total;
                    continue;
                }
                var items = Issues.Where(i => i.Category == c.Label).ToList();
                c.OpenCount = items.Count(i => i.IsOpen);
                c.TotalCount = items.Count;
            }
        }

        // ══════════════════════ JKR Audit Copilot (C14) ══════════════════════
        // Screen state machine + run surface. The SCREENS card binds XAML to these
        // members. Decisions map rule id → CellDecision (D10); every score is
        // derived deterministically by JkrCopilotMath.

        public IJkrCopilotSource CopilotSource { get; set; } = new FixtureCopilotSource();

        private CopilotScreen _screen = CopilotScreen.S1;
        public CopilotScreen CurrentScreen
        {
            get => _screen;
            private set
            {
                if (_screen == value) return;
                _screen = value;
                Raise(nameof(CurrentScreen));
                Raise(nameof(IsS1)); Raise(nameof(IsS2)); Raise(nameof(IsS3));
                Raise(nameof(IsS5)); Raise(nameof(IsS6)); Raise(nameof(CanZoom));
            }
        }
        public bool IsS1 => _screen == CopilotScreen.S1;
        public bool IsS2 => _screen == CopilotScreen.S2;
        public bool IsS3 => _screen == CopilotScreen.S3;
        public bool IsS5 => _screen == CopilotScreen.S5;
        public bool IsS6 => _screen == CopilotScreen.S6;
        public bool CanZoom => _screen == CopilotScreen.S3 || _screen == CopilotScreen.S5;

        private bool _zoomed;
        public bool IsZoomed { get => _zoomed; set { if (_zoomed != value) { _zoomed = value; Raise(); } } }

        // ── S1 run parameters ──
        private string _reportLanguage = "BM";   // D6: ms | en, default BM
        public string ReportLanguage { get => _reportLanguage; set { _reportLanguage = value; Raise(); } }
        private bool _densityComfortable;         // D7
        public bool IsDensityComfortable { get => _densityComfortable; set { _densityComfortable = value; Raise(); } }

        public IReadOnlyList<JkrCopilotPhase> Phases =>
            RunData?.Phases ?? FixtureCopilotSource.DesignPhases;
        public IReadOnlyDictionary<int, JkrCopilotLod> Lods =>
            RunData?.Lods ?? FixtureCopilotSource.DesignLods;

        private bool _isRunning;
        public bool IsRunning { get => _isRunning; private set { _isRunning = value; Raise(nameof(IsRunning)); Raise(nameof(CanRun)); } }
        /// <summary>Run is disabled (canExecute=false) until a LOD is chosen.</summary>
        public bool CanRun => !IsRunning && Ready;
        public string RunLabel => IsRunning ? "Scanning…" : (NoLod ? "Choose a detail level to run" : "Run audit");

        public async Task RunAsync()
        {
            if (!CanRun) return;
            IsRunning = true;
            _decisions = new Dictionary<string, CellDecision>();
            _detailRule = null; _detailIndex = 0; _confirm = null;
            CurrentScreen = CopilotScreen.S2;
            _activeStep = 0;
            RunProgress = BuildRunProgress(0);
            Raise(nameof(RunProgress));
            try
            {
                var req = new PanelRunRequest
                {
                    LodLevel = _selectedLodLevel,
                    Discipline = SelectedDiscipline,
                    ReportLanguage = ReportLanguage,
                    IsDensityComfortable = IsDensityComfortable,
                };
                var data = await CopilotSource.LoadRunAsync(req);
                RunData = data;
                ComputeResults(data);
                CurrentScreen = CopilotScreen.S3;
            }
            finally
            {
                IsRunning = false;
                Raise(nameof(RunLabel));
            }
        }

        /// <summary>Advance the S2 per-section scan marker (driven by the UI timer).</summary>
        public void AdvanceRunStep()
        {
            if (CurrentScreen != CopilotScreen.S2) return;
            if (_activeStep < FixtureCopilotSource.DesignSections.Count - 1)
            {
                _activeStep++;
                RunProgress = BuildRunProgress(_activeStep);
                Raise(nameof(RunProgress));
            }
        }

        public RunProgress RunProgress { get; private set; }
        private int _activeStep;

        private RunProgress BuildRunProgress(int step)
        {
            var sections = new List<RunSectionProgress>();
            int i = 0;
            foreach (var s in FixtureCopilotSource.DesignSections)
            {
                sections.Add(new RunSectionProgress
                {
                    Id = s.Id,
                    Name = s.Name,
                    Stat = i < step ? "done" : (i == step ? "scanning" : "queued"),
                });
                i++;
            }
            return new RunProgress { ActiveStep = step, Sections = sections };
        }

        // ── Results ──
        public JkrCopilotRunData RunData { get; private set; }
        public ScoreSummary Summary { get; private set; }
        public int RowsFail { get; private set; }
        public string Leverage { get; private set; }
        public IReadOnlyList<JkrCopilotRule> TopFixes { get; private set; } = Array.Empty<JkrCopilotRule>();
        public IReadOnlyList<SectionScore> Sections { get; private set; } = Array.Empty<SectionScore>();
        public IReadOnlyList<RuleGroup> Groups { get; private set; } = Array.Empty<RuleGroup>();

        private Dictionary<string, CellDecision> _decisions = new Dictionary<string, CellDecision>();

        public int OpenRuleCells => RunData == null ? 0 : JkrCopilotMath.FailedCells(RunData.Rules, _decisions);
        public int ManualRuleCells => RunData == null ? 0 : JkrCopilotMath.ManualCells(RunData.Rules, _decisions);
        public int IgnoredRuleCells => RunData == null ? 0 : JkrCopilotMath.IgnoredCells(RunData.Rules, _decisions);
        public int ResolvedRuleCells => RunData == null ? 0 : JkrCopilotMath.ResolvedCells(RunData.Rules, _decisions);

        private void ComputeResults(JkrCopilotRunData data)
        {
            Summary = JkrCopilotMath.Summary(data.Rules, _decisions, data.TotalAi);
            RowsFail = JkrCopilotMath.RowsFail(data.Rules, _decisions);
            Leverage = JkrCopilotMath.Leverage(data.Rules, _decisions);
            TopFixes = JkrCopilotMath.TopFixes(data.Rules, _decisions);
            Sections = data.Sections.Select(s => JkrCopilotMath.Section(data.Rules, _decisions, s)).ToList();
            RefreshRuleList();
            RaiseAllResults();
        }

        private void RaiseAllResults()
        {
            Raise(nameof(Summary)); Raise(nameof(RowsFail)); Raise(nameof(Leverage)); Raise(nameof(TopFixes));
            Raise(nameof(Sections)); Raise(nameof(Groups)); Raise(nameof(OpenRuleCells)); Raise(nameof(ManualRuleCells));
            Raise(nameof(IgnoredRuleCells)); Raise(nameof(ResolvedRuleCells)); Raise(nameof(FilteredRuleCount));
            Raise(nameof(HasFilteredRules));
        }

        private void RefreshAfterDecision() => ComputeResults(RunData);

        // ── Tabs / filters / list ──
        private CopilotTab _copilotTab = CopilotTab.Open;
        public CopilotTab ActiveCopilotTab
        {
            get => _copilotTab;
            set
            {
                if (_copilotTab != value) { _copilotTab = value; DetailRule = null; RefreshRuleList(); Raise(nameof(ActiveCopilotTab)); }
            }
        }
        private string _searchQuery = "";
        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                var v = value ?? "";
                if (_searchQuery == v) return;
                _searchQuery = v;
                RefreshRuleList();
                Raise(nameof(SearchQuery)); Raise(nameof(FilteredRuleCount)); Raise(nameof(HasFilteredRules));
            }
        }
        private string _selectedSection;          // null = All
        public string SelectedSection
        {
            get => _selectedSection;
            set { if (_selectedSection == value) return; _selectedSection = value; RefreshRuleList(); Raise(nameof(SelectedSection)); }
        }
        public IReadOnlyList<SectionOption> SectionOptions { get; } = new[]
        {
            new SectionOption(null, "All"),
            new SectionOption("A", "A · Penamaan"),
            new SectionOption("B", "B · Parameter"),
            new SectionOption("C", "C · Kualiti"),
            new SectionOption("D", "D · Geometri"),
            new SectionOption("E", "E · Dokumen"),
        };

        public ObservableCollection<JkrCopilotRule> FilteredRules { get; } = new ObservableCollection<JkrCopilotRule>();
        public int FilteredRuleCount => FilteredRules.Count;
        public bool HasFilteredRules => FilteredRules.Count > 0;

        private List<JkrCopilotRule> _ordered = new List<JkrCopilotRule>();
        private HashSet<string> _collapsedGroups = new HashSet<string>();

        private static readonly string[] SecOrder = { "A", "B", "C", "D", "E" };

        private void RefreshRuleList()
        {
            if (RunData == null)
            {
                _ordered.Clear();
                FilteredRules.Clear();
                Groups = Array.Empty<RuleGroup>();
                Raise(nameof(Groups)); Raise(nameof(FilteredRuleCount)); Raise(nameof(HasFilteredRules));
                return;
            }
            var source = RunData.Rules.Where(r => FitsTab(r) && FitsSection(r) && FitsQuery(r)).ToList();
            IEnumerable<JkrCopilotRule> sorted;
            if (_copilotTab == CopilotTab.Open)
                sorted = source.OrderBy(r => SecRank(r.Sec)).ThenBy(r => r.Item, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal);
            else
                sorted = JkrCopilotMath.Rank(source);
            _ordered = sorted.ToList();
            FilteredRules.Clear();
            foreach (var r in _ordered) FilteredRules.Add(r);
            Groups = BuildGroups(_ordered);
            Raise(nameof(Groups)); Raise(nameof(FilteredRuleCount)); Raise(nameof(HasFilteredRules));
        }

        private static int SecRank(string sec) { int i = Array.IndexOf(SecOrder, sec); return i < 0 ? SecOrder.Length : i; }

        private bool FitsTab(JkrCopilotRule r)
        {
            var st = JkrCopilotMath.State(r, _decisions);
            switch (_copilotTab)
            {
                case CopilotTab.Open: return r.Kind == "ai" && st == CellDecision.Open;
                case CopilotTab.Manual: return r.Kind == "manual" && st == CellDecision.Open;
                case CopilotTab.Ignored: return st == CellDecision.Ignored;
                case CopilotTab.Resolved: return st == CellDecision.Resolved || st == CellDecision.Comply;
                default: return false;
            }
        }

        private bool FitsSection(JkrCopilotRule r) => _selectedSection == null || r.Sec == _selectedSection;

        private bool FitsQuery(JkrCopilotRule r)
        {
            if (string.IsNullOrEmpty(_searchQuery)) return true;
            var q = _searchQuery.ToLowerInvariant();
            return (r.Title ?? "").ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0
                || (r.Cat ?? "").ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0
                || (r.Item ?? "").ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0
                || (r.Sec ?? "").ToLowerInvariant().IndexOf(q, StringComparison.Ordinal) >= 0;
        }

        private IReadOnlyList<RuleGroup> BuildGroups(List<JkrCopilotRule> ordered)
        {
            var list = new List<RuleGroup>();
            foreach (var g in ordered.GroupBy(r => r.Item))
            {
                var items = g.ToList();
                string sec = items[0].Sec;
                list.Add(new RuleGroup
                {
                    Item = g.Key,
                    Name = RunData.RowNames.TryGetValue(g.Key, out var nm) ? nm : g.Key,
                    Sec = sec,
                    SecName = RunData.Sections.FirstOrDefault(s => s.Id == sec)?.Name ?? "",
                    Cells = items.Sum(r => r.Cells),
                    Rows = items.Sum(r => r.Rows),
                    Crit = items.Any(r => r.Crit),
                    IsOpen = !_collapsedGroups.Contains(g.Key),
                    Rules = items,
                });
            }
            return list;
        }

        public void ToggleGroup(string item)
        {
            if (_collapsedGroups.Contains(item)) _collapsedGroups.Remove(item); else _collapsedGroups.Add(item);
            Groups = BuildGroups(_ordered);
            Raise(nameof(Groups));
        }

        // ── Detail (S4 overlay) ──
        private JkrCopilotRule _detailRule;
        private int _detailIndex;
        public JkrCopilotRule DetailRule
        {
            get => _detailRule;
            private set { _detailRule = value; Raise(nameof(DetailRule)); Raise(nameof(HasDetail)); Raise(nameof(DetailPosition)); }
        }
        public bool HasDetail => _detailRule != null;
        public string DetailPosition => _ordered.Count == 0 ? "1 / 0" : (_detailIndex + 1) + " / " + _ordered.Count;

        public void OpenDetail(JkrCopilotRule rule)
        {
            if (rule == null) return;
            int i = _ordered.IndexOf(rule);
            _detailIndex = i < 0 ? 0 : i;
            DetailRule = rule;
        }
        public void PrevDetail() => StepDetail(-1);
        public void NextDetail() => StepDetail(1);
        private void StepDetail(int dir)
        {
            if (_ordered.Count == 0) return;
            _detailIndex = (_detailIndex + dir + _ordered.Count) % _ordered.Count;
            DetailRule = _ordered[_detailIndex];
        }

        // ── Manual decide ops ──
        public void MarkComply() => Decide(CellDecision.Comply);
        public void MarkNot() => Decide(CellDecision.NotComply);
        public void MarkDefer() => Decide(CellDecision.Defer);
        public void IgnoreDetail() => Decide(CellDecision.Ignored);
        private void Decide(CellDecision d)
        {
            if (_detailRule == null) return;
            _decisions[_detailRule.Id] = d;
            DetailRule = null;
            RefreshAfterDecision();
        }

        // ── Fix / ignore confirm sheets ──
        private ConfirmRequest _confirm;
        public ConfirmRequest ConfirmRequest
        {
            get => _confirm;
            private set { _confirm = value; Raise(nameof(ConfirmRequest)); Raise(nameof(HasConfirm)); }
        }
        public bool HasConfirm => _confirm != null;

        public void FixDetail() { if (_detailRule != null) OpenConfirmOne(_detailRule.Id); }
        public void FixTop() => RequestConfirm("top");
        public void FixAll() => RequestConfirm("all");
        public void IgnoreAll() => RequestConfirm("ignoreAll");

        private void OpenConfirmOne(string id)
        {
            var r = RunData.Rules.FirstOrDefault(x => x.Id == id);
            if (r == null) return;
            string cat = (r.Cat ?? "").ToLowerInvariant();
            ConfirmRequest = new ConfirmRequest
            {
                Kind = "one", RuleId = id,
                Title = $"Change {r.Cells} {cat} in the model?",
                Body = $"{r.From} → {r.To} across {r.Cells} {cat}. Nothing is written until you confirm.",
                Note = "Writes to the open model · undoable",
                Cta = "Apply fix",
            };
        }

        private void RequestConfirm(string kind)
        {
            if (RunData == null) return;
            if (kind == "all")
            {
                var fx = JkrCopilotMath.Fixables(RunData.Rules, _decisions);
                int cells = fx.Sum(r => r.Cells), rows = fx.Sum(r => r.Rows);
                ConfirmRequest = new ConfirmRequest
                {
                    Kind = "all",
                    Title = $"Apply {fx.Count} auto-fixes?",
                    Body = $"Touches {JkrCopilotMath.Fmt(cells)} cells across {fx.Count} rules and clears {rows} Borang rows. Every change is written to the open model.",
                    Note = "Chargeable generate action · one run",
                    Cta = "I consent, run it",
                };
            }
            else if (kind == "top")
            {
                var top = JkrCopilotMath.TopFixes(RunData.Rules, _decisions);
                int cells = top.Sum(r => r.Cells), rows = top.Sum(r => r.Rows);
                ConfirmRequest = new ConfirmRequest
                {
                    Kind = "top",
                    Title = $"Apply {top.Count} auto-fixes?",
                    Body = $"Touches {JkrCopilotMath.Fmt(cells)} cells and clears {rows} Borang rows.",
                    Note = "Chargeable generate action · one run",
                    Cta = "I consent, run it",
                };
            }
            else // ignoreAll
            {
                int n = _ordered.Count;
                ConfirmRequest = new ConfirmRequest
                {
                    Kind = "ignoreAll",
                    Title = $"Ignore {n} rules?",
                    Body = "They stay in the exported Borang as not comply. Ignoring only clears them from your working list.",
                    Note = "No change to the model",
                    Cta = "Ignore them",
                };
            }
        }

        public void CommitConfirm()
        {
            if (_confirm == null || RunData == null) return;
            switch (_confirm.Kind)
            {
                case "one":
                    if (!string.IsNullOrEmpty(_confirm.RuleId)) _decisions[_confirm.RuleId] = CellDecision.Resolved;
                    break;
                case "all":
                    foreach (var r in JkrCopilotMath.Fixables(RunData.Rules, _decisions)) _decisions[r.Id] = CellDecision.Resolved;
                    break;
                case "top":
                    foreach (var r in JkrCopilotMath.TopFixes(RunData.Rules, _decisions)) _decisions[r.Id] = CellDecision.Resolved;
                    break;
                case "ignoreAll":
                    foreach (var r in RunData.Rules.Where(FitsTab)) _decisions[r.Id] = CellDecision.Ignored;
                    break;
            }
            ConfirmRequest = null;
            DetailRule = null;
            RefreshAfterDecision();
        }
        public void CancelConfirm() => ConfirmRequest = null;

        // ── Navigation ──
        public void ShowManual() { ActiveCopilotTab = CopilotTab.Manual; CurrentScreen = CopilotScreen.S5; DetailRule = null; }
        public void GoExport() { CurrentScreen = CopilotScreen.S6; DetailRule = null; }

        // ─── INotifyPropertyChanged ───
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
