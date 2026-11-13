using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Cad2Bim;
using Cad2Bim.Services;
using Cad2Bim.ViewModels;
using Cad2Bim.ViewModels.Shapes;
using RevitWebAppSync.Services.CadToBim;
using CadWall = Cad2Bim.Wall;
using CadSegment = Cad2Bim.Segment;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>One row of the Layers list: the upstream LayerViewModel (name, visibility,
    /// shapes) plus the role dropdown, the reason line and the entity count.</summary>
    public sealed class CadLayerViewModel : LayerViewModel
    {
        private readonly Action<CadLayerViewModel, LayerRole> _onRoleChosen;
        private LayerRole _role;

        public CadLayerViewModel(string name, int count, LayerRole role,
                                 string reason = null, string lookReason = null,
                                 Action<CadLayerViewModel, LayerRole> onRoleChosen = null) : base(name)
        {
            Count = count;
            _role = role;
            Reason = reason ?? "";
            LookReason = lookReason;
            _onRoleChosen = onRoleChosen;
        }

        public int Count { get; }

        /// <summary>Why the role is what it is ("name has \"wall\"", "set by you").</summary>
        public string Reason { get; }

        /// <summary>Why this layer is flagged "need a look"; null when it is not.</summary>
        public string LookReason { get; }

        public bool NeedsLook => LookReason != null;

        /// <summary>The line under the row: the flag reason when flagged, else the role reason.</summary>
        public string DetailText => LookReason ?? Reason;

        public LayerRole Role
        {
            get => _role;
            set
            {
                if (SetField(ref _role, value))
                {
                    OnPropertyChanged(nameof(RoleLabel));
                    OnPropertyChanged(nameof(RoleChoice));
                }
            }
        }

        public string RoleLabel => LayerRoles.Label(_role);

        /// <summary>The dropdown's selection. Picking a different role hands it to the view
        /// model (one change = one re-detect), which replaces the whole Layers list.</summary>
        public RoleChoice RoleChoice
        {
            get => LayerRoles.ChoiceFor(_role);
            set
            {
                if (value == null || value.Role == _role) return;
                if (_onRoleChosen != null) _onRoleChosen(this, value.Role);
                else Role = value.Role;
            }
        }

        public IReadOnlyList<RoleChoice> RoleChoices => LayerRoles.Choices;
    }

    /// <summary>One tab of the step bar.</summary>
    public sealed class StepTab : ViewModelBase
    {
        public StepTab(CadToBimStep step, string title)
        {
            Step = step;
            Title = title;
        }

        public CadToBimStep Step { get; }
        public string Title { get; }

        private bool _isCurrent;
        public bool IsCurrent { get => _isCurrent; set => SetField(ref _isCurrent, value); }

        private bool _isDone;
        public bool IsDone
        {
            get => _isDone;
            set { if (SetField(ref _isDone, value)) OnPropertyChanged(nameof(Text)); }
        }

        private bool _isEnabled;
        public bool IsEnabled { get => _isEnabled; set => SetField(ref _isEnabled, value); }

        /// <summary>"1 Layers", "1 Layers ✓" once done.</summary>
        public string Text => _isDone ? Title + " ✓" : Title;
    }

    /// <summary>One CREATED row of the Review step.</summary>
    public sealed class CreatedRow
    {
        public BuildCategory Category { get; set; }
        public string Label { get; set; }
        public int Count { get; set; }
    }

    /// <summary>One TYPES row of the Build step: a wall width or door/window size and the
    /// type it gets. Selected starts at what the builder would choose by itself.</summary>
    public sealed class TypeRowViewModel : ViewModelBase
    {
        private readonly Action<TypeRowViewModel> _changed;

        public TypeRowViewModel(TypeRowPlan plan, TypeRowOption selected, Action<TypeRowViewModel> changed)
        {
            Plan = plan;
            _selected = selected ?? plan.Default;
            _changed = changed;
        }

        public TypeRowPlan Plan { get; }
        public string Label => Plan.Label;
        /// <summary>"×33".</summary>
        public string CountText => "×" + Plan.Count;
        public List<TypeRowOption> Options => Plan.Options;
        /// <summary>One entry only ("none in the model"): nothing to choose.</summary>
        public bool CanChoose => Plan.Options.Count > 1;
        /// <summary>"12 walls 115 mm thick: Generic - 115mm" — the size and the full type name
        /// (family included), for the row's tooltip.</summary>
        public string Tip => Plan.Count + (Plan.Slot == TypeSlot.Wall ? (Plan.Count == 1 ? " wall " : " walls ") + Math.Round(Plan.WidthMm) + " mm thick"
                                         : " " + BuildReport.Noun(Plan.Slot == TypeSlot.Door ? BuildCategory.Door : BuildCategory.Window, Plan.Count)
                                           + " " + Math.Round(Plan.WidthMm) + " x " + Math.Round(Plan.HeightMm) + " mm")
                             + ": " + (_selected?.Label ?? "");

        private TypeRowOption _selected;
        public TypeRowOption Selected
        {
            get => _selected;
            set
            {
                if (value == null) return;   // a ComboBox clears SelectedItem while its list is swapped
                if (SetField(ref _selected, value))
                {
                    OnPropertyChanged(nameof(Tip));
                    _changed?.Invoke(this);
                }
            }
        }
    }

    /// <summary>A level of the open project, for the "Add to this project" picker. Id is the
    /// ElementId VALUE (ElementIdCompat.ToLong in CadToBimPanel.RefreshLevels) — this file is
    /// linked into Tests and carries no Revit type.</summary>
    public sealed class LevelChoice
    {
        public string Name;
        public long Id;
        public override string ToString() => Name;
    }

    /// <summary>Everything the viewport overlay draws, captured at one moment.</summary>
    public sealed class OverlaySnapshot
    {
        public static readonly OverlaySnapshot Empty = new OverlaySnapshot(
            new List<CadWall>(), new List<CadWall>(), new List<Opening>(), new List<Space>(), new List<BoxMm>());

        public OverlaySnapshot(IReadOnlyList<CadWall> active, IReadOnlyList<CadWall> erased,
                               IReadOnlyList<Opening> openings, IReadOnlyList<Space> spaces, IReadOnlyList<BoxMm> boxes,
                               IReadOnlyList<Opening> pickedOut = null, IReadOnlyList<SkippedItem> highlights = null)
        {
            Active = active;
            Erased = erased;
            Openings = openings;
            Spaces = spaces;
            Boxes = boxes;
            PickedOut = pickedOut ?? new List<Opening>();
            Highlights = highlights ?? new List<SkippedItem>();
        }

        public IReadOnlyList<CadWall> Active { get; }
        public IReadOnlyList<CadWall> Erased { get; }
        /// <summary>Openings that will be built (Pick leaves the others out).</summary>
        public IReadOnlyList<Opening> Openings { get; }
        public IReadOnlyList<Space> Spaces { get; }
        public IReadOnlyList<BoxMm> Boxes { get; }
        /// <summary>Openings the drafter left out with Pick.</summary>
        public IReadOnlyList<Opening> PickedOut { get; }
        /// <summary>Skipped items marked red in the Review step.</summary>
        public IReadOnlyList<SkippedItem> Highlights { get; }
    }

    /// <summary>What one detection pass produced: the session plus what the pane displays.</summary>
    public sealed class DetectResult
    {
        public CadToBimSession Session;
        public Dictionary<string, List<object>> LayerShapes = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, int> Census = new Dictionary<string, int>();
        /// <summary>Wall-role layer -> how many of its lines lie along a detected wall. null = not
        /// measured (no "wall layer, but no walls found" flag is raised).</summary>
        public IReadOnlyDictionary<string, int> WallFacesByLayer;
        public Rect Bounds = Rect.Empty;
        public int PlanCount;
        public int Entities;
        public int UnpairedWallLines;
        public double ReadSeconds;
        public string UnitsText = "";
        /// <summary>Set when AEC (Civil 3D / Architecture / MEP) entities were skipped but the
        /// drawing still had readable plain geometry, so detection continued rather than being
        /// refused (CadSourceVerdict.IsRefused). Null when nothing was skipped. Apply() appends
        /// this to Status after the normal "Detected N walls…" text.</summary>
        public string AecWarning;
    }

    public delegate DetectResult DetectDelegate(string path, CadToBimSettings settings, double sMinMm, double sMaxMm,
                                                IReadOnlyDictionary<string, LayerRole> roles, IProgress<string> progress,
                                                CancellationToken ct);

    /// <summary>
    /// The CAD to BIM pane in four steps (AI-344): Layers → Preview → Build → Review.
    /// Detection and brushing run on the thread pool and touch only Cad2Bim types; results are
    /// applied on the WPF dispatcher (the await continuation). Everything that touches Revit
    /// (build, check what is still built, undo a build, select) is a BuildRequest handed to
    /// CadToBimBuildHandler through IBuildEventRaiser; the answer comes back through
    /// IBuildRequestSink.Completed. One job at a time.
    /// </summary>
    public sealed class CadToBimViewModel : ViewModelBase
    {
        /// <summary>Above this many walls from one brush box, ask before adding them.</summary>
        public const int BrushConfirmAbove = 40;
        /// <summary>Status text for a drawing written by a vertical AutoCAD (spec §3 "Detect").</summary>
        public const string UnsupportedSourceMessage =
            "This drawing was saved by Civil 3D / AutoCAD Architecture / AutoCAD MEP. " +
            "Run EXPORTTOAUTOCAD in AutoCAD and open the exported file.";
        private const int DebounceMs = 300;
        private const double UnpairedMinLengthMm = 500.0;

        private readonly IBuildRequestSink _handler;
        private readonly IBuildEventRaiser _raiser;
        private readonly DetectDelegate _detect;
        private readonly Action<Action> _onUi;
        private readonly Action<CadToBimSettings> _saveSettings;
        private readonly DispatcherTimer _debounce;
        private readonly Dictionary<string, LayerRole> _roles = new Dictionary<string, LayerRole>(StringComparer.OrdinalIgnoreCase);
        private readonly List<BoxMm> _boxes = new List<BoxMm>();
        private readonly StepNavigator _nav = new StepNavigator();

        // Detect and Brush both set Cad2Bim.Wall's process-wide static thresholds (SMin/SMax,
        // and Brush additionally MinFaceAspect/MinFaceLength) before reading the drawing, then
        // rely on them for the duration of that one read. OpenAsync cancels the PREVIOUS
        // CancellationTokenSource on a new open but does not wait for its Task.Run to actually
        // finish, so two engine calls could otherwise run on different thread-pool threads at
        // once and stomp each other's thresholds mid-classification. This gate makes sure only
        // one engine call (detect or brush) is ever inside its Task.Run body at a time; the
        // cancellation token still lets a superseded call skip its own work once it is this
        // call's turn.
        private readonly SemaphoreSlim _engineGate = new SemaphoreSlim(1, 1);

        private CadToBimSettings _settings;
        private CancellationTokenSource _cts;
        private DetectResult _result;
        private Stopwatch _buildClock;
        private BoxMm _pendingBox;
        private double _detectedSMin;
        private double _detectedSMax;
        private bool _building;
        private bool _completedHooked;
        private BuildJobKind? _job;               // the handler job in flight, if any
        private int _erasedAtBuild;
        private int _pickedAtBuild;
        private List<SkippedItem> _highlights = new List<SkippedItem>();

        public CadToBimViewModel(IBuildRequestSink handler, IBuildEventRaiser raiser,
                                 CadToBimSettings settings = null, DetectDelegate detect = null,
                                 Action<Action> onUi = null, Action<CadToBimSettings> saveSettings = null)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _raiser = raiser ?? throw new ArgumentNullException(nameof(raiser));
            _settings = settings ?? CadToBimSettings.Load();
            _detect = detect ?? Detect;
            _onUi = onUi ?? DefaultOnUi;
            _saveSettings = saveSettings ?? (s => s.Save());

            _sMin = _settings.SMinMm;
            _sMax = _settings.SMaxMm;
            _height = _settings.WallHeightMm;

            Steps = new ObservableCollection<StepTab>
            {
                new StepTab(CadToBimStep.Layers, "1 Layers"),
                new StepTab(CadToBimStep.Preview, "2 Preview"),
                new StepTab(CadToBimStep.Build, "3 Build"),
                new StepTab(CadToBimStep.Review, "4 Review"),
            };
            RefreshSteps();

            _debounce = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(DebounceMs),
            };
            _debounce.Tick += (_, __) => { _debounce.Stop(); OnDebounced(); };
        }

        // ───────── bindable state ─────────

        private CadToBimSession _session;
        public CadToBimSession Session
        {
            get => _session;
            private set
            {
                if (SetField(ref _session, value))
                {
                    OnPropertyChanged(nameof(HasSession));
                    _nav.HasSession = value != null;
                    RefreshSteps();
                }
            }
        }

        public bool HasSession => _session != null;

        private ObservableCollection<LayerViewModel> _layers = new ObservableCollection<LayerViewModel>();
        /// <summary>Replaced wholesale per detect (one viewport rebuild instead of one per layer).
        /// Flagged ("need a look") layers first, then by name.</summary>
        public ObservableCollection<LayerViewModel> Layers
        {
            get => _layers;
            private set => SetField(ref _layers, value);
        }

        private string _layerSummary = "";
        /// <summary>"18 · 2 need a look".</summary>
        public string LayerSummary { get => _layerSummary; private set => SetField(ref _layerSummary, value); }

        public ObservableCollection<LevelChoice> Levels { get; } = new ObservableCollection<LevelChoice>();

        private LevelChoice _selectedLevel;
        public LevelChoice SelectedLevel
        {
            get => _selectedLevel;
            set { if (SetField(ref _selectedLevel, value)) UpdateLevelForecast(); }
        }

        private Rect _bounds = Rect.Empty;
        public Rect Bounds { get => _bounds; private set => SetField(ref _bounds, value); }

        private string _status = "Open a DWG to begin.";
        public string Status { get => _status; private set => SetField(ref _status, value); }

        private bool _busy;
        public bool Busy
        {
            get => _busy;
            private set
            {
                if (SetField(ref _busy, value))
                {
                    OnPropertyChanged(nameof(CanConfirm));
                    OnPropertyChanged(nameof(CanEdit));
                    OnPropertyChanged(nameof(CanUndoBuild));
                }
            }
        }

        /// <summary>Spec §3 "Confirm": "Controls lock" while a build is in flight. Bound to the
        /// sidebar's editable sections' IsEnabled (CadToBimPanel.xaml) - layers, thickness/height,
        /// storey mode, target/level - so nothing changes under a build already in progress.</summary>
        public bool CanEdit => !_busy && !_building;

        private BuildTarget _target = BuildTarget.AddToProject;
        public BuildTarget Target
        {
            get => _target;
            set
            {
                if (SetField(ref _target, value))
                {
                    OnPropertyChanged(nameof(IsAddToProject));
                    UpdateLevelForecast();
                    UpdateTypes();
                    // What counts as built depends on the target: recount, and for "Add to this
                    // project" ask the model again (Ctrl+Z, or another project is active now).
                    UpdateCounts();
                    if (_target == BuildTarget.AddToProject && IsBuildStep) CheckBuilt();
                }
            }
        }

        public bool IsAddToProject => _target == BuildTarget.AddToProject;

        /// <summary>The document the last check or build ran against (BuildReport.DocumentKey);
        /// null until one reported back.</summary>
        private string _activeDocumentKey;

        /// <summary>Whether walls recorded as built are left out of the next build: only for
        /// "Add to this project", and only while the active project is the one they were built
        /// into. "Save as new .rvt" always builds everything that is active.</summary>
        private bool TracksBuilt =>
            _target == BuildTarget.AddToProject && _session != null &&
            (string.IsNullOrEmpty(_session.BuiltDocumentKey) || string.IsNullOrEmpty(_activeDocumentKey) ||
             string.Equals(_session.BuiltDocumentKey, _activeDocumentKey, StringComparison.OrdinalIgnoreCase));

        private StoreyMode _storeys = StoreyMode.Stack;
        public StoreyMode Storeys
        {
            get => _storeys;
            set { if (SetField(ref _storeys, value)) UpdateLevelForecast(); }
        }

        private double _height;
        public double HeightMm
        {
            get => _height;
            set { if (SetField(ref _height, value)) Bounce(); }
        }

        private double _sMin;
        public double SMinMm
        {
            get => _sMin;
            set { if (SetField(ref _sMin, value)) Bounce(); }
        }

        private double _sMax;
        public double SMaxMm
        {
            get => _sMax;
            set { if (SetField(ref _sMax, value)) Bounce(); }
        }

        private int _wallCount;
        public int WallCount { get => _wallCount; private set => SetField(ref _wallCount, value); }
        private int _doorCount;
        public int DoorCount { get => _doorCount; private set => SetField(ref _doorCount, value); }
        private int _windowCount;
        public int WindowCount { get => _windowCount; private set => SetField(ref _windowCount, value); }
        private int _roomCount;
        public int RoomCount { get => _roomCount; private set => SetField(ref _roomCount, value); }
        private int _erasedCount;
        public int ErasedCount { get => _erasedCount; private set => SetField(ref _erasedCount, value); }
        private int _forcedCount;
        public int ForcedCount { get => _forcedCount; private set => SetField(ref _forcedCount, value); }
        private int _pickedCount;
        /// <summary>Doors and windows left out with Pick.</summary>
        public int PickedCount { get => _pickedCount; private set => SetField(ref _pickedCount, value); }

        private int _extraCount;
        /// <summary>Doors, windows and rooms the next build adds alongside its walls.</summary>
        public int ExtraCount { get => _extraCount; private set => SetField(ref _extraCount, value); }

        private int _builtCount;
        /// <summary>Walls the pane believes earlier builds made.</summary>
        public int BuiltCount
        {
            get => _builtCount;
            private set
            {
                if (SetField(ref _builtCount, value))
                {
                    OnPropertyChanged(nameof(HasBuilt));
                    OnPropertyChanged(nameof(BuiltText));
                }
            }
        }

        public bool HasBuilt => _builtCount > 0;

        public string BuiltText => _builtCount + (_builtCount == 1 ? " wall is" : " walls are") +
                                   " already built in the model and not counted.";

        public string ConfirmLabel
        {
            get
            {
                if (_session == null) return "Build";
                if (_wallCount == 0) return _builtCount > 0 ? "Check the model again" : "Nothing new to build";
                string label = "Build " + _wallCount + (_wallCount == 1 ? " wall" : " walls");
                return _extraCount > 0 ? label + " + " + _extraCount + " more" : label;
            }
        }

        /// <summary>Also true with nothing pending when walls are recorded as built: the click
        /// then re-checks the model, which finds walls undone with Ctrl+Z.</summary>
        public bool CanConfirm => !_busy && !JobBlocks && _session != null && (_wallCount > 0 || _builtCount > 0);

        /// <summary>A job is in flight that a new one must wait for. A background CheckBuilt does
        /// not count: a build, undo or select simply takes its place in the handler's slot (the
        /// build re-checks the model itself).</summary>
        private bool JobBlocks => _job.HasValue && _job.Value != BuildJobKind.CheckBuilt;

        private string _warning = "";
        public string Warning { get => _warning; private set => SetField(ref _warning, value); }

        private string _levelForecast = "";
        /// <summary>"Will create 1 new level: CAD Level 2" (Build step), or "".</summary>
        public string LevelForecast { get => _levelForecast; private set => SetField(ref _levelForecast, value); }

        // ───────── types (Build step, AI-345) ─────────

        private TypeCatalog _typeCatalog = TypeCatalog.None;
        private List<TypeRowViewModel> _allTypeRows = new List<TypeRowViewModel>();
        /// <summary>The drafter's choices by row key, kept across refreshes (a brush or erase
        /// rebuilds the rows; the choice for "125 mm" stays).</summary>
        private readonly Dictionary<string, long> _typePicks = new Dictionary<string, long>();
        private const long PickedNew = -1;

        /// <summary>Rows shown: every door and window size, and the most used wall widths
        /// (all of them once ShowAllTypes is on).</summary>
        public ObservableCollection<TypeRowViewModel> TypeRows { get; } = new ObservableCollection<TypeRowViewModel>();

        public bool HasTypeRows => TypeRows.Count > 0;

        private bool _showAllTypes;
        public bool ShowAllTypes
        {
            get => _showAllTypes;
            set { if (SetField(ref _showAllTypes, value)) ShowTypeRows(); }
        }

        private string _moreTypesText = "";
        /// <summary>"Show 27 more wall widths", "Show fewer", or "" when all fit.</summary>
        public string MoreTypesText { get => _moreTypesText; private set => SetField(ref _moreTypesText, value); }

        private string _typesNote = "";
        /// <summary>Under the rows: what happens to sizes the model lacks, or why there are no rows.</summary>
        public string TypesNote { get => _typesNote; private set => SetField(ref _typesNote, value); }

        private string _typesSummary = "";
        /// <summary>Title-row text: "12 sizes".</summary>
        public string TypesSummary { get => _typesSummary; private set => SetField(ref _typesSummary, value); }

        private string _drawingPath;
        public string DrawingPath { get => _drawingPath; private set => SetField(ref _drawingPath, value); }
        private string _drawingName = "No drawing";
        public string DrawingName { get => _drawingName; private set => SetField(ref _drawingName, value); }
        private string _unitsText = "";
        public string UnitsText { get => _unitsText; private set => SetField(ref _unitsText, value); }
        private string _readText = "";
        public string ReadText { get => _readText; private set => SetField(ref _readText, value); }
        private int _planCount;
        public int PlanCount
        {
            get => _planCount;
            private set { if (SetField(ref _planCount, value)) OnPropertyChanged(nameof(PlanText)); }
        }
        public string PlanText => _planCount == 0 ? "" : _planCount + " found";

        private OverlaySnapshot _overlay = OverlaySnapshot.Empty;
        public OverlaySnapshot Overlay { get => _overlay; private set => SetField(ref _overlay, value); }

        private BrushResult _pendingBrush;
        /// <summary>Non-null while a big brush result waits for the drafter's yes/no.</summary>
        public BrushResult PendingBrushConfirm { get => _pendingBrush; private set => SetField(ref _pendingBrush, value); }

        /// <summary>Asked before overwriting an existing .rvt; the view wires a TaskDialog. Null = overwrite.</summary>
        public Func<string, bool> OverwritePrompt { get; set; }

        public CadToBimSettings Settings => _settings;

        // ───────── steps ─────────

        public ObservableCollection<StepTab> Steps { get; }

        public CadToBimStep Step => _nav.Current;
        public bool IsLayersStep => _nav.Current == CadToBimStep.Layers;
        public bool IsPreviewStep => _nav.Current == CadToBimStep.Preview;
        public bool IsBuildStep => _nav.Current == CadToBimStep.Build;
        public bool IsReviewStep => _nav.Current == CadToBimStep.Review;

        /// <summary>Open a step from the step bar (or Back/Next). Never re-detects: erase,
        /// brush and pick work stay as they are.</summary>
        public bool GoTo(CadToBimStep step)
        {
            CadToBimStep before = _nav.Current;
            if (!_nav.GoTo(step)) return false;
            if (before == CadToBimStep.Review && step != CadToBimStep.Review) ClearHighlights();
            RefreshSteps();
            if (step == CadToBimStep.Build)
            {
                UpdateLevelForecast();
                UpdateTypes();
                // The drafter may have pressed Ctrl+Z in Revit since the last build: ask the
                // model which built walls are still there before showing what will be built.
                CheckBuilt();
            }
            return true;
        }

        public bool Next() => GoTo(_nav.Current + 1);

        public bool Back() => _nav.Current > CadToBimStep.Layers && GoTo(_nav.Current - 1);

        private void RefreshSteps()
        {
            if (Steps == null) return;
            foreach (StepTab tab in Steps)
            {
                tab.IsCurrent = tab.Step == _nav.Current;
                tab.IsDone = _nav.IsDone(tab.Step);
                tab.IsEnabled = _nav.CanOpen(tab.Step);
            }
            OnPropertyChanged(nameof(Step));
            OnPropertyChanged(nameof(IsLayersStep));
            OnPropertyChanged(nameof(IsPreviewStep));
            OnPropertyChanged(nameof(IsBuildStep));
            OnPropertyChanged(nameof(IsReviewStep));
        }

        // ───────── review (step 4) ─────────

        private BuildReport _lastBuild;
        /// <summary>The report the Review step shows; null before the first build.</summary>
        public BuildReport LastBuild { get => _lastBuild; private set => SetField(ref _lastBuild, value); }

        private string _reviewHeadline = "";
        public string ReviewHeadline { get => _reviewHeadline; private set => SetField(ref _reviewHeadline, value); }

        public ObservableCollection<CreatedRow> CreatedRows { get; } = new ObservableCollection<CreatedRow>();
        public ObservableCollection<SkipGroup> SkipGroups { get; } = new ObservableCollection<SkipGroup>();
        public ObservableCollection<string> Warnings { get; } = new ObservableCollection<string>();

        private string _skippedTitle = "SKIPPED";
        public string SkippedTitle { get => _skippedTitle; private set => SetField(ref _skippedTitle, value); }
        private string _warningsTitle = "WARNINGS";
        public string WarningsTitle { get => _warningsTitle; private set => SetField(ref _warningsTitle, value); }
        public bool HasSkipped => SkipGroups.Count > 0;
        public bool HasWarnings => Warnings.Count > 0;

        public bool LastBuildWasNewFile => _lastBuild != null && _lastBuild.Target == BuildTarget.NewFile;

        public bool CanUndoBuild => _lastBuild != null && !_busy && !JobBlocks;

        /// <summary>Select in model: what the last build made, one category or (null) all four.</summary>
        public void SelectCreated(BuildCategory? category)
        {
            if (_lastBuild == null) return;
            List<long> ids = category.HasValue
                ? _lastBuild.IdsOf(category.Value)
                : new[] { BuildCategory.Wall, BuildCategory.Door, BuildCategory.Window, BuildCategory.Room }
                    .SelectMany(c => _lastBuild.IdsOf(c)).ToList();
            if (ids.Count == 0)
            {
                Status = "Nothing of that kind was created.";
                return;
            }

            RunJob(new BuildRequest
            {
                Kind = BuildJobKind.Select,
                ElementIds = ids,
                DocumentKey = _lastBuild.DocumentKey,
            }, "Selecting…");
        }

        /// <summary>Marks one skipped group red in the viewport.</summary>
        public void ShowSkipGroup(SkipGroup group)
        {
            if (group == null) return;
            _highlights = group.Items.ToList();
            PushOverlay();
            Status = "Showing " + group.CountText + " skipped: " + group.ReasonText + ".";
        }

        /// <summary>
        /// Undo this build: deletes every id the last build recorded in one Revit transaction,
        /// then returns to Preview with those walls unbuilt. A build saved as a new .rvt has
        /// nothing to delete in this project; the drafter is told to delete the file instead.
        /// </summary>
        public void UndoBuild()
        {
            if (_lastBuild == null || _busy || JobBlocks) return;
            if (LastBuildWasNewFile)
            {
                Status = "That build was saved as " + Path.GetFileName(_lastBuild.OutputPath) +
                         ". To discard it, close it in Revit and delete the file.";
                return;
            }

            List<long> ids = _lastBuild.AllCreatedIds();
            if (ids.Count == 0)
            {
                Status = "That build left nothing in the model to undo.";
                return;
            }

            RunJob(new BuildRequest
            {
                Kind = BuildJobKind.Undo,
                ElementIds = ids,
                DocumentKey = _lastBuild.DocumentKey,
            }, "Undoing the build…");
        }

        /// <summary>Build more: back to Preview to brush or restore walls and build again.</summary>
        public void BuildMore()
        {
            LeaveReview(CadToBimStep.Preview);
            Status = "Brush, restore or pick, then build again: only new walls are added.";
        }

        /// <summary>Done: the review is closed and the pane goes back to the start.</summary>
        public void Done()
        {
            LeaveReview(CadToBimStep.Layers);
            Status = "Done. Use Change… to convert another drawing.";
        }

        /// <summary>Closes the review (its markers too) and opens <paramref name="step"/>.</summary>
        private void LeaveReview(CadToBimStep step)
        {
            ClearHighlights();
            _nav.ClearReview();
            GoTo(step);
            RefreshSteps();
        }

        private void ClearHighlights()
        {
            if (_highlights.Count == 0) return;
            _highlights = new List<SkippedItem>();
            PushOverlay();
        }

        // ───────── open / detect ─────────

        public async Task OpenAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (_building)
            {
                Status = "Wait for the build to finish.";
                return;
            }

            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;

            // A different drawing: its own remembered roles, and the flow starts again.
            bool sameDrawing = _session != null &&
                               string.Equals(_session.DrawingPath, path, StringComparison.OrdinalIgnoreCase);
            if (!sameDrawing && !string.Equals(_drawingPath, path, StringComparison.OrdinalIgnoreCase))
            {
                LoadRoles(path);
                _nav.Reset();
                LastBuild = null;
                _highlights = new List<SkippedItem>();
                RefreshSteps();
            }

            Busy = true;
            DrawingPath = path;
            DrawingName = Path.GetFileName(path);
            Status = "Reading…";

            var progress = new Progress<string>(s => Status = s);
            var roles = new Dictionary<string, LayerRole>(_roles, StringComparer.OrdinalIgnoreCase);
            double sMin = _sMin, sMax = _sMax;
            CadToBimSettings settings = _settings;

            bool gated = false;
            try
            {
                // Wait for any detect/brush already in flight to finish before this one touches
                // the engine's static thresholds. The wait itself is cancellable: a superseded
                // open drops out here instead of running once it reaches the front of the queue.
                await _engineGate.WaitAsync(cts.Token);
                gated = true;
                if (cts.IsCancellationRequested) return;

                DetectResult result = await Task.Run(
                    () => _detect(path, settings, sMin, sMax, roles, progress, cts.Token), cts.Token);
                if (cts.IsCancellationRequested) return;
                Apply(result, sMin, sMax);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer open, or the pane closed.
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) return;
                Session = null;
                _result = null;
                Layers = new ObservableCollection<LayerViewModel>();
                LayerSummary = "";
                Bounds = Rect.Empty;
                RecountAndRedraw();
                Status = Describe(ex);
            }
            finally
            {
                if (gated) _engineGate.Release();
                if (ReferenceEquals(_cts, cts)) Busy = false;
            }
        }

        /// <summary>Re-run detection on the open drawing (thresholds, roles, settings changed).</summary>
        public void Redetect()
        {
            if (_session == null || _building) return;
            var _ = OpenAsync(_session.DrawingPath);
        }

        /// <summary>Stop an in-flight detection (pane closed, "Change…" pressed).</summary>
        public void Cancel() => _cts?.Cancel();

        private void Apply(DetectResult result, double sMin, double sMax)
        {
            CadToBimSession fresh = result.Session;
            CadToBimSession previous = _session;

            if (previous != null && string.Equals(previous.DrawingPath, fresh.DrawingPath, StringComparison.OrdinalIgnoreCase))
            {
                CarryForward(previous, fresh);
            }
            else
            {
                _boxes.Clear();
            }

            Session = fresh;
            _result = result;
            _detectedSMin = sMin;
            _detectedSMax = sMax;

            var names = new HashSet<string>(result.Census.Keys, StringComparer.OrdinalIgnoreCase);
            names.UnionWith(result.LayerShapes.Keys);

            var rows = new List<CadLayerViewModel>();
            foreach (string name in names)
            {
                List<object> shapes;
                if (!result.LayerShapes.TryGetValue(name, out shapes)) shapes = new List<object>();
                int count;
                if (!result.Census.TryGetValue(name, out count) || count == 0) count = shapes.Count;

                RoleGuess guess = LayerRoleRules.Guess(name, _settings, _roles);
                int? faces = null;
                if (result.WallFacesByLayer != null)
                    faces = result.WallFacesByLayer.TryGetValue(name, out int f) ? f : 0;
                string look = LayerRoleRules.NeedsALook(guess.Role, guess.ByUser, count, faces);

                rows.Add(new CadLayerViewModel(name, count, guess.Role, guess.Reason, look, OnRoleChosen)
                {
                    Items = shapes,
                    IsVisible = guess.Role != LayerRole.Ignore,
                });
            }

            List<CadLayerViewModel> ordered = SortLayers(rows);
            Layers = new ObservableCollection<LayerViewModel>(ordered);
            int flagged = ordered.Count(l => l.NeedsLook);
            LayerSummary = ordered.Count + (flagged > 0 ? " · " + flagged + " need a look" : "");

            Bounds = result.Bounds;
            PlanCount = result.PlanCount;
            UnitsText = result.UnitsText;
            ReadText = result.ReadSeconds.ToString("0.00") + " s · " + result.Entities.ToString("N0") + " entities";

            RecountAndRedraw();
            int doors = fresh.Openings.Count(o => o.IsDoor), windows = fresh.Openings.Count(o => !o.IsDoor);
            Status = "Detected " + CountOf(fresh.Walls.Count, BuildCategory.Wall) + ", " + CountOf(doors, BuildCategory.Door) + ", " +
                     CountOf(windows, BuildCategory.Window) + ", " + CountOf(fresh.Spaces.Count, BuildCategory.Room) +
                     " in " + result.ReadSeconds.ToString("0.0") + " s.";
            if (!string.IsNullOrEmpty(result.AecWarning)) Status += " · " + result.AecWarning;
        }

        /// <summary>Flagged ("need a look") layers first, then by name.</summary>
        internal static List<CadLayerViewModel> SortLayers(IEnumerable<CadLayerViewModel> layers) =>
            layers.OrderBy(l => l.NeedsLook ? 0 : 1)
                  .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                  .ToList();

        // ───────── correct ─────────

        public void ToggleErase(CadWall wall)
        {
            if (_session == null || wall == null || _building) return;
            _session.ToggleErase(wall);
            RecountAndRedraw();
            Status = _session.Erased.Contains(wall)
                ? "Wall left out. Click it again to restore."
                : "Wall restored.";
        }

        /// <summary>Pick tool: leave one door or window out of the build, or bring it back.</summary>
        public void TogglePick(Opening opening)
        {
            if (_session == null || opening == null || _building) return;
            _session.TogglePick(opening);
            RecountAndRedraw();
            string what = opening.IsDoor ? "Door" : "Window";
            Status = _session.IsPickedOut(opening)
                ? what + " left out. Click it again to bring it back."
                : what + " brought back.";
        }

        /// <summary>The role dropdown changed: remember it for this drawing, re-detect once.</summary>
        public void SetRole(CadLayerViewModel layer, LayerRole role)
        {
            if (layer == null || _building) return;
            if (_roles.TryGetValue(layer.Name, out LayerRole current) && current == role && layer.Role == role) return;

            layer.Role = role;
            _roles[layer.Name] = role;

            string path = _session?.DrawingPath ?? _drawingPath;
            if (!string.IsNullOrEmpty(path))
            {
                _settings.SetRoleOverride(path, layer.Name, LayerRoles.Key(role));
                try { _saveSettings(_settings); } catch { /* a settings write is never fatal */ }
            }

            Redetect();
        }

        private void OnRoleChosen(CadLayerViewModel layer, LayerRole role) => SetRole(layer, role);

        /// <summary>The drafter's remembered roles for this drawing replace the in-memory ones.</summary>
        private void LoadRoles(string path)
        {
            _roles.Clear();
            foreach (KeyValuePair<string, string> pair in _settings.RoleOverridesFor(path))
            {
                if (LayerRoles.TryParse(pair.Value, out LayerRole role)) _roles[pair.Key] = role;
            }
        }

        public void ApplySettings(CadToBimSettings settings)
        {
            if (settings == null) return;
            _settings = settings;
            _sMin = settings.SMinMm;
            _sMax = settings.SMaxMm;
            _height = settings.WallHeightMm;
            OnPropertyChanged(nameof(SMinMm));
            OnPropertyChanged(nameof(SMaxMm));
            OnPropertyChanged(nameof(HeightMm));
            Redetect();
        }

        public void SetLevels(IEnumerable<LevelChoice> levels)
        {
            Levels.Clear();
            if (levels != null)
            {
                foreach (LevelChoice level in levels) Levels.Add(level);
            }
            SelectedLevel = Levels.Count > 0 ? Levels[0] : null;
            UpdateLevelForecast();
        }

        private void Bounce()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        private void OnDebounced()
        {
            if (_session == null) return;
            bool thresholdsChanged = Math.Abs(_sMin - _detectedSMin) > 1e-9 || Math.Abs(_sMax - _detectedSMax) > 1e-9;
            if (!thresholdsChanged) return;   // height only matters at build time
            if (_sMin <= 0 || _sMin >= _sMax)
            {
                Status = "Thickness range must be 0 < min < max.";
                return;
            }
            Redetect();
        }

        /// <summary>"Will create N new level(s): …" for the Build step. Mirrors the builder's
        /// LevelFor through LevelPlanner; levels come from SetLevels (elevation order).</summary>
        private void UpdateLevelForecast()
        {
            if (_session == null || _target != BuildTarget.AddToProject || Levels.Count == 0)
            {
                LevelForecast = "";
                return;
            }

            int plans = 1;
            if (_storeys == StoreyMode.Stack)
            {
                List<CadWall> layout = _session.Active();
                plans = layout.Count == 0 ? 0 : CadClassifier.ClusterPlans(layout).Count;
            }

            int baseIndex = _selectedLevel != null ? Levels.IndexOf(_selectedLevel) : 0;
            List<string> made = LevelPlanner.NewLevels(Levels.Select(l => l.Name).ToList(), baseIndex, plans);
            LevelForecast = made.Count == 0 ? ""
                : "Will create " + made.Count + (made.Count == 1 ? " new level: " : " new levels: ") + string.Join(", ", made);
        }

        /// <summary>The open model's wall, door and window types (Cad2BimBuilder.ReadTypes,
        /// read with the levels). TypeCatalog.None when no project is open.</summary>
        public void SetTypeCatalog(TypeCatalog catalog)
        {
            _typeCatalog = catalog ?? TypeCatalog.None;
            UpdateTypes();
        }

        private void UpdateTypes()
        {
            var (pending, openings, _) = PendingSelection();
            UpdateTypes(pending, openings);
        }

        /// <summary>Rebuilds the TYPES rows for what the next build creates. Each row starts at
        /// the builder's own choice, or the drafter's earlier pick for that size.</summary>
        private void UpdateTypes(List<CadWall> pending, List<Opening> openings)
        {
            List<TypeRowPlan> plans = _session == null
                ? new List<TypeRowPlan>()
                : BuildTypePlan.Rows(pending.Select(w => w.Thickness), openings,
                                     _settings.DoorHeightMm, _settings.WindowHeightMm, _typeCatalog);

            bool listed = _target == BuildTarget.AddToProject && _typeCatalog.Known;
            _allTypeRows = listed
                ? plans.Select(p => new TypeRowViewModel(p, Remembered(p), OnTypePicked)).ToList()
                : new List<TypeRowViewModel>();
            ShowTypeRows();

            int sizes = plans.Count;
            TypesSummary = sizes == 0 ? "" : sizes + (sizes == 1 ? " size" : " sizes");
            if (sizes == 0)
                TypesNote = "";
            else if (_target == BuildTarget.NewFile)
                TypesNote = "New file: each size gets a template type of that size, or a new CAD type.";
            else if (!_typeCatalog.Known)
                TypesNote = "Types are picked when you build: a type of each size, or a new CAD type.";
            else
                TypesNote = "Missing sizes are made as new types.";
        }

        private TypeRowOption Remembered(TypeRowPlan plan)
        {
            if (!_typePicks.TryGetValue(plan.Key, out long id)) return null;
            return id == PickedNew
                ? plan.Options.FirstOrDefault(o => o.IsNew)
                : plan.Options.FirstOrDefault(o => o.TypeId == id);
        }

        private void OnTypePicked(TypeRowViewModel row)
        {
            TypeRowOption picked = row.Selected;
            if (picked == null) return;
            if (picked == row.Plan.Default) _typePicks.Remove(row.Plan.Key);
            else _typePicks[row.Plan.Key] = picked.IsNew ? PickedNew : picked.TypeId;
        }

        private void ShowTypeRows()
        {
            List<TypeRowViewModel> walls = _allTypeRows.Where(r => r.Plan.Slot == TypeSlot.Wall).ToList();
            List<TypeRowViewModel> shownWalls = walls;
            int hidden = walls.Count - BuildTypePlan.WallRowsShown;
            if (!_showAllTypes && hidden > 0)
            {
                // The most used widths, still listed thinnest first.
                var top = new HashSet<TypeRowViewModel>(walls
                    .OrderByDescending(r => r.Plan.Count).ThenBy(r => r.Plan.WidthMm)
                    .Take(BuildTypePlan.WallRowsShown));
                shownWalls = walls.Where(top.Contains).ToList();
            }

            TypeRows.Clear();
            foreach (TypeRowViewModel row in shownWalls) TypeRows.Add(row);
            foreach (TypeRowViewModel row in _allTypeRows.Where(r => r.Plan.Slot != TypeSlot.Wall)) TypeRows.Add(row);

            MoreTypesText = hidden <= 0 ? ""
                : _showAllTypes ? "Show fewer"
                : "Show " + hidden + " more wall " + (hidden == 1 ? "width" : "widths");
            OnPropertyChanged(nameof(HasTypeRows));
        }

        /// <summary>What goes to the builder: every row set to an existing type (hidden wall
        /// rows included). "New: CAD …" rows are left to the builder.</summary>
        internal List<TypeChoice> TypeChoices() =>
            _target == BuildTarget.AddToProject && _typeCatalog.Known
                ? BuildTypePlan.Choices(_allTypeRows.Select(r => (r.Plan, r.Selected)))
                : null;

        // ───────── brush ─────────

        public async Task BrushAsync(BoxMm box)
        {
            if (_session == null)
            {
                Status = "Open a DWG first";
                return;
            }
            if (_busy) return;

            Busy = true;
            Status = "Brushing…";
            string path = _session.DrawingPath;
            CadToBimSettings settings = _settings;
            double median = _session.MedianThicknessMm;

            try
            {
                // Same gate as OpenAsync: Cad2BimBrush.Run touches Wall's static thresholds too
                // (MinFaceAspect/MinFaceLength as well as SMin/SMax), so a brush can never run
                // while a detect (or another brush) is mid-read.
                await _engineGate.WaitAsync();
                BrushResult result;
                try
                {
                    result = await Task.Run(() => Cad2BimBrush.Run(path, box, settings, median));
                }
                finally
                {
                    _engineGate.Release();
                }

                if (result == null || result.Walls == null || result.Walls.Count == 0)
                {
                    Status = string.IsNullOrEmpty(result?.Note)
                        ? "No wall found inside that box. Try a box that follows one wall rather than a room."
                        : result.Note;
                    return;
                }

                if (result.Walls.Count > BrushConfirmAbove)
                {
                    _pendingBox = box;
                    PendingBrushConfirm = result;
                    Status = result.Walls.Count + " walls from that box — confirm?";
                    return;
                }

                AcceptBrush(result, box);
            }
            catch (Exception ex)
            {
                Status = Describe(ex);
            }
            finally
            {
                Busy = false;
            }
        }

        public void ResolveBrushConfirm(bool accept)
        {
            BrushResult result = PendingBrushConfirm;
            PendingBrushConfirm = null;
            if (result == null) return;
            if (accept) AcceptBrush(result, _pendingBox);
            else Status = "Brush cancelled.";
        }

        private void AcceptBrush(BrushResult result, BoxMm box)
        {
            _session.AddForced(result.Walls);
            _boxes.Add(box);
            RecountAndRedraw();
            Status = result.Walls.Count + (result.Walls.Count == 1 ? " wall" : " walls") + " brushed in." +
                     (string.IsNullOrEmpty(result.Note) ? "" : " " + result.Note);
        }

        // ───────── build ─────────

        /// <summary>What the next build creates: pending walls, the openings in them that Pick
        /// did not leave out, and the rooms whose walls are ALL pending (a room that gained one
        /// brushed wall while the rest were already built must not be created twice, F7).</summary>
        private (List<CadWall> Walls, List<Opening> Openings, List<Space> Spaces) PendingSelection()
        {
            if (_session == null) return (new List<CadWall>(), new List<Opening>(), new List<Space>());

            List<CadWall> pending = TracksBuilt ? _session.Pending() : _session.Active();
            var pendingSet = new HashSet<CadWall>(pending);
            List<Opening> openings = _session.Openings
                .Where(o => o.Wall != null && pendingSet.Contains(o.Wall) && !_session.IsPickedOut(o))
                .ToList();
            List<Space> spaces = _session.Spaces
                .Where(s =>
                {
                    List<CadWall> spaceWalls = s.SubElements.OfType<CadWall>().ToList();
                    return spaceWalls.Count > 0 && spaceWalls.All(pendingSet.Contains);
                })
                .ToList();
            return (pending, openings, spaces);
        }

        public void Confirm()
        {
            if (_busy || JobBlocks) return;
            if (_session == null)
            {
                Status = "Open a DWG first";
                return;
            }

            var (pending, openings, spaces) = PendingSelection();
            if (pending.Count == 0)
            {
                // Walls undone with Ctrl+Z are still listed as built until the model is asked.
                if (TracksBuilt && _session.BuiltWallIds.Count > 0 && CheckBuilt()) return;
                Status = "Nothing new to build. Brush or restore a wall, then build again.";
                return;
            }

            string output = null;
            if (_target == BuildTarget.NewFile)
            {
                output = Path.ChangeExtension(_session.DrawingPath, ".rvt");
                if (File.Exists(output) && OverwritePrompt != null && !OverwritePrompt(output))
                {
                    Status = "Kept the existing " + Path.GetFileName(output) + ".";
                    return;
                }
            }

            // Rows for exactly this pending set, with the drafter's picks carried over.
            UpdateTypes(pending, openings);

            var request = new BuildRequest
            {
                Kind = BuildJobKind.Build,
                Target = _target,
                DrawingPath = _session.DrawingPath,
                Walls = pending,
                // The full layout, not just this Confirm's pending subset - keeps plan origins
                // and level indices stable across Confirms (finding F1). The builder still
                // CREATES only Walls; openings/spaces below are already filtered to it.
                LayoutWalls = _session.Active(),
                Openings = openings,
                Spaces = spaces,
                HeightMm = _height,
                Storeys = _storeys,
                LevelId = _target == BuildTarget.AddToProject ? _selectedLevel?.Id : null,
                OutputPath = output,
                TemplatePath = string.IsNullOrWhiteSpace(_settings.TemplatePath) ? null : _settings.TemplatePath,
                WindowSillMm = _settings.WindowSillMm,
                // Checked on the Revit thread before anything is created (Ctrl+Z sync).
                // Only walls this build leaves out as built (none for a new file).
                BuiltWallIds = TracksBuilt ? _session.BuiltWallIds.Values.ToList() : new List<long>(),
                DoorHeightMm = _settings.DoorHeightMm,
                WindowHeightMm = _settings.WindowHeightMm,
                JoinWalls = _settings.JoinWalls,
                TypeChoices = TypeChoices(),
            };

            _erasedAtBuild = _session.Erased.Count;
            _pickedAtBuild = _session.PickedOut.Count;
            _buildClock = Stopwatch.StartNew();
            _building = true;
            if (!RunJob(request, "Building " + pending.Count + (pending.Count == 1 ? " wall…" : " walls…")))
            {
                _building = false;
                OnPropertyChanged(nameof(CanEdit));
            }
        }

        /// <summary>Asks Revit which walls recorded as built are gone (Ctrl+Z). False when there
        /// is nothing to ask or another job is running.</summary>
        public bool CheckBuilt()
        {
            if (_session == null || _session.BuiltWallIds.Count == 0 || _target != BuildTarget.AddToProject ||
                _busy || _job != null) return false;
            return RunJob(new BuildRequest
            {
                Kind = BuildJobKind.CheckBuilt,
                ElementIds = _session.BuiltWallIds.Values.ToList(),
            }, null);
        }

        /// <summary>Hands one job to the handler. Build and Undo lock the pane; CheckBuilt and
        /// Select do not. False when Revit would not take it.</summary>
        private bool RunJob(BuildRequest request, string status)
        {
            if (!_completedHooked)
            {
                _handler.Completed += OnBuilt;
                _completedHooked = true;
            }

            bool locks = request.Kind == BuildJobKind.Build || request.Kind == BuildJobKind.Undo;
            _handler.Request = request;
            _job = request.Kind;
            if (locks) Busy = true;
            if (status != null) Status = status;
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(CanUndoBuild));

            if (_raiser.Raise()) return true;

            if (ReferenceEquals(_handler.Request, request)) _handler.Request = null;
            _job = null;
            if (locks) Busy = false;
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(CanUndoBuild));
            if (status != null) Status = "Revit is busy, try again";
            return false;
        }

        // Called from CadToBimBuildHandler.Execute on Revit's main thread; hop to the dispatcher.
        private void OnBuilt(BuildReport report) => _onUi(() => ApplyReport(report));

        private void ApplyReport(BuildReport report)
        {
            BuildJobKind kind = report?.Kind ?? _job ?? BuildJobKind.Build;
            _job = null;
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(CanUndoBuild));

            switch (kind)
            {
                case BuildJobKind.CheckBuilt: ApplyCheck(report); return;
                case BuildJobKind.Undo: ApplyUndo(report); return;
                case BuildJobKind.Select: ApplySelect(report); return;
                default: ApplyBuild(report); return;
            }
        }

        private void ApplyCheck(BuildReport report)
        {
            if (report == null || !report.Ok || _session == null) return;
            if (!string.IsNullOrEmpty(report.DocumentKey)) _activeDocumentKey = report.DocumentKey;
            if (!TracksBuilt)
            {
                // Another project is active: the built ids belong to the one the walls went
                // into, so keep them for when it is active again and count every wall here.
                RecountAndRedraw();
                if (_target == BuildTarget.AddToProject)
                    Status = "The walls built earlier went into " + Path.GetFileName(_session.BuiltDocumentKey) +
                             ", not this project, so every wall counts here.";
                return;
            }
            int forgotten = _session.ForgetBuilt(report.MissingIds);
            RecountAndRedraw();
            if (forgotten > 0)
                Status = forgotten + (forgotten == 1 ? " wall" : " walls") +
                         " from an earlier build are no longer in the model (undone in Revit?). They are back in the build list.";
            else if (_wallCount == 0)
                Status = "Everything detected is already built. Brush or restore a wall to build more.";
        }

        private void ApplySelect(BuildReport report)
        {
            if (report == null) return;
            Status = !report.Ok ? report.Error
                   : report.Affected == 0 ? "None of those elements are in the model any more."
                   : "Selected " + report.Affected + " in the model.";
        }

        private void ApplyUndo(BuildReport report)
        {
            Busy = false;
            if (report == null) return;
            if (!report.Ok)
            {
                Status = "Undo failed: " + report.Error;
                return;
            }

            string done = UndoSummary.DoneText(_lastBuild, report);
            _session?.ForgetBuilt(report.MissingIds);
            var gone = new HashSet<long>(report.MissingIds);
            foreach (LevelChoice level in Levels.Where(l => gone.Contains(l.Id)).ToList())
            {
                if (ReferenceEquals(level, _selectedLevel)) SelectedLevel = Levels.FirstOrDefault(l => !gone.Contains(l.Id));
                Levels.Remove(level);
            }
            LastBuild = null;
            OnPropertyChanged(nameof(LastBuildWasNewFile));
            OnPropertyChanged(nameof(CanUndoBuild));
            LeaveReview(CadToBimStep.Preview);
            RecountAndRedraw();
            Status = done;
        }

        private void ApplyBuild(BuildReport report)
        {
            double seconds = _buildClock != null ? _buildClock.Elapsed.TotalSeconds
                           : report != null ? report.Elapsed.TotalSeconds : 0;
            _building = false;

            if (report == null)
            {
                Busy = false;
                Status = "Build finished without a report.";
                return;
            }

            if (report.Stale)
            {
                // Nothing was built: the pane's list was out of date. Put the undone walls back
                // and let the drafter see the new count before building.
                int forgotten = _session?.ForgetBuilt(report.MissingIds) ?? 0;
                RecountAndRedraw();
                Busy = false;
                Status = forgotten + (forgotten == 1 ? " wall" : " walls") +
                         " from an earlier build are no longer in the model (undone in Revit?). " +
                         "Nothing was built. Check the count, then build again.";
                return;
            }

            // A new .rvt is a copy of the whole drawing: nothing in it counts as built for the
            // project the pane builds into, so only "Add to this project" builds are recorded.
            bool wasNewFile = _target == BuildTarget.NewFile;
            if (report.Ok && _session != null && !wasNewFile) _session.MarkBuilt(report);
            if (report.Ok && !string.IsNullOrEmpty(report.DocumentKey)) _activeDocumentKey = report.DocumentKey;
            RecountAndRedraw();
            Busy = false;

            if (!report.Ok)
            {
                // A NewFile failure's message is already the drafter's next move (builder's own
                // wording, e.g. "could not save <path>: ... — build into the open project instead,
                // or free the file and Confirm again"); a "Build failed: " prefix would bury it.
                // Target is left unchanged so a retry (or switching to Add to project) is still
                // there to try (finding F5).
                Status = _target == BuildTarget.NewFile ? report.Error : "Build failed: " + report.Error;
                return;
            }

            // A successful NewFile build saved the drawing beside the dwg and switched Revit's
            // active document to it (Cad2BimBuilder.Build, OpenAndActivateDocument). The target
            // stays "Save as new .rvt": its walls are not recorded as built, so switching to
            // "Add to this project" here would build them all a second time.
            report.Target = wasNewFile ? BuildTarget.NewFile : BuildTarget.AddToProject;

            FillReview(report, seconds);
            if (!wasNewFile) AddNewLevels(report);
            _nav.Built();
            RefreshSteps();

            var text = new StringBuilder();
            text.Append("Done in ").Append(seconds.ToString("0.0")).Append(" s — ")
                .Append(CountOf(report.Walls, BuildCategory.Wall)).Append(", ").Append(CountOf(report.Doors, BuildCategory.Door)).Append(", ")
                .Append(CountOf(report.Windows, BuildCategory.Window)).Append(", ").Append(CountOf(report.Rooms, BuildCategory.Room)).Append('.');
            if (report.Skipped.Count > 0)
                text.Append(' ').Append(report.Skipped.Count).Append(" skipped, see the list.");
            if (wasNewFile)
                text.Append(" Saved ").Append(report.OutputPath).Append(" and opened it.");
            else
                text.Append(" One undo step.");
            Status = text.ToString();
        }

        /// <summary>Fills the Review step from a successful build.</summary>
        private void FillReview(BuildReport report, double seconds)
        {
            LastBuild = report;
            _highlights = new List<SkippedItem>();

            ReviewHeadline = "Built in " + seconds.ToString("0.0") + " s" +
                             (report.LevelNames.Count > 0 ? " on " + string.Join(", ", report.LevelNames) : "");

            CreatedRows.Clear();
            CreatedRows.Add(new CreatedRow { Category = BuildCategory.Wall, Label = "Walls", Count = report.Walls });
            CreatedRows.Add(new CreatedRow { Category = BuildCategory.Door, Label = "Doors", Count = report.Doors });
            CreatedRows.Add(new CreatedRow { Category = BuildCategory.Window, Label = "Windows", Count = report.Windows });
            CreatedRows.Add(new CreatedRow { Category = BuildCategory.Room, Label = "Rooms", Count = report.Rooms });

            SkipGroups.Clear();
            foreach (SkipGroup group in report.SkipGroups()) SkipGroups.Add(group);
            SkippedTitle = "SKIPPED (" + report.Skipped.Count + ")";

            Warnings.Clear();
            foreach (string warning in BuildWarnings(report, _erasedAtBuild, _pickedAtBuild,
                                                     _result != null ? _result.UnpairedWallLines : 0))
                Warnings.Add(warning);
            WarningsTitle = "WARNINGS (" + Warnings.Count + ")";

            OnPropertyChanged(nameof(HasSkipped));
            OnPropertyChanged(nameof(HasWarnings));
            OnPropertyChanged(nameof(LastBuildWasNewFile));
            OnPropertyChanged(nameof(CanUndoBuild));
        }

        /// <summary>Levels the build made go to the top of the picker (they sit above every other),
        /// so the next forecast does not announce them again.</summary>
        private void AddNewLevels(BuildReport report)
        {
            if (report.NewLevelIds.Count != report.NewLevelNames.Count) return;
            for (int i = 0; i < report.NewLevelIds.Count; i++)
            {
                if (Levels.Any(l => l.Id == report.NewLevelIds[i])) continue;
                Levels.Add(new LevelChoice { Name = report.NewLevelNames[i], Id = report.NewLevelIds[i] });
            }
        }

        /// <summary>Things that were built but may surprise the drafter (design §6 "Warnings").</summary>
        internal static List<string> BuildWarnings(BuildReport report, int erased, int picked, int unpaired)
        {
            var warnings = new List<string>();
            if (report == null) return warnings;
            foreach (string level in report.NewLevelNames)
                warnings.Add("New level made: " + level + (report.NewViewIds.Count > 0 ? " (with a floor plan)" : ""));
            if (erased > 0)
                warnings.Add(erased + (erased == 1 ? " wall" : " walls") + " erased in preview");
            if (picked > 0)
                warnings.Add(picked + (picked == 1 ? " door or window" : " doors or windows") + " left out with Pick");
            if (unpaired > 0)
                warnings.Add(UnpairedText(unpaired));

            // AI-345: types made for drawn sizes the model lacked, and sizing that did not take.
            string walls = BuildTypePlan.NamesLine("New wall types made: ", report.NewWallTypeNames);
            if (walls != null) warnings.Add(walls);
            string openings = BuildTypePlan.NamesLine("New door/window types made: ", report.NewOpeningTypeNames);
            if (openings != null) warnings.Add(openings);
            if (report.UnsizedOpenings > 0)
                warnings.Add(report.UnsizedOpenings + (report.UnsizedOpenings == 1 ? " door or window keeps its" : " doors or windows keep their") +
                             " family's size (no type could be resized)");
            if (report.JoinsSkipped)
                warnings.Add("Wall joins rolled back: walls stand unjoined, as drawn");
            if (report.DoorsNotTurned > 0)
                warnings.Add(report.DoorsNotTurned + (report.DoorsNotTurned == 1 ? " door" : " doors") +
                             " could not be turned to match the drawing");
            return warnings;
        }

        /// <summary>"1 wall", "2 doors" (BuildReport.Noun).</summary>
        internal static string CountOf(int count, BuildCategory category) => count + " " + BuildReport.Noun(category, count);

        /// <summary>"1 wall-layer line not paired", "3 wall-layer lines not paired".</summary>
        internal static string UnpairedText(int unpaired) =>
            unpaired + (unpaired == 1 ? " wall-layer line" : " wall-layer lines") + " not paired";

        // ───────── recount / overlay ─────────

        private void RecountAndRedraw()
        {
            if (_session == null)
            {
                Overlay = OverlaySnapshot.Empty;
                UpdateCounts();
                return;
            }

            List<CadWall> active = _session.Active();
            var elaborated = Elaborate(_session.Model, active, _settings, _roles);
            _session.Openings.Clear();
            _session.Openings.AddRange(elaborated.Openings);
            _session.Spaces.Clear();
            _session.Spaces.AddRange(elaborated.Spaces);

            UpdateCounts();
            PushOverlay();
            if (IsBuildStep) UpdateLevelForecast();
        }

        private void PushOverlay()
        {
            if (_session == null)
            {
                Overlay = OverlaySnapshot.Empty;
                return;
            }

            List<Opening> kept = _session.Openings.Where(o => !_session.IsPickedOut(o)).ToList();
            List<Opening> picked = _session.Openings.Where(_session.IsPickedOut).ToList();
            Overlay = new OverlaySnapshot(_session.Active(), _session.Erased.ToList(), kept,
                                          _session.Spaces.ToList(), _boxes.ToList(), picked, _highlights.ToList());
        }

        private void UpdateCounts()
        {
            var (pending, openings, spaces) = PendingSelection();
            WallCount = pending.Count;
            // "Will be built": the same openings and rooms the next build request carries
            // (PendingSelection), so ones on walls an earlier build made are not counted again.
            DoorCount = openings.Count(o => o.IsDoor);
            WindowCount = openings.Count(o => !o.IsDoor);
            RoomCount = spaces.Count;
            ErasedCount = _session != null ? _session.Erased.Count : 0;
            ForcedCount = _session != null ? _session.Forced.Count : 0;
            PickedCount = _session != null ? _session.Openings.Count(_session.IsPickedOut) : 0;
            BuiltCount = _session != null && TracksBuilt ? _session.BuiltWallIds.Count : 0;
            ExtraCount = openings.Count + spaces.Count;
            Warning = _result != null && _result.UnpairedWallLines > 0
                ? UnpairedText(_result.UnpairedWallLines) + " — try Brush"
                : "";
            OnPropertyChanged(nameof(ConfirmLabel));
            OnPropertyChanged(nameof(CanConfirm));
            // What is pending decides the TYPES rows (erase, brush, pick, Ctrl+Z sync).
            if (IsBuildStep) UpdateTypes(pending, openings);
        }

        // Re-detection makes new Wall objects; erase marks and built ids follow by centreline,
        // brushed walls by identity (CadToBimSession.CarryForwardFrom, unit-tested in Task 14).
        private static void CarryForward(CadToBimSession from, CadToBimSession to) => to.CarryForwardFrom(from);

        private static void DefaultOnUi(Action action)
        {
            Application app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess()) app.Dispatcher.BeginInvoke(action);
            else action();
        }

        /// <summary>
        /// Sets the process-wide static thresholds Detect and the openings classifier read for
        /// the duration of one engine call: Wall.SMin/SMax (pairing, from the thickness range)
        /// and the door swing radius band CadClassifier.SwingMinRadiusMm/SwingMaxRadiusMm (from
        /// CadToBimSettings.DoorMinRadiusMm/DoorMaxRadiusMm, finding F3 - previously hardcoded in
        /// Openings.cs and never reachable from the settings window). Called only inside the
        /// _engineGate critical section, same rule as the statics it touches (see _engineGate's
        /// own comment above).
        /// </summary>
        internal static void ApplyEngineThresholds(CadToBimSettings settings, double sMinMm, double sMaxMm)
        {
            CadWall.SMin = sMinMm;
            CadWall.SMax = sMaxMm;
            CadClassifier.SwingMinRadiusMm = settings.DoorMinRadiusMm;
            CadClassifier.SwingMaxRadiusMm = settings.DoorMaxRadiusMm;
        }

        // ───────── detection (thread pool) ─────────

        /// <summary>
        /// The pipeline Cad2BimConvertCommand ran, minus Revit: read once through the exclusion
        /// filter to learn the layers, read again with the wall + opening layers included when
        /// the drawing names them, pair faces, add outline walls, dedupe, then openings, rooms and
        /// plan clusters. The viewport linework is walked from the same document, scaled to mm.
        /// </summary>
        public static DetectResult Detect(string path, CadToBimSettings settings, double sMinMm, double sMaxMm,
                                          IReadOnlyDictionary<string, LayerRole> roles, IProgress<string> progress,
                                          CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            progress?.Report("Reading " + Path.GetFileName(path) + "…");

            ACadSharp.CadDocument document = CadRenderSource.Read(path);
            ct.ThrowIfCancellationRequested();

            progress?.Report("Classifying…");
            CadModel survey = ModelSource.Read(document, BaseFilter(settings, roles));

            // Civil 3D / AutoCAD Architecture / MEP: class NAMES alone (AECC_*/AEC_*/AECB_*)
            // must not refuse — those CLASSES records persist even after every AEC object they
            // once described is gone, and a drawing like that can still be plain, readable
            // linework. Read the model first, then refuse only when AEC entities actually exist
            // in this drawing (InstanceCount, IsAnEntity) AND nothing readable came back either;
            // otherwise continue and warn instead of refusing.
            int readableCount = survey.Segments.Count + survey.Outlines.Count + survey.Arcs.Count;
            bool refused = CadSourceVerdict.IsRefused(
                document.Classes.Select(c => (c.DxfName, c.IsAnEntity, c.InstanceCount)),
                readableCount,
                out string aecWarning);
            if (refused) throw new NotSupportedException(UnsupportedSourceMessage);

            List<string> wallLayers = survey.LayerCensus.Keys
                .Where(layer => RoleOf(layer, settings, roles) == LayerRole.Wall)
                .OrderBy(layer => layer, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CadModel model = survey;
            if (wallLayers.Count > 0)
            {
                LayerFilter focused = BaseFilter(settings, roles);
                focused.Include.AddRange(wallLayers);
                // Door and window linework must reach the classifier for openings, even though
                // walls are paired only from the wall layers below.
                focused.Include.AddRange(survey.LayerCensus.Keys.Where(layer =>
                {
                    LayerRole role = RoleOf(layer, settings, roles);
                    return role == LayerRole.Door || role == LayerRole.Window;
                }));
                model = ModelSource.Read(document, focused);
            }
            ct.ThrowIfCancellationRequested();

            ApplyEngineThresholds(settings, sMinMm, sMaxMm);

            List<CadSegment> wallSegments = model.Segments
                .Where(s => s.Layer.Length == 0 || RoleOf(s.Layer, settings, roles) == LayerRole.Wall)
                .ToList();
            // The Wall-role layers' own lines, before merging drops their layer names: used
            // after classification to flag a Wall layer that gave no walls.
            List<CadSegment> wallLayerLines = wallSegments.Where(s => s.Layer.Length > 0).ToList();
            if (wallSegments.Count == 0) wallSegments = model.Segments.ToList();

            wallSegments = CadClassifier.MergeCollinearSegments(wallSegments);
            List<CadWall> walls = CadClassifier.ClassifyWalls(wallSegments);

            var wallFaces = new HashSet<CadSegment>(wallSegments);
            List<CadSegment> elsewhere = model.Segments.Where(s => !wallFaces.Contains(s)).ToList();
            walls.AddRange(CadClassifier.ClassifyWallsElsewhere(walls, elsewhere));
            walls.AddRange(CadClassifier.WallsFromOutlines(model.Outlines));
            walls = CadClassifier.DeduplicateWalls(walls);
            ct.ThrowIfCancellationRequested();

            progress?.Report("Openings and rooms…");
            var session = new CadToBimSession
            {
                DrawingPath = path,
                Scale = model.Scale,
                Model = model,
            };
            session.SetWalls(walls);          // also sets MedianThicknessMm (100 mm when nothing was found)

            var elaborated = Elaborate(model, walls, settings, roles);
            session.Openings.AddRange(elaborated.Openings);
            session.Spaces.AddRange(elaborated.Spaces);

            List<PlanCluster> plans = CadClassifier.ClusterPlans(walls, model.Texts);

            var used = new HashSet<CadSegment>(walls.SelectMany(w => w.Geometry.OfType<CadSegment>()));
            int unpaired = wallSegments.Count(s => !used.Contains(s) && s.Length >= UnpairedMinLengthMm);

            progress?.Report("Drawing…");
            var sink = new LayerSink(model.Scale);
            CadRenderSource.Walk(document, sink);

            double? header = Units.FromHeader(document);
            string units = "1 unit = " + model.Scale.ToString("0.###") + " mm · " +
                           (header.HasValue && Math.Abs(header.Value - model.Scale) < 1e-9 ? "header" : "inferred");

            return new DetectResult
            {
                Session = session,
                LayerShapes = sink.Shapes,
                Census = survey.LayerCensus,
                WallFacesByLayer = CountWallFaces(wallLayerLines, walls),
                Bounds = sink.Bounds(),
                PlanCount = plans.Count,
                Entities = sink.Entities,
                UnpairedWallLines = unpaired,
                ReadSeconds = clock.Elapsed.TotalSeconds,
                UnitsText = units,
                AecWarning = aecWarning,
            };
        }

        // Same three calls ClassificationService.Elaborate makes, on the walls actually in play,
        // with the command's symbol-based opening pass (window linework from window layers only -
        // a door layer's own linework must never turn into a window, spec finding F6).
        // internal (not private): CadToBimViewModelTests exercises it directly, without needing a
        // real DWG on disk (Detect() cannot be driven that way).
        internal static (List<Opening> Openings, List<Space> Spaces) Elaborate(
            CadModel model, List<CadWall> walls, CadToBimSettings settings, IReadOnlyDictionary<string, LayerRole> roles)
        {
            if (model == null || walls == null || walls.Count == 0)
            {
                return (new List<Opening>(), new List<Space>());
            }

            WallGraph graph = CadClassifier.CreateTopologicalPoints(walls);
            List<Space> spaces = CadClassifier.ClassifySpaces(graph, model.Texts);
            CadClassifier.SplitWalls(walls, spaces);

            // Window marks come from Window-role layers. The settings' window hints only make the
            // first guess (LayerRoleRules.Guess); the drafter's dropdown choice decides.
            var roleByLayer = new Dictionary<string, LayerRole>(StringComparer.OrdinalIgnoreCase);
            List<CadSegment> windowLines = model.Segments
                .Where(s =>
                {
                    string layer = s.Layer ?? "";
                    if (!roleByLayer.TryGetValue(layer, out LayerRole role))
                    {
                        role = RoleOf(layer, settings, roles);
                        roleByLayer[layer] = role;
                    }
                    return role == LayerRole.Window;
                })
                .ToList();
            List<Opening> openings = CadClassifier.ClassifyOpeningsFromSymbols(walls, model.Arcs, windowLines);

            return (openings, spaces);
        }

        /// <summary>
        /// Per layer, how many of its lines lie along a detected wall (parallel, within the wall's
        /// half thickness plus a little of its centreline). Every layer present in
        /// <paramref name="lines"/> gets an entry, so a Wall layer that gave no walls reads 0.
        /// </summary>
        internal static Dictionary<string, int> CountWallFaces(IReadOnlyList<CadSegment> lines, IReadOnlyList<CadWall> walls)
        {
            const double SlackMm = 20.0;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (lines == null) return counts;
            foreach (CadSegment line in lines)
                if (!counts.ContainsKey(line.Layer)) counts[line.Layer] = 0;
            if (walls == null || walls.Count == 0) return counts;

            List<CadSegment> centres = walls.Select(w => w.Centerline).ToList();
            double reach = walls.Max(w => w.Thickness) / 2 + SlackMm;
            var index = new SegmentIndex(centres, 1000);

            foreach (CadSegment line in lines)
            {
                var mid = new Cad2Bim.Point((line.P1.x + line.P2.x) / 2, (line.P1.y + line.P2.y) / 2);
                foreach (int i in index.Near(new CadSegment(mid, mid), reach))
                {
                    if (i < 0 || i >= walls.Count) continue;
                    CadWall wall = walls[i];
                    if (!line.isParallelTo(wall.Centerline)) continue;
                    if (CadOverlayViewport.DistanceToSegment(mid.x, mid.y, wall.Centerline) > wall.Thickness / 2 + SlackMm) continue;
                    counts[line.Layer]++;
                    break;
                }
            }

            return counts;
        }

        private static LayerFilter BaseFilter(CadToBimSettings settings, IReadOnlyDictionary<string, LayerRole> roles)
        {
            LayerFilter filter = settings.ToLayerFilter();
            foreach (KeyValuePair<string, LayerRole> role in roles)
            {
                if (role.Value == LayerRole.Ignore)
                {
                    filter.Exclude.Add(role.Key);
                }
                else
                {
                    // A chip set to wall/opening/other beats a default exclusion glob (GRID*, FURN*).
                    filter.Exclude.RemoveAll(glob => LayerFilter.Matches(role.Key, glob));
                }
            }
            return filter;
        }

        /// <summary>The drafter's role for a layer, else the first guess (LayerRoleRules.Guess).</summary>
        internal static LayerRole RoleOf(string layer, CadToBimSettings settings, IReadOnlyDictionary<string, LayerRole> overrides) =>
            LayerRoleRules.Guess(layer, settings, overrides).Role;

        internal static string Describe(Exception ex)
        {
            Exception root = ex;
            while (root.InnerException != null) root = root.InnerException;
            string message = root.Message;
            if (message.IndexOf("EXPORTTOAUTOCAD", StringComparison.Ordinal) >= 0) return message;   // Detect already said it
            string lower = message.ToLowerInvariant();
            if (root is NotSupportedException || lower.Contains("proxy") || lower.Contains("aecc") ||
                lower.Contains("aec_") || lower.Contains("not supported"))
            {
                // A reader failure that smells like a vertical-app drawing gets the same advice.
                message += " — " + UnsupportedSourceMessage;
            }
            return message;
        }

        /// <summary>Viewport linework grouped by layer, scaled to millimetres so it sits under
        /// the overlay (ModelSource rescales the classifier's model; Flatten does not).</summary>
        private sealed class LayerSink : ICadSink
        {
            private readonly double _scale;
            private double _minX = double.MaxValue, _minY = double.MaxValue;
            private double _maxX = double.MinValue, _maxY = double.MinValue;

            public LayerSink(double scale) { _scale = scale <= 0 ? 1.0 : scale; }

            public Dictionary<string, List<object>> Shapes { get; } =
                new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

            public int Entities { get; private set; }

            public void Polyline(IReadOnlyList<(double X, double Y)> points, bool isClosed, string layer, CadSource source)
            {
                if (points.Count >= 2) Add(layer, points, isClosed);
            }

            public void Arc(IReadOnlyList<(double X, double Y)> points, ArcParams? parameters, string layer, CadSource source)
            {
                if (points.Count >= 2) Add(layer, points, false);
            }

            public void Text(double x, double y, double height, string value, string layer, CadSource source)
            {
                // Text is not stroked; room names come back through the classifier as labels.
            }

            private void Add(string layer, IReadOnlyList<(double X, double Y)> raw, bool isClosed)
            {
                var points = new List<(double X, double Y)>(raw.Count);
                foreach ((double X, double Y) p in raw)
                {
                    double x = p.X * _scale, y = p.Y * _scale;
                    points.Add((x, y));
                    if (x < _minX) _minX = x;
                    if (y < _minY) _minY = y;
                    if (x > _maxX) _maxX = x;
                    if (y > _maxY) _maxY = y;
                }

                string name = string.IsNullOrEmpty(layer) ? "0" : layer;
                List<object> list;
                if (!Shapes.TryGetValue(name, out list))
                {
                    list = new List<object>();
                    Shapes[name] = list;
                }
                list.Add(new PolylineShape(points, isClosed));
                Entities++;
            }

            public Rect Bounds() =>
                _minX > _maxX ? Rect.Empty : new Rect(_minX, _minY, _maxX - _minX, _maxY - _minY);
        }
    }
}
