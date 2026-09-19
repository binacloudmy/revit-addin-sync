# CAD to BIM pane — design

**Date:** 2026-09-08
**Repos:** `revit-addin-sync` (all feature code), `bina-ai` (cleanup only)
**Status:** design approved in chat, pending user review of this document

## 1. Goal

One ribbon button turns a 2D floor-plan DWG into native Revit elements (walls with thickness, doors, windows, named rooms) inside a running Revit session, with a preview the drafter can correct before anything is built. Output is either the currently open project or a new `.rvt` saved beside the drawing. No cloud, no LLM, no IFC.

This replaces the hand re-tracing step between "2D design development in AutoCAD" and "BIM model for JKR submission". Target fidelity is LOD 100–200: correct wall layout and thickness, openings on the right walls, rooms named from CAD text. Real wall types, floors, roofs and family swaps are later copilot work, out of scope here.

## 2. What already exists and what we keep

Three unmerged addin branches implement CAD to BIM. Decision: **`origin/feat/cad2bim` is the engine.** It is the only branch with unit inference, hatch-poché outline reading, a wall topology graph with door-gap bridging, planar-face room extraction, room naming, plan clustering, and reliable door hosting (dictionary lookup from the wall-creation pass). It has zero tests; this spec adds them.

| Branch | Fate |
|---|---|
| `origin/feat/cad2bim` | merged into the feature branch; engine + `CadViewport` reused verbatim; its two ribbon commands are dissolved into the pane |
| `origin/feat/cad-segment-stitching` | left alone (MCP tool family for copilot chat; later convergence) |
| `origin/feat/cad-walls-to-centerlines` | no unique content; close |
| `origin/feat/cad-to-bim-viewer` (addin + bina-ai) | close both; the Python solve was a lossy port of C# originals |
| `Cad2Bim/cad2bim/IfcExporter.cs` | stays in the `Cad2Bim/` folder for the headless CLI; **unlinked** from the addin build |

Engine files linked into the addin (all under `Cad2Bim/cad2bim/`, no Revit dependency): `Geometry.cs`, `ModelSource.cs`, `Openings.cs`, `Plans.cs`, `Outlines.cs`, `Spaces.cs`, `SpatialIndex.cs`, `Topology.cs`, `Units.cs`, `Services/CadRenderSource.cs`, `Services/ClassificationService.cs`, `ViewModels/ViewModelBase.cs`, `ViewModels/RelayCommand.cs`, `ViewModels/LayerViewModel.cs`, `ViewModels/SettingsViewModel.cs`, `ViewModels/Shapes/*.cs`, `Views/Rendering/CadViewport.cs`, `Views/Controls/NumericScrubBox.cs`.

Not linked: `App.xaml`, `MainWindow.xaml`, `MainViewModel.cs`, `Themes/`, `Cad2Bim.Headless/`.

## 3. User flow

```
[CAD to BIM] button on Bina tab
   → file dialog (DWG, DXF); cancel = nothing
   → pane opens, "Detecting…" (background thread; Revit stays responsive)
   → pane shows linework + green walls + blue openings + amber rooms + counts
   → drafter corrects: layer visibility/role, thickness range, height,
     Erase (click wall), Brush (drag box over missed linework)
   → "Put the result": ◉ Add to this project  ○ Save as new .rvt
   → Confirm → build on Revit thread, one undo step → report in pane
   → pane stays open; brush more → Confirm again appends only new walls
```

Detailed per-step behaviour:

**Open.** `OpenCadToBimCommand : IExternalCommand` (OTA gate first, like every ribbon command). Shows the dockable pane, runs `OpenFileDialog` (filter `*.dwg;*.dxf`), hands the path to the pane view model. Uses `ZeroDocCommandAvailability` because "Save as new .rvt" works with no project open.

**Detect** (background). `ClassificationService.Load(path)` → `Units.Resolve` → `ModelSource.Read` with `LayerFilter` from settings → `Classify(sMin, sMax)` → `Elaborate` → `ClusterPlans`. Reported through `IProgress<string>` to the status line. Cancelled by "Change…" or closing the pane. Failures land in the status line with the innermost exception message; unsupported source apps (Civil 3D, ACA, MEP) get the "run EXPORTTOAUTOCAD first" text.

