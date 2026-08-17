# Bomba Scan Loop (Pane → Backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Bomba pane's stub findings with real engine output — PRD 2026-08-12 §8 Phase 1: `Rescan → POST /v1/compliance/bomba-check → findings` — fire systems only.

**Architecture:** `Services/BombaComplianceService.cs` mirrors `JkrComplianceService` verbatim (cloud base URL, fresh Bearer per call, Newtonsoft snake_case DTOs, 401→Error ladder). A facts extractor reads floor area + height from the host doc. The panel code-behind runs the JKR `Rescan_Click → RunScanAsync` shape, plus the band-resolution handshake: `needs_input` responses cascade through TaskDialog command links until a leaf resolves. A mapper turns `FindingModel[]` into the existing `CheckVm/FindingVm` contract; the view model gains `Scanning` and `ReplaceChecks`.

**Branch:** `feat/bomba-scan-loop` off `feat/modeling-tools` (pane shell, 9 commits).

## Global Constraints

- All pane-shell constraints from `2026-08-12-bomba-compliance-pane.md` still hold (tri-state `Passed`, no bare code letters, subject labels, `[X]` placeholders, tokens inherited, `ElementId` as `long`).
- **Build gate:** `~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo` AND `-f net8.0-windows` → 0 errors; no new warning naming a Bomba file. No runtime test possible on macOS — say so honestly.
- **No commits** — stage only; user commits.
- Backend contract = `bina-ai` `app/schemas/bomba_models.py` on branch `feat/bomba-compliance-api` (snake_case wire).
- **Honesty over coverage:** phase 1 sends `present_counts = {}` and `searched_models = ["Architecture"]` — no M&E counting yet, so the engine answers NOT CHECKED ("link M&E"), never a false "missing". Coverage stays `null` (renders "coverage unknown") until room-based checks exist; `CoverageVm.Summary` wording is rooms-specific and must not be abused for systems.
- Jurisdiction + starting path are named constants (`"peninsular"`, root `"IV"`) in one place, marked for the phase-2 jurisdiction picker; the band cascade itself always ASKS via TaskDialog when >1 option, auto-advances when exactly 1.

## Tasks

### Task 1: `Services/BombaComplianceService.cs` — DTOs + HTTP client
Copy the `JkrComplianceService` shape: ctor `baseUrl ?? BinaConfig.Load().ResolvedCloudBaseUrl` (cloud, NOT AI base — engine mode mounts no /v1/compliance), `HttpClient` 60 s timeout, static `AttachAuth`, `LoginRequiredMessage = ComplianceService.LoginRequiredMessage`, `LastRequestJson/LastResponseJson/LastCallUtc`. Methods: `IsAvailableAsync()` → `GET /v1/compliance/bomba-health`; `CheckAsync(BombaCheckRequestDto)` → `POST /v1/compliance/bomba-check`; `RecheckAsync` → `POST /v1/compliance/bomba-recheck`. Error ladder: success → parse; 401 → `Error = LoginRequiredMessage`; other → `Error = "Server error: …"`; catch → `Error = ex.Message`. DTOs with `[JsonProperty("snake_case")]` mirroring `bomba_models.py` exactly (`passed` is `bool?`, `element_ids` is `List<long>`).

### Task 2: `Services/BombaFactsExtractor.cs`
`Extract(Document doc)` → `{ ProjectName, FileName, FloorAreaM2 (double?), HeightMm (double?), SearchedModels }`. Floor area = Σ placed-room `Area` (ft² → m², × 0.09290304; null when no rooms). Height = (max − min level elevation) ft → mm (× 304.8; null when < 2 levels). SearchedModels = `["Architecture"]` — the host doc really was searched; M&E is deliberately absent until link-reading lands.

### Task 3: `UI/Bomba/BombaMapper.cs`
`Map(BombaCheckResponseDto)` → `List<CheckVm>`. Group findings by `check`; title map `fire_systems → "Fire systems"` (fallback: underscores → spaces, sentence case). Per finding: `Passed` straight through (tri-state), `Severity` Pass/High/NotChecked, `Action` string→enum, headline from state (present count / "not found in models searched" / "cannot verify — M&E not searched"), metrics lines from `metrics` dict, steps→`CalcStepVm`, provenance (`ClauseRef`, `SchedulePath`, `RulesVersion` + ` · SAMPLE` suffix whenever `rules_status != "VERIFIED"` — the [X]-until-verified rule applied to provenance).

### Task 4: View model — `Scanning`, `ReplaceChecks`, stub removal
Delete `LoadStubData()`/`NewStep` (JKR `StubData.cs` precedent). Ctor: empty `Checks`, `ScopeLabel = "Bomba Compliance"`, `ScopeDetail = "Belum diimbas — tekan Re-check"`, `ChangedSinceRun = 0`. Add `bool Scanning` (raises `NotScanning` too), `bool NotScanning => !Scanning`, and `ReplaceChecks(IList<CheckVm>, CoverageVm)` (clear+refill `Checks`, set `Coverage`, select first check — `CollectionChanged → RaiseAggregates` already refreshes the verdict).

### Task 5: Panel wiring
XAML: Re-check button gets `x:Name="RescanButton"`, `Click="Rescan_Click"`, `IsEnabled="{Binding NotScanning}"`; drop the stub ToolTip. Code-behind: `_uiApp` + real `SetRevitApp` + `UiAppLive => _uiApp ?? App.UiApp` (JKR :114-120), `Rescan_Click => _ = RunScanAsync()`, `RunScanAsync` guard/`try/catch TaskDialog`/`finally Scanning=false`, `RunScanInner`: extract facts → cascade loop (≤6 rounds): call `CheckAsync`; on `needs_input` auto-advance single option, TaskDialog command links (cap 4) for multiple, cancel aborts; on `Error == LoginRequiredMessage` → persistent `ScopeDetail` message (JKR's no-dialog rule); other error → TaskDialog "Scan failed"; success → `BombaMapper.Map` → `ReplaceChecks`, `ScopeDetail` shows jurisdiction + rules version/status.

### Task 6: Compile gate
`~/.dotnet/dotnet build RevitWebAppSync.csproj -f net48 -v q --nologo` and `-f net8.0-windows`; 0 errors, no new Bomba-file warnings. Stage everything. Report Windows/Revit runtime verification as pending.
