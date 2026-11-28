---
description: "Tasks for AI-629: wall guess confidence and ranked questions"
---

# Tasks: Wall guess confidence and ranked questions

**Input**: Design documents from `specs/001-wall-guess-scoring/` (plan.md, spec.md, research.md, data-model.md, contracts/scoring-service.md, quickstart.md)

**Tests**: Required. FR-013 and constitution V call for them. Test command: `dotnet test Tests/Tests.csproj -c Release -p:SkipRevitSources=true`.

**Organization**: Grouped by user story. US1, US2 and US4 are P1; US3 is P2. The ASWARA acceptance check needs all four heuristics, so it sits in the final phase.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1–US4 from spec.md

---

## Phase 1: Setup

- [ ] T001 Create `Cad2Bim/cad2bim/Confidence.cs` (namespace `Cad2Bim`, explicit `using System; System.Collections.Generic; System.Globalization; System.Linq;` only, net48-safe syntax). Add the enums `WallGuess`, `CostToFix` and `ThicknessSource` exactly as specified in data-model.md (`WallGuess` order and values, `CostToFix` integer weights, `ThicknessSource` values).
- [ ] T002 [P] Link the new file: add `<Compile Include="Cad2Bim\cad2bim\Confidence.cs" Link="Cad2Bim\Confidence.cs" />` beside the `Geometry.cs` link in `RevitWebAppSync.csproj` (~line 231), and `<Compile Include="..\Cad2Bim\cad2bim\Confidence.cs" Link="Cad2Bim\Confidence.cs" />` beside the `Geometry.cs` link in `Tests/Tests.csproj` (~line 90). `Cad2Bim.Headless` already globs `..\cad2bim\*.cs`, and `Cad2Bim/cad2bim/cad2bim.csproj` includes it by default. Check that neither needs a change.

## Phase 2: Foundational (blocks every story)

- [ ] T003 Add to `Cad2Bim.Wall` in `Cad2Bim/cad2bim/Geometry.cs`: `ThicknessSource` (settable, default `Faces`), `Guesses` (get-only `List<WallConfidence>`) and `Basis` (settable string, default `""`), per data-model.md `Wall.ThicknessSource`, `Wall.Guesses`, `Wall.Basis`. Do not touch the constructor or `Thickness`.
- [ ] T004 Add `WallConfidence` and `RankedQuestion` to `Cad2Bim/cad2bim/Confidence.cs` with immutable members per data-model.md `WallConfidence.Guess/Key/Uncertainty/CostToFixLater/Basis/ItemsAffected/Score` and `RankedQuestion.Guess/Key/Basis/Uncertainty/CostToFixLater/Walls/ItemsAffected/Score`. Compute `Score` once, in the constructor, through `ScoringService.Score`.
- [ ] T005 Add `static class ScoringService` to `Cad2Bim/cad2bim/Confidence.cs` with `ThicknessGroupMm`, `TypeMatchToleranceMm`, `Uncertainty(WallGuess)`, `Cost(WallGuess)`, `Score(...)`, `ThicknessGroup(...)` and `Rank(...)`, exactly as in contracts/scoring-service.md. `Rank` uses `OrderByDescending(Score).ThenByDescending(cost).ThenByDescending(items).ThenBy(guess).ThenBy(key, StringComparer.Ordinal)` and compares scores exactly (no epsilon).

**Checkpoint**: All three TFMs and the Tests project compile; existing tests stay green.

## Phase 3: User Story 1 – Know which wall guesses matter most (P1) 🎯 MVP

**Goal**: Walls are grouped into ranked questions by score.
**Independent test**: Synthetic walls with a catalogue: ranked output is ordered, ties are stable, and the defaulted-type group for 175 mm comes first when it has the highest score.

- [ ] T006 [P] [US1] Create `Tests/Cad2BimScoringTests.cs` covering formula and ranking. `Score(0.6, 10, High) == 18`. `Rank` orders by score. Each tie-break level is decided in turn: same score/different cost, same score+cost/different items, same score+cost+items/different guess, everything equal/different key. Repeated `Rank` calls on a shuffled input give the same order. A clean sheet (catalogue covers every thickness, all faces on wall layers, `graph == null`) gives an empty list and empty `Guesses` on every wall (spec US1 scenario 3). Build walls with `new Wall(new Segment(...), new Segment(...))` as `Tests/Cad2BimPairingTests.cs` does.
- [ ] T007 [P] [US1] Add DefaultedType tests to `Tests/Cad2BimScoringTests.cs`. 175 mm walls with catalogue `{100, 150, 200}` produce one question, key `"175"`, items = the number of walls. A 174.6 mm wall joins the 175 group. Catalogue entry 176 (within 2.5 mm) means no guess. A `null` catalogue means no DefaultedType guess at all.
- [ ] T008 [US1] Implement `ScoringService.ScoreWalls(walls, graph, isWallLayer, typeThicknessesMm)` in `Cad2Bim/cad2bim/Confidence.cs` with the DefaultedType heuristic only. Steps: clear `Guesses`; group with a `Dictionary<(WallGuess, string), List<Wall>>`; build `RankedQuestion`s; write a `WallConfidence` per wall with the group's `ItemsAffected`; return `Rank(...)`. Use the thickness group and match tolerance from contracts/scoring-service.md, and a `HashSet<double>` of catalogue groups for lookup.

