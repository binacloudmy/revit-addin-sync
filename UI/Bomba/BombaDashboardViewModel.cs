using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Bomba
{
    /// One select of the purpose-group cascade (design 1A / prototype: Group →
    /// Occupancy → Sub-item). Levels are generic — depth comes from the rules
    /// tree, never a fixed count.
    public class CascadeLevelVm : NotifyBase
    {
        private BombaOptionDto _selected;

        /// Mono uppercase caption over the select ("PURPOSE GROUP", …).
        public string Label { get; set; }

        public ObservableCollection<BombaOptionDto> Options { get; private set; }

        public CascadeLevelVm() { Options = new ObservableCollection<BombaOptionDto>(); }

        public BombaOptionDto Selected
        {
            get { return _selected; }
            set { Set(ref _selected, value); }
        }
    }

    // Bound to real engine output: the panel code-behind runs the scan
    // (BombaComplianceService), BombaMapper builds CheckVm/FindingVm, and
    // ReplaceChecks swaps them in. The VM itself never talks HTTP.

    public class BombaDashboardViewModel : NotifyBase
    {
        private PaneState _state = PaneState.NeedsSetup;
        private CheckVm _selected;
        private string _scopeLabel = "Bomba Compliance";
        private string _scopeDetail = "Belum diimbas";
        private int _changedSinceRun;
        private bool _scanning;
        private bool _canRun;
        private string _cascadeCrumb = "";
        private string _setupGuidance =
            "Occupant load comes from floor area and purpose group. Without it, "
            + "no check can return a number — choose the schedule row that "
            + "applies. You're asked once per model.";

        private CoverageVm _coverage;

        public ObservableCollection<CheckVm> Checks { get; private set; }

        /// The purpose-group cascade the setup state binds to. One entry per
        /// tree level; the panel appends levels as selections narrow the path.
        public ObservableCollection<CascadeLevelVm> Cascade { get; private set; }

        public CoverageVm Coverage
        {
            get { return _coverage; }
            set
            {
                if (Set(ref _coverage, value)) RaiseAggregates();
            }
        }

        public BombaDashboardViewModel()
        {
            Checks = new ObservableCollection<CheckVm>();
            Cascade = new ObservableCollection<CascadeLevelVm>();
            // A re-check replaces the contents of Checks. Without this, the
            // tab strip and finding list refresh but the verdict block keeps
            // showing the previous run's numbers.
            Checks.CollectionChanged += (s, e) => RaiseAggregates();
        }

        /// True once the cascade has narrowed to a leaf row — enables Run.
        public bool CanRun
        {
            get { return _canRun; }
            set { Set(ref _canRun, value); }
        }

        /// The resolved path shown mono under the cascade, e.g. "IV.1.a".
        public string CascadeCrumb
        {
            get { return _cascadeCrumb; }
            set
            {
                if (Set(ref _cascadeCrumb, value)) Raise("HasCrumb");
            }
        }

        public bool HasCrumb { get { return !string.IsNullOrEmpty(CascadeCrumb); } }

        /// Why the pane asks — or, mid-scan, the backend's needs_input guidance.
        public string SetupGuidance
        {
            get { return _setupGuidance; }
            set { Set(ref _setupGuidance, value); }
        }

        private string _measuredFacts = "";

        /// Mono line under the cascade: what the model measured (largest
        /// storey, height). Measured values are real; showing them is how the
        /// drafter sees why a band resolved itself — or why it couldn't.
        public string MeasuredFacts
        {
            get { return _measuredFacts; }
            set
            {
                if (Set(ref _measuredFacts, value)) Raise("HasMeasuredFacts");
            }
        }

        public bool HasMeasuredFacts { get { return !string.IsNullOrEmpty(MeasuredFacts); } }

        /// True while a scan round-trip is in flight; the Re-check button
        /// binds IsEnabled to NotScanning.
        public bool Scanning
        {
            get { return _scanning; }
            set
            {
                if (Set(ref _scanning, value)) Raise("NotScanning");
            }
        }

        public bool NotScanning { get { return !Scanning; } }

        /// One scan's results replace the previous run wholesale. FindingVm's
        /// settable properties do not notify, so fresh instances arrive here —
        /// never mutate the old ones. CollectionChanged already re-raises the
        /// verdict block per add.
        public void ReplaceChecks(IList<CheckVm> checks, CoverageVm coverage)
        {
            Checks.Clear();
            if (checks != null)
                foreach (CheckVm c in checks) Checks.Add(c);
            Coverage = coverage;
            SelectedCheck = Checks.FirstOrDefault();
            State = PaneState.Ready;
            ChangedSinceRun = 0;
        }

        /// Single place to keep the verdict block's derived numbers in sync.
        /// Everything the 26pt verdict reads from must be raised here.
        private void RaiseAggregates()
        {
            Raise("TotalFailures");
            Raise("TotalNotChecked");
            Raise("NotCheckedSuffix");
            Raise("VerdictCount");
            Raise("VerdictWord");
            Raise("VerdictBreakdown");
        }

        public PaneState State
        {
            get { return _state; }
            set
            {
                if (Set(ref _state, value))
                {
                    Raise("ShowSetup");
                    Raise("ShowStale");
                    Raise("ShowResults");
                }
            }
        }

        public CheckVm SelectedCheck
        {
            get { return _selected; }
            set
            {
                if (Set(ref _selected, value))
                {
                    Raise("VisibleFindings");
                    Raise("HasFindings");
                }
            }
        }

        public string ScopeLabel { get { return _scopeLabel; } set { Set(ref _scopeLabel, value); } }
        public string ScopeDetail { get { return _scopeDetail; } set { Set(ref _scopeDetail, value); } }
        public int ChangedSinceRun { get { return _changedSinceRun; } set { Set(ref _changedSinceRun, value); } }

        public bool ShowSetup { get { return State == PaneState.NeedsSetup; } }
        public bool ShowStale { get { return State == PaneState.Stale; } }
        public bool ShowResults { get { return State == PaneState.Ready || State == PaneState.Stale; } }

        public int TotalFailures { get { return Checks.Sum(c => c.FailCount); } }
        public int TotalNotChecked { get { return Checks.Sum(c => c.NotCheckedCount); } }

        public string VerdictCount { get { return TotalFailures.ToString(); } }
        public string VerdictWord { get { return TotalFailures == 1 ? "finding" : "findings"; } }

        /// A different quantity from Coverage.Summary: that one counts ROOMS
        /// skipped, this one counts FINDINGS that could not be verified. Both
        /// render amber; the wording is what keeps them from reading as the
        /// same fact contradicting itself.
        public string NotCheckedSuffix
        {
            get { return TotalNotChecked == 1 ? " finding not verified" : " findings not verified"; }
        }

        /// Names which checks contributed, by SUBJECT — never by schedule number.
        /// Zero failures is NOT the same claim as "all checks ran" — coverage
        /// gaps and unavailable checks must still surface here, or this line
        /// repeats the exact "all passed while rooms went unchecked" mistake
        /// this pane exists to avoid.
        public string VerdictBreakdown
        {
            get
            {
                List<string> parts = Checks
                    .Where(c => c.Available && c.FailCount > 0)
                    .Select(c => c.Title + " " + c.FailCount)
                    .ToList();
                if (parts.Count > 0) return string.Join(" · ", parts.ToArray());

                List<string> notes = new List<string>();
                notes.Add("No failures");
                notes.Add(Coverage == null ? "coverage unknown" : "coverage " + Coverage.Label);
                if (TotalNotChecked > 0) notes.Add(TotalNotChecked + NotCheckedSuffix);

                List<string> unavailable = Checks.Where(c => !c.Available).Select(c => c.Title).ToList();
                if (unavailable.Count > 0) notes.Add(string.Join(", ", unavailable.ToArray()) + " not available");

                return string.Join(" · ", notes.ToArray());
            }
        }

        public string StaleLabel { get { return ChangedSinceRun + " rooms changed since this run"; } }

        public IEnumerable<FindingVm> VisibleFindings
        {
            get
            {
                if (SelectedCheck == null) return Enumerable.Empty<FindingVm>();
                // Failures first, then not-checked, then passes.
                return SelectedCheck.Findings
                    .OrderBy(f => f.Passed == false ? 0 : (!f.Passed.HasValue ? 1 : 2))
                    .ToList();
            }
        }

        public bool HasFindings
        {
            get { return SelectedCheck != null && SelectedCheck.Findings.Count > 0; }
        }

    }
}