**Correct.**
- Layer list: checkbox = viewport visibility only. Role chip (wall / opening / ignore) edits `LayerFilter.Include`/`Exclude` and re-runs detect.
- Thickness range, wall height: `NumericScrubBox`, debounced 300 ms, re-runs detect.
- Storey mode radio: "Stack as storeys" (one level per plan cluster at 3000 mm steps) / "Keep drawing position, one level".
- **Erase**: click within 150 mm (drawing units) of a centerline toggles the wall into `Session.Erased`. Drawn red dashed. Openings hosted on an erased wall and rooms bounded by it are re-elaborated.
- **Brush**: drag box in viewport → `Cad2BimBrush.Run(model, boxMm, settings)`: re-read linework inside the box without the wall-layer include list, with `Wall.MinFaceLength = 0` and `Wall.MinFaceAspect = 0` inside a `try/finally`; unpaired faces ≥ 500 mm become single-line walls at the session's median thickness; if nothing pairs, `WallFromCloud` over the box extent. Results go to `Session.Forced`, drawn green with a cyan box. This absorbs the former "Walls from Selection" command.
- Counts (walls, doors, windows, rooms, erased, brushed) update on every change. Footer warns when unpaired wall-layer linework remains.

**Confirm.** Plan = `classified − erased + forced − alreadyBuilt`. Pane sets `BuildRequest` on the handler and raises the `ExternalEvent`. Controls lock, status shows build steps.

**Build** (Revit main thread, `Cad2BimBuilder.Build(uiapp, request) → BuildReport`):
1. Target `add`: `doc = uiapp.ActiveUIDocument.Document`, level from pane picker (default lowest). Target `new`: `app.NewProjectDocument(app.DefaultProjectTemplate)` if the template exists, else `NewProjectDocument(UnitSystem.Metric)`.
2. `TransactionGroup("CAD to BIM")`.
3. Walls: `Wall.Create(doc, line, wallType.Id, level.Id, heightFt, 0, false, false)` with the first `WallKind.Basic` type; `WallUtils.DisallowWallJoinAtEnd` both ends; `SilenceJoinFailures : IFailuresPreprocessor` on the transaction. Centerlines shorter than 1 mm or `ShortCurveTolerance` skipped. `Dictionary<CadWall, Wall>` kept.
4. `doc.Regenerate()`.
5. Openings: `NewFamilyInstance(point, symbol, hostWall, level, NonStructural)`; host from the dictionary, never a geometric search; windows at `level.Elevation + 900 mm`; symbol = first `FamilySymbol` in `OST_Doors` / `OST_Windows`, `Activate()` if inactive. No family in template → skipped and counted.
6. Rooms: `SketchPlane.Create` + `NewRoomBoundaryLines` from the classifier boundary, `NewRoom(level, centroid)`, name from CAD text.
7. `Assimilate()`. Re-query the document for surviving element ids (the report distinguishes "rolled back" from "hidden by view").
8. Target `new` only: `SaveAs(<dwg dir>/<dwg name>.rvt)` (overwrite prompt if the file exists), `doc.Close(false)`, `uiapp.OpenAndActivateDocument(path)`.
9. `Session.BuiltWallIds[cadWall] = elementId` for every created wall so a later Confirm appends only new ones. Erasing after build does not delete; undo is the drafter's tool.

Failure inside the group → `RollBack()`, nothing saved, innermost message in the status line, controls unlock. Save failure → build stays in the unsaved document, pane offers Save As.

**Settings** (⚙ in pane title): thickness range default 50–400 mm, wall height 3000 mm, door swing radius 500–1500 mm, window sill 900 mm, layer name hints (EN + Malay lists), default exclusion globs, template path override. Persisted in `%APPDATA%\RevitWebAppSync\config.json` under a `cadToBim` key via `BinaConfig`.