**Checkpoint**: T006–T007 pass.

## Phase 4: User Story 2 – Read why a wall is a guess (P1)

**Goal**: Every wall carries a template basis. All four heuristics fire.
**Independent test**: Per-heuristic unit tests assert the exact basis strings from contracts/scoring-service.md.

- [ ] T009 [P] [US2] Add heuristic and basis tests to `Tests/Cad2BimScoringTests.cs`:
  - Faces on layer `0` with `isWallLayer = l => l.Contains("WALL")` → `found on layer 0, not a wall layer`.
  - Faces with an empty `Layer` → no LayerHintMismatch.
  - A `ThicknessSource.Median` wall → `thickness 100 mm taken from the drawing median, not two faces`.
  - A `Cloud` wall → the cloud wording, as a separate question from Median.
  - An L of two walls plus one free wall, built through `CadClassifier.CreateTopologicalPoints` → the expected `UnjoinedEnds` count and the `ends.question` / `ends.wall` wording, singular and plural.
  - A clean wall → `two faces 115 mm apart on layer A-WALL`.
  - An outline wall → `hatched outline … mm wide`.
  - Every basis produced matches one template regex and contains no `%`.
- [ ] T010 [US2] Implement LayerHintMismatch in `ScoreWalls` (`Cad2Bim/cad2bim/Confidence.cs`) per research.md R2. Key = the lowest-ordinal non-empty non-wall face layer.
- [ ] T011 [US2] Implement DefaultedThickness in `ScoreWalls` per research.md R3. Keys `"{T}:median"` / `"{T}:cloud"` (data-model.md `WallConfidence.Key`).
- [ ] T012 [US2] Implement UnjoinedEnds in `ScoreWalls` per research.md R5. Loose nodes are `TopologicalPoint.Degree < 2`. Expand `node.Walls` through `graph.Members`. Attribute the end to members whose centreline end is within `CadClassifier.DefaultJunctionToleranceMm`. Key `"sheet"`. `graph == null` turns it off. Use a `HashSet<Wall>` and avoid `List.Contains` in loops (constitution VI).
- [ ] T013 [US2] Add the basis template table (one `static readonly` map from template id to format string, `CultureInfo.InvariantCulture`) to `Cad2Bim/cad2bim/Confidence.cs`, per contracts/scoring-service.md. Set `Wall.Basis` (data-model.md `Wall.Basis`) to the top-scoring guess's basis or the proven template.
- [ ] T014 [P] [US2] Set `ThicknessSource.Outline` on walls returned by `CadClassifier.WallsFromOutlines` and `ThicknessSource.Cloud` on the wall returned by `CadClassifier.WallFromCloud` in `Cad2Bim/cad2bim/Outlines.cs` (data-model.md `Wall.ThicknessSource`). Add assertions to `Tests/Cad2BimOutlinesTests.cs`.
- [ ] T015 [P] [US2] Set `ThicknessSource.Median` in `Cad2BimBrush.SingleLineWall` (`Services/CadToBim/Cad2BimBrush.cs`). Add an assertion to `Tests/Cad2BimBrushTests.cs`.

**Checkpoint**: T006–T015 green.

## Phase 5: User Story 4 – No AI in the numbers (P1)

**Goal**: Prove scoring is deterministic and has no network or AI dependency.
**Independent test**: Fixture equality plus a source scan.

- [ ] T016 [P] [US4] In `Tests/Cad2BimScoringTests.cs`, add a fixture that builds the same synthetic sheet twice from scratch (all four heuristics firing), runs `ScoreWalls` on each, and asserts identical `(Guess, Key, ItemsAffected, Score, Basis)` sequences and identical per-wall `Basis` (research.md R6). Run it under `CultureInfo.CurrentCulture = new CultureInfo("ms-MY")` as well as invariant culture.
- [ ] T017 [P] [US4] In `Tests/Cad2BimScoringTests.cs`, add a source-scan test. Find the repo root by walking up from `AppContext.BaseDirectory` to the folder holding `revit-addin-sync.sln`. Assert that `Cad2Bim/cad2bim/Confidence.cs` contains none of `System.Net`, `HttpClient`, `Copilot`, `Bina`, `Random`, `DateTime`.

## Phase 6: User Story 3 – Guesses survive the session (P2)

**Goal**: Confidence follows walls through erase, brush, re-detect and Confirm.
**Independent test**: `CadToBimSessionTests` scenarios.

