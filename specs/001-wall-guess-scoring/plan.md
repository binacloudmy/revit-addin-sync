# Implementation Plan: Wall guess confidence and ranked questions

**Branch**: `ai-629-spec` (feature folder `specs/001-wall-guess-scoring`) | **Date**: 2026-11-28 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/001-wall-guess-scoring/spec.md` (Linear AI-629, epic AI-621 CAD to BIM v1)

## Summary

Give every detected wall a list of deterministic guesses (uncertainty, basis, items affected, cost to fix later) and rank them into questions with one scoring component. The engine work is one new Revit-free file in `Cad2Bim/cad2bim/` (`Confidence.cs`: `WallGuess`, `CostToFix`, `ThicknessSource`, `WallConfidence`, `RankedQuestion`, `ScoringService`) plus three small additions to existing engine types (`Wall.ThicknessSource`, `Wall.Guesses`, `Wall.Basis`). It is wired into the three places that run detection today: the pane (`CadToBimViewModel.Detect` / `Elaborate`), the read_dwg_plan reader (`DwgPlanReader.Read`, so the env-gated ASWARA test can check the ranking) and the headless harness (`Cad2Bim.Headless/Program.cs`, for timings). Results live on the `Cad2Bim.Wall` instances and in a new `CadToBimSession.Questions` list. No UI, no Revit API calls, no contract change, no AI.

## Technical Context

**Language/Version**: C# (LangVersion as set per csproj). The engine files compile into `RevitWebAppSync.csproj` (net48 / net8.0-windows / net10.0-windows), `Tests/Tests.csproj` (net10.0-windows) and `Cad2Bim.Headless` (net8.0) by source linking.

**Primary Dependencies**: None new. Engine uses System + Linq only (ACadSharp is not needed for scoring).

**Storage**: In memory only: `Cad2Bim.Wall` instances and `RevitWebAppSync.Services.CadToBim.CadToBimSession`. No file, no server.

**Testing**: xUnit in `Tests/` via `dotnet test Tests/Tests.csproj -c Release -p:SkipRevitSources=true`; ASWARA check in `Tests/DwgRealSheetTests.cs` (env-gated by `AI610_ASWARA_DWG`).

**Target Platform**: Revit 2023–2027 add-in (Windows) + headless harness (any OS with .NET 8).

**Project Type**: Desktop add-in with a shared, Revit-free geometry engine.

**Performance Goals**: Scoring adds < 5% to detect time on ASWARA plan 1 (SC-004), measured via `Cad2Bim.Headless`.

**Constraints**: Deterministic; no network; no `List.Contains` in per-wall loops (constitution VI); net48-safe syntax (records already work via `Services/Net48Shims.cs` `IsExternalInit`); no Revit types in engine files.

**Scale/Scope**: ASWARA ~1,800 walls across 8 listed plans (351/277/493/405/126/48/23/96 from `Aswara_classifier_baseline_layer_picks_and_counts`) and at least 671 loose ends on plans 1–3 alone; elec plan 3 has 420 loose ends. Walls only.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | How |
|---|---|---|
| I. Spec before code | PASS | This spec → plan → tasks; no code written in this run. |
| II. One codebase, every Revit version | PASS | New engine file is plain C#; linked into `RevitWebAppSync.csproj` (all three TFMs) and `Tests/Tests.csproj`. No version defines needed. Builder untouched. Test versions: smoke in a Revit session on **Revit 2024** (net48) and **Revit 2026** (net8.0-windows) on the Windows VM. **Revit 2027** (net10.0-windows) gets a compile-only check in CI `build` unless the VM has it installed (T028). Actual versions go in the PR. |
| III. Never block Revit | PASS | No Revit API call, no I/O, no network. Scoring runs inside the existing background `Detect` path. |
| IV. Engine logic stays headless | PASS | `ScoringService` lives in `Cad2Bim/cad2bim/Confidence.cs`, runs in `Cad2Bim.Headless`. The only Revit-side input (the wall-type catalogue) is a plain `IReadOnlyCollection<double>` passed in. |
| V. Tested, then verified in Revit | PASS with note | Unit tests in `Tests/`; ASWARA counts reported before/after. Nothing a user sees changes, so no Revit-session verification is required beyond "detect and Confirm still work" smoke in the PR. |
| VI. Measure before optimising | PASS | Before/after headless timing on ASWARA required (SC-004); per-wall lookups use `HashSet`/`Dictionary`. |
| Contract changes paired with bina-ai | PASS | read_dwg_plan envelope (`DwgPlanReader.Build`) and `contracts/copilot/tools.json` untouched (FR-014). |

No violations; Complexity Tracking empty.

## Project Structure

### Documentation (this feature)

```text
specs/001-wall-guess-scoring/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── scoring-service.md
├── checklists/requirements.md
└── tasks.md            # /speckit-tasks
```

### Source Code (repository root)

```text
Cad2Bim/cad2bim/
├── Confidence.cs        # NEW: WallGuess, CostToFix, ThicknessSource, WallConfidence, RankedQuestion, ScoringService
├── Geometry.cs          # Wall: + ThicknessSource, + Guesses, + Basis
└── Outlines.cs          # WallsFromOutlines → ThicknessSource.Outline; WallFromCloud → ThicknessSource.Cloud
Cad2Bim/Cad2Bim.Headless/
└── Program.cs           # --score / --types= : print ranked questions + timing (file already globbed in by ..\cad2bim\*.cs)
Services/CadToBim/
├── Cad2BimBrush.cs      # SingleLineWall → ThicknessSource.Median
├── CadToBimSession.cs   # + Questions, + TypeThicknessesMm, + Rescore(...)
└── DwgPlanReader.cs     # DwgPlanSheet + Walls, + Graph (not emitted by Build)
UI/CadToBim/
└── CadToBimViewModel.cs # Elaborate returns the WallGraph too; Detect and the erase path call Session.Rescore
RevitWebAppSync.csproj   # link Cad2Bim\cad2bim\Confidence.cs
Tests/
├── Tests.csproj         # link ..\Cad2Bim\cad2bim\Confidence.cs
├── Cad2BimScoringTests.cs        # NEW: formula, ordering, ties, each heuristic + basis, no-%, AI-free fixture
├── CadToBimSessionTests.cs       # + confidence survives erase / re-detect / MarkBuilt
├── CadToBimViewModelTests.cs     # update Elaborate deconstruction (3-tuple)
└── DwgRealSheetTests.cs          # + Aswara_top_wall_question_is_175mm_with_no_matching_type
```

**Structure Decision**: Engine type goes next to its siblings in `Cad2Bim/cad2bim/` (constitution IV), as a `CadClassifier`-style static (`ScoringService`) so `Cad2Bim.Headless` picks it up through its existing `..\cad2bim\*.cs` glob. Pane and reader only call it.

## Design

### Heuristics (FR-006, research R2–R5)

| Guess (tie order) | Fires when | Key | Uncertainty | Cost | Basis template |
|---|---|---|---|---|---|
| `DefaultedType` | a catalogue is supplied and no entry is within 2.5 mm of the wall's 5 mm thickness group | group mm | 0.6 | High | `thickness {T} mm has no matching type` |
| `DefaultedThickness` | `Wall.ThicknessSource` is `Median` or `Cloud` | group mm | 0.5 | Medium | `thickness {T} mm taken from the drawing median, not two faces` / `thickness {T} mm taken from the extent of marks, not two faces` |
| `LayerHintMismatch` | at least one face `Segment.Layer` is non-empty and no face layer has the Wall role | lowest-ordinal non-wall face layer | 0.4 | Medium | `found on layer {L}, not a wall layer` |
| `UnjoinedEnds` | a `TopologicalPoint` with `Degree < 2` lies within `CadClassifier.DefaultJunctionToleranceMm` of an end of the wall's `Centerline` (walls reached through `WallGraph.Members` of the node's representatives) | `sheet` | 0.3 | Low | `{N} wall ends meet no other wall` (question) / `an end meets no other wall` (wall) |

Proven basis (no guess): `two faces {T} mm apart on layer {L}` (Faces), `hatched outline {T} mm wide` (Outline). `{T}` is the 5 mm group as a whole number, `{N}` a count, `{L}` the layer name verbatim.

### Score and ranking (FR-003–FR-005)

`score = uncertainty × itemsAffected × (int)cost` with `CostToFix { Low = 1, Medium = 2, High = 3 }`. Questions are grouped by `(Guess, Key)` over **active** walls; `ItemsAffected = walls.Count`. Order: score desc, then cost desc, then items desc, then `WallGuess` ascending, then `Key` by `StringComparer.Ordinal`. Scores are computed in `double` from exact constants; ties are compared exactly (no epsilon) because every input is a small rational constant times an integer.

### Wiring

- **Pane**: `CadToBimViewModel.Elaborate` returns `(Openings, Spaces, Graph)`. `Detect` and the erase/brush re-elaborate path (`CadToBimViewModel.cs:747`) call `session.Rescore(graph, layer => RoleOf(layer, settings, roles) == LayerRole.Wall)`, which runs `ScoringService.ScoreWalls(Active(), …, TypeThicknessesMm)` and stores `Questions`. Erased walls drop out because only `Active()` is scored (FR-010).
- **Re-detect**: new `Wall` instances are rescored from scratch; `CarryForwardFrom` needs no change, because guesses are a pure function of the walls (SC-005). Brushed walls carried by identity get rescored with the rest.
- **Confirm**: `BuildRequest.Walls` already carries the `Wall` instances, so `Guesses`/`Basis` reach the builder untouched; `Cad2BimBuilder` is not changed.
- **read_dwg_plan reader**: `DwgPlanReader.Read` stores `walls` and `graph` on `DwgPlanSheet` (`Walls`, `Graph`). `Build` does not emit them (FR-014). The ASWARA test calls `ScoringService.ScoreWalls(sheet.Walls, sheet.Graph, isWallLayer, catalogue)`.
- **Catalogue**: `CadToBimSession.TypeThicknessesMm` defaults to `null`, so defaulted-type does not fire in the pane until a follow-up fills it from the document's basic wall types in Revit API context. Tests and the harness pass a fixed list.

## Risks

1. **The ASWARA acceptance criterion may not hold with these numbers.** ASWARA plans 1–3 alone have 671 loose ends (223 + 205 + 243). If even 500 walls carry one, `UnjoinedEnds` scores 0.3 × 500 × 1 = 150. The 175 mm group (0.6 × n × 3 = 1.8n) would need more than 83 walls to beat that. Thin 50/70/80 mm groups (221 of 351 walls on plan 1) would also outscore 175 mm if the catalogue leaves them out. Mitigation: task T024 measures group sizes on ASWARA before the constants are frozen. If 175 mm is not on top, stop and go back to the product owner (Open questions 1, 3, 5). Do not tune constants just to make the test pass.
2. **No real "defaulted type" in the builder today.** `Cad2BimBuilder.DefaultWallType` gives every wall the first basic `WallType`. The heuristic models what the builder *should* match against; it fires only when a catalogue is supplied.
3. **Defaulted thickness never fires on detect output.** Every detected wall has a measured thickness (`new Wall(e1, e2)` measures `Segment.Distance`). Only brushed walls (`Cad2BimBrush.SingleLineWall`, `CadClassifier.WallFromCloud`) are defaulted.
4. **Synthesised faces have no layer.** The faces built in `CadClassifier.Build` (Outlines.cs, used by `WallsFromOutlines`/`WallFromCloud`) and `Cad2BimBrush.SingleLineWall` are `new Segment(...)` with the default empty `Layer`. They never trigger layer-hint mismatch. That is intended (no evidence either way), and a unit test pins it.

## Complexity Tracking

None.
