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
    /// <summary>What a layer is for, as far as the classifier is concerned.</summary>
    public enum LayerRole { Other, Wall, Opening, Ignore }

    /// <summary>One row of the Layers list: the upstream LayerViewModel (name, visibility,
    /// shapes) plus the role chip and the entity count.</summary>
    public sealed class CadLayerViewModel : LayerViewModel
    {
        private LayerRole _role;

        public CadLayerViewModel(string name, int count, LayerRole role) : base(name)
        {
            Count = count;
            _role = role;
        }

        public int Count { get; }

        public LayerRole Role
        {
            get => _role;
            set
            {
                if (SetField(ref _role, value)) OnPropertyChanged(nameof(RoleLabel));
            }
        }

        public string RoleLabel
        {
            get
            {
                switch (_role)
                {
                    case LayerRole.Wall: return "WALL";
                    case LayerRole.Opening: return "OPEN";
                    case LayerRole.Ignore: return "SKIP";
                    default: return "—";
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
                               IReadOnlyList<Opening> openings, IReadOnlyList<Space> spaces, IReadOnlyList<BoxMm> boxes)
        {
            Active = active;
            Erased = erased;
            Openings = openings;
            Spaces = spaces;
            Boxes = boxes;
        }

        public IReadOnlyList<CadWall> Active { get; }
        public IReadOnlyList<CadWall> Erased { get; }
        public IReadOnlyList<Opening> Openings { get; }
        public IReadOnlyList<Space> Spaces { get; }
        public IReadOnlyList<BoxMm> Boxes { get; }
    }

    /// <summary>What one detection pass produced: the session plus what the pane displays.</summary>
    public sealed class DetectResult
    {
        public CadToBimSession Session;
        public Dictionary<string, List<object>> LayerShapes = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, int> Census = new Dictionary<string, int>();
        public Rect Bounds = Rect.Empty;
        public int PlanCount;
        public int Entities;
        public int UnpairedWallLines;
        public double ReadSeconds;
        public string UnitsText = "";
    }

    public delegate DetectResult DetectDelegate(string path, CadToBimSettings settings, double sMinMm, double sMaxMm,
                                                IReadOnlyDictionary<string, LayerRole> roles, IProgress<string> progress,
                                                CancellationToken ct);

    /// <summary>
    /// Open / detect / correct / confirm for the CAD to BIM pane. Detection and brushing run on
    /// the thread pool and touch only Cad2Bim types; results are applied on the WPF dispatcher
    /// (the await continuation). Building happens only in CadToBimBuildHandler.Execute on Revit's
    /// main thread; this class hands it a BuildRequest and raises the event through
    /// IBuildEventRaiser, then hears back through IBuildRequestSink.Completed.
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
        private readonly DispatcherTimer _debounce;
        private readonly Dictionary<string, LayerRole> _roles = new Dictionary<string, LayerRole>(StringComparer.OrdinalIgnoreCase);
        private readonly List<BoxMm> _boxes = new List<BoxMm>();

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

        public CadToBimViewModel(IBuildRequestSink handler, IBuildEventRaiser raiser,
                                 CadToBimSettings settings = null, DetectDelegate detect = null,
                                 Action<Action> onUi = null)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _raiser = raiser ?? throw new ArgumentNullException(nameof(raiser));
            _settings = settings ?? CadToBimSettings.Load();
            _detect = detect ?? Detect;
            _onUi = onUi ?? DefaultOnUi;

            _sMin = _settings.SMinMm;
            _sMax = _settings.SMaxMm;
            _height = _settings.WallHeightMm;

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
                if (SetField(ref _session, value)) OnPropertyChanged(nameof(HasSession));
            }
        }

        public bool HasSession => _session != null;

        private ObservableCollection<LayerViewModel> _layers = new ObservableCollection<LayerViewModel>();
        /// <summary>Replaced wholesale per detect (one viewport rebuild instead of one per layer).</summary>
        public ObservableCollection<LayerViewModel> Layers
        {
            get => _layers;
            private set => SetField(ref _layers, value);
        }

        public ObservableCollection<LevelChoice> Levels { get; } = new ObservableCollection<LevelChoice>();

        private LevelChoice _selectedLevel;
        public LevelChoice SelectedLevel { get => _selectedLevel; set => SetField(ref _selectedLevel, value); }

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
                if (SetField(ref _busy, value)) OnPropertyChanged(nameof(CanConfirm));
            }
        }

        private BuildTarget _target = BuildTarget.AddToProject;
        public BuildTarget Target
        {
            get => _target;
            set
            {
                if (SetField(ref _target, value)) OnPropertyChanged(nameof(IsAddToProject));
            }
        }

        public bool IsAddToProject => _target == BuildTarget.AddToProject;

        private StoreyMode _storeys = StoreyMode.Stack;
        public StoreyMode Storeys { get => _storeys; set => SetField(ref _storeys, value); }

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

        public string ConfirmLabel
        {
            get
            {
                if (_session == null) return "Confirm";
                if (_wallCount == 0) return "Nothing new to build";
                return "Confirm — build " + _wallCount + (_wallCount == 1 ? " wall" : " walls");
            }
        }

        public bool CanConfirm => !_busy && _session != null && _wallCount > 0;

        private string _warning = "";
        public string Warning { get => _warning; private set => SetField(ref _warning, value); }

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
        public string PlanText => _planCount == 0 ? "" : "· " + _planCount + " found";

        private OverlaySnapshot _overlay = OverlaySnapshot.Empty;
        public OverlaySnapshot Overlay { get => _overlay; private set => SetField(ref _overlay, value); }

        private BrushResult _pendingBrush;
        /// <summary>Non-null while a big brush result waits for the drafter's yes/no.</summary>
        public BrushResult PendingBrushConfirm { get => _pendingBrush; private set => SetField(ref _pendingBrush, value); }

        /// <summary>Asked before overwriting an existing .rvt; the view wires a TaskDialog. Null = overwrite.</summary>
        public Func<string, bool> OverwritePrompt { get; set; }

        public CadToBimSettings Settings => _settings;

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

            var layers = new ObservableCollection<LayerViewModel>();
            foreach (string name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                List<object> shapes;
                if (!result.LayerShapes.TryGetValue(name, out shapes)) shapes = new List<object>();
                int count;
                if (!result.Census.TryGetValue(name, out count) || count == 0) count = shapes.Count;

                LayerRole role = RoleOf(name, _settings, _roles);
                layers.Add(new CadLayerViewModel(name, count, role)
                {
                    Items = shapes,
                    IsVisible = role != LayerRole.Ignore,
                });
            }
            Layers = layers;

            Bounds = result.Bounds;
            PlanCount = result.PlanCount;
            UnitsText = result.UnitsText;
            ReadText = result.ReadSeconds.ToString("0.00") + " s · " + result.Entities.ToString("N0") + " entities";

            RecountAndRedraw();
            Status = "Detected " + fresh.Walls.Count + " walls, " + DoorCount + " doors, " + WindowCount +
                     " windows, " + RoomCount + " rooms in " + result.ReadSeconds.ToString("0.0") + " s.";
        }

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

        public void CycleRole(CadLayerViewModel layer)
        {
            if (layer == null) return;
            LayerRole next;
            switch (layer.Role)
            {
                case LayerRole.Wall: next = LayerRole.Opening; break;
                case LayerRole.Opening: next = LayerRole.Ignore; break;
                case LayerRole.Ignore: next = LayerRole.Other; break;
                default: next = LayerRole.Wall; break;
            }
            layer.Role = next;
            _roles[layer.Name] = next;
            Redetect();
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

        // ───────── confirm / build ─────────

        public void Confirm()
        {
            if (_busy) return;
            if (_session == null)
            {
                Status = "Open a DWG first";
                return;
            }

            List<CadWall> pending = _session.Pending();
            if (pending.Count == 0)
            {
                Status = "Nothing new to build. Brush or restore a wall, then confirm again.";
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

            var pendingSet = new HashSet<CadWall>(pending);
            var request = new BuildRequest
            {
                Target = _target,
                DrawingPath = _session.DrawingPath,
                Walls = pending,
                Openings = _session.Openings.Where(o => o.Wall != null && pendingSet.Contains(o.Wall)).ToList(),
                Spaces = _session.Spaces.Where(s => s.SubElements.OfType<CadWall>().Any(pendingSet.Contains)).ToList(),
                HeightMm = _height,
                Storeys = _storeys,
                LevelId = _target == BuildTarget.AddToProject ? _selectedLevel?.Id : null,
                OutputPath = output,
                TemplatePath = string.IsNullOrWhiteSpace(_settings.TemplatePath) ? null : _settings.TemplatePath,
            };

            if (!_completedHooked)
            {
                _handler.Completed += OnBuilt;
                _completedHooked = true;
            }

            _handler.Request = request;
            _buildClock = Stopwatch.StartNew();
            _building = true;
            Busy = true;
            Status = "Building " + pending.Count + (pending.Count == 1 ? " wall…" : " walls…");

            if (!_raiser.Raise())
            {
                _handler.Request = null;
                _building = false;
                Busy = false;
                Status = "Revit is busy, try again";
            }
        }

        // Called from CadToBimBuildHandler.Execute on Revit's main thread; hop to the dispatcher.
        private void OnBuilt(BuildReport report) => _onUi(() => ApplyReport(report));

        private void ApplyReport(BuildReport report)
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

            if (report.Ok && _session != null) _session.MarkBuilt(report);
            RecountAndRedraw();
            Busy = false;

            if (!report.Ok)
            {
                Status = "Build failed: " + report.Error;
                return;
            }

            var text = new StringBuilder();
            text.Append("Done in ").Append(seconds.ToString("0.0")).Append(" s — ")
                .Append(report.Walls).Append(" walls, ").Append(report.Doors).Append(" doors, ")
                .Append(report.Windows).Append(" windows, ").Append(report.Rooms).Append(" rooms.");
            if (report.SkippedWalls > 0)
                text.Append(' ').Append(report.SkippedWalls).Append(" walls skipped (centreline too short for Revit).");
            if (report.SkippedOpenings > 0)
                text.Append(' ').Append(report.SkippedOpenings).Append(" openings skipped (no door/window family in the template).");
            if (!string.IsNullOrEmpty(report.OutputPath))
                text.Append(" Saved ").Append(report.OutputPath).Append(" and opened it in Revit.");
            else
                text.Append(" One undo step. Brush more and confirm again to add only the new walls.");
            Status = text.ToString();
        }

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
            Overlay = new OverlaySnapshot(active, _session.Erased.ToList(), _session.Openings.ToList(),
                                          _session.Spaces.ToList(), _boxes.ToList());
        }

        private void UpdateCounts()
        {
            List<CadWall> pending = _session != null ? _session.Pending() : new List<CadWall>();
            WallCount = pending.Count;
            DoorCount = _session != null ? _session.Openings.Count(o => o.IsDoor) : 0;
            WindowCount = _session != null ? _session.Openings.Count(o => !o.IsDoor) : 0;
            RoomCount = _session != null ? _session.Spaces.Count : 0;
            ErasedCount = _session != null ? _session.Erased.Count : 0;
            ForcedCount = _session != null ? _session.Forced.Count : 0;
            Warning = _result != null && _result.UnpairedWallLines > 0
                ? _result.UnpairedWallLines + " wall-layer lines not paired — try Brush"
                : "";
            OnPropertyChanged(nameof(ConfirmLabel));
            OnPropertyChanged(nameof(CanConfirm));
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

            // Civil 3D / AutoCAD Architecture / MEP: the CLASSES section says so before a single
            // entity is read. Refuse now with the one message the drafter can act on.
            string unsupported = UnsupportedSource(document.Classes.Select(c => c.DxfName));
            if (unsupported != null) throw new NotSupportedException(unsupported);

            progress?.Report("Classifying…");
            CadModel survey = ModelSource.Read(document, BaseFilter(settings, roles));
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
                focused.Include.AddRange(survey.LayerCensus.Keys.Where(layer => RoleOf(layer, settings, roles) == LayerRole.Opening));
                model = ModelSource.Read(document, focused);
            }
            ct.ThrowIfCancellationRequested();

            CadWall.SMin = sMinMm;
            CadWall.SMax = sMaxMm;

            List<CadSegment> wallSegments = model.Segments
                .Where(s => s.Layer.Length == 0 || RoleOf(s.Layer, settings, roles) == LayerRole.Wall)
                .ToList();
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
                Bounds = sink.Bounds(),
                PlanCount = plans.Count,
                Entities = sink.Entities,
                UnpairedWallLines = unpaired,
                ReadSeconds = clock.Elapsed.TotalSeconds,
                UnitsText = units,
            };
        }

        // Same three calls ClassificationService.Elaborate makes, on the walls actually in play,
        // with the command's symbol-based opening pass (window linework from opening layers).
        private static (List<Opening> Openings, List<Space> Spaces) Elaborate(
            CadModel model, List<CadWall> walls, CadToBimSettings settings, IReadOnlyDictionary<string, LayerRole> roles)
        {
            if (model == null || walls == null || walls.Count == 0)
            {
                return (new List<Opening>(), new List<Space>());
            }

            WallGraph graph = CadClassifier.CreateTopologicalPoints(walls);
            List<Space> spaces = CadClassifier.ClassifySpaces(graph, model.Texts);
            CadClassifier.SplitWalls(walls, spaces);

            List<CadSegment> windowLines = model.Segments
                .Where(s => RoleOf(s.Layer, settings, roles) == LayerRole.Opening)
                .ToList();
            List<Opening> openings = CadClassifier.ClassifyOpeningsFromSymbols(walls, model.Arcs, windowLines);

            return (openings, spaces);
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

        internal static LayerRole RoleOf(string layer, CadToBimSettings settings, IReadOnlyDictionary<string, LayerRole> overrides)
        {
            if (layer == null) return LayerRole.Other;
            LayerRole forced;
            if (overrides != null && overrides.TryGetValue(layer, out forced)) return forced;
            if (Mentions(layer, settings.WallLayerHints)) return LayerRole.Wall;
            if (Mentions(layer, settings.OpeningLayerHints)) return LayerRole.Opening;
            foreach (string glob in settings.ExcludeGlobs)
            {
                if (LayerFilter.Matches(layer, glob)) return LayerRole.Ignore;
            }
            return LayerRole.Other;
        }

        private static bool Mentions(string layer, IEnumerable<string> words) =>
            words != null && words.Any(w => !string.IsNullOrEmpty(w) && layer.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// The engine has no idea what wrote the file; ACadSharp reads the CLASSES section fine
        /// and hands back proxies for everything Civil 3D / Architecture / MEP drew. Same rule as
        /// BinaVibe's CadFileReader.DetectSource on feat/cad-segment-stitching: any registered
        /// class named AECC_* (Civil 3D), AEC_* (Architecture) or AECB_* (MEP) means the plan
        /// must be exported to plain DWG first. Null when the drawing is plain AutoCAD.
        /// </summary>
        internal static string UnsupportedSource(IEnumerable<string> dxfClassNames)
        {
            if (dxfClassNames == null) return null;
            foreach (string name in dxfClassNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith("AECC_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("AEC_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("AECB_", StringComparison.OrdinalIgnoreCase))
                {
                    return UnsupportedSourceMessage;
                }
            }
            return null;
        }

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