- [ ] T018 [US3] Add `TypeThicknessesMm`, `Questions` and `Rescore(WallGraph, Func<string,bool>)` to `Services/CadToBim/CadToBimSession.cs`, per data-model.md `CadToBimSession.TypeThicknessesMm/Questions/Rescore`. Scope is `Active()` only.
- [ ] T019 [US3] Change `CadToBimViewModel.Elaborate` (`UI/CadToBim/CadToBimViewModel.cs:916`) to return `(Openings, Spaces, Graph)`. Update both deconstructions in `Tests/CadToBimViewModelTests.cs` (lines 322, 329) to three elements.
- [ ] T020 [US3] In `UI/CadToBim/CadToBimViewModel.cs`, call `session.Rescore(graph, layer => RoleOf(layer, settings, roles) == LayerRole.Wall)` after `Elaborate` in `Detect` (~line 879) and after the re-elaborate on the active walls (~line 747). No UI binding.
- [ ] T021 [P] [US3] Add tests to `Tests/CadToBimSessionTests.cs`:
  - After `Rescore`, `ToggleErase` + `Rescore` lowers the question's `ItemsAffected` by one.
  - A re-detect (`SetWalls` with new instances + `CarryForwardFrom` + `Rescore`) gives per-wall `Guesses` equal to a fresh `ScoreWalls` (SC-005).
  - `MarkBuilt` leaves `Guesses`/`Basis` on built walls untouched.
  - A brushed `Median` wall carried forward is still a DefaultedThickness guess.

## Phase 7: Polish and cross-cutting

- [ ] T022 Store `walls` and `graph` on `DwgPlanSheet` (new fields `Walls`, `Graph`) in `DwgPlanReader.Read` (`Services/CadToBim/DwgPlanReader.cs`), per data-model.md `DwgPlanSheet`. Do not emit them from `DwgPlanReader.Build` (FR-014). `Tests/DwgPlanReaderTests.cs` must stay green unchanged.
- [ ] T023 [P] Add `--score` and `--types=a,b,c` to `Cad2Bim/Cad2Bim.Headless/Program.cs`. Print the top 20 ranked questions (score, items, basis) and the scoring time in ms next to the existing output (research.md R8). Also list it in the usage text.
- [ ] T024 Calibration gate (plan Risk 1). Add `Aswara_wall_question_census` (env-gated, logs only) to `Tests/DwgRealSheetTests.cs`. It scores `sheet.Walls`/`sheet.Graph` with `l => new CadToBimSettings().IsWallLayer(l)` and logs every wall-thickness group's count plus the top 20 questions. Run it on ASWARA. Choose the fixture catalogue (Open question 5) and record the measured groups as R9 in `specs/001-wall-guess-scoring/research.md`. **If 175 mm is not the top question under the spec's constants, stop and raise it with the product owner (Open questions 1, 3, 5) before T025. Do not tune the constants to make the test pass.**
- [ ] T025 Add `Aswara_top_wall_question_is_175mm_with_no_matching_type` to `Tests/DwgRealSheetTests.cs`. It uses a `static readonly double[] AswaraTypeCatalogueMm` (from T024) and asserts the first `RankedQuestion` is `DefaultedType`/`"175"` with basis `thickness 175 mm has no matching type`, and that scores are non-increasing (SC-001). Also assert that every `Wall.Basis` and question basis on the sheet matches a contracts/scoring-service.md template and contains no `%` (SC-002).
- [ ] T026 Time ASWARA through `Cad2Bim.Headless` with and without `--score` (quickstart.md §3). Record the before/after numbers in the PR. Fail SC-004 if scoring takes ≥ 5% of detect time.
- [ ] T027 Run the full `dotnet test Tests/Tests.csproj -c Release -p:SkipRevitSources=true` with and without `AI610_ASWARA_DWG`. Confirm that the `Aswara_classifier_baseline_layer_picks_and_counts` counts are unchanged and that CI `build` passes for net48/net8.0-windows/net10.0-windows (constitution II).
- [ ] T028 Smoke-test in Revit (Windows VM) on Revit 2024 and Revit 2026, plus Revit 2027 if it is installed (plan Constitution Check II). Detect ASWARA in the CAD to BIM pane, erase one wall, Confirm. Behaviour should be unchanged. Record the versions and steps in the PR (constitution V).

---

## Dependencies

- Setup (T001–T002) → Foundational (T003–T005) → US1 (T006–T008) → US2 (T009–T015) → US4 (T016–T017) and US3 (T018–T021) in either order → Polish (T022–T028).
- T008 before T010–T013: all of them edit `ScoreWalls` in the same file, so run them one after another.
- T019 before T020. T018 before T020–T021.
- T022 before T024. T024 gates T025.
- US4 needs every heuristic, so it follows US2.

## Parallel examples

- After T005: T006 and T007 (tests) can be written in parallel with each other.
- In US2: T014 (`Outlines.cs`) and T015 (`Cad2BimBrush.cs`) can run in parallel with T009, which is in the tests file.
- US4 T016/T017 and US3 T021 touch different test files and can run in parallel.
- T023 (headless) can run in parallel with T022.

## Implementation strategy

1. MVP: Phases 1–3. Ranked DefaultedType questions on synthetic walls, with no wiring.
2. Add US2 (all heuristics and basis), then US4 (proofs). The engine is then complete and headless-testable.
3. Add US3 (session wiring, still no UI).
4. Polish. T024 is a hard gate on the ticket's ASWARA acceptance criterion.