## 4. Components

```
App.cs
  CreateRibbonTab: new panel "CAD to BIM" after BINA AI, one PushButtonData
                   ("CadToBim", "CAD to\nBIM", OpenCadToBimCommand, icons CadToBim16/32)
  OnStartup:       RegisterDockablePane(CadToBimPaneHost.PaneId, "BINA CAD to BIM", host)
                   CadToBimBuildEvent = ExternalEvent.Create(CadToBimBuildHandler)
  OnShutdown:      dispose

Commands/OpenCadToBimCommand.cs          IExternalCommand; OTA gate; show pane; file dialog; vm.Open(path)

UI/CadToBim/CadToBimPaneHost.cs          Page + IDockablePaneProvider; PaneId B1A4C057-0005-4000-8000-000000000005
                                         DockPosition.Right, VisibleByDefault false; same try/catch + telemetry as CopilotPaneHost
UI/CadToBim/CadToBimPanel.xaml(.cs)      layout: CadViewport (fill) + 258 px sidebar; tools overlay (Pan/Erase/Brush); legend
UI/CadToBim/CadToBimViewModel.cs         Open/Detect/Correct/Confirm; owns CadToBimSession; IProgress; CancellationTokenSource
UI/CadToBim/CadToBimSession.cs           path, scale, model, walls, openings, spaces, plans, Erased, Forced, BuiltWallIds
UI/CadToBim/CadToBimSettings.cs          POCO + load/save through BinaConfig

Services/CadToBim/Cad2BimBuilder.cs      Build(uiapp, BuildRequest) → BuildReport   (extracted from Cad2BimConvertCommand)
Services/CadToBim/Cad2BimBrush.cs        Run(model, boxMm, settings) → List<Wall>   (extracted from Cad2BimSelectionCommand)
Services/CadToBim/CadToBimBuildHandler.cs IExternalEventHandler; Request field; Completed callback marshalled to Dispatcher
Services/CadToBim/SilenceJoinFailures.cs moved from the command

Cad2Bim/IsExternalInit.cs                #if NETFRAMEWORK polyfill for record/init on net48
Resources/Icons/CadToBim{16,32,64}.png + svg/CadToBim.svg
```

Deleted after extraction: `Commands/Cad2BimConvertCommand.cs`, `Commands/Cad2BimSelectionCommand.cs` (no forks, no `_v2`).

`CadViewport` gains two capabilities, implemented as a thin subclass or partial so the upstream file stays verbatim: hit-test nearest centerline (reuses `SegmentIndex`), and a drag-box overlay layer. Overlay layers added on top of the existing per-layer `DrawingVisual`s: walls (green band width = thickness × scale, min 3 px), erased (red dashed), openings (blue), rooms (amber dashed + name), brush boxes (cyan).

## 5. Threading

- Detect and Brush run on the thread pool via `Task.Run`; they touch only `Cad2Bim` types. Results are applied on the WPF dispatcher.
- Build runs only inside `CadToBimBuildHandler.Execute`, on Revit's main thread, triggered by `ExternalEvent.Raise()` from the pane. The `ExternalEvent` is created in `OnStartup` (valid API context) and stored on `App`.
- `Raise()` returning anything but `Accepted` shows "Revit is busy, try again" in the status line.
- One request at a time: the view model refuses Confirm while a build is in flight.

## 6. Multi-target build (net48, net8, net10)

- Remove the `Condition="'$(TargetFramework)' != 'net48'"` on the Cad2Bim link group and the `#if !REVIT2023_24` around the ribbon code.
- `ACadSharp 3.6.51` for all TFMs (package ships `lib/net48`; depends on `System.Memory 4.6.3` there).
- `LangVersion latest` is already set. `record`, `init`, `record struct` need `IsExternalInit` on net48; everything else the branch uses (`is not`, target-typed `new`, switch expressions, tuples) is compiler-only.
- Known risk: `System.Memory` binding inside Revit 2023/2024 (same family as the earlier Roslyn duplicate-assembly incident). Verify on the Windows rig; add a `bindingRedirect` in `RevitWebAppSync.addin`-adjacent config only if it bites.
- Revit 2027 (`net10.0-windows`) is newer than the branch; nothing in the engine is TFM-specific, so it should build unchanged.

