using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace RevitWebAppSync.UI.Bomba
{
    // Stub data for now: the pane is buildable and reviewable before the HTTP
    // client to bina-ai exists, and before data/bomba_rules.json is verified.
    //
    // When the backend lands: replace LoadStubData() with a call to the
    // /bomba endpoints and map Finding -> FindingVm. Nothing else changes.

    public class BombaDashboardViewModel : NotifyBase
    {
        private PaneState _state = PaneState.Ready;
        private CheckVm _selected;
        private string _scopeLabel = "Aras 01 — Blok A";
        private string _scopeDetail = "24 rooms · 31 doors";
        private int _changedSinceRun = 3;

        public ObservableCollection<CheckVm> Checks { get; private set; }
        public CoverageVm Coverage { get; set; }

        public BombaDashboardViewModel()
        {
            Checks = new ObservableCollection<CheckVm>();
            LoadStubData();
            _selected = Checks.FirstOrDefault();
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

        /// Names which checks contributed, by SUBJECT — never by schedule number.
        public string VerdictBreakdown
        {
            get
            {
                List<string> parts = Checks
                    .Where(c => c.Available && c.FailCount > 0)
                    .Select(c => c.Title + " " + c.FailCount)
                    .ToList();
                if (parts.Count == 0) return "All checks ran on " + ScopeLabel;
                return string.Join(" · ", parts.ToArray());
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

        // ── stub data ───────────────────────────────────────────────────────
        // Measured values are plausible model reads. Every rule-derived
        // threshold is FindingVm.PlaceholderValue and stays so until verified.

        private void LoadStubData()
        {
            const string P = FindingVm.PlaceholderValue;

            Coverage = new CoverageVm();
            Coverage.RoomsChecked = 20;
            Coverage.RoomsTotal = 24;
            Coverage.SkipReasons.Add("unenclosed_or_unplaced");
            Coverage.SkipReasons.Add("no_boundary");

            CheckVm exit = new CheckVm();
            exit.Title = "Exit width";
            FindingVm dewan = new FindingVm();
            dewan.Subject = "Dewan Serbaguna";
            dewan.RoomNumber = "R-1-04";
            dewan.Headline = "Exit width short by " + P + " mm";
            dewan.Passed = false;
            dewan.Severity = Severity.High;
            dewan.Metrics = P + " occupants from 321 m²\nneed " + P + " mm · have 1800 mm";
            dewan.ClauseRef = "UBBL 1984 " + P;
            dewan.RulesVersion = "bomba_rules v0.1";
            dewan.Jurisdiction = "peninsular";
            dewan.SchedulePath = "III.2.a.ii";
            dewan.Action = FindingAction.Fixable;
            dewan.FixLabel = "Widen both doors";
            dewan.ElementIds.Add(884213);
            dewan.ElementIds.Add(884219);
            dewan.Steps.Add(NewStep("Occupants per floor", "321 m² ÷ " + P + " m²/person = " + P, P));
            dewan.Steps.Add(NewStep("Exit width units", P + " ÷ " + P + " = " + P + " units", P));
            dewan.Steps.Add(NewStep("Round TOTAL first", P + " → " + P + " units", "181"));
            dewan.Steps.Add(NewStep("Convert to mm", P + " units = " + P + " mm", "177(e)"));
            exit.Findings.Add(dewan);

            FindingVm pejabat = new FindingVm();
            pejabat.Subject = "Pejabat";
            pejabat.RoomNumber = "R-1-02";
            pejabat.Headline = "Passes with " + P + " mm to spare";
            pejabat.Passed = true;
            pejabat.Severity = Severity.Pass;
            pejabat.Metrics = P + " occupants from 48 m² · have 900 mm";
            pejabat.ClauseRef = "UBBL 1984 " + P;
            pejabat.RulesVersion = "bomba_rules v0.1";
            pejabat.Jurisdiction = "peninsular";
            exit.Findings.Add(pejabat);

            // The differentiator: competitors report the permitted limit only.
            CheckVm travel = new CheckVm();
            travel.Title = "Travel distance";
            FindingVm terbuka = new FindingVm();
            terbuka.Subject = "Pejabat Terbuka";
            terbuka.RoomNumber = "R-1-11";
            terbuka.Headline = "Measured 42.6 m — two-way limit " + P + " m applies";
            terbuka.Passed = false;
            terbuka.Severity = Severity.High;
            terbuka.Metrics =
                "measured                42.6 m\n" +
                "limit · two-way         " + P + " m  ← applies\n" +
                "limit · one-way dead-end " + P + " m\n" +
                "limit · corridor dead-end " + P + " m";
            terbuka.Guidance = "Needs a design decision — add a second exit on the east façade, "
                             + "or relocate the corridor entry. All three limits are shown because "
                             + "changing the design can change which one binds.";
            terbuka.ClauseRef = "UBBL 1984 " + P;
            terbuka.RulesVersion = "bomba_rules v0.1";
            terbuka.Jurisdiction = "peninsular";
            terbuka.Action = FindingAction.GuidanceOnly;
            travel.Findings.Add(terbuka);

            // "Missing" vs "cannot verify" — the distinction that avoids a
            // false accusation of absent fire protection.
            CheckVm systems = new CheckVm();
            systems.Title = "Fire systems";
            FindingVm callPoint = new FindingVm();
            callPoint.Subject = "Manual call point";
            callPoint.Headline = "Cannot verify — no M&E model was searched";
            callPoint.Passed = null;   // NOT CHECKED, not failed
            callPoint.Severity = Severity.NotChecked;
            callPoint.Metrics = "required " + P;
            callPoint.Guidance = "Fire systems are modelled in the M&E discipline. "
                               + "Link the M&E model and re-check. This is not a finding of absence.";
            callPoint.ClauseRef = "UBBL 1984 " + P;
            callPoint.RulesVersion = "bomba_rules v0.1";
            callPoint.Jurisdiction = "peninsular";
            callPoint.SchedulePath = "IV.1.a.ii";
            callPoint.Action = FindingAction.GuidanceOnly;
            callPoint.SearchedModels.Add("Architecture");
            systems.Findings.Add(callPoint);

            FindingVm hoseReel = new FindingVm();
            hoseReel.Subject = "Hose reel system";
            hoseReel.Headline = "6 found across 2 levels";
            hoseReel.Passed = true;
            hoseReel.Severity = Severity.Pass;
            hoseReel.Metrics = "required " + P + " · present 6";
            hoseReel.ClauseRef = "UBBL 1984 " + P;
            hoseReel.RulesVersion = "bomba_rules v0.1";
            hoseReel.Jurisdiction = "peninsular";
            hoseReel.SearchedModels.Add("Architecture");
            hoseReel.SearchedModels.Add("M&E");
            systems.Findings.Add(hoseReel);

            // Visible but disabled. Hiding it makes users wonder whether it
            // exists; guessing its content would be dangerous.
            CheckVm unprotected = new CheckVm();
            unprotected.Title = "Unprotected areas";
            unprotected.Available = false;
            unprotected.UnavailableReason = "rules pending verification";

            Checks.Add(exit);
            Checks.Add(travel);
            Checks.Add(systems);
            Checks.Add(unprotected);
        }

        private static CalcStepVm NewStep(string label, string expression, string byLaw)
        {
            CalcStepVm s = new CalcStepVm();
            s.Label = label;
            s.Expression = expression;
            s.ByLaw = byLaw;
            return s;
        }
    }
}
