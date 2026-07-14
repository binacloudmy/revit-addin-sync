using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;

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

        private int _selectedLodLevel = 300;
        public int SelectedLodLevel
        {
            get => _selectedLodLevel;
            set { if (_selectedLodLevel != value) { _selectedLodLevel = value; Raise(); } }
        }

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

        /// <summary>Count of auto-fixable issues (Open + Ignored with a fix_action).</summary>
        public int FixableCount => Issues.Count(i => i.IsActionable && i.AutoFixable && !string.IsNullOrEmpty(i.FixAction));

        /// <summary>Count of issues the AI Fix button can send to the backend:
        /// actionable, no deterministic fix, tied to a real element, and carrying
        /// a backend check_id for the round-trip.</summary>
        public int AiFixableCount => Issues.Count(i =>
            i.IsActionable && !i.AutoFixable && i.RevitElementId > 0 && !string.IsNullOrEmpty(i.CheckId));

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

        // ─── INotifyPropertyChanged ───
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
