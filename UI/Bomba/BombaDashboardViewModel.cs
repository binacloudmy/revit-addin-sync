using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI.Bomba
{
    /// One select of the building-type cascade (Setup screen). Levels are
    /// generic — depth comes from the rules tree, never a fixed count.
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

    // Modern Flow (design 10A): the pane is a state machine of six screens.
    // The panel code-behind runs the scan and navigation; this VM only holds
    // what the screens bind to. It never talks HTTP.
    public class BombaDashboardViewModel : NotifyBase
    {
        private BombaScreen _screen = BombaScreen.Home;
        private bool _scanning;
        private IssueVm _current;
        private string _notice;

        public BombaDashboardViewModel()
        {
            Issues = new ObservableCollection<IssueVm>();
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
                    Raise("OnSummary"); Raise("OnDetail"); Raise("OnDone");
                    Raise("BackVisible"); Raise("DotsVisible");
                }
            }
        }

        public bool OnHome { get { return Screen == BombaScreen.Home; } }
        public bool OnSetup { get { return Screen == BombaScreen.Setup; } }
        public bool OnChecking { get { return Screen == BombaScreen.Checking; } }
        public bool OnSummary { get { return Screen == BombaScreen.Summary; } }
        public bool OnDetail { get { return Screen == BombaScreen.Detail; } }
        public bool OnDone { get { return Screen == BombaScreen.Done; } }
        public bool BackVisible { get { return Screen == BombaScreen.Summary || Screen == BombaScreen.Detail || Screen == BombaScreen.Setup; } }
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

        // ── home ────────────────────────────────────────────────────────────

        private string _buildingType = "Not set — tap to choose";
        private string _floorLabel = "";
        private string _readLabel = "";

        public string BuildingType { get { return _buildingType; } set { Set(ref _buildingType, value); } }
        public string FloorLabel { get { return _floorLabel; } set { Set(ref _floorLabel, value); } }
        public string ReadLabel { get { return _readLabel; } set { Set(ref _readLabel, value); } }

        // ── setup (building-type cascade) ───────────────────────────────────

        public ObservableCollection<CascadeLevelVm> Cascade { get; private set; }

        private bool _canRun;
        private string _setupGuidance =
            "Pick the schedule row that applies. Room sizes and heights are "
            + "read from the model — the size band chooses itself.";

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

        // ── summary + detail ────────────────────────────────────────────────

        public ObservableCollection<IssueVm> Issues { get; private set; }
        public ObservableCollection<DotVm> Dots { get; private set; }

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
        public string OpenWord { get { return OpenCount == 1 ? "thing to fix" : "things to fix"; } }

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

        // ── done ────────────────────────────────────────────────────────────

        private string _doneTitle = "All clear";
        private string _doneSub = "";

        public string DoneTitle { get { return _doneTitle; } set { Set(ref _doneTitle, value); } }
        public string DoneSub { get { return _doneSub; } set { Set(ref _doneSub, value); } }
    }
}
