# Quickstart: validate wall guess scoring

## Prerequisites

- .NET SDK able to build `Tests/Tests.csproj` (net10.0-windows; CI `tests` job) and `Cad2Bim/Cad2Bim.Headless` (net8.0).
- For the ASWARA checks: `AI610_ASWARA_DWG` pointing at `01_FLOOR PLAN_ASRAMA ASWARA.dwg` (not in the repo).

## 1. Unit tests (no drawing, no network)

```bash
dotnet test Tests/Tests.csproj -c Release -p:SkipRevitSources=true \
  --filter "FullyQualifiedName~Cad2BimScoringTests|FullyQualifiedName~CadToBimSessionTests|FullyQualifiedName~CadToBimViewModelTests"
```

Expected: formula, ordering, tie-break, each heuristic's basis, no-`%` and AI-free fixtures pass (FR-013). Run once with the network off: same result.

## 2. ASWARA ranking (env-gated)

```bash
AI610_ASWARA_DWG="/path/01_FLOOR PLAN_ASRAMA ASWARA.dwg" \
dotnet test Tests/Tests.csproj -c Release -p:SkipRevitSources=true \
  --filter "FullyQualifiedName~DwgRealSheetTests.Aswara_top_wall_question"
```

Expected: the first `RankedQuestion` is `DefaultedType`, key `175`, basis `thickness 175 mm has no matching type`. The test logs the top 10 questions with score and items; paste them into the PR (constitution V).

## 3. Timing (constitution VI)

```bash
dotnet run --project Cad2Bim/Cad2Bim.Headless -c Release -- "/path/01_FLOOR PLAN_ASRAMA ASWARA.dwg"            # before
dotnet run --project Cad2Bim/Cad2Bim.Headless -c Release -- "/path/01_FLOOR PLAN_ASRAMA ASWARA.dwg" --score --types=<fixture list>
```

Expected: scoring time < 5% of the detect time (SC-004); ranked list printed.

## 4. Regression

Full `dotnet test` run: existing `DwgRealSheetTests` baselines (plans 351/277/493/…, doors, windows) and the `DwgPlanReaderTests` envelope are unchanged (FR-012, FR-014).