## 7. Merge plan

1. `git checkout -b feat/cad-to-bim-pane origin/develop`
2. `git merge origin/feat/cad2bim` — one conflict, `App.cs`: keep develop's ribbon, drop the branch's two `aiPanel` buttons and `#if`. Keep the branch's csproj Cad2Bim block, edited per §6, with `IfcExporter.cs` removed from the link list and the viewport/VM/shape/control files added.
3. Extract builder + brush, delete the two commands, add pane, panel, command, icons, settings.
4. Tests (§8). `dotnet build` all three TFMs on the Mac (`~/.dotnet` SDK builds net48 + net8; net10 to confirm). Windows smoke on 2024 + 2025.
5. PR to `develop`; then `develop → staging` PR per the standing rule.

bina-ai, separate small PR: delete remote `feat/cad-to-bim-viewer`; remove the phantom `cad_walls_to_centerlines` references in `app/agents/revit/copilot/tool_help.py:195-206` and `app/agents/revit/copilot/tools.py:814,818` (the tool has no Python definition and steers the model at a dead end). No other backend change.

## 8. Tests

`Tests/` project, engine only (its Revit API reference is metadata-only, so no `XYZ`, no `Document`):

| File | Covers |
|---|---|
| `Cad2BimUnitsTests` | header unit honoured; lying header (inches on an mm drawing) → inferred scale; 3 m–1 km sanity band |
| `Cad2BimPairingTests` | two parallel faces → one wall with thickness = gap; antiparallel faces still pair; corridor face serves two walls (`MaxFaceUses = 2`); aspect guard rejects 45° hatch strokes; dedupe keeps the longer |
| `Cad2BimTopologyTests` | `MergeCollinearSegments` joins gaps < 150 mm and both drawing directions; `BridgeGaps` bridges facing degree-1 stubs ≤ 2000 mm, not divergent ones; T-junction preserved |
| `Cad2BimSpacesTests` | four walls → one room; sliver < 1.5 m² dropped; name picks letters over "17.79MP" |
| `Cad2BimOpeningsTests` | arc r 500–1500, sweep 60–120° → door on nearest wall; window marks grouped, split at 1200 mm; merge picks door over opening |
| `Cad2BimPlansTests` | two plans separated by a 5 m gap → two clusters ordered by area |
| `Cad2BimBrushTests` | box with guards off pairs 100 mm pieces; cloud fallback yields one wall; single-line fallback uses median thickness |
| `Cad2BimSessionTests` | erase/brush/re-confirm diff yields only unbuilt walls; erase after build does not remove from `BuiltWallIds` |

Manual Windows smoke (gate before PR): three real DWGs from the original sample set, both output targets, Revit 2024 (net48) and 2025 (net8): detect time, wall/door/window/room counts vs the headless CLI, undo removes everything, saved file reopens.

## 9. Out of scope (v1)

- Multiple DWGs into one project as separate floors (planned v1.1: "Add floor" reuses the pane with a level picker).
- Sections/elevations for heights; height stays a single setting.
- Real wall types by thickness band, JKR family swaps, floors, roofs, ceilings: copilot tools after the model exists.
- IFC export button.
- Copilot chat driving CAD to BIM (`feat/cad-segment-stitching` convergence).
- Telemetry beyond the existing `subsystem/failed` pane-registration event.

## 10. Open risks

- DWG quality dominates results: exploded poché, walls on layer 0, unnamed door blocks all push work onto Brush. Accepted; the engine's outline and cloud fallbacks exist for exactly this.
- `System.Memory` on net48 (§6).
- Default project template missing door/window families → openings skipped; report says so. Settings allow a template override.
- Large sheets (50k+ segments): `SegmentIndex` keeps pairing near-linear, but `_score`-style O(n²) code from the abandoned Python port must not be reintroduced.
