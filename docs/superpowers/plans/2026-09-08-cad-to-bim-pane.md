# CAD to BIM Pane Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One ribbon button ("CAD to BIM" on the Bina tab) turns a 2D floor-plan DWG/DXF into native Revit walls (with thickness), doors, windows and named rooms inside the running Revit session, with a dockable preview pane the drafter corrects (layer roles, thickness range, Erase, Brush) before anything is built; output goes into the open project or a new `.rvt` saved beside the drawing. No cloud, no LLM, no IFC.

**Architecture:** The unmerged `origin/feat/cad2bim` engine (`Cad2Bim/cad2bim/*`, pure C# + ACadSharp, no Revit) is merged and linked into all three add-in TFMs. Its two ribbon commands are dissolved: the Revit-side build loop becomes `Services/CadToBim/Cad2BimBuilder` (run only inside an `ExternalEvent` handler on Revit's thread), the selection-box logic becomes the Revit-free `Cad2BimBrush`. A WPF dockable pane (`UI/CadToBim/*`) hosts the engine's `CadViewport` inside a composition wrapper that draws the wall/opening/room/erase/brush overlay, driven by `CadToBimViewModel` (detect + brush on the thread pool, results on the dispatcher, build through two Revit-free seams `IBuildRequestSink` / `IBuildEventRaiser`). Every type the Tests project links is free of `Autodesk.Revit.*` types — Revit element ids travel as `long` through `ElementIdCompat`. Settings persist under the `cadToBim` key of the add-in's `config.json`.

**Tech Stack:** C# / WPF, `RevitWebAppSync.csproj` multi-target `net48;net8.0-windows;net10.0-windows` (Revit 2023–2027), `ACadSharp 3.6.51`, xunit 2.9.2 (`Tests/Tests.csproj`, `net10.0-windows`), `~/.dotnet/dotnet` 10.0.302 on the Mac as compile gate, Windows rig (Revit 2024 + 2025) for test execution and smoke; bina-ai side is Python 3.13 / `uv` / pytest (Task 20 only).

**Spec:** docs/superpowers/specs/2026-09-08-cad-to-bim-pane-design.md

## Global Constraints

- TFMs: every add-in source, engine link and new file compiles on `net48`, `net8.0-windows` AND `net10.0-windows` — build all three after every task that touches the add-in (`~/.dotnet/dotnet build RevitWebAppSync.csproj -f <tfm>`).
- `ACadSharp 3.6.51` everywhere (add-in csproj on all TFMs; `Tests/Tests.csproj` once, in Task 4). Never a second `PackageReference`.
- No `*_v2.py`/`*_v2.cs`/`*V2` forks: superseded code is deleted in the same change (`Commands/Cad2BimConvertCommand.cs` in Task 12, `Commands/Cad2BimSelectionCommand.cs` in Task 15).
- Executors STAGE (`git add`) and never commit (repo owner rule). Task 1 leaves a merge in progress on purpose; nobody runs `git merge --abort`.
- bina-ai (Task 20 only): never run the full pytest suite — target files.
- Tests compile on the Mac ONLY with `~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true`; they RUN only on Windows / CI (`dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~<Class>"`). Every "run test" step below gives both commands.
- `[Collection("Cad2Bim")]` on every test class that constructs a `Cad2Bim.Wall`, reads `Cad2Bim.Wall.SMin/SMax/MinFaceLength/MinFaceAspect/MaxFaceUses`, or otherwise touches `Cad2Bim` types — those thresholds are mutable statics and xunit parallelises across classes. No `CollectionDefinition` is needed for a plain named collection.
- `Tests/Tests.csproj` engine links (`..\Cad2Bim\cad2bim\...`), the `ACadSharp` package, `EnableWindowsTargeting` and the `<Using>` globals are added EXACTLY ONCE, in Task 4. Every later task adds only ITS OWN new `Services/CadToBim` / `UI/CadToBim` source links (ownership table under "Reconciled contract").
- No `#if !REVIT2023_24` in any new code; no `Cad2Bim/IsExternalInit.cs` (develop's `Services/Net48Shims.cs` already defines `IsExternalInit`, `KeyValuePair.Deconstruct`, `Dictionary.GetValueOrDefault` and an `ElementId.Value` extension for net48 — a second `IsExternalInit` is CS0101).
- Revit-UI-free DTOs: `BuildTypes.cs`, `BuildReport.cs`, `BoxMm.cs`, `BuildSeams.cs`, `CadToBimSettings.cs`, `CadToBimSession.cs`, `Cad2BimBrush.cs`, `CadOverlayViewport.cs`, `CadToBimViewModel.cs`, `CadToBimSettingsDraft.cs` contain NO `Autodesk.Revit.*` type (not even `ElementId` — the Tests project's `RevitAPI.dll` reference is metadata-only and cannot load at test runtime). Revit-touching files (`SilenceJoinFailures.cs`, `Cad2BimBuilder.cs`, `CadToBimBuildHandler.cs`, `ExternalEventRaiser.cs`, `ElementIdCompat.cs`, panel/host/window/command/App.cs) are never linked into Tests.
- Engine sources are edited only where net48 / ImplicitUsings force it (Task 1 Step 3.4, exact list); `CadViewport.cs` stays verbatim (Task 16 wraps it by composition — it is `sealed`).
- Type-name clashes: any file with both `using Autodesk.Revit.DB;` and `using Cad2Bim;` aliases `using CadWall = Cad2Bim.Wall; using CadOpening = Cad2Bim.Opening; using CadSegment = Cad2Bim.Segment; using CadPoint = Cad2Bim.Point; using CadArc = Cad2Bim.Arc;` (Revit has its own `Wall`, `Opening`, `Arc`, `Point`).
- Engine type facts (verified on `origin/feat/cad2bim`): `record Point(double x, double y)` — lower-case `x`/`y`; `Segment(Point p1, Point p2)`, `Segment.P1/P2/Length/Layer`, `static Segment.Midpoint(Segment)`; `Wall(Segment e1, Segment e2)` throws `ArgumentException` unless the faces are parallel and `Wall.SMin ≤ gap ≤ Wall.SMax`, `Wall.Centerline/Thickness/Geometry/IsOutdoor`; `Opening.Wall/IsDoor/Position/Width/Geometry`; `Space.Boundary : List<Point>`, `Space.Name` (null when unlabelled), `Space.SubElements : List<BuildingElement>` (its walls: `.OfType<Wall>()`), `Space.Area`; `Arc { Center, Radius, StartAngle, EndAngle, SweepDegrees, PointAt(angle), StartPoint }`; `PlanCluster { Walls, MinX/MinY/MaxX/MaxY (settable), Width/Height/Area, Texts, Add(Wall), Contains(Point) }` from `CadClassifier.ClusterPlans(List<Wall>, IReadOnlyList<TextElement> texts = null, double gapMm = 5000)`; `CadModel { Segments, Arcs, Texts, LayerCensus, Outlines, Scale }`; `LayerFilter { Include, Exclude, IncludeHatch, IncludeDimensions, Allows(layer, CadSource), static Matches(value, glob) }`; `CadRenderSource.Read(path) → CadDocument`, `CadRenderSource.Walk(doc, ICadSink)`; `ICadSink { Polyline(points, isClosed, layer, source); Arc(points, ArcParams? parameters, layer, source); Text(x, y, height, value, layer, source) }`; `Units.FromHeader(CadDocument) → double?`; `CadClassifier.ClassifyWallsElsewhere(List<Wall>, List<Segment>)` lives in `Outlines.cs`.
- Test namespaces in the repo are mixed (`Tests`, `RevitWebAppSync.Tests`, `RevitAddinSync.Tests`); each task keeps the namespace its file shows — all three compile in the one assembly.

## Execution order

Tasks keep their numbers 1–22 but are NOT executed in numeric order. Run them in this sequence; every arrow is a real compile dependency:

1. **Task 1** — branch `feat/cad-to-bim-pane`, merge `origin/feat/cad2bim`, csproj link block for all TFMs, net48 source fixes. Everything else depends on the engine being on the branch.
2. **Task 2** — ribbon icons (independent; needs nothing).
3. **Task 4** — `Tests/Tests.csproj`: `EnableWindowsTargeting`, `ACadSharp`, `<Using>` globals, ALL engine links (once). Every later test depends on this.
4. **Tasks 5, 6, 7, 8, 9** — engine characterisation tests (any order; need only Task 4).
5. **Task 10** — Revit-free build contract (`BuildTypes.cs`, `BuildReport.cs`, `BoxMm.cs`, `BuildSeams.cs`) + `SilenceJoinFailures.cs`. Needs Task 4 (engine types in Tests).
6. **Task 13** — `CadToBimSettings` + `BinaConfig` passthrough. Needs Task 4.
7. **Task 14** — `ElementIdCompat` + `CadToBimSession`. Needs Task 10 (`BuildReport`).
8. **Task 15** — `Cad2BimBrush` (+ `git rm Commands/Cad2BimSelectionCommand.cs`). Needs Tasks 10 (`BoxMm`) and 13 (`ToLayerFilter`).
9. **Task 11** — `Cad2BimBuilder`. Needs Tasks 10 and 14 (`ElementIdCompat`). Its add-in build is the first net48 compile of a Revit-touching CadToBim file.
10. **Task 12** — `CadToBimBuildHandler : IExternalEventHandler, IBuildRequestSink` + `ExternalEventRaiser` + `git rm Commands/Cad2BimConvertCommand.cs`. MUST run after Task 15 (the selection command references nine members of the convert command; deleting the convert command first breaks the build) and after Task 11.
11. **Task 16** — `CadOverlayViewport`. Needs Task 10 (`BoxMm`) and Task 4 (`CadViewport` linked into Tests).
12. **Task 17** — `CadToBimViewModel`. Needs Tasks 10, 13, 14, 15, 16.
13. **Task 18** — `CadToBimPanel` + tokens/styles/theme. Needs Tasks 12 (`ExternalEventRaiser`), 16, 17. Its add-in compile gate goes green only after Task 19 (the panel's gear opens `CadToBimSettingsWindow`, created in Task 19) — run Task 18's Tests compile at its slot and the add-in build at the end of Task 19.
14. **Task 19** — `CadToBimPaneHost` + `CadToBimSettingsDraft` + `CadToBimSettingsWindow`. Needs Task 18.
15. **Task 3** — ribbon panel, `OpenCadToBimCommand`, pane registration, `ExternalEvent` creation in `App.cs`. Runs AFTER Tasks 12, 17 and 19 because its compile gate references `CadToBimBuildHandler`, `CadToBimPaneHost`, `CadToBimPanel`, `CadToBimViewModel`. (Its source-lint unit test passes standalone; the `dotnet build` gate is the reason for the slot.)
16. **Task 20** — bina-ai phantom-tool cleanup (separate repo, separate branch; independent of 1–19).
17. **Task 21** — close stale branches (owner runs; after the feature PR is opened).
18. **Task 22** — Windows smoke checklist document (write it any time; it is executed on the rig after Task 3).

Whole-tree gates after Task 3: `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48`, `-f net8.0-windows`, `-f net10.0-windows` → `Build succeeded` ×3; `~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true` → `Build succeeded`; on Windows `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2Bim|FullyQualifiedName~CadToBim|FullyQualifiedName~CadOverlayViewport|FullyQualifiedName~XamlResourceScope"` → all green; then the smoke checklist (Task 22) on Revit 2024 + 2025.

## Reconciled contract

This section REPLACES `plan-contract.md`. Every task body below agrees with it. File → owning task in the comment on each type.

```csharp
namespace RevitWebAppSync.Services.CadToBim
{
    // ── Services/CadToBim/BuildTypes.cs — Task 10 — Revit-free, linked into Tests ──
    public enum BuildTarget { AddToProject, NewFile }
    public enum StoreyMode { Stack, KeepPosition }

    public sealed class BuildRequest
    {
        public BuildTarget Target { get; set; }
        public string DrawingPath { get; set; }
        public List<Cad2Bim.Wall> Walls { get; set; }          // pending walls only (session.Pending())
        public List<Cad2Bim.Opening> Openings { get; set; }    // may be the session's full list; builder ignores openings whose Wall is not pending
        public List<Cad2Bim.Space> Spaces { get; set; }        // same rule via SubElements
        public double HeightMm { get; set; } = 3000;
        public StoreyMode Storeys { get; set; } = StoreyMode.Stack;
        public long? LevelId { get; set; }                     // AddToProject only; ElementId value (ElementIdCompat.ToLong); null = lowest level
        public string OutputPath { get; set; }                 // NewFile only; <dwgdir>/<dwgname>.rvt — the pane has already asked about overwrite
        public string TemplatePath { get; set; }               // null = app.DefaultProjectTemplate
    }

    // ── Services/CadToBim/BuildReport.cs — Task 10 — Revit-free ──
    public sealed class BuildReport
    {
        public int Walls, Doors, Windows, Rooms, SkippedOpenings, SkippedWalls;
        public string OutputPath; public TimeSpan Elapsed; public string Error;
        public Dictionary<Cad2Bim.Wall, long> WallIds = new();  // surviving Revit wall id per CAD wall (ElementIdCompat.ToLong)
        public bool Ok => Error == null;
    }

    // ── Services/CadToBim/BoxMm.cs — Task 10 — Revit-free ──
    public readonly record struct BoxMm(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width { get; } public double Height { get; }
        public bool Contains(double x, double y);              // inclusive
        public bool Contains(Cad2Bim.Point p);
        public static BoxMm Normalised(double x1, double y1, double x2, double y2);   // any two corners
    }

    // ── Services/CadToBim/BuildSeams.cs — Task 10 — Revit-free ──
    public interface IBuildEventRaiser { bool Raise(); }                       // true = ExternalEventRequest.Accepted
    public interface IBuildRequestSink { BuildRequest Request { get; set; } event Action<BuildReport> Completed; }

    // ── Services/CadToBim/SilenceJoinFailures.cs — Task 10 — Revit ──
    public sealed class SilenceJoinFailures : Autodesk.Revit.DB.IFailuresPreprocessor { }

    // ── Services/CadToBim/Cad2BimBuilder.cs — Task 11 — Revit ──
    public static class Cad2BimBuilder
    {
        public static BuildReport Build(Autodesk.Revit.UI.UIApplication uiapp, BuildRequest req);   // never throws; Error = innermost message
    }

    // ── Services/CadToBim/CadToBimBuildHandler.cs — Task 12 — Revit ──
    public sealed class CadToBimBuildHandler : Autodesk.Revit.UI.IExternalEventHandler, IBuildRequestSink
    {
        public BuildRequest Request { get; set; }               // set by the pane before Raise(); cleared by Execute in finally
        public event Action<BuildReport> Completed;             // raised on Revit's main thread
        public void Execute(Autodesk.Revit.UI.UIApplication app);
        public string GetName();                                // "BINA CAD to BIM build"
    }

    // ── Services/CadToBim/ExternalEventRaiser.cs — Task 12 — Revit UI, NOT linked into Tests ──
    public sealed class ExternalEventRaiser : IBuildEventRaiser
    {
        public ExternalEventRaiser(Autodesk.Revit.UI.ExternalEvent externalEvent);   // null event → Raise() returns false
        public bool Raise();
    }

    // ── Services/CadToBim/CadToBimSettings.cs — Task 13 — Revit-free ──
    public sealed class CadToBimSettings
    {
        public const string ConfigKey = "cadToBim";
        public double SMinMm = 50, SMaxMm = 400, WallHeightMm = 3000, DoorMinRadiusMm = 500, DoorMaxRadiusMm = 1500, WindowSillMm = 900;
        public List<string> WallLayerHints = new() { "wall", "dinding", "tembok", "partition", "bata" };
        public List<string> OpeningLayerHints = new() { "door", "pintu", "win", "tingkap", "glaz" };
        public List<string> ExcludeGlobs = new() { "PERABUT", "FURNITURE", "FURN*", "SANI*", "FITTING", "Toilet-fitting", "*-DIM*", "DEFPOINTS", "G-bubble", "GRID*" };
        public string TemplatePath;
        internal static string ConfigPathOverride;              // tests only
        public static string DefaultConfigPath { get; }         // %APPDATA%\RevitWebAppSync\config.json (same path as BinaConfig.ConfigPath)
        public static CadToBimSettings Load();                  // JObject read of the "cadToBim" key; defaults on missing/corrupt
        public void Save();                                     // read-modify-write of the same file; never throws
        public Cad2Bim.LayerFilter ToLayerFilter();             // Exclude = ExcludeGlobs, Include empty
        public bool IsWallLayer(string layer);                  // case-insensitive substring of WallLayerHints
        public bool IsOpeningLayer(string layer);
    }
    // BinaConfig.cs (Task 13): [JsonProperty("cadToBim", NullValueHandling = Ignore)] public Newtonsoft.Json.Linq.JObject CadToBim { get; set; }  — opaque passthrough

    // ── Services/CadToBim/ElementIdCompat.cs — Task 14 — Revit, NOT linked into Tests ──
    internal static class ElementIdCompat
    {
        public static long ToLong(Autodesk.Revit.DB.ElementId id);          // IntegerValue on net48 (Revit 2023 API), Value on net8+; null → -1
        public static Autodesk.Revit.DB.ElementId ToElementId(long value);  // int ctor on net48, long ctor on net8+
    }

    // ── Services/CadToBim/CadToBimSession.cs — Task 14 — Revit-free ──
    public sealed class CadToBimSession
    {
        public string DrawingPath; public double Scale = 1.0;
        public Cad2Bim.Services.ClassificationService Service;  // contract compatibility; the pane never reads it
        public Cad2Bim.CadModel Model;                          // the drawing in mm (segments/arcs/texts/outlines) for re-elaboration
        public List<Cad2Bim.Wall> Walls = new(); public List<Cad2Bim.Opening> Openings = new(); public List<Cad2Bim.Space> Spaces = new();
        public HashSet<Cad2Bim.Wall> Erased = new(); public List<Cad2Bim.Wall> Forced = new();
        public Dictionary<Cad2Bim.Wall, long> BuiltWallIds = new();
        public double MedianThicknessMm = 100.0;                // recomputed by SetWalls
        public int ErasedCount { get; } public int ForcedCount { get; } public int BuiltCount { get; }
        public void SetWalls(List<Cad2Bim.Wall> walls);         // assigns, recomputes median, prunes Erased entries not in Walls ∪ Forced
        public List<Cad2Bim.Wall> Active();                     // (Walls ∪ Forced) − Erased, detected first, deduped by reference
        public List<Cad2Bim.Wall> Pending();                    // Active() − BuiltWallIds.Keys
        public void ToggleErase(Cad2Bim.Wall w);                // null ignored
        public void AddForced(IEnumerable<Cad2Bim.Wall> ws);    // dedupe by reference against Walls and Forced
        public void MarkBuilt(BuildReport r);                   // only when r.Ok; never prunes
        public void CarryForwardFrom(CadToBimSession previous); // after a re-detect: Erased + BuiltWallIds re-keyed by CenterlineKey, Forced by identity
        public static string CenterlineKey(Cad2Bim.Wall w);     // "x1,y1,x2,y2" of the mm-rounded centreline, direction-independent
        internal static double Median(IReadOnlyList<Cad2Bim.Wall> walls, double fallback);   // sorted[count/2]
    }

    // ── Services/CadToBim/Cad2BimBrush.cs — Task 15 — Revit-free ──
    public sealed class BrushResult { public List<Cad2Bim.Wall> Walls = new(); public string Note; public int Paired, SingleLine, Cloud; }
    public static class Cad2BimBrush
    {
        public const double SingleLineMinLengthMm = 500.0;
        public static BrushResult Run(string drawingPath, BoxMm box, CadToBimSettings settings, double medianThicknessMm);
        internal static BrushResult RunOnModel(Cad2Bim.CadModel model, BoxMm box, double medianThicknessMm);
        internal static Cad2Bim.Wall SingleLineWall(Cad2Bim.Segment line, double thicknessMm);
    }
    // The ">40 walls — build them anyway?" question lives in CadToBimViewModel, never in the brush.
}

namespace RevitWebAppSync.UI.CadToBim
{
    // ── UI/CadToBim/CadOverlayViewport.cs — Task 16 — Revit-free (WPF), linked into Tests ──
    public enum ViewportMode { Pan, Erase, Brush }
    public sealed class CadOverlayViewport : System.Windows.Controls.Grid
    {
        public const double EraseToleranceMm = 150.0;
        public static readonly DependencyProperty ModeProperty, LayersSourceProperty, ContentBoundsProperty, BackdropProperty;
        public ViewportMode Mode { get; set; }
        public IEnumerable<Cad2Bim.ViewModels.LayerViewModel> LayersSource { get; set; }
        public Rect ContentBounds { get; set; }  public Brush Backdrop { get; set; }
        public Cad2Bim.Views.Rendering.CadViewport Viewport { get; }          // the verbatim upstream viewport (first child)
        public event Action<Cad2Bim.Wall> WallClicked;  public event Action<BoxMm> BoxDragged;  public event Action<double, double> CursorMoved;
        public void SetOverlay(IReadOnlyList<Cad2Bim.Wall> active, IReadOnlyList<Cad2Bim.Wall> erased, IReadOnlyList<Cad2Bim.Opening> openings, IReadOnlyList<Cad2Bim.Space> spaces, IReadOnlyList<BoxMm> boxes);
        public Cad2Bim.Wall HitTestWall(System.Windows.Point screenPt, double toleranceMm);
        public void Redraw();
        internal static Cad2Bim.Wall NearestWall(IReadOnlyList<Cad2Bim.Wall> walls, Cad2Bim.SegmentIndex index, double xMm, double yMm, double tolMm);
        internal static double DistanceToSegment(double xMm, double yMm, Cad2Bim.Segment s);
    }

    // ── UI/CadToBim/CadToBimViewModel.cs — Task 17 — Revit-free, linked into Tests ──
    public enum LayerRole { Other, Wall, Opening, Ignore }
    public sealed class CadLayerViewModel : Cad2Bim.ViewModels.LayerViewModel { public CadLayerViewModel(string name, int count, LayerRole role); public int Count { get; } public LayerRole Role { get; set; } public string RoleLabel { get; } }
    public sealed class LevelChoice { public string Name; public long Id; }   // ElementId value (ElementIdCompat.ToLong), filled by CadToBimPanel.RefreshLevels
    public sealed class OverlaySnapshot { public static readonly OverlaySnapshot Empty; public IReadOnlyList<Cad2Bim.Wall> Active, Erased; public IReadOnlyList<Cad2Bim.Opening> Openings; public IReadOnlyList<Cad2Bim.Space> Spaces; public IReadOnlyList<BoxMm> Boxes; }
    public sealed class DetectResult { public CadToBimSession Session; public Dictionary<string, List<object>> LayerShapes; public IReadOnlyDictionary<string, int> Census; public Rect Bounds; public int PlanCount, Entities, UnpairedWallLines; public double ReadSeconds; public string UnitsText; }
    public delegate DetectResult DetectDelegate(string path, CadToBimSettings settings, double sMinMm, double sMaxMm, IReadOnlyDictionary<string, LayerRole> roles, IProgress<string> progress, CancellationToken ct);
    public sealed class CadToBimViewModel : Cad2Bim.ViewModels.ViewModelBase
    {
        public const int BrushConfirmAbove = 40;
        public const string UnsupportedSourceMessage = "This drawing was saved by Civil 3D / AutoCAD Architecture / AutoCAD MEP. Run EXPORTTOAUTOCAD in AutoCAD and open the exported file.";
        public CadToBimViewModel(IBuildRequestSink handler, IBuildEventRaiser raiser, CadToBimSettings settings = null, DetectDelegate detect = null, Action<Action> onUi = null);
        public Task OpenAsync(string path); public void ToggleErase(Cad2Bim.Wall w); public Task BrushAsync(BoxMm box); public void ResolveBrushConfirm(bool accept);
        public void Confirm(); public void Redetect(); public void Cancel(); public void ApplySettings(CadToBimSettings s); public void CycleRole(CadLayerViewModel layer); public void SetLevels(IEnumerable<LevelChoice> levels);
        public CadToBimSession Session { get; } public bool HasSession { get; } public ObservableCollection<Cad2Bim.ViewModels.LayerViewModel> Layers { get; } public Rect Bounds { get; }
        public string Status { get; } public bool Busy { get; }
        public BuildTarget Target { get; set; } public bool IsAddToProject { get; } public StoreyMode Storeys { get; set; }
        public double HeightMm { get; set; } public double SMinMm { get; set; } public double SMaxMm { get; set; }
        public int WallCount { get; } public int DoorCount { get; } public int WindowCount { get; } public int RoomCount { get; } public int ErasedCount { get; } public int ForcedCount { get; }
        public string ConfirmLabel { get; } public bool CanConfirm { get; } public string Warning { get; }
        public string DrawingPath { get; } public string DrawingName { get; } public string UnitsText { get; } public string ReadText { get; } public int PlanCount { get; } public string PlanText { get; }
        public ObservableCollection<LevelChoice> Levels { get; } public LevelChoice SelectedLevel { get; set; }
        public OverlaySnapshot Overlay { get; } public BrushResult PendingBrushConfirm { get; }
        public Func<string, bool> OverwritePrompt { get; set; }   // view wires a TaskDialog; null = overwrite
        public CadToBimSettings Settings { get; }
        public static DetectResult Detect(string path, CadToBimSettings settings, double sMinMm, double sMaxMm, IReadOnlyDictionary<string, LayerRole> roles, IProgress<string> progress, CancellationToken ct);
        internal static string UnsupportedSource(IEnumerable<string> dxfClassNames);   // UnsupportedSourceMessage when any class starts with AECC_/AEC_/AECB_, else null
        internal static LayerRole RoleOf(string layer, CadToBimSettings settings, IReadOnlyDictionary<string, LayerRole> overrides);
        internal static string Describe(Exception ex);                              // innermost message; EXPORTTOAUTOCAD hint for proxy/AEC failures
    }

    // ── UI/CadToBim/CadToBimTheme.cs + CadToBimTokens.xaml + CadToBimStyles.xaml + CadToBimPanel.xaml(.cs) — Task 18 — Revit ──
    public static class CadToBimTheme { public static void EnsureLoaded(); public static ResourceDictionary NewThemeDictionary(); }
    public partial class CadToBimPanel : System.Windows.Controls.UserControl
    {
        public CadToBimPanel();                                                          // = this(App.CadToBimBuildHandler, App.CadToBimBuildEvent)
        public CadToBimPanel(CadToBimBuildHandler handler, Autodesk.Revit.UI.ExternalEvent buildEvent);   // vm = new CadToBimViewModel(handler, new ExternalEventRaiser(buildEvent))
        public CadToBimViewModel ViewModel { get; }
        public void RefreshLevels(Autodesk.Revit.DB.Document doc);                        // valid API context only (OpenCadToBimCommand calls it before OpenAsync)
    }
    // Resource keys: CadToBim.Wall / .Opening / .Room / .Erase / .Brush (tokens); CadToBim.Section / .SectionTitle / .Mono / .Float / .Tool / .Button / .Primary / .Chip / .Radio (styles); implicit style for Cad2Bim.Views.Controls.NumericScrubBox.

    // ── UI/CadToBim/CadToBimPaneHost.cs + CadToBimSettingsDraft.cs + CadToBimSettingsWindow.xaml(.cs) — Task 19 ──
    public class CadToBimPaneHost : System.Windows.Controls.Page, Autodesk.Revit.UI.IDockablePaneProvider
    {
        public static readonly DockablePaneId PaneId = new(new Guid("B1A4C057-0005-4000-8000-000000000005"));
        public CadToBimPaneHost();                              // parameterless; builds new CadToBimPanel() — App.CadToBimBuildHandler/Event must exist first
        public CadToBimPanel Panel { get; }                     // null when construction failed (pane shows the error text)
        public void SetupDockablePane(DockablePaneProviderData data);   // DockPosition.Right, VisibleByDefault = false
    }
    public sealed class CadToBimSettingsDraft : Cad2Bim.ViewModels.ViewModelBase   // Revit-free, linked into Tests
    {
        public static CadToBimSettingsDraft From(CadToBimSettings s); public string Validate(); public void WriteTo(CadToBimSettings s);
        public double SMin, SMax, Height, DoorMin, DoorMax, Sill { get; set; } public string WallHints, OpeningHints, ExcludeGlobs, TemplatePath { get; set; }
        public static List<string> SplitList(string csv);
    }
    public partial class CadToBimSettingsWindow : System.Windows.Window { public CadToBimSettingsWindow(CadToBimSettings settings); }   // ShowDialog()==true ⇒ settings mutated + saved
}

// App.cs — Task 3
public static CadToBimPaneHost CadToBimPaneHost { get; private set; }
public static CadToBimBuildHandler CadToBimBuildHandler { get; private set; }
public static ExternalEvent CadToBimBuildEvent { get; private set; }     // created in OnStartup BEFORE new CadToBimPaneHost(); disposed + nulled in OnShutdown
// Commands/OpenCadToBimCommand.cs — Task 3 : IExternalCommand  (OTA gate → show pane → Panel.RefreshLevels(active doc) → OpenFileDialog *.dwg;*.dxf → ViewModel.OpenAsync)
// Ribbon — Task 3: panel "CAD to BIM" between "BINA AI" and "Compliance"; button "CadToBim" / "CAD to\nBIM"; ZeroDocCommandAvailability; LoadIcon("CadToBim", 16|32).
// Icons — Task 2: Resources/Icons/CadToBim16.png, CadToBim32.png, CadToBim64.png + svg/CadToBim16.svg + svg/CadToBim32.svg + a CadToBim entry in scripts/gen-ribbon-icons.mjs.
```

**Stale-Wall-reference gap, resolved:** a re-detect (threshold, role chip or settings change) produces new `Cad2Bim.Wall` instances, so `Erased` and `BuiltWallIds` keyed by reference would go stale; `CadToBimSession.CarryForwardFrom(previous)` (Task 14, unit-tested) re-keys erased marks and built ids onto the new instances by `CenterlineKey` (mm-rounded, direction-independent centreline endpoints) and carries brushed walls (and their erased/built state) by identity, and `CadToBimViewModel.Apply` (Task 17) calls it whenever the re-detected drawing has the same path.

**`Tests/Tests.csproj` ownership (each line added exactly once):**

| Task | Adds to `Tests/Tests.csproj` |
|---|---|
| 4 | `<EnableWindowsTargeting>true</EnableWindowsTargeting>`; `<PackageReference Include="ACadSharp" Version="3.6.51" />`; the `<Using>` group (System, System.Collections.Generic, System.Linq); ONE `<ItemGroup>` with every engine link (18 files, list in Task 4) and the marker comment `<!-- CAD to BIM pane sources (Revit-free) — later tasks append here -->` |
| 10 | `..\Services\CadToBim\BuildTypes.cs`, `BuildReport.cs`, `BoxMm.cs`, `BuildSeams.cs` |
| 13 | `..\Services\CadToBim\CadToBimSettings.cs` |
| 14 | `..\Services\CadToBim\CadToBimSession.cs` |
| 15 | `..\Services\CadToBim\Cad2BimBrush.cs` |
| 16 | `..\UI\CadToBim\CadOverlayViewport.cs` |
| 17 | `..\UI\CadToBim\CadToBimViewModel.cs` |
| 19 | `..\UI\CadToBim\CadToBimSettingsDraft.cs` |

Never linked into Tests: `SilenceJoinFailures.cs`, `Cad2BimBuilder.cs`, `CadToBimBuildHandler.cs`, `ExternalEventRaiser.cs`, `ElementIdCompat.cs`, `BinaConfig.cs`, anything under `UI/CadToBim/` other than the three files above, `Cad2Bim/cad2bim/AssemblyInfo.cs`, `Cad2Bim/cad2bim/IfcExporter.cs`.

**Environment facts (verified on this Mac 2026-09-08):** SDK `~/.dotnet/dotnet` = 10.0.302 (builds net48, net8.0-windows, net10.0-windows). `rsvg-convert` at `/opt/homebrew/bin/rsvg-convert`; `magick`/`inkscape`/`@resvg/resvg-js` absent. `Tests/Tests.csproj` is `net10.0-windows` + `PlatformTarget x64` + `UseWPF`: compiles on the Mac only with `-p:EnableWindowsTargeting=true -p:SkipRevitSources=true` (NETSDK1100 otherwise) and cannot execute here (arm64 host, x64 test host: "Could not find 'dotnet' host for the 'X64' architecture"). CI (`.github/workflows/tests.yml`) runs `dotnet test Tests/Tests.csproj -c Release -p:SkipRevitSources=true` on `windows-latest`. Expect a wall of `CS8632` warnings from the linked engine on every build (its csproj has `<Nullable>enable</Nullable>`, ours do not); warnings only, `TreatWarningsAsErrors` is set nowhere — do not add `<Nullable>` to either csproj. A stray `RevitWebAppSync_0dy2c2ea_wpftmp.csproj` (leftover of an interrupted WPF build) may sit untracked in the worktree — Task 1 deletes it before building.

---

### Task 1: Branch + merge `origin/feat/cad2bim` + csproj + net48 source fixes

**Files:**
- Create: `Tests/Cad2BimNet48Tests.cs`
- Modify: `App.cs` (merge conflict hunk only — one hunk at develop lines 1020–1026 after auto-merge; keep develop's side)
- Modify: `RevitWebAppSync.csproj:189-218` (the merged-in Cad2Bim block at the end of the file)
- Modify (arrive with the merge, then edited): `Cad2Bim/cad2bim/Geometry.cs:6,53`, `Cad2Bim/cad2bim/ModelSource.cs:85`, `Cad2Bim/cad2bim/Openings.cs:258,316`, `Cad2Bim/cad2bim/Topology.cs:104`, `Cad2Bim/cad2bim/Services/CadRenderSource.cs:348,383`, `Cad2Bim/cad2bim/Services/ClassificationService.cs:1`, `Cad2Bim/cad2bim/ViewModels/ViewModelBase.cs:1`, `Cad2Bim/cad2bim/ViewModels/RelayCommand.cs:1`, `Cad2Bim/cad2bim/ViewModels/LayerViewModel.cs:1`, `Cad2Bim/cad2bim/ViewModels/SettingsViewModel.cs:1`, `Cad2Bim/cad2bim/Views/Rendering/CadViewport.cs:1,170,215`, `Cad2Bim/cad2bim/Views/Controls/NumericScrubBox.cs:1,223`
- Test: `Tests/Cad2BimNet48Tests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: branch `feat/cad-to-bim-pane` with the whole `Cad2Bim/` tree + `Commands/Cad2BimConvertCommand.cs` + `Commands/Cad2BimSelectionCommand.cs` (both still `#if !REVIT2023_24`-wrapped; the builder/brush extraction section deletes them). Add-in compiles on net48, net8.0-windows, net10.0-windows with these engine types available on all TFMs: `Cad2Bim.{Point,Segment,Arc,TextElement,Wall,Opening,Space,CadModel,LayerFilter,ModelSource,Units,CadClassifier,WallGraph}`, `Cad2Bim.Services.{ClassificationService,CadRenderSource}`, `Cad2Bim.ViewModels.{ViewModelBase,RelayCommand,LayerViewModel,SettingsViewModel}`, `Cad2Bim.ViewModels.Shapes.{ArcShape,PolylineShape,SegmentShape,WallShape}`, `Cad2Bim.Views.Rendering.CadViewport`, `Cad2Bim.Views.Controls.NumericScrubBox`; NuGet `ACadSharp 3.6.51` on all TFMs (net48 pulls `System.Memory 4.6.3`).

- [ ] **Step 1: Write the failing test**

`Tests/Cad2BimNet48Tests.cs`:

```csharp
// The Cad2Bim engine (Cad2Bim/cad2bim/*) is linked into all three add-in
// TFMs. net48 (Revit 2023/2024) has no Math.Clamp, no string.EndsWith(char),
// no System.Index and no System.Runtime.Intrinsics, and the add-in does not
// enable ImplicitUsings the way cad2bim.csproj does. The Mac build proves
// all of that once; these tests pin it so a later upstream sync of the
// Cad2Bim folder cannot quietly bring one back and break the Revit 2024
// build on the release box.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class Cad2BimNet48Tests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        // Every engine file the add-in links (csproj paths, backslashes as written there).
        private static readonly string[] Linked =
        {
            @"Cad2Bim\cad2bim\Geometry.cs",
            @"Cad2Bim\cad2bim\ModelSource.cs",
            @"Cad2Bim\cad2bim\Openings.cs",
            @"Cad2Bim\cad2bim\Plans.cs",
            @"Cad2Bim\cad2bim\Outlines.cs",
            @"Cad2Bim\cad2bim\Spaces.cs",
            @"Cad2Bim\cad2bim\SpatialIndex.cs",
            @"Cad2Bim\cad2bim\Topology.cs",
            @"Cad2Bim\cad2bim\Units.cs",
            @"Cad2Bim\cad2bim\Services\CadRenderSource.cs",
            @"Cad2Bim\cad2bim\Services\ClassificationService.cs",
            @"Cad2Bim\cad2bim\ViewModels\ViewModelBase.cs",
            @"Cad2Bim\cad2bim\ViewModels\RelayCommand.cs",
            @"Cad2Bim\cad2bim\ViewModels\LayerViewModel.cs",
            @"Cad2Bim\cad2bim\ViewModels\SettingsViewModel.cs",
            @"Cad2Bim\cad2bim\ViewModels\Shapes\ArcShape.cs",
            @"Cad2Bim\cad2bim\ViewModels\Shapes\PolylineShape.cs",
            @"Cad2Bim\cad2bim\ViewModels\Shapes\SegmentShape.cs",
            @"Cad2Bim\cad2bim\ViewModels\Shapes\WallShape.cs",
            @"Cad2Bim\cad2bim\Views\Rendering\CadViewport.cs",
            @"Cad2Bim\cad2bim\Views\Controls\NumericScrubBox.cs",
        };

        private static string Src(string csprojPath) =>
            File.ReadAllText(Path.Combine(RepoRoot(), csprojPath.Replace('\\', Path.DirectorySeparatorChar)));

        [Fact]
        public void Csproj_LinksEveryEngineFile_UnconditionallyForAllTfms()
        {
            var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "RevitWebAppSync.csproj"));

            foreach (var f in Linked)
                Assert.Contains($"<Compile Include=\"{f}\"", csproj);

            // The headless CLI's IFC writer never enters the add-in (the csproj
            // comment may NAME the file; only a Compile link is banned).
            Assert.DoesNotContain(@"<Compile Include=""Cad2Bim\cad2bim\IfcExporter.cs""", csproj);
            Assert.Contains("<PackageReference Include=\"ACadSharp\" Version=\"3.6.51\" />", csproj);

            // The link group must not be gated off net48 any more.
            var group = csproj.Split(new[] { "<ItemGroup" }, StringSplitOptions.None)
                .Single(chunk => chunk.Contains(@"Cad2Bim\cad2bim\Geometry.cs"));
            var openTag = group.Substring(0, group.IndexOf('>'));
            Assert.DoesNotContain("Condition", openTag);
        }

        [Fact]
        public void LinkedSources_UseNoApiThatNet48Lacks()
        {
            var banned = new[]
            {
                "Math.Clamp(",                 // .NET Core 2.0+
                "System.Runtime.Intrinsics",   // .NET Core 3.0+
                "[^1]",                        // System.Index
                "EndsWith('",                  // string.EndsWith(char)
                "StartsWith('",                // string.StartsWith(char)
                ".Contains('",                 // string.Contains(char)
            };
            foreach (var f in Linked)
            {
                var src = Src(f);
                foreach (var b in banned)
                    Assert.False(src.Contains(b), $"{f} uses `{b}`, which net48 does not have");
            }
        }

        [Fact]
        public void LinkedSources_CarryTheUsingsImplicitUsingsUsedToSupply()
        {
            // cad2bim.csproj enables ImplicitUsings; RevitWebAppSync.csproj does not.
            var needSystem = new[]
            {
                @"Cad2Bim\cad2bim\ViewModels\RelayCommand.cs",
                @"Cad2Bim\cad2bim\ViewModels\SettingsViewModel.cs",
                @"Cad2Bim\cad2bim\ViewModels\LayerViewModel.cs",
                @"Cad2Bim\cad2bim\Views\Rendering\CadViewport.cs",
                @"Cad2Bim\cad2bim\Views\Controls\NumericScrubBox.cs",
            };
            var needGenerics = new[]
            {
                @"Cad2Bim\cad2bim\Services\ClassificationService.cs",
                @"Cad2Bim\cad2bim\ViewModels\ViewModelBase.cs",
                @"Cad2Bim\cad2bim\ViewModels\LayerViewModel.cs",
                @"Cad2Bim\cad2bim\Views\Rendering\CadViewport.cs",
            };
            foreach (var f in needSystem)
                Assert.True(Src(f).Contains("using System;"), f + " needs `using System;`");
            foreach (var f in needGenerics)
                Assert.True(Src(f).Contains("using System.Collections.Generic;"), f + " needs `using System.Collections.Generic;`");
            Assert.True(Src(@"Cad2Bim\cad2bim\Views\Rendering\CadViewport.cs").Contains("using System.Linq;"),
                "CadViewport.cs needs `using System.Linq;` (Skip/Select/ToList)");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimNet48Tests"`
Expected: FAIL — `Csproj_LinksEveryEngineFile_UnconditionallyForAllTfms` fails with `Assert.Contains() Failure: Sub-string not found ... <Compile Include="Cad2Bim\cad2bim\Geometry.cs"` (develop has no Cad2Bim block); the other two fail with `DirectoryNotFoundException: ... Cad2Bim/cad2bim/Geometry.cs` (folder does not exist on develop).

- [ ] **Step 3: Write minimal implementation**

**Step 3.1 — branch and merge.**

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
rm -f RevitWebAppSync_0dy2c2ea_wpftmp.csproj
git fetch origin
git checkout -b feat/cad-to-bim-pane origin/develop
git merge --no-ff --no-commit origin/feat/cad2bim
```

Expected output ends with:
```
Auto-merging App.cs
CONFLICT (content): Merge conflict in App.cs
Auto-merging RevitWebAppSync.csproj
Automatic merge failed; fix conflicts and then commit the result.
```
(`--no-commit` because executors never commit: the merge stays in progress — `MERGE_HEAD` present — and the repo owner's first commit on the branch becomes the merge commit. Everything staged by Tasks 1–3 lands in it. `git status` will say "All conflicts fixed but you are still merging" from Step 3.2 onward; that is the intended state.)

**Step 3.2 — resolve `App.cs` (one hunk).** The branch's whole change to `App.cs` is 38 added lines in `CreateRibbonTab` (`git diff origin/develop...origin/feat/cad2bim -- App.cs`); it collides with develop's `MarkComingSoon` lines at the same spot. The hunk as git writes it (around line 1020):

```
            aiPanel.AddItem(askAiButtonData);

<<<<<<< HEAD
            // Compliance ships as coming soon. All three commands are built and
            // the buttons are added rather than hidden, so the panel keeps its
            // width and nothing on the tab reflows on the release that turns
            // them on — the icon a drafter learns now is in the same place then.
            MarkComingSoon(compliancePanel.AddItem(jkrComplianceButtonData));
            MarkComingSoon(compliancePanel.AddItem(bombaComplianceButtonData));
=======
            // CAD to BIM reads a DWG and builds native walls in the open model. Not on
            // Revit 2023/24: the classifier uses language features that target predates.
#if !REVIT2023_24
            PushButtonData cadToBimButtonData = new PushButtonData(
                "Cad2BimConvert",
                "CAD to\nBIM",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.Cad2BimConvertCommand")
            {
                ToolTip = "Build Revit walls from a DWG",
                LongDescription = "Reads a CAD drawing, works out which linework is wall, and " +
                                  "creates native Revit walls from it in the open model. Save the " +
                                  "model afterwards to get a .rvt.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            aiPanel.AddItem(cadToBimButtonData);

            // What the automatic pass misses, the drafter points at. The guards that make the
            // automatic pass safe are also what make it miss things, and a box drawn round a
            // wall is better evidence than any of them.
            PushButtonData cadSelectionButtonData = new PushButtonData(
                "Cad2BimSelection",
                "Walls from\nSelection",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.Cad2BimSelectionCommand")
            {
                ToolTip = "Build walls from CAD linework you select",
                LongDescription = "Run after CAD to BIM. Drag a box over linework it missed and " +
                                  "the walls are built from it, including walls drawn as a single line.",
                Image = LoadImage("RevitWebAppSync.Resources.revitSync.png", 16),
                LargeImage = LoadImage("RevitWebAppSync.Resources.revitSync.png", 32)
            };

            aiPanel.AddItem(cadSelectionButtonData);
#endif

            // Compliance: JKR
            compliancePanel.AddItem(jkrComplianceButtonData);
            compliancePanel.AddItem(bombaComplianceButtonData);
>>>>>>> origin/feat/cad2bim

            // Cost-to-BIM: Cost Tracker dashboard (restored standalone — see
```

Resolution: keep the HEAD side, drop the branch side entirely (the two `aiPanel` buttons and the `#if !REVIT2023_24` go; Task 3 adds the single new button on its own panel). The resolved text:

```csharp
            aiPanel.AddItem(askAiButtonData);

            // Compliance ships as coming soon. All three commands are built and
            // the buttons are added rather than hidden, so the panel keeps its
            // width and nothing on the tab reflows on the release that turns
            // them on — the icon a drafter learns now is in the same place then.
            MarkComingSoon(compliancePanel.AddItem(jkrComplianceButtonData));
            MarkComingSoon(compliancePanel.AddItem(bombaComplianceButtonData));

            // Cost-to-BIM: Cost Tracker dashboard (restored standalone — see
```

Then:
```bash
git add App.cs
git diff origin/develop -- App.cs        # must print NOTHING: App.cs is byte-identical to develop after Task 1
```

**Step 3.3 — csproj.** After auto-merge the file ends with the branch's block (`RevitWebAppSync.csproj:189-218`). Replace that block. BEFORE (exactly as merged):

```xml
  <!-- The Cad2Bim folder holds its own projects, including a WPF viewer. The SDK glob would
       otherwise compile all of it into the add-in. -->
  <ItemGroup>
    <Compile Remove="Cad2Bim\**" />
    <None Remove="Cad2Bim\**" />
    <Page Remove="Cad2Bim\**" />
    <ApplicationDefinition Remove="Cad2Bim\**" />
  </ItemGroup>

  <!-- Cad2Bim classification, shared with the standalone viewer. Linked rather than
       project-referenced because the viewer targets one framework and the add-in targets
       three; the sources themselves carry no Revit and no WPF dependency. Not on net48:
       Revit 2023/24 predate the language features these use. -->
  <ItemGroup Condition="'$(TargetFramework)' != 'net48'">
    <Compile Include="Cad2Bim\cad2bim\Geometry.cs" Link="Cad2Bim\Geometry.cs" />
    <Compile Include="Cad2Bim\cad2bim\ModelSource.cs" Link="Cad2Bim\ModelSource.cs" />
    <Compile Include="Cad2Bim\cad2bim\Openings.cs" Link="Cad2Bim\Openings.cs" />
    <Compile Include="Cad2Bim\cad2bim\Plans.cs" Link="Cad2Bim\Plans.cs" />
    <Compile Include="Cad2Bim\cad2bim\Outlines.cs" Link="Cad2Bim\Outlines.cs" />
    <Compile Include="Cad2Bim\cad2bim\Spaces.cs" Link="Cad2Bim\Spaces.cs" />
    <Compile Include="Cad2Bim\cad2bim\SpatialIndex.cs" Link="Cad2Bim\SpatialIndex.cs" />
    <Compile Include="Cad2Bim\cad2bim\Topology.cs" Link="Cad2Bim\Topology.cs" />
    <Compile Include="Cad2Bim\cad2bim\Units.cs" Link="Cad2Bim\Units.cs" />
    <Compile Include="Cad2Bim\cad2bim\IfcExporter.cs" Link="Cad2Bim\IfcExporter.cs" />
    <Compile Include="Cad2Bim\cad2bim\Services\CadRenderSource.cs" Link="Cad2Bim\CadRenderSource.cs" />
    <Compile Include="Cad2Bim\cad2bim\ViewModels\Shapes\PolylineShape.cs" Link="Cad2Bim\PolylineShape.cs" />
    <PackageReference Include="ACadSharp" Version="3.6.51" />
  </ItemGroup>
```

AFTER:

```xml
  <!-- The Cad2Bim folder holds its own projects (a WPF viewer and a headless CLI).
       The SDK glob would otherwise compile all of it into the add-in — including
       App.xaml (a second ApplicationDefinition) and AssemblyInfo.cs (a second
       ThemeInfo). Remove first, then link exactly what the pane needs. -->
  <ItemGroup>
    <Compile Remove="Cad2Bim\**" />
    <None Remove="Cad2Bim\**" />
    <Page Remove="Cad2Bim\**" />
    <ApplicationDefinition Remove="Cad2Bim\**" />
  </ItemGroup>

  <!-- Cad2Bim engine + viewport, shared with the standalone viewer. Linked rather than
       project-referenced because the viewer targets one framework and the add-in targets
       three. No Revit dependency; the ViewModels/Views files are plain WPF. Built for ALL
       TFMs: the net48 gaps (Math.Clamp, EndsWith(char), System.Index, the Intrinsics using)
       are closed in the sources themselves; record/init, KeyValuePair.Deconstruct and
       Dictionary.GetValueOrDefault come from Services\Net48Shims.cs. IfcExporter.cs stays
       out — it serves the headless CLI only. MainViewModel / MainWindow / App.xaml /
       Themes are the viewer's own and stay out too. -->
  <ItemGroup>
    <Compile Include="Cad2Bim\cad2bim\Geometry.cs" Link="Cad2Bim\Geometry.cs" />
    <Compile Include="Cad2Bim\cad2bim\ModelSource.cs" Link="Cad2Bim\ModelSource.cs" />
    <Compile Include="Cad2Bim\cad2bim\Openings.cs" Link="Cad2Bim\Openings.cs" />
    <Compile Include="Cad2Bim\cad2bim\Plans.cs" Link="Cad2Bim\Plans.cs" />
    <Compile Include="Cad2Bim\cad2bim\Outlines.cs" Link="Cad2Bim\Outlines.cs" />
    <Compile Include="Cad2Bim\cad2bim\Spaces.cs" Link="Cad2Bim\Spaces.cs" />
    <Compile Include="Cad2Bim\cad2bim\SpatialIndex.cs" Link="Cad2Bim\SpatialIndex.cs" />
    <Compile Include="Cad2Bim\cad2bim\Topology.cs" Link="Cad2Bim\Topology.cs" />
    <Compile Include="Cad2Bim\cad2bim\Units.cs" Link="Cad2Bim\Units.cs" />
    <Compile Include="Cad2Bim\cad2bim\Services\CadRenderSource.cs" Link="Cad2Bim\Services\CadRenderSource.cs" />
    <Compile Include="Cad2Bim\cad2bim\Services\ClassificationService.cs" Link="Cad2Bim\Services\ClassificationService.cs" />
    <Compile Include="Cad2Bim\cad2bim\ViewModels\ViewModelBase.cs" Link="Cad2Bim\ViewModels\ViewModelBase.cs" />
    <Compile Include="Cad2Bim\cad2bim\ViewModels\RelayCommand.cs" Link="Cad2Bim\ViewModels\RelayCommand.cs" />
    <Compile Include="Cad2Bim\cad2bim\ViewModels\LayerViewModel.cs" Link="Cad2Bim\ViewModels\LayerViewModel.cs" />
    <Compile Include="Cad2Bim\cad2bim\ViewModels\SettingsViewModel.cs" Link="Cad2Bim\ViewModels\SettingsViewModel.cs" />
    <Compile Include="Cad2Bim\cad2bim\ViewModels\Shapes\ArcShape.cs" Link="Cad2Bim\ViewModels\Shapes\ArcShape.cs" />
    <Compile Include="Cad2Bim\cad2bim\ViewModels\Shapes\PolylineShape.cs" Link="Cad2Bim\ViewModels\Shapes\PolylineShape.cs" />
    <Compile Include="Cad2Bim\cad2bim\ViewModels\Shapes\SegmentShape.cs" Link="Cad2Bim\ViewModels\Shapes\SegmentShape.cs" />
    <Compile Include="Cad2Bim\cad2bim\ViewModels\Shapes\WallShape.cs" Link="Cad2Bim\ViewModels\Shapes\WallShape.cs" />
    <Compile Include="Cad2Bim\cad2bim\Views\Rendering\CadViewport.cs" Link="Cad2Bim\Views\Rendering\CadViewport.cs" />
    <Compile Include="Cad2Bim\cad2bim\Views\Controls\NumericScrubBox.cs" Link="Cad2Bim\Views\Controls\NumericScrubBox.cs" />
    <PackageReference Include="ACadSharp" Version="3.6.51" />
  </ItemGroup>
```

Every `Include` path above was verified against `git ls-tree -r --name-only origin/feat/cad2bim -- Cad2Bim` (all 21 exist; `PolylineShape.cs` was already linked on the branch and is kept because `CadRenderSource.cs` emits it).

**Step 3.4 — net48 / implicit-usings fixes in the linked sources.** If you build now, `-f net8.0-windows` fails with CS0246/CS0103/CS1061 in the 7 files that relied on ImplicitUsings, and `-f net48` additionally fails with CS0234 (`System.Runtime.Intrinsics`), CS0117 (`Math` has no `Clamp`) ×8, CS1503 (`EndsWith('*')`: cannot convert char to string) and CS0518 (`System.Index` not defined, from `vertices[^1]`). Each hit and its fix, exact before → after:

1. `Cad2Bim/cad2bim/Geometry.cs:6` — delete the line `using System.Runtime.Intrinsics.Arm;` (unused; the namespace does not exist on net48).
2. `Cad2Bim/cad2bim/Geometry.cs:53`
   `double angleRad = Math.Asin(Math.Clamp(cross, -1.0, 1.0));`
   → `double angleRad = Math.Asin(Math.Min(Math.Max(cross, -1.0), 1.0));`
3. `Cad2Bim/cad2bim/ModelSource.cs:85`
   `return pattern.EndsWith('*') || cursor == value.Length;`
   → `return pattern.EndsWith("*", StringComparison.Ordinal) || cursor == value.Length;`
4. `Cad2Bim/cad2bim/Openings.cs:258`
   `Point on = PointAlong(line, Math.Clamp(at, 0, line.Length));`
   → `Point on = PointAlong(line, Math.Min(Math.Max(at, 0), line.Length));`
5. `Cad2Bim/cad2bim/Openings.cs:316`
   `double angle = Math.Acos(Math.Clamp(cosine, 0.0, 1.0)) * 180.0 / Math.PI;`
   → `double angle = Math.Acos(Math.Min(Math.Max(cosine, 0.0), 1.0)) * 180.0 / Math.PI;`
6. `Cad2Bim/cad2bim/Topology.cs:104`
   `.Select(t => Math.Clamp(t, 0.0, length))`
   → `.Select(t => Math.Min(Math.Max(t, 0.0), length))`
7. `Cad2Bim/cad2bim/Services/CadRenderSource.cs:348` (`vertices` is `IReadOnlyList<(double X, double Y, double Bulge)>`)
   `points.Add((vertices[^1].X, vertices[^1].Y));`
   → `points.Add((vertices[vertices.Count - 1].X, vertices[vertices.Count - 1].Y));`
8. `Cad2Bim/cad2bim/Services/CadRenderSource.cs:383`
   `Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / StepAngle) + 1, MinCurvePoints, MaxCurvePoints);`
   → `Math.Min(Math.Max((int)Math.Ceiling(Math.Abs(sweep) / StepAngle) + 1, MinCurvePoints), MaxCurvePoints);`
9. `Cad2Bim/cad2bim/Views/Rendering/CadViewport.cs:170`
   `scale = Math.Clamp(scale, MinScale, MaxScale);`
   → `scale = Math.Min(Math.Max(scale, MinScale), MaxScale);`
10. `Cad2Bim/cad2bim/Views/Rendering/CadViewport.cs:215`
    `double newScale = Math.Clamp(Scale * factor, MinScale, MaxScale);`
    → `double newScale = Math.Min(Math.Max(Scale * factor, MinScale), MaxScale);`
11. `Cad2Bim/cad2bim/Views/Controls/NumericScrubBox.cs:223`
    `=> Math.Clamp(value, this.Minimum, this.Maximum);`
    → `=> Math.Min(Math.Max(value, this.Minimum), this.Maximum);`

Explicit usings (insert as the FIRST lines of each file, above whatever is there; all TFMs need these):

12. `Cad2Bim/cad2bim/Services/ClassificationService.cs` — file currently starts at `namespace Cad2Bim.Services {`. Insert before it:
    ```csharp
    using System.Collections.Generic;

    ```
13. `Cad2Bim/cad2bim/ViewModels/ViewModelBase.cs` — before `using System.ComponentModel;` insert:
    ```csharp
    using System.Collections.Generic;
    ```
14. `Cad2Bim/cad2bim/ViewModels/RelayCommand.cs` — before `using System.Windows.Input;` insert:
    ```csharp
    using System;
    ```
15. `Cad2Bim/cad2bim/ViewModels/LayerViewModel.cs` — file starts at `namespace Cad2Bim.ViewModels {`. Insert before it:
    ```csharp
    using System;
    using System.Collections.Generic;

    ```
16. `Cad2Bim/cad2bim/ViewModels/SettingsViewModel.cs` — before `using System.ComponentModel;` insert:
    ```csharp
    using System;
    ```
17. `Cad2Bim/cad2bim/Views/Rendering/CadViewport.cs` — before `using System.Collections.Specialized;` insert:
    ```csharp
    using System;
    using System.Collections.Generic;
    using System.Linq;
    ```
18. `Cad2Bim/cad2bim/Views/Controls/NumericScrubBox.cs` — before `using System.Globalization;` insert:
    ```csharp
    using System;
    ```

What is NOT touched and why: `record`/`init`/`readonly record struct` (Geometry, Topology, CadRenderSource, Shapes) — `IsExternalInit` is in `Services/Net48Shims.cs`; `foreach (var (wall, positions) in byWall)` (`Openings.cs:161`) and `GetValueOrDefault` (`ModelSource.cs:282`, `Spaces.cs:125,130`, both on `Dictionary<,>`) — shimmed there too; C# 12 collection expressions `= []` on `List<>`/`Dictionary<>` (`CadViewport.cs:57-58`), `??=`, switch expressions, tuples (`System.ValueTuple` is in-box on 4.7+), target-typed `new()`, `is not` — compiler-only, `LangVersion latest` is already set. `string.Split('*')` (`ModelSource.cs:68`) binds to the `params char[]` overload and is fine. Expect a wall of **CS8632 warnings** ("nullable annotation outside a #nullable context") on all TFMs: cad2bim.csproj has `<Nullable>enable</Nullable>`, the add-in does not; warnings only, `TreatWarningsAsErrors` is not set anywhere — leave them.

**Step 3.5 — build all three TFMs.**

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows
```

Expected: each ends `Build succeeded.` with 0 errors (first run restores `ACadSharp 3.6.51` from nuget.org — the local cache only has 1.1.19 — plus `CSMath`/`CSUtilities` inside the package and, for net48, `System.Memory 4.6.3`). If net48 reports any further `CS0117 'Math' does not contain a definition for 'Clamp'`, `CS0518 ... 'System.Index'`, or `CS1503 ... 'char' to 'string'`, the file/line it names was missed above — apply the same rewrite pattern. If it reports `CS0101 The namespace 'System.Runtime.CompilerServices' already contains a definition for 'IsExternalInit'`, someone created `Cad2Bim/IsExternalInit.cs` — delete it (Contract correction 1). `Commands/Cad2BimConvertCommand.cs` and `Commands/Cad2BimSelectionCommand.cs` arrive with the merge and compile on net8/net10 (they keep their own `#if !REVIT2023_24`); the extraction section deletes them.

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimNet48Tests"`
Expected: PASS (3 tests). And the three `dotnet build` commands from Step 3.5 each print `Build succeeded.` — the build IS the acceptance test for this task.

- [ ] **Step 5: Stage**

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git add App.cs RevitWebAppSync.csproj Tests/Cad2BimNet48Tests.cs \
  Cad2Bim/cad2bim/Geometry.cs Cad2Bim/cad2bim/ModelSource.cs Cad2Bim/cad2bim/Openings.cs \
  Cad2Bim/cad2bim/Topology.cs Cad2Bim/cad2bim/Services/CadRenderSource.cs \
  Cad2Bim/cad2bim/Services/ClassificationService.cs Cad2Bim/cad2bim/ViewModels/ViewModelBase.cs \
  Cad2Bim/cad2bim/ViewModels/RelayCommand.cs Cad2Bim/cad2bim/ViewModels/LayerViewModel.cs \
  Cad2Bim/cad2bim/ViewModels/SettingsViewModel.cs Cad2Bim/cad2bim/Views/Rendering/CadViewport.cs \
  Cad2Bim/cad2bim/Views/Controls/NumericScrubBox.cs
git status --short | grep -v '^A \|^M ' || true    # nothing unstaged from this task should remain
```
(The rest of the `Cad2Bim/` tree and the two old commands were staged by the merge itself.) Repo owner rule: executors STAGE, never commit — the in-progress merge is committed by the owner.

**net48 fix list is the union of every section's findings.** Section E independently flagged `System.Runtime.Intrinsics.Arm` (Geometry.cs), `Math.Clamp`, `string.EndsWith(char)`, `vertices[^1]`, `Dictionary.GetValueOrDefault` (Spaces.cs) and C# 12 collection expressions; all are covered above — `GetValueOrDefault` and `KeyValuePair.Deconstruct` by `Services/Net48Shims.cs`, collection expressions by the compiler, the rest by the 11 rewrites in Step 3.4. No polyfill file is created anywhere in this plan.

**Merge left in progress on purpose** (`git merge --no-ff --no-commit`): all tasks stage into the same pending merge commit; the owner's first `git commit` on `feat/cad-to-bim-pane` is the merge. `git status` shows "All conflicts fixed but you are still merging" throughout — no executor runs `git merge --abort`. If the owner prefers a clean merge commit first, they commit right after Step 3.2 and later tasks stage on top.

---

### Task 2: Ribbon icons `CadToBim{16,32,64}.png` + SVG masters

**Files:**
- Create: `Resources/Icons/svg/CadToBim16.svg`, `Resources/Icons/svg/CadToBim32.svg`, `Resources/Icons/CadToBim16.png`, `Resources/Icons/CadToBim32.png`, `Resources/Icons/CadToBim64.png`
- Modify: `scripts/gen-ribbon-icons.mjs:152-165` (append a `CadToBim` entry to `ICONS`, after `CostTracker`)
- Test: `Tests/CadToBimIconTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: manifest resources `RevitWebAppSync.Resources.Icons.CadToBim16.png` / `CadToBim32.png` / `CadToBim64.png`, picked up by the existing glob `<EmbeddedResource Include="Resources\Icons\*.png" />` (`RevitWebAppSync.csproj`, no csproj edit needed) and loaded by `App.LoadIcon("CadToBim", 16|32)` in Task 3.

- [ ] **Step 1: Write the failing test**

`Tests/CadToBimIconTests.cs`:

```csharp
// Ribbon icons are embedded by the Resources\Icons\*.png glob and bound by
// App.LoadIcon("CadToBim", 16|32). A PNG rasterised at the wrong pixel size
// is what blows a ribbon button out (Resources/Icons/README.md), and a
// missing file is a null Image with no error at all — so the three sizes are
// pinned here, straight from the PNG IHDR chunk.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CadToBimIconTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static string Icons(params string[] parts) =>
            Path.Combine(new[] { RepoRoot(), "Resources", "Icons" }.Concat(parts).ToArray());

        [Theory]
        [InlineData(16)]
        [InlineData(32)]
        [InlineData(64)]
        public void Png_ExistsAtItsNominalPixelSize(int size)
        {
            var path = Icons($"CadToBim{size}.png");
            Assert.True(File.Exists(path), "missing " + path);

            var bytes = File.ReadAllBytes(path);
            // PNG layout: 8-byte signature, IHDR length (4), "IHDR" (4),
            // width (4, big-endian), height (4, big-endian).
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());
            int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            Assert.Equal(size, width);
            Assert.Equal(size, height);
        }

        [Theory]
        [InlineData(16, "1.3")]
        [InlineData(32, "1.8")]
        public void SvgMaster_IsOnItsGrid_WithTheSetsInkAndOneAccent(int grid, string strokeWidth)
        {
            var svg = File.ReadAllText(Icons("svg", $"CadToBim{grid}.svg"));
            Assert.Contains($"viewBox=\"0 0 {grid} {grid}\"", svg);
            Assert.Contains($"stroke-width=\"{strokeWidth}\"", svg);
            Assert.Contains("#33383D", svg);   // graphite structure (README rule)
            Assert.Contains("#1B6EC2", svg);   // the one accent
            Assert.DoesNotContain("#D93B94", svg); // never two accents: no AI magenta here
        }

        [Fact]
        public void Generator_CarriesTheDrawing()
        {
            // README: the script holds the drawings; the PNGs are output.
            var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "gen-ribbon-icons.mjs"));
            Assert.Contains("CadToBim: {", script);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~CadToBimIconTests"`
Expected: FAIL — `Png_ExistsAtItsNominalPixelSize` ×3 with `missing .../Resources/Icons/CadToBim16.png`; `SvgMaster_...` ×2 with `FileNotFoundException`; `Generator_CarriesTheDrawing` with `Assert.Contains() Failure ... CadToBim: {`.

- [ ] **Step 3: Write minimal implementation**

**Step 3.1 — the drawing, in the generator (source of truth per README).** In `scripts/gen-ribbon-icons.mjs`, the `CostTracker` entry currently closes the `ICONS` map:

```js
  CostTracker: {
    32: `...`,
    16: `...`
  }
};
```

Change the closing `  }\n};` to add the entry (note the comma after CostTracker's `}`):

```js
  CostTracker: {
    32: `<path d="M5.4 5v21.6H27" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/>
         <g fill="none" stroke="currentColor" stroke-width="1.8">
           <rect x="9.4" y="18.4" width="4.4" height="8.2" fill="#C4DFF3"/>
           <rect x="15.6" y="14.4" width="4.4" height="12.2" fill="#7FBEE4"/>
           <rect x="21.8" y="9.4" width="4.4" height="17.2" fill="#3E8FCB"/>
         </g>`,
    16: `<path d="M2.7 2.5v10.8h10.8" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>
         <g fill="none" stroke="currentColor" stroke-width="1.3">
           <rect x="4.9" y="9.4" width="2.4" height="3.9" fill="#C4DFF3"/>
           <rect x="8.4" y="6.7" width="2.4" height="6.6" fill="#3E8FCB"/>
         </g>`
  },
  // CAD to BIM: a DWG sheet (dog-eared, two plan lines) feeding an isometric
  // block whose top face carries the accent — the model is the meaning. Same
  // cube geometry family as DownloadModel; accent #1B6EC2 (model surfaces).
  // The 16 keeps one plan line.
  CadToBim: {
    32: `<g fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round">
           <path d="M3.5 3.5h8l3.5 3.5v12h-11.5z"/>
           <path d="M11.5 3.5V7H15"/>
           <path d="M6.5 11h5"/><path d="M6.5 14.5h3"/>
           <path d="M22.5 14.5l6 3.2-6 3.2-6-3.2z" fill="#1B6EC2"/>
           <path d="M22.5 14.5l6 3.2v7.3l-6 3.2-6-3.2v-7.3z"/>
           <path d="M16.5 17.7l6 3.2 6-3.2"/>
           <path d="M22.5 20.9v7.3"/>
         </g>`,
    16: `<g fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round">
           <path d="M1.5 1.5h4l2 2v6.5h-6z"/>
           <path d="M5.5 1.5v2h2"/>
           <path d="M3.5 6h2.5"/>
           <path d="M11.3 7.2l3.2 1.7-3.2 1.7-3.2-1.7z" fill="#1B6EC2"/>
           <path d="M11.3 7.2l3.2 1.7v3.9l-3.2 1.7-3.2-1.7V8.9z"/>
           <path d="M8.1 8.9l3.2 1.7 3.2-1.7"/>
           <path d="M11.3 10.6v3.9"/>
         </g>`
  }
};
```

**Step 3.2 — the SVG masters** (exactly what `svgFor()` in the script emits: `currentColor` → `#33383D`, wrapper with `fill="none"`). Write these two files by hand — the script's dependency `@resvg/resvg-js` is not installed and the repo has no `package.json` to install it into.

`Resources/Icons/svg/CadToBim32.svg`:
```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="32" height="32" fill="none">
<g fill="none" stroke="#33383D" stroke-width="1.8" stroke-linejoin="round">
           <path d="M3.5 3.5h8l3.5 3.5v12h-11.5z"/>
           <path d="M11.5 3.5V7H15"/>
           <path d="M6.5 11h5"/><path d="M6.5 14.5h3"/>
           <path d="M22.5 14.5l6 3.2-6 3.2-6-3.2z" fill="#1B6EC2"/>
           <path d="M22.5 14.5l6 3.2v7.3l-6 3.2-6-3.2v-7.3z"/>
           <path d="M16.5 17.7l6 3.2 6-3.2"/>
           <path d="M22.5 20.9v7.3"/>
         </g>
</svg>
```

`Resources/Icons/svg/CadToBim16.svg`:
```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16" width="16" height="16" fill="none">
<g fill="none" stroke="#33383D" stroke-width="1.3" stroke-linejoin="round">
           <path d="M1.5 1.5h4l2 2v6.5h-6z"/>
           <path d="M5.5 1.5v2h2"/>
           <path d="M3.5 6h2.5"/>
           <path d="M11.3 7.2l3.2 1.7-3.2 1.7-3.2-1.7z" fill="#1B6EC2"/>
           <path d="M11.3 7.2l3.2 1.7v3.9l-3.2 1.7-3.2-1.7V8.9z"/>
           <path d="M8.1 8.9l3.2 1.7 3.2-1.7"/>
           <path d="M11.3 10.6v3.9"/>
         </g>
</svg>
```

Geometry check (32 grid): sheet occupies x 3.5–15, y 3.5–19; cube spans x 16.5–28.5, y 14.5–28.2; with the 1.8 stroke nothing exceeds 29.1 — inside the 32 box. (16 grid): sheet x 1.5–7.5, y 1.5–10; cube x 8.1–14.5, y 7.2–14.5; max 15.15 with stroke — inside.

**Step 3.3 — rasterise** (verified tool on this Mac: `/opt/homebrew/bin/rsvg-convert`; `sips` cannot rasterise SVG, `qlmanage -t` renders SVG but only to a fixed thumbnail and cannot be trusted for exact pixel sizes; `magick`/`inkscape` absent):

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
rsvg-convert -w 16 -h 16 Resources/Icons/svg/CadToBim16.svg -o Resources/Icons/CadToBim16.png
rsvg-convert -w 32 -h 32 Resources/Icons/svg/CadToBim32.svg -o Resources/Icons/CadToBim32.png
rsvg-convert -w 64 -h 64 Resources/Icons/svg/CadToBim32.svg -o Resources/Icons/CadToBim64.png
sips -g pixelWidth -g pixelHeight Resources/Icons/CadToBim16.png Resources/Icons/CadToBim32.png Resources/Icons/CadToBim64.png
```
Expected `sips` output: `pixelWidth: 16 / pixelHeight: 16`, `32 / 32`, `64 / 64`. Eyeball: `qlmanage -p Resources/Icons/CadToBim64.png` (transparent background, graphite sheet + blue-topped block).

(If you would rather run the generator: `cd /tmp && npm i @resvg/resvg-js` then `NODE_PATH=/tmp/node_modules node scripts/gen-ribbon-icons.mjs Resources/Icons` — that rewrites EVERY icon's SVG+PNG; only the three CadToBim PNGs and two SVGs should show in `git status`. rsvg-convert is the smaller footprint.)

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~CadToBimIconTests"`
Expected: PASS (6 tests). Then confirm the existing glob embeds them: `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows && strings bin/Debug/net8.0-windows/RevitWebAppSync.dll | grep -c 'RevitWebAppSync.Resources.Icons.CadToBim'` → prints `3` (one manifest resource name per PNG; `/usr/bin/strings` is present on this Mac).

- [ ] **Step 5: Stage**

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git add Resources/Icons/CadToBim16.png Resources/Icons/CadToBim32.png Resources/Icons/CadToBim64.png \
  Resources/Icons/svg/CadToBim16.svg Resources/Icons/svg/CadToBim32.svg \
  scripts/gen-ribbon-icons.mjs Tests/CadToBimIconTests.cs
```

---

### Task 3: Ribbon panel + `OpenCadToBimCommand` + pane registration + ExternalEvent

**Files:**
- Create: `Commands/OpenCadToBimCommand.cs`
- Modify: `App.cs:1-11` (usings), `App.cs:52-53` (statics, after `CopilotPaneHost`), `App.cs:504-505` (OnStartup — insert after the Issues pane block, before "Subscribe to document changes"), `App.cs:757-758` (OnShutdown — after the four `Dispose()` lines), `App.cs:799-800` (new `RibbonPanel` between `aiPanel` and `compliancePanel`), `App.cs:934-935` (new `PushButtonData` after `askAiButtonData`), `App.cs:1018-1019` (`cadPanel.AddItem` after `aiPanel.AddItem(askAiButtonData)`)
- Test: `Tests/CadToBimRibbonTests.cs`

**Slot:** runs AFTER Tasks 12, 17 and 19 (see "Execution order") — its unit test is a source lint and passes standalone, but its `dotnet build` gate needs `CadToBimBuildHandler`, `CadToBimPaneHost`, `CadToBimPanel` and `CadToBimViewModel` in the tree.

**Interfaces:**
- Consumes (from other sections; the source-lint test does not need them, the compile gate does):
  - `RevitWebAppSync.Services.CadToBim.CadToBimBuildHandler : IExternalEventHandler` — parameterless ctor; `public BuildRequest Request { get; set; }`, `public event Action<BuildReport> Completed`.
  - `RevitWebAppSync.UI.CadToBim.CadToBimPaneHost : Page, IDockablePaneProvider` — parameterless ctor; `public static readonly DockablePaneId PaneId = new(new Guid("B1A4C057-0005-4000-8000-000000000005"))`; `public CadToBimPanel Panel { get; }`.
  - `RevitWebAppSync.UI.CadToBim.CadToBimPanel : UserControl` — `public CadToBimViewModel ViewModel { get; }`.
  - `RevitWebAppSync.UI.CadToBim.CadToBimViewModel` — `public Task OpenAsync(string path)`.
  - `Services.UpdateService.EnsureUpToDate()` (exists on develop), `ZeroDocCommandAvailability` (exists on develop), `App.LoadIcon(string, int)` (exists), Task 2's PNGs.
  - `RevitWebAppSync.UI.CadToBim.CadToBimPanel.RefreshLevels(Autodesk.Revit.DB.Document)` (Task 18) — fills the level picker; must be called from a valid API context, which the command is.
- Produces:
  - `public static CadToBimPaneHost App.CadToBimPaneHost { get; private set; }`
  - `public static CadToBimBuildHandler App.CadToBimBuildHandler { get; private set; }`
  - `public static ExternalEvent App.CadToBimBuildEvent { get; private set; }` — created in `OnStartup` BEFORE `new CadToBimPaneHost()`, so the panel's constructor may do `new CadToBimViewModel(App.CadToBimBuildHandler, App.CadToBimBuildEvent)`. All three are null after `OnShutdown`.
  - Ribbon: tab "Bina", panel "CAD to BIM" (between "BINA AI" and "Compliance"), button internal name `CadToBim` → `RevitWebAppSync.Commands.OpenCadToBimCommand`. Revit command-id form, if anyone needs `PostCommand`: `CustomCtrl_%CustomCtrl_%Bina%CAD to BIM%CadToBim`.
  - Dockable pane registered as `"BINA CAD to BIM"` under `CadToBimPaneHost.PaneId`.

- [ ] **Step 1: Write the failing test**

`Tests/CadToBimRibbonTests.cs`:

```csharp
// The add-in has no ribbon harness: a PushButtonData whose class-name string
// does not match a real IExternalCommand fails only at click time inside
// Revit ("command not found"), and a pane the view model needs an
// ExternalEvent for must have that event created BEFORE the pane host.
// Both are string/order facts about App.cs, pinned here the way
// ToolManifestTests pins ToolRegistry's switch arms.

using System;
using System.IO;
using Xunit;

namespace RevitAddinSync.Tests
{
    public class CadToBimRibbonTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RevitWebAppSync.csproj")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static string AppCs() => File.ReadAllText(Path.Combine(RepoRoot(), "App.cs"));

        [Fact]
        public void Ribbon_HasACadToBimPanel_BetweenAiAndCompliance()
        {
            var app = AppCs();
            int ai = app.IndexOf("CreateRibbonPanel(tabName, \"BINA AI\")", StringComparison.Ordinal);
            int cad = app.IndexOf("CreateRibbonPanel(tabName, \"CAD to BIM\")", StringComparison.Ordinal);
            int compliance = app.IndexOf("CreateRibbonPanel(tabName, \"Compliance\")", StringComparison.Ordinal);
            Assert.True(ai > 0, "BINA AI panel missing");
            Assert.True(cad > ai, "CAD to BIM panel must be created after BINA AI");
            Assert.True(compliance > cad, "CAD to BIM panel must be created before Compliance");
            Assert.Contains("cadPanel.AddItem(cadToBimButtonData);", app);
        }

        [Fact]
        public void Button_NamesARealCommandClass_WithIconsAndZeroDocAvailability()
        {
            var app = AppCs();
            const string className = "RevitWebAppSync.Commands.OpenCadToBimCommand";
            Assert.Contains("\"" + className + "\"", app);

            var cmd = File.ReadAllText(Path.Combine(RepoRoot(), "Commands", "OpenCadToBimCommand.cs"));
            Assert.Contains("namespace RevitWebAppSync.Commands", cmd);
            Assert.Contains("public class OpenCadToBimCommand : IExternalCommand", cmd);
            Assert.Contains("[Transaction(TransactionMode.Manual)]", cmd);
            Assert.Contains("Services.UpdateService.EnsureUpToDate()", cmd);   // OTA gate first, like every ribbon command
            Assert.Contains("*.dwg;*.dxf", cmd);
            Assert.Contains("RefreshLevels(", cmd);              // level picker filled in API context, before OpenAsync

            // Anchor on the ctor's first two arguments, not on "CadToBim", alone —
            // LoadIcon("CadToBim", 16) contains that substring too.
            int button = app.IndexOf("\"CadToBim\",\n                \"CAD to\\nBIM\",", StringComparison.Ordinal);
            Assert.True(button > 0, "PushButtonData \"CadToBim\" / \"CAD to\\nBIM\" missing");
            var block = app.Substring(button, Math.Min(1200, app.Length - button));
            Assert.Contains("LoadIcon(\"CadToBim\", 16)", block);
            Assert.Contains("LoadIcon(\"CadToBim\", 32)", block);
            Assert.Contains("AvailabilityClassName = typeof(ZeroDocCommandAvailability).FullName", block);
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "Resources", "Icons", "CadToBim16.png")));
            Assert.True(File.Exists(Path.Combine(RepoRoot(), "Resources", "Icons", "CadToBim32.png")));
        }

        [Fact]
        public void Startup_CreatesBuildEvent_BeforeThePaneHost_AndRegistersThePane()
        {
            var app = AppCs();
            int handler = app.IndexOf("CadToBimBuildHandler = new CadToBimBuildHandler();", StringComparison.Ordinal);
            int evt = app.IndexOf("CadToBimBuildEvent = ExternalEvent.Create(CadToBimBuildHandler);", StringComparison.Ordinal);
            int host = app.IndexOf("CadToBimPaneHost = new CadToBimPaneHost();", StringComparison.Ordinal);
            Assert.True(handler > 0, "handler not created in OnStartup");
            Assert.True(evt > handler, "ExternalEvent must be created after the handler");
            Assert.True(host > evt, "pane host must be created AFTER the ExternalEvent (its view model takes both)");

            Assert.Contains("\"BINA CAD to BIM\"", app);
            Assert.Contains("CadToBimPaneHost.PaneId,", app);
            Assert.Contains("new { name = \"cad_to_bim_pane\", error_class = cadEx.GetType().Name }", app);
        }

        [Fact]
        public void Shutdown_DropsEventHandlerAndHost()
        {
            var app = AppCs();
            int shutdown = app.IndexOf("public Result OnShutdown(", StringComparison.Ordinal);
            Assert.True(shutdown > 0);
            var body = app.Substring(shutdown);
            Assert.Contains("CadToBimBuildEvent?.Dispose();", body);
            Assert.Contains("CadToBimBuildEvent = null;", body);
            Assert.Contains("CadToBimBuildHandler = null;", body);
            Assert.Contains("CadToBimPaneHost = null;", body);
        }

        [Fact]
        public void OldBranchCommands_AreGone_FromTheRibbon()
        {
            var app = AppCs();
            Assert.DoesNotContain("Cad2BimConvertCommand", app);
            Assert.DoesNotContain("Cad2BimSelectionCommand", app);
            Assert.DoesNotContain("#if !REVIT2023_24\n            PushButtonData cad", app);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~CadToBimRibbonTests"`
Expected: FAIL — `Ribbon_HasACadToBimPanel_...` with `CAD to BIM panel must be created after BINA AI` (cad = −1); `Button_Names...` with `Assert.Contains() Failure ... "RevitWebAppSync.Commands.OpenCadToBimCommand"`; `Startup_Creates...` with `handler not created in OnStartup`; `Shutdown_Drops...` with `Assert.Contains() Failure ... CadToBimBuildEvent?.Dispose();`. `OldBranchCommands_AreGone_FromTheRibbon` PASSES already (Task 1 resolved the conflict to develop's side) — that is expected.

- [ ] **Step 3: Write minimal implementation**

**Step 3.1 — `Commands/OpenCadToBimCommand.cs` (new file, whole content):**

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.UI.CadToBim;

namespace RevitWebAppSync.Commands
{
    /// <summary>
    /// Ribbon command: shows the right-docked CAD to BIM pane, asks for a DWG/DXF and
    /// hands the path to the pane's view model, which detects on the thread pool and
    /// draws the preview. Everything after that — corrections, Confirm, the build on
    /// Revit's thread through App.CadToBimBuildEvent — happens inside the pane.
    ///
    /// Zero-document availability (ZeroDocCommandAvailability on the button): "Save as
    /// new .rvt" needs no open project, so the button must work from Revit's empty
    /// ribbon too. The command itself never touches the active document.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class OpenCadToBimCommand : IExternalCommand
    {
        private const string Title = "BINA CAD to BIM";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // OTA gate: a mandatory update blocks the plugin until installed.
            if (!Services.UpdateService.EnsureUpToDate()) return Result.Cancelled;

            try
            {
                UIApplication uiApp = commandData.Application;

                // GetDockablePane throws (Autodesk.Revit.Exceptions.ArgumentException)
                // rather than returning null when the id was never registered — the
                // catch below turns that into the same "restart Revit" dialog.
                DockablePane pane = uiApp.GetDockablePane(CadToBimPaneHost.PaneId);
                if (pane == null)
                {
                    TaskDialog.Show(Title, "CAD to BIM panel not found. Please restart Revit.");
                    return Result.Failed;
                }

                CadToBimPaneHost host = App.CadToBimPaneHost;
                CadToBimViewModel viewModel = host?.Panel?.ViewModel;
                if (viewModel == null)
                {
                    // The host swallowed its own init failure and is showing the
                    // error text instead of the panel (same pattern as CopilotPaneHost).
                    TaskDialog.Show(Title, "CAD to BIM panel failed to load. Please restart Revit.");
                    return Result.Failed;
                }

                if (!pane.IsShown())
                {
                    pane.Show();
                }

                string path = PickDrawing();
                if (path == null)
                {
                    // Cancel = nothing happens; the pane stays open with whatever it had.
                    return Result.Cancelled;
                }

                // The level picker reads the Document, which is only legal here (a valid
                // API context) — never from the pane. No document = empty picker.
                host.Panel.RefreshLevels(uiApp.ActiveUIDocument?.Document);

                // Fire-and-forget: detection runs on the thread pool inside OpenAsync
                // and reports through the pane's status line. The command returns at
                // once so Revit stays responsive. A fault that escapes the view model
                // still gets a dialog, marshalled back onto the UI thread.
                Task open = viewModel.OpenAsync(path);
                open.ContinueWith(t =>
                {
                    Exception root = t.Exception?.GetBaseException() ?? t.Exception;
                    host.Dispatcher.BeginInvoke(new Action(() =>
                        TaskDialog.Show(Title + " — Error",
                            $"Could not open {Path.GetFileName(path)}: {root?.Message}")));
                }, TaskContinuationOptions.OnlyOnFaulted);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title + " — Error", $"Failed to open CAD to BIM: {ex.Message}");
                return Result.Failed;
            }
        }

        private static string PickDrawing()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the drawing to convert",
                Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf",
                CheckFileExists = true,
                Multiselect = false
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }
    }
}
```
(`Microsoft.Win32.OpenFileDialog` is the WPF dialog the repo already uses — `Commands/CostImportCommand.cs:29`, `UI/Copilot/Controls/PromptBar.xaml.cs:188` — available on all three TFMs via `UseWPF`.)

**Step 3.2 — `App.cs` usings** (`App.cs:1-11`). After `using RevitWebAppSync.UI.Copilot;` add:

```csharp
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim;
```

**Step 3.3 — `App.cs` statics.** After line 53 (`public static CopilotPaneHost CopilotPaneHost { get; private set; }`) add:

```csharp

        // CAD to BIM dockable pane + the ExternalEvent that runs a build on
        // Revit's thread. The pane is plain WPF with no API context; the event
        // is created in OnStartup (a valid API context) BEFORE the pane host,
        // because the pane's view model takes the handler and the event in its
        // constructor and raises the event from Confirm.
        public static CadToBimPaneHost CadToBimPaneHost { get; private set; }
        public static CadToBimBuildHandler CadToBimBuildHandler { get; private set; }
        public static ExternalEvent CadToBimBuildEvent { get; private set; }
```

**Step 3.4 — `App.cs` OnStartup registration.** Insert after the Issues pane block's closing `}` (`App.cs:504`), before the comment `// Subscribe to document changes for live cost updates`:

```csharp

                // Register the CAD to BIM dockable pane. Handler + ExternalEvent
                // first: ExternalEvent.Create is only legal here, and the pane's
                // view model wants both when the host constructs it.
                try
                {
                    CadToBimBuildHandler = new CadToBimBuildHandler();
                    CadToBimBuildEvent = ExternalEvent.Create(CadToBimBuildHandler);

                    CadToBimPaneHost = new CadToBimPaneHost();
                    application.RegisterDockablePane(
                        CadToBimPaneHost.PaneId,
                        "BINA CAD to BIM",
                        CadToBimPaneHost);
                }
                catch (Exception cadEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[BINA] CAD to BIM dockable pane registration failed: {cadEx.Message}");
                    Services.TelemetryService.Track("subsystem", "failed",
                        new { name = "cad_to_bim_pane", error_class = cadEx.GetType().Name });
                }
```

**Step 3.5 — `App.cs` OnShutdown.** After `try { VibeIndexer?.Dispose(); } catch { }` (`App.cs:757`) add:

```csharp

            // CAD to BIM: drop the external event, its handler and the pane so
            // nothing can raise a build into an unloading add-in.
            try { CadToBimBuildEvent?.Dispose(); } catch { }
            CadToBimBuildEvent = null;
            CadToBimBuildHandler = null;
            CadToBimPaneHost = null;
```

**Step 3.6 — `App.cs` ribbon panel.** `App.cs:798-800` currently:

```csharp
            RibbonPanel cdePanel = application.CreateRibbonPanel(tabName, "BINA CDE");
            RibbonPanel aiPanel = application.CreateRibbonPanel(tabName, "BINA AI");
            RibbonPanel compliancePanel = application.CreateRibbonPanel(tabName, "Compliance");
```
becomes (panel order on the tab is creation order, so this sits between BINA AI and Compliance):

```csharp
            RibbonPanel cdePanel = application.CreateRibbonPanel(tabName, "BINA CDE");
            RibbonPanel aiPanel = application.CreateRibbonPanel(tabName, "BINA AI");
            // CAD to BIM is neither backend: it runs entirely inside Revit, so it
            // gets its own panel rather than sitting under a sign-in it does not need.
            RibbonPanel cadPanel = application.CreateRibbonPanel(tabName, "CAD to BIM");
            RibbonPanel compliancePanel = application.CreateRibbonPanel(tabName, "Compliance");
```

**Step 3.7 — `App.cs` button data.** After the `askAiButtonData` initialiser's closing `};` (`App.cs:934`), before `// Cost Tracker buttons`, add:

```csharp

            // CAD to BIM: one DWG in, native walls / doors / windows / rooms out,
            // with a preview the drafter corrects first. Zero-doc availability:
            // "Save as new .rvt" needs no open project.
            PushButtonData cadToBimButtonData = new PushButtonData(
                "CadToBim",
                "CAD to\nBIM",
                Assembly.GetExecutingAssembly().Location,
                "RevitWebAppSync.Commands.OpenCadToBimCommand")
            {
                ToolTip = "Build Revit walls, doors, windows and rooms from a DWG",
                LongDescription = "Reads a 2D floor-plan DWG or DXF, shows what it found so you can erase or brush walls, then builds native Revit elements into this project or into a new .rvt saved beside the drawing.",
                Image = LoadIcon("CadToBim", 16),
                LargeImage = LoadIcon("CadToBim", 32),
                AvailabilityClassName = typeof(ZeroDocCommandAvailability).FullName
            };
```

**Step 3.8 — `App.cs` add the button.** `App.cs:1018` `aiPanel.AddItem(askAiButtonData);` becomes:

```csharp
            aiPanel.AddItem(askAiButtonData);
            cadPanel.AddItem(cadToBimButtonData);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~CadToBimRibbonTests"`
Expected: PASS (5 tests).

Compile gate (needs the handler + pane host + panel/VM tasks from the other sections to be in the tree; until then it fails with `CS0246 The type or namespace name 'CadToBimPaneHost' could not be found` and `CS0234 ... 'CadToBim' does not exist in the namespace 'RevitWebAppSync.Services'`):
```bash
cd /Users/ashraf/development/bina/revit-addin-sync
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows
~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows
```
Expected: `Build succeeded.` ×3.

Manual check (the add-in has no ribbon unit tests; this is the Windows smoke gate, Revit 2024 = net48 build and Revit 2025 = net8 build):
1. Start Revit with NO document (Home screen → back out to the empty ribbon). Bina tab shows panels in the order BINA CDE · BINA AI · CAD to BIM · Compliance; "CAD to BIM" panel has one button, two-line label "CAD to / BIM", the sheet+block icon, and it is ENABLED with no document open (ZeroDoc availability). Hover shows the tooltip text above.
2. Click it → file dialog titled "Choose the drawing to convert" with filter "CAD drawings (*.dwg;*.dxf)". Cancel → nothing else happens, the pane stays as it was (opened, empty).
3. Click again, pick a DWG → the "BINA CAD to BIM" pane is docked right and its status line reads "Detecting…" then the counts; Revit stays responsive meanwhile.
4. Open a project, repeat 2–3: identical behaviour.
5. Close Revit: no crash on exit (OnShutdown disposes the event); a second launch registers the pane again with no "duplicate pane id" dialog.

- [ ] **Step 5: Stage**

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git add App.cs Commands/OpenCadToBimCommand.cs Tests/CadToBimRibbonTests.cs
```

---

## Engine test conventions (Tasks 4–9, 14, 15, 16, 17)

- **Tasks 5–9 are characterisation tests of engine code that already exists on `origin/feat/cad2bim`.** They cannot "fail first" for want of an implementation. Their Step 2 is the honest equivalent: run the class filter before the file exists and get `No test matches the given testcase filter` (vstest prints exactly that and exits 0). Task 4's Step 2 is a genuine red: the engine is not linked into `Tests.csproj` until Task 4 Step 3.
- All six engine test files (Tasks 4–9) were executed on this Mac through a throwaway `net10.0` xunit harness that links the same engine files from `origin/feat/cad2bim`: **62 tests, 62 passed, 0 failed**. Every expected number in Tasks 4–9 comes from that run, not from reading the code. The harness recipe: a folder with the engine files copied via `git show origin/feat/cad2bim:Cad2Bim/cad2bim/<file>` into `engine/`, the test files in `tests/`, and a `Probe.csproj` (`net10.0`, `IsTestProject`, `LangVersion latest`, `EnableDefaultCompileItems=false`, packages `Microsoft.NET.Test.Sdk 17.11.1` / `xunit 2.9.2` / `xunit.runner.visualstudio 2.8.2` / `ACadSharp 3.6.51`, `<Compile Include="engine\*.cs" />` + `<Compile Include="tests\*.cs" />`), run with `~/.dotnet/dotnet test Probe.csproj`. Whether to check such a harness in as `Tests/Cad2Bim.Mac.csproj` is a repo-owner decision; it is not part of this plan.
- **Private engine members** (`BridgeGaps`, `MergeCollinear` wall runs, `AssignNames`, `IsDoorSwing`, `Merge`, `FindSwing`, `Across`) are tested only through their public callers (`CreateTopologicalPoints(walls, toleranceMm=150, maxGapMm=2000, mergeGapMm=600)`, `ClassifySpaces(graph, texts, minAreaMm2=1_500_000)`, `ClassifyOpeningsFromSymbols` / `ClassifyOpenings`). **Internal** members (`CadClassifier.IsName`, `SignedArea`, `Contains`, `PointAlong`, `TryIntersect`, `DistanceAlong`, `IsPerpendicular`) are callable directly: the engine is linked as *source* into `Tests.csproj`, so it compiles into the test assembly itself. No `InternalsVisibleTo` exists or is needed anywhere; do not make private members internal for testing.
- **The aspect guard is tested with 250 mm strokes at 45°, 200 mm apart** (long enough for `Wall.MinFaceLength` = 200, shorter than 200 × `MinFaceAspect` 1.5 = 300); a 60 mm stroke pair is rejected by `MinFaceLength`, not the aspect guard, and is a separate test.
- `Units.Resolve` is testable without a DWG: ACadSharp 3.6.51's `new CadDocument()` builds an in-memory document whose `Header.InsUnits` is settable.
- Engine behaviours pinned that the pane/builder tasks rely on: `SplitWalls` counts rooms per *wall object* (a wall spanning two rooms is `IsOutdoor == false`; wall runs merged in the graph are represented by one member); `ClassifySpaces` increments the static `Diagnostics.Dropped/TooSmall/Areas` counters and never resets them (harmless); three parallel faces at 0/200/400 mm give two walls (`MaxFaceUses = 2`) and `DeduplicateWalls` is what stops a later doubling — run it after `ClassifyWalls` in every detect/brush path; `FaceJoinGapMm` 150, `DefaultMergeGapMm` 600 and `DefaultGapBridgeMm` 2000 are three different constants (500 mm apart → ONE run, 900 mm → two runs joined by a bridge edge = doorway, 2500 mm → disconnected); `Units.Resolve` disbelieves a header that puts the plan outside 3 m..1 km.

## Assertion tolerances (shared by Tasks 4–9)

- `Assert.Equal(expected, actual, 6)` (rounds to 1e-6 mm) for every axis-aligned length, midline coordinate and opening position: the engine computes these by projection and averaging of exact inputs, so the only noise is double rounding (~1e-12). Six decimals is tight enough to catch a real regression (a 1 mm drift) and loose enough never to flake.
- `Assert.Equal(expected, actual, 3)` (1e-3) for the 30°-rotated point cloud (a `cos`/`sin` round trip against the engine's own 2°-step angle table) and for mm² areas (16 000 000 via shoelace — exact in principle, but 1e-3 on a 1.6e7 value is 1e-10 relative and reads better than `, 6`).
- Exact `Assert.Equal(double, double)` for `Units.InferScale` / `Units.Resolve`: they return one of five literal table values (1, 10, 25.4, 304.8, 1000), never a computed one.
- `Assert.Single` / `Assert.Empty` / `Assert.Equal(int, count)` for cardinalities; `Assert.Same` for object identity where the engine promises to keep a specific instance (`DeduplicateWalls` keeps the longer wall).

Every test class carries `[Collection("Cad2Bim")]` so the six classes run sequentially: `Wall.SMin/SMax/MinFaceLength/MinFaceAspect/MaxFaceUses` are mutable statics, and the Brush code (Task 15) mutates two of them inside `try/finally`. xunit would otherwise run test classes in parallel.

Fixture geometry convention used throughout: walls are built from two faces at ±thickness/2 so `Wall.Centerline` equals the intended line exactly (verified to 1e-6). Test counts per file: Pairing 10, Topology 9, Spaces 11 (7 facts + 5 theory rows), Openings 10, PlansUnits 12, Outlines 10 → 62.

---

### Task 4: Link the Cad2Bim engine into the Tests project + wall pairing tests

**Files:**
- Modify: `Tests/Tests.csproj:1-9` (PropertyGroup: add `EnableWindowsTargeting`) and `Tests/Tests.csproj:10-24` (PackageReference group: add ACadSharp) and a new `<ItemGroup>` of engine links placed directly after the existing `<Compile Include="..\BinaVibe\Mcp\Tools\ToolManifest.g.cs" ... />` line (~line 33)
- Test: `Tests/Cad2BimPairingTests.cs`

**Interfaces:**
- Consumes: engine sources landed in the working tree by Task 1's merge of `origin/feat/cad2bim` — `Cad2Bim/cad2bim/{Geometry,SpatialIndex,Plans,Topology,Spaces,Openings,Units,Outlines}.cs`; `CadClassifier.ClassifyWalls(IReadOnlyList<Segment>) → List<Wall>`; `CadClassifier.DeduplicateWalls(List<Wall>) → List<Wall>`; `Wall.Thickness`, `Wall.Centerline`.
- Produces: `Tests.csproj` compiling the `Cad2Bim` namespace — every later task (5–9, and Task 15's `Cad2BimBrushTests`, Task 14's `CadToBimSessionTests` and the Task 16/17 pane tests) depends on this csproj edit and must not repeat it.

- [ ] **Step 1: Write the failing test**

`Tests/Cad2BimPairingTests.cs`:

```csharp
// Wall pairing (Cad2Bim/cad2bim/Geometry.cs: CadClassifier.ClassifyWalls, Wall,
// Segment.Midline) and de-duplication (Plans.cs: CadClassifier.DeduplicateWalls).
// Pure doubles, millimetres throughout — the engine normalises every drawing to mm on
// load, so the static thresholds (Wall.SMin 50, SMax 400, MinFaceLength 200,
// MinFaceAspect 1.5, MaxFaceUses 2) are read at their defaults here and never mutated.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimPairingTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        [Fact]
        public void Two_parallel_faces_200_apart_become_one_wall_with_the_midline_between_them()
        {
            var faces = new List<Segment> { S(0, 0, 5000, 0), S(0, 200, 5000, 200) };

            var walls = CadClassifier.ClassifyWalls(faces);

            Wall w = Assert.Single(walls);
            Assert.Equal(200.0, w.Thickness, 6);
            Assert.Equal(100.0, w.Centerline.P1.y, 6);
            Assert.Equal(100.0, w.Centerline.P2.y, 6);
            Assert.Equal(5000.0, w.Centerline.Length, 6);
        }

        [Fact]
        public void Antiparallel_faces_still_pair()
        {
            // Same wall, second face drawn right-to-left.
            var faces = new List<Segment> { S(0, 0, 5000, 0), S(5000, 200, 0, 200) };

            var walls = CadClassifier.ClassifyWalls(faces);

            Wall w = Assert.Single(walls);
            Assert.Equal(200.0, w.Thickness, 6);
            Assert.Equal(100.0, w.Centerline.P1.y, 6);
            Assert.Equal(5000.0, w.Centerline.Length, 6);
        }

        [Fact]
        public void Midline_is_clipped_to_the_overlap_of_the_two_faces()
        {
            var faces = new List<Segment> { S(0, 0, 5000, 0), S(2000, 200, 8000, 200) };

            var walls = CadClassifier.ClassifyWalls(faces);

            Wall w = Assert.Single(walls);
            Assert.Equal(3000.0, w.Centerline.Length, 6);
            Assert.Equal(2000.0, Math.Min(w.Centerline.P1.x, w.Centerline.P2.x), 6);
            Assert.Equal(5000.0, Math.Max(w.Centerline.P1.x, w.Centerline.P2.x), 6);
        }

        [Fact]
        public void Corridor_face_serves_two_walls()
        {
            // Three parallel faces at 0 / 200 / 400: the middle one is the far face of
            // both walls (Wall.MaxFaceUses = 2), so the drawing yields two walls, not one.
            var faces = new List<Segment>
            {
                S(0, 0, 5000, 0), S(0, 200, 5000, 200), S(0, 400, 5000, 400),
            };

            var walls = CadClassifier.ClassifyWalls(faces);

            Assert.Equal(2, walls.Count);
            Assert.All(walls, w => Assert.Equal(200.0, w.Thickness, 6));
            var midlines = walls.Select(w => w.Centerline.P1.y).OrderBy(y => y).ToList();
            Assert.Equal(100.0, midlines[0], 6);
            Assert.Equal(300.0, midlines[1], 6);
        }

        [Fact]
        public void Aspect_guard_rejects_45_degree_hatch_strokes()
        {
            // Two 250 mm strokes at 45°, 200 mm apart: long enough for MinFaceLength (200)
            // but shorter than 200 × MinFaceAspect (1.5) = 300, so they are hatch, not wall.
            double h = 250.0 / Math.Sqrt(2);   // stroke run per axis
            double o = 200.0 / Math.Sqrt(2);   // perpendicular offset per axis
            var strokes = new List<Segment> { S(0, 0, h, h), S(-o, o, h - o, h + o) };

            Assert.Empty(CadClassifier.ClassifyWalls(strokes));
        }

        [Fact]
        public void Aspect_guard_keeps_a_short_return_wall()
        {
            // 350 mm faces 200 mm apart: 350 ≥ 300, so this short return is a wall.
            var faces = new List<Segment> { S(0, 0, 350, 0), S(0, 200, 350, 200) };

            Wall w = Assert.Single(CadClassifier.ClassifyWalls(faces));
            Assert.Equal(200.0, w.Thickness, 6);
        }

        [Fact]
        public void Ticks_shorter_than_MinFaceLength_never_pair()
        {
            var ticks = new List<Segment> { S(0, 0, 60, 0), S(0, 200, 60, 200) };

            Assert.Empty(CadClassifier.ClassifyWalls(ticks));
        }

        [Fact]
        public void Faces_further_apart_than_SMax_do_not_pair()
        {
            var faces = new List<Segment> { S(0, 0, 5000, 0), S(0, 3000, 5000, 3000) };

            Assert.Empty(CadClassifier.ClassifyWalls(faces));
        }

        [Fact]
        public void DeduplicateWalls_keeps_the_longer_of_two_near_identical_walls()
        {
            var longer = new Wall(S(0, 0, 5000, 0), S(0, 200, 5000, 200));
            var shorter = new Wall(S(1000, 0, 4000, 0), S(1000, 200, 4000, 200));

            var kept = CadClassifier.DeduplicateWalls(new List<Wall> { shorter, longer });

            Wall w = Assert.Single(kept);
            Assert.Same(longer, w);
        }

        [Fact]
        public void DeduplicateWalls_keeps_parallel_walls_that_are_not_on_the_same_line()
        {
            // Centrelines 300 mm apart: two real walls (DuplicateOffsetMm is 60).
            var a = new Wall(S(0, 0, 5000, 0), S(0, 200, 5000, 200));
            var b = new Wall(S(0, 300, 5000, 300), S(0, 500, 5000, 500));

            var kept = CadClassifier.DeduplicateWalls(new List<Wall> { a, b });

            Assert.Equal(2, kept.Count);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (Mac compile gate): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true`
Expected: FAIL — `Tests/Cad2BimPairingTests.cs(10,7): error CS0246: The type or namespace name 'Cad2Bim' could not be found` (the engine is not linked yet).

- [ ] **Step 3: Write minimal implementation** — the csproj edit (the engine itself is already on the branch). This is the ONLY place in the plan that touches the engine links, the ACadSharp package or the `<Using>` globals in `Tests/Tests.csproj`; later tasks append only their own `Services/CadToBim` / `UI/CadToBim` links under the marker comment below.

In `Tests/Tests.csproj`, inside the first `<PropertyGroup>` (after `<IsTestProject>true</IsTestProject>`):

```xml
    <!-- Same as RevitWebAppSync.csproj:35 — lets the macOS dev box compile this
         net10.0-windows project as a gate. Tests still only RUN on Windows / CI. -->
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
```

In the first `<ItemGroup>` of `<PackageReference>`s (after the PdfPig line):

```xml
    <!-- ACadSharp: Geometry.cs (CadLoader), Units.cs (FromHeader/Resolve), ModelSource.cs
         and Services/CadRenderSource.cs take an ACadSharp CadDocument. Same version as
         RevitWebAppSync.csproj. -->
    <PackageReference Include="ACadSharp" Version="3.6.51" />
```

A new `<ItemGroup>` directly after the closing `</PropertyGroup>` of the first property group (before the package group):

```xml
  <!-- The Cad2Bim engine + viewport files were written under ImplicitUsings (their own
       cad2bim.csproj). Task 1 gave the linked sources explicit usings; these globals are the
       belt to those braces so a later upstream sync of the folder cannot break this project. -->
  <ItemGroup>
    <Using Include="System" />
    <Using Include="System.Collections.Generic" />
    <Using Include="System.Linq" />
  </ItemGroup>
```

A new `<ItemGroup>` directly after the `ToolManifest.g.cs` link line (inside the existing Compile group is also fine, but a separate group keeps the marker findable):

```xml
  <!-- Cad2Bim engine (CAD to BIM pane): pure geometry, System + Linq + ACadSharp + plain WPF,
       no Revit. Linked the same way RevitWebAppSync.csproj links them. Everything the pane
       tests reach — the classifier files (Tasks 4-9), ModelSource/ClassificationService/
       CadRenderSource (LayerFilter, CadModel, the DWG read: Tasks 13, 15, 17), the view-model
       base + LayerViewModel + shapes + CadViewport (the overlay wrapper and the pane VM:
       Tasks 16, 17). Internal members are reachable from the tests because the source compiles
       INTO this assembly; no InternalsVisibleTo needed. AssemblyInfo.cs (WPF ThemeInfo),
       IfcExporter.cs, RelayCommand/SettingsViewModel/NumericScrubBox and the viewer's own
       MainViewModel/MainWindow/App.xaml/Themes stay out. -->
  <ItemGroup>
    <Compile Include="..\Cad2Bim\cad2bim\Geometry.cs" Link="Cad2Bim\Geometry.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\SpatialIndex.cs" Link="Cad2Bim\SpatialIndex.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\Plans.cs" Link="Cad2Bim\Plans.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\Topology.cs" Link="Cad2Bim\Topology.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\Spaces.cs" Link="Cad2Bim\Spaces.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\Openings.cs" Link="Cad2Bim\Openings.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\Units.cs" Link="Cad2Bim\Units.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\Outlines.cs" Link="Cad2Bim\Outlines.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\ModelSource.cs" Link="Cad2Bim\ModelSource.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\Services\CadRenderSource.cs" Link="Cad2Bim\Services\CadRenderSource.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\Services\ClassificationService.cs" Link="Cad2Bim\Services\ClassificationService.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\ViewModels\ViewModelBase.cs" Link="Cad2Bim\ViewModels\ViewModelBase.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\ViewModels\LayerViewModel.cs" Link="Cad2Bim\ViewModels\LayerViewModel.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\ViewModels\Shapes\ArcShape.cs" Link="Cad2Bim\ViewModels\Shapes\ArcShape.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\ViewModels\Shapes\PolylineShape.cs" Link="Cad2Bim\ViewModels\Shapes\PolylineShape.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\ViewModels\Shapes\SegmentShape.cs" Link="Cad2Bim\ViewModels\Shapes\SegmentShape.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\ViewModels\Shapes\WallShape.cs" Link="Cad2Bim\ViewModels\Shapes\WallShape.cs" />
    <Compile Include="..\Cad2Bim\cad2bim\Views\Rendering\CadViewport.cs" Link="Cad2Bim\Views\Rendering\CadViewport.cs" />
    <!-- CAD to BIM pane sources (Revit-free) — later tasks append here -->
  </ItemGroup>
```

Every `Include` path exists on the branch (`git ls-tree -r --name-only origin/feat/cad2bim -- Cad2Bim`); Task 1 has already given these files the explicit usings and the net48-neutral call sites they need, so the same sources compile here unchanged.

- [ ] **Step 4: Run test to verify it passes**

Run (Mac compile gate): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true`
Expected: `Build succeeded.` with 36× `warning CS8632` from the linked engine (nullable annotations in a project without `<Nullable>`; harmless, see Notes) and 0 errors.

Run (Windows rig or CI): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimPairingTests"`
Expected: PASS — `Passed! - Failed: 0, Passed: 10` (verified 10/10 on the Mac harness).

- [ ] **Step 5: Stage**

`git add Tests/Tests.csproj Tests/Cad2BimPairingTests.cs`   (repo owner rule: executors STAGE, never commit)

---

### Task 5: Topology tests — face joining, wall graph, gap bridging

**Files:**
- Test: `Tests/Cad2BimTopologyTests.cs`

**Interfaces:**
- Consumes: Task 4's csproj links; `CadClassifier.MergeCollinearSegments(IReadOnlyList<Segment>, double gapMm = 150) → List<Segment>`; `CadClassifier.CreateTopologicalPoints(List<Wall>, double toleranceMm = 150, double maxGapMm = 2000, double mergeGapMm = 600) → WallGraph`; `WallGraph.Nodes/Edges/Members`; `TopologicalPoint.Position/Degree`; `WallEdge.A/B`.
- Produces: the `W(x1,y1,x2,y2,thickness)` wall factory (builds two faces at ±thickness/2 so `Wall.Centerline` is exactly the given line) — repeated verbatim in Tasks 6 and 8 and reusable by Tasks 14/15's Brush/Session tests.

- [ ] **Step 1: Write the failing test**

`Tests/Cad2BimTopologyTests.cs`:

```csharp
// Face joining (Plans.cs: CadClassifier.MergeCollinearSegments) and the wall graph
// (Topology.cs: CreateTopologicalPoints, which runs the private MergeCollinear and
// BridgeGaps). BridgeGaps and MergeCollinear are private, so they are exercised through
// CreateTopologicalPoints with its maxGapMm / mergeGapMm parameters. Millimetres.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimTopologyTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        /// <summary>A wall whose centreline is exactly (x1,y1)→(x2,y2): two faces offset
        /// ±thickness/2 along the perpendicular.</summary>
        private static Wall W(double x1, double y1, double x2, double y2, double thickness = 200)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            double nx = -dy / len * thickness / 2, ny = dx / len * thickness / 2;
            return new Wall(S(x1 + nx, y1 + ny, x2 + nx, y2 + ny),
                            S(x1 - nx, y1 - ny, x2 - nx, y2 - ny));
        }

        private static int NodeAt(WallGraph g, double x, double y) =>
            g.Nodes.FindIndex(n => Math.Abs(n.Position.x - x) < 1e-6 && Math.Abs(n.Position.y - y) < 1e-6);

        private static bool HasEdge(WallGraph g, int a, int b) =>
            g.Edges.Any(e => (e.A == a && e.B == b) || (e.A == b && e.B == a));

        // ── MergeCollinearSegments ───────────────────────────────────────

        [Fact]
        public void MergeCollinearSegments_joins_two_pieces_across_a_100mm_gap()
        {
            var pieces = new List<Segment> { S(0, 0, 2000, 0), S(2100, 0, 5000, 0) };

            var faces = CadClassifier.MergeCollinearSegments(pieces);

            Segment f = Assert.Single(faces);
            Assert.Equal(5000.0, f.Length, 6);
            Assert.Equal(0.0, Math.Min(f.P1.x, f.P2.x), 6);
            Assert.Equal(5000.0, Math.Max(f.P1.x, f.P2.x), 6);
        }

        [Fact]
        public void MergeCollinearSegments_joins_left_to_right_with_right_to_left()
        {
            var pieces = new List<Segment> { S(0, 0, 2000, 0), S(5000, 0, 2100, 0) };

            Segment f = Assert.Single(CadClassifier.MergeCollinearSegments(pieces));
            Assert.Equal(5000.0, f.Length, 6);
        }

        [Fact]
        public void MergeCollinearSegments_leaves_a_gap_wider_than_150mm_alone()
        {
            var pieces = new List<Segment> { S(0, 0, 2000, 0), S(2300, 0, 5000, 0) };

            Assert.Equal(2, CadClassifier.MergeCollinearSegments(pieces).Count);
        }

        [Fact]
        public void MergeCollinearSegments_does_not_join_two_faces_of_a_thin_wall()
        {
            // Parallel, 100 mm apart across the line: two faces, not one face in pieces.
            var pieces = new List<Segment> { S(0, 0, 2000, 0), S(2100, 100, 5000, 100) };

            Assert.Equal(2, CadClassifier.MergeCollinearSegments(pieces).Count);
        }

        // ── CreateTopologicalPoints ──────────────────────────────────────

        [Fact]
        public void T_junction_yields_four_nodes_and_three_edges()
        {
            var walls = new List<Wall> { W(0, 0, 6000, 0), W(3000, 0, 3000, 4000) };

            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);

            Assert.Equal(4, g.Nodes.Count);
            Assert.Equal(3, g.Edges.Count);
            int junction = NodeAt(g, 3000, 0);
            Assert.True(junction >= 0, "junction node missing");
            Assert.Equal(3, g.Nodes[junction].Degree);
        }

        [Fact]
        public void Collinear_walls_500mm_apart_merge_into_one_run()
        {
            // 500 ≤ DefaultMergeGapMm (600): drafting slop, one wall.
            var walls = new List<Wall> { W(0, 0, 3000, 0), W(3500, 0, 7000, 0) };

            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);

            Assert.Equal(2, g.Nodes.Count);
            Assert.Single(g.Edges);
            Assert.Equal(2, g.Members[walls[0]].Count);
        }

        [Fact]
        public void BridgeGaps_joins_facing_stubs_900mm_apart()
        {
            // 900 > merge gap (600) so they stay two runs; 900 ≤ bridge gap (2000) and the
            // loose ends face each other, so a bridging edge closes the doorway.
            var walls = new List<Wall> { W(0, 0, 3000, 0), W(3900, 0, 7000, 0) };

            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);

            Assert.Equal(4, g.Nodes.Count);
            Assert.Equal(3, g.Edges.Count);
            int left = NodeAt(g, 3000, 0), right = NodeAt(g, 3900, 0);
            Assert.True(HasEdge(g, left, right), "bridge edge missing");
            Assert.Equal(2, g.Nodes[left].Degree);
            Assert.Equal(2, g.Nodes[right].Degree);
        }

        [Fact]
        public void BridgeGaps_does_not_bridge_2500mm()
        {
            var walls = new List<Wall> { W(0, 0, 3000, 0), W(5500, 0, 8500, 0) };

            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);

            Assert.Equal(4, g.Nodes.Count);
            Assert.Equal(2, g.Edges.Count);
            Assert.All(g.Nodes, n => Assert.Equal(1, n.Degree));
        }

        [Fact]
        public void BridgeGaps_does_not_bridge_divergent_stubs()
        {
            // Second stub starts 900 mm ahead but heads off at 30°: cos 30° = 0.866 is
            // under the collinearity floor (cos 25° = 0.906), so no bridge.
            double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
            var walls = new List<Wall>
            {
                W(0, 0, 3000, 0),
                W(3900, 0, 3900 + (3000 * c), 3000 * s),
            };

            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);

            Assert.Equal(4, g.Nodes.Count);
            Assert.Equal(2, g.Edges.Count);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (Windows rig or CI, before the file exists): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimTopologyTests"`
Expected: `No test matches the given testcase filter \`FullyQualifiedName~Cad2BimTopologyTests\`` — the class does not exist yet (engine code under test already does; see Contract correction 2).

- [ ] **Step 3: Write minimal implementation** — none: the engine is on the branch. Prove the linked source is the branch's, unmodified:

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git diff --stat origin/feat/cad2bim -- Cad2Bim/cad2bim/Topology.cs Cad2Bim/cad2bim/Plans.cs
```
Expected: empty output (no diff).

- [ ] **Step 4: Run test to verify it passes**

Run (Mac compile gate): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true`   Expected: `Build succeeded.`
Run (Windows rig or CI): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimTopologyTests"`
Expected: PASS — `Passed! - Failed: 0, Passed: 9` (verified 9/9 on the Mac harness). Exact numbers pinned: T-junction → 4 nodes / 3 edges / junction Degree 3; collinear walls 500 mm apart → 2 nodes / 1 edge / `Members[first]` has 2 walls; 900 mm facing stubs → 4 nodes / 3 edges, bridged ends Degree 2; 2500 mm → 4 / 2, all Degree 1; 30° divergent → 4 / 2.

- [ ] **Step 5: Stage**

`git add Tests/Cad2BimTopologyTests.cs`

---

### Task 6: Spaces tests — rooms, sliver floor, naming, outdoor split

**Files:**
- Test: `Tests/Cad2BimSpacesTests.cs`

**Interfaces:**
- Consumes: Task 4's csproj links; `CadClassifier.ClassifySpaces(WallGraph, List<TextElement>, double minAreaMm2 = 1_500_000) → List<Space>`; `CadClassifier.SplitWalls(List<Wall>, List<Space>)`; `internal static bool CadClassifier.IsName(string)`; `Space.Area/Boundary/Name`; `Wall.IsOutdoor`.
- Produces: `Square(size, ox, oy)` four-wall factory (bottom, right, top, left) and `T(text, x, y)` label factory — repeated verbatim in Task 8.

- [ ] **Step 1: Write the failing test**

`Tests/Cad2BimSpacesTests.cs`:

```csharp
// Rooms (Spaces.cs: CadClassifier.ClassifySpaces, SplitWalls, IsName). AssignNames is
// private and runs at the end of ClassifySpaces, so naming is asserted through the
// Space.Name a classified room comes back with. IsName is internal — reachable because
// the engine source is compiled into this assembly. Millimetres, mm² for areas.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimSpacesTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        private static Wall W(double x1, double y1, double x2, double y2, double thickness = 200)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            double nx = -dy / len * thickness / 2, ny = dx / len * thickness / 2;
            return new Wall(S(x1 + nx, y1 + ny, x2 + nx, y2 + ny),
                            S(x1 - nx, y1 - ny, x2 - nx, y2 - ny));
        }

        /// <summary>Four walls: bottom, right, top, left.</summary>
        private static List<Wall> Square(double size, double ox = 0, double oy = 0) => new()
        {
            W(ox, oy, ox + size, oy),
            W(ox + size, oy, ox + size, oy + size),
            W(ox + size, oy + size, ox, oy + size),
            W(ox, oy + size, ox, oy),
        };

        private static TextElement T(string text, double x, double y) => new()
        {
            P1 = new Point(x, y), P2 = new Point(x + 400, y + 100), Text = text,
        };

        private static readonly List<TextElement> NoText = new();

        [Fact]
        public void Four_walls_in_a_4m_square_make_one_room_of_16m2()
        {
            WallGraph g = CadClassifier.CreateTopologicalPoints(Square(4000));

            var spaces = CadClassifier.ClassifySpaces(g, NoText);

            Space room = Assert.Single(spaces);
            Assert.Equal(16_000_000.0, room.Area, 3);
            Assert.Equal(4, room.Boundary.Count);
            Assert.Null(room.Name);
        }

        [Fact]
        public void A_1m_square_is_dropped_as_a_sliver()
        {
            WallGraph g = CadClassifier.CreateTopologicalPoints(Square(1000));

            Assert.Empty(CadClassifier.ClassifySpaces(g, NoText));
            // It is a closed loop — only the 1.5 m² floor (MinSpaceAreaMm2) removes it.
            Space small = Assert.Single(CadClassifier.ClassifySpaces(g, NoText, minAreaMm2: 0));
            Assert.Equal(1_000_000.0, small.Area, 3);
        }

        [Fact]
        public void Room_takes_the_label_that_is_a_name_not_the_area_annotation()
        {
            WallGraph g = CadClassifier.CreateTopologicalPoints(Square(4000));
            var texts = new List<TextElement> { T("17.79MP", 1000, 1000), T("BILIK 1", 2000, 2000) };

            Space room = Assert.Single(CadClassifier.ClassifySpaces(g, texts));

            Assert.Equal("BILIK 1", room.Name);
        }

        [Fact]
        public void Label_outside_every_room_names_nothing()
        {
            WallGraph g = CadClassifier.CreateTopologicalPoints(Square(4000));
            var texts = new List<TextElement> { T("DAPUR", 9000, 9000) };

            Space room = Assert.Single(CadClassifier.ClassifySpaces(g, texts));

            Assert.Null(room.Name);
        }

        [Theory]
        [InlineData("BILIK 1", true)]
        [InlineData("DAPUR", true)]
        [InlineData("17.79MP", false)]
        [InlineData("D1", false)]
        [InlineData("A", false)]
        public void IsName_wants_letters_outnumbering_digits(string text, bool expected)
        {
            Assert.Equal(expected, CadClassifier.IsName(text));
        }

        [Fact]
        public void SplitWalls_marks_the_walls_of_a_lone_room_as_outdoor()
        {
            List<Wall> walls = Square(4000);
            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);
            var spaces = CadClassifier.ClassifySpaces(g, NoText);

            CadClassifier.SplitWalls(walls, spaces);

            Assert.All(walls, w => Assert.True(w.IsOutdoor));
        }

        [Fact]
        public void SplitWalls_marks_a_wall_with_a_room_on_each_side_as_internal()
        {
            // 8 m × 4 m envelope split down the middle into two 4 m rooms.
            Wall bottom = W(0, 0, 8000, 0), right = W(8000, 0, 8000, 4000);
            Wall top = W(8000, 4000, 0, 4000), left = W(0, 4000, 0, 0);
            Wall middle = W(4000, 0, 4000, 4000);
            var walls = new List<Wall> { bottom, right, top, left, middle };
            WallGraph g = CadClassifier.CreateTopologicalPoints(walls);
            var spaces = CadClassifier.ClassifySpaces(g, NoText);
            Assert.Equal(2, spaces.Count);
            Assert.All(spaces, s => Assert.Equal(16_000_000.0, s.Area, 3));

            CadClassifier.SplitWalls(walls, spaces);

            Assert.False(middle.IsOutdoor);
            Assert.True(right.IsOutdoor);
            Assert.True(left.IsOutdoor);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (Windows rig or CI, before the file exists): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimSpacesTests"`
Expected: `No test matches the given testcase filter \`FullyQualifiedName~Cad2BimSpacesTests\``.

- [ ] **Step 3: Write minimal implementation** — none: the engine is on the branch. Prove the linked source is unmodified:

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git diff --stat origin/feat/cad2bim -- Cad2Bim/cad2bim/Spaces.cs
```
Expected: empty output.

- [ ] **Step 4: Run test to verify it passes**

Run (Mac compile gate): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true`   Expected: `Build succeeded.`
Run (Windows rig or CI): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimSpacesTests"`
Expected: PASS — `Passed! - Failed: 0, Passed: 11` (7 facts + 5 theory rows; verified 11/11 on the Mac harness). Pinned: 4 m square → 1 space, Area 16 000 000, Boundary 4 points, Name null; 1 m square → 0 spaces at the default floor, 1 space of 1 000 000 at `minAreaMm2: 0`; "BILIK 1" wins over "17.79MP"; two 4 m rooms → middle wall `IsOutdoor == false`, end walls true.

- [ ] **Step 5: Stage**

`git add Tests/Cad2BimSpacesTests.cs`

---

### Task 7: Openings tests — door swings, window marks, merge, jamb pairs

**Files:**
- Test: `Tests/Cad2BimOpeningsTests.cs`

**Interfaces:**
- Consumes: Task 4's csproj links; `CadClassifier.ClassifyOpeningsFromSymbols(List<Wall>, IReadOnlyList<Arc> swings, IReadOnlyList<Segment> windowLines) → List<Opening>`; `CadClassifier.ClassifyOpenings(List<Wall>, IReadOnlyList<Segment>, IReadOnlyList<Arc>) → List<Opening>`; `Opening.IsDoor/Width/Position/Wall`; `Arc` init properties.
- Produces: `Swing(cx, cy, radius, sweepDegrees)` arc factory and `Mark(x)` window-mark factory — reusable by Tasks 14/15's Session tests when they need an opening on an erased wall.

- [ ] **Step 1: Write the failing test**

`Tests/Cad2BimOpeningsTests.cs`:

```csharp
// Doors and windows (Openings.cs: CadClassifier.ClassifyOpeningsFromSymbols and
// ClassifyOpenings). IsDoorSwing and Merge are private and run inside
// ClassifyOpeningsFromSymbols, so swing acceptance and door-over-window merging are
// asserted through what that method returns. One 5 m wall along the X axis, 200 mm
// thick, hosts everything. Millimetres; arc angles in radians as DWG stores them.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimOpeningsTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        /// <summary>Centreline (0,0)→(5000,0), faces at y = ±100.</summary>
        private static Wall HostWall() => new Wall(S(0, 100, 5000, 100), S(0, -100, 5000, -100));

        private static Arc Swing(double cx, double cy, double radius, double sweepDegrees = 90) => new()
        {
            Center = new Point(cx, cy), Radius = radius,
            StartAngle = 0, EndAngle = sweepDegrees * Math.PI / 180.0,
        };

        /// <summary>A window mark: a short line crossing the wall at x.</summary>
        private static Segment Mark(double x) => S(x, -150, x, 150);

        private static readonly List<Arc> NoArcs = new();
        private static readonly List<Segment> NoLines = new();

        [Fact]
        public void Quarter_swing_of_800mm_radius_near_a_wall_is_a_door_800mm_wide()
        {
            Wall wall = HostWall();
            var swings = new List<Arc> { Swing(2000, 100, 800) };

            var openings = CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { wall }, swings, NoLines);

            Opening door = Assert.Single(openings);
            Assert.True(door.IsDoor);
            Assert.Same(wall, door.Wall);
            Assert.Equal(800.0, door.Width, 6);
            Assert.Equal(2000.0, door.Position.x, 6);
            Assert.Equal(0.0, door.Position.y, 6);
        }

        [Fact]
        public void Swing_of_300mm_radius_is_not_a_door()
        {
            var swings = new List<Arc> { Swing(2000, 100, 300) };

            Assert.Empty(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, swings, NoLines));
        }

        [Fact]
        public void Half_circle_sweep_is_not_a_door()
        {
            var swings = new List<Arc> { Swing(2000, 100, 800, sweepDegrees: 180) };

            Assert.Empty(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, swings, NoLines));
        }

        [Fact]
        public void Swing_far_from_any_wall_finds_no_host()
        {
            // Hinge 2 m off the centreline: past DoorHostReachMm (400) and 2 × thickness.
            var swings = new List<Arc> { Swing(2000, 2000, 800) };

            Assert.Empty(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, swings, NoLines));
        }

        [Fact]
        public void Window_marks_are_grouped_along_the_wall_and_split_at_gaps_over_1200mm()
        {
            // Marks 1000/1400/1800 (one window), then a 1300 mm gap, then 3100/3500/3900.
            var marks = new List<Segment>
            {
                Mark(1000), Mark(1400), Mark(1800), Mark(3100), Mark(3500), Mark(3900),
            };

            var openings = CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, NoArcs, marks)
                .OrderBy(o => o.Position.x).ToList();

            Assert.Equal(2, openings.Count);
            Assert.All(openings, o => Assert.False(o.IsDoor));
            Assert.All(openings, o => Assert.Equal(800.0, o.Width, 6));
            Assert.Equal(1400.0, openings[0].Position.x, 6);
            Assert.Equal(3500.0, openings[1].Position.x, 6);
        }

        [Fact]
        public void Window_marks_within_1200mm_stay_one_window()
        {
            var marks = new List<Segment> { Mark(1000), Mark(1800), Mark(2600) };

            Opening window = Assert.Single(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, NoArcs, marks));
            Assert.Equal(1600.0, window.Width, 6);
        }

        [Fact]
        public void Window_narrower_than_600mm_is_frame_detail_not_an_opening()
        {
            var marks = new List<Segment> { Mark(1000), Mark(1400) };

            Assert.Empty(CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, NoArcs, marks));
        }

        [Fact]
        public void Merge_prefers_the_door_when_a_window_group_overlaps_it()
        {
            // Door spans 1600..2400; window marks span exactly the same stretch.
            var swings = new List<Arc> { Swing(2000, 100, 800) };
            var marks = new List<Segment> { Mark(1600), Mark(1800), Mark(2000), Mark(2200), Mark(2400) };

            var openings = CadClassifier.ClassifyOpeningsFromSymbols(new List<Wall> { HostWall() }, swings, marks);

            Opening kept = Assert.Single(openings);
            Assert.True(kept.IsDoor);
            Assert.Equal(800.0, kept.Width, 6);
        }

        [Fact]
        public void Jamb_pair_with_a_swing_hinged_at_a_jamb_reads_as_a_door()
        {
            // The jamb-based reader: two 200 mm lines across the wall 800 mm apart, and a
            // swing whose centre sits on the first jamb.
            Wall wall = HostWall();
            var segments = new List<Segment>
            {
                wall.Geometry[0] as Segment, wall.Geometry[1] as Segment,
                S(1600, -100, 1600, 100), S(2400, -100, 2400, 100),
            };
            var arcs = new List<Arc> { Swing(1600, 0, 800) };

            var openings = CadClassifier.ClassifyOpenings(new List<Wall> { wall }, segments, arcs);

            Opening door = Assert.Single(openings);
            Assert.True(door.IsDoor);
            Assert.Equal(800.0, door.Width, 6);
            Assert.Equal(2000.0, door.Position.x, 6);
        }

        [Fact]
        public void Jamb_pair_without_a_swing_is_a_plain_opening()
        {
            Wall wall = HostWall();
            var segments = new List<Segment>
            {
                wall.Geometry[0] as Segment, wall.Geometry[1] as Segment,
                S(1600, -100, 1600, 100), S(2400, -100, 2400, 100),
            };

            Opening opening = Assert.Single(CadClassifier.ClassifyOpenings(new List<Wall> { wall }, segments, NoArcs));
            Assert.False(opening.IsDoor);
            Assert.Equal(800.0, opening.Width, 6);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (Windows rig or CI, before the file exists): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimOpeningsTests"`
Expected: `No test matches the given testcase filter \`FullyQualifiedName~Cad2BimOpeningsTests\``.

- [ ] **Step 3: Write minimal implementation** — none: the engine is on the branch. Prove the linked source is unmodified:

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git diff --stat origin/feat/cad2bim -- Cad2Bim/cad2bim/Openings.cs
```
Expected: empty output.

- [ ] **Step 4: Run test to verify it passes**

Run (Mac compile gate): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true`   Expected: `Build succeeded.`
Run (Windows rig or CI): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimOpeningsTests"`
Expected: PASS — `Passed! - Failed: 0, Passed: 10` (verified 10/10 on the Mac harness). Pinned: r 800 / 90° swing at (2000,100) → one door, Width 800, Position (2000, 0); r 300 → none; 180° sweep → none; hinge 2 m off the wall → none; marks 1000/1400/1800 + 3100/3500/3900 → two 800 mm windows centred at x = 1400 and 3500; marks 1000/1800/2600 → one 1600 mm window; marks 1000/1400 → none (400 < 600); door + coincident window marks → one opening, `IsDoor`.

- [ ] **Step 5: Stage**

`git add Tests/Cad2BimOpeningsTests.cs`

---

### Task 8: Plans + Units tests — clustering, scale inference, header sanity, normalize

**Files:**
- Test: `Tests/Cad2BimPlansUnitsTests.cs`

**Interfaces:**
- Consumes: Task 4's csproj links (incl. the ACadSharp package: `ACadSharp.CadDocument`, `ACadSharp.Types.Units.UnitsType`); `CadClassifier.ClusterPlans(List<Wall>, IReadOnlyList<TextElement>? texts = null, double gapMm = 5000) → List<PlanCluster>`; `Units.InferScale(IReadOnlyList<Segment>) → double`; `Units.Resolve(CadDocument, IReadOnlyList<Segment>) → double`; `Units.Normalize(IReadOnlyList<GeometryElement>, IReadOnlyList<TextElement>, double scale) → (List<GeometryElement>, List<TextElement>)`.
- Produces: `DocWithUnits(UnitsType)` — the one-line way to fake a DWG header for any later header-dependent test.

- [ ] **Step 1: Write the failing test**

`Tests/Cad2BimPlansUnitsTests.cs`:

```csharp
// Plan clustering (Plans.cs: CadClassifier.ClusterPlans) and unit handling (Units.cs:
// Units.InferScale, Units.Resolve, Units.Normalize). Resolve takes an ACadSharp
// CadDocument for its header; a bare `new CadDocument()` (no file) is enough to set
// Header.InsUnits, so the lying-header rule is testable without a DWG on disk.

using System;
using System.Collections.Generic;
using System.Linq;
using ACadSharp;
using ACadSharp.Types.Units;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimPlansUnitsTests
    {
        private static Segment S(double x1, double y1, double x2, double y2) =>
            new Segment(new Point(x1, y1), new Point(x2, y2));

        private static Wall W(double x1, double y1, double x2, double y2, double thickness = 200)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            double nx = -dy / len * thickness / 2, ny = dx / len * thickness / 2;
            return new Wall(S(x1 + nx, y1 + ny, x2 + nx, y2 + ny),
                            S(x1 - nx, y1 - ny, x2 - nx, y2 - ny));
        }

        private static List<Wall> Square(double size, double ox = 0, double oy = 0) => new()
        {
            W(ox, oy, ox + size, oy),
            W(ox + size, oy, ox + size, oy + size),
            W(ox + size, oy + size, ox, oy + size),
            W(ox, oy + size, ox, oy),
        };

        private static TextElement T(string text, double x, double y) => new()
        {
            P1 = new Point(x, y), P2 = new Point(x + 400, y + 100), Text = text,
        };

        private static CadDocument DocWithUnits(UnitsType units)
        {
            var doc = new CadDocument();
            doc.Header.InsUnits = units;
            return doc;
        }

        // ── ClusterPlans ─────────────────────────────────────────────────

        [Fact]
        public void Two_squares_8m_apart_are_two_clusters_ordered_by_area_descending()
        {
            // 4 m square at the origin; 6 m square starting at x = 12 000 (gap 8 000 > 5 000).
            var walls = Square(4000).Concat(Square(6000, ox: 12000)).ToList();

            var clusters = CadClassifier.ClusterPlans(walls);

            Assert.Equal(2, clusters.Count);
            Assert.Equal(36_000_000.0, clusters[0].Area, 3);
            Assert.Equal(16_000_000.0, clusters[1].Area, 3);
            Assert.Equal(4, clusters[0].Walls.Count);
            Assert.Equal(4, clusters[1].Walls.Count);
        }

        [Fact]
        public void Squares_4m_apart_stay_one_plan()
        {
            var walls = Square(4000).Concat(Square(6000, ox: 8000)).ToList();

            PlanCluster only = Assert.Single(CadClassifier.ClusterPlans(walls));
            Assert.Equal(8, only.Walls.Count);
        }

        [Fact]
        public void Labels_land_on_the_plan_that_contains_them()
        {
            var walls = Square(4000).Concat(Square(6000, ox: 12000)).ToList();
            var texts = new List<TextElement> { T("TINGKAT 1", 14000, 3000), T("TINGKAT BAWAH", 2000, 2000) };

            var clusters = CadClassifier.ClusterPlans(walls, texts);

            Assert.Equal("TINGKAT 1", Assert.Single(clusters[0].Texts).Text);
            Assert.Equal("TINGKAT BAWAH", Assert.Single(clusters[1].Texts).Text);
        }

        // ── Units.InferScale ─────────────────────────────────────────────

        [Fact]
        public void InferScale_reads_a_30000_unit_diagonal_as_millimetres()
        {
            Assert.Equal(1.0, Units.InferScale(new[] { S(0, 0, 18000, 24000) }));
        }

        [Fact]
        public void InferScale_reads_a_30_unit_diagonal_as_metres()
        {
            Assert.Equal(1000.0, Units.InferScale(new[] { S(0, 0, 18, 24) }));
        }

        [Fact]
        public void InferScale_reads_a_1181_unit_diagonal_as_inches()
        {
            Assert.Equal(25.4, Units.InferScale(new[] { S(0, 0, 708.6, 944.8) }));
        }

        [Fact]
        public void InferScale_reads_a_98_unit_diagonal_as_feet()
        {
            Assert.Equal(304.8, Units.InferScale(new[] { S(0, 0, 59.04, 78.72) }));
        }

        [Fact]
        public void InferScale_of_nothing_is_millimetres()
        {
            Assert.Equal(1.0, Units.InferScale(new List<Segment>()));
        }

        // ── Units.Resolve ────────────────────────────────────────────────

        [Fact]
        public void Resolve_believes_an_honest_header()
        {
            // Metres, 30-unit diagonal → 30 m: a building.
            var segments = new[] { S(0, 0, 18, 24) };

            Assert.Equal(1000.0, Units.Resolve(DocWithUnits(UnitsType.Meters), segments));
        }

        [Fact]
        public void Resolve_disbelieves_a_header_that_makes_the_plan_2km_wide()
        {
            // Millimetre drawing (90 m diagonal) declaring inches → 2.3 km, outside the
            // 3 m..1 km band, so the scale is inferred from the geometry instead.
            var segments = new[] { S(0, 0, 54000, 72000) };

            Assert.Equal(1.0, Units.Resolve(DocWithUnits(UnitsType.Inches), segments));
        }

        [Fact]
        public void Resolve_infers_when_the_header_is_unitless()
        {
            var segments = new[] { S(0, 0, 18, 24) };

            Assert.Equal(1000.0, Units.Resolve(DocWithUnits(UnitsType.Unitless), segments));
        }

        // ── Units.Normalize ──────────────────────────────────────────────

        [Fact]
        public void Normalize_scales_segments_arcs_and_text_into_millimetres()
        {
            var geometry = new List<GeometryElement>
            {
                S(0, 0, 10, 0),
                new Arc { Center = new Point(1, 1), Radius = 2, StartAngle = 0, EndAngle = 1 },
            };
            var text = new List<TextElement> { T("BILIK", 3, 4) };

            var (scaledGeometry, scaledText) = Units.Normalize(geometry, text, 25.4);

            var segment = Assert.IsType<Segment>(scaledGeometry[0]);
            Assert.Equal(254.0, segment.Length, 6);
            var arc = Assert.IsType<Arc>(scaledGeometry[1]);
            Assert.Equal(50.8, arc.Radius, 6);
            Assert.Equal(25.4, arc.Center.x, 6);
            Assert.Equal(1.0, arc.EndAngle, 6);   // angles are not lengths
            Assert.Equal(76.2, scaledText[0].P1.x, 6);
            Assert.Equal("BILIK", scaledText[0].Text);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (Windows rig or CI, before the file exists): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimPlansUnitsTests"`
Expected: `No test matches the given testcase filter \`FullyQualifiedName~Cad2BimPlansUnitsTests\``.

- [ ] **Step 3: Write minimal implementation** — none: the engine is on the branch. Prove the linked source is unmodified:

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git diff --stat origin/feat/cad2bim -- Cad2Bim/cad2bim/Units.cs Cad2Bim/cad2bim/Plans.cs
```
Expected: empty output.

- [ ] **Step 4: Run test to verify it passes**

Run (Mac compile gate): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true`   Expected: `Build succeeded.`
Run (Windows rig or CI): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimPlansUnitsTests"`
Expected: PASS — `Passed! - Failed: 0, Passed: 12` (verified 12/12 on the Mac harness). Pinned: 4 m + 6 m squares 8 m apart → 2 clusters, Areas 36 000 000 then 16 000 000, 4 walls each; 4 m apart → 1 cluster of 8 walls; InferScale 30 000 → 1.0, 30 → 1000, 1181 → 25.4, 98.4 → 304.8, empty → 1.0; Resolve: Meters + 30 units → 1000, Inches + 90 000 units → 1.0 (header disbelieved), Unitless + 30 units → 1000.

- [ ] **Step 5: Stage**

`git add Tests/Cad2BimPlansUnitsTests.cs`

---

### Task 9: Outlines tests — wall from a point cloud, walls from poché outlines

**Files:**
- Test: `Tests/Cad2BimOutlinesTests.cs`

**Interfaces:**
- Consumes: Task 4's csproj links; `CadClassifier.WallFromCloud(IReadOnlyList<Point>) → Wall` (null when nothing wall-shaped); `CadClassifier.WallsFromOutlines(IReadOnlyList<List<Point>>) → List<Wall>`.
- Produces: `Cloud(length, width, step)` and `Rotate(points, degrees)` factories — Tasks 14/15's Brush tests need exactly these for the "cloud fallback yields one wall" case.

- [ ] **Step 1: Write the failing test**

`Tests/Cad2BimOutlinesTests.cs`:

```csharp
// Walls read from poché rather than paired faces (Outlines.cs: CadClassifier.WallFromCloud,
// WallsFromOutlines). Both search for the narrowest box around the points and accept it
// when the short side is a wall thickness (SMin..SMax) and the long side is at least
// OutlineAspect (2.5) times that. Millimetres.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Xunit;

namespace Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimOutlinesTests
    {
        private static Point P(double x, double y) => new(x, y);

        /// <summary>A grid of points filling a length × width box at the origin.</summary>
        private static List<Point> Cloud(double length, double width, double step = 250)
        {
            var points = new List<Point>();
            for (double x = 0; x <= length + 1e-9; x += step)
            {
                points.Add(P(x, 0));
                points.Add(P(x, width / 2));
                points.Add(P(x, width));
            }
            return points;
        }

        private static List<Point> Rotate(IEnumerable<Point> points, double degrees)
        {
            double c = Math.Cos(degrees * Math.PI / 180.0), s = Math.Sin(degrees * Math.PI / 180.0);
            return points.Select(p => P((p.x * c) - (p.y * s), (p.x * s) + (p.y * c))).ToList();
        }

        private static List<Point> Rect(double w, double h) => new() { P(0, 0), P(w, 0), P(w, h), P(0, h) };

        // ── WallFromCloud ────────────────────────────────────────────────

        [Fact]
        public void Cloud_2000_by_150_becomes_one_wall_2000_long_and_150_thick()
        {
            Wall w = CadClassifier.WallFromCloud(Cloud(2000, 150));

            Assert.NotNull(w);
            Assert.Equal(150.0, w.Thickness, 6);
            Assert.Equal(2000.0, w.Centerline.Length, 6);
            Assert.Equal(75.0, w.Centerline.P1.y, 6);
            Assert.Equal(75.0, w.Centerline.P2.y, 6);
        }

        [Fact]
        public void Cloud_rotated_30_degrees_is_found_at_its_own_angle()
        {
            // The direction search steps 2°, so 30° is hit exactly.
            Wall w = CadClassifier.WallFromCloud(Rotate(Cloud(2000, 150), 30));

            Assert.NotNull(w);
            Assert.Equal(150.0, w.Thickness, 3);
            Assert.Equal(2000.0, w.Centerline.Length, 3);
            double dx = w.Centerline.P2.x - w.Centerline.P1.x, dy = w.Centerline.P2.y - w.Centerline.P1.y;
            Assert.Equal(30.0, Math.Abs(Math.Atan2(dy, dx) * 180.0 / Math.PI), 3);
        }

        [Fact]
        public void Cloud_thinner_than_SMin_is_not_a_wall()
        {
            Assert.Null(CadClassifier.WallFromCloud(Cloud(2000, 20)));
        }

        [Fact]
        public void Cloud_as_wide_as_it_is_long_is_not_a_wall()
        {
            // 400 × 300: thickness in range, but 400 < 300 × 2.5.
            Assert.Null(CadClassifier.WallFromCloud(Cloud(400, 300, step: 100)));
        }

        [Fact]
        public void Cloud_of_fewer_than_four_points_is_nothing()
        {
            Assert.Null(CadClassifier.WallFromCloud(new List<Point> { P(0, 0), P(2000, 0), P(2000, 150) }));
        }

        // ── WallsFromOutlines ────────────────────────────────────────────

        [Fact]
        public void Rectangle_3000_by_200_is_one_wall()
        {
            var walls = CadClassifier.WallsFromOutlines(new List<List<Point>> { Rect(3000, 200) });

            Wall w = Assert.Single(walls);
            Assert.Equal(200.0, w.Thickness, 6);
            Assert.Equal(3000.0, w.Centerline.Length, 6);
            Assert.Equal(100.0, w.Centerline.P1.y, 6);
            Assert.Equal(100.0, w.Centerline.P2.y, 6);
        }

        [Fact]
        public void Room_sized_outline_is_not_a_wall()
        {
            Assert.Empty(CadClassifier.WallsFromOutlines(new List<List<Point>> { Rect(4000, 3000) }));
        }

        [Fact]
        public void Column_outline_is_not_a_wall()
        {
            Assert.Empty(CadClassifier.WallsFromOutlines(new List<List<Point>> { Rect(300, 300) }));
        }

        [Fact]
        public void Outlines_with_fewer_than_four_points_are_skipped()
        {
            var triangle = new List<Point> { P(0, 0), P(3000, 0), P(1500, 200) };

            Assert.Empty(CadClassifier.WallsFromOutlines(new List<List<Point>> { triangle }));
        }

        [Fact]
        public void Mixed_outlines_yield_only_the_wall_shaped_ones()
        {
            var outlines = new List<List<Point>> { Rect(3000, 200), Rect(4000, 3000), Rect(300, 300), Rect(2500, 115) };

            var walls = CadClassifier.WallsFromOutlines(outlines);

            Assert.Equal(2, walls.Count);
            Assert.Contains(walls, w => Math.Abs(w.Thickness - 200) < 1e-6);
            Assert.Contains(walls, w => Math.Abs(w.Thickness - 115) < 1e-6);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (Windows rig or CI, before the file exists): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimOutlinesTests"`
Expected: `No test matches the given testcase filter \`FullyQualifiedName~Cad2BimOutlinesTests\``.

- [ ] **Step 3: Write minimal implementation** — none: the engine is on the branch. Prove the linked source is unmodified:

```bash
cd /Users/ashraf/development/bina/revit-addin-sync
git diff --stat origin/feat/cad2bim -- Cad2Bim/cad2bim/Outlines.cs
```
Expected: empty output.

- [ ] **Step 4: Run test to verify it passes**

Run (Mac compile gate): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true`   Expected: `Build succeeded.`
Run (Windows rig or CI): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimOutlinesTests"`
Expected: PASS — `Passed! - Failed: 0, Passed: 10` (verified 10/10 on the Mac harness). Pinned: 2000 × 150 cloud → Thickness 150, Length 2000, centreline at y = 75; the same cloud at 30° → same thickness/length, centreline at 30°; 2000 × 20 → null; 400 × 300 → null; 3 points → null; 3000 × 200 outline → one wall at y = 100; 4000 × 3000 and 300 × 300 outlines → none; triangle → skipped; mixed list → 2 walls (200 and 115 thick).

- [ ] **Step 5: Stage**

`git add Tests/Cad2BimOutlinesTests.cs`

Whole-section run on Windows/CI: `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2Bim"` → Expected `Passed: 62` (10 + 9 + 11 + 10 + 12 + 10).

---

### Task 10: Build contract types (Revit-free) + SilenceJoinFailures

**Files:**
- Create: `Services/CadToBim/BuildTypes.cs` (enums + `BuildRequest`)
- Create: `Services/CadToBim/BuildReport.cs`
- Create: `Services/CadToBim/BoxMm.cs`
- Create: `Services/CadToBim/BuildSeams.cs` (`IBuildEventRaiser`, `IBuildRequestSink`)
- Create: `Services/CadToBim/SilenceJoinFailures.cs` (Revit; NOT linked into Tests)
- Modify: `Tests/Tests.csproj` (four `<Compile Include>` lines under the marker comment Task 4 added)
- Test: `Tests/Cad2BimBuildTypesTests.cs`

**Interfaces:**
- Consumes: `Cad2Bim.Wall`, `Cad2Bim.Opening`, `Cad2Bim.Space`, `Cad2Bim.Point` (engine, linked into Tests by Task 4); `Autodesk.Revit.DB.IFailuresPreprocessor` (SilenceJoinFailures only).
- Produces (verbatim from the reconciled contract):
  - `enum BuildTarget { AddToProject, NewFile }`, `enum StoreyMode { Stack, KeepPosition }`
  - `sealed class BuildRequest { Target, DrawingPath, Walls, Openings, Spaces, HeightMm = 3000, Storeys = Stack, long? LevelId, OutputPath, TemplatePath }`
  - `sealed class BuildReport { int Walls, Doors, Windows, Rooms, SkippedOpenings, SkippedWalls; string OutputPath; TimeSpan Elapsed; string Error; Dictionary<Cad2Bim.Wall, long> WallIds; bool Ok => Error == null; }`
  - `readonly record struct BoxMm(MinX, MinY, MaxX, MaxY) { Width, Height, Contains(x, y), Contains(Point), static Normalised(x1, y1, x2, y2) }`
  - `interface IBuildEventRaiser { bool Raise(); }`, `interface IBuildRequestSink { BuildRequest Request { get; set; } event Action<BuildReport> Completed; }`
  - `public sealed class SilenceJoinFailures : IFailuresPreprocessor`

Why the split: `BuildRequest`/`BuildReport`/`BoxMm`/the seams are what the pane, the session and the brush are unit-tested against, and the Tests project's `RevitAPI.dll` reference is metadata-only (it resolves at compile time but any type that carries an `ElementId` field fails to load at test runtime). So Revit element ids travel as `long` (converted by `ElementIdCompat`, Task 14) and `BuildRequest.LevelId` is `long?`. `record struct` compiles on net48 because `Services/Net48Shims.cs` already supplies `IsExternalInit` — no polyfill is created.

- [ ] **Step 1: Write the failing test**

`Tests/Cad2BimBuildTypesTests.cs`:

```csharp
// BuildRequest / BuildReport / BoxMm / the two build seams are the pane -> builder
// contract for CAD to BIM (spec 2026-09-08 §3 "Build", §5 "Threading"). Pure shapes:
// no XYZ, no Document, no Transaction, no ElementId is constructed or even declared
// here — the Revit API reference is metadata-only. Cad2Bim.Wall is referenced as a
// dictionary key type only.

using System;
using System.Collections.Generic;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace RevitAddinSync.Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimBuildTypesTests
    {
        [Fact]
        public void Report_IsOk_WhenErrorIsNull()
        {
            var report = new BuildReport();
            Assert.True(report.Ok);
            Assert.Null(report.Error);
            Assert.Empty(report.WallIds);          // never null: the session merges it blindly
            Assert.Equal(TimeSpan.Zero, report.Elapsed);
            Assert.Equal(0, report.Walls + report.Doors + report.Windows + report.Rooms
                            + report.SkippedOpenings + report.SkippedWalls);
        }

        [Fact]
        public void Report_IsNotOk_WhenErrorIsSet()
        {
            var report = new BuildReport { Error = "Open a project first." };
            Assert.False(report.Ok);
        }

        [Fact]
        public void Report_WallIds_AreLongs_NotRevitTypes()
        {
            // The type argument is the contract: the session and the VM store ElementId VALUES.
            Assert.Equal(typeof(Dictionary<Cad2Bim.Wall, long>), typeof(BuildReport).GetField("WallIds").FieldType);
        }

        [Fact]
        public void Request_Defaults_AreStoreyHeightAndStack()
        {
            var request = new BuildRequest();
            Assert.Equal(3000, request.HeightMm);
            Assert.Equal(StoreyMode.Stack, request.Storeys);
            Assert.Equal(BuildTarget.AddToProject, request.Target);   // enum zero = the safe target
            Assert.Null(request.LevelId);                               // null = lowest level
            Assert.Equal(typeof(long?), typeof(BuildRequest).GetProperty("LevelId").PropertyType);
            Assert.Null(request.OutputPath);
            Assert.Null(request.TemplatePath);
        }

        [Fact]
        public void BoxMm_IsValueEqual()
        {
            Assert.Equal(new BoxMm(0, 0, 1000, 500), new BoxMm(0, 0, 1000, 500));
            Assert.NotEqual(new BoxMm(0, 0, 1000, 500), new BoxMm(0, 0, 1000, 501));
        }

        [Fact]
        public void BoxMm_Normalised_AcceptsAnyTwoCorners_AndContainsIsInclusive()
        {
            var box = BoxMm.Normalised(10, 20, -5, -8);
            Assert.Equal(new BoxMm(-5, -8, 10, 20), box);
            Assert.Equal(15, box.Width);
            Assert.Equal(28, box.Height);
            Assert.True(box.Contains(10, 20));
            Assert.True(box.Contains(new Cad2Bim.Point(-5, -8)));
            Assert.False(box.Contains(10.001, 0));
        }

        private sealed class FakeSink : IBuildRequestSink
        {
            public BuildRequest Request { get; set; }
            public event Action<BuildReport> Completed;
            public void Fire(BuildReport r) => Completed?.Invoke(r);
        }

        private sealed class FakeRaiser : IBuildEventRaiser
        {
            public bool Accept = true;
            public bool Raise() => Accept;
        }

        [Fact]
        public void Seams_AreImplementableWithoutRevit()
        {
            // The view model (Task 17) is tested against exactly these two fakes.
            var sink = new FakeSink();
            BuildReport seen = null;
            sink.Completed += r => seen = r;
            sink.Request = new BuildRequest { Target = BuildTarget.NewFile };
            sink.Fire(new BuildReport { Walls = 3 });
            Assert.Equal(3, seen.Walls);
            Assert.Equal(BuildTarget.NewFile, sink.Request.Target);

            IBuildEventRaiser raiser = new FakeRaiser { Accept = false };
            Assert.False(raiser.Raise());
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (Mac compile gate): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded" | head`
Expected: FAIL with `error CS0234: The type or namespace name 'CadToBim' does not exist in the namespace 'RevitWebAppSync.Services'`.
Run (Windows): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimBuildTypesTests"` Expected: FAIL, same compile error.

- [ ] **Step 3: Write minimal implementation**

`Services/CadToBim/BuildTypes.cs`:

```csharp
// The pane -> builder contract for CAD to BIM (spec 2026-09-08 §3 "Build").
//
// BuildRequest is everything Cad2BimBuilder needs to make Revit elements from
// the classifier's output. Plain data, and deliberately free of Revit types, so
// the pane can be unit-tested and a request can cross the ExternalEvent boundary
// as one object. The level is an ElementId VALUE (ElementIdCompat.ToLong on the
// pane side, ElementIdCompat.ToElementId in the builder).

using System.Collections.Generic;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>Where the built elements go.</summary>
    public enum BuildTarget
    {
        /// <summary>Into the active document, on a level the drafter picked.</summary>
        AddToProject,
        /// <summary>Into a new project created from a template and saved beside the drawing.</summary>
        NewFile,
    }

    /// <summary>How the floor plans a drawing holds side by side are placed.</summary>
    public enum StoreyMode
    {
        /// <summary>One level per plan cluster, each shifted so its own corner meets the
        /// origin - the building the way it stands.</summary>
        Stack,
        /// <summary>Everything on one level, exactly where the drawing puts it, so it lands on
        /// top of a linked CAD and can be compared against it.</summary>
        KeepPosition,
    }

    public sealed class BuildRequest
    {
        public BuildTarget Target { get; set; }
        public string DrawingPath { get; set; }

        /// <summary>Pending walls only (session.Pending()): a later Confirm appends, never
        /// rebuilds what is already in the model.</summary>
        public List<Cad2Bim.Wall> Walls { get; set; }
        public List<Cad2Bim.Opening> Openings { get; set; }
        public List<Cad2Bim.Space> Spaces { get; set; }

        /// <summary>Millimetres. A plan carries no height, so this is an assumption until a
        /// section is read; 3 m is the ordinary storey. Also the storey step in Stack mode.</summary>
        public double HeightMm { get; set; } = 3000;
        public StoreyMode Storeys { get; set; } = StoreyMode.Stack;

        /// <summary>AddToProject only. The ElementId value of the level the drafter picked
        /// (ElementIdCompat.ToLong); null = the lowest level in the document.</summary>
        public long? LevelId { get; set; }

        /// <summary>NewFile only: &lt;dwg dir&gt;/&lt;dwg name&gt;.rvt. The pane has already asked
        /// about overwriting by the time the builder sees this.</summary>
        public string OutputPath { get; set; }

        /// <summary>NewFile only. null = Application.DefaultProjectTemplate.</summary>
        public string TemplatePath { get; set; }
    }
}
```

`Services/CadToBim/BuildReport.cs`:

```csharp
// BuildReport — what one Cad2BimBuilder.Build run produced. Deliberately free of
// Revit types: the Tests project's RevitAPI reference is metadata-only at runtime,
// so a Dictionary<Wall, ElementId> here would make CadToBimSession untestable.
// Wall ids travel as long (ElementIdCompat.ToLong / ToElementId on the Revit side).

using System;
using System.Collections.Generic;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class BuildReport
    {
        public int Walls;
        public int Doors;
        public int Windows;
        public int Rooms;
        public int SkippedOpenings;
        public int SkippedWalls;

        /// <summary>NewFile target only: the .rvt that was written.</summary>
        public string OutputPath;

        public TimeSpan Elapsed;

        /// <summary>null = success. Innermost exception message otherwise; the
        /// TransactionGroup was rolled back and nothing below is in the model.</summary>
        public string Error;

        /// <summary>CAD wall -> Revit wall id (ElementId value), for every wall the
        /// build created and that survived Assimilate(). Keyed by reference.</summary>
        public Dictionary<Cad2Bim.Wall, long> WallIds = new();

        public bool Ok => Error == null;
    }
}
```

`Services/CadToBim/BoxMm.cs`:

```csharp
// BoxMm — an axis-aligned box in drawing millimetres (the engine's coordinate space
// after Units.Resolve). Kept in its own Revit-free file: the Tests project links it.
// `record struct` on net48 needs IsExternalInit, which Services/Net48Shims.cs supplies.

using System;

namespace RevitWebAppSync.Services.CadToBim
{
    public readonly record struct BoxMm(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        /// <summary>Inclusive on every edge.</summary>
        public bool Contains(double x, double y) =>
            x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

        public bool Contains(Cad2Bim.Point p) => Contains(p.x, p.y);

        /// <summary>From any two opposite corners, in any order — a drag can go up-left.</summary>
        public static BoxMm Normalised(double x1, double y1, double x2, double y2) =>
            new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }
}
```

`Services/CadToBim/BuildSeams.cs`:
```csharp
using System;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>
    /// Raises the build ExternalEvent. An interface because the view model is unit-tested in a
    /// project that references RevitAPI (DB) but not RevitAPIUI, where ExternalEvent lives.
    /// </summary>
    public interface IBuildEventRaiser
    {
        /// <summary>True when Revit accepted the request (ExternalEventRequest.Accepted).</summary>
        bool Raise();
    }

    /// <summary>What the view model needs from CadToBimBuildHandler: a request slot and a
    /// completion callback. The handler implements this; tests fake it.</summary>
    public interface IBuildRequestSink
    {
        BuildRequest Request { get; set; }
        event Action<BuildReport> Completed;
    }
}
```

`Services/CadToBim/SilenceJoinFailures.cs` (body verbatim from `origin/feat/cad2bim:Commands/Cad2BimConvertCommand.cs:340-373`, visibility widened so the builder and any later caller share one copy; the branch's second copy in `Cad2BimSelectionCommand.cs:343` dies with that file in Task 15):

```csharp
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>
    /// Clears the join complaints that bulk wall creation raises. Warnings are deleted
    /// outright; errors are resolved the way the dialog's own default button would, so the
    /// run continues instead of stopping on the first of a thousand.
    /// </summary>
    public sealed class SilenceJoinFailures : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            IList<FailureMessageAccessor> failures = accessor.GetFailureMessages();
            if (failures.Count == 0) return FailureProcessingResult.Continue;

            bool resolved = false;

            foreach (FailureMessageAccessor failure in failures)
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    accessor.DeleteWarning(failure);
                    continue;
                }

                if (failure.HasResolutions())
                {
                    accessor.ResolveFailure(failure);
                    resolved = true;
                }
            }

            return resolved
                ? FailureProcessingResult.ProceedWithCommit
                : FailureProcessingResult.Continue;
        }
    }
}
```

`Tests/Tests.csproj` — directly after the marker comment `<!-- CAD to BIM pane sources (Revit-free) — later tasks append here -->` (Task 4):

```xml
    <!-- CAD to BIM pane -> builder contract (spec 2026-09-08 §3, §5). Pure shapes:
         Cad2Bim.Wall/Opening/Space/Point are key/list/parameter types only; element ids
         are longs — nothing Revit is declared. -->
    <Compile Include="..\Services\CadToBim\BuildTypes.cs" Link="CadToBim\BuildTypes.cs" />
    <Compile Include="..\Services\CadToBim\BuildReport.cs" Link="CadToBim\BuildReport.cs" />
    <Compile Include="..\Services\CadToBim\BoxMm.cs" Link="CadToBim\BoxMm.cs" />
    <Compile Include="..\Services\CadToBim\BuildSeams.cs" Link="CadToBim\BuildSeams.cs" />
```

- [ ] **Step 4: Run test to verify it passes**

Run (Mac compile gate): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded"`  Expected: `Build succeeded`.
Run (Windows): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimBuildTypesTests"`  Expected: PASS, 7 tests.

Then the add-in itself: `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows` — Expected: `Build succeeded` ×3 (the net48 one proves `record struct` binds to the shim's `IsExternalInit`; if it reports `CS0101 ... already contains a definition for 'IsExternalInit'`, someone created `Cad2Bim/IsExternalInit.cs` — delete it).

- [ ] **Step 5: Stage**

`git add Services/CadToBim/BuildTypes.cs Services/CadToBim/BuildReport.cs Services/CadToBim/BoxMm.cs Services/CadToBim/BuildSeams.cs Services/CadToBim/SilenceJoinFailures.cs Tests/Cad2BimBuildTypesTests.cs Tests/Tests.csproj`   (repo owner rule: executors STAGE, never commit)

---

### Task 11: `Cad2BimBuilder.Build`

**Files:**
- Create: `Services/CadToBim/Cad2BimBuilder.cs`
- Test: none possible (constructs `Transaction`, `XYZ`, `Document`). Verify = compile net48 + net8.0-windows on the Mac, then the Windows smoke rows in Step 4.

**Interfaces:**
- Consumes: Task 10 types (`BuildRequest`, `BuildReport`, `BuildTarget`, `StoreyMode`, `SilenceJoinFailures`); Task 14 `ElementIdCompat.ToLong / ToElementId`; engine `Cad2Bim.PlanCluster`, `Cad2Bim.CadClassifier.ClusterPlans(List<Wall>, IReadOnlyList<TextElement>, double)`, `Cad2Bim.Wall.Centerline/Thickness`, `Cad2Bim.Opening.Wall/IsDoor/Position/Width`, `Cad2Bim.Space.Boundary/Name/SubElements`, `Cad2Bim.Segment.P1/P2/Length`, `Cad2Bim.Point.x/y`.
- Produces: `public static BuildReport Cad2BimBuilder.Build(Autodesk.Revit.UI.UIApplication uiapp, BuildRequest req)` — never throws; every failure lands in `report.Error` as the innermost exception message; `report.WallIds[cadWall] = ElementIdCompat.ToLong(revitWall.Id)` for every surviving wall.

**Slot:** after Tasks 10 and 14 (see "Execution order").

Behaviour locked by this task (the pane and session sections rely on these):
1. `req.Walls` is the pending set. Openings whose `Wall` is not in `req.Walls` are **ignored, not counted** (they belong to an earlier Confirm). Openings whose wall IS pending but got no Revit host, or have no `FamilySymbol`, or are narrower than 300 mm → `SkippedOpenings++`.
2. Spaces are assigned to the cluster of their first wall in `SubElements`; a space with no pending wall is ignored (already built).
3. One `TransactionGroup("CAD to BIM")` assimilated = one undo step. Per cluster two transactions: `"CAD to BIM: walls"` then `"CAD to BIM: openings and rooms"`, both with `SilenceJoinFailures`.
4. `report.WallIds` / `report.Walls` come from a survivor re-query after `Assimilate()`, never from the loop's tally.
5. NewFile: `SaveAsOptions { OverwriteExistingFile = true }` is unconditional — **the pane must prompt about an existing file before raising the event**. On save failure the new document is closed without saving and `report.Error` carries the message; because the session only calls `MarkBuilt` on `Ok`, a second Confirm rebuilds (this is the v1 substitute for the spec's "pane offers Save As", see notes).
6. AddToProject level: `req.LevelId` if it resolves to a `Level`, else the lowest level. Stack mode: cluster `i` goes on the `i`-th level at or above the base level (by elevation order), creating `"CAD Level {i+1}"` at `base.Elevation + i × HeightMm` when there is none.

- [ ] **Step 1: Write the failing test**

Not applicable (Revit-touching). The failing check is the compile: `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows 2>&1 | grep -c Cad2BimBuilder` → `0` (nothing references the type yet; Task 12's handler will).

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && grep -rn "Cad2BimBuilder" --include="*.cs" Services UI Commands App.cs`
Expected: no output (the type does not exist yet).

- [ ] **Step 3: Write minimal implementation**

`Services/CadToBim/Cad2BimBuilder.cs` (extracted from `origin/feat/cad2bim:Commands/Cad2BimConvertCommand.cs` lines 141–330 and helpers 375–641; the storey TaskDialog became `req.Storeys`, the report TaskDialog became `BuildReport`, `DefaultWallHeightMm` became `req.HeightMm`):

```csharp
// Builds native Revit elements from the classifier's output (spec 2026-09-08 §3 "Build").
//
// This is what "CAD to RVT" has to mean. RVT is a closed format that nothing outside
// Revit can write, so the only way to produce one is to create the elements inside a
// running Revit and let the user save. The classification is the same code the
// standalone viewer runs - the Cad2Bim sources carry no Revit dependency, which is what
// makes them usable from both.
//
// Runs only on Revit's main thread, inside CadToBimBuildHandler.Execute. Never throws:
// every failure is a BuildReport with Error set, the group rolled back, and (for a new
// file) the throwaway document closed - so no hidden document is left holding a lock.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Cad2Bim;

// Revit has its own Segment, Wall and Point; the classifier's are always spelled through these.
using CadSegment = Cad2Bim.Segment;
using CadWall = Cad2Bim.Wall;
using CadOpening = Cad2Bim.Opening;
using CadPoint = Cad2Bim.Point;
using RevitWall = Autodesk.Revit.DB.Wall;

namespace RevitWebAppSync.Services.CadToBim
{
    public static class Cad2BimBuilder
    {
        /// <summary>Ordinary sill height, in millimetres. The plan does not say - that is a
        /// section - so it is assumed, like the wall height.</summary>
        private const double WindowSillMm = 900.0;

        /// <summary>Narrower than any real opening.</summary>
        private const double MinOpeningWidthMm = 300.0;

        /// <summary>One plan cluster and where it goes: the shift that brings its corner to
        /// the origin (zero in KeepPosition mode) and which storey it is.</summary>
        private sealed class Placement
        {
            public PlanCluster Plan;
            public double OriginX;
            public double OriginY;
            public int LevelIndex;
        }

        public static BuildReport Build(UIApplication uiapp, BuildRequest req)
        {
            var report = new BuildReport();
            Stopwatch clock = Stopwatch.StartNew();

            Document doc = null;
            bool isNew = false;
            TransactionGroup group = null;

            try
            {
                if (uiapp == null) throw new ArgumentNullException(nameof(uiapp));
                if (req == null) throw new ArgumentNullException(nameof(req));
                if (req.Walls == null || req.Walls.Count == 0)
                    throw new InvalidOperationException("Nothing to build: every detected wall is erased or already built.");

                (doc, isNew) = ResolveDocument(uiapp, req);

                WallType wallType = DefaultWallType(doc);
                if (wallType == null)
                    throw new InvalidOperationException("This model has no basic wall type to use.");

                Level baseLevel = BaseLevel(doc, req);
                if (baseLevel == null)
                    throw new InvalidOperationException("This model has no level to build on.");

                List<Placement> placements = ClusterAndPlace(req);
                if (placements.Count == 0)
                    throw new InvalidOperationException("No floor plans could be separated out.");

                // Which cluster each pending wall belongs to. Openings and rooms follow their
                // walls, so a room cannot span two floors and an opening cannot host itself on
                // the wrong one. Reference identity: Cad2Bim.Wall does not override Equals.
                var owner = new Dictionary<CadWall, Placement>();
                foreach (Placement placement in placements)
                    foreach (CadWall wall in placement.Plan.Walls)
                        owner[wall] = placement;

                List<CadOpening> openings = req.Openings ?? new List<CadOpening>();
                List<Space> spaces = req.Spaces ?? new List<Space>();

                // One undo step for the whole build, however many plans it holds.
                group = new TransactionGroup(doc, "CAD to BIM");
                group.Start();

                var createdIds = new Dictionary<CadWall, ElementId>();

                foreach (Placement placement in placements)
                {
                    Level level;
                    var hosts = new Dictionary<CadWall, RevitWall>();

                    using (var tx = new Transaction(doc, "CAD to BIM: walls"))
                    {
                        tx.Start();
                        Silence(tx);

                        level = LevelFor(doc, baseLevel, placement.LevelIndex, req.HeightMm);
                        if (level == null)
                        {
                            tx.RollBack();
                            report.SkippedWalls += placement.Plan.Walls.Count;
                            continue;
                        }

                        foreach (CadWall wall in placement.Plan.Walls)
                        {
                            try
                            {
                                RevitWall made = CreateWall(
                                    doc, wall, wallType, level, req.HeightMm,
                                    placement.OriginX, placement.OriginY);

                                if (made != null)
                                {
                                    createdIds[wall] = made.Id;
                                    hosts[wall] = made;
                                }
                                else
                                {
                                    report.SkippedWalls++;
                                }
                            }
                            catch
                            {
                                // One bad centreline should not cost the other thousand.
                                report.SkippedWalls++;
                            }
                        }

                        tx.Commit();
                    }

                    using (var tx = new Transaction(doc, "CAD to BIM: openings and rooms"))
                    {
                        tx.Start();
                        Silence(tx);

                        // A door needs the wall's geometry to exist before it can cut it.
                        doc.Regenerate();

                        List<CadOpening> here = openings
                            .Where(o => o.Wall != null && owner.TryGetValue(o.Wall, out Placement p) && p == placement)
                            .ToList();
                        CreateOpenings(doc, here, hosts, level, placement.OriginX, placement.OriginY, report);

                        List<Space> rooms = spaces
                            .Where(s => OwnerOf(s, owner) == placement)
                            .ToList();
                        report.Rooms += CreateRooms(doc, rooms, level, placement.OriginX, placement.OriginY);

                        tx.Commit();
                    }
                }

                group.Assimilate();

                // Ask the model what is actually in it rather than trusting the loop's own
                // tally: a transaction that rolls back, or elements a failure handler quietly
                // deletes, leave the counter saying one thing and the model saying another.
                var present = new HashSet<ElementId>(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(RevitWall))
                        .WhereElementIsNotElementType()
                        .ToElementIds());

                foreach (KeyValuePair<CadWall, ElementId> pair in createdIds)
                    if (present.Contains(pair.Value)) report.WallIds[pair.Key] = ElementIdCompat.ToLong(pair.Value);

                report.Walls = report.WallIds.Count;

                if (isNew)
                {
                    // The pane has already asked about overwriting; here the answer was yes.
                    doc.SaveAs(req.OutputPath, new SaveAsOptions { OverwriteExistingFile = true });
                    report.OutputPath = req.OutputPath;

                    // A document made by NewProjectDocument has no window. Close it and open the
                    // saved file the ordinary way so the drafter is looking at it.
                    doc.Close(false);
                    doc = null;

                    try
                    {
                        uiapp.OpenAndActivateDocument(req.OutputPath);
                    }
                    catch (Exception ex)
                    {
                        // Saved and closed cleanly; only the activation failed (Revit refuses
                        // while another document is mid-edit). The path is in the report.
                        Debug.WriteLine("[CadToBim] saved but could not activate " + req.OutputPath + ": " + ex.Message);
                    }
                }
                else
                {
                    report.OutputPath = doc.PathName;
                }
            }
            catch (Exception ex)
            {
                if (group != null && group.HasStarted())
                {
                    try { group.RollBack(); } catch { /* already closed by a failed transaction */ }
                }

                report.Error = Innermost(ex).Message;
                report.WallIds.Clear();
                report.Walls = 0;

                if (isNew && doc != null)
                {
                    // Nothing saved: drop the throwaway document so it holds no lock and
                    // shows up in no window list.
                    try { doc.Close(false); } catch { /* never opened properly */ }
                }
            }
            finally
            {
                group?.Dispose();
                report.Elapsed = clock.Elapsed;
            }

            return report;
        }

        // ─── document + level ────────────────────────────────────────────

        /// <summary>The document to build into. AddToProject = the active one; NewFile = a
        /// fresh project from the template, the metric default when the template is
        /// missing (same fallback as DwgScratchCache.TryOpenScratch).</summary>
        private static (Document doc, bool isNew) ResolveDocument(UIApplication uiapp, BuildRequest req)
        {
            if (req.Target == BuildTarget.AddToProject)
            {
                Document active = uiapp.ActiveUIDocument?.Document;
                if (active == null) throw new InvalidOperationException("Open a project first.");
                if (active.IsFamilyDocument) throw new InvalidOperationException("Open a project, not a family.");
                return (active, false);
            }

            if (string.IsNullOrEmpty(req.OutputPath))
                throw new InvalidOperationException("No output path for the new file.");

            Autodesk.Revit.ApplicationServices.Application app = uiapp.Application;
            string template = string.IsNullOrEmpty(req.TemplatePath) ? app.DefaultProjectTemplate : req.TemplatePath;

            Document created = !string.IsNullOrEmpty(template) && File.Exists(template)
                ? app.NewProjectDocument(template)
                : app.NewProjectDocument(UnitSystem.Metric);

            if (created == null) throw new InvalidOperationException("Revit could not create a new project.");
            return (created, true);
        }

        /// <summary>The level the first plan goes on: the drafter's pick when it still
        /// resolves, else the lowest level in the document.</summary>
        private static Level BaseLevel(Document doc, BuildRequest req)
        {
            if (req.Target == BuildTarget.AddToProject && req.LevelId.HasValue && req.LevelId.Value > 0)
            {
                ElementId picked = ElementIdCompat.ToElementId(req.LevelId.Value);
                if (doc.GetElement(picked) is Level level) return level;
            }

            return LowestLevel(doc);
        }

        /// <summary>
        /// The level a given plan belongs on. Existing levels are used in order of height
        /// first - a template usually ships with a couple - and further ones are created above
        /// them as the drawing needs. Storey height is the wall height, since a plan says
        /// nothing about either. Counted from the base level, so "Add to project" on Level 2
        /// stacks upward from Level 2.
        /// </summary>
        private static Level LevelFor(Document doc, Level baseLevel, int index, double heightMm)
        {
            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .ToList();

            int baseIndex = levels.FindIndex(level => level.Id == baseLevel.Id);
            if (baseIndex < 0) baseIndex = 0;

            int target = baseIndex + index;
            if (target < levels.Count) return levels[target];

            try
            {
                Level created = Level.Create(doc, baseLevel.Elevation + FromMm(heightMm * index));
                if (created != null) created.Name = "CAD Level " + (index + 1);
                return created;
            }
            catch
            {
                return levels.LastOrDefault();
            }
        }

        private static Level LowestLevel(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => level.Elevation)
                .FirstOrDefault();

        private static WallType DefaultWallType(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(type => type.Kind == WallKind.Basic);

        // ─── clustering ──────────────────────────────────────────────────

        /// <summary>
        /// One storey per floor plan. Read literally the sheet is a single level 350 metres
        /// across with every floor lying flat beside the others - a carpet, not a building.
        /// Each plan gets its own level, and each is shifted so its own corner meets the
        /// origin, which stacks them the way the building actually is.
        ///
        /// Two things a converted model gets used for, and they want opposite placement. To
        /// keep as a model, each plan belongs at the origin on its own level. To check against
        /// the drawing it came from, it has to land exactly on top of the linked CAD - same
        /// coordinates, same layout, no stacking - or the two cannot be compared at all.
        /// </summary>
        private static List<Placement> ClusterAndPlace(BuildRequest req)
        {
            var placements = new List<Placement>();

            if (req.Storeys == StoreyMode.KeepPosition)
            {
                // One cluster covering everything, placed where the drawing has it.
                var whole = new PlanCluster();
                foreach (CadWall wall in req.Walls) whole.Add(wall);
                whole.MinX = 0;
                whole.MinY = 0;
                placements.Add(new Placement { Plan = whole, OriginX = 0, OriginY = 0, LevelIndex = 0 });
                return placements;
            }

            List<PlanCluster> plans = CadClassifier.ClusterPlans(req.Walls);
            for (int i = 0; i < plans.Count; i++)
            {
                placements.Add(new Placement
                {
                    Plan = plans[i],
                    OriginX = plans[i].MinX,
                    OriginY = plans[i].MinY,
                    LevelIndex = i,
                });
            }

            return placements;
        }

        /// <summary>The cluster a room belongs to: that of the first of its walls that is
        /// being built now. A room none of whose walls are pending was built last time.</summary>
        private static Placement OwnerOf(Space space, Dictionary<CadWall, Placement> owner)
        {
            foreach (CadWall wall in space.SubElements.OfType<CadWall>())
                if (owner.TryGetValue(wall, out Placement placement)) return placement;
            return null;
        }

        // ─── element creation ────────────────────────────────────────────

        /// <summary>
        /// Revit tries to join every new wall to whatever it touches, and traced walls touch
        /// constantly. Each failed join raises a dialog, so a thousand walls becomes a
        /// thousand interruptions; the joins are not wanted anyway.
        /// </summary>
        private static void Silence(Transaction tx)
        {
            FailureHandlingOptions options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new SilenceJoinFailures());
            options.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(options);
        }

        private static RevitWall CreateWall(
            Document doc, CadWall wall, WallType wallType, Level level, double heightMm,
            double originX, double originY)
        {
            CadSegment centerline = wall.Centerline;
            if (centerline.Length < 1.0) return null;   // shorter than a millimetre

            XYZ start = ToRevit(centerline.P1, originX, originY);
            XYZ end = ToRevit(centerline.P2, originX, originY);
            if (start.DistanceTo(end) < doc.Application.ShortCurveTolerance) return null;

            Curve curve = Line.CreateBound(start, end);
            double height = FromMm(heightMm);

            RevitWall created = RevitWall.Create(
                doc, curve, wallType.Id, level.Id, height, 0.0, false, false);

            if (created != null)
            {
                // Traced walls meet at whatever angle the drawing had them; letting Revit
                // resolve those joins produces geometry nobody asked for and a warning for
                // each one. They are placed as drawn and joined later, deliberately, if at all.
                WallUtils.DisallowWallJoinAtEnd(created, 0);
                WallUtils.DisallowWallJoinAtEnd(created, 1);
            }

            return created;
        }

        /// <summary>
        /// Places each opening into the wall it belongs to.
        ///
        /// A door needs a host: Revit cuts the opening out of the wall it is placed in, which
        /// is why the classifier's wall-to-opening pairing matters more here than anywhere
        /// else. An opening whose host failed to build is skipped rather than dropped into
        /// space, since a door standing in a room is worse than a door that is missing.
        /// </summary>
        private static void CreateOpenings(
            Document doc, List<CadOpening> openings,
            Dictionary<CadWall, RevitWall> hosts,
            Level level, double originX, double originY, BuildReport report)
        {
            FamilySymbol doorType = FirstSymbol(doc, BuiltInCategory.OST_Doors);
            FamilySymbol windowType = FirstSymbol(doc, BuiltInCategory.OST_Windows);

            foreach (CadOpening opening in openings)
            {
                FamilySymbol symbol = opening.IsDoor ? doorType : windowType;
                if (symbol == null) { report.SkippedOpenings++; continue; }   // template has no family

                if (!hosts.TryGetValue(opening.Wall, out RevitWall host)) { report.SkippedOpenings++; continue; }
                if (opening.Width < MinOpeningWidthMm) { report.SkippedOpenings++; continue; }

                try
                {
                    if (!symbol.IsActive) symbol.Activate();

                    // A window sits at sill height; a door starts at the floor.
                    double z = level.Elevation + (opening.IsDoor ? 0 : FromMm(WindowSillMm));
                    XYZ where = ToRevit(opening.Position, originX, originY);

                    FamilyInstance placed = doc.Create.NewFamilyInstance(
                        new XYZ(where.X, where.Y, z), symbol, host, level,
                        Autodesk.Revit.DB.Structure.StructuralType.NonStructural);

                    if (placed == null) { report.SkippedOpenings++; continue; }

                    if (opening.IsDoor) report.Doors++;
                    else report.Windows++;
                }
                catch
                {
                    // A single opening that will not host should not cost the rest.
                    report.SkippedOpenings++;
                }
            }
        }

        private static FamilySymbol FirstSymbol(Document doc, BuiltInCategory category) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(category)
                .Cast<FamilySymbol>()
                .OrderBy(symbol => symbol.Name)
                .FirstOrDefault();

        /// <summary>
        /// Places each room the classifier found, bounded by its own outline.
        ///
        /// Revit works rooms out from enclosed wall boundaries, and traced walls are neither
        /// continuous nor joined - they are placed as the drawing drew them, deliberately. Ask
        /// Revit to find rooms among those and it returns a model full of "not enclosed".
        ///
        /// So the boundary the classifier already computed is drawn as room separation lines
        /// and the room placed inside it. The enclosure is then ours rather than a side effect
        /// of wall geometry, and a room appears wherever a loop closed - regardless of how
        /// ragged the walls around it are.
        /// </summary>
        private static int CreateRooms(
            Document doc, List<Space> spaces, Level level, double originX, double originY)
        {
            if (spaces.Count == 0) return 0;

            ViewPlan view = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(plan => !plan.IsTemplate && plan.GenLevel != null &&
                                        plan.GenLevel.Id == level.Id);

            // Room separation lines are view-hosted; a level with no plan view (one this
            // build just created) gets its walls and openings but no rooms.
            if (view == null) return 0;

            SketchPlane sketch = SketchPlane.Create(
                doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation)));

            int placed = 0;

            foreach (Space space in spaces)
            {
                if (space.Boundary.Count < 3) continue;

                try
                {
                    var curves = new CurveArray();
                    for (int i = 0; i < space.Boundary.Count; i++)
                    {
                        XYZ from = ToRevit(space.Boundary[i], originX, originY);
                        XYZ to = ToRevit(space.Boundary[(i + 1) % space.Boundary.Count], originX, originY);

                        if (from.DistanceTo(to) < doc.Application.ShortCurveTolerance) continue;
                        curves.Append(Line.CreateBound(from, to));
                    }

                    if (curves.Size < 3) continue;

                    doc.Create.NewRoomBoundaryLines(sketch, curves, view);

                    // Placed at the centre of the outline, which for the loops a floor plan
                    // produces is inside them.
                    double x = space.Boundary.Average(point => point.x);
                    double y = space.Boundary.Average(point => point.y);
                    XYZ centre = ToRevit(new CadPoint(x, y), originX, originY);

                    Autodesk.Revit.DB.Architecture.Room room =
                        doc.Create.NewRoom(level, new UV(centre.X, centre.Y));

                    if (room == null) continue;

                    if (space.Name != null) room.Name = space.Name;
                    placed++;
                }
                catch
                {
                    // A room that will not place should not cost the rest of them.
                }
            }

            return placed;
        }

        // ─── units + errors ──────────────────────────────────────────────

        /// <summary>The classifier works in millimetres; the Revit API works in feet.
        /// UnitTypeId exists from Revit 2021, so this is the same call on 2023-2027.</summary>
        private static double FromMm(double millimetres) =>
            UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);

        private static XYZ ToRevit(CadPoint point, double originX, double originY) =>
            new XYZ(FromMm(point.x - originX), FromMm(point.y - originY), 0);

        /// <summary>The message the drafter can act on is the innermost one; the wrappers
        /// above it say "Execution failed" and nothing else.</summary>
        private static Exception Innermost(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
```

net48 notes baked into the code above: every `ElementId` ↔ `long` crossing goes through `ElementIdCompat` (Task 14: `IntegerValue` on net48 = Revit 2023 API, `Value` on net8+), comparisons use `==` on `ElementId`, no `Wall.Create(doc, line, levelId, false)` shortcut, and the tuple deconstruction `(doc, isNew) = ...` is compiler-only (`System.ValueTuple` is in-box on 4.7+; `ModelSource.Read` already returns tuples on net48).

- [ ] **Step 4: Verify (compile both TFMs + Windows smoke rows)**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48`
Expected: `Build succeeded` twice, 0 errors (warnings about nullable annotations in engine files are pre-existing).

Then on the Windows rig (Revit 2024 for net48, 2025 for net8), after Tasks 12 and 16–19 + 3 land, these rows are the "Builder rows" table of the smoke checklist `docs/superpowers/plans/2026-09-08-cad-to-bim-smoke.md` (Task 22); each is PASS/FAIL with the report values written next to it:

| # | Row | Expected |
|---|-----|----------|
| C1 | AddToProject, Level 1, Stack, sample DWG with ≥2 plans, Confirm | Walls on Level 1 and on "CAD Level 2" (elevation = Level 1 + 3000 mm); report Walls/Doors/Windows/Rooms equal what the pane counted minus Skipped*; report Elapsed shown |
| C2 | After C1, one Ctrl+Z | Every wall, door, window, room and "CAD Level 2" gone; Undo list shows one entry "CAD to BIM" |
| C3 | AddToProject, KeepPosition, same DWG linked at origin in the plan view | Walls sit on top of the linked linework; all on Level 1; no "CAD Level N" created |
| C4 | NewFile (Save as new .rvt), default template | `<dwgdir>/<dwgname>.rvt` exists, becomes the active document, reopens after Revit restart; report OutputPath = that path |
| C5 | NewFile with Settings → template override pointing at a metric template with no door/window families | Error null, Doors = Windows = 0, SkippedOpenings > 0, walls present |
| C6 | AddToProject with no document open | Status shows "Open a project first."; nothing created; controls unlock |
| C7 | After C1, Brush a box that adds N walls, Confirm again | Report Walls = N (only the brushed walls); no duplicates of C1 walls; second undo entry "CAD to BIM" |
| C8 | NewFile with `TemplatePath` = an existing non-.rte file (forced failure) | Error = innermost Revit message; no extra document in Revit's Switch Windows list; no .rvt written |
| C9 | AddToProject on Level 2 (picker), Stack, 2-plan DWG | First plan on Level 2, second on the next existing level above (or "CAD Level 2" at Level 2 + 3000 mm when none) |

- [ ] **Step 5: Stage**

`git add Services/CadToBim/Cad2BimBuilder.cs`   (repo owner rule: executors STAGE, never commit)

---

### Task 12: `CadToBimBuildHandler` + `ExternalEventRaiser` + delete `Cad2BimConvertCommand.cs`

**Files:**
- Create: `Services/CadToBim/CadToBimBuildHandler.cs`
- Create: `Services/CadToBim/ExternalEventRaiser.cs` (Revit UI; NOT linked into Tests)
- Delete: `Commands/Cad2BimConvertCommand.cs`
- Test: none (IExternalEventHandler; `Execute` needs a `UIApplication`; `ExternalEvent.Raise` needs Revit). Verify = build all TFMs + grep. The seam interfaces both types implement are pinned by `Tests/Cad2BimBuildTypesTests.Seams_AreImplementableWithoutRevit` (Task 10) and exercised through fakes in `Tests/CadToBimViewModelTests` (Task 17).

**Interfaces:**
- Consumes: `Cad2BimBuilder.Build(UIApplication, BuildRequest)` (Task 11), `BuildRequest`/`BuildReport`/`IBuildRequestSink`/`IBuildEventRaiser` (Task 10).
- Produces (verbatim from the reconciled contract):
  ```csharp
  public sealed class CadToBimBuildHandler : Autodesk.Revit.UI.IExternalEventHandler, IBuildRequestSink {
      public BuildRequest Request { get; set; }
      public event Action<BuildReport> Completed;
      public void Execute(UIApplication app);
      public string GetName();   // "BINA CAD to BIM build"
  }
  public sealed class ExternalEventRaiser : IBuildEventRaiser {
      public ExternalEventRaiser(Autodesk.Revit.UI.ExternalEvent externalEvent);   // null → Raise() false
      public bool Raise();                                                          // == ExternalEventRequest.Accepted
  }
  ```
  Wiring contract for `App.cs` (Task 3): `CadToBimBuildHandler = new CadToBimBuildHandler(); CadToBimBuildEvent = ExternalEvent.Create(CadToBimBuildHandler);` in `OnStartup` BEFORE `new CadToBimPaneHost()`. The panel (Task 18) builds `new CadToBimViewModel(App.CadToBimBuildHandler, new ExternalEventRaiser(App.CadToBimBuildEvent))`; the view model treats `Raise() == false` as "Revit is busy, try again".

**Precondition (ordering):** `Commands/Cad2BimSelectionCommand.cs` references `Cad2BimConvertCommand.LastDrawingPath / LastOriginX / LastOriginY / LastLevelId / LastThicknessMm / DefaultWallType / BuildFilter / CreateWall / FromMm` (lines 55–331 on the branch). Task 15 `git rm`s that file. Task 12 MUST run after Task 15, or the build breaks at Step 4. Task 1 (spec §7.2) has already dropped the two `aiPanel` PushButtons and the `#if !REVIT2023_24` block from `App.cs`; Step 2 checks both.

- [ ] **Step 1: Write the failing test**

Not applicable (Revit-touching). The failing check is the reference grep in Step 2.

- [ ] **Step 2: Verify preconditions and that the handler does not exist yet**

Run:
```
cd /Users/ashraf/development/bina/revit-addin-sync && \
  test ! -e Commands/Cad2BimSelectionCommand.cs && echo "selection command gone" ; \
  grep -rn "Cad2BimConvertCommand\|Cad2BimSelectionCommand" --include="*.cs" --include="*.addin" --include="*.csproj" . | grep -v "/bin/\|/obj/\|/.worktrees/\|/.claude/" ; \
  grep -rn "CadToBimBuildHandler\|ExternalEventRaiser" --include="*.cs" Services UI App.cs
```
Expected: `selection command gone`; the first grep lists ONLY `Commands/Cad2BimConvertCommand.cs` self-references (its class declaration); the second grep prints nothing. If `selection command gone` is missing, stop: run Task 15 first. If the first grep lists `App.cs`, stop: the merge step left the branch's ribbon buttons in — remove those two `PushButtonData` blocks and their `#if` before continuing (spec §7.2).

- [ ] **Step 3: Write minimal implementation**

`Services/CadToBim/CadToBimBuildHandler.cs`:

```csharp
// The one place CAD to BIM touches the Revit API (spec 2026-09-08 §5 "Threading").
//
// The pane runs detection on the thread pool and never holds a Revit object. When the
// drafter confirms, it sets Request and raises the ExternalEvent App.OnStartup created
// for this handler; Revit calls Execute on its main thread at the next opportunity,
// which is the only context Transaction/Wall.Create are legal in. Same pattern as
// BombaAutoFixHandler and CodeExecutionHandler.
//
// One request at a time: the view model refuses Confirm while Request is non-null.

using System;
using System.Diagnostics;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class CadToBimBuildHandler : IExternalEventHandler, IBuildRequestSink
    {
        /// <summary>Set by the pane immediately before ExternalEvent.Raise(). Cleared by
        /// Execute whatever happens, so a failed build never wedges the next Confirm.</summary>
        public BuildRequest Request { get; set; }

        /// <summary>Raised on Revit's main thread with the finished report (Ok or Error).
        /// Revit's main thread is the WPF UI thread for a dockable pane, but the view
        /// model still marshals through its Dispatcher before touching bound state.</summary>
        public event Action<BuildReport> Completed;

        public void Execute(UIApplication app)
        {
            BuildRequest request = Request;
            try
            {
                if (request == null) return;   // raised with nothing to do (double Raise)

                BuildReport report;
                try
                {
                    report = Cad2BimBuilder.Build(app, request);
                }
                catch (Exception ex)
                {
                    // Build() catches its own failures; this is the belt for the braces.
                    report = new BuildReport { Error = ex.Message };
                }

                try
                {
                    Completed?.Invoke(report);
                }
                catch (Exception ex)
                {
                    // A listener that throws must not surface as a Revit error dialog.
                    Debug.WriteLine("[CadToBim] Completed listener threw: " + ex.Message);
                }
            }
            finally
            {
                Request = null;
            }
        }

        public string GetName() => "BINA CAD to BIM build";
    }
}
```

`Services/CadToBim/ExternalEventRaiser.cs` (the Revit side of `IBuildEventRaiser`; the pane's view model never sees `ExternalEvent`, which lives in RevitAPIUI — an assembly the Tests project does not reference):

```csharp
using Autodesk.Revit.UI;

namespace RevitWebAppSync.Services.CadToBim
{
    /// <summary>Production IBuildEventRaiser over the ExternalEvent App.OnStartup created.</summary>
    public sealed class ExternalEventRaiser : IBuildEventRaiser
    {
        private readonly ExternalEvent _event;

        public ExternalEventRaiser(ExternalEvent externalEvent)
        {
            _event = externalEvent;
        }

        public bool Raise()
        {
            // A null event means OnStartup never created it (pane opened in a host without
            // Revit); "busy, try again" is the honest surface for that too.
            if (_event == null) return false;
            try
            {
                return _event.Raise() == ExternalEventRequest.Accepted;
            }
            catch
            {
                return false;
            }
        }
    }
}

- [ ] **Step 3b: Delete the command**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && git rm Commands/Cad2BimConvertCommand.cs`

Every symbol the deletion removes, and where it now lives:

| Removed from `Cad2BimConvertCommand` | Now |
|---|---|
| `DefaultWallHeightMm` (3000) | `BuildRequest.HeightMm` default (Task 10); `CadToBimSettings.WallHeightMm` (Task 13) |
| `LastDrawingPath`, `LastOriginX/Y`, `LastLevelId`, `LastThicknessMm` (cross-command statics) | `CadToBimSession.DrawingPath`, `.MedianThicknessMm`, `.BuiltWallIds` (Task 14). Origin/level are no longer remembered: brushed walls join the same session and the builder re-clusters them itself |
| `Execute` — OTA gate `UpdateService.EnsureUpToDate()` | `OpenCadToBimCommand.Execute` (Task 3) |
| `Execute` — `PickDrawing()` OpenFileDialog | `OpenCadToBimCommand` (Task 3) |
| `Execute` — survey read / `WallLayers` / `IsWallLayer` / `IsWindowLayer` / `IsOpeningLayer` / `Mentions` / `BuildFilter` (lines 69–117, 447–457, 569–589) | `CadToBimViewModel.OpenAsync` detect pipeline + `CadToBimSettings.WallLayerHints / OpeningLayerHints / ExcludeGlobs` (Tasks 17 + 13) |
| `Execute` — "No walls found" TaskDialog | view-model status line |
| `Execute` — "Where should the walls go?" TaskDialog (lines 146–173) | Storey mode radio → `BuildRequest.Storeys`; `Cad2BimBuilder.ClusterAndPlace` (Task 11) |
| `Execute` — wall type / level / transaction / walls / regenerate / openings / rooms / survivors (lines 180–262) | `Cad2BimBuilder.Build` (Task 11) |
| `Execute` — select + zoom survivors (lines 266–270) | dropped for v1 (the pane shows counts; AddToProject leaves the view where it was). Assembler may add `uidoc.ShowElements(report.WallIds.Values)` in the view model's Completed handler if wanted |
| `Execute` — report `StringBuilder` + TaskDialog (lines 275–329) | `BuildReport` fields rendered by the pane |
| `SilenceJoinFailures` (private nested) | `Services/CadToBim/SilenceJoinFailures.cs`, public (Task 10) |
| `CreateOpenings`, `WindowSillMm`, `FirstSymbol` | `Cad2BimBuilder` private (Task 11), skip counting added |
| `CreateRooms` | `Cad2BimBuilder.CreateRooms` (Task 11) |
| `CreateWall` (internal) | `Cad2BimBuilder.CreateWall` (Task 11), height parameterised |
| `FromMm`, `ToRevit` (internal) | `Cad2BimBuilder` private (Task 11) |
| `LevelFor(document, index)` | `Cad2BimBuilder.LevelFor(doc, baseLevel, index, heightMm)` (Task 11) — counts from the chosen base level |
| `LowestLevel`, `DefaultWallType` | `Cad2BimBuilder` private (Task 11) |
| `[Transaction(Manual)] IExternalCommand` registration + `"Cad2BimConvert"` ribbon button | gone; single `"CadToBim"` button → `OpenCadToBimCommand` (Task 3) |

- [ ] **Step 4: Verify**

Run:
```
cd /Users/ashraf/development/bina/revit-addin-sync && \
  ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows && \
  ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 && \
  ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows && \
  grep -rn "Cad2BimConvertCommand\|Cad2BimSelectionCommand" --include="*.cs" --include="*.addin" --include="*.csproj" --include="*.md" . | grep -v "/bin/\|/obj/\|/.worktrees/\|/.claude/\|docs/superpowers/specs/2026-09-08\|docs/superpowers/plans/2026-09-08"
```
Expected: `Build succeeded` ×3; the grep prints nothing (the spec and this plan are the only allowed mentions, and they are excluded). `Tests/CadToBimRibbonTests.OldBranchCommands_AreGone_FromTheRibbon` (Task 3) pins that `App.cs` does not mention either command.

- [ ] **Step 5: Stage**

`git add Services/CadToBim/CadToBimBuildHandler.cs Services/CadToBim/ExternalEventRaiser.cs` (the `git rm` in Step 3b already staged the deletion)   (repo owner rule: executors STAGE, never commit)

---

### Task 13: CadToBimSettings

**Files:**
- Create: `Services/CadToBim/CadToBimSettings.cs`
- Modify: `BinaConfig.cs:145-146` (insert after `public int EnginePort { get; set; } = 48820;`)
- Modify: `Tests/Tests.csproj` (one `<Compile Include>` line under the marker comment Task 4 added)
- Test: `Tests/CadToBimSettingsTests.cs`

**Interfaces:**
- Consumes: `Cad2Bim.LayerFilter` (engine, linked into the addin csproj by the merge step; linked into Tests here).
- Produces:
  ```csharp
  namespace RevitWebAppSync.Services.CadToBim {
    public sealed class CadToBimSettings {
      public const string ConfigKey = "cadToBim";
      public double SMinMm = 50, SMaxMm = 400, WallHeightMm = 3000, DoorMinRadiusMm = 500, DoorMaxRadiusMm = 1500, WindowSillMm = 900;
      public List<string> WallLayerHints, OpeningLayerHints, ExcludeGlobs;
      public string TemplatePath;
      internal static string ConfigPathOverride;          // tests only
      public static string DefaultConfigPath { get; }
      public static CadToBimSettings Load();
      public void Save();
      public Cad2Bim.LayerFilter ToLayerFilter();        // Exclude = ExcludeGlobs, Include empty
      public bool IsWallLayer(string layer);
      public bool IsOpeningLayer(string layer);
    }
  }
  ```
  `BinaConfig.CadToBim : Newtonsoft.Json.Linq.JObject` (opaque passthrough, `[JsonProperty("cadToBim")]`).

- [ ] **Step 1: Write the failing test**

`Tests/CadToBimSettingsTests.cs`:

```csharp
// CadToBimSettings — the pane's persisted defaults, stored under the "cadToBim"
// key of the same config.json BinaConfig owns. Pinned here: the spec defaults,
// a Save→Load round trip that REPLACES lists (Newtonsoft's default
// ObjectCreationHandling.Auto appends into field initialisers, so a saved
// 6-item hint list would come back as 11), preservation of the other keys in
// the file, and the EN+Malay layer hints.

using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace RevitWebAppSync.Tests
{
    [Collection("Cad2Bim")]
    public class CadToBimSettingsTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public CadToBimSettingsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "bina_cadtobim_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "config.json");
            CadToBimSettings.ConfigPathOverride = _path;
        }

        public void Dispose()
        {
            CadToBimSettings.ConfigPathOverride = null;
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        [Fact]
        public void Defaults_MatchSpec()
        {
            var s = new CadToBimSettings();
            Assert.Equal(50, s.SMinMm);
            Assert.Equal(400, s.SMaxMm);
            Assert.Equal(3000, s.WallHeightMm);
            Assert.Equal(500, s.DoorMinRadiusMm);
            Assert.Equal(1500, s.DoorMaxRadiusMm);
            Assert.Equal(900, s.WindowSillMm);
            Assert.Equal(new[] { "wall", "dinding", "tembok", "partition", "bata" }, s.WallLayerHints);
            Assert.Equal(new[] { "door", "pintu", "win", "tingkap", "glaz" }, s.OpeningLayerHints);
            Assert.Equal(new[] { "PERABUT", "FURNITURE", "FURN*", "SANI*", "FITTING", "Toilet-fitting",
                                 "*-DIM*", "DEFPOINTS", "G-bubble", "GRID*" }, s.ExcludeGlobs);
            Assert.Null(s.TemplatePath);
        }

        [Fact]
        public void Load_NoFile_ReturnsDefaults()
        {
            Assert.False(File.Exists(_path));
            var s = CadToBimSettings.Load();
            Assert.Equal(400, s.SMaxMm);
            Assert.Equal(5, s.WallLayerHints.Count);
        }

        [Fact]
        public void Load_MissingKey_ReturnsDefaults()
        {
            File.WriteAllText(_path, "{ \"Email\": \"a@b.c\", \"EnginePort\": 48820 }");
            var s = CadToBimSettings.Load();
            Assert.Equal(50, s.SMinMm);
        }

        [Fact]
        public void Load_CorruptFile_ReturnsDefaults()
        {
            File.WriteAllText(_path, "{ not json");
            var s = CadToBimSettings.Load();
            Assert.Equal(3000, s.WallHeightMm);
        }

        [Fact]
        public void SaveThenLoad_RoundTrips_AndReplacesListsInsteadOfAppending()
        {
            var s = new CadToBimSettings { SMinMm = 75, SMaxMm = 350, WallHeightMm = 3300, TemplatePath = @"C:\t\JKR.rte" };
            s.WallLayerHints.Add("kekisi");          // 6 items now
            s.ExcludeGlobs.Clear();
            s.ExcludeGlobs.Add("XREF*");             // 1 item
            s.Save();

            var back = CadToBimSettings.Load();
            Assert.Equal(75, back.SMinMm);
            Assert.Equal(350, back.SMaxMm);
            Assert.Equal(3300, back.WallHeightMm);
            Assert.Equal(@"C:\t\JKR.rte", back.TemplatePath);
            Assert.Equal(6, back.WallLayerHints.Count);          // 11 if lists appended
            Assert.Equal("kekisi", back.WallLayerHints.Last());
            Assert.Equal(new[] { "XREF*" }, back.ExcludeGlobs);   // not defaults + XREF*
        }

        [Fact]
        public void Save_PreservesOtherConfigKeys_AndWritesUnderCadToBim()
        {
            File.WriteAllText(_path, "{ \"Email\": \"a@b.c\", \"EnginePort\": 48820 }");
            new CadToBimSettings { WindowSillMm = 1000 }.Save();

            JObject root = JObject.Parse(File.ReadAllText(_path));
            Assert.Equal("a@b.c", (string)root["Email"]);
            Assert.Equal(48820, (int)root["EnginePort"]);
            Assert.Equal(1000, (double)root["cadToBim"]["WindowSillMm"]);
        }

        [Fact]
        public void Save_CreatesMissingDirectory()
        {
            CadToBimSettings.ConfigPathOverride = Path.Combine(_dir, "nested", "deeper", "config.json");
            new CadToBimSettings().Save();
            Assert.True(File.Exists(CadToBimSettings.ConfigPathOverride));
        }

        [Fact]
        public void IsWallLayer_MatchesHintsCaseInsensitiveSubstring()
        {
            var s = new CadToBimSettings();
            Assert.True(s.IsWallLayer("A-DINDING"));
            Assert.True(s.IsWallLayer("a-wall-ext"));
            Assert.True(s.IsWallLayer("PARTITION_150"));
            Assert.False(s.IsWallLayer("PERABUT"));
            Assert.False(s.IsWallLayer(""));
            Assert.False(s.IsWallLayer(null));
        }

        [Fact]
        public void IsOpeningLayer_MatchesDoorsAndWindows()
        {
            var s = new CadToBimSettings();
            Assert.True(s.IsOpeningLayer("A-PINTU"));
            Assert.True(s.IsOpeningLayer("A-Glazing"));
            Assert.True(s.IsOpeningLayer("WIN-1"));
            Assert.False(s.IsOpeningLayer("A-DINDING"));
        }

        [Fact]
        public void ToLayerFilter_CarriesExclusionsOnly_NoIncludeList()
        {
            var s = new CadToBimSettings();
            Cad2Bim.LayerFilter f = s.ToLayerFilter();
            Assert.Empty(f.Include);
            Assert.Equal(s.ExcludeGlobs, f.Exclude);
            Assert.False(f.IncludeHatch);
            Assert.False(f.IncludeDimensions);
            Assert.False(f.Allows("A-FURN-01", Cad2Bim.Services.CadSource.Geometry));
            Assert.True(f.Allows("A-DINDING", Cad2Bim.Services.CadSource.Geometry));
            Assert.True(f.Allows("0", Cad2Bim.Services.CadSource.Geometry));   // no include list = every layer
        }
    }
}
```

`Tests/Tests.csproj` — directly after the Task 10 links under the marker comment `<!-- CAD to BIM pane sources (Revit-free) — later tasks append here -->`:

```xml
    <Compile Include="..\Services\CadToBim\CadToBimSettings.cs" Link="CadToBim\CadToBimSettings.cs" />
```

(The engine files this needs — `ModelSource.cs` for `LayerFilter`, `Services/CadRenderSource.cs` for `CadSource` — and the ACadSharp package were linked once in Task 4; do not add them again.)

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true`
Expected: FAIL with `error CS0246: The type or namespace name 'CadToBimSettings' could not be found` (Mac). Windows: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CadToBimSettingsTests"` → same compile error.

- [ ] **Step 3: Write minimal implementation**

`Services/CadToBim/CadToBimSettings.cs`:

```csharp
// CadToBimSettings — the CAD to BIM pane's tunable defaults, persisted under the
// "cadToBim" key of %APPDATA%\RevitWebAppSync\config.json (the file BinaConfig owns).
//
// Why this does its own file I/O instead of going through BinaConfig.Load()/Save():
// BinaConfig.Load() runs ApplyDefaults/ApplyHeals (which may WRITE the file) and reads
// Cloud Docs tokens out of the Windows Credential Manager, and BinaConfig.cs cannot be
// linked into the Tests project. So this class reads the file as a JObject, replaces
// only its own key, and writes it back; BinaConfig carries the key through as an opaque
// JObject (BinaConfig.CadToBim) so ITS Save() does not drop what was written here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class CadToBimSettings
    {
        public const string ConfigKey = "cadToBim";

        // Millimetres throughout (the engine normalises every drawing to mm on load).
        public double SMinMm = 50;
        public double SMaxMm = 400;
        public double WallHeightMm = 3000;
        public double DoorMinRadiusMm = 500;
        public double DoorMaxRadiusMm = 1500;
        public double WindowSillMm = 900;

        // Case-insensitive substring hints, English + Malay — the same words the
        // Cad2BimConvertCommand used (Mentions()).
        public List<string> WallLayerHints = new() { "wall", "dinding", "tembok", "partition", "bata" };
        public List<string> OpeningLayerHints = new() { "door", "pintu", "win", "tingkap", "glaz" };

        // LayerFilter globs (`*` = any run of characters, case-insensitive). Furniture,
        // sanitary, dimensions, grid bubbles: draws, but not fabric.
        public List<string> ExcludeGlobs = new()
        {
            "PERABUT", "FURNITURE", "FURN*", "SANI*", "FITTING", "Toilet-fitting",
            "*-DIM*", "DEFPOINTS", "G-bubble", "GRID*",
        };

        // null = app.DefaultProjectTemplate at build time.
        public string TemplatePath;

        /// <summary>Tests point this at a temp file. null = the real config.json.</summary>
        internal static string ConfigPathOverride;

        /// <summary>Same path BinaConfig.ConfigPath computes (that one is private).</summary>
        public static string DefaultConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitWebAppSync",
            "config.json");

        private static string ConfigPath => ConfigPathOverride ?? DefaultConfigPath;

        // Replace, not Auto: with Auto, Newtonsoft APPENDS a saved list onto the field
        // initialiser, so five default hints plus six saved ones come back as eleven.
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            NullValueHandling = NullValueHandling.Ignore,
        });

        public static CadToBimSettings Load()
        {
            try
            {
                JObject root = ReadRoot();
                if (root != null && root[ConfigKey] is JObject node)
                {
                    CadToBimSettings loaded = node.ToObject<CadToBimSettings>(Serializer);
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception)
            {
                // Unreadable config is the defaults — same policy as BinaConfig.Load().
            }

            return new CadToBimSettings();
        }

        public void Save()
        {
            try
            {
                JObject root = null;
                try { root = ReadRoot(); } catch (Exception) { /* corrupt file: start over */ }
                root ??= new JObject();

                root[ConfigKey] = JObject.FromObject(this, Serializer);

                string directory = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(ConfigPath, root.ToString(Formatting.Indented));
            }
            catch (Exception)
            {
                // Same policy as BinaConfig.Save(): a settings write is never fatal.
            }
        }

        private static JObject ReadRoot()
        {
            if (!File.Exists(ConfigPath)) return null;
            string json = File.ReadAllText(ConfigPath);
            return string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
        }

        /// <summary>The exclusions only. No Include list: the wall-layer include list is
        /// something the detect pass derives per drawing from the hints, and the brush
        /// deliberately reads every layer.</summary>
        public Cad2Bim.LayerFilter ToLayerFilter()
        {
            var filter = new Cad2Bim.LayerFilter();
            filter.Exclude.AddRange(ExcludeGlobs.Where(glob => !string.IsNullOrWhiteSpace(glob)));
            return filter;
        }

        public bool IsWallLayer(string layer) => Mentions(layer, WallLayerHints);

        public bool IsOpeningLayer(string layer) => Mentions(layer, OpeningLayerHints);

        private static bool Mentions(string layer, List<string> words) =>
            !string.IsNullOrEmpty(layer) &&
            words.Any(word => !string.IsNullOrEmpty(word) &&
                              layer.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
```

`BinaConfig.cs` — insert after line 145 (`public int EnginePort { get; set; } = 48820;`), before the `// Shared loopback secret` comment:

```csharp

        // CAD to BIM pane settings (Services/CadToBim/CadToBimSettings.cs) share
        // this file. Held opaque so Save() carries the key through untouched;
        // the settings class does its own read-modify-write on the same path
        // and owns the shape. Never read this from add-in code — use
        // CadToBimSettings.Load().
        [JsonProperty("cadToBim", NullValueHandling = NullValueHandling.Ignore)]
        public Newtonsoft.Json.Linq.JObject CadToBim { get; set; }
```

(`using Newtonsoft.Json;` is already at the top of `BinaConfig.cs`; `JObject` is fully qualified to avoid adding a using to a 650-line file.)

- [ ] **Step 4: Run test to verify it passes**

Run (Mac compile gate): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows`
Expected: Build succeeded (CS8632 nullable-annotation warnings from the engine files are expected; 0 errors).
Run (Windows): `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CadToBimSettingsTests"`  Expected: PASS, 10 tests.

- [ ] **Step 5: Stage**

`git add Services/CadToBim/CadToBimSettings.cs BinaConfig.cs Tests/Tests.csproj Tests/CadToBimSettingsTests.cs`   (repo owner rule: executors STAGE, never commit)


**BinaConfig API facts (develop, `BinaConfig.cs`):** typed class, not a dictionary. `[JsonProperty]` used sparingly (line 22 `costMarkupPct`); most members serialise under their C# names. `ConfigPath` is `private static readonly string` = `%APPDATA%\RevitWebAppSync\config.json` (line 399). `Load()` (line 405) deserialises with `JsonConvert.DeserializeObject<BinaConfig>`, then `LoadBinaCloudTokens()` (Credential Manager), `ApplyDefaults()` (one-time gate, can Save) and `ApplyHeals()` (every load, saves on change). `Save()` (line 627) serialises `this` with `Formatting.Indented` — so ANY key not declared on the class is DROPPED on the next `BinaConfig.Save()`; that is why this task adds the opaque `JObject CadToBim` property. `BinaConfig.cs` is not (and cannot be) linked into Tests. Known, accepted race: a `BinaConfig` instance loaded before `CadToBimSettings.Save()` and saved afterwards writes back the older `cadToBim` node (same class of race every other field already has, since every caller does its own `BinaConfig.Load()`).

---

### Task 14: `ElementIdCompat` + `CadToBimSession`

**Files:**
- Create: `Services/CadToBim/ElementIdCompat.cs` (Revit; NOT linked into Tests)
- Create: `Services/CadToBim/CadToBimSession.cs`
- Modify: `Tests/Tests.csproj` (one `<Compile Include>` line under the marker comment)
- Test: `Tests/CadToBimSessionTests.cs`

**Interfaces:**
- Consumes: `BuildReport` (Task 10, `Dictionary<Cad2Bim.Wall, long> WallIds`); `Cad2Bim.Wall` (reference identity — `Wall` does not override `Equals`, so `HashSet<Wall>`/`Dictionary<Wall,…>` key by reference; `Wall.Centerline.P1/P2`, `Wall.Thickness`); `Cad2Bim.Services.ClassificationService`; `Cad2Bim.CadModel`; `Autodesk.Revit.DB.ElementId` (ElementIdCompat only).
- Produces (verbatim from the reconciled contract):
  ```csharp
  namespace RevitWebAppSync.Services.CadToBim {
    internal static class ElementIdCompat {                      // addin only, NOT linked into Tests
      public static long ToLong(Autodesk.Revit.DB.ElementId id);           // IntegerValue on net48, Value on net8+; null → -1
      public static Autodesk.Revit.DB.ElementId ToElementId(long value);   // int ctor on net48, long ctor on net8+
    }
    public sealed class CadToBimSession {
      public string DrawingPath; public double Scale = 1.0;
      public Cad2Bim.Services.ClassificationService Service;
      public Cad2Bim.CadModel Model;                              // the drawing in mm, for re-elaboration after Erase/Brush
      public List<Cad2Bim.Wall> Walls; public List<Cad2Bim.Opening> Openings; public List<Cad2Bim.Space> Spaces;
      public HashSet<Cad2Bim.Wall> Erased; public List<Cad2Bim.Wall> Forced;
      public Dictionary<Cad2Bim.Wall, long> BuiltWallIds;
      public double MedianThicknessMm;                            // 100 until SetWalls sees a wall
      public int ErasedCount { get; } public int ForcedCount { get; } public int BuiltCount { get; }
      public void SetWalls(List<Cad2Bim.Wall> walls);             // assigns, recomputes median, prunes stale Erased
      public List<Cad2Bim.Wall> Active();                         // (Walls ∪ Forced) − Erased
      public List<Cad2Bim.Wall> Pending();                        // Active() − BuiltWallIds.Keys
      public void ToggleErase(Cad2Bim.Wall w);
      public void AddForced(IEnumerable<Cad2Bim.Wall> ws);        // dedupe by reference
      public void MarkBuilt(BuildReport r);                       // only when r.Ok
      public void CarryForwardFrom(CadToBimSession previous);     // re-detect: Erased + BuiltWallIds re-keyed by CenterlineKey, Forced by identity
      public static string CenterlineKey(Cad2Bim.Wall w);         // mm-rounded, direction-independent
      internal static double Median(IReadOnlyList<Cad2Bim.Wall> walls, double fallback);
    }
  }
  ```

Design points settled here (the view model, Task 17, relies on every one):
- `Active()` = `(Walls ∪ Forced) − Erased`, not `Walls − Erased + Forced`: the literal formula would re-add an erased brushed wall, and the drafter must be able to erase a brush result too.
- `SetWalls` (not field assignment) is how detect output arrives: it recomputes `MedianThicknessMm` and prunes `Erased` entries that point at instances no longer in `Walls ∪ Forced`. `Forced` and `BuiltWallIds` are never pruned.
- A re-detect (thresholds, role chips, settings) makes NEW `Wall` instances. `CarryForwardFrom(previous)` re-keys erased marks and built ids onto the fresh instances by `CenterlineKey` — the mm-rounded centreline endpoints in a direction-independent order — and carries brushed walls (with their erased/built state) by identity. Without this, `Pending()` after a re-detect would rebuild every wall an earlier Confirm already made.
- Erasing after a build hides the wall from the pane; it never removes it from `BuiltWallIds` and never deletes anything — undo is the drafter's tool.

- [ ] **Step 1: Write the failing test**

`Tests/CadToBimSessionTests.cs`:

```csharp
// CadToBimSession — the pane's correction state between detect and Confirm.
// Pinned: erase is a toggle that hides a wall from Active and Pending; brushed
// (Forced) walls join Active and can be erased too; after a build, Pending
// excludes the built walls while Active still shows them; erasing a built wall
// never touches BuiltWallIds (undo is the drafter's tool); the median thickness
// is recomputed when the detect pass hands over a new wall list.
//
// [Collection("Cad2Bim")]: Cad2Bim.Wall's thresholds (SMin/SMax/MinFaceAspect/
// MinFaceLength) are STATIC and mutable. xunit runs test classes in parallel, so
// every class that constructs a Wall or touches those statics shares this
// collection and runs serially.

using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace RevitWebAppSync.Tests
{
    [Collection("Cad2Bim")]
    public class CadToBimSessionTests
    {
        /// <summary>A horizontal wall: faces at y and y + thickness, 0..length along x.</summary>
        private static Wall WallAt(double y, double thickness = 100, double length = 1000) =>
            new(new Segment(new Point(0, y), new Point(length, y)),
                new Segment(new Point(0, y + thickness), new Point(length, y + thickness)));

        private static CadToBimSession Session(params Wall[] walls)
        {
            var session = new CadToBimSession();
            session.SetWalls(walls.ToList());
            return session;
        }

        [Fact]
        public void Erase_RemovesFromActiveAndPending_TwiceRestores()
        {
            Wall a = WallAt(0), b = WallAt(1000);
            var s = Session(a, b);

            s.ToggleErase(a);
            Assert.DoesNotContain(a, s.Active());
            Assert.DoesNotContain(a, s.Pending());
            Assert.Contains(b, s.Active());
            Assert.Equal(1, s.ErasedCount);

            s.ToggleErase(a);
            Assert.Contains(a, s.Active());
            Assert.Contains(a, s.Pending());
            Assert.Equal(0, s.ErasedCount);
        }

        [Fact]
        public void Forced_AppearsInActive_DedupedByReference()
        {
            Wall a = WallAt(0), brushed = WallAt(2000);
            var s = Session(a);

            s.AddForced(new[] { brushed });
            s.AddForced(new[] { brushed });     // same instance again
            s.AddForced(new[] { a });           // already a detected wall

            Assert.Equal(new[] { a, brushed }, s.Active());
            Assert.Equal(1, s.ForcedCount);
            Assert.Single(s.Forced);
        }

        [Fact]
        public void Erase_AppliesToForcedWallsToo()
        {
            Wall brushed = WallAt(2000);
            var s = Session();
            s.AddForced(new[] { brushed });

            s.ToggleErase(brushed);
            Assert.Empty(s.Active());
            Assert.Empty(s.Pending());

            s.ToggleErase(brushed);
            Assert.Equal(new[] { brushed }, s.Active());
        }

        [Fact]
        public void MarkBuilt_PendingExcludesBuilt_ActiveStillIncludes()
        {
            Wall a = WallAt(0), b = WallAt(1000), later = WallAt(2000);
            var s = Session(a, b);

            var report = new BuildReport { Walls = 2 };
            report.WallIds[a] = 1001;
            report.WallIds[b] = 1002;
            s.MarkBuilt(report);

            Assert.Equal(2, s.BuiltCount);
            Assert.Equal(new[] { a, b }, s.Active());
            Assert.Empty(s.Pending());

            s.AddForced(new[] { later });
            Assert.Equal(new[] { later }, s.Pending());      // only the new wall goes to the next Confirm
            Assert.Equal(3, s.Active().Count);
        }

        [Fact]
        public void EraseAfterBuild_KeepsBuiltWallIds()
        {
            Wall a = WallAt(0);
            var s = Session(a);
            var report = new BuildReport();
            report.WallIds[a] = 77;
            s.MarkBuilt(report);

            s.ToggleErase(a);
            Assert.DoesNotContain(a, s.Active());
            Assert.True(s.BuiltWallIds.ContainsKey(a));
            Assert.Equal(77, s.BuiltWallIds[a]);
        }

        [Fact]
        public void MarkBuilt_NullOrFailedReport_ChangesNothing()
        {
            Wall a = WallAt(0);
            var s = Session(a);
            s.MarkBuilt(null);
            s.MarkBuilt(new BuildReport { Error = "rolled back" });
            Assert.Empty(s.BuiltWallIds);
            Assert.Equal(new[] { a }, s.Pending());
        }

        [Fact]
        public void SetWalls_RecomputesMedianThickness()
        {
            var s = new CadToBimSession();
            Assert.Equal(100, s.MedianThicknessMm);                // fallback before any detect

            s.SetWalls(new List<Wall> { WallAt(0, 230), WallAt(1000, 100), WallAt(2000, 100) });
            Assert.Equal(100, s.MedianThicknessMm, 6);

            s.SetWalls(new List<Wall> { WallAt(0, 230), WallAt(1000, 230), WallAt(2000, 100) });
            Assert.Equal(230, s.MedianThicknessMm, 6);

            s.SetWalls(new List<Wall>());
            Assert.Equal(230, s.MedianThicknessMm, 6);             // empty detect keeps the last value
        }

        [Fact]
        public void Median_IsUpperMiddleForEvenCounts_SameAsTheOldCommand()
        {
            var walls = new List<Wall> { WallAt(0, 100), WallAt(1000, 230) };
            Assert.Equal(230, CadToBimSession.Median(walls, 50), 6);
            Assert.Equal(50, CadToBimSession.Median(new List<Wall>(), 50), 6);
        }

        [Fact]
        public void SetWalls_DropsStaleErasures_KeepsForcedAndBuilt()
        {
            Wall old = WallAt(0), brushed = WallAt(3000);
            var s = Session(old);
            s.AddForced(new[] { brushed });
            s.ToggleErase(old);
            s.ToggleErase(brushed);
            var report = new BuildReport();
            report.WallIds[old] = 5;
            s.MarkBuilt(report);

            Wall fresh = WallAt(0);                               // re-detect: new instances
            s.SetWalls(new List<Wall> { fresh });

            Assert.Equal(1, s.ErasedCount);                       // `old` pruned, `brushed` kept
            Assert.Contains(brushed, s.Erased);
            Assert.Single(s.Forced);
            Assert.True(s.BuiltWallIds.ContainsKey(old));         // history is never rewritten here
            Assert.Equal(new[] { fresh }, s.Active());
        }

        [Fact]
        public void ToggleErase_Null_IsIgnored()
        {
            var s = Session(WallAt(0));
            s.ToggleErase(null);
            Assert.Equal(0, s.ErasedCount);

        [Fact]
        public void CenterlineKey_IsDirectionIndependent_AndRoundsToMm()
        {
            Wall leftToRight = new(new Segment(new Point(0, 0), new Point(1000, 0)),
                                   new Segment(new Point(0, 100), new Point(1000, 100)));
            Wall rightToLeft = new(new Segment(new Point(1000.2, 0), new Point(0.3, 0)),
                                   new Segment(new Point(1000.2, 100), new Point(0.3, 100)));
            Assert.Equal("0,50,1000,50", CadToBimSession.CenterlineKey(leftToRight));
            Assert.Equal(CadToBimSession.CenterlineKey(leftToRight), CadToBimSession.CenterlineKey(rightToLeft));
            Assert.NotEqual(CadToBimSession.CenterlineKey(leftToRight), CadToBimSession.CenterlineKey(WallAt(2000)));
        }

        [Fact]
        public void CarryForwardFrom_ReKeysErasedAndBuilt_ByCenterline_AndKeepsForcedByIdentity()
        {
            // First detect: walls a, b; drafter erases a, builds b, brushes in c (then erases it).
            Wall a = WallAt(0), b = WallAt(1000), brushed = WallAt(3000);
            var first = Session(a, b);
            first.ToggleErase(a);
            var report = new BuildReport();
            report.WallIds[b] = 42;
            first.MarkBuilt(report);
            first.AddForced(new[] { brushed });
            first.ToggleErase(brushed);

            // Re-detect: the same two centrelines come back as NEW instances, plus a third wall.
            Wall a2 = WallAt(0), b2 = WallAt(1000), d2 = WallAt(5000);
            var second = new CadToBimSession { DrawingPath = first.DrawingPath };
            second.SetWalls(new List<Wall> { a2, b2, d2 });

            second.CarryForwardFrom(first);

            Assert.Contains(a2, second.Erased);                    // erase mark followed the centreline
            Assert.DoesNotContain(a, second.Erased);               // the stale instance is not dragged along
            Assert.Equal(42, second.BuiltWallIds[b2]);             // built id followed the centreline
            Assert.False(second.BuiltWallIds.ContainsKey(b));
            Assert.Equal(new[] { brushed }, second.Forced);        // brushed walls survive by identity
            Assert.Contains(brushed, second.Erased);               // and so does their erased state
            Assert.Equal(new[] { d2 }, second.Pending());          // only the genuinely new wall goes to the next Confirm
            Assert.Equal(2, second.ErasedCount);

            second.CarryForwardFrom(null);                         // no-op, never throws
            Assert.Equal(2, second.ErasedCount);
        }
        }
    }
}
```
- [ ] **Step 2: Run test to verify it fails**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded" | head`
Expected: FAIL with `error CS0246: The type or namespace name 'CadToBimSession' could not be found` (`BuildReport` already exists from Task 10). Windows: `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~CadToBimSessionTests"` → same.

- [ ] **Step 3: Write minimal implementation**

`Services/CadToBim/ElementIdCompat.cs` (addin only — NOT linked into Tests):

```csharp
// ElementIdCompat — the one place the ElementId value/IntegerValue drift lives.
// net48 builds against Revit 2023 (IntegerValue : int, no Value); net8/net10 build
// against 2025/2027 (Value : long, IntegerValue obsolete). Services/Net48Shims.cs
// also gives net48 an `.Value` extension (int) for the rest of the codebase; this
// class is explicit on purpose so the two ctors are chosen without an implicit cast.

using Autodesk.Revit.DB;

namespace RevitWebAppSync.Services.CadToBim
{
    internal static class ElementIdCompat
    {
        public static long ToLong(ElementId id)
        {
            if (id == null) return -1;
#if NETFRAMEWORK
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        public static ElementId ToElementId(long value)
        {
#if NETFRAMEWORK
            return new ElementId(checked((int)value));
#else
            return new ElementId(value);
#endif
        }
    }
}
```

`Services/CadToBim/CadToBimSession.cs`:

```csharp
// CadToBimSession — everything the pane knows about one drawing between "Detecting…"
// and Confirm: the detect output, the drafter's corrections (Erased, Forced), and
// which walls an earlier Confirm already built. Pure C#: no Revit, no WPF, so the
// Confirm diff (Pending) is unit-tested rather than trusted.
//
// Identity is by reference. Cad2Bim.Wall does not override Equals, and that is what
// we want: a re-detect makes new Wall instances, SetWalls prunes erasures that point
// at instances no longer in play, and CarryForwardFrom re-keys what should survive a
// re-detect (erase marks, built ids) by centreline instead.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim.Services;
using CadWall = Cad2Bim.Wall;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class CadToBimSession
    {
        public string DrawingPath;

        /// <summary>Millimetres per drawing unit, as Units.Resolve decided.</summary>
        public double Scale = 1.0;

        /// <summary>The loaded drawing as the engine's service object (contract compatibility;
        /// the pane re-elaborates from Model instead).</summary>
        public ClassificationService Service;

        /// <summary>The drawing in millimetres (segments, arcs, texts, outlines) so openings and
        /// rooms can be re-derived after an erase or a brush without re-reading the file.</summary>
        public Cad2Bim.CadModel Model;

        public List<CadWall> Walls = new();
        public List<Cad2Bim.Opening> Openings = new();
        public List<Cad2Bim.Space> Spaces = new();

        /// <summary>Walls the drafter clicked away. Toggle, so a mis-click undoes itself.</summary>
        public HashSet<CadWall> Erased = new();

        /// <summary>Walls the brush produced. Independent of the detect pass.</summary>
        public List<CadWall> Forced = new();

        /// <summary>CAD wall -> Revit ElementId value for every wall a Confirm built.
        /// Never pruned here: erasing after a build hides the wall from the pane, it does
        /// not delete it — undo is the drafter's tool for that.</summary>
        public Dictionary<CadWall, long> BuiltWallIds = new();

        /// <summary>Thickness a single-line brush wall is given. The drawing's own
        /// median beats any constant; 100 mm until a detect pass has run.</summary>
        public double MedianThicknessMm = 100.0;

        public int ErasedCount => Erased.Count;
        public int ForcedCount => Forced.Count;
        public int BuiltCount => BuiltWallIds.Count;

        /// <summary>Detect output arrives here. Recomputes the median and drops
        /// erasures that pointed at instances of the previous pass.</summary>
        public void SetWalls(List<CadWall> walls)
        {
            Walls = walls ?? new List<CadWall>();
            MedianThicknessMm = Median(Walls, MedianThicknessMm);

            var live = new HashSet<CadWall>(Walls);
            live.UnionWith(Forced);
            Erased.RemoveWhere(wall => !live.Contains(wall));
        }

        /// <summary>Upper-middle median (same formula the old convert command used:
        /// sorted[count / 2]); the fallback when there are no walls.</summary>
        internal static double Median(IReadOnlyList<CadWall> walls, double fallback)
        {
            if (walls == null || walls.Count == 0) return fallback;

            List<double> thicknesses = walls.Select(wall => wall.Thickness).OrderBy(t => t).ToList();
            return thicknesses[thicknesses.Count / 2];
        }

        /// <summary>(Walls ∪ Forced) − Erased, detected walls first, no duplicates.</summary>
        public List<CadWall> Active()
        {
            var active = new List<CadWall>(Walls.Count + Forced.Count);
            var seen = new HashSet<CadWall>();

            foreach (CadWall wall in Walls)
            {
                if (Erased.Contains(wall) || !seen.Add(wall)) continue;
                active.Add(wall);
            }

            foreach (CadWall wall in Forced)
            {
                if (Erased.Contains(wall) || !seen.Add(wall)) continue;
                active.Add(wall);
            }

            return active;
        }

        /// <summary>What the next Confirm builds: Active() minus what an earlier one built.</summary>
        public List<CadWall> Pending() =>
            Active().Where(wall => !BuiltWallIds.ContainsKey(wall)).ToList();

        public void ToggleErase(CadWall wall)
        {
            if (wall == null) return;
            if (!Erased.Remove(wall)) Erased.Add(wall);
        }

        /// <summary>Brush results. Deduped by reference against Forced and Walls.</summary>
        public void AddForced(IEnumerable<CadWall> walls)
        {
            if (walls == null) return;

            var known = new HashSet<CadWall>(Walls);
            known.UnionWith(Forced);

            foreach (CadWall wall in walls)
            {
                if (wall == null || !known.Add(wall)) continue;
                Forced.Add(wall);
            }
        }

        /// <summary>Records what a build created. A failed report (Error set) was rolled
        /// back, so it records nothing.</summary>
        public void MarkBuilt(BuildReport report)
        {
            if (report == null || !report.Ok || report.WallIds == null) return;

            foreach (KeyValuePair<CadWall, long> pair in report.WallIds)
            {
                if (pair.Key == null) continue;
                BuiltWallIds[pair.Key] = pair.Value;
            }
        }

        /// <summary>
        /// After a re-detect of the SAME drawing: the previous session's erase marks and built
        /// ids follow their centrelines onto this session's new Wall instances; brushed walls
        /// are the drafter's own objects and come across by identity, erased/built state and all.
        /// Nothing from the previous session's Walls list itself is kept — those instances are
        /// dead. Call after SetWalls.
        /// </summary>
        public void CarryForwardFrom(CadToBimSession previous)
        {
            if (previous == null) return;

            var byKey = new Dictionary<string, CadWall>(StringComparer.Ordinal);
            foreach (CadWall wall in Walls) byKey[CenterlineKey(wall)] = wall;

            foreach (CadWall old in previous.Erased)
            {
                if (byKey.TryGetValue(CenterlineKey(old), out CadWall match)) Erased.Add(match);
            }

            foreach (KeyValuePair<CadWall, long> built in previous.BuiltWallIds)
            {
                if (byKey.TryGetValue(CenterlineKey(built.Key), out CadWall match)) BuiltWallIds[match] = built.Value;
            }

            foreach (CadWall forced in previous.Forced)
            {
                AddForced(new[] { forced });
                if (previous.Erased.Contains(forced)) Erased.Add(forced);
                if (previous.BuiltWallIds.TryGetValue(forced, out long id)) BuiltWallIds[forced] = id;
            }
        }

        /// <summary>"x1,y1,x2,y2" of the centreline rounded to whole millimetres, endpoints
        /// ordered so the same wall drawn either way gives the same key.</summary>
        public static string CenterlineKey(CadWall wall)
        {
            long x1 = (long)Math.Round(wall.Centerline.P1.x), y1 = (long)Math.Round(wall.Centerline.P1.y);
            long x2 = (long)Math.Round(wall.Centerline.P2.x), y2 = (long)Math.Round(wall.Centerline.P2.y);
            bool swap = x1 > x2 || (x1 == x2 && y1 > y2);
            return swap ? x2 + "," + y2 + "," + x1 + "," + y1 : x1 + "," + y1 + "," + x2 + "," + y2;
        }
    }
}
```

`Tests/Tests.csproj` — directly after the Task 13 link under the marker comment:

```xml
    <Compile Include="..\Services\CadToBim\CadToBimSession.cs" Link="CadToBim\CadToBimSession.cs" />
```

- [ ] **Step 4: Run test to verify it passes**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows`
Expected: Build succeeded, 0 errors (net48 must compile `ElementIdCompat` against `IntegerValue`; net8/net10 against `Value`).
Run (Windows): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~CadToBimSessionTests"`  Expected: PASS, 12 tests.

- [ ] **Step 5: Stage**

`git add Services/CadToBim/ElementIdCompat.cs Services/CadToBim/CadToBimSession.cs Tests/Tests.csproj Tests/CadToBimSessionTests.cs`   (repo owner rule: executors STAGE, never commit)

---

### Task 15: `Cad2BimBrush` + delete `Cad2BimSelectionCommand.cs`

**Files:**
- Create: `Services/CadToBim/Cad2BimBrush.cs` (also declares `BrushResult`)
- Delete: `Commands/Cad2BimSelectionCommand.cs` (its logic minus Revit is this file; its private `SilenceJoinFailures` copy is superseded by Task 10's)
- Modify: `Tests/Tests.csproj` (one `<Compile Include>` line under the marker comment)
- Test: `Tests/Cad2BimBrushTests.cs`

**Interfaces:**
- Consumes: `BoxMm` (Task 10); `CadToBimSettings.ToLayerFilter()` (Task 13); engine: `Cad2Bim.Services.CadRenderSource.Read(string)`, `Cad2Bim.ModelSource.Read(CadDocument, LayerFilter)`, `Cad2Bim.CadClassifier.MergeCollinearSegments / ClassifyWalls / WallsFromOutlines / DeduplicateWalls / WallFromCloud`, `Cad2Bim.Segment.Midpoint`, statics `Cad2Bim.Wall.MinFaceAspect`, `Cad2Bim.Wall.MinFaceLength` (read `SMin`/`SMax` too — those are left as the detect pass set them).
- Produces:
  ```csharp
  namespace RevitWebAppSync.Services.CadToBim {
    public sealed class BrushResult { public List<Cad2Bim.Wall> Walls = new(); public string Note; public int Paired, SingleLine, Cloud; }
    public static class Cad2BimBrush {
      public const double SingleLineMinLengthMm = 500.0;
      public static BrushResult Run(string drawingPath, BoxMm box, CadToBimSettings settings, double medianThicknessMm);
      internal static BrushResult RunOnModel(Cad2Bim.CadModel model, BoxMm box, double medianThicknessMm);
      internal static Cad2Bim.Wall SingleLineWall(Cad2Bim.Segment line, double thicknessMm);
    }
  }
  ```
- **The 40-wall confirmation moves to the VM**: `Cad2BimBrush` never asks anything. `CadToBimViewModel.BrushAsync` (Task 17) checks `result.Walls.Count > 40` and shows the "That is a lot for one selection… Add them anyway?" prompt BEFORE `Session.AddForced(result.Walls)`; on No it discards the result.

**Slot:** after Tasks 10 and 13, BEFORE Task 12 (which deletes the convert command this file's predecessor references).

- [ ] **Step 1: Write the failing test**

`Tests/Cad2BimBrushTests.cs`:

```csharp
// Cad2BimBrush — "drag a box over what the automatic pass missed". Inside the box
// the guards come off (no aspect test, no minimum face length), a lone line ≥ 500 mm
// becomes a wall at the session's median thickness, and a box full of hatch strokes
// becomes one wall from its extent. Tested on hand-built CadModels through
// RunOnModel; Run(path, …) only adds the DWG read in front of it.
//
// [Collection("Cad2Bim")]: Wall.MinFaceAspect / MinFaceLength / SMin / SMax are static
// mutable — see CadToBimSessionTests. The restore test below asserts the brush puts
// back the PREVIOUS values, not the defaults.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using RevitWebAppSync.Services.CadToBim;
using Xunit;

namespace RevitWebAppSync.Tests
{
    [Collection("Cad2Bim")]
    public class Cad2BimBrushTests
    {
        private static readonly BoxMm Box = new(-100, -100, 2100, 300);

        private static Segment Seg(double x1, double y1, double x2, double y2) =>
            new(new Point(x1, y1), new Point(x2, y2));

        private static CadModel Model(params Segment[] segments)
        {
            var model = new CadModel();
            model.Segments.AddRange(segments);
            return model;
        }

        [Fact]
        public void TwoShortFaces_PairWithGuardsOff()
        {
            // 100 mm pieces 115 apart: the automatic pass rejects them twice over
            // (MinFaceLength 200, aspect 100 < 115 × 1.5). Inside a box they are a wall.
            var r = Cad2BimBrush.RunOnModel(Model(Seg(0, 0, 100, 0), Seg(0, 115, 100, 115)), Box, 100);

            Wall wall = Assert.Single(r.Walls);
            Assert.Equal(115, wall.Thickness, 3);
            Assert.Equal(100, wall.Centerline.Length, 3);
            Assert.Equal(1, r.Paired);
            Assert.Equal(0, r.SingleLine);
            Assert.Equal(0, r.Cloud);
            Assert.Equal("1 walls paired, 0 single-line, 0 cloud", r.Note);
        }

        [Fact]
        public void LoneLongFace_BecomesSingleLineWall_AtMedianThickness()
        {
            var r = Cad2BimBrush.RunOnModel(Model(Seg(0, 0, 800, 0)), Box, medianThicknessMm: 100);

            Wall wall = Assert.Single(r.Walls);
            Assert.Equal(100, wall.Thickness, 6);
            Assert.Equal(800, wall.Centerline.Length, 3);
            Assert.Equal(0, wall.Centerline.P1.y, 6);            // centreline IS the drawn line
            Assert.Equal(1, r.SingleLine);
            Assert.Equal("0 walls paired, 1 single-line, 0 cloud", r.Note);
        }

        [Fact]
        public void LoneShortFace_IsNeitherWallNorCloud()
        {
            // 300 mm < 500 mm single-line floor; two endpoints are not a cloud (< 4 points).
            var r = Cad2BimBrush.RunOnModel(Model(Seg(0, 0, 300, 0)), Box, 100);

            Assert.Empty(r.Walls);
            Assert.Contains("no wall shape", r.Note);
            Assert.Contains("1 lines", r.Note);
        }

        [Fact]
        public void HatchCloud_FallsBackToOneWallFromExtent()
        {
            // 40 strokes, 20 mm long, every one at a different angle (4° apart, so none
            // pairs even with the guards off), filling a 2000 × 150 band.
            var strokes = new List<Segment>();
            for (int i = 0; i < 40; i++)
            {
                double angle = (5 + 4 * i) * Math.PI / 180.0;
                double cx = 25 + 50 * i;
                double cy = i % 2 == 0 ? 10 : 140;
                double dx = 10 * Math.Cos(angle), dy = 10 * Math.Sin(angle);
                strokes.Add(Seg(cx - dx, cy - dy, cx + dx, cy + dy));
            }

            var r = Cad2BimBrush.RunOnModel(Model(strokes.ToArray()), Box, 100);

            Wall wall = Assert.Single(r.Walls);
            Assert.InRange(wall.Thickness, 140, 160);
            Assert.InRange(wall.Centerline.Length, 1800, 2100);
            Assert.Equal(0, r.Paired);
            Assert.Equal(1, r.Cloud);
            Assert.Equal("0 walls paired, 0 single-line, 1 cloud", r.Note);
        }

        [Fact]
        public void EmptyBox_ReportsNoLinework()
        {
            var r = Cad2BimBrush.RunOnModel(Model(Seg(5000, 5000, 6000, 5000)), Box, 100);

            Assert.Empty(r.Walls);
            Assert.Equal("no linework in box", r.Note);
        }

        [Fact]
        public void OnlyLineworkWithMidpointInsideBox_IsRead()
        {
            // Two faces inside, one 800 mm loner outside: exactly one wall.
            var r = Cad2BimBrush.RunOnModel(
                Model(Seg(0, 0, 100, 0), Seg(0, 115, 100, 115), Seg(3000, 0, 3800, 0)), Box, 100);

            Assert.Single(r.Walls);
            Assert.Equal(0, r.SingleLine);
        }

        [Fact]
        public void PochéOutlineInsideBox_IsAWall()
        {
            // A closed 1200 × 150 outline (hatch boundary) with no stroke linework.
            var model = new CadModel();
            model.Outlines.Add(new List<Point>
            {
                new(0, 0), new(1200, 0), new(1200, 150), new(0, 150),
            });

            var r = Cad2BimBrush.RunOnModel(model, Box, 100);

            Wall wall = Assert.Single(r.Walls);
            Assert.Equal(150, wall.Thickness, 3);
            Assert.Equal(1, r.Paired);
        }

        [Fact]
        public void RestoresStaticThresholds_ToTheirPreviousValues()
        {
            double aspectBefore = Wall.MinFaceAspect;
            double minFaceBefore = Wall.MinFaceLength;
            try
            {
                Wall.MinFaceAspect = 7.25;          // deliberately NOT the defaults
                Wall.MinFaceLength = 333;

                Cad2BimBrush.RunOnModel(Model(Seg(0, 0, 100, 0), Seg(0, 115, 100, 115)), Box, 100);

                Assert.Equal(7.25, Wall.MinFaceAspect);
                Assert.Equal(333, Wall.MinFaceLength);
            }
            finally
            {
                Wall.MinFaceAspect = aspectBefore;
                Wall.MinFaceLength = minFaceBefore;
            }
        }

        [Fact]
        public void SingleLineWall_OutsideThicknessRange_IsNull()
        {
            Assert.Null(Cad2BimBrush.SingleLineWall(Seg(0, 0, 800, 0), Wall.SMax + 100));
            Assert.Null(Cad2BimBrush.SingleLineWall(Seg(0, 0, 0, 0), 100));       // zero length
        }

        [Fact]
        public void BoxMm_Normalised_AcceptsAnyTwoCorners_AndContainsIsInclusive()
        {
            var box = BoxMm.Normalised(10, 20, -5, -8);
            Assert.Equal(new BoxMm(-5, -8, 10, 20), box);
            Assert.Equal(15, box.Width);
            Assert.Equal(28, box.Height);
            Assert.True(box.Contains(10, 20));
            Assert.True(box.Contains(new Point(-5, -8)));
            Assert.False(box.Contains(10.001, 0));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded" | head`
Expected: FAIL with `error CS0246: The type or namespace name 'Cad2BimBrush' could not be found` (`BoxMm` already exists from Task 10). Windows: `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimBrushTests"` → same.

- [ ] **Step 3: Write minimal implementation**

`Services/CadToBim/Cad2BimBrush.cs`:

```csharp
// Cad2BimBrush — walls from linework the drafter points at (extracted from the
// former Commands/Cad2BimSelectionCommand.cs, minus everything Revit).
//
// The automatic pass has to be careful, because everything it accepts it accepts
// without being asked: two parallel faces, a minimum length, a face several times
// longer than the wall is thick. Those guards are why it finds about a quarter of
// the linework on a busy drawing. A drafter dragging a box over a wall is telling
// us it is a wall — better evidence than any rule — so inside the box the guards
// come off, a single line becomes a wall in its own right, and if nothing at all
// pairs, the extent of what was selected becomes the wall (exploded poché).
//
// No Revit, no UI. The 40-wall "build them anyway?" question lives in the view model.

using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim;
using Cad2Bim.Services;
using CadPoint = Cad2Bim.Point;
using CadSegment = Cad2Bim.Segment;
using CadWall = Cad2Bim.Wall;

namespace RevitWebAppSync.Services.CadToBim
{
    public sealed class BrushResult
    {
        public List<CadWall> Walls = new();

        /// <summary>One line for the status bar: "12 walls paired, 3 single-line, 0 cloud",
        /// "no linework in box", or why the box yielded nothing.</summary>
        public string Note;

        /// <summary>Paired faces + poché outlines.</summary>
        public int Paired;
        public int SingleLine;
        public int Cloud;
    }

    public static class Cad2BimBrush
    {
        /// <summary>Shortest line worth building a wall along on its own. Below this a
        /// selection box is picking up detail rather than fabric.</summary>
        public const double SingleLineMinLengthMm = 500.0;

        /// <summary>
        /// Re-reads the drawing with the settings' exclusions but NO wall-layer include
        /// list (a wall the automatic pass missed may well sit on a layer not named for
        /// walls), then runs the box.
        /// </summary>
        public static BrushResult Run(string drawingPath, BoxMm box, CadToBimSettings settings,
                                      double medianThicknessMm)
        {
            LayerFilter filter = (settings ?? new CadToBimSettings()).ToLayerFilter();
            CadModel model = ModelSource.Read(CadRenderSource.Read(drawingPath), filter);
            return RunOnModel(model, box, medianThicknessMm);
        }

        /// <summary>The box, on a model already in millimetres. Wall.SMin/SMax are read as
        /// the detect pass left them; MinFaceAspect and MinFaceLength are zeroed for the
        /// duration and restored to whatever they were.</summary>
        internal static BrushResult RunOnModel(CadModel model, BoxMm box, double medianThicknessMm)
        {
            var result = new BrushResult();

            List<CadSegment> inside = model.Segments
                .Where(segment => box.Contains(CadSegment.Midpoint(segment)))
                .ToList();

            List<List<CadPoint>> outlines = model.Outlines
                .Where(outline => outline.Count >= 4 && box.Contains(Centroid(outline)))
                .ToList();

            if (inside.Count == 0 && outlines.Count == 0)
            {
                result.Note = "no linework in box";
                return result;
            }

            List<CadSegment> faces;
            List<CadWall> walls;

            double aspect = CadWall.MinFaceAspect;
            double minFace = CadWall.MinFaceLength;
            try
            {
                // The drafter's box is the evidence, so the guards that stand in for
                // evidence are not needed inside it.
                CadWall.MinFaceAspect = 0;
                CadWall.MinFaceLength = 0;

                faces = CadClassifier.MergeCollinearSegments(inside);
                walls = CadClassifier.ClassifyWalls(faces);
                walls.AddRange(CadClassifier.WallsFromOutlines(outlines));
                walls = CadClassifier.DeduplicateWalls(walls);
            }
            finally
            {
                CadWall.MinFaceAspect = aspect;
                CadWall.MinFaceLength = minFace;
            }

            result.Paired = walls.Count;

            // What is left over becomes a wall on its own line: inside a box the drafter
            // drew, a line that pairs with nothing is a wall drawn as one line, not a stray.
            var used = new HashSet<CadSegment>(walls.SelectMany(wall => wall.Geometry).OfType<CadSegment>());
            foreach (CadSegment face in faces)
            {
                if (used.Contains(face) || face.Length < SingleLineMinLengthMm) continue;

                CadWall single = SingleLineWall(face, medianThicknessMm);
                if (single == null) continue;

                walls.Add(single);
                result.SingleLine++;
            }

            var points = new List<CadPoint>(inside.Count * 2);
            foreach (CadSegment segment in inside)
            {
                points.Add(segment.P1);
                points.Add(segment.P2);
            }
            foreach (List<CadPoint> outline in outlines) points.AddRange(outline);

            if (walls.Count == 0)
            {
                // Nothing paired and nothing ran long enough to stand alone: exploded
                // poché. The drafter has just drawn a box round it, so the extent of the
                // marks becomes the wall — narrow side thickness, long axis centreline.
                CadWall boxed = CadClassifier.WallFromCloud(points);
                if (boxed != null)
                {
                    walls.Add(boxed);
                    result.Cloud = 1;
                }
            }

            result.Walls = walls;
            result.Note = walls.Count == 0
                ? inside.Count + " lines in box, but no wall shape - their extent is " + Extent(points) +
                  ". Try a box that follows one wall rather than a room."
                : result.Paired + " walls paired, " + result.SingleLine + " single-line, " +
                  result.Cloud + " cloud";

            return result;
        }

        /// <summary>A wall from one line: the line is the centreline, given the thickness
        /// the rest of the drawing uses. null if that thickness is outside Wall.SMin..SMax
        /// or the line has no length.</summary>
        internal static CadWall SingleLineWall(CadSegment line, double thicknessMm)
        {
            double length = line.Length;
            if (length <= 0) return null;

            double half = thicknessMm / 2.0;
            double dx = (line.P2.x - line.P1.x) / length;
            double dy = (line.P2.y - line.P1.y) / length;

            CadSegment Offset(double side) => new CadSegment(
                new CadPoint(line.P1.x + (-dy * half * side), line.P1.y + (dx * half * side)),
                new CadPoint(line.P2.x + (-dy * half * side), line.P2.y + (dx * half * side)));

            try
            {
                return new CadWall(Offset(1), Offset(-1));
            }
            catch (ArgumentException)
            {
                // Outside the thickness range the model is set to accept.
                return null;
            }
        }

        private static CadPoint Centroid(List<CadPoint> outline)
        {
            double x = 0, y = 0;
            foreach (CadPoint point in outline) { x += point.x; y += point.y; }
            return new CadPoint(x / outline.Count, y / outline.Count);
        }

        private static string Extent(List<CadPoint> points)
        {
            if (points.Count == 0) return "empty";

            double minX = points.Min(p => p.x), maxX = points.Max(p => p.x);
            double minY = points.Min(p => p.y), maxY = points.Max(p => p.y);
            return (maxX - minX).ToString("0") + " by " + (maxY - minY).ToString("0") + " mm";
        }
    }
}
```

Engine facts this relies on (verified on `origin/feat/cad2bim`): `Segment.Midpoint(Segment)` is public static; `Wall.Geometry` is `List<GeometryElement>` holding the two face `Segment`s that `ClassifyWalls` paired (the same instances `MergeCollinearSegments` returned, so the `used` set works by reference); `WallFromCloud` returns null for < 4 points, width outside `SMin..SMax`, or length < 2.5 × width; `Wall(e1, e2)` throws `ArgumentException` when not parallel or thickness out of range; `MergeCollinearSegments` clusters by direction (2°) and offset (25 mm), so parallel faces 115 mm apart stay separate and 40 strokes 4° apart stay 40 faces.

`Tests/Tests.csproj` — directly after the Task 14 link under the marker comment:

```xml
    <Compile Include="..\Services\CadToBim\Cad2BimBrush.cs" Link="CadToBim\Cad2BimBrush.cs" />
```

- [ ] **Step 3b: Delete the selection command**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && git rm Commands/Cad2BimSelectionCommand.cs`

What the deletion removes and where it now lives: the box-selection read (`ToDrawingX/Y` feet → drawing mm via the last placement origin) → gone, the pane's viewport hands over drawing-mm `BoxMm` directly (Task 16); the guards-off pairing, single-line fallback and cloud fallback → `Cad2BimBrush.RunOnModel`; the "no wall shape" / "no linework" TaskDialogs → `BrushResult.Note`; the 40-wall TaskDialog → `CadToBimViewModel.BrushAsync` (Task 17); the wall creation → `Cad2BimBuilder` (Task 11) on the next Confirm; its private `SilenceJoinFailures` → `Services/CadToBim/SilenceJoinFailures.cs` (Task 10); its `"Cad2BimSelection"` ribbon button → already dropped from `App.cs` in Task 1. Deliberate behaviour changes: poché outlines inside the box are read too (`WallsFromOutlines`, counted in `Paired`).

- [ ] **Step 4: Run test to verify it passes**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows && ! grep -rn "Cad2BimSelectionCommand" --include="*.cs" --include="*.addin" --include="*.csproj" . | grep -v "/bin/\|/obj/\|/.worktrees/\|/.claude/"`
Expected: Build succeeded ×4, 0 errors; the grep prints nothing (the `!` makes the pipeline succeed only when nothing references the deleted command).
Run (Windows): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~Cad2BimBrushTests"`  Expected: PASS, 10 tests.

Hand-check for the executor if `HatchCloud_FallsBackToOneWallFromExtent` is the one that fails: the strokes' normalised directions (dx ≥ 0) are 5°,9°,…,89°,−87°,…,−19° — all 4° apart, so `isParallelTo` (2° tolerance via cross-product) is false for every pair and `ClassifyWalls` pairs nothing; `WallFromCloud` scans 90 angles at 2° steps and the narrowest box is at 0° (≈1969 × 149.98 mm), which passes `SMin ≤ 149.98 ≤ SMax` and `1969 ≥ 2.5 × 149.98`. If it fails, the first thing to check is that `Wall.SMin/SMax` are still 50/400 — another test class (outside the `Cad2Bim` collection) changed them in parallel.

- [ ] **Step 5: Stage**

`git add Services/CadToBim/Cad2BimBrush.cs Tests/Tests.csproj Tests/Cad2BimBrushTests.cs` (the `git rm` in Step 3b already staged the deletion)   (repo owner rule: executors STAGE, never commit)

---

### Task 16: `CadOverlayViewport` — overlay drawing, hit-test, Erase/Brush mouse modes

**Files:**
- Create: `UI/CadToBim/CadOverlayViewport.cs`
- Modify: `Tests/Tests.csproj` (one `<Compile Include>` line under the marker comment Task 4 added)
- Test: `Tests/CadOverlayViewportTests.cs`

**Interfaces:**
- Consumes: `Cad2Bim.Views.Rendering.CadViewport` (DPs `LayersSource : IEnumerable<LayerViewModel>`, `ContentBounds : Rect`, `Backdrop : Brush`; private `MatrixTransform transform` shared by every layer `DrawingVisual`); `Cad2Bim.SegmentIndex(IReadOnlyList<Segment>, double cellSize)` + `Near(Segment, double margin) : IEnumerable<int>`; `Cad2Bim.Wall/Opening/Space/Arc/Segment/Point`; `RevitWebAppSync.Services.CadToBim.BoxMm(MinX, MinY, MaxX, MaxY)`.
- Produces:
  ```csharp
  namespace RevitWebAppSync.UI.CadToBim {
    public enum ViewportMode { Pan, Erase, Brush }
    public sealed class CadOverlayViewport : System.Windows.Controls.Grid {
      public const double EraseToleranceMm = 150.0;
      public static readonly DependencyProperty ModeProperty, LayersSourceProperty, ContentBoundsProperty, BackdropProperty;
      public ViewportMode Mode { get; set; }                       // two-way by default
      public IEnumerable<Cad2Bim.ViewModels.LayerViewModel> LayersSource { get; set; }
      public Rect ContentBounds { get; set; }  public Brush Backdrop { get; set; }
      public CadViewport Viewport { get; }
      public event Action<Cad2Bim.Wall> WallClicked;  public event Action<BoxMm> BoxDragged;  public event Action<double, double> CursorMoved; // mm
      public void SetOverlay(IReadOnlyList<Cad2Bim.Wall> active, IReadOnlyList<Cad2Bim.Wall> erased, IReadOnlyList<Cad2Bim.Opening> openings, IReadOnlyList<Cad2Bim.Space> spaces, IReadOnlyList<BoxMm> boxes);
      public Cad2Bim.Wall HitTestWall(System.Windows.Point screenPt, double toleranceMm);
      public void Redraw();                                        // after a theme swap
      internal static Cad2Bim.Wall NearestWall(IReadOnlyList<Cad2Bim.Wall> walls, Cad2Bim.SegmentIndex index, double xMm, double yMm, double tolMm);
      internal static double DistanceToSegment(double xMm, double yMm, Cad2Bim.Segment s);
    }
  }
  ```

- [ ] **Step 1: Write the failing test**

`Tests/CadOverlayViewportTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using RevitWebAppSync.UI.CadToBim;
using CadWall = Cad2Bim.Wall;
using CadPoint = Cad2Bim.Point;
using CadSegment = Cad2Bim.Segment;

namespace Tests
{
    // Pure geometry behind CadOverlayViewport.HitTestWall. The WPF surface itself (visuals,
    // mouse capture) is covered by the Windows smoke row in Task 18.
    [Collection("Cad2Bim")]
    public class CadOverlayViewportTests
    {
        // Two parallel faces `thickness` apart around the requested centreline; Wall's own ctor
        // then derives Centerline == (x1,y1)-(x2,y2). Thickness 100 sits inside Wall.SMin/SMax.
        internal static CadWall WallAt(double x1, double y1, double x2, double y2, double thickness = 100)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double nx = -dy / len * thickness / 2, ny = dx / len * thickness / 2;
            return new CadWall(
                new CadSegment(new CadPoint(x1 + nx, y1 + ny), new CadPoint(x2 + nx, y2 + ny)),
                new CadSegment(new CadPoint(x1 - nx, y1 - ny), new CadPoint(x2 - nx, y2 - ny)));
        }

        private static List<CadWall> Plan() => new List<CadWall>
        {
            WallAt(0, 0, 9000, 0),          // A: bottom external
            WallAt(0, 4300, 4200, 4300),    // B: internal
            WallAt(0, 200, 9000, 200),      // C: 200 mm above A
        };

        [Fact]
        public void Nearest_wall_within_tolerance_is_returned()
        {
            var walls = Plan();
            CadWall hit = CadOverlayViewport.NearestWall(walls, null, 2000, 4390, 150);
            Assert.Same(walls[1], hit);
        }

        [Fact]
        public void Nothing_within_tolerance_returns_null()
        {
            Assert.Null(CadOverlayViewport.NearestWall(Plan(), null, 2000, 2000, 150));
        }

        [Fact]
        public void Closest_of_two_candidates_wins()
        {
            var walls = Plan();
            // 140 mm from A, 60 mm from C: both inside the 150 mm tolerance, C is nearer.
            CadWall hit = CadOverlayViewport.NearestWall(walls, null, 1000, 140, 150);
            Assert.Same(walls[2], hit);
        }

        [Fact]
        public void Spatial_index_and_linear_scan_agree()
        {
            var walls = Plan();
            var index = new Cad2Bim.SegmentIndex(walls.Select(w => w.Centerline).ToList(), 1000);
            foreach (var (x, y) in new[] { (2000.0, 4390.0), (1000.0, 140.0), (8990.0, -100.0), (5000.0, 3000.0) })
            {
                Assert.Same(
                    CadOverlayViewport.NearestWall(walls, null, x, y, 150),
                    CadOverlayViewport.NearestWall(walls, index, x, y, 150));
            }
        }

        [Fact]
        public void Distance_clamps_to_the_segment_ends()
        {
            var s = new CadSegment(new CadPoint(0, 0), new CadPoint(9000, 0));
            Assert.Equal(1000, CadOverlayViewport.DistanceToSegment(10000, 0, s), 6);
            Assert.Equal(300, CadOverlayViewport.DistanceToSegment(4500, 300, s), 6);
            Assert.Equal(0, CadOverlayViewport.DistanceToSegment(4500, 0, s), 6);
        }

        [Fact]
        public void Empty_wall_list_never_hits()
        {
            Assert.Null(CadOverlayViewport.NearestWall(new List<CadWall>(), null, 0, 0, 150));
            Assert.Null(CadOverlayViewport.NearestWall(null, null, 0, 0, 150));
        }
    }
}
```

`Tests/Tests.csproj` — directly after the Task 15 link under the marker comment `<!-- CAD to BIM pane sources (Revit-free) — later tasks append here -->`:

```xml
    <!-- CAD to BIM pane UI that carries no Revit type (WPF only; CadViewport + shapes +
         LayerViewModel are linked above by Task 4). -->
    <Compile Include="..\UI\CadToBim\CadOverlayViewport.cs" Link="CadToBim\CadOverlayViewport.cs" />
```

(The engine, `CadViewport.cs`, the `<Using>` globals and the ACadSharp package were all linked once in Task 4; nothing else is added here.)

**Why composition, not a subclass or partial:** `Cad2Bim.Views.Rendering.CadViewport` is `public sealed class CadViewport : FrameworkElement` (311 lines, kept verbatim) and keeps its one `MatrixTransform` in `private readonly MatrixTransform transform`. The wrapper is a `Grid` whose first child is the untouched viewport and second child a transparent overlay host; the CAD→screen matrix is the viewport's own `MatrixTransform` instance — the same object it assigns to every layer `DrawingVisual` (`new DrawingVisual { Transform = transform }`) — read through `VisualTreeHelper.GetChild(viewport, 0)`, with a reflection fallback on the field name `transform` before any layer exists. mm→screen is `(s·x + tx, −s·y + ty)` (Y flipped), so text under the shared transform would render mirrored — room labels are a separate screen-space visual. Pan/zoom mutate `transform.Matrix` → `Freezable.Changed` fires → the overlay re-renders pens after a 100 ms settle, labels immediately. The viewport pans on left/middle drag and zooms on wheel in the bubbling `OnMouse*` overrides; the wrapper intercepts only the LEFT button in `OnPreviewMouse*` (tunnelling) when `Mode != Pan`, so middle-button pan and wheel zoom keep working in Erase/Brush.

- [ ] **Step 2: Run test to verify it fails**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded" | head`
Expected: FAIL with `error CS0246: The type or namespace name 'CadOverlayViewport' could not be found` (plus CS2001 "Source file '..\UI\CadToBim\CadOverlayViewport.cs' could not be found").
Run (Windows): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~CadOverlayViewportTests"` Expected: FAIL, same compile error.

- [ ] **Step 3: Write minimal implementation**

`UI/CadToBim/CadOverlayViewport.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cad2Bim;
using Cad2Bim.ViewModels;
using Cad2Bim.Views.Rendering;
using RevitWebAppSync.Services.CadToBim;
using CadWall = Cad2Bim.Wall;
using CadPoint = Cad2Bim.Point;
using CadSegment = Cad2Bim.Segment;
using CadArc = Cad2Bim.Arc;
using Point = System.Windows.Point;

namespace RevitWebAppSync.UI.CadToBim
{
    public enum ViewportMode { Pan, Erase, Brush }

    /// <summary>
    /// The upstream CadViewport (sealed, kept verbatim) plus an overlay drawn ABOVE its layer
    /// visuals: wall bands, erased walls, openings, rooms, brush boxes and the live drag box.
    ///
    /// Composition, not inheritance. The viewport is the first child of this Grid, the overlay
    /// host the second, and the overlay's DrawingVisuals share the viewport's own MatrixTransform
    /// instance (the one it assigns to every layer visual), so pan and zoom move the overlay for
    /// free; only pen widths are re-rendered once a zoom settles, exactly as the viewport does.
    ///
    /// Mouse: in Pan mode nothing here touches the mouse. In Erase and Brush modes the LEFT
    /// button is taken in the Preview (tunnelling) phase and marked handled, so the viewport
    /// never starts a pan; middle-button pan and wheel zoom still reach the viewport untouched.
    /// </summary>
    public sealed class CadOverlayViewport : Grid
    {
        public const double EraseToleranceMm = 150.0;

        private const double MinDragPx = 4.0;
        private const double WallCenterPx = 2.0;
        private const double WallHoverPx = 4.0;
        private const double WallBandMinPx = 3.0;
        private const byte WallBandAlpha = 0x2E;
        private const double OpeningPx = 2.2;
        private const double RoomPx = 1.0;
        private const double ErasePx = 1.8;
        private const double BoxPx = 1.2;
        private const byte BoxFillAlpha = 0x24;
        private const double LabelPx = 10.5;
        private const int ArcSteps = 12;

        public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
            nameof(Mode), typeof(ViewportMode), typeof(CadOverlayViewport),
            new FrameworkPropertyMetadata(ViewportMode.Pan, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) => ((CadOverlayViewport)d).OnModeChanged()));

        public static readonly DependencyProperty LayersSourceProperty = DependencyProperty.Register(
            nameof(LayersSource), typeof(IEnumerable<LayerViewModel>), typeof(CadOverlayViewport),
            new PropertyMetadata(null, (d, e) => ((CadOverlayViewport)d).viewport.LayersSource = (IEnumerable<LayerViewModel>)e.NewValue));

        public static readonly DependencyProperty ContentBoundsProperty = DependencyProperty.Register(
            nameof(ContentBounds), typeof(Rect), typeof(CadOverlayViewport),
            new PropertyMetadata(Rect.Empty, (d, e) => ((CadOverlayViewport)d).viewport.ContentBounds = (Rect)e.NewValue));

        public static readonly DependencyProperty BackdropProperty = DependencyProperty.Register(
            nameof(Backdrop), typeof(Brush), typeof(CadOverlayViewport),
            new PropertyMetadata(null, (d, e) => ((CadOverlayViewport)d).viewport.Backdrop = (Brush)e.NewValue ?? Brushes.White));

        public event Action<CadWall> WallClicked;
        public event Action<BoxMm> BoxDragged;
        /// <summary>Cursor position in drawing millimetres, for the coords readout.</summary>
        public event Action<double, double> CursorMoved;

        private static readonly Typeface LabelFace =
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        private readonly CadViewport viewport;
        private readonly OverlayHost overlay;
        private readonly DispatcherTimer settleTimer;
        private MatrixTransform shared;
        private double lastScale;

        private IReadOnlyList<CadWall> active = new CadWall[0];
        private IReadOnlyList<CadWall> erased = new CadWall[0];
        private IReadOnlyList<Opening> openings = new Opening[0];
        private IReadOnlyList<Space> spaces = new Space[0];
        private IReadOnlyList<BoxMm> boxes = new BoxMm[0];
        private SegmentIndex activeIndex;
        private CadWall hover;

        private bool pressed;
        private bool dragging;
        private Point dragStart;

        public CadOverlayViewport()
        {
            viewport = new CadViewport { Backdrop = Brushes.White };
            overlay = new OverlayHost();
            Children.Add(viewport);
            Children.Add(overlay);

            settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            settleTimer.Tick += (_, __) => { settleTimer.Stop(); RedrawAll(); };
        }

        public ViewportMode Mode
        {
            get => (ViewportMode)GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        public IEnumerable<LayerViewModel> LayersSource
        {
            get => (IEnumerable<LayerViewModel>)GetValue(LayersSourceProperty);
            set => SetValue(LayersSourceProperty, value);
        }

        public Rect ContentBounds
        {
            get => (Rect)GetValue(ContentBoundsProperty);
            set => SetValue(ContentBoundsProperty, value);
        }

        public Brush Backdrop
        {
            get => (Brush)GetValue(BackdropProperty);
            set => SetValue(BackdropProperty, value);
        }

        /// <summary>The wrapped upstream viewport (pan/zoom/layers live there).</summary>
        public CadViewport Viewport => viewport;

        // ───────── public surface ─────────

        public void SetOverlay(IReadOnlyList<CadWall> activeWalls, IReadOnlyList<CadWall> erasedWalls,
                               IReadOnlyList<Opening> openingList, IReadOnlyList<Space> spaceList,
                               IReadOnlyList<BoxMm> boxList)
        {
            active = activeWalls ?? new CadWall[0];
            erased = erasedWalls ?? new CadWall[0];
            openings = openingList ?? new Opening[0];
            spaces = spaceList ?? new Space[0];
            boxes = boxList ?? new BoxMm[0];
            activeIndex = active.Count > 0
                ? new SegmentIndex(active.Select(w => w.Centerline).ToList(), 1000)
                : null;
            hover = null;
            RedrawAll();
        }

        public CadWall HitTestWall(Point screenPt, double toleranceMm)
        {
            Point? mm = ToMm(screenPt);
            if (!mm.HasValue) return null;
            return NearestWall(active, activeIndex, mm.Value.X, mm.Value.Y, toleranceMm);
        }

        /// <summary>Re-render every overlay visual (call after a theme swap).</summary>
        public void Redraw() => RedrawAll();

        /// <summary>
        /// The active wall whose centreline passes within <paramref name="tolMm"/> of the point,
        /// nearest first. <paramref name="index"/> must have been built over the SAME list in the
        /// same order (it yields list indices); null = plain scan.
        /// </summary>
        internal static CadWall NearestWall(IReadOnlyList<CadWall> walls, SegmentIndex index,
                                            double xMm, double yMm, double tolMm)
        {
            if (walls == null || walls.Count == 0 || tolMm < 0) return null;

            IEnumerable<int> candidates = index != null
                ? index.Near(new CadSegment(new CadPoint(xMm, yMm), new CadPoint(xMm, yMm)), tolMm)
                : Enumerable.Range(0, walls.Count);

            CadWall best = null;
            double bestDistance = tolMm;
            bool found = false;

            foreach (int i in candidates)
            {
                if (i < 0 || i >= walls.Count) continue;
                double d = DistanceToSegment(xMm, yMm, walls[i].Centerline);
                if (d > tolMm) continue;
                if (found && d >= bestDistance) continue;
                best = walls[i];
                bestDistance = d;
                found = true;
            }

            return best;
        }

        /// <summary>Perpendicular distance clamped to the segment's own extent.</summary>
        internal static double DistanceToSegment(double x, double y, CadSegment s)
        {
            double ax = s.P1.x, ay = s.P1.y, bx = s.P2.x, by = s.P2.y;
            double dx = bx - ax, dy = by - ay;
            double len2 = (dx * dx) + (dy * dy);
            double t = len2 <= 0 ? 0 : (((x - ax) * dx) + ((y - ay) * dy)) / len2;
            if (t < 0) t = 0;
            else if (t > 1) t = 1;
            double px = ax + (t * dx) - x;
            double py = ay + (t * dy) - y;
            return Math.Sqrt((px * px) + (py * py));
        }

        // ───────── shared transform ─────────

        private MatrixTransform Shared()
        {
            if (shared != null) return shared;

            MatrixTransform found = null;
            if (VisualTreeHelper.GetChildrenCount(viewport) > 0 &&
                VisualTreeHelper.GetChild(viewport, 0) is DrawingVisual layerVisual)
            {
                found = layerVisual.Transform as MatrixTransform;
            }

            if (found == null)
            {
                // No layer visual yet (nothing bound, or an empty collection): the viewport keeps
                // the one MatrixTransform it hands every layer in a private readonly field.
                FieldInfo field = typeof(CadViewport).GetField("transform", BindingFlags.Instance | BindingFlags.NonPublic);
                found = field?.GetValue(viewport) as MatrixTransform;
            }

            if (found == null) return null;

            shared = found;
            lastScale = shared.Matrix.M11;
            shared.Changed += OnTransformChanged;
            overlay.Attach(shared);
            return shared;
        }

        private void OnTransformChanged(object sender, EventArgs e)
        {
            double scale = shared.Matrix.M11;
            if (Math.Abs(scale - lastScale) > 1e-12)
            {
                // Zoom: pen widths are in CAD units, re-render once it settles (viewport does the same).
                lastScale = scale;
                settleTimer.Stop();
                settleTimer.Start();
            }

            // Room names are screen-space text; they follow every pan immediately.
            RedrawLabels();
        }

        private Point? ToMm(Point screen)
        {
            MatrixTransform t = Shared();
            if (t == null) return null;
            Matrix m = t.Matrix;
            if (!m.HasInverse) return null;
            m.Invert();
            return m.Transform(screen);
        }

        private BoxMm? ToBoxMm(Rect screenRect)
        {
            Point? a = ToMm(screenRect.TopLeft);
            Point? b = ToMm(screenRect.BottomRight);
            if (!a.HasValue || !b.HasValue) return null;
            return new BoxMm(
                Math.Min(a.Value.X, b.Value.X), Math.Min(a.Value.Y, b.Value.Y),
                Math.Max(a.Value.X, b.Value.X), Math.Max(a.Value.Y, b.Value.Y));
        }

        private static Point P(CadPoint p) => new Point(p.x, p.y);

        // ───────── mouse ─────────

        private void OnModeChanged()
        {
            pressed = false;
            dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            hover = null;
            overlay.SetDrag(null, Colors.Transparent);
            Cursor = Mode == ViewportMode.Pan ? Cursors.Hand : Cursors.Cross;
            RedrawWalls();
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (Mode == ViewportMode.Pan)
            {
                base.OnPreviewMouseLeftButtonDown(e);
                return;
            }

            pressed = true;
            dragging = false;
            dragStart = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;    // the viewport never sees it, so no pan starts
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            Point position = e.GetPosition(this);
            Point? mm = ToMm(position);
            if (mm.HasValue) CursorMoved?.Invoke(mm.Value.X, mm.Value.Y);

            if (Mode == ViewportMode.Pan)
            {
                base.OnPreviewMouseMove(e);
                return;
            }

            if (Mode == ViewportMode.Brush && pressed)
            {
                if (!dragging &&
                    (Math.Abs(position.X - dragStart.X) >= MinDragPx || Math.Abs(position.Y - dragStart.Y) >= MinDragPx))
                {
                    dragging = true;
                }

                if (dragging)
                {
                    overlay.SetDrag(RectOf(dragStart, position), Colour("CadToBim.Brush", 0x12, 0xA3, 0xB4));
                }

                e.Handled = true;
                return;
            }

            if (Mode == ViewportMode.Erase && !pressed)
            {
                CadWall under = HitTestWall(position, EraseToleranceMm);
                if (!ReferenceEquals(under, hover))
                {
                    hover = under;
                    RedrawWalls();
                }
                Cursor = under != null ? Cursors.Hand : Cursors.Cross;
            }
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!pressed)
            {
                base.OnPreviewMouseLeftButtonUp(e);
                return;
            }

            pressed = false;
            ReleaseMouseCapture();
            e.Handled = true;
            Point position = e.GetPosition(this);

            if (Mode == ViewportMode.Erase)
            {
                CadWall wall = HitTestWall(position, EraseToleranceMm);
                if (wall != null) WallClicked?.Invoke(wall);
                return;
            }

            if (Mode == ViewportMode.Brush)
            {
                overlay.SetDrag(null, Colors.Transparent);
                if (!dragging) return;
                dragging = false;

                Rect box = RectOf(dragStart, position);
                if (box.Width < MinDragPx || box.Height < MinDragPx) return;

                BoxMm? mmBox = ToBoxMm(box);
                if (mmBox.HasValue) BoxDragged?.Invoke(mmBox.Value);
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            pressed = false;
            dragging = false;
            overlay.SetDrag(null, Colors.Transparent);
            base.OnLostMouseCapture(e);
        }

        private static Rect RectOf(Point a, Point b) =>
            new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)),
                     new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));

        // ───────── rendering ─────────

        private double CurrentScale()
        {
            MatrixTransform t = Shared();
            return t == null ? 0 : Math.Max(Math.Abs(t.Matrix.M11), 1e-6);
        }

        private void RedrawAll()
        {
            double scale = CurrentScale();
            if (scale <= 0) return;
            RedrawWalls(scale);
            RedrawErased(scale);
            RedrawOpenings(scale);
            RedrawRooms(scale);
            RedrawBoxes(scale);
            RedrawLabels();
        }

        private void RedrawWalls()
        {
            double scale = CurrentScale();
            if (scale > 0) RedrawWalls(scale);
        }

        private void RedrawWalls(double scale)
        {
            Color wall = Colour("CadToBim.Wall", 0x1E, 0x9E, 0x4F);
            SolidColorBrush bandBrush = Frozen(Color.FromArgb(WallBandAlpha, wall.R, wall.G, wall.B));
            SolidColorBrush lineBrush = Frozen(wall);
            Pen centre = FrozenPen(lineBrush, WallCenterPx / scale);
            Pen hot = FrozenPen(lineBrush, WallHoverPx / scale);
            var bands = new Dictionary<int, Pen>();

            using (DrawingContext dc = overlay.Walls.RenderOpen())
            {
                foreach (CadWall w in active)
                {
                    Point a = P(w.Centerline.P1), b = P(w.Centerline.P2);

                    // Band = real thickness, but never thinner than 3 px on screen.
                    int key = (int)Math.Round(Math.Max(w.Thickness, WallBandMinPx / scale));
                    Pen band;
                    if (!bands.TryGetValue(key, out band))
                    {
                        band = FrozenPen(bandBrush, key);
                        bands[key] = band;
                    }

                    dc.DrawLine(band, a, b);
                    dc.DrawLine(ReferenceEquals(w, hover) ? hot : centre, a, b);
                }
            }
        }

        private void RedrawErased(double scale)
        {
            Pen pen = Dashed(Frozen(Colour("CadToBim.Erase", 0xD1, 0x3B, 0x3B)), ErasePx / scale, 6, 4);
            using (DrawingContext dc = overlay.Erased.RenderOpen())
            {
                foreach (CadWall w in erased)
                {
                    dc.DrawLine(pen, P(w.Centerline.P1), P(w.Centerline.P2));
                }
            }
        }

        private void RedrawOpenings(double scale)
        {
            Pen pen = FrozenPen(Frozen(Colour("CadToBim.Opening", 0x2B, 0x7D, 0xE9)), OpeningPx / scale);
            using (DrawingContext dc = overlay.Openings.RenderOpen())
            {
                foreach (Opening o in openings)
                {
                    foreach (GeometryElement g in o.Geometry)
                    {
                        if (g is CadSegment s) dc.DrawLine(pen, P(s.P1), P(s.P2));
                        else if (g is CadArc arc) DrawArc(dc, pen, arc);
                    }
                }
            }
        }

        // Tessellated rather than an ArcSegment: the shared transform flips Y, and a polyline
        // needs no reasoning about sweep direction under a mirror.
        private static void DrawArc(DrawingContext dc, Pen pen, CadArc arc)
        {
            double sweep = arc.SweepDegrees * Math.PI / 180.0;
            if (sweep <= 0) sweep = 2 * Math.PI;

            Point previous = P(arc.PointAt(arc.StartAngle));
            for (int i = 1; i <= ArcSteps; i++)
            {
                Point next = P(arc.PointAt(arc.StartAngle + (sweep * i / ArcSteps)));
                dc.DrawLine(pen, previous, next);
                previous = next;
            }

            // The leaf: hinge to the swing's start.
            dc.DrawLine(pen, P(arc.Center), P(arc.StartPoint));
        }

        private void RedrawRooms(double scale)
        {
            Pen pen = Dashed(Frozen(Colour("CadToBim.Room", 0xC7, 0x7D, 0x0A)), RoomPx / scale, 3, 4);
            using (DrawingContext dc = overlay.Rooms.RenderOpen())
            {
                foreach (Space s in spaces)
                {
                    List<CadPoint> boundary = s.Boundary;
                    if (boundary == null || boundary.Count < 3) continue;
                    for (int i = 0; i < boundary.Count; i++)
                    {
                        dc.DrawLine(pen, P(boundary[i]), P(boundary[(i + 1) % boundary.Count]));
                    }
                }
            }
        }

        private void RedrawBoxes(double scale)
        {
            Color c = Colour("CadToBim.Brush", 0x12, 0xA3, 0xB4);
            Brush fill = Frozen(Color.FromArgb(BoxFillAlpha, c.R, c.G, c.B));
            Pen pen = Dashed(Frozen(c), BoxPx / scale, 5, 3);
            using (DrawingContext dc = overlay.Boxes.RenderOpen())
            {
                foreach (BoxMm b in boxes)
                {
                    dc.DrawRectangle(fill, pen, new Rect(new Point(b.MinX, b.MinY), new Point(b.MaxX, b.MaxY)));
                }
            }
        }

        // Screen-space: FormattedText under the Y-flipped shared transform would render mirrored.
        private void RedrawLabels()
        {
            using (DrawingContext dc = overlay.Labels.RenderOpen())
            {
                if (shared == null) return;
                Matrix m = shared.Matrix;
                Brush brush = Frozen(Colour("CadToBim.Room", 0xC7, 0x7D, 0x0A));
                double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

                foreach (Space s in spaces)
                {
                    if (string.IsNullOrWhiteSpace(s.Name) || s.Boundary == null || s.Boundary.Count < 3) continue;

                    Point centre = m.Transform(new Point(s.Boundary.Average(p => p.x), s.Boundary.Average(p => p.y)));
                    if (centre.X < -200 || centre.Y < -200 || centre.X > ActualWidth + 200 || centre.Y > ActualHeight + 200) continue;

                    var text = new FormattedText("◆ " + s.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                                 LabelFace, LabelPx, brush, pixelsPerDip);
                    dc.DrawText(text, new Point(centre.X - (text.Width / 2), centre.Y - (text.Height / 2)));
                }
            }
        }

        // ───────── colours ─────────

        // Reads the CadToBim.* brush mounted on the panel (local theme dictionary, see
        // CadToBimTheme); falls back to the light mockup value when hosted elsewhere.
        private Color Colour(string key, byte r, byte g, byte b)
        {
            if (TryFindResource(key) is SolidColorBrush brush) return brush.Color;
            return Color.FromRgb(r, g, b);
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static Pen FrozenPen(Brush brush, double width)
        {
            var pen = new Pen(brush, width);
            pen.Freeze();
            return pen;
        }

        // Dash lengths are multiples of the pen width, so they stay screen-constant like the width.
        private static Pen Dashed(Brush brush, double width, double on, double off)
        {
            var pen = new Pen(brush, width)
            {
                DashStyle = new DashStyle(new double[] { on, off }, 0),
                DashCap = PenLineCap.Flat,
            };
            pen.Freeze();
            return pen;
        }

        // ───────── overlay host ─────────

        /// <summary>
        /// Holds the overlay DrawingVisuals (bottom → top: boxes, rooms, openings, erased, walls,
        /// labels) and paints the live drag box in screen space. Never hit-test visible: every
        /// mouse decision is made by the Grid above in the Preview phase.
        /// </summary>
        private sealed class OverlayHost : FrameworkElement
        {
            private readonly VisualCollection visuals;
            private Rect? drag;
            private Pen dragPen;
            private Brush dragFill;

            public DrawingVisual Boxes { get; } = new DrawingVisual();
            public DrawingVisual Rooms { get; } = new DrawingVisual();
            public DrawingVisual Openings { get; } = new DrawingVisual();
            public DrawingVisual Erased { get; } = new DrawingVisual();
            public DrawingVisual Walls { get; } = new DrawingVisual();
            public DrawingVisual Labels { get; } = new DrawingVisual();

            public OverlayHost()
            {
                IsHitTestVisible = false;
                visuals = new VisualCollection(this) { Boxes, Rooms, Openings, Erased, Walls, Labels };
            }

            public void Attach(MatrixTransform transform)
            {
                Boxes.Transform = transform;
                Rooms.Transform = transform;
                Openings.Transform = transform;
                Erased.Transform = transform;
                Walls.Transform = transform;
                // Labels stay in screen space on purpose.
            }

            public void SetDrag(Rect? rect, Color color)
            {
                drag = rect;
                if (rect.HasValue)
                {
                    var stroke = new SolidColorBrush(color);
                    stroke.Freeze();
                    dragPen = Dashed(stroke, BoxPx, 5, 3);
                    dragFill = Frozen(Color.FromArgb(BoxFillAlpha, color.R, color.G, color.B));
                }
                InvalidateVisual();
            }

            protected override int VisualChildrenCount => visuals.Count;

            protected override Visual GetVisualChild(int index) => visuals[index];

            protected override void OnRender(DrawingContext dc)
            {
                if (drag.HasValue) dc.DrawRectangle(dragFill, dragPen, drag.Value);
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded" | head`  Expected: `Build succeeded` (0 errors).
Run (Windows): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~CadOverlayViewportTests"` Expected: PASS (6 tests).
Also: `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows` Expected: `Build succeeded` ×3 (0 errors; CS8632 nullable warnings from engine files are expected).

- [ ] **Step 5: Stage**

`git add UI/CadToBim/CadOverlayViewport.cs Tests/CadOverlayViewportTests.cs Tests/Tests.csproj`   (repo owner rule: executors STAGE, never commit)

---

### Task 17: `CadToBimViewModel` — detect, correct, brush, confirm

**Files:**
- Create: `UI/CadToBim/CadToBimViewModel.cs`
- Modify: `Tests/Tests.csproj` (one `<Compile Include>` line under the marker comment)
- Test: `Tests/CadToBimViewModelTests.cs`

**Interfaces:**
- Consumes (Tasks 10, 13, 14, 15, 16): `BuildTarget`, `StoreyMode`, `BoxMm`, `BuildRequest` (`long? LevelId`), `BuildReport`, `IBuildRequestSink`, `IBuildEventRaiser`, `BrushResult`, `Cad2BimBrush.Run(string, BoxMm, CadToBimSettings, double)`, `CadToBimSettings` (`SMinMm`, `SMaxMm`, `WallHeightMm`, `WallLayerHints`, `OpeningLayerHints`, `ExcludeGlobs`, `TemplatePath`, `Load()`, `Save()`, `ToLayerFilter()`), `CadToBimSession` (`DrawingPath`, `Scale`, `Model`, `Walls`, `Openings`, `Spaces`, `Erased`, `Forced`, `BuiltWallIds`, `MedianThicknessMm`, `SetWalls()`, `Active()`, `Pending()`, `ToggleErase()`, `AddForced()`, `MarkBuilt()`, `CarryForwardFrom()`). Engine: `CadRenderSource.Read/Walk`, `ICadSink`, `ModelSource.Read`, `LayerFilter` (+ static `Matches`), `CadModel`, `CadClassifier.{MergeCollinearSegments, ClassifyWalls, ClassifyWallsElsewhere, WallsFromOutlines, DeduplicateWalls, CreateTopologicalPoints, ClassifySpaces, SplitWalls, ClassifyOpeningsFromSymbols, ClusterPlans}`, `Units.FromHeader`, `PolylineShape`, `LayerViewModel`, `ViewModelBase`; ACadSharp `CadDocument.Classes` (`DxfClass.DxfName`).
- Produces:
  ```csharp
  namespace RevitWebAppSync.UI.CadToBim {
    public enum LayerRole { Other, Wall, Opening, Ignore }
    public sealed class CadLayerViewModel : Cad2Bim.ViewModels.LayerViewModel { public CadLayerViewModel(string name, int count, LayerRole role); public int Count { get; } public LayerRole Role { get; set; } public string RoleLabel { get; } }
    public sealed class LevelChoice { public string Name; public long Id; }   // ElementId value; filled by CadToBimPanel.RefreshLevels (Task 18)
    public sealed class OverlaySnapshot { public static readonly OverlaySnapshot Empty; IReadOnlyList<Wall> Active, Erased; IReadOnlyList<Opening> Openings; IReadOnlyList<Space> Spaces; IReadOnlyList<BoxMm> Boxes; }
    public sealed class DetectResult { public CadToBimSession Session; public Dictionary<string, List<object>> LayerShapes; public IReadOnlyDictionary<string,int> Census; public Rect Bounds; public int PlanCount, Entities, UnpairedWallLines; public double ReadSeconds; public string UnitsText; }
    public delegate DetectResult DetectDelegate(string path, CadToBimSettings settings, double sMinMm, double sMaxMm, IReadOnlyDictionary<string, LayerRole> roles, IProgress<string> progress, CancellationToken ct);
    public sealed class CadToBimViewModel : Cad2Bim.ViewModels.ViewModelBase {
      public const int BrushConfirmAbove = 40;
      public const string UnsupportedSourceMessage;   // "This drawing was saved by Civil 3D / AutoCAD Architecture / AutoCAD MEP. Run EXPORTTOAUTOCAD in AutoCAD and open the exported file."
      public CadToBimViewModel(IBuildRequestSink handler, IBuildEventRaiser raiser, CadToBimSettings settings = null, DetectDelegate detect = null, Action<Action> onUi = null);
      public Task OpenAsync(string path); public void ToggleErase(Wall w); public Task BrushAsync(BoxMm box); public void ResolveBrushConfirm(bool accept);
      public void Confirm(); public void Redetect(); public void Cancel(); public void ApplySettings(CadToBimSettings s); public void CycleRole(CadLayerViewModel layer); public void SetLevels(IEnumerable<LevelChoice> levels);
      public CadToBimSession Session { get; } public ObservableCollection<LayerViewModel> Layers { get; } public Rect Bounds { get; }
      public string Status { get; } public bool Busy { get; } public bool HasSession { get; }
      public BuildTarget Target { get; set; } public StoreyMode Storeys { get; set; } public bool IsAddToProject { get; }
      public double HeightMm { get; set; } public double SMinMm { get; set; } public double SMaxMm { get; set; }
      public int WallCount, DoorCount, WindowCount, RoomCount, ErasedCount, ForcedCount { get; }   // properties (private set)
      public string ConfirmLabel { get; } public bool CanConfirm { get; } public string Warning { get; }
      public string DrawingPath, DrawingName, UnitsText, ReadText, PlanText { get; } public int PlanCount { get; }
      public ObservableCollection<LevelChoice> Levels { get; } public LevelChoice SelectedLevel { get; set; }
      public OverlaySnapshot Overlay { get; } public BrushResult PendingBrushConfirm { get; }
      public Func<string, bool> OverwritePrompt { get; set; }   // view answers; null = overwrite
      public CadToBimSettings Settings { get; }
      public static DetectResult Detect(string path, CadToBimSettings settings, double sMinMm, double sMaxMm, IReadOnlyDictionary<string, LayerRole> roles, IProgress<string> progress, CancellationToken ct);
      internal static string UnsupportedSource(IEnumerable<string> dxfClassNames);
      internal static LayerRole RoleOf(string layer, CadToBimSettings settings, IReadOnlyDictionary<string, LayerRole> overrides);
      internal static string Describe(Exception ex);
    }
  }
  ```

Design points (all verified against the engine on `origin/feat/cad2bim`):
- The constructor takes the two Revit-free seams because `Tests/Tests.csproj` references only `RevitAPI` (DB), never `RevitAPIUI`, so `ExternalEvent` / `ExternalEventRequest` / `IExternalEventHandler` cannot appear in this file. The panel (Task 18) passes `App.CadToBimBuildHandler` and `new ExternalEventRaiser(App.CadToBimBuildEvent)`.
- Detection follows `Cad2BimConvertCommand.Execute` verbatim (two-pass `ModelSource.Read`, `MergeCollinearSegments` → `ClassifyWalls` → `ClassifyWallsElsewhere` → `WallsFromOutlines` → `DeduplicateWalls`), and elaboration makes the same three static calls `ClassificationService.Elaborate` makes plus `ClassifyOpeningsFromSymbols` (the command's opening path), on the walls actually in play. `Session.Model` (the `CadModel` in mm) is what makes re-elaboration after Erase/Brush possible without re-reading the file; `Session.Service` stays for contract compatibility but is never read here.
- `CadRenderSource.Flatten`/`Walk` output is in raw drawing units, not mm — `ModelSource.Read` rescales the classifier's model by `CadModel.Scale` but the viewport shapes are not rescaled (the branch's debug viewer has this latent mismatch for non-mm drawings). `LayerSink` multiplies every vertex by `model.Scale` so linework and overlay share one coordinate space.
- `LayerViewModel` is not sealed; `CadLayerViewModel` subclasses it (adds `Role`, `Count`, `RoleLabel`) so `ObservableCollection<LayerViewModel> Layers` still matches the contract and the viewport binding. `Layers` is replaced wholesale per detect — adding 30 layers one by one would make `CadViewport.RebuildLayers` re-batch 25k polylines 30 times.
- Counts are properties with private setters (XAML binds them), never fields.
- **Civil 3D / AutoCAD Architecture / MEP**: the engine has no detection (`git grep -i EXPORTTOAUTOCAD origin/feat/cad2bim` is empty; `CadRenderSource.Read` is a bare `DwgReader`/`DxfReader`). `Detect` scans `document.Classes` for `AECC_`/`AEC_`/`AECB_` prefixes — the same rule as `BinaVibe/Mcp/Tools/CadFileReader.DetectSource` on `origin/feat/cad-segment-stitching` — and throws `NotSupportedException(UnsupportedSourceMessage)`, which `OpenAsync` puts in `Status` verbatim (smoke row 5).
- Re-detection makes new `Wall` objects; `Apply` calls `Session.CarryForwardFrom(previous)` (Task 14) when the path is unchanged, so erase marks and built ids follow their centrelines and brushed walls survive by identity.
- Elaboration after Erase/Brush runs synchronously on the UI thread (`CreateTopologicalPoints` + `ClassifySpaces` + `ClassifyOpeningsFromSymbols` over active walls). Fine for a few thousand walls; if a 50k-segment sheet makes a click feel slow, move `RecountAndRedraw`'s `Elaborate` behind `Task.Run` with a generation counter.
- Window lines for `ClassifyOpeningsFromSymbols` are every segment on an `Opening`-role layer (door layers included). The engine's `Merge` prefers a door over an opening at the same spot, so a door layer feeding "window" lines yields no duplicate.
- `Cad2BimBrush.Run` takes the drawing PATH (contract) and therefore re-parses the DWG on every brush; acceptable for v1.

- [ ] **Step 1: Write the failing test**

`Tests/CadToBimViewModelTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim;
using CadWall = Cad2Bim.Wall;

namespace Tests
{
    // The view model is driven here without Revit: the build seam is two interfaces, detection is
    // a delegate, and "marshal to the UI thread" runs inline because no WPF Application exists.
    [Collection("Cad2Bim")]
    public class CadToBimViewModelTests
    {
        private sealed class FakeSink : IBuildRequestSink
        {
            public BuildRequest Request { get; set; }
            public event Action<BuildReport> Completed;
            public void Fire(BuildReport report) => Completed?.Invoke(report);
        }

        private sealed class FakeRaiser : IBuildEventRaiser
        {
            public int Calls;
            public bool Accept = true;
            public bool Raise() { Calls++; return Accept; }
        }

        private const string Dwg = "/tmp/taman-desa/A-101 Unit Plan.dwg";

        private static DetectResult TwoWalls(string path)
        {
            var session = new CadToBimSession
            {
                DrawingPath = path,
                Scale = 1.0,
                Model = new Cad2Bim.CadModel(),
                MedianThicknessMm = 100,
            };
            session.Walls.Add(CadOverlayViewportTests.WallAt(0, 0, 9000, 0));
            session.Walls.Add(CadOverlayViewportTests.WallAt(0, 4300, 4200, 4300));
            return new DetectResult
            {
                Session = session,
                LayerShapes = new Dictionary<string, List<object>>(),
                Census = new Dictionary<string, int> { ["A-WALL"] = 4 },
                Bounds = new System.Windows.Rect(0, 0, 9000, 4300),
                PlanCount = 1,
                Entities = 4,
                ReadSeconds = 0.01,
                UnitsText = "1 unit = 1 mm · header",
            };
        }

        private static (CadToBimViewModel vm, FakeSink sink, FakeRaiser raiser) Make()
        {
            var sink = new FakeSink();
            var raiser = new FakeRaiser();
            var vm = new CadToBimViewModel(sink, raiser, new CadToBimSettings(),
                (path, settings, sMin, sMax, roles, progress, ct) => TwoWalls(path));
            return (vm, sink, raiser);
        }

        [Fact]
        public void Confirm_without_a_session_asks_for_a_dwg()
        {
            var (vm, _, raiser) = Make();
            vm.Confirm();
            Assert.Equal("Open a DWG first", vm.Status);
            Assert.Equal(0, raiser.Calls);
            Assert.False(vm.Busy);
        }

        [Fact]
        public async Task Open_fills_counts_and_the_confirm_label()
        {
            var (vm, _, _) = Make();
            await vm.OpenAsync(Dwg);
            Assert.True(vm.HasSession);
            Assert.False(vm.Busy);
            Assert.Equal(2, vm.WallCount);
            Assert.Equal("A-101 Unit Plan.dwg", vm.DrawingName);
            Assert.Equal("Confirm — build 2 walls", vm.ConfirmLabel);
            Assert.True(vm.CanConfirm);
            Assert.Single(vm.Layers);
        }

        [Fact]
        public async Task Confirm_with_pending_walls_raises_once_and_locks_the_pane()
        {
            var (vm, sink, raiser) = Make();
            await vm.OpenAsync(Dwg);

            vm.Confirm();

            Assert.Equal(1, raiser.Calls);
            Assert.True(vm.Busy);
            Assert.NotNull(sink.Request);
            Assert.Equal(2, sink.Request.Walls.Count);
            Assert.Equal(BuildTarget.AddToProject, sink.Request.Target);
            Assert.Null(sink.Request.OutputPath);
            Assert.Equal(Dwg, sink.Request.DrawingPath);

            vm.Confirm();                       // refused while a build is in flight
            Assert.Equal(1, raiser.Calls);
        }

        [Fact]
        public async Task Build_error_lands_in_status_and_unlocks()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Confirm();

            sink.Fire(new BuildReport { Error = "This model has no basic wall type to use." });

            Assert.False(vm.Busy);
            Assert.Contains("no basic wall type", vm.Status);
            Assert.Equal(2, vm.WallCount);      // nothing was marked built
        }

        [Fact]
        public async Task Refused_raise_says_revit_is_busy()
        {
            var (vm, _, raiser) = Make();
            raiser.Accept = false;
            await vm.OpenAsync(Dwg);

            vm.Confirm();

            Assert.Equal("Revit is busy, try again", vm.Status);
            Assert.False(vm.Busy);
        }

        [Fact]
        public async Task New_file_target_puts_the_rvt_beside_the_dwg()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.Target = BuildTarget.NewFile;

            vm.Confirm();

            Assert.Equal(Path.ChangeExtension(Dwg, ".rvt"), sink.Request.OutputPath);
            Assert.Equal(BuildTarget.NewFile, sink.Request.Target);
            Assert.Null(sink.Request.LevelId);
        }

        [Fact]
        public async Task Erase_drops_the_wall_count_by_one_and_restores_on_second_click()
        {
            var (vm, _, _) = Make();
            await vm.OpenAsync(Dwg);
            CadWall first = vm.Session.Walls[0];

            vm.ToggleErase(first);
            Assert.Equal(1, vm.WallCount);
            Assert.Equal(1, vm.ErasedCount);
            Assert.Single(vm.Overlay.Erased);
            Assert.Equal("Confirm — build 1 wall", vm.ConfirmLabel);

            vm.ToggleErase(first);
            Assert.Equal(2, vm.WallCount);
            Assert.Equal(0, vm.ErasedCount);
        }

        [Fact]
        public async Task Erased_walls_are_left_out_of_the_build_request()
        {
            var (vm, sink, _) = Make();
            await vm.OpenAsync(Dwg);
            vm.ToggleErase(vm.Session.Walls[1]);

            vm.Confirm();

            Assert.Single(sink.Request.Walls);
            Assert.Same(vm.Session.Walls[0], sink.Request.Walls[0]);
        }

        [Fact]
        public async Task Detect_failure_puts_the_innermost_message_in_status()
        {
            var sink = new FakeSink();
            var vm = new CadToBimViewModel(sink, new FakeRaiser(), new CadToBimSettings(),
                (path, settings, sMin, sMax, roles, progress, ct) =>
                    throw new InvalidOperationException("outer", new IOException("Header is not a DWG")));

            await vm.OpenAsync(Dwg);

            Assert.False(vm.Busy);
            Assert.False(vm.HasSession);
            Assert.StartsWith("Header is not a DWG", vm.Status);
        }

        [Fact]
        public void Role_defaults_come_from_the_settings_hints()
        {
            var settings = new CadToBimSettings();
            var none = new Dictionary<string, LayerRole>();
            Assert.Equal(LayerRole.Wall, CadToBimViewModel.RoleOf("A-WALL", settings, none));
            Assert.Equal(LayerRole.Wall, CadToBimViewModel.RoleOf("Dinding-Bata", settings, none));
            Assert.Equal(LayerRole.Opening, CadToBimViewModel.RoleOf("A-GLAZ", settings, none));
            Assert.Equal(LayerRole.Ignore, CadToBimViewModel.RoleOf("PERABUT", settings, none));
            Assert.Equal(LayerRole.Ignore, CadToBimViewModel.RoleOf("A-DIMS", settings, none));
            Assert.Equal(LayerRole.Other, CadToBimViewModel.RoleOf("0", settings, none));
            var forced = new Dictionary<string, LayerRole> { ["0"] = LayerRole.Wall };
            Assert.Equal(LayerRole.Wall, CadToBimViewModel.RoleOf("0", settings, forced));
        }

        [Fact]
        public void Civil3d_and_aec_classes_are_refused_with_the_exporttoautocad_text()
        {
            Assert.Null(CadToBimViewModel.UnsupportedSource(null));
            Assert.Null(CadToBimViewModel.UnsupportedSource(new[] { "ACDBPLACEHOLDER", "SCALE", "TABLESTYLE" }));
            Assert.Equal(CadToBimViewModel.UnsupportedSourceMessage, CadToBimViewModel.UnsupportedSource(new[] { "SCALE", "AECC_PIPE" }));
            Assert.Equal(CadToBimViewModel.UnsupportedSourceMessage, CadToBimViewModel.UnsupportedSource(new[] { "aec_wall" }));   // case-insensitive, like CadFileReader
            Assert.Equal(CadToBimViewModel.UnsupportedSourceMessage, CadToBimViewModel.UnsupportedSource(new[] { "AECB_DUCT" }));
            Assert.Contains("EXPORTTOAUTOCAD", CadToBimViewModel.UnsupportedSourceMessage);
        }

        [Fact]
        public async Task Unsupported_source_lands_in_status_verbatim_and_leaves_no_session()
        {
            // Detect throws exactly what the real Detect throws for a Civil 3D file.
            var vm = new CadToBimViewModel(new FakeSink(), new FakeRaiser(), new CadToBimSettings(),
                (path, settings, sMin, sMax, roles, progress, ct) =>
                    throw new NotSupportedException(CadToBimViewModel.UnsupportedSourceMessage));

            await vm.OpenAsync(Dwg);

            Assert.False(vm.Busy);
            Assert.False(vm.HasSession);
            Assert.Equal(CadToBimViewModel.UnsupportedSourceMessage, vm.Status);   // not doubled by Describe
            Assert.False(vm.CanConfirm);
        }
    }
}
```

`Tests/Tests.csproj` — directly after the Task 16 link under the marker comment:

```xml
    <Compile Include="..\UI\CadToBim\CadToBimViewModel.cs" Link="CadToBim\CadToBimViewModel.cs" />
```

- [ ] **Step 2: Run test to verify it fails**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded" | head`
Expected: FAIL with `error CS0246: The type or namespace name 'CadToBimViewModel' could not be found` (and `DetectResult`, `LayerRole`; plus CS2001 for the missing source file). `IBuildRequestSink`/`IBuildEventRaiser` already exist from Task 10.
Run (Windows): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~CadToBimViewModelTests"` Expected: FAIL, same compile errors.

- [ ] **Step 3: Write minimal implementation**

`UI/CadToBim/CadToBimViewModel.cs`:
```csharp
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

            try
            {
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
                BrushResult result = await Task.Run(() => Cad2BimBrush.Run(path, box, settings, median));

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
```

- [ ] **Step 4: Run test to verify it passes**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded" | head`  Expected: `Build succeeded` (0 errors).
Run (Windows): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~CadToBimViewModelTests|FullyQualifiedName~CadOverlayViewportTests"`  Expected: PASS (12 + 6 tests).
Then the addin: `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows`  Expected: `Build succeeded` ×3.

- [ ] **Step 5: Stage**

`git add UI/CadToBim/CadToBimViewModel.cs Tests/CadToBimViewModelTests.cs Tests/Tests.csproj`   (repo owner rule: executors STAGE, never commit)

---

### Task 18: `CadToBimPanel` — XAML matching the mockup, tokens, theme, code-behind

**Files:**
- Create: `UI/CadToBim/CadToBimTokens.xaml`
- Create: `UI/CadToBim/CadToBimStyles.xaml`
- Create: `UI/CadToBim/CadToBimTheme.cs`
- Create: `UI/CadToBim/CadToBimPanel.xaml`
- Create: `UI/CadToBim/CadToBimPanel.xaml.cs`
- Modify: `Tests/XamlResourceScopeTests.cs:40-48` (three `InlineData` rows + the CadToBim scope union)
- Test: `Tests/XamlResourceScopeTests.cs` (existing text-level test; the panel itself is verified by the Windows smoke row at the end of this task)


**Slot:** after Tasks 12, 16, 17. The panel's gear opens `CadToBimSettingsWindow` (Task 19), so this task's ADD-IN compile gate goes green only at the end of Task 19; run the Tests compile here and the add-in build there.

**Interfaces:**
- Consumes: Task 16 `CadOverlayViewport` (`Mode`, `LayersSource`, `ContentBounds`, `Backdrop`, `WallClicked`, `BoxDragged`, `CursorMoved`, `SetOverlay`, `Redraw`); Task 17 `CadToBimViewModel` (+ `CadLayerViewModel`, `OverlaySnapshot`, `LevelChoice`), `ExternalEventRaiser`; `RevitWebAppSync.UI.Copilot.CopilotTheme` (`EnsureLoaded()`, `NewThemeDictionary()`, `IsDark`, `ThemeChanged`); `RevitWebAppSync.UI.Copilot.Controls.{BoolToVisibilityConverter, EnumEqualsConverter, NotEmptyToVisibilityConverter}` (`UI/Copilot/Controls/CopilotConverters.cs`); `Helpers.WpfAppBootstrap.Ensure()`; `Services.RevitWindowOwner.SetOwner(Window, UIApplication)`; `App.UiApp`, `App.CadToBimBuildHandler`, `App.CadToBimBuildEvent`; `Cad2Bim.Views.Controls.NumericScrubBox` (template parts `PART_Display`, `PART_ValueText`, `PART_Editor`; DPs `Value`, `Minimum`, `Maximum`, `Step`, `Label`, `Format`).
- Produces:
  ```csharp
  namespace RevitWebAppSync.UI.CadToBim {
    public static class CadToBimTheme { public static void EnsureLoaded(); public static ResourceDictionary NewThemeDictionary(); }
    public partial class CadToBimPanel : System.Windows.Controls.UserControl {
      public CadToBimPanel();                                                   // App.CadToBimBuildHandler / App.CadToBimBuildEvent
      public CadToBimPanel(CadToBimBuildHandler handler, Autodesk.Revit.UI.ExternalEvent buildEvent);
      public CadToBimViewModel ViewModel { get; }
      public void RefreshLevels(Autodesk.Revit.DB.Document doc);              // call from a valid API context (OpenCadToBimCommand)
    }
  }
  ```
  Resource keys (tokens): `CadToBim.Wall`, `CadToBim.Opening`, `CadToBim.Room`, `CadToBim.Erase`, `CadToBim.Brush`. Styles: `CadToBim.SectionTitle`, `CadToBim.Section`, `CadToBim.Tool`, `CadToBim.Float`, `CadToBim.Button`, `CadToBim.Primary`, `CadToBim.Chip`, `CadToBim.Radio`, `CadToBim.Mono`, implicit `NumericScrubBox`.

- [ ] **Step 1: Write the failing test**

`Tests/XamlResourceScopeTests.cs` — add three rows to the `[Theory]` (after line 42 `[InlineData("UI/Copilot/Controls/PromptBar.xaml")]`):
```csharp
        [InlineData("UI/CadToBim/CadToBimPanel.xaml")]
        [InlineData("UI/CadToBim/CadToBimStyles.xaml")]
        [InlineData("UI/CadToBim/CadToBimSettingsWindow.xaml")]
```
and add the CadToBim scope. New static array next to `AppScope`:
```csharp
        // Dictionaries the CAD to BIM panel and settings window merge into their OWN resources
        // (CadToBimStyles nests CadToBimTokens) — visible from any UI/CadToBim XAML.
        private static readonly string[] CadToBimScope =
        {
            "UI/CadToBim/CadToBimTokens.xaml", "UI/CadToBim/CadToBimStyles.xaml",
        };
```
and directly after the line `foreach (var d in AppScope) visible.UnionWith(KeysIn(Path.Combine(root, d)));`:
```csharp
            if (rel.StartsWith("UI/CadToBim/"))
                foreach (var d in CadToBimScope) visible.UnionWith(KeysIn(Path.Combine(root, d)));
```

- [ ] **Step 2: Run test to verify it fails**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded"` Expected: `Build succeeded` (the test is data-driven; it fails at run time).
Run (Windows): `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~XamlResourceScopeTests"` Expected: FAIL — the three new rows throw `FileNotFoundException: .../UI/CadToBim/CadToBimPanel.xaml` (the settings-window row goes green in Task 19).

- [ ] **Step 3: Write minimal implementation**

`UI/CadToBim/CadToBimTokens.xaml` (light values from the approved mockup; the dark column lives in `CadToBimTheme`):
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- CAD to BIM overlay palette (scratchpad cad-to-bim-pane.html, light):
         wall #1E9E4F · door/window #2B7DE9 · room #C77D0A · erased #D13B3B · brushed #12A3B4.
         Everything else the pane uses is an existing Cp.* token. Dark values: CadToBimTheme. -->
    <SolidColorBrush x:Key="CadToBim.Wall"    Color="#1E9E4F"/>
    <SolidColorBrush x:Key="CadToBim.Opening" Color="#2B7DE9"/>
    <SolidColorBrush x:Key="CadToBim.Room"    Color="#C77D0A"/>
    <SolidColorBrush x:Key="CadToBim.Erase"   Color="#D13B3B"/>
    <SolidColorBrush x:Key="CadToBim.Brush"   Color="#12A3B4"/>
</ResourceDictionary>
```

`UI/CadToBim/CadToBimStyles.xaml`:
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:cad="clr-namespace:RevitWebAppSync.UI.CadToBim"
                    xmlns:scrub="clr-namespace:Cad2Bim.Views.Controls">

    <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="CadToBimTokens.xaml"/>
    </ResourceDictionary.MergedDictionaries>

    <!-- Sidebar section: 9,11 padding, 1px rule underneath (mockup .sec). -->
    <Style x:Key="CadToBim.Section" TargetType="Border">
        <Setter Property="Padding" Value="11,9"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Cp.Line}"/>
        <Setter Property="BorderThickness" Value="0,0,0,1"/>
    </Style>

    <!-- Section heading: 10.5px, letter-spaced caps (write the text in caps; WPF has no text-transform). -->
    <Style x:Key="CadToBim.SectionTitle" TargetType="TextBlock">
        <Setter Property="FontSize" Value="10.5"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Foreground" Value="{DynamicResource Cp.Faint}"/>
        <Setter Property="Margin" Value="0,0,0,6"/>
    </Style>

    <Style x:Key="CadToBim.Mono" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{DynamicResource Cp.FontMono}"/>
        <Setter Property="FontSize" Value="11.5"/>
        <Setter Property="Foreground" Value="{DynamicResource Cp.Ink}"/>
    </Style>

    <!-- Floating card over the viewport (tools, legend, coords). -->
    <Style x:Key="CadToBim.Float" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource Cp.Bg}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Cp.Line}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CornerRadius" Value="5"/>
        <Setter Property="Padding" Value="3"/>
        <Setter Property="Effect">
            <Setter.Value>
                <DropShadowEffect Color="#000000" Opacity="0.08" BlurRadius="4" ShadowDepth="1" Direction="270"/>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Viewport tool (Pan / Erase / Brush): RadioButton drawn as a segmented toggle. -->
    <Style x:Key="CadToBim.Tool" TargetType="RadioButton">
        <Setter Property="Foreground" Value="{DynamicResource Cp.Ink}"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Padding" Value="9,4"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="RadioButton">
                    <Border x:Name="bd" Background="Transparent" BorderBrush="Transparent" BorderThickness="1"
                            CornerRadius="3" Padding="{TemplateBinding Padding}">
                        <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="{DynamicResource Cp.Hover}"/>
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="{DynamicResource Cp.BlueSoft}"/>
                            <Setter TargetName="bd" Property="BorderBrush" Value="{DynamicResource Cp.Accent}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Secondary button (Change…, Browse…, Cancel). -->
    <Style x:Key="CadToBim.Button" TargetType="Button">
        <Setter Property="Foreground" Value="{DynamicResource Cp.Ink}"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Padding" Value="10,4"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="bd" Background="{DynamicResource Cp.Bg}" BorderBrush="{DynamicResource Cp.Line}"
                            BorderThickness="1" CornerRadius="4" Padding="{TemplateBinding Padding}">
                        <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="{DynamicResource Cp.Hover}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.5"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Primary: full-width accent (Confirm, OK). -->
    <Style x:Key="CadToBim.Primary" TargetType="Button" BasedOn="{StaticResource CadToBim.Button}">
        <Setter Property="Foreground" Value="{DynamicResource Cp.AccentContrast}"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Padding" Value="12,8"/>
        <Setter Property="HorizontalAlignment" Value="Stretch"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="bd" Background="{DynamicResource Cp.Accent}" BorderBrush="{DynamicResource Cp.Accent}"
                            BorderThickness="1" CornerRadius="4" Padding="{TemplateBinding Padding}">
                        <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="{DynamicResource Cp.BlueHover}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.5"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Layer role chip: 9.5px caps, 1px border coloured by role (DataContext = CadLayerViewModel). -->
    <Style x:Key="CadToBim.Chip" TargetType="Button">
        <Setter Property="FontSize" Value="9.5"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Padding" Value="5,1"/>
        <Setter Property="Foreground" Value="{DynamicResource Cp.Faint}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Cp.Line}"/>
        <Setter Property="ToolTip" Value="Click to change what this layer is for: wall → opening → skip → neutral"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border Background="Transparent" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"
                            CornerRadius="3" Padding="{TemplateBinding Padding}">
                        <ContentPresenter VerticalAlignment="Center"/>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <DataTrigger Binding="{Binding Role}" Value="Wall">
                <Setter Property="Foreground" Value="{DynamicResource CadToBim.Wall}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource CadToBim.Wall}"/>
            </DataTrigger>
            <DataTrigger Binding="{Binding Role}" Value="Opening">
                <Setter Property="Foreground" Value="{DynamicResource CadToBim.Opening}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource CadToBim.Opening}"/>
            </DataTrigger>
        </Style.Triggers>
    </Style>

    <Style x:Key="CadToBim.Radio" TargetType="RadioButton">
        <Setter Property="Foreground" Value="{DynamicResource Cp.Ink}"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Margin" Value="0,2"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
    </Style>

    <!-- NumericScrubBox comes without its Themes/Generic.xaml (not linked): this implicit style
         IS its template. Same parts the control looks up (PART_Display / PART_ValueText / PART_Editor). -->
    <Style TargetType="{x:Type scrub:NumericScrubBox}">
        <Setter Property="Background" Value="{DynamicResource Cp.Bg}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Cp.Line}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Foreground" Value="{DynamicResource Cp.Ink}"/>
        <Setter Property="FontFamily" Value="{DynamicResource Cp.FontMono}"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Cursor" Value="SizeWE"/>
        <Setter Property="Focusable" Value="True"/>
        <Setter Property="MinWidth" Value="62"/>
        <Setter Property="ToolTip" Value="Drag sideways to scrub, click to type"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="{x:Type scrub:NumericScrubBox}">
                    <Border x:Name="Chrome" Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="3" Padding="6,3">
                        <Grid>
                            <DockPanel x:Name="PART_Display" Background="Transparent">
                                <TextBlock DockPanel.Dock="Left" Text="{TemplateBinding Label}" Foreground="{DynamicResource Cp.Faint}"/>
                                <TextBlock x:Name="PART_ValueText" HorizontalAlignment="Right" Foreground="{TemplateBinding Foreground}"/>
                            </DockPanel>
                            <TextBox x:Name="PART_Editor" Visibility="Collapsed" HorizontalAlignment="Stretch"
                                     VerticalContentAlignment="Center" BorderThickness="0" Padding="0"
                                     Background="{DynamicResource Cp.Sunken}" Foreground="{DynamicResource Cp.Ink}"
                                     CaretBrush="{DynamicResource Cp.Ink}" SelectionBrush="{DynamicResource Cp.Accent}"/>
                        </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource Cp.Accent}"/>
                        </Trigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                            <Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource Cp.Accent}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
```

`UI/CadToBim/CadToBimTheme.cs` (follows `CopilotTheme`: tokens merged once at app scope for parse-time lookups; a fresh per-theme dictionary is mounted in the panel's own resources and swapped on `CopilotTheme.ThemeChanged`, because app-scope changes do not re-invalidate `DynamicResource` inside Revit's pane host):
```csharp
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using RevitWebAppSync.UI.Copilot;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>
    /// The five CAD to BIM overlay brushes, light and dark. Rides on CopilotTheme: same
    /// persisted light/dark choice (the Copilot moon button), same ThemeChanged event, same
    /// "mount a local dictionary and swap it" mechanism the Copilot panel uses inside Revit.
    /// </summary>
    public static class CadToBimTheme
    {
        private static bool _loaded;
        private static readonly object _lock = new object();

        // key → (light, dark); both columns from the approved mockup's CSS variables.
        private static readonly Dictionary<string, (string light, string dark)> Palette =
            new Dictionary<string, (string, string)>
            {
                ["CadToBim.Wall"]    = ("#1E9E4F", "#3FCB78"),
                ["CadToBim.Opening"] = ("#2B7DE9", "#5EA1F5"),
                ["CadToBim.Room"]    = ("#C77D0A", "#E9A23B"),
                ["CadToBim.Erase"]   = ("#D13B3B", "#F0605C"),
                ["CadToBim.Brush"]   = ("#12A3B4", "#35C5D6"),
            };

        public static void EnsureLoaded()
        {
            CopilotTheme.EnsureLoaded();
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                if (!Helpers.WpfAppBootstrap.Ensure()) return;
                var asm = typeof(CadToBimTheme).Assembly.GetName().Name;
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new System.Uri($"pack://application:,,,/{asm};component/UI/CadToBim/CadToBimTokens.xaml"),
                });
                _loaded = true;
            }
        }

        /// <summary>A fresh dictionary of the five brushes for the CURRENT theme (frozen — swaps
        /// replace the whole dictionary, nothing is mutated in place).</summary>
        public static ResourceDictionary NewThemeDictionary()
        {
            EnsureLoaded();
            var dictionary = new ResourceDictionary();
            foreach (var entry in Palette)
            {
                try
                {
                    var brush = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(CopilotTheme.IsDark ? entry.Value.dark : entry.Value.light));
                    brush.Freeze();
                    dictionary[entry.Key] = brush;
                }
                catch
                {
                    // Malformed hex would be a typo in Palette; skip rather than kill the pane.
                }
            }
            return dictionary;
        }
    }
}
```

`UI/CadToBim/CadToBimPanel.xaml`:
```xml
<UserControl x:Class="RevitWebAppSync.UI.CadToBim.CadToBimPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:cad="clr-namespace:RevitWebAppSync.UI.CadToBim"
             xmlns:ctl="clr-namespace:RevitWebAppSync.UI.Copilot.Controls"
             xmlns:scrub="clr-namespace:Cad2Bim.Views.Controls"
             Background="{DynamicResource Cp.PanelBg}"
             FontFamily="{DynamicResource Cp.Font}" FontSize="12"
             Foreground="{DynamicResource Cp.Ink}"
             MinWidth="520" MinHeight="360">

    <UserControl.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="pack://application:,,,/RevitWebAppSync;component/UI/CadToBim/CadToBimStyles.xaml"/>
            </ResourceDictionary.MergedDictionaries>
            <ctl:BoolToVisibilityConverter x:Key="BoolVis"/>
            <ctl:EnumEqualsConverter x:Key="EnumEq"/>
            <ctl:NotEmptyToVisibilityConverter x:Key="NotEmptyVis"/>
        </ResourceDictionary>
    </UserControl.Resources>

    <DockPanel UseLayoutRounding="True" TextOptions.TextFormattingMode="Display">

        <!-- ═══ Pane title ═══ -->
        <Border DockPanel.Dock="Top" Background="{DynamicResource Cp.Sunken}"
                BorderBrush="{DynamicResource Cp.Line}" BorderThickness="0,0,0,1" Padding="10,5">
            <DockPanel LastChildFill="False">
                <TextBlock DockPanel.Dock="Left" Text="BINA CAD to BIM" FontWeight="SemiBold" VerticalAlignment="Center"/>
                <Button DockPanel.Dock="Right" x:Name="GearBtn" Content="⚙" FontSize="14" Padding="5,0" Click="OnGearClick"
                        Background="Transparent" BorderThickness="0" Cursor="Hand" Foreground="{DynamicResource Cp.Muted}"
                        ToolTip="Settings: thickness range, wall height, door radius, layer name hints (EN/Malay), default template"/>
            </DockPanel>
        </Border>

        <!-- ═══ Footer ═══ -->
        <Border DockPanel.Dock="Bottom" Background="{DynamicResource Cp.Sunken}"
                BorderBrush="{DynamicResource Cp.Line}" BorderThickness="0,1,0,0" Padding="10,3">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="{Binding Mode, ElementName=Viewport}" FontSize="11" Foreground="{DynamicResource Cp.Faint}" Margin="0,0,14,0"/>
                <TextBlock Text="Classifier: cad2bim · 100% local, no cloud" FontSize="11" Foreground="{DynamicResource Cp.Faint}" Margin="0,0,14,0"/>
                <TextBlock Text="{Binding Warning}" FontSize="11" Foreground="{DynamicResource CadToBim.Room}"
                           Visibility="{Binding Warning, Converter={StaticResource NotEmptyVis}}"/>
            </StackPanel>
        </Border>

        <!-- ═══ Body: viewport | 258 px sidebar ═══ -->
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="258"/>
            </Grid.ColumnDefinitions>

            <!-- Viewport + floating chrome -->
            <Grid Grid.Column="0" ClipToBounds="True">
                <cad:CadOverlayViewport x:Name="Viewport"
                                        LayersSource="{Binding Layers}"
                                        ContentBounds="{Binding Bounds}"
                                        Backdrop="{DynamicResource Cp.Bg}"/>

                <!-- Empty state -->
                <TextBlock Text="Open a DWG or DXF to begin. Nothing is built until you confirm."
                           HorizontalAlignment="Center" VerticalAlignment="Center" TextWrapping="Wrap" MaxWidth="280"
                           TextAlignment="Center" Foreground="{DynamicResource Cp.Faint}"
                           Visibility="{Binding HasSession, Converter={StaticResource BoolVis}, ConverterParameter=Invert}"/>

                <!-- Tools: Pan · Erase · Brush -->
                <Border Style="{StaticResource CadToBim.Float}" HorizontalAlignment="Left" VerticalAlignment="Top" Margin="8">
                    <StackPanel Orientation="Horizontal">
                        <RadioButton Style="{StaticResource CadToBim.Tool}" Content="Pan" GroupName="ViewportTool"
                                     ToolTip="Drag to pan, wheel to zoom"
                                     IsChecked="{Binding Mode, ElementName=Viewport, Converter={StaticResource EnumEq}, ConverterParameter=Pan, Mode=TwoWay}"/>
                        <RadioButton Style="{StaticResource CadToBim.Tool}" Content="Erase" GroupName="ViewportTool"
                                     ToolTip="Click a detected wall to leave it out of the build"
                                     IsChecked="{Binding Mode, ElementName=Viewport, Converter={StaticResource EnumEq}, ConverterParameter=Erase, Mode=TwoWay}"/>
                        <RadioButton Style="{StaticResource CadToBim.Tool}" Content="Brush" GroupName="ViewportTool"
                                     ToolTip="Drag a box over linework the classifier missed; walls inside are forced"
                                     IsChecked="{Binding Mode, ElementName=Viewport, Converter={StaticResource EnumEq}, ConverterParameter=Brush, Mode=TwoWay}"/>
                    </StackPanel>
                </Border>

                <!-- Mode hint (top-right), text set from code-behind -->
                <Border x:Name="ModeHint" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="8" MaxWidth="260"
                        Background="{DynamicResource Cp.BlueSoft}" BorderBrush="{DynamicResource Cp.Accent}" BorderThickness="1"
                        CornerRadius="4" Padding="9,4" Visibility="Collapsed">
                    <TextBlock x:Name="ModeHintText" FontSize="11.5" TextWrapping="Wrap"/>
                </Border>

                <!-- Legend (bottom-left) -->
                <Border Style="{StaticResource CadToBim.Float}" HorizontalAlignment="Left" VerticalAlignment="Bottom" Margin="8" Padding="9,5">
                    <WrapPanel MaxWidth="360">
                        <WrapPanel.Resources>
                            <Style TargetType="TextBlock">
                                <Setter Property="FontSize" Value="11"/>
                                <Setter Property="Foreground" Value="{DynamicResource Cp.Muted}"/>
                                <Setter Property="VerticalAlignment" Value="Center"/>
                                <Setter Property="Margin" Value="0,0,12,0"/>
                            </Style>
                            <Style TargetType="Rectangle">
                                <Setter Property="Width" Value="18"/>
                                <Setter Property="Height" Value="3"/>
                                <Setter Property="Margin" Value="0,0,5,0"/>
                                <Setter Property="VerticalAlignment" Value="Center"/>
                            </Style>
                        </WrapPanel.Resources>
                        <Rectangle Fill="{DynamicResource Cp.Faint}"/><TextBlock Text="DWG linework"/>
                        <Rectangle Fill="{DynamicResource CadToBim.Wall}"/><TextBlock Text="Wall centerline · thickness"/>
                        <Rectangle Fill="{DynamicResource CadToBim.Opening}"/><TextBlock Text="Door / window"/>
                        <Rectangle Fill="{DynamicResource CadToBim.Room}"/><TextBlock Text="Room boundary"/>
                        <Rectangle Fill="{DynamicResource CadToBim.Erase}" Height="2"/><TextBlock Text="Erased"/>
                        <Rectangle Width="12" Height="10" Stroke="{DynamicResource CadToBim.Brush}" StrokeThickness="1.5" Fill="Transparent"/><TextBlock Text="Brushed"/>
                    </WrapPanel>
                </Border>

                <!-- Coords (bottom-right) -->
                <Border Style="{StaticResource CadToBim.Float}" HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="8" Padding="7,2">
                    <TextBlock x:Name="Coords" Style="{StaticResource CadToBim.Mono}" FontSize="11" Foreground="{DynamicResource Cp.Faint}" Text="x 0 · y 0 mm"/>
                </Border>
            </Grid>

            <!-- Sidebar -->
            <Border Grid.Column="1" BorderBrush="{DynamicResource Cp.Line}" BorderThickness="1,0,0,0" Background="{DynamicResource Cp.PanelBg}">
                <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                    <StackPanel>

                        <!-- DRAWING -->
                        <Border Style="{StaticResource CadToBim.Section}">
                            <StackPanel>
                                <TextBlock Style="{StaticResource CadToBim.SectionTitle}" Text="DRAWING"/>
                                <DockPanel Margin="0,0,0,6">
                                    <Button DockPanel.Dock="Right" Style="{StaticResource CadToBim.Button}" Content="Change…" Click="OnChangeClick" ToolTip="Pick another drawing"/>
                                    <TextBlock Text="{Binding DrawingName}" ToolTip="{Binding DrawingPath}" FontWeight="SemiBold"
                                               TextTrimming="CharacterEllipsis" VerticalAlignment="Center" Margin="0,0,8,0"/>
                                </DockPanel>
                                <DockPanel Margin="0,2">
                                    <TextBlock Text="Units" Foreground="{DynamicResource Cp.Muted}"/>
                                    <TextBlock Style="{StaticResource CadToBim.Mono}" Text="{Binding UnitsText}" HorizontalAlignment="Right"/>
                                </DockPanel>
                                <DockPanel Margin="0,2">
                                    <TextBlock Text="Read" Foreground="{DynamicResource Cp.Muted}"/>
                                    <TextBlock Style="{StaticResource CadToBim.Mono}" Text="{Binding ReadText}" HorizontalAlignment="Right"/>
                                </DockPanel>
                            </StackPanel>
                        </Border>

                        <!-- LAYERS -->
                        <Border Style="{StaticResource CadToBim.Section}">
                            <StackPanel>
                                <TextBlock Style="{StaticResource CadToBim.SectionTitle}" Text="LAYERS"/>
                                <ScrollViewer MaxHeight="150" VerticalScrollBarVisibility="Auto">
                                    <ItemsControl ItemsSource="{Binding Layers}">
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate>
                                                <DockPanel Margin="2,1">
                                                    <CheckBox DockPanel.Dock="Left" IsChecked="{Binding IsVisible}" VerticalAlignment="Center" Margin="0,0,7,0"
                                                              ToolTip="Show or hide this layer in the preview (does not change detection)"/>
                                                    <Button DockPanel.Dock="Right" Style="{StaticResource CadToBim.Chip}" Content="{Binding RoleLabel}" Click="OnRoleClick" Margin="6,0,0,0"/>
                                                    <TextBlock DockPanel.Dock="Right" Text="{Binding Count}" FontSize="11" Foreground="{DynamicResource Cp.Faint}" VerticalAlignment="Center"/>
                                                    <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" Margin="0,0,6,0"/>
                                                </DockPanel>
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                </ScrollViewer>
                            </StackPanel>
                        </Border>

                        <!-- WALLS -->
                        <Border Style="{StaticResource CadToBim.Section}">
                            <StackPanel>
                                <TextBlock Style="{StaticResource CadToBim.SectionTitle}" Text="WALLS"/>
                                <DockPanel Margin="0,2">
                                    <TextBlock Text="Thickness" Foreground="{DynamicResource Cp.Muted}" VerticalAlignment="Center"/>
                                    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                                        <scrub:NumericScrubBox Value="{Binding SMinMm}" Minimum="10" Maximum="2000" Step="1" Format="0" ToolTip="Minimum wall thickness, mm"/>
                                        <TextBlock Text="–" Margin="4,0" VerticalAlignment="Center" Foreground="{DynamicResource Cp.Faint}"/>
                                        <scrub:NumericScrubBox Value="{Binding SMaxMm}" Minimum="20" Maximum="3000" Step="1" Format="0" ToolTip="Maximum wall thickness, mm"/>
                                        <TextBlock Text="mm" Margin="4,0,0,0" VerticalAlignment="Center" FontSize="11" Foreground="{DynamicResource Cp.Faint}"/>
                                    </StackPanel>
                                </DockPanel>
                                <DockPanel Margin="0,2">
                                    <TextBlock Text="Height" Foreground="{DynamicResource Cp.Muted}" VerticalAlignment="Center"/>
                                    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                                        <scrub:NumericScrubBox Value="{Binding HeightMm}" Minimum="100" Maximum="20000" Step="10" Format="0" ToolTip="Wall height, mm (a plan carries no height)"/>
                                        <TextBlock Text="mm" Margin="4,0,0,0" VerticalAlignment="Center" FontSize="11" Foreground="{DynamicResource Cp.Faint}"/>
                                    </StackPanel>
                                </DockPanel>
                            </StackPanel>
                        </Border>

                        <!-- PLANS ON SHEET -->
                        <Border Style="{StaticResource CadToBim.Section}">
                            <StackPanel>
                                <StackPanel Orientation="Horizontal">
                                    <TextBlock Style="{StaticResource CadToBim.SectionTitle}" Text="PLANS ON SHEET"/>
                                    <TextBlock Style="{StaticResource CadToBim.Mono}" Text="{Binding PlanText}" FontSize="10.5" Margin="6,0,0,6" Foreground="{DynamicResource Cp.Muted}"/>
                                </StackPanel>
                                <RadioButton Style="{StaticResource CadToBim.Radio}" GroupName="Storeys" Content="Stack as storeys (Level 1, 2, …)"
                                             IsChecked="{Binding Storeys, Converter={StaticResource EnumEq}, ConverterParameter=Stack, Mode=TwoWay}"/>
                                <RadioButton Style="{StaticResource CadToBim.Radio}" GroupName="Storeys" Content="Keep drawing position, one level"
                                             IsChecked="{Binding Storeys, Converter={StaticResource EnumEq}, ConverterParameter=KeepPosition, Mode=TwoWay}"/>
                            </StackPanel>
                        </Border>

                        <!-- WILL BE BUILT -->
                        <Border Style="{StaticResource CadToBim.Section}">
                            <StackPanel>
                                <TextBlock Style="{StaticResource CadToBim.SectionTitle}" Text="WILL BE BUILT"/>
                                <Grid>
                                    <Grid.Resources>
                                        <Style TargetType="TextBlock" BasedOn="{StaticResource CadToBim.Mono}">
                                            <Setter Property="Margin" Value="0,2"/>
                                        </Style>
                                    </Grid.Resources>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/>
                                        <ColumnDefinition Width="10"/>
                                        <ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/>
                                    </Grid.ColumnDefinitions>
                                    <Grid.RowDefinitions>
                                        <RowDefinition/><RowDefinition/><RowDefinition/>
                                    </Grid.RowDefinitions>
                                    <TextBlock Grid.Row="0" Grid.Column="0" Text="Walls" Foreground="{DynamicResource Cp.Muted}"/>
                                    <TextBlock Grid.Row="0" Grid.Column="1" Text="{Binding WallCount}"/>
                                    <TextBlock Grid.Row="0" Grid.Column="3" Text="Doors" Foreground="{DynamicResource Cp.Muted}"/>
                                    <TextBlock Grid.Row="0" Grid.Column="4" Text="{Binding DoorCount}"/>
                                    <TextBlock Grid.Row="1" Grid.Column="0" Text="Windows" Foreground="{DynamicResource Cp.Muted}"/>
                                    <TextBlock Grid.Row="1" Grid.Column="1" Text="{Binding WindowCount}"/>
                                    <TextBlock Grid.Row="1" Grid.Column="3" Text="Rooms" Foreground="{DynamicResource Cp.Muted}"/>
                                    <TextBlock Grid.Row="1" Grid.Column="4" Text="{Binding RoomCount}"/>
                                    <TextBlock Grid.Row="2" Grid.Column="0" Text="Erased" Foreground="{DynamicResource Cp.Muted}"/>
                                    <TextBlock Grid.Row="2" Grid.Column="1" Text="{Binding ErasedCount}"/>
                                    <TextBlock Grid.Row="2" Grid.Column="3" Text="Brushed" Foreground="{DynamicResource Cp.Muted}"/>
                                    <TextBlock Grid.Row="2" Grid.Column="4" Text="{Binding ForcedCount}"/>
                                </Grid>
                            </StackPanel>
                        </Border>

                        <!-- PUT THE RESULT -->
                        <Border Style="{StaticResource CadToBim.Section}" BorderThickness="0">
                            <StackPanel>
                                <TextBlock Style="{StaticResource CadToBim.SectionTitle}" Text="PUT THE RESULT"/>
                                <RadioButton Style="{StaticResource CadToBim.Radio}" GroupName="Target" Content="Add to this project"
                                             IsChecked="{Binding Target, Converter={StaticResource EnumEq}, ConverterParameter=AddToProject, Mode=TwoWay}"/>
                                <ComboBox ItemsSource="{Binding Levels}" SelectedItem="{Binding SelectedLevel}" Margin="22,0,0,4" FontSize="11.5"
                                          ToolTip="Level the walls are placed on (Stack mode starts here)"
                                          Visibility="{Binding IsAddToProject, Converter={StaticResource BoolVis}}"/>
                                <RadioButton Style="{StaticResource CadToBim.Radio}" GroupName="Target" Content="Save as new .rvt beside the drawing"
                                             IsChecked="{Binding Target, Converter={StaticResource EnumEq}, ConverterParameter=NewFile, Mode=TwoWay}"/>
                                <Button Style="{StaticResource CadToBim.Primary}" Content="{Binding ConfirmLabel}" IsEnabled="{Binding CanConfirm}"
                                        Click="OnConfirmClick" Margin="0,8,0,6"/>
                                <TextBlock Text="{Binding Status}" TextWrapping="Wrap" FontSize="11.5" Foreground="{DynamicResource Cp.Muted}" MinHeight="32"/>
                            </StackPanel>
                        </Border>
                    </StackPanel>
                </ScrollViewer>
            </Border>
        </Grid>
    </DockPanel>
</UserControl>
```

`UI/CadToBim/CadToBimPanel.xaml.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Services;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.Copilot;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>
    /// The CAD to BIM dockable-pane body: CadOverlayViewport on the left, the six-section sidebar
    /// on the right. All decisions live in CadToBimViewModel; this file wires mouse events,
    /// dialogs (file picker, overwrite, big-brush confirm, settings) and the theme dictionaries.
    /// </summary>
    public partial class CadToBimPanel : UserControl
    {
        private readonly CadToBimViewModel _vm;
        private ResourceDictionary _copilotTheme;
        private ResourceDictionary _cadTheme;
        private bool _themeDark;

        /// <summary>Reachable from the ribbon command via App.CadToBimPaneHost.Panel.ViewModel.</summary>
        public CadToBimViewModel ViewModel => _vm;

        public CadToBimPanel() : this(App.CadToBimBuildHandler, App.CadToBimBuildEvent) { }

        public CadToBimPanel(CadToBimBuildHandler handler, ExternalEvent buildEvent)
        {
            // Cp.* and CadToBim.* live in dictionaries merged at app scope; without this the
            // first {StaticResource} in the XAML below throws inside InitializeComponent.
            CopilotTheme.EnsureLoaded();
            CadToBimTheme.EnsureLoaded();

            _vm = new CadToBimViewModel(handler, new ExternalEventRaiser(buildEvent));
            InitializeComponent();
            DataContext = _vm;

            // Same trick as CopilotPanel: theme brushes mounted on THIS element's resources and
            // swapped on ThemeChanged, because app-scope changes do not re-invalidate
            // DynamicResource inside Revit's dockable-pane host.
            _copilotTheme = CopilotTheme.NewThemeDictionary();
            _cadTheme = CadToBimTheme.NewThemeDictionary();
            _themeDark = CopilotTheme.IsDark;
            Resources.MergedDictionaries.Add(_copilotTheme);
            Resources.MergedDictionaries.Add(_cadTheme);

            Loaded += (_, __) =>
            {
                CopilotTheme.ThemeChanged -= SwapLocalTheme;
                CopilotTheme.ThemeChanged += SwapLocalTheme;
                SwapLocalTheme();
            };
            Unloaded += (_, __) =>
            {
                CopilotTheme.ThemeChanged -= SwapLocalTheme;
                _vm.Cancel();          // closing/hiding the pane cancels an in-flight detect
            };

            Viewport.WallClicked += wall => _vm.ToggleErase(wall);
            Viewport.BoxDragged += box => { var _ = _vm.BrushAsync(box); };
            Viewport.CursorMoved += (x, y) =>
                Coords.Text = string.Format(CultureInfo.InvariantCulture, "x {0:0} · y {1:0} mm", x, y);

            DependencyPropertyDescriptor
                .FromProperty(CadOverlayViewport.ModeProperty, typeof(CadOverlayViewport))
                .AddValueChanged(Viewport, (_, __) => UpdateModeHint());

            _vm.PropertyChanged += OnVmChanged;
            _vm.OverwritePrompt = path => Ask(
                "Overwrite " + System.IO.Path.GetFileName(path) + "?",
                "A file with that name already sits beside the drawing. Replace it with the new model?");

            UpdateModeHint();
        }

        /// <summary>Levels of the open project for the "Add to this project" picker. Reads the
        /// Document, so call it from a valid API context (OpenCadToBimCommand does, before OpenAsync).</summary>
        public void RefreshLevels(Document doc)
        {
            if (doc == null)
            {
                _vm.SetLevels(new List<LevelChoice>());
                return;
            }
            try
            {
                var levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(level => level.Elevation)
                    .Select(level => new LevelChoice { Name = level.Name, Id = ElementIdCompat.ToLong(level.Id) })
                    .ToList();
                _vm.SetLevels(levels);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BINA] CadToBim RefreshLevels: " + ex.Message);
                _vm.SetLevels(new List<LevelChoice>());
            }
        }

        // ───────── view-model → view ─────────

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(CadToBimViewModel.Overlay):
                    OverlaySnapshot o = _vm.Overlay;
                    Viewport.SetOverlay(o.Active, o.Erased, o.Openings, o.Spaces, o.Boxes);
                    break;

                case nameof(CadToBimViewModel.PendingBrushConfirm):
                    if (_vm.PendingBrushConfirm == null) break;
                    // Deferred so BrushAsync's finally releases Busy before the modal opens.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BrushResult pending = _vm.PendingBrushConfirm;
                        if (pending == null) return;
                        bool yes = Ask(
                            pending.Walls.Count + " walls from that box",
                            "That is a lot for one selection, and usually means the box caught something " +
                            "that is not wall (a stair, fittings, a title block). Add them anyway?");
                        _vm.ResolveBrushConfirm(yes);
                    }));
                    break;
            }
        }

        private void UpdateModeHint()
        {
            string hint;
            switch (Viewport.Mode)
            {
                case ViewportMode.Erase: hint = "Click a green wall to leave it out. Click again to restore."; break;
                case ViewportMode.Brush: hint = "Drag a box over linework the classifier skipped. Walls inside are forced in."; break;
                default: hint = null; break;
            }
            ModeHintText.Text = hint ?? "";
            ModeHint.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SwapLocalTheme()
        {
            if (_cadTheme != null && _themeDark == CopilotTheme.IsDark) return;
            var dicts = Resources.MergedDictionaries;
            Replace(dicts, ref _copilotTheme, CopilotTheme.NewThemeDictionary());
            Replace(dicts, ref _cadTheme, CadToBimTheme.NewThemeDictionary());
            _themeDark = CopilotTheme.IsDark;
            Viewport.Redraw();     // overlay pens read CadToBim.* at render time
        }

        private static void Replace(IList<ResourceDictionary> dicts, ref ResourceDictionary current, ResourceDictionary next)
        {
            int i = current != null ? dicts.IndexOf(current) : -1;
            if (i >= 0) { dicts.RemoveAt(i); dicts.Insert(i, next); }
            else dicts.Add(next);
            current = next;
        }

        // ───────── clicks ─────────

        private void OnChangeClick(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the drawing to convert",
                Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true) return;
            var _ = _vm.OpenAsync(dialog.FileName);
        }

        private void OnConfirmClick(object sender, RoutedEventArgs e) => _vm.Confirm();

        private void OnRoleClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CadLayerViewModel layer) _vm.CycleRole(layer);
        }

        private void OnGearClick(object sender, RoutedEventArgs e)
        {
            CadToBimSettings settings = _vm.Settings;
            var window = new CadToBimSettingsWindow(settings);
            RevitWindowOwner.SetOwner(window, App.UiApp);
            if (window.ShowDialog() == true) _vm.ApplySettings(settings);
        }

        // Revit's TaskDialog when hosted in Revit; MessageBox when it is not available (UiHarness).
        private static bool Ask(string instruction, string content)
        {
            try
            {
                var dialog = new TaskDialog("BINA CAD to BIM")
                {
                    MainInstruction = instruction,
                    MainContent = content,
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                return dialog.Show() == TaskDialogResult.Yes;
            }
            catch
            {
                return MessageBox.Show(content, instruction, MessageBoxButton.YesNo, MessageBoxImage.Question,
                                       MessageBoxResult.No) == MessageBoxResult.Yes;
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run (Mac, Tests compile — the panel itself is not linked into Tests, so this only proves nothing in Task 18 broke the linked sources): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded"`  Expected: `Build succeeded`.
Run (Mac, add-in — AFTER Task 19, because `OnGearClick` constructs `CadToBimSettingsWindow`): `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows 2>&1 | grep -E "error|Build succeeded" && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 2>&1 | grep -E "error|Build succeeded" && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows 2>&1 | grep -E "error|Build succeeded"`  Expected: `Build succeeded` ×3 (XAML compiles into the assembly via the SDK's default `Page` glob — `UseWPF=true`, no explicit Page items needed). Until Task 19 lands the only error is `CS0246 ... 'CadToBimSettingsWindow'`.
Run (Windows): `dotnet test Tests/Tests.csproj -p:SkipRevitSources=true --filter "FullyQualifiedName~XamlResourceScopeTests"` Expected: the `CadToBimPanel.xaml` and `CadToBimStyles.xaml` rows PASS; the `CadToBimSettingsWindow.xaml` row still fails until Task 19.
Manual rows (Windows, Revit 2024 net48 + 2025 net8) are rows 4, 9, 10 and E1 of the smoke checklist (Task 22): open an `A-101`-class sample DWG → pane shows linework, green bands, blue openings, amber rooms with names; wheel-zoom keeps 2 px centrelines; Erase click turns a wall red-dashed and drops "Walls" by one; Brush drag over the un-detected partition shows a cyan box and raises "Brushed"; Pan drag still pans while Erase/Brush is active with the middle button; toggling the Copilot moon button re-colours the overlay.

- [ ] **Step 5: Stage**

`git add UI/CadToBim/CadToBimTokens.xaml UI/CadToBim/CadToBimStyles.xaml UI/CadToBim/CadToBimTheme.cs UI/CadToBim/CadToBimPanel.xaml UI/CadToBim/CadToBimPanel.xaml.cs Tests/XamlResourceScopeTests.cs`   (repo owner rule: executors STAGE, never commit)

---

### Task 19: `CadToBimPaneHost` + `CadToBimSettingsWindow`

**Files:**
- Create: `UI/CadToBim/CadToBimPaneHost.cs`
- Create: `UI/CadToBim/CadToBimSettingsDraft.cs` (pure, linked into Tests)
- Create: `UI/CadToBim/CadToBimSettingsWindow.xaml`
- Create: `UI/CadToBim/CadToBimSettingsWindow.xaml.cs`
- Modify: `Tests/Tests.csproj` (one `<Compile Include>` line under the marker comment)
- Test: `Tests/XamlResourceScopeTests.cs` (the `CadToBimSettingsWindow.xaml` row added in Task 18) + `Tests/CadToBimSettingsDraftTests.cs` (validation rules of the settings draft, pure)

**Interfaces:**
- Consumes: Task 18 `CadToBimPanel`, `CadToBimTheme`; `CopilotTheme`; `CadToBimSettings` (fields per contract + `Save()`); `Services.TelemetryService.Track(string kind, string stage, object payload)` (internal static — same assembly); `Cad2Bim.ViewModels.ViewModelBase`; `NumericScrubBox`.
- Produces:
  ```csharp
  namespace RevitWebAppSync.UI.CadToBim {
    public class CadToBimPaneHost : System.Windows.Controls.Page, Autodesk.Revit.UI.IDockablePaneProvider {
      public static readonly DockablePaneId PaneId;   // B1A4C057-0005-4000-8000-000000000005
      public CadToBimPanel Panel { get; }
      public void SetupDockablePane(DockablePaneProviderData data);   // Right, VisibleByDefault=false
    }
    public sealed class CadToBimSettingsDraft : Cad2Bim.ViewModels.ViewModelBase {
      public static CadToBimSettingsDraft From(CadToBimSettings s); public string Validate(); public void WriteTo(CadToBimSettings s);
      public double SMin, SMax, Height, DoorMin, DoorMax, Sill { get; set; } public string WallHints, OpeningHints, ExcludeGlobs, TemplatePath { get; set; }
      public static List<string> SplitList(string csv);
    }
    public partial class CadToBimSettingsWindow : System.Windows.Window { public CadToBimSettingsWindow(CadToBimSettings settings); }  // ShowDialog()==true ⇒ settings mutated + saved
  }
  ```

- [ ] **Step 1: Write the failing test**

`Tests/CadToBimSettingsDraftTests.cs`:
```csharp
using System.Linq;
using Xunit;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.CadToBim;

namespace Tests
{
    // The settings window's draft: what the drafter types is validated and only then written
    // back to the persisted settings (Cancel leaves them untouched).
    public class CadToBimSettingsDraftTests
    {
        [Fact]
        public void Draft_round_trips_the_defaults()
        {
            var settings = new CadToBimSettings();
            var draft = CadToBimSettingsDraft.From(settings);
            Assert.Equal(50, draft.SMin);
            Assert.Equal(400, draft.SMax);
            Assert.Equal(3000, draft.Height);
            Assert.Equal("wall, dinding, tembok, partition, bata", draft.WallHints);
            Assert.Null(draft.Validate());

            draft.WriteTo(settings);
            Assert.Equal(new[] { "wall", "dinding", "tembok", "partition", "bata" }, settings.WallLayerHints);
            Assert.Equal(400, settings.SMaxMm);
        }

        [Fact]
        public void Min_must_be_below_max()
        {
            var draft = CadToBimSettingsDraft.From(new CadToBimSettings());
            draft.SMin = 400;
            draft.SMax = 300;
            Assert.Equal("Minimum thickness must be smaller than the maximum.", draft.Validate());
            draft.DoorMin = 1500; draft.DoorMax = 500; draft.SMin = 50; draft.SMax = 400;
            Assert.Equal("Door swing minimum radius must be smaller than the maximum.", draft.Validate());
        }

        [Fact]
        public void Lists_split_on_commas_and_drop_blanks()
        {
            Assert.Equal(new[] { "A-WALL", "Dinding" }, CadToBimSettingsDraft.SplitList(" A-WALL ,, Dinding , "));
            Assert.Empty(CadToBimSettingsDraft.SplitList(null));
        }

        [Fact]
        public void Template_path_blank_writes_null()
        {
            var settings = new CadToBimSettings { TemplatePath = @"C:\old.rte" };
            var draft = CadToBimSettingsDraft.From(settings);
            draft.TemplatePath = "   ";
            draft.WriteTo(settings);
            Assert.Null(settings.TemplatePath);
        }
    }
}
```
`Tests/Tests.csproj` — directly after the Task 17 link under the marker comment: `<Compile Include="..\UI\CadToBim\CadToBimSettingsDraft.cs" Link="CadToBim\CadToBimSettingsDraft.cs" />` (the draft lives in its own file so it links without the Window).

- [ ] **Step 2: Run test to verify it fails**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded" | head`  Expected: FAIL with `error CS0246: The type or namespace name 'CadToBimSettingsDraft' could not be found`.
Run (Windows): `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CadToBimSettingsDraftTests|FullyQualifiedName~XamlResourceScopeTests"` Expected: FAIL (compile error; and the settings-window XAML row is still `FileNotFoundException`).

- [ ] **Step 3: Write minimal implementation**

`UI/CadToBim/CadToBimPaneHost.cs` (mirrors `UI/Copilot/CopilotPaneHost.cs` line for line):
```csharp
using System;
using System.Windows.Controls;
using Autodesk.Revit.UI;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>
    /// Hosts the CadToBimPanel as a Revit dockable pane, docked right like the Copilot pane.
    /// Registered in App.OnStartup (RegisterDockablePane(PaneId, "BINA CAD to BIM", host)).
    /// </summary>
    public class CadToBimPaneHost : Page, IDockablePaneProvider
    {
        private CadToBimPanel _panel;

        // Contract id — distinct from Cost (…001), Compliance, JKR and Copilot panes.
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("B1A4C057-0005-4000-8000-000000000005"));

        public CadToBimPaneHost()
        {
            try
            {
                Copilot.CopilotTheme.EnsureLoaded();
                CadToBimTheme.EnsureLoaded();
                _panel = new CadToBimPanel();
                this.Content = new Frame
                {
                    Content = _panel,
                    NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden
                };
            }
            catch (Exception ex)
            {
                // Built by reflection/XAML, so what arrives is usually a TargetInvocationException /
                // XamlParseException WRAPPER whose Message says nothing. Log the chain and put the
                // INNERMOST message on screen: that one names the missing resource / bad binding.
                var root = ex; while (root.InnerException != null) root = root.InnerException;
                System.Diagnostics.Debug.WriteLine("[BINA] CadToBimPaneHost init error: " + ex);
                try { RevitWebAppSync.Services.TelemetryService.Track("cad_to_bim", "pane_init_failed", new { error_class = root.GetType().Name }); } catch { }
                this.Content = new TextBlock
                {
                    Text = $"BINA CAD to BIM failed to load: {root.Message}\n({root.GetType().Name})",
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Foreground = System.Windows.Media.Brushes.Red,
                    Margin = new System.Windows.Thickness(10)
                };
            }
        }

        /// <summary>Null when construction failed (the pane then shows the error text).</summary>
        public CadToBimPanel Panel => _panel;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
            data.VisibleByDefault = false;
        }
    }
}
```

`UI/CadToBim/CadToBimSettingsDraft.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Cad2Bim.ViewModels;
using RevitWebAppSync.Services.CadToBim;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>Editable copy of CadToBimSettings for the settings window. Nothing reaches the
    /// real settings until Validate() returns null and WriteTo() runs (OK), so Cancel is free.</summary>
    public sealed class CadToBimSettingsDraft : ViewModelBase
    {
        private double _sMin, _sMax, _height, _doorMin, _doorMax, _sill;
        private string _wallHints = "", _openingHints = "", _excludeGlobs = "", _templatePath = "";

        public double SMin { get => _sMin; set => SetField(ref _sMin, value); }
        public double SMax { get => _sMax; set => SetField(ref _sMax, value); }
        public double Height { get => _height; set => SetField(ref _height, value); }
        public double DoorMin { get => _doorMin; set => SetField(ref _doorMin, value); }
        public double DoorMax { get => _doorMax; set => SetField(ref _doorMax, value); }
        public double Sill { get => _sill; set => SetField(ref _sill, value); }
        public string WallHints { get => _wallHints; set => SetField(ref _wallHints, value ?? ""); }
        public string OpeningHints { get => _openingHints; set => SetField(ref _openingHints, value ?? ""); }
        public string ExcludeGlobs { get => _excludeGlobs; set => SetField(ref _excludeGlobs, value ?? ""); }
        public string TemplatePath { get => _templatePath; set => SetField(ref _templatePath, value ?? ""); }

        public static CadToBimSettingsDraft From(CadToBimSettings s) => new CadToBimSettingsDraft
        {
            SMin = s.SMinMm,
            SMax = s.SMaxMm,
            Height = s.WallHeightMm,
            DoorMin = s.DoorMinRadiusMm,
            DoorMax = s.DoorMaxRadiusMm,
            Sill = s.WindowSillMm,
            WallHints = string.Join(", ", s.WallLayerHints ?? new List<string>()),
            OpeningHints = string.Join(", ", s.OpeningLayerHints ?? new List<string>()),
            ExcludeGlobs = string.Join(", ", s.ExcludeGlobs ?? new List<string>()),
            TemplatePath = s.TemplatePath ?? "",
        };

        /// <summary>Null when everything is usable; otherwise the message to show.</summary>
        public string Validate()
        {
            if (_sMin <= 0) return "Minimum thickness must be above 0 mm.";
            if (_sMin >= _sMax) return "Minimum thickness must be smaller than the maximum.";
            if (_height <= 0) return "Wall height must be above 0 mm.";
            if (_doorMin <= 0) return "Door swing minimum radius must be above 0 mm.";
            if (_doorMin >= _doorMax) return "Door swing minimum radius must be smaller than the maximum.";
            if (_sill < 0) return "Window sill cannot be negative.";
            if (SplitList(_wallHints).Count == 0) return "Give at least one wall layer hint (e.g. wall, dinding).";
            string template = _templatePath.Trim();
            if (template.Length > 0 && !System.IO.File.Exists(template)) return "Template file not found: " + template;
            return null;
        }

        public void WriteTo(CadToBimSettings s)
        {
            s.SMinMm = _sMin;
            s.SMaxMm = _sMax;
            s.WallHeightMm = _height;
            s.DoorMinRadiusMm = _doorMin;
            s.DoorMaxRadiusMm = _doorMax;
            s.WindowSillMm = _sill;
            s.WallLayerHints = SplitList(_wallHints);
            s.OpeningLayerHints = SplitList(_openingHints);
            s.ExcludeGlobs = SplitList(_excludeGlobs);
            string template = _templatePath.Trim();
            s.TemplatePath = template.Length == 0 ? null : template;
        }

        public static List<string> SplitList(string csv) =>
            (csv ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(p => p.Trim())
                       .Where(p => p.Length > 0)
                       .ToList();
    }
}
```

`UI/CadToBim/CadToBimSettingsWindow.xaml`:
```xml
<Window x:Class="RevitWebAppSync.UI.CadToBim.CadToBimSettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:scrub="clr-namespace:Cad2Bim.Views.Controls"
        Title="CAD to BIM settings" Width="440" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize" ShowInTaskbar="False"
        Background="{DynamicResource Cp.PanelBg}" Foreground="{DynamicResource Cp.Ink}"
        FontFamily="{DynamicResource Cp.Font}" FontSize="12">

    <Window.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="pack://application:,,,/RevitWebAppSync;component/UI/CadToBim/CadToBimStyles.xaml"/>
            </ResourceDictionary.MergedDictionaries>
            <Style x:Key="RowLabel" TargetType="TextBlock">
                <Setter Property="Foreground" Value="{DynamicResource Cp.Muted}"/>
                <Setter Property="VerticalAlignment" Value="Center"/>
                <Setter Property="Margin" Value="0,0,12,0"/>
            </Style>
            <Style x:Key="Unit" TargetType="TextBlock">
                <Setter Property="Foreground" Value="{DynamicResource Cp.Faint}"/>
                <Setter Property="FontSize" Value="11"/>
                <Setter Property="VerticalAlignment" Value="Center"/>
                <Setter Property="Margin" Value="4,0,0,0"/>
            </Style>
            <Style TargetType="TextBox">
                <Setter Property="Background" Value="{DynamicResource Cp.Bg}"/>
                <Setter Property="Foreground" Value="{DynamicResource Cp.Ink}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource Cp.Line}"/>
                <Setter Property="Padding" Value="6,4"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
            </Style>
        </ResourceDictionary>
    </Window.Resources>

    <Grid Margin="16">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/> <!-- 0 heading: walls -->
            <RowDefinition Height="Auto"/> <!-- 1 thickness -->
            <RowDefinition Height="Auto"/> <!-- 2 height -->
            <RowDefinition Height="Auto"/> <!-- 3 heading: openings -->
            <RowDefinition Height="Auto"/> <!-- 4 door swing -->
            <RowDefinition Height="Auto"/> <!-- 5 sill -->
            <RowDefinition Height="Auto"/> <!-- 6 heading: layers -->
            <RowDefinition Height="Auto"/> <!-- 7 wall hints -->
            <RowDefinition Height="Auto"/> <!-- 8 opening hints -->
            <RowDefinition Height="Auto"/> <!-- 9 exclusions -->
            <RowDefinition Height="Auto"/> <!-- 10 heading: template -->
            <RowDefinition Height="Auto"/> <!-- 11 template -->
            <RowDefinition Height="Auto"/> <!-- 12 error -->
            <RowDefinition Height="Auto"/> <!-- 13 buttons -->
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Grid.ColumnSpan="2" Style="{StaticResource CadToBim.SectionTitle}" Text="WALLS"/>
        <TextBlock Grid.Row="1" Style="{StaticResource RowLabel}" Text="Thickness range"/>
        <StackPanel Grid.Row="1" Grid.Column="1" Orientation="Horizontal" Margin="0,2">
            <scrub:NumericScrubBox Value="{Binding SMin}" Minimum="1" Maximum="3000" Step="1" Format="0" Width="80"/>
            <TextBlock Style="{StaticResource Unit}" Text="–"/>
            <scrub:NumericScrubBox Value="{Binding SMax}" Minimum="2" Maximum="5000" Step="1" Format="0" Width="80" Margin="4,0,0,0"/>
            <TextBlock Style="{StaticResource Unit}" Text="mm"/>
        </StackPanel>
        <TextBlock Grid.Row="2" Style="{StaticResource RowLabel}" Text="Wall height"/>
        <StackPanel Grid.Row="2" Grid.Column="1" Orientation="Horizontal" Margin="0,2">
            <scrub:NumericScrubBox Value="{Binding Height}" Minimum="100" Maximum="20000" Step="10" Format="0" Width="80"/>
            <TextBlock Style="{StaticResource Unit}" Text="mm"/>
        </StackPanel>

        <TextBlock Grid.Row="3" Grid.ColumnSpan="2" Style="{StaticResource CadToBim.SectionTitle}" Text="OPENINGS" Margin="0,12,0,6"/>
        <TextBlock Grid.Row="4" Style="{StaticResource RowLabel}" Text="Door swing radius"/>
        <StackPanel Grid.Row="4" Grid.Column="1" Orientation="Horizontal" Margin="0,2">
            <scrub:NumericScrubBox Value="{Binding DoorMin}" Minimum="1" Maximum="5000" Step="5" Format="0" Width="80"/>
            <TextBlock Style="{StaticResource Unit}" Text="–"/>
            <scrub:NumericScrubBox Value="{Binding DoorMax}" Minimum="2" Maximum="5000" Step="5" Format="0" Width="80" Margin="4,0,0,0"/>
            <TextBlock Style="{StaticResource Unit}" Text="mm"/>
        </StackPanel>
        <TextBlock Grid.Row="5" Style="{StaticResource RowLabel}" Text="Window sill"/>
        <StackPanel Grid.Row="5" Grid.Column="1" Orientation="Horizontal" Margin="0,2">
            <scrub:NumericScrubBox Value="{Binding Sill}" Minimum="0" Maximum="5000" Step="10" Format="0" Width="80"/>
            <TextBlock Style="{StaticResource Unit}" Text="mm above level"/>
        </StackPanel>

        <TextBlock Grid.Row="6" Grid.ColumnSpan="2" Style="{StaticResource CadToBim.SectionTitle}" Text="LAYER NAMES (COMMA-SEPARATED, EN + MALAY)" Margin="0,12,0,6"/>
        <TextBlock Grid.Row="7" Style="{StaticResource RowLabel}" Text="Wall hints"/>
        <TextBox Grid.Row="7" Grid.Column="1" Text="{Binding WallHints, UpdateSourceTrigger=PropertyChanged}" Margin="0,2"
                 ToolTip="A layer whose name contains any of these holds walls"/>
        <TextBlock Grid.Row="8" Style="{StaticResource RowLabel}" Text="Opening hints"/>
        <TextBox Grid.Row="8" Grid.Column="1" Text="{Binding OpeningHints, UpdateSourceTrigger=PropertyChanged}" Margin="0,2"
                 ToolTip="A layer whose name contains any of these holds doors or windows"/>
        <TextBlock Grid.Row="9" Style="{StaticResource RowLabel}" Text="Exclude (globs)"/>
        <TextBox Grid.Row="9" Grid.Column="1" Text="{Binding ExcludeGlobs, UpdateSourceTrigger=PropertyChanged}" Margin="0,2"
                 ToolTip="Layers never read: * stands for any run of characters (FURN*, *-DIM*)"/>

        <TextBlock Grid.Row="10" Grid.ColumnSpan="2" Style="{StaticResource CadToBim.SectionTitle}" Text="NEW .RVT TEMPLATE" Margin="0,12,0,6"/>
        <TextBlock Grid.Row="11" Style="{StaticResource RowLabel}" Text="Template (.rte)"/>
        <DockPanel Grid.Row="11" Grid.Column="1" Margin="0,2">
            <Button DockPanel.Dock="Right" Style="{StaticResource CadToBim.Button}" Content="Browse…" Click="OnBrowse" Margin="6,0,0,0"/>
            <TextBox Text="{Binding TemplatePath, UpdateSourceTrigger=PropertyChanged}"
                     ToolTip="Blank = Revit's default project template. Pick one with door and window families loaded."/>
        </DockPanel>

        <TextBlock Grid.Row="12" Grid.ColumnSpan="2" x:Name="ErrorText" Foreground="{DynamicResource Cp.Red}" TextWrapping="Wrap" Margin="0,10,0,0"/>

        <StackPanel Grid.Row="13" Grid.ColumnSpan="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,14,0,0">
            <Button Style="{StaticResource CadToBim.Button}" Content="Cancel" IsCancel="True" Click="OnCancel" MinWidth="80" Margin="0,0,8,0"/>
            <Button Style="{StaticResource CadToBim.Primary}" Content="OK" IsDefault="True" Click="OnOk" MinWidth="80" HorizontalAlignment="Right"/>
        </StackPanel>
    </Grid>
</Window>
```

`UI/CadToBim/CadToBimSettingsWindow.xaml.cs`:
```csharp
using System;
using System.Windows;
using RevitWebAppSync.Services.CadToBim;
using RevitWebAppSync.UI.Copilot;

namespace RevitWebAppSync.UI.CadToBim
{
    /// <summary>Small modal behind the pane's gear: the six numbers, the two hint lists, the
    /// exclusion globs and the template override. OK validates, writes back and saves.</summary>
    public partial class CadToBimSettingsWindow : Window
    {
        private readonly CadToBimSettings _settings;
        private readonly CadToBimSettingsDraft _draft;

        public CadToBimSettingsWindow(CadToBimSettings settings)
        {
            CopilotTheme.EnsureLoaded();
            CadToBimTheme.EnsureLoaded();
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _draft = CadToBimSettingsDraft.From(settings);
            InitializeComponent();
            DataContext = _draft;
            // A Window is not inside the pane, so it mounts the theme brushes itself.
            Resources.MergedDictionaries.Add(CopilotTheme.NewThemeDictionary());
            Resources.MergedDictionaries.Add(CadToBimTheme.NewThemeDictionary());
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choose the project template for new files",
                Filter = "Revit template (*.rte)|*.rte|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog(this) == true) _draft.TemplatePath = dialog.FileName;
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            string error = _draft.Validate();
            if (error != null)
            {
                ErrorText.Text = error;
                return;
            }

            _draft.WriteTo(_settings);
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                // Applied for this session even when %APPDATA% is read-only; say so.
                ErrorText.Text = "Settings applied, but could not be saved: " + ex.Message;
                DialogResult = true;
                return;
            }
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run (Mac): `cd /Users/ashraf/development/bina/revit-addin-sync && ~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true -p:SkipRevitSources=true 2>&1 | grep -E "error|Build succeeded" && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net8.0-windows 2>&1 | grep -E "error|Build succeeded" && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 2>&1 | grep -E "error|Build succeeded" && ~/.dotnet/dotnet build RevitWebAppSync.csproj -f net10.0-windows 2>&1 | grep -E "error|Build succeeded"`  Expected: `Build succeeded` four times (this is also Task 18's deferred add-in gate).
Run (Windows): `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CadToBim|FullyQualifiedName~CadOverlayViewport|FullyQualifiedName~XamlResourceScope"` Expected: PASS (all rows, including `CadToBimSettingsWindow.xaml`).
Manual row (Windows) = row E2 of the smoke checklist (Task 22): gear → window opens centred on Revit, in front of it; set SMin 400 / SMax 300 → red "Minimum thickness must be smaller…" and stays open; fix → OK → pane re-detects with the new range; reopen → values persisted (`%APPDATA%\RevitWebAppSync\config.json` key `cadToBim`); Copilot dark theme → window chrome dark.

- [ ] **Step 5: Stage**

`git add UI/CadToBim/CadToBimPaneHost.cs UI/CadToBim/CadToBimSettingsDraft.cs UI/CadToBim/CadToBimSettingsWindow.xaml UI/CadToBim/CadToBimSettingsWindow.xaml.cs Tests/CadToBimSettingsDraftTests.cs Tests/Tests.csproj`   (repo owner rule: executors STAGE, never commit)

---

### Task 20: bina-ai phantom-tool cleanup (`cad_walls_to_centerlines`)


Repo: `/Users/ashraf/development/bina/bina-ai`. Branch: `git checkout -b chore/remove-phantom-cad-centerlines-tool origin/develop`. Independent of Tasks 1–19 (different repo).

**Two red gates, one pre-existing.** `tests/test_tool_help.py::test_registry_keys_are_real_tools` already FAILS on `develop` today (`TOOL_HELP entry 'cad_walls_to_centerlines' is not a real tool`) — it guards the KEYS of `TOOL_HELP`; this task turns it green as a side effect of deleting the entry, and adds a new test in `tests/test_tool_contracts.py` that guards the TEXT the model reads (docstrings and help bodies naming a registry tool Python does not expose), which the existing test cannot see. Do not present the existing test as new. Measured 2026-09-08: `registry − ALL_TOOLS` = `{apply_family_naming_fixes, cad_walls_to_centerlines, dwg_open_attachment, get_family_naming_facts, pdf_open_attachment}`, and the only one referenced from any docstring or help body is `cad_walls_to_centerlines` (from `cad_walls_from_attachment`'s docstring and help entry) — so the new test fails on exactly two references and is noise-free after the fix. `tests/test_tool_help.py` carries a second, unrelated, pre-existing failure, `test_descriptions_stay_lean` (`list_dimensions` description 637 chars vs the 420 cap, from `1a5d0ac`) — deselect it, do not fix it in this PR. Line numbers below are `origin/develop` numbers (the working tree on `feat/concise-reply-and-door-csv-export` has `cad_walls_from_attachment`'s docstring at 811–818 instead of 806–812; `tool_help.py` is identical) — cut the branch from `origin/develop` first. The addin C# executor `cad_walls_to_centerlines` exists only on addin `origin/main` (`BinaVibe/Mcp/Tools/ToolRegistry.cs:56`), not `origin/develop`; the snapshot `tests/data/addin_registry_tools.txt` was cut from main and keeps the name — harmless, see the "Unchanged on purpose" note.

**Files:**
- Modify: `tests/test_tool_contracts.py:70` (append one test after `test_every_defined_tool_is_registered`)
- Modify: `app/agents/revit/copilot/tool_help.py:151-154` (rewrite two lines of the `cad_walls_from_attachment` help body)
- Modify: `app/agents/revit/copilot/tool_help.py:195-206` (delete the `cad_walls_to_centerlines` entry)
- Modify: `app/agents/revit/copilot/tools.py:806-812` (docstring of `cad_walls_from_attachment`)
- Test: `tests/test_tool_contracts.py`, `tests/test_dwg_tools.py`, `tests/test_tool_help.py::test_registry_keys_are_real_tools`
- Unchanged on purpose: `tests/data/addin_registry_tools.txt:14` (`cad_walls_to_centerlines`). The snapshot mirrors the addin registry on `main`, which does have the C# executor. The only consumer is `test_executable_manifest_matches_registry_contract`, which asserts `manifest ⊆ registry`; an extra registry line can never fail that direction, so the line stays until the snapshot is next regenerated from C#.

**Interfaces:**
- Consumes: `app.agents.revit.copilot.tool_help.TOOL_HELP: dict[str, str]`; `app.agents.revit.copilot.tools.ALL_TOOLS` (agno `Function` objects with `.name`, `.entrypoint`); `tests/data/addin_registry_tools.txt` (one registry name per line).
- Produces: nothing other tasks rely on. After this task no model-visible text in bina-ai names `cad_walls_to_centerlines`.

- [ ] **Step 1: Write the failing test**

Append to `tests/test_tool_contracts.py` directly after `test_every_defined_tool_is_registered` (after line 70 on develop, before `test_server_side_tools_are_never_cold`):

```python
# A registry name that Python does not expose is a phantom: the addin may run
# it, but the agent has no schema for it, so any docstring or help text that
# steers the model toward it ends in "unknown tool". Measured 2026-09-08:
# cad_walls_from_attachment's docstring and TOOL_HELP entry both said "for
# model CAD use cad_walls_to_centerlines", a tool with no Python definition.
# tests/test_tool_help.py::test_registry_keys_are_real_tools guards the KEYS
# of TOOL_HELP; this guards the TEXT the model reads.
def test_model_visible_text_never_names_a_phantom_tool():
    from app.agents.revit.copilot.tool_help import TOOL_HELP

    registry = set(SNAPSHOT.read_text().split())
    real = {t.name for t in T.ALL_TOOLS}
    phantoms = registry - real
    assert phantoms, "expected at least one registry-only name (pane-only openers)"

    texts = {f"docstring:{t.name}": inspect.getdoc(t.entrypoint) or "" for t in T.ALL_TOOLS}
    texts.update({f"help:{key}": body for key, body in TOOL_HELP.items()})

    offenders = sorted(
        f"{where} -> {name}"
        for where, text in texts.items()
        for name in phantoms
        if re.search(rf"\b{re.escape(name)}\b", text)
    )
    assert not offenders, (
        f"model-visible text names tools the agent cannot call: {offenders} — "
        "point the text at a tool in ALL_TOOLS or define the tool"
    )
```

(`inspect`, `re`, `SNAPSHOT` and `T` are already imported at the top of the file.)

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /Users/ashraf/development/bina/bina-ai && uv run pytest tests/test_tool_contracts.py tests/test_dwg_tools.py -x -q -p no:cacheprovider`
Expected: FAIL with `model-visible text names tools the agent cannot call: ['docstring:cad_walls_from_attachment -> cad_walls_to_centerlines', 'help:cad_walls_from_attachment -> cad_walls_to_centerlines']`

Also confirm the existing red test: `uv run pytest tests/test_tool_help.py -q -p no:cacheprovider --deselect tests/test_tool_help.py::test_descriptions_stay_lean`
Expected: FAIL with `TOOL_HELP entry 'cad_walls_to_centerlines' is not a real tool`

- [ ] **Step 3: Write minimal implementation**

3a. `app/agents/revit/copilot/tool_help.py:151-154` — before:

```python
    "cad_walls_from_attachment": (
        "Create Revit walls from an ATTACHED DWG — no Revit link needed.\n\n"
        "ATTACHMENTS ONLY (att:<guid>). For model CAD (linked in Revit), use "
        "cad_walls_to_centerlines instead.\n\n"
```

after:

```python
    "cad_walls_from_attachment": (
        "Create Revit walls from an ATTACHED DWG — no Revit link needed.\n\n"
        "ATTACHMENTS ONLY (att:<guid>). There is no link-based wall tracer: for "
        "CAD linked or imported in the model, read it with extract_cad_geometry "
        "and build walls from its segments with create_wall.\n\n"
```

3b. `app/agents/revit/copilot/tool_help.py:195-206` — delete the whole entry, i.e. these twelve lines, leaving `"cad_doors_from_attachment": (` directly after the closing `),` of `cad_walls_from_attachment`:

```python
    "cad_walls_to_centerlines": (
        "Create Revit walls from a CAD LINK (ImportInstance in Revit model).\n\n"
        "MODEL CAD ONLY. For attachments, use cad_walls_from_attachment instead — "
        "it reads geometry directly via ACadSharp and doesn't need a Revit link.\n\n"
        "Workflow:\n"
        "1. Link DWG into Revit (batch_link_models or manual)\n"
        "2. Call with create=False to preview proposed walls\n"
        "3. Call with create=True to build walls\n\n"
        "Args: import_id (from list_cad_links), layer_filter, level, type_name, "
        "height_mm, thickness_to_type (band mapping), create.\n\n"
        "The attachment path (cad_walls_from_attachment) is simpler — no link step."
    ),
```

3c. `app/agents/revit/copilot/tools.py:806-812` (docstring of `async def cad_walls_from_attachment`, which starts at line 792 on develop) — before:

```python
    """Create Revit walls from an ATTACHED DWG — no Revit link needed.

    Reads line geometry directly via ACadSharp, pairs parallel lines into wall
    centerlines (same algorithm as ``cad_walls_to_centerlines``), then creates
    walls in one Transaction.

    ATTACHMENTS ONLY (``att:<guid>``). For model CAD (linked in Revit), use
    ``cad_walls_to_centerlines``.
```

after:

```python
    """Create Revit walls from an ATTACHED DWG — no Revit link needed.

    Reads line geometry directly via ACadSharp, pairs parallel lines into wall
    centerlines, then creates walls in one Transaction.

    ATTACHMENTS ONLY (``att:<guid>``). For CAD linked in the model read it with
    ``extract_cad_geometry`` and build walls with ``create_wall``.
```

`extract_cad_geometry` and `create_wall` are both in `ALL_TOOLS` (verified). The advertised description of `cad_walls_from_attachment` is the docstring up to `Args:`; it was 342 chars and the rewrite keeps it under the 420-char lean cap (`test_descriptions_stay_lean`), check with:

`PYTHONPATH=. uv run python -c "import app.agents.revit.copilot.tools as T; t=[t for t in T.ALL_TOOLS if t.name=='cad_walls_from_attachment'][0]; print(len(t.description))"` — Expected: a number ≤ 420.

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /Users/ashraf/development/bina/bina-ai && uv run pytest tests/test_tool_contracts.py tests/test_dwg_tools.py -x -q -p no:cacheprovider`
Expected: PASS (17 passed; baseline before this task was 16 passed)

Run: `uv run pytest tests/test_tool_help.py -q -p no:cacheprovider --deselect tests/test_tool_help.py::test_descriptions_stay_lean`
Expected: PASS (4 passed, 1 deselected)

Run: `grep -rn "cad_walls_to_centerlines" app/ tests/ docs/ --include="*.py" --include="*.md"`
Expected: no output (the only remaining hit in the repo is `tests/data/addin_registry_tools.txt:14`, which is not `.py`/`.md`).

Never run the full suite (repo rule; 20 files hang on staging Postgres at collection).

- [ ] **Step 5: Stage**

`git add tests/test_tool_contracts.py app/agents/revit/copilot/tool_help.py app/agents/revit/copilot/tools.py`   (repo owner rule: executors STAGE, never commit)

Suggested commit subject for the owner: `chore(copilot): remove phantom cad_walls_to_centerlines references`. PR target `develop`; then `develop → staging` per the standing rule. No deploy, no ingest, no prompt change (help text and docstrings load at import; the staging container picks them up on its next image).

---

### Task 21: close stale branches (owner runs these; nothing is executed by the plan)

**Files:**
- Create: none
- Modify: none (remote refs only)
- Test: none — verification is the `gh`/`git` output quoted below

**Interfaces:**
- Consumes: spec §2 branch fates; Task 1 has merged `origin/feat/cad2bim` into `feat/cad-to-bim-pane`.
- Produces: `feat/cad-to-bim-viewer` gone from both remotes; `feat/cad-walls-to-centerlines` gone from the addin remote.

Branch facts (measured 2026-09-08, none merged into either `develop`):

| Repo | Branch | Ahead of `develop` | Tip | Fate |
|---|---|---|---|---|
| bina-ai | `feat/cad-to-bim-viewer` | 11 | `023e26d` | delete (spec §2) |
| bina-ai | `feat/cad-walls-to-centerlines` | 4 | `5327099` | owner decision — holds the only Python def of the tool Task 20 unreferences; not named in spec §2. Recommend delete for the same reason (no C# executor on addin `develop`). |
| bina-ai | `feat/copilot-cad-link` | 0 (merged) | — | optional housekeeping delete |
| addin | `feat/cad-to-bim-viewer` | 12 | `023e26d`-era | delete (spec §2) |
| addin | `feat/cad-walls-to-centerlines` | 15 | `c788f18` | delete (spec §2: no unique content) |
| addin | `feat/cad2bim` | 33 | — | KEEP until the feature PR merges (Task 1 merged it into `feat/cad-to-bim-pane`) |
| addin | `feat/cad-segment-stitching` | 24 | — | KEEP (spec §2: later convergence) |

- [ ] **Step 1: Check for open or merged PRs on every branch to be deleted**

Run:
```bash
gh pr list --repo binacloudmy/bina-ai --head feat/cad-to-bim-viewer --state all
gh pr list --repo binacloudmy/bina-ai --head feat/cad-walls-to-centerlines --state all
gh pr list --repo binacloudmy/revit-addin-sync --head feat/cad-to-bim-viewer --state all
gh pr list --repo binacloudmy/revit-addin-sync --head feat/cad-walls-to-centerlines --state all
```
Expected: `no pull requests match your search` for each. If a PR is OPEN, close it first with a reason so the decision is on the record:
```bash
gh pr close <number> --repo <owner/repo> --comment "Superseded by the CAD to BIM pane (docs/superpowers/specs/2026-09-08-cad-to-bim-pane-design.md §2); branch closed."
```

- [ ] **Step 2: Optional safety net — archive tags before deleting (each branch carries 4–15 unmerged commits)**

Run:
```bash
cd /Users/ashraf/development/bina/bina-ai
git fetch origin
git tag archive/feat-cad-to-bim-viewer origin/feat/cad-to-bim-viewer
git push origin archive/feat-cad-to-bim-viewer

cd /Users/ashraf/development/bina/revit-addin-sync
git fetch origin
git tag archive/feat-cad-to-bim-viewer origin/feat/cad-to-bim-viewer
git tag archive/feat-cad-walls-to-centerlines origin/feat/cad-walls-to-centerlines
git push origin archive/feat-cad-to-bim-viewer archive/feat-cad-walls-to-centerlines
```
Expected: `[new tag]` lines. Skip this step if the owner is happy to rely on GitHub's 90-day reflog for deleted branches.

- [ ] **Step 3: Delete the remote branches**

Run:
```bash
git -C /Users/ashraf/development/bina/bina-ai push origin --delete feat/cad-to-bim-viewer
git -C /Users/ashraf/development/bina/revit-addin-sync push origin --delete feat/cad-to-bim-viewer feat/cad-walls-to-centerlines
```
Expected: ` - [deleted]         feat/cad-to-bim-viewer` (and the second name for the addin).

If the owner takes the recommendation on the bina-ai copy:
```bash
git -C /Users/ashraf/development/bina/bina-ai push origin --delete feat/cad-walls-to-centerlines
```

- [ ] **Step 4: Verify and prune local tracking refs**

Run:
```bash
git -C /Users/ashraf/development/bina/bina-ai fetch --prune && git -C /Users/ashraf/development/bina/bina-ai branch -r | grep -i cad
git -C /Users/ashraf/development/bina/revit-addin-sync fetch --prune && git -C /Users/ashraf/development/bina/revit-addin-sync branch -r | grep -i cad
```
Expected: bina-ai shows at most `origin/feat/cad-walls-to-centerlines` (if kept) and `origin/feat/copilot-cad-link`; addin shows exactly `origin/feat/cad-segment-stitching` and `origin/feat/cad2bim` (plus the feature branch once pushed).

- [ ] **Step 5: Stage**

Nothing to stage — this task changes remote refs only. Do NOT delete `feat/cad2bim` or `feat/cad-segment-stitching`; `feat/cad2bim` is deleted only after the `feat/cad-to-bim-pane` PR merges to `develop`.

**Owner-decision list for this task:** bina-ai `feat/cad-walls-to-centerlines` (4 commits, tip `5327099`, holds the only Python `async def cad_walls_to_centerlines` at `tools.py:1475`; not merged, not an ancestor of `feat/cad-to-bim-viewer`; spec §2 names only the addin copy) — recommended delete, same check-then-delete commands above; if kept, Task 20's new test fails on that branch if it is ever rebased, which is the intended signal. bina-ai `feat/copilot-cad-link` (0 ahead, merged) — optional housekeeping delete.

---

### Task 22: Windows smoke checklist

**Files:**
- Create: `docs/superpowers/plans/2026-09-08-cad-to-bim-smoke.md` (addin repo `/Users/ashraf/development/bina/revit-addin-sync`)
- Modify: none
- Test: none (this file IS the manual test; the owner fills it on the Windows rig)

**Interfaces:**
- Consumes: the installed build from `installer\build-installer.ps1` (produces `RevitCopilot-<ver>-setup.exe` at the repo root; installs the loader to `%APPDATA%\Autodesk\Revit\Addins\2024` (net48) and `\2025` (net8) and the payload to `%LOCALAPPDATA%\Bina\RevitSync\versions\<ver>\net48` and `\net8.0`); the ribbon panel "CAD to BIM" and button "CAD to\nBIM" (spec §4); `CadToBimViewModel` counts `WallCount/DoorCount/WindowCount/RoomCount/ErasedCount/ForcedCount`; `BuildReport.SkippedOpenings`; the headless CLI `Cad2Bim/Cad2Bim.Headless` (kept in the folder, not linked into the addin).
- Produces: the PR gate record (spec §7 step 4, §8 "Manual Windows smoke").


Notes folded from the other sections: CLI vs pane counts will not be identical (`Program.cs` runs a richer hand-rolled pipeline than `ClassificationService.Classify`+`Elaborate`; Task 17's `Detect` mirrors the old convert command — wall-layer word filter, `ClassifyWallsElsewhere`, `WallsFromOutlines`, `DeduplicateWalls`, `ClassifyOpeningsFromSymbols` — so the walls band may be tightened to "identical" once rows 6–8 show it). Row 1 depends on the installer carrying `System.Memory.dll` into the `net48` payload (spec §6: ACadSharp 3.6.51 → System.Memory 4.6.3 on net48) — `installer/prune-payload.ps1` exists; check it does not drop `System.Memory.dll`, `ACadSharp.dll`, `CSMath.dll`, `CSUtilities.dll`. Sample DWGs are in no branch (`Cad2Bim/.gitignore` has `*.dwg`); the checklist tells the owner to copy them from the engine author's machine. Row 5 (Civil 3D) is implemented by Task 17's `UnsupportedSource`.

- [ ] **Step 1: Write the checklist file**

Create `docs/superpowers/plans/2026-09-08-cad-to-bim-smoke.md` with exactly this content:

````markdown
# CAD to BIM pane — Windows smoke checklist

**Date:** 2026-09-08  **Spec:** `docs/superpowers/specs/2026-09-08-cad-to-bim-pane-design.md` §3, §6, §8
**Gate:** every row PASS on both columns before the `feat/cad-to-bim-pane → develop` PR is opened. A FAIL is a finding to fix on the branch, not a note.
**Rig:** Windows, Revit 2024 (net48 payload) and Revit 2025 (net8.0 payload), 100% and 150% display scaling available, .NET 8 SDK installed (for the CLI).

## Prerequisites

1. Build from the feature branch at the commit under test:
   `powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version <x.y.z>` → `RevitCopilot-<x.y.z>-setup.exe` at the repo root.
2. `BINA_SYNC_PLUGIN_DIR` must be **unset** on the rig (a dev box with it set loads the repo `bin\` build instead of the installed payload and the install rows prove nothing).
3. Sample drawings. `Cad2Bim/README.md` §"Bring your own drawing": `*.dwg` is git-ignored in `Cad2Bim/` on purpose ("Copy your test file next to the checkout and pass its path") — the repo names no sample path. The original set lives on the engine author's machine; copy three plans from it to `C:\cad2bim-samples\sample-01.dwg`, `sample-02.dwg`, `sample-03.dwg` and one Civil 3D-authored DWG to `C:\cad2bim-samples\civil3d.dwg`. Record the real file names in the table.
4. Reference counts from the headless CLI, run once per sample from the repo root **before** opening Revit (same SMin/SMax as the pane, default 50 400):
   ```
   dotnet run --project Cad2Bim\Cad2Bim.Headless -- C:\cad2bim-samples\sample-01.dwg 50 400 --faces --symbols
   ```
   Read these lines from its output: `walls     N` (under `-- walls --`, after dedupe), `doors     N` and `other     N` (under `-- openings --`), `rooms     N` (under `-- spaces --`). Paste them into rows 6–8. Mapping: pane Walls ↔ `walls`, Doors ↔ `doors`, Windows ↔ `other`, Rooms ↔ `rooms`.
   The CLI is the *measuring surface*, not ground truth: `Program.cs` pre-filters to wall-named layers, adds `ClassifyWallsElsewhere` + `WallsFromOutlines`, and finds openings with `ClassifyOpeningsFromSymbols`, whereas the pane runs `ClassificationService.Classify` + `Elaborate`. Expect the same band, not identical numbers: walls within 10%, doors/rooms within ±2. A larger gap is a finding — write it in the Notes column with both numbers.
5. A template with no door/window families for row 14: in Revit, New → Project template (Metric), Manage → Purge Unused (all), Save As `C:\cad2bim-samples\NoFamilies.rte`.

## Checklist

Fill each result cell with PASS / FAIL / n/a plus the measured value where the Expected column asks for one.

| # | Step | Expected | 2024 (net48) | 2025 (net8) |
|---|---|---|---|---|
| 1 | Run `RevitCopilot-<x.y.z>-setup.exe`; then check the folders | `%APPDATA%\Autodesk\Revit\Addins\2024\BinaSync.addin` and `...\2025\BinaSync.addin` exist; `%LOCALAPPDATA%\Bina\RevitSync\versions\<x.y.z>\net48\` and `\net8.0\` each contain `RevitWebAppSync.dll` and `ACadSharp.dll`; the `net48` folder also contains `System.Memory.dll` | | |
| 2 | Start Revit at **100%** display scaling; open the Bina tab | Panel "CAD to BIM" sits after BINA AI; one large button "CAD to BIM" with a crisp 32 px icon (not blank, not the generic placeholder); tooltip shows | | |
| 3 | Set display scaling to **150%**, sign out/in, start Revit | Same panel and button; icon not blurred, not clipped; button label wraps to two lines | | |
| 4 | With **no project open**, click CAD to BIM; cancel the dialog; click again and pick `sample-01.dwg` | Button is enabled with no document; first click shows an Open dialog filtered to `*.dwg;*.dxf`; cancel does nothing; second click docks the pane on the right, status "Detecting…", Revit stays responsive, then linework + green walls + blue openings + amber rooms + counts appear | | |
| 5 | Click CAD to BIM and pick `civil3d.dwg` | No crash. Status line shows the unsupported-source text from spec §3 ("run EXPORTTOAUTOCAD first"); pane stays usable and "Change…" opens the dialog again | | |
| 6 | Open `sample-01.dwg` (real name: ______); time from OK to counts | Detect time ______ s (record). Pane counts W/D/Wi/R = ____ / ____ / ____ / ____ vs CLI `walls`/`doors`/`other`/`rooms` = ____ / ____ / ____ / ____; within the band in Prerequisite 4 | | |
| 7 | Open `sample-02.dwg` (real name: ______) | Same as row 6 — record both sets of numbers and the detect time | | |
| 8 | Open `sample-03.dwg` (real name: ______) | Same as row 6 — record both sets of numbers and the detect time | | |
| 9 | On `sample-01`, select Erase, click one wall (within 150 mm of its centerline); then Confirm with "Add to this project" in an open metric project | Wall turns red dashed; Walls −1, Erased 1; openings hosted on it and rooms bounded by it re-elaborate. After build the Revit wall count equals the pane's Walls count and the erased wall is absent at that location | | |
| 10 | Undo the build (Ctrl+Z); select Brush; drag a box over the partition Detect missed; Confirm | A green wall with a cyan box appears inside the drag box; Walls +1, Brushed 1. After build that wall exists in Revit at the brushed location | | |
| 11 | In a project with levels "Level 1" and "Level 2", open `sample-02`, target "Add to this project", level picker = Level 1, Confirm; then press Ctrl+Z **once** | Walls, doors, windows and rooms appear on Level 1 only (check the Level parameter on a wall and a door). One Ctrl+Z removes every created element; one Ctrl+Y restores them all | | |
| 12 | Target "Save as new .rvt", Confirm on `sample-03` | `C:\cad2bim-samples\sample-03.rvt` is created beside the DWG; it becomes the active document with the elements visible; close it; File → Open from disk → same elements present | | |
| 13 | Repeat row 12 with `sample-03.rvt` already on disk; answer Cancel, then repeat and answer OK | Cancel: an overwrite prompt appeared and the existing file's modified time is unchanged, nothing else saved. OK: file replaced (new modified time) and opened | | |
| 14 | Pane ⚙ → template path = `C:\cad2bim-samples\NoFamilies.rte`; target "Save as new .rvt"; Confirm on `sample-01` | Walls and rooms are built; status/report shows skipped openings = the pane's Doors + Windows count (no doors or windows placed, no exception); restore the template setting afterwards | | |
| 15 | After a successful "Add to this project" build, Brush two more walls, Confirm again | Only the two brushed walls are added: Revit wall count rises by exactly 2; no duplicate wall sits on any previously built centerline (select-all walls → Properties count) | | |
| 16 | **2024 only.** After rows 4–6 on Revit 2024, open the newest `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2024\Journals\journal.*.txt` | No `FileLoadException`, no `Could not load file or assembly 'System.Memory`, no `ACadSharp` load error after the CAD to BIM click; Detect completed (row 6 has counts) | | n/a |
| 17 | With the pane open and a drawing loaded: switch the active view to a 3D view and back to the plan; then close the project (pane still open); then open another project | Pane keeps its drawing and counts across the view switch; after the document close the pane stays open and its controls respond; "Add to this project" works in the newly opened project | | |
| 18 | Erase a wall **after** it was built (row 9 project), then Confirm again | The built wall is not deleted (spec §3: undo is the drafter's tool); no new wall is created for it; counts show Erased +1 | | |

## Builder rows (Task 11 — `Cad2BimBuilder`)

Same rig, both columns. Prefixed `C` so the numbered-row checks in Task 22 Step 2 stay at 18.

| # | Row | Expected | 2024 (net48) | 2025 (net8) |
|---|-----|----------|---|---|
| C1 | AddToProject, Level 1, Stack, sample DWG with ≥2 plans, Confirm | Walls on Level 1 and on "CAD Level 2" (elevation = Level 1 + 3000 mm); report Walls/Doors/Windows/Rooms equal what the pane counted minus Skipped*; report Elapsed shown | | |
| C2 | After C1, one Ctrl+Z | Every wall, door, window, room and "CAD Level 2" gone; Undo list shows one entry "CAD to BIM" | | |
| C3 | AddToProject, KeepPosition, same DWG linked at origin in the plan view | Walls sit on top of the linked linework; all on Level 1; no "CAD Level N" created | | |
| C4 | NewFile (Save as new .rvt), default template | `<dwgdir>/<dwgname>.rvt` exists, becomes the active document, reopens after Revit restart; report OutputPath = that path | | |
| C5 | NewFile with Settings → template override pointing at a metric template with no door/window families | Error null, Doors = Windows = 0, SkippedOpenings > 0, walls present | | |
| C6 | AddToProject with no document open | Status shows "Open a project first."; nothing created; controls unlock | | |
| C7 | After C1, Brush a box that adds N walls, Confirm again | Report Walls = N (only the brushed walls); no duplicates of C1 walls; second undo entry "CAD to BIM" | | |
| C8 | NewFile with `TemplatePath` = an existing non-.rte file (forced failure) | Error = innermost Revit message; no extra document in Revit's Switch Windows list; no .rvt written | | |
| C9 | AddToProject on Level 2 (picker), Stack, 2-plan DWG | First plan on Level 2, second on the next existing level above (or "CAD Level 2" at Level 2 + 3000 mm when none) | | |

## Pane rows (Tasks 16–19)

| # | Row | Expected | 2024 (net48) | 2025 (net8) |
|---|-----|----------|---|---|
| E1 | With a drawing loaded, toggle the Copilot pane's moon (dark/light) button, then wheel-zoom in and out | Overlay re-colours to the dark/light palette (green/blue/amber/red/cyan swap to their dark values); wall centrelines stay 2 px and bands never thinner than 3 px at any zoom; room names stay upright and centred | | |
| E2 | Pane ⚙ → set SMin 400 / SMax 300 → OK; then fix to 50 / 400 → OK; reopen ⚙ | First OK: red "Minimum thickness must be smaller than the maximum." and the window stays open. Second OK: window closes, pane re-detects with the new range. Reopen: values persisted (`%APPDATA%\RevitWebAppSync\config.json` has a `cadToBim` key); window is centred on Revit and follows the Copilot dark theme | | |
| E3 | Change a layer's role chip (e.g. `0` → WALL) on a drawing with walls on layer 0; then change the thickness range | Re-detect runs (status "Reading…" → counts); walls previously erased stay erased and walls already built stay out of the next Confirm after the re-detect (carry-forward by centreline) | | |

## Sign-off

| Field | Value |
|---|---|
| Build version (`-Version`) | |
| Branch / commit | `feat/cad-to-bim-pane` @ |
| Tester / date | |
| Revit 2024 build | |
| Revit 2025 build | |
| Verdict (all rows PASS?) | |
| Findings (row → note) | |
````

- [ ] **Step 2: Verify the file renders as a table**

Run: `cd /Users/ashraf/development/bina/revit-addin-sync && grep -c '^| [0-9]' docs/superpowers/plans/2026-09-08-cad-to-bim-smoke.md`
Expected: `18`

Run: `awk -F'|' '/^\| [0-9]/ && NF != 7 {print "bad row:", $0}' docs/superpowers/plans/2026-09-08-cad-to-bim-smoke.md`
Expected: no output (every numbered row has exactly 5 cells).

- [ ] **Step 3: Write minimal implementation**

Not applicable — the document is the deliverable. Nothing in the addin build changes.

- [ ] **Step 4: Run test to verify it passes**

Run: `git -C /Users/ashraf/development/bina/revit-addin-sync status --short docs/superpowers/plans/`
Expected: `?? docs/superpowers/plans/2026-09-08-cad-to-bim-smoke.md`

- [ ] **Step 5: Stage**

`git add docs/superpowers/plans/2026-09-08-cad-to-bim-smoke.md`   (repo owner rule: executors STAGE, never commit)

---

## Self-review record

Assembled 2026-09-08 from six section drafts (A: Tasks 1–3, B: 4–9, C: 10–12, D: 13–15, E: 16–19, F: 20–22) against `plan-contract.md`; each section's "Contract corrections" and "Notes for assembler" were folded into the bodies above and removed. Task bodies and code blocks are otherwise verbatim from the sections.

### (a) Spec coverage — which task implements what

| Spec item | Task(s) |
|---|---|
| §3 Open: `OpenCadToBimCommand`, OTA gate, `ZeroDocCommandAvailability`, `OpenFileDialog *.dwg;*.dxf`, cancel = nothing | 3 |
| §3 Detect: `Units.Resolve` → `ModelSource.Read` with settings `LayerFilter` → classify → elaborate → `ClusterPlans`, `IProgress<string>`, cancel on "Change…"/pane close, innermost message in status | 17 (Detect/OpenAsync/Cancel), 18 (Change… + Unloaded → Cancel) |
| §3 Detect: unsupported source apps → "run EXPORTTOAUTOCAD" text | 17 (`UnsupportedSource`, `Describe`); smoke row 5 (22) |
| §3 Correct: layer checkbox = visibility only; role chip edits Include/Exclude and re-detects | 17 (`CadLayerViewModel`, `CycleRole`, `BaseFilter`), 18 (chip/checkbox XAML) |
| §3 Correct: thickness range / wall height `NumericScrubBox`, 300 ms debounce, re-detect | 17 (`Bounce`/`OnDebounced`), 18 (implicit `NumericScrubBox` style + bindings) |
| §3 Correct: storey mode radio | 18 (XAML) → 17 `Storeys` → 11 `ClusterAndPlace` |
| §3 Erase: 150 mm hit-test, red dashed, re-elaborate openings/rooms | 16 (`EraseToleranceMm`, `NearestWall`, erased layer), 17 (`ToggleErase` → `RecountAndRedraw`), 14 (`Erased`) |
| §3 Brush: guards off in try/finally, single-line ≥ 500 mm at median, `WallFromCloud` fallback, `Forced`, cyan box | 15 (`Cad2BimBrush`), 16 (drag box + boxes layer), 17 (`BrushAsync`, >40 confirm), 14 (`Forced`, `MedianThicknessMm`) |
| §3 Counts + footer warning for unpaired wall-layer linework | 17 (`UpdateCounts`, `Warning`), 18 (WILL BE BUILT + footer) |
| §3 Confirm: plan = classified − erased + forced − alreadyBuilt; set request, raise event, lock controls | 14 (`Pending`), 17 (`Confirm`), 12 (`Request`, `ExternalEventRaiser`) |
| §3 Build steps 1–7 (target doc, `TransactionGroup`, walls with `DisallowWallJoinAtEnd` + `SilenceJoinFailures`, regenerate, openings by dictionary host, rooms from boundary lines, assimilate + survivor re-query) | 11 (+ 10 `SilenceJoinFailures`) |
| §3 Build step 8: `SaveAs` beside the drawing, overwrite prompt, close + `OpenAndActivateDocument` | 11 (save/close/activate), 17 (`OverwritePrompt` before raising), 18 (TaskDialog wiring) |
| §3 Build step 9: `BuiltWallIds` so a later Confirm appends only new walls; erase after build does not delete | 14 (`MarkBuilt`, `Pending`, `CarryForwardFrom`), 17 (`ApplyReport`) |
| §3 Failure: rollback, innermost message, controls unlock; save failure | 11 (`RollBack`, `Innermost`), 17 (`ApplyReport`). Deviation: a `NewProjectDocument` has no window, so on save failure the builder closes it and reports; walls stay pending and the next Confirm rebuilds — flag in the PR description |
| §3 Settings (⚙): defaults, EN + Malay hints, exclusion globs, template override, `config.json` `cadToBim` key via `BinaConfig` | 13 (`CadToBimSettings` + `BinaConfig.CadToBim` passthrough), 19 (draft + window), 18 (gear) |
| §4 `App.cs`: panel "CAD to BIM" after BINA AI, one `PushButtonData`, `RegisterDockablePane`, `ExternalEvent.Create`, dispose | 3 |
| §4 `Commands/OpenCadToBimCommand.cs` | 3 |
| §4 `UI/CadToBim/CadToBimPaneHost.cs` (PaneId, Right, VisibleByDefault false, try/catch + telemetry) | 19 |
| §4 `UI/CadToBim/CadToBimPanel.xaml(.cs)` (viewport + 258 px sidebar, tools overlay, legend) | 18 |
| §4 `UI/CadToBim/CadToBimViewModel.cs` | 17 |
| §4 `CadToBimSession` (path, scale, model, walls, openings, spaces, plans, Erased, Forced, BuiltWallIds) | 14 — placed under `Services/CadToBim/` (namespace per contract) so Tests can link it; plan count lives on `DetectResult.PlanCount` |
| §4 `CadToBimSettings` | 13 |
| §4 `Services/CadToBim/Cad2BimBuilder.cs` | 11 |
| §4 `Services/CadToBim/Cad2BimBrush.cs` | 15 |
| §4 `Services/CadToBim/CadToBimBuildHandler.cs` | 12 |
| §4 `Services/CadToBim/SilenceJoinFailures.cs` | 10 |
| §4 `Cad2Bim/IsExternalInit.cs` | superseded — `Services/Net48Shims.cs` on develop already defines it (Task 1) |
| §4 Icons `CadToBim{16,32,64}.png` + SVG masters | 2 (`svg/CadToBim16.svg` + `svg/CadToBim32.svg`, per the icon README's two-drawing rule) |
| §4 Delete `Cad2BimConvertCommand.cs` / `Cad2BimSelectionCommand.cs` | 12 / 15 |
| §4 `CadViewport` gains hit-test + drag-box overlay "as a thin subclass or partial" | 16 — by composition (`CadViewport` is `sealed`); upstream file verbatim |
| §5 Threading (thread pool detect/brush, dispatcher apply, build only in handler, `Raise()` ≠ Accepted → busy text, one request at a time) | 17, 12 |
| §6 Remove `Condition != net48` on the link group and `#if !REVIT2023_24` | 1 (csproj + App.cs conflict), 12/15 (the gated commands are deleted) |
| §6 ACadSharp 3.6.51 all TFMs | 1 (add-in), 4 (Tests) |
| §6 `IsExternalInit` for record/init/record struct on net48 | 1 (shim already present; other net48 gaps fixed at 11 call sites) |
| §6 `System.Memory` binding risk on Revit 2023/2024 | 22 smoke row 16 (journal check); binding redirect only if it bites |
| §6 Revit 2027 / net10 | 1, and every add-in build gate runs `-f net10.0-windows` |
| §7.1 branch from develop; §7.2 merge with the one `App.cs` conflict and csproj edit | 1 |
| §7.3 extract builder + brush, delete commands, add pane/panel/command/icons/settings | 10–19, 2, 3 |
| §7.4 tests + all-TFM builds on the Mac + Windows smoke | 4–9 (+ the pane tests in 10, 13–17, 19), 22 |
| §7.5 PR to develop, then develop → staging | owner (after Task 22 gate); Task 21 branch cleanup afterwards |
| §7 bina-ai: delete `feat/cad-to-bim-viewer`, remove phantom `cad_walls_to_centerlines` references | 20 (references + regression test), 21 (branch) |
| §8 `Cad2BimUnitsTests`, `Cad2BimPlansTests` | 8 (one file `Cad2BimPlansUnitsTests`, 12 tests, covering both rows) |
| §8 `Cad2BimPairingTests` | 4 |
| §8 `Cad2BimTopologyTests` | 5 |
| §8 `Cad2BimSpacesTests` | 6 |
| §8 `Cad2BimOpeningsTests` | 7 |
| §8 `Cad2BimBrushTests` | 15 |
| §8 `Cad2BimSessionTests` | 14 (`CadToBimSessionTests`, 12 tests incl. carry-forward) |
| §8 (not named) outlines, build types, settings, overlay hit-test, view model, settings draft, icons, ribbon lint, XAML resource scope | 9, 10, 13, 16, 17, 19, 2, 3, 18 |
| §8 Manual Windows smoke | 22 (rows 1–18 + builder rows C1–C9 + pane rows E1–E3) |

No spec behaviour was left without a task; no new task was needed.

### (b) Placeholder scan

`grep -n -i "TBD\|TODO\|similar to Task\|add error handling\|fill in\|implement later"` over this file → no matches.

### (c) Type consistency — reconciliation edits made, by task

- **Global:** removed every section's `## Contract corrections` / `## Notes for assembler`; folded their content into the constraints, the reconciled contract, the execution order and the task bodies. Replaced "section A/B/C…" cross-references with task numbers.
- **Task 1 (A):** unchanged body; added the net48-union note (section E's list adds nothing to A's 11 rewrites + 7 using blocks) and the "merge left in progress" note. No `Cad2Bim/IsExternalInit.cs` anywhere.
- **Task 2 (A):** unchanged (`svg/CadToBim16.svg` + `svg/CadToBim32.svg` + generator entry + `rsvg-convert`); every other section's single-`CadToBim.svg` wording was dropped.
- **Task 3 (A):** added `host.Panel.RefreshLevels(uiApp.ActiveUIDocument?.Document)` before `OpenAsync` (section E's cross-section requirement) and the matching lint assertion; documented the slot after 12/17/19.
- **Task 4 (B):** widened the once-only `Tests.csproj` edit from 8 classifier files to the full 18-file engine set that Tasks 13/16/17 need (`ModelSource`, `Services/*`, `ViewModelBase`, `LayerViewModel`, `Shapes/*`, `CadViewport`), added the `<Using>` globals, and the marker comment later tasks append under. Sections C, D and E each carried their own engine-link/ACadSharp block — all stripped.
- **Tasks 5–9 (B):** unchanged.
- **Task 10 (C):** rewritten to the reconciled contract: `BuildTypes.cs` holds only the enums + `BuildRequest` (no `using Autodesk.Revit.DB`, `long? LevelId`); `BoxMm` moved to its own `BoxMm.cs` (D's version with `Contains`/`Normalised`/`Width`/`Height`); `BuildReport.cs` (D's `Dictionary<Wall,long>` version) and `BuildSeams.cs` (E's two interfaces) created here so Tasks 11/12/14/17 consume them; C's conditional `IsExternalInit.cs` step and conditional engine links deleted; test gained `[Collection("Cad2Bim")]`, the `long`-typed assertions, the `Normalised` test and a seams-fake test.
- **Task 11 (C):** `report.WallIds[...] = ElementIdCompat.ToLong(...)`; `BaseLevel` reads `long? LevelId` through `ElementIdCompat.ToElementId`; consumes note updated; smoke rows pointed at Task 22's file.
- **Task 12 (C):** handler declared `: IExternalEventHandler, IBuildRequestSink`; `ExternalEventRaiser.cs` moved here from E's Task 17; precondition names Task 15; verify adds net10.
- **Task 13 (D):** kept D's `CadToBimSettings` (`ToLayerFilter`, `IsWallLayer`, `IsOpeningLayer`, `ConfigPathOverride`, JObject read-modify-write, `BinaConfig.CadToBim` passthrough); its Tests.csproj block reduced to the one settings link; `[Collection]` added.
- **Task 14 (D):** `BuildReport` creation moved to Task 10; `FromLong` renamed `ToElementId`; session gained `public Cad2Bim.CadModel Model`, `public static string CenterlineKey(Wall)` and `public void CarryForwardFrom(CadToBimSession)` (E's carry-forward, moved into the session so it is unit-tested and so brushed walls keep their erased/built state); two tests added (12 total).
- **Task 15 (D):** `BoxMm.cs` no longer created here (Task 10); `git rm Commands/Cad2BimSelectionCommand.cs` added with the symbol map (spec §4 deletion that no section owned); Tests.csproj reduced to the brush link.
- **Task 16 (E):** Tests.csproj reduced to the overlay link (engine/ACadSharp/`<Using>` are Task 4's); `[Collection]` added to `CadOverlayViewportTests`; composition rationale folded in.
- **Task 17 (E):** `LevelChoice.Id` → `long`; `CarryForward` delegates to `Session.CarryForwardFrom`; VM-local `CenterlineKey` and `Median` removed (session owns both; `Detect` calls `session.SetWalls(walls)`); Civil 3D check added (`UnsupportedSourceMessage`, `UnsupportedSource(IEnumerable<string>)`, `document.Classes` scan in `Detect`, `Describe` no longer doubles the hint) with two tests (12 total); Files list no longer creates seams/raiser or modifies handler/session; `[Collection]` added.
- **Task 18 (E):** `RefreshLevels` stores `ElementIdCompat.ToLong(level.Id)`; add-in build gate documented as deferred to Task 19; manual rows mapped to smoke rows.
- **Task 19 (E):** unchanged apart from the Tests.csproj anchor wording.
- **Task 20 (F):** added the finding that `tests/test_tool_help.py::test_registry_keys_are_real_tools` is already red on develop and that this task turns it green.
- **Task 21 (F):** bina-ai `feat/cad-walls-to-centerlines` stated explicitly in the owner-decision list.
- **Task 22 (F):** smoke document gained the builder rows C1–C9 (from Task 11) and pane rows E1–E3 (from Tasks 16–19); the numbered-row checks stay at 18.
- Verified by grep over this file after assembly: no `FromLong`, no `BuildContracts.cs`, no `Dictionary<…, ElementId>` on `WallIds`/`BuiltWallIds`, no `ElementId` in any code block of a Tests-linked file, no upper-case `.X/.Y` on `Cad2Bim.Point`, each Tests.csproj link line appears exactly once, all 22 task headings present once, code fences balanced.

### Conflicts resolved by judgement (flag to the owner)

- Rule "Task 4 adds the 8 engine links" vs Tasks 13/16/17 needing `ModelSource`, `ClassificationService`, `CadRenderSource`, `ViewModelBase`, `LayerViewModel`, shapes and `CadViewport` in Tests: resolved by making Task 4 link all 18 files once (the "exactly once" rule wins over the count).
- `BoxMm`/`BuildReport`/`BuildSeams` ownership (C put `BoxMm` in `BuildTypes.cs`, D created `BuildReport.cs`/`BoxMm.cs` in Tasks 14/15, E created `BuildSeams.cs` in Task 17): all four Revit-free contract files are created in Task 10 so every consumer sees them in execution order.
- Task 18 ↔ 19 mutual reference (`CadToBimPanel.OnGearClick` → `CadToBimSettingsWindow`; `CadToBimPaneHost` → `CadToBimPanel`): kept E's task split, Task 18's add-in build gate is run at the end of Task 19.
- Spec §3 "save failure → pane offers Save As": not implemented literally (a `NewProjectDocument` has no window to stay in); the builder closes and reports, walls stay pending, the next Confirm rebuilds. Carried as a documented deviation in Task 11 for the PR description.
