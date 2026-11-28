# Data model: Wall guess confidence

All new engine types live in `Cad2Bim/cad2bim/Confidence.cs`, namespace `Cad2Bim`. Session additions are in `Services/CadToBim/CadToBimSession.cs`, namespace `RevitWebAppSync.Services.CadToBim`.

## Enums

### `WallGuess`
Declaration order is the tie-break order.

| Value | Uncertainty | Cost |
|---|---|---|
| `DefaultedType` | 0.6 | `High` |
| `DefaultedThickness` | 0.5 | `Medium` |
| `LayerHintMismatch` | 0.4 | `Medium` |
| `UnjoinedEnds` | 0.3 | `Low` |

### `CostToFix`
`Low = 1, Medium = 2, High = 3`. The integer value is the score weight.

### `ThicknessSource`
`Faces` (default; measured between two drawn faces), `Outline` (short side of a hatch outline), `Median` (brush single-line wall: drawing median), `Cloud` (brush box: narrow side of the marks' extent). `Median` and `Cloud` are defaulted.

## `WallConfidence` (one guess on one wall)

This is the ticket's `Confidence` record, named `WallConfidence` because this ticket covers walls only (G9 adds other element kinds). The spec's "Confidence" entity maps to this type, and the ticket's "DetectedElement" maps to `Cad2Bim.Wall`.

| Field | Type | Rule |
|---|---|---|
| `Guess` | `WallGuess` | required |
| `Key` | `string` | group key: thickness group as invariant integer mm (`"175"`; `"175:median"` / `"175:cloud"` for defaulted thickness), layer name, or `"sheet"` |
| `Uncertainty` | `double` | from the `WallGuess` table; 0 < u ≤ 1 |
| `CostToFixLater` | `CostToFix` | from the `WallGuess` table |
| `Basis` | `string` | filled template, per-wall wording (contracts/scoring-service.md); no `%` |
| `ItemsAffected` | `int` | walls in the same `(Guess, Key)` group among the walls scored; ≥ 1 |
| `Score` | `double` | `Uncertainty × ItemsAffected × (int)CostToFixLater` |

Immutable after creation (get-only properties or `init`).

## `RankedQuestion` (one group of walls sharing a guess)

| Field | Type | Rule |
|---|---|---|
| `Guess` | `WallGuess` | |
| `Key` | `string` | |
| `Basis` | `string` | question wording (e.g. `thickness 175 mm has no matching type`, `312 wall ends meet no other wall`) |
| `Uncertainty`, `CostToFixLater` | as above | |
| `Walls` | `IReadOnlyList<Wall>` | in input order; reference identity |
| `ItemsAffected` | `int` | `Walls.Count` |
| `Score` | `double` | as above |

## Additions to `Cad2Bim.Wall` (`Geometry.cs`)

| Member | Type | Default | Set by |
|---|---|---|---|
| `ThicknessSource` | `ThicknessSource` | `Faces` | `CadClassifier.WallsFromOutlines` → `Outline`; `CadClassifier.WallFromCloud` → `Cloud`; `Cad2BimBrush.SingleLineWall` → `Median` |
| `Guesses` | `List<WallConfidence>` | empty | `ScoringService.ScoreWalls` (cleared then refilled on every call) |
| `Basis` | `string` | `""` | `ScoringService.ScoreWalls`: the basis of the wall's first guess in `Rank` order, or the proven template when there is none |

## Additions to `CadToBimSession`

| Member | Type | Notes |
|---|---|---|
| `TypeThicknessesMm` | `IReadOnlyCollection<double>` | `null` = no catalogue (defaulted type off) |
| `Questions` | `List<RankedQuestion>` | empty until the first `Rescore` |
| `Rescore(WallGraph graph, Func<string,bool> isWallLayer)` | method | `Questions = ScoringService.ScoreWalls(Active(), graph, isWallLayer, TypeThicknessesMm).ToList()` |

## Additions to `DwgPlanSheet` (`DwgPlanReader.cs`)

`List<Wall> Walls` and `WallGraph Graph`, set in `DwgPlanReader.Read`. They are not emitted by `DwgPlanReader.Build`, so the read_dwg_plan envelope is unchanged.

## Lifecycle

```
Detect ─► SetWalls ─► Elaborate (graph) ─► Rescore ─► Questions
   ▲                                            │
   │ re-detect (new instances; rescored)        ▼
   └───────────── erase / brush ─► Elaborate(Active) ─► Rescore
Confirm: BuildRequest.Walls carry Guesses/Basis unchanged.
```

Validation: `ItemsAffected ≥ 1`; `Basis` non-empty and free of `%`; `Score > 0` for every question; no wall appears twice in one question.
