using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Bomba
{
    /// One select of the band cascade (Setup screen). Levels are generic —
    /// depth comes from the rules tree, never a fixed count. Since the top
    /// pick moved into the Home inline select, Setup only appears for a
    /// genuine needs_input boundary.
    public class CascadeLevelVm : NotifyBase
    {
        private BombaOptionDto _selected;

        /// Mono uppercase caption over the select ("BAND", …).
        public string Label { get; set; }

        public ObservableCollection<BombaOptionDto> Options { get; private set; }

        public CascadeLevelVm() { Options = new ObservableCollection<BombaOptionDto>(); }

        public BombaOptionDto Selected
        {
            get { return _selected; }
            set { Set(ref _selected, value); }
        }
    }

    // Modern Flow (design 10A): the pane is a state machine of screens. The
    // panel code-behind runs the scan and navigation; this VM only holds what
    // the screens bind to. It never talks HTTP.
    public class BombaDashboardViewModel : NotifyBase
    {
        private BombaScreen _screen = BombaScreen.Home;
        private bool _scanning;
        private IssueVm _current;
        private string _notice;

        public BombaDashboardViewModel()
        {
            Issues = new ObservableCollection<IssueVm>();
            FilteredIssues = new ObservableCollection<IssueVm>();
            Chips = new ObservableCollection<ChipVm>();
            PgOptions = new ObservableCollection<PgOptionVm>();
            Extinguishing = new ObservableCollection<ReqRowVm>();
            Alarm = new ObservableCollection<ReqRowVm>();
            Cascade = new ObservableCollection<CascadeLevelVm>();
            Dots = new ObservableCollection<DotVm>();
            CheckRows = new ObservableCollection<CheckRowVm>
            {
                new CheckRowVm { Label = "Read rooms & measure" },
                new CheckRowVm { Label = "Resolve requirements" },
                new CheckRowVm { Label = "Fire systems" },
            };
        }

        // ── screens ─────────────────────────────────────────────────────────

        public BombaScreen Screen
        {
            get { return _screen; }
            set
            {
                if (Set(ref _screen, value))
                {
                    Raise("OnHome"); Raise("OnSetup"); Raise("OnChecking");
                    Raise("OnSummary"); Raise("OnDetail"); Raise("OnDone"); Raise("OnNeeds");
                    Raise("BackVisible"); Raise("DotsVisible");
                }
            }
        }

        public bool OnHome { get { return Screen == BombaScreen.Home; } }
        public bool OnNeeds { get { return Screen == BombaScreen.Needs; } }
        public bool OnSetup { get { return Screen == BombaScreen.Setup; } }
        public bool OnChecking { get { return Screen == BombaScreen.Checking; } }
        public bool OnSummary { get { return Screen == BombaScreen.Summary; } }
        public bool OnDetail { get { return Screen == BombaScreen.Detail; } }
        public bool OnDone { get { return Screen == BombaScreen.Done; } }
        public bool BackVisible { get { return Screen == BombaScreen.Summary || Screen == BombaScreen.Detail || Screen == BombaScreen.Setup || Screen == BombaScreen.Needs; } }
        public bool DotsVisible { get { return Screen == BombaScreen.Detail; } }

        public bool Scanning
        {
            get { return _scanning; }
            set
            {
                if (Set(ref _scanning, value)) Raise("NotScanning");
            }
        }

        public bool NotScanning { get { return !Scanning; } }

        /// Amber banner on Home — login required, backend unreachable.
        public string Notice
        {
            get { return _notice; }
            set
            {
                if (Set(ref _notice, value)) Raise("HasNotice");
            }
        }

        public bool HasNotice { get { return !string.IsNullOrEmpty(Notice); } }

        // ── home: building type (inline select) ────────────────────────────

        private string _buildingType = "Not set";
        private string _pgTag = "tap to choose";
        private string _pgEvidence = "";
        private bool _pgOpen;
        private bool _pgLoading;

        public string BuildingType { get { return _buildingType; } set { Set(ref _buildingType, value); } }

        /// "auto" (room-name read), "your pick" (asserted), or the empty
        /// prompt — who decided is always one glance away.
        public string PgTag { get { return _pgTag; } set { Set(ref _pgTag, value); } }

        public string PgEvidence
        {
            get { return _pgEvidence; }
            set { if (Set(ref _pgEvidence, value)) Raise("HasPgEvidence"); }
        }

        public bool HasPgEvidence { get { return !string.IsNullOrEmpty(PgEvidence); } }

        public bool PgOpen
        {
            get { return _pgOpen; }
            set { if (Set(ref _pgOpen, value)) Raise("PgChev"); }
        }

        public string PgChev { get { return PgOpen ? "▴" : "▾"; } }

        public bool PgLoading { get { return _pgLoading; } set { Set(ref _pgLoading, value); } }

        public ObservableCollection<PgOptionVm> PgOptions { get; private set; }

        // ── home: model + M&E scope rows ────────────────────────────────────

        private string _floorLabel = "";
        private string _readLabel = "";
        private string _meValue = "";
        private Brush _meInk = M.Sub;
        private string _meWarn = "";

        public string FloorLabel { get { return _floorLabel; } set { Set(ref _floorLabel, value); } }
        public string ReadLabel { get { return _readLabel; } set { Set(ref _readLabel, value); } }

        public string MeValue { get { return _meValue; } set { Set(ref _meValue, value); } }
        public Brush MeInk { get { return _meInk; } set { Set(ref _meInk, value); } }

        public string MeWarn
        {
            get { return _meWarn; }
            set { if (Set(ref _meWarn, value)) Raise("HasMeWarn"); }
        }

        public bool HasMeWarn { get { return !string.IsNullOrEmpty(MeWarn); } }

        // ── setup (band ask only) ───────────────────────────────────────────

        public ObservableCollection<CascadeLevelVm> Cascade { get; private set; }

        private bool _canRun;
        private string _setupGuidance =
            "The measured facts sit on a boundary — choose the band that applies.";

        public bool CanRun { get { return _canRun; } set { Set(ref _canRun, value); } }
        public string SetupGuidance { get { return _setupGuidance; } set { Set(ref _setupGuidance, value); } }

        private string _measuredFacts = "";
        public string MeasuredFacts
        {
            get { return _measuredFacts; }
            set { if (Set(ref _measuredFacts, value)) Raise("HasMeasuredFacts"); }
        }
        public bool HasMeasuredFacts { get { return !string.IsNullOrEmpty(MeasuredFacts); } }

        // ── checking ────────────────────────────────────────────────────────

        private int _progressPct;
        private string _progressTitle = "Reading the model";
        private string _progressSub = "";

        public ObservableCollection<CheckRowVm> CheckRows { get; private set; }

        public int ProgressPct
        {
            get { return _progressPct; }
            set
            {
                if (Set(ref _progressPct, value)) Raise("ProgressText");
            }
        }

        public string ProgressText { get { return ProgressPct + "%"; } }
        public string ProgressTitle { get { return _progressTitle; } set { Set(ref _progressTitle, value); } }
        public string ProgressSub { get { return _progressSub; } set { Set(ref _progressSub, value); } }

        // ── summary: filter chips + list ────────────────────────────────────

        public ObservableCollection<IssueVm> Issues { get; private set; }
        public ObservableCollection<IssueVm> FilteredIssues { get; private set; }
        public ObservableCollection<ChipVm> Chips { get; private set; }
        public ObservableCollection<DotVm> Dots { get; private set; }

        private string _filterCls = "open";
        private int _passCount;
        private string _fixedSinceLast = "";

        public int PassCount { get { return _passCount; } set { Set(ref _passCount, value); } }

        /// "3 fixed since last check ✓" — computed by subject diff between
        /// scans, so it only ever states what two real scans proved.
        public string FixedSinceLast
        {
            get { return _fixedSinceLast; }
            set { if (Set(ref _fixedSinceLast, value)) Raise("HasFixedSinceLast"); }
        }

        public bool HasFixedSinceLast { get { return !string.IsNullOrEmpty(FixedSinceLast); } }

        public string FilterCls
        {
            get { return _filterCls; }
            set
            {
                if (Set(ref _filterCls, value)) RebuildFiltered();
            }
        }

        private bool _filterEmpty;
        public bool FilterEmpty { get { return _filterEmpty; } set { Set(ref _filterEmpty, value); } }

        /// Chips are derived from the live issue list — the headline number
        /// and the chip counts share one source and cannot disagree.
        public void RebuildChips()
        {
            Chips.Clear();
            var open = Issues.Where(i => !i.Done).ToList();
            Chips.Add(new ChipVm { Cls = "open", Label = open.Count + " open", Ink = M.Ink, Bg = M.ChipNeutral });
            int nFix = open.Count(i => i.Cls == "fix");
            int nCant = open.Count(i => i.Cls == "cant");
            if (nFix > 0) Chips.Add(new ChipVm { Cls = "fix", Label = "! " + nFix + " to fix", Ink = M.Red, Bg = M.RedTint });
            if (nCant > 0) Chips.Add(new ChipVm { Cls = "cant", Label = "— " + nCant + " can't check", Ink = M.Dim, Bg = M.Line });
            if (PassCount > 0) Chips.Add(new ChipVm { Cls = "pass", Label = "✓ " + PassCount + " pass", Ink = M.Green, Bg = M.GreenTint });
            foreach (var c in Chips) c.Active = c.Cls == _filterCls;
            RebuildFiltered();
        }

        private void RebuildFiltered()
        {
            foreach (var c in Chips) c.Active = c.Cls == _filterCls;
            FilteredIssues.Clear();
            // "pass" has no issue rows — the pass chip filters the list to
            // empty and the empty-state line explains where the passes live
            // (the Required fire systems screen carries the green chips).
            if (_filterCls != "pass")
                foreach (var i in Issues.Where(x => !x.Done && (_filterCls == "open" || x.Cls == _filterCls)))
                    FilteredIssues.Add(i);
            FilterEmpty = FilteredIssues.Count == 0;
            Raise("FilterEmptyText");
        }

        public string FilterEmptyText
        {
            get
            {
                if (_filterCls == "pass")
                    return PassCount + " systems pass — see them with their counts under Required fire systems.";
                return "Nothing in this bucket — tap “open” to see the working set.";
            }
        }

        public IssueVm CurrentIssue
        {
            get { return _current; }
            set
            {
                if (Set(ref _current, value)) { Raise("CurPos"); RebuildDots(); }
            }
        }

        public int OpenCount { get { return Issues.Count(i => !i.Done); } }
        public string OpenCountText { get { return OpenCount.ToString(); } }
        public string OpenWord { get { return OpenCount == 1 ? "open item" : "open items"; } }

        public string CurPos
        {
            get
            {
                if (_current == null) return "";
                return "Issue " + (Issues.IndexOf(_current) + 1) + " of " + Issues.Count;
            }
        }

        public void RaiseCounts()
        {
            Raise("OpenCount"); Raise("OpenCountText"); Raise("OpenWord");
            RebuildChips();
            RebuildDots();
        }

        private void RebuildDots()
        {
            Dots.Clear();
            foreach (var it in Issues)
            {
                Dots.Add(new DotVm
                {
                    W = ReferenceEquals(it, _current) ? 20 : 7,
                    Fill = it.Done ? M.Green : ReferenceEquals(it, _current) ? M.Accent : M.Line,
                });
            }
        }

        // ── required fire systems (the schedule row's full answer) ──────────

        public ObservableCollection<ReqRowVm> Extinguishing { get; private set; }
        public ObservableCollection<ReqRowVm> Alarm { get; private set; }

        private string _needsSub = "";
        private string _needsNote = "";
        private string _needsCite = "";

        public string NeedsSub { get { return _needsSub; } set { Set(ref _needsSub, value); } }
        public string NeedsCite { get { return _needsCite; } set { Set(ref _needsCite, value); } }

        public string NeedsNote
        {
            get { return _needsNote; }
            set { if (Set(ref _needsNote, value)) Raise("HasNeedsNote"); }
        }

        public bool HasNeedsNote { get { return !string.IsNullOrEmpty(NeedsNote); } }

        private bool _bylawOpen;

        /// The "WHY — the rule behind this" expander (By-law 225).
        public bool BylawOpen
        {
            get { return _bylawOpen; }
            set
            {
                if (Set(ref _bylawOpen, value)) Raise("BylawChev");
            }
        }

        public string BylawChev { get { return BylawOpen ? "▴" : "▾"; } }

        // ── done ────────────────────────────────────────────────────────────

        private string _doneTitle = "All clear";
        private string _doneSub = "";

        public string DoneTitle { get { return _doneTitle; } set { Set(ref _doneTitle, value); } }
        public string DoneSub { get { return _doneSub; } set { Set(ref _doneSub, value); } }
    }
}
