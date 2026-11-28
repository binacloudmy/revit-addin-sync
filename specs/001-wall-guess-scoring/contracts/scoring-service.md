# Contract: `Cad2Bim.ScoringService` (in-process, engine)

Internal C# surface consumed by `CadToBimSession`, `DwgPlanReader` tests and `Cad2Bim.Headless`. Not a bina-ai contract: `contracts/copilot/tools.json` and the read_dwg_plan envelope do not change.

## Members

```csharp
public static class ScoringService
{
    public const double ThicknessGroupMm = 5.0;
    public const double TypeMatchToleranceMm = 2.5;

    public static double Uncertainty(WallGuess guess);          // table in data-model.md
    public static CostToFix Cost(WallGuess guess);              // table in data-model.md
    public static double Score(double uncertainty, int itemsAffected, CostToFix cost);
    public static double ThicknessGroup(double thicknessMm);    // Math.Round(t / 5) * 5

    /// Clears and refills Wall.Guesses and Wall.Basis for every wall in `walls`,
    /// then returns the ranked questions. Pure: same inputs → same output, no I/O.
    public static IReadOnlyList<RankedQuestion> ScoreWalls(
        IReadOnlyList<Wall> walls,
        WallGraph? graph,                              // null: UnjoinedEnds off
        Func<string, bool> isWallLayer,                // Wall role predicate
        IReadOnlyCollection<double>? typeThicknessesMm // null: DefaultedType off
    );

    /// Stable order: Score desc, CostToFixLater desc, ItemsAffected desc,
    /// Guess asc (enum order), Key ordinal asc.
    public static List<RankedQuestion> Rank(IEnumerable<RankedQuestion> questions);
}
```

Preconditions: `walls` non-null (empty → empty result); `isWallLayer` non-null; walls must be the ones `graph` was built from.

## Basis templates (exhaustive)

| Template id | Text (InvariantCulture) | Used on |
|---|---|---|
| `type.question`, `type.wall` | `thickness {T} mm has no matching type` | question + wall |
| `thickness.median` | `thickness {T} mm taken from the drawing median, not two faces` | question + wall |
| `thickness.cloud` | `thickness {T} mm taken from the extent of marks, not two faces` | question + wall |
| `layer` | `found on layer {L}, not a wall layer` | question + wall |
| `ends.question` | `{N} wall ends meet no other wall` (`1 wall end meets no other wall` when N = 1) | question |
| `ends.wall` | `an end meets no other wall` / `both ends meet no other wall` | wall |
| `proven.faces` | `two faces {T} mm apart on layer {L}` | wall, no guess |
| `proven.outline` | `hatched outline {T} mm wide` | wall, no guess |

`{T}` = whole-mm thickness group, `{N}` = loose-end count, `{L}` = layer name verbatim (empty layer → `0`). For `DefaultedThickness`, the key is `"{T}:median"` or `"{T}:cloud"`, so the two wordings never share a question. Rules: no `%`, no fraction or decimal of the uncertainty, never free text.

## Guarantees

- Deterministic and culture-independent. No network, file, clock or random access. No reference to `System.Net`, the copilot or bina-ai code.
- O(walls + graph nodes) with `HashSet`/`Dictionary` lookups.
- A wall erased from the session is not passed in, so it never counts in `ItemsAffected`.
