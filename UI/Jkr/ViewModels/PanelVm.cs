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
    public class ToastVm
    {
        public string Message { get; set; } = "";
        /// <summary>Show the Undo button: only on decision toasts (ignore, approve,
        /// mark manual, reopen) — never on fixes (undo those in Revit) or notices.</summary>
        public bool CanUndo { get; set; }
    }

    /// <summary>What Undo puts back: every issue the last decision changed, with
    /// the status it had before (Ignore all changes many at once).</summary>
    public class UndoSnapshot
    {
        public List<(string IssueId, IssueStatus PreviousStatus)> Items = new List<(string, IssueStatus)>();
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

        /// <summary>BIM010 section-and-cell summary of the last scan (fill_audit rows
        /// + backend checks). Read by the Results view and the Borang export.</summary>
        public BinaVibe.Mcp.Tools.Audit.Bim010Summary Summary { get; set; }
        // Counts and % cover AI-verified items only; items that need a person
        // (cannot_verify) are never a failure and never in the % — they are
        // counted in the Manual tab (JkrIssueCounts).
        public int Total => JkrIssueCounts.Total(Issues);
        public int OpenCount => JkrIssueCounts.Open(Issues);
        public int IgnoredCount => JkrIssueCounts.Ignored(Issues);
        public int ResolvedCount => JkrIssueCounts.Resolved(Issues);
        /// <summary>Manual tab: needs a person + marked manual by the user.</summary>
        public int ManualFixCount => JkrIssueCounts.Manual(Issues);
        // Compliance progress — Manual is deliberately excluded. The user has
        // triaged it but the model is still non-compliant, so it shouldn't
        // inflate the "% resolved" indicator.
        public int NonOpenCount => IgnoredCount + ResolvedCount;
        public int Percent => JkrIssueCounts.Percent(Issues);

        public int HighOpen => Issues.Count(i => i.IsAiOpen && i.Priority == IssuePriority.High);
        public int MedOpen  => Issues.Count(i => i.IsAiOpen && i.Priority == IssuePriority.Medium);
        public int LowOpen  => Issues.Count(i => i.IsAiOpen && i.Priority == IssuePriority.Low);

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
            !i.NeedsPerson && (i.IsActionable || i.Status == IssueStatus.ManualFixNeeded);

        /// <summary>Count of auto-fixable issues (Open + Ignored + Manual with a fix_action).</summary>
        public int FixableCount => Issues.Count(i => FixRetryable(i) && i.AutoFixable && !string.IsNullOrEmpty(i.FixAction));

        /// <summary>Count of issues Fix All's AI phase can send to the backend:
        /// retryable, no deterministic fix, tied to a real element, and carrying
        /// a backend check_id for the round-trip.</summary>
        public int AiFixableCount => Issues.Count(i => FixRetryable(i) && i.AiFixCandidate);

        public string SessionLine
        {
            get
            {
                if (OpenCount > 0 && ManualFixCount > 0)
                    return $"{OpenCount} open · {ManualFixCount} manual";
                if (OpenCount > 0)
                    return $"{OpenCount} issue{(OpenCount == 1 ? "" : "s")} to go";
                if (ManualFixCount > 0)
                    return $"{ManualFixCount} for manual review";
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
                    OpenCount = items.Count(i => i.IsAiOpen),
                    TotalCount = items.Count(i => !i.NeedsPerson),
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
                // Items that need a person live only in the Manual tab.
                if (Tab == TabKind.Open && !i.IsAiOpen) continue;
                if (Tab == TabKind.Ignored && (i.NeedsPerson || i.Status != IssueStatus.Ignored)) continue;
                if (Tab == TabKind.Resolved && (i.NeedsPerson || (i.Status != IssueStatus.Fixed && i.Status != IssueStatus.Approved))) continue;
                if (Tab == TabKind.Manual && !i.InManualTab) continue;
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

        /// <summary>Toast times from the prototype: 6 s while Undo is offered, 2.6 s otherwise.</summary>
        public static readonly TimeSpan UndoToastTime = TimeSpan.FromMilliseconds(6000);
        public static readonly TimeSpan ToastTime = TimeSpan.FromMilliseconds(2600);

        /// <summary>A status change Undo can take back: decisions only. A fix
        /// changed the model, so it is undone in Revit (Ctrl+Z), not here.</summary>
        public static bool IsUndoable(IssueStatus newStatus) => newStatus != IssueStatus.Fixed;

        /// <summary>The last undoable decision is still on offer.</summary>
        public bool CanUndo => _undo != null && Toast != null && Toast.CanUndo;

        public void ApplyAction(IssueVm issue, IssueStatus newStatus, bool advance)
        {
            if (issue == null) return;
            var prev = issue.Status;
            var snapshot = new UndoSnapshot();
            snapshot.Items.Add((issue.Id, prev));

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
            var titleShort = issue.Title.Length > 40 ? issue.Title.Substring(0, 40) + "…" : issue.Title;
            // prototype: a fix says "1 change(s) written to model".
            var message = newStatus == IssueStatus.Fixed
                ? RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.ChangesWritten(1)
                : RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.Decision(newStatus.ToString(), titleShort);
            if (IsUndoable(newStatus)) ShowToast(message, snapshot);
            else ShowToast(message);

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

        /// <summary>One decision for many issues (Ignore all) with ONE toast and one
        /// Undo that puts every issue back.</summary>
        public void ApplyDecisions(IEnumerable<IssueVm> issues, IssueStatus newStatus, string message)
        {
            var list = (issues ?? Enumerable.Empty<IssueVm>()).Where(i => i != null).ToList();
            if (list.Count == 0) return;
            var snapshot = new UndoSnapshot();
            foreach (var i in list)
            {
                snapshot.Items.Add((i.Id, i.Status));
                i.Status = newStatus;
            }
            if (IsUndoable(newStatus)) ShowToast(message, snapshot);
            else ShowToast(message);
            Refresh();
            RaiseCounts();
        }

        /// <summary>Put back every issue the last decision changed. Returns them so
        /// the caller can save the restored statuses (audit file).</summary>
        public List<IssueVm> Undo()
        {
            var restored = new List<IssueVm>();
            if (_undo == null) return restored;
            foreach (var (id, prev) in _undo.Items)
            {
                var issue = Issues.FirstOrDefault(i => i.Id == id);
                if (issue == null) continue;
                issue.Status = prev;
                restored.Add(issue);
            }
            _undo = null;
            _toastTimer?.Stop();
            Toast = null;
            Refresh();
            RaiseCounts();
            return restored;
        }

        /// <summary>A notice: no Undo, 2.6 s. Clears any pending Undo so the button
        /// can never act on an older decision.</summary>
        internal void ShowToast(string msg) => ShowToast(msg, null);

        private void ShowToast(string msg, UndoSnapshot undo)
        {
            _undo = undo;
            Toast = new ToastVm { Message = msg, CanUndo = undo != null };
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = RevitWebAppSync.UI.Jkr.Panel.Core.PaneText.ToastTime(msg, undo != null) };
            _toastTimer.Tick += (_, __) => { _toastTimer.Stop(); Toast = null; _undo = null; };
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
                c.OpenCount = items.Count(i => i.IsAiOpen);
                c.TotalCount = items.Count(i => !i.NeedsPerson);
            }
        }

        // ─── INotifyPropertyChanged ───
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
