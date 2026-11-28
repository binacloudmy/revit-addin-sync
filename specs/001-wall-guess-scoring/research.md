# Research: Wall guess confidence and ranked questions

All Technical Context items were resolvable from the code; no NEEDS CLARIFICATION remain. Product-level defaults are in spec.md "Open questions".

## R1. Where does "DetectedElement" live?

- **Decision**: On `Cad2Bim.Wall` (`Cad2Bim/cad2bim/Geometry.cs`): add `Guesses` (list of `WallConfidence`), `Basis` (string) and `ThicknessSource`.
- **Rationale**: There is no `DetectedElement` type anywhere in the repo. Every consumer already passes `Wall` instances by reference: `CadToBimSession.Walls/Forced/Erased`, `BuildRequest.Walls`, `BuildReport.WallIds`, `PlanCluster.Walls`. Hanging the data on the wall means it reaches Review and Confirm with no new plumbing.
- **Alternatives**: (a) a new `DetectedElement` wrapper: every list in session/builder/reader would change type, which is too large for an M ticket and touches the builder. (b) A side `Dictionary<Wall, …>` in the session only: the read_dwg_plan reader and the headless harness have no session, so it would be duplicated.

## R2. Layer-hint mismatch signal

- **Decision**: A wall mismatches when at least one of its face `Segment.Layer` values is non-empty and none has the Wall role. In the pane, "Wall role" is `CadToBimViewModel.RoleOf(layer, settings, roles) == LayerRole.Wall`; in the reader it is the same call with `noRoles`; in the harness it is `CadToBimSettings.IsWallLayer`, or the `--include` list when given. `ScoringService` takes a `Func<string, bool>` so the engine does not depend on `CadToBimSettings` (Services) or `LayerRole` (UI).
- **Rationale**: Walls admitted by `CadClassifier.ClassifyWallsElsewhere` (`Outlines.cs`) come from non-wall layers. That is exactly the guess the ticket names. Faces built by code (`Outlines.cs` `Build`, `Cad2BimBrush.SingleLineWall`) have an empty layer and carry no evidence.
- **Alternatives**: Treat suspicious wall-named layers (`n_low wall`, `n_hatching retaining wall`) as mismatches. Rejected for this ticket: that is a new classification rule, not a confidence record (known-limits "Thin wall groups and false walls"; G9).

## R3. Defaulted thickness

- **Decision**: New `ThicknessSource { Faces, Outline, Median, Cloud }` on `Wall`, default `Faces`. `CadClassifier.WallsFromOutlines` sets `Outline`, `CadClassifier.WallFromCloud` sets `Cloud`, `Cad2BimBrush.SingleLineWall` sets `Median`. `Median` and `Cloud` are guesses.
- **Rationale**: `Wall(Segment, Segment)` always measures `Segment.Distance`. The only defaulted thickness in the code is the brush's `medianThicknessMm` (`CadToBimSession.MedianThicknessMm`, 100 mm fallback) and the cloud box's narrow side.
- **Consequence**: The heuristic never fires on plain detect output. That is documented as plan Risk 3.

## R4. Defaulted type and the catalogue

- **Decision**: `ScoringService.ScoreWalls(..., IReadOnlyCollection<double>? typeThicknessesMm)`. `null` turns the heuristic off. Thickness group = `Math.Round(t / 5.0) * 5.0`. A match is any entry within 2.5 mm of the group.
- **Rationale**: `Cad2BimBuilder.DefaultWallType(doc)` returns the first `WallType` with `Kind == WallKind.Basic` and uses it for every wall. Nothing matches by thickness today, so "no matching type" can only be judged against a catalogue someone supplies. Collecting it from Revit needs Revit API context (constitution III/IV), so it is a follow-up. Tests pass a fixed list.
- **Alternatives**: Read types in the engine: rejected, the engine must stay Revit-free.

## R5. Unjoined ends

- **Decision**: Use the `WallGraph` the detect path already builds (`CadClassifier.CreateTopologicalPoints`). Loose node = `TopologicalPoint.Degree < 2`, the same test `DwgPlanReader` uses for `DwgPlan.LooseEnds`. Node walls are run representatives, so expand them through `WallGraph.Members[rep]`. Attribute the loose end to each member whose `Centerline.P1`/`P2` lies within `CadClassifier.DefaultJunctionToleranceMm` (150 mm) of the node. Use a `HashSet<Wall>` for the result.
- **Rationale**: Reuses the graph; no second graph build in the pane once `Elaborate` returns it.
- **Alternatives**: Rebuild the graph inside `ScoringService`: doubles topology cost on elec-sized sheets (constitution VI).

## R6. Scores, ties, determinism, no AI

- **Decision**: Fixed constants per `WallGuess`; `CostToFix` weights 1/2/3. Tie-break: cost desc, items desc, `WallGuess` order, key ordinal. Basis strings are built with `string.Format(CultureInfo.InvariantCulture, …)` from templates in one static table. `Confidence.cs` has no `using` beyond `System`, `System.Collections.Generic`, `System.Globalization`, `System.Linq`.
- **AI-off fixture**: The engine and the pane have no AI switch (only `BinaConfig.AIBaseUrl` exists, and the engine never reads it). The fixture therefore (a) runs `ScoreWalls` twice on the same synthetic walls and once more on a fresh copy, and asserts byte-equal ranked output and basis strings, and (b) adds a source-scan test that `Cad2Bim/cad2bim/Confidence.cs` mentions no `System.Net`, `HttpClient`, `Copilot` or `Bina` identifiers. This is weaker than the ticket's "AI hard-disabled" wording; see the conflicts in the summary.
- **No percentages**: A test asserts that every basis matches `^[^%]*$`, contains no number between 0 and 1 in decimal form, and matches one template regex.

## R7. Session persistence ("G6")

- **Decision**: `CadToBimSession.Questions` (`List<RankedQuestion>`) and `TypeThicknessesMm`, recomputed by `Rescore(WallGraph, Func<string,bool>)` whenever the pane re-elaborates. No serialisation.
- **Rationale**: No G6 contract exists in this repo. The session is the only store the pane has, and it is in memory by design (header comment in `CadToBimSession.cs`).

## R8. Timing

- **Decision**: Add `--score` and `--types=100,150,…` to `Cad2Bim.Headless/Program.cs`. They print the ranked questions and the elapsed scoring milliseconds next to the existing timings.
- **Rationale**: Constitution VI requires timings through the harness.
