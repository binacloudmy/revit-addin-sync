# Copilot Result Pattern Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After every copilot turn the drafter sees one short reply, a result card shaped for the task, and the touched elements marked in the Revit view, with Undo everywhere and no confirmation card.

**Architecture:** Add-in only. Writes auto-approve inside the existing W3 preview→commit runner. A new `ResultCardModel` is built from the turn's envelopes and rendered by `TurnEvidenceView`. A new `HighlightSet` (element ids by state) feeds two drawing layers: `CanvasRings` (Revit `TemporaryGraphicsManager`, rings in the canvas) and the existing `HighlightOverlayWindow` (toast, bottom-docked callouts, projected leaders).

**Tech Stack:** C# / WPF / Revit API 2023–2027 via Nice3point NuGet (net48, net8.0-windows, net10.0-windows). xunit tests run in the scratch harness `scratchpad/fix-tests`.

**Spec:** `docs/superpowers/specs/2026-11-09-copilot-result-pattern-design.md`

## Global Constraints

- No backend / protocol 3 / `contracts/copilot/tools.json` changes except the optional Task 10.
- No new NuGet dependencies.
- Ring colours: changed `#4CC77F`, created/selected `#6B9BFF`, fixable `#FF7B6B`, needs-you `#E7A84A`.
- Fade timer 8 s; navigation redraw debounce 150 ms.
- Overlay stays click-through except its own controls.
- Branch `fix/copilot-jumble`; never `git add -A`; commit after each task.
- Build on Mac: `~/.dotnet/dotnet build RevitWebAppSync.csproj -c Debug -p:Version=0.0.88`. Tests: `cd scratchpad/fix-tests && ~/.dotnet/dotnet test` (add new test files to `fix-tests.csproj` as absolute `<Compile Include>` entries, like the existing ones).

## Review Focus

1. A write whose preview fails must still stop: auto-approve must not turn a preview error into a commit (Task 1 test `PreviewFailure_StillRejects`).
2. An envelope with `changes.touched` null or empty must produce an empty `HighlightSet`, not a crash (Task 4 test).
3. Projection with zero-width zoom corners (degenerate view) must return null, not NaN (Task 6 test).
4. Elements deleted by the turn have no location; rings for `Deleted` use the pre-commit position recorded in the preview, else are skipped (Task 5: skip when location null).
5. Undo must clear rings and overlay even when `UndoTurnAsync` fails (Task 7: clear first, then undo).

---

### Task 1: Auto-approve writes

**Files:**
- Create: `BinaVibe/Copilot/Writes/AutoApprovePresenter.cs`
- Modify: `UI/Copilot/PaneToolRunner.cs:181-189` (Presenter.AskAsync)
- Test: `Tests/AutoApproveTests.cs`

**Interfaces:**
- Produces: `public sealed class AutoApprovePresenter : IConfirmPresenter { Task<ConfirmDecision> AskAsync(ConfirmRequest req, CancellationToken ct) }` returning `new ConfirmDecision { Approved = true, ApproveThisBuild = false }`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task AutoApprove_ReturnsApproved() {
  var d = await new AutoApprovePresenter().AskAsync(new ConfirmRequest(), CancellationToken.None);
  Assert.True(d.Approved); Assert.False(d.ApproveThisBuild); }

[Fact] public async Task PreviewFailure_StillRejects() {
  // WriteBatchRunner with FakeEngine whose PreviewAsync throws / returns a failed preview, presenter = AutoApprovePresenter
  // assert every result envelope has Ok == false and Status == "rejected"/"failed" (match WriteBatchRunnerTests fixture)
}
```
Reuse `FakeEngine` from `Tests/WriteBatchRunnerTests.cs:42-52`.

- [ ] **Step 2: Run tests, expect compile failure** (`AutoApprovePresenter` missing)
- [ ] **Step 3: Implement `AutoApprovePresenter`** in `BinaVibe/Copilot/Writes/AutoApprovePresenter.cs`.
- [ ] **Step 4: In `PaneToolRunner.cs:181-189`** keep building the `ConfirmCardModel` (the preview facts feed the result card later) but return `Task.FromResult(new ConfirmDecision { Approved = true })`; do not call `_owner.View.ShowConfirmAsync`.
- [ ] **Step 5: Run tests, expect PASS**; run the full harness, expect no regressions except tests that assert a confirm card is shown; update those to assert no card (`ConfirmCardModelTests` stays, it tests the model).
- [ ] **Step 6: Commit** `feat(copilot): apply writes without a confirmation card`

### Task 2: Reply language follows the prompt

**Files:**
- Create: `UI/Copilot/Model/PromptLanguage.cs`
- Modify: `UI/Copilot/CopilotTurnRouter.cs:236`
- Test: `Tests/PromptLanguageTests.cs`

**Interfaces:**
- Produces: `public static class PromptLanguage { public static ReceiptLanguage Detect(string? prompt) }`. Malay when ≥2 of these tokens appear as words (case-insensitive): `tolong sila isi isikan semua untuk dalam dengan dan pada jenis lampu tukar buat pilih tunjuk`; else English.

- [ ] **Step 1: Tests**: `Detect("Tolong isikan Model untuk kedua-dua jenis lampu") == Ms`; `Detect("Fill Model and URL on both types") == En`; `Detect(null) == En`; `Detect("dan") == En` (one token is not enough).
- [ ] **Step 2: Run, expect failure.**
- [ ] **Step 3: Implement** and replace line 236 with `var lang = PromptLanguage.Detect(text);` (the turn's prompt text variable in that method).
- [ ] **Step 4: Run, expect PASS. Commit** `feat(copilot): receipt language follows the prompt`

### Task 3: ResultCardModel and headline

**Files:**
- Create: `UI/Copilot/Model/ResultCardModel.cs`
- Test: `Tests/ResultCardModelTests.cs`

**Interfaces:**
- Consumes: `TurnEvidence` (`UI/Copilot/Model/TurnEvidence.cs:107`), `Envelope` (`BinaVibe/Copilot/Contracts/Envelope.cs:97`), `FieldChangeDto`, `ChangesDto.Added/Deleted`, `HighlightDto`, `WarningDto`.
- Produces:
```csharp
public enum ResultKind { None, Changes, Created, Findings, Selection }
public sealed class ChangeRow { public string Param; public string? Before; public string? After; public string Source; }   // Source: "your prompt" | attachment name | "" 
public sealed class TypeGroup { public string TypeName; public int Instances; public List<long> Ids; public List<ChangeRow> Rows; }
public sealed class CreatedItem { public long Id; public string Label; }
public sealed class Finding { public string Title; public string Severity; public List<long> Ids; public bool Fixable; }
public sealed class ResultCardModel {
  public ResultKind Kind; public string Headline; public string Detail; public string? Note;
  public List<TypeGroup> Groups; public List<CreatedItem> Created; public List<Finding> Findings;
  public int Selected; public int VisibleInView;
  public static ResultCardModel From(TurnEvidence ev, IReadOnlyList<Envelope> envelopes, ReceiptLanguage lang);
}
```
Kind rules: any `set_parameters`/`run_revit_code` commit with `changes.touched.modified` → Changes; `added` non-empty and no modified → Created; envelopes with `Warnings` and no commit → Findings; else if a read envelope has `Highlight.Selected > 0` → Selection; else None.
Headline (En): Changes `"{rows×groups} changes applied"` / Detail `"{groups} types · {instances} fixtures · read back from Revit"` (append `" · {n} mismatches"` when `Verified.Mismatches.Count > 0`); Created `"{n} created"`; Findings `"{ids} elements fail · {kinds} kinds"`; Selection `"{Selected} selected"` / `"{VisibleInView} visible in view"`. Ms strings mirror ReceiptTemplates style.
Group by the `types[]` entries in `set_parameters` `data` (`SetParametersTool.cs:278-295`), instances from `instances_affected`; when absent, one group per distinct element id with `Instances = 1`.

- [ ] **Step 1: Tests** with hand-built envelopes (copy the JSON fixture style from `Tests/EnvelopeChangesTests.cs`): two types × 5 fields → `Kind == Changes`, `Headline == "10 changes applied"`, `Groups.Count == 2`, `Groups[0].Rows.Count == 5`; `added` only → Created; empty touched → `Kind == None`; a mismatch → Detail contains "1 mismatch".
- [ ] **Step 2: Run, expect failure. Step 3: Implement. Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** `feat(copilot): result card model from turn envelopes`

### Task 4: HighlightSet producer

**Files:**
- Create: `UI/Copilot/Highlights/HighlightSet.cs`
- Test: `Tests/HighlightSetTests.cs`

**Interfaces:**
- Produces:
```csharp
public enum MarkState { Changed, Created, Selected, Fixable, NeedsYou, Deleted }
public sealed class HighlightSet {
  public Dictionary<MarkState, HashSet<long>> Ids;
  public List<TypeGroup> Groups;            // from ResultCardModel, for callouts
  public bool IsEmpty { get; }
  public static HighlightSet From(ResultCardModel card, IReadOnlyList<Envelope> envelopes);
  public static string ColorOf(MarkState s);   // the four hex values from Global Constraints; Deleted → NeedsYou colour
}
```
Mapping: modified ids → Changed; added → Created; deleted → Deleted; Warnings with Severity "error" → Fixable, else NeedsYou; Selection kind → Selected ids from the find envelope `data.rows[].id` when present.

- [ ] **Step 1: Tests**: 52 modified ids across two groups → `Ids[Changed].Count == 52`; null `Changes` → `IsEmpty`; `ColorOf(Changed) == "#4CC77F"`.
- [ ] **Step 2–4: fail → implement → pass. Step 5: Commit** `feat(copilot): highlight set from result envelopes`

### Task 5: Rings in the Revit canvas

**Files:**
- Create: `UI/Copilot/Highlights/RingImages.cs`, `UI/Copilot/Highlights/CanvasRings.cs`
- Modify: `UI/Copilot/CopilotPanel.xaml.cs:190-196`
- Test: `Tests/RingImagesTests.cs`

**Interfaces:**
- Produces:
```csharp
public static class RingImages { public static string PathFor(MarkState s, double dpiScale); }  // writes %LOCALAPPDATA%\Bina\RevitSync\rings\{state}-{scale}.png once; 18 px ring at 1.0, stroke 2.5, dashed for NeedsYou/Deleted; returns the path
public sealed class CanvasRings : ITemporaryGraphicsHandler {
  public void Show(UIDocument uidoc, View view, HighlightSet set, Func<long, XYZ?> locationOf);
  public void Clear();
  public event Action<long>? ElementClicked;     // first click selects; the host decides about Type Properties
}
```
Implementation notes: `TemporaryGraphicsManager.GetTemporaryGraphicsManager(doc)`, `AddControl(new InCanvasControlData(path, xyz), view.Id)`, keep returned indices for `RemoveControl`; `SetControlHandler(this)`; `locationOf` = `(el.Location as LocationPoint)?.Point ?? el.get_BoundingBox(view)?.Center`; skip ids with null location. Must run in a Revit API context: route through the panel's existing Revit job mechanism (the one `SelectElements` uses in `ChatView.xaml.cs:119`).
- In `CopilotPanel.xaml.cs` on result: build `HighlightSet` and call `CanvasRings.Show`; on Clear/Undo call `Clear()`.

- [ ] **Step 1: Test** `RingImages.PathFor(MarkState.Changed, 1.0)` returns an existing file ending `changed-1.png`, decodes to 18×18 (use `PngBitmapDecoder` in test; harness is net10.0-windows).
- [ ] **Step 2–4: fail → implement → pass.**
- [ ] **Step 5: Wire** `CanvasRings` into the panel and build the add-in (`dotnet build`), expect 0 errors on all three TFMs.
- [ ] **Step 6: Commit** `feat(copilot): mark touched elements with in-canvas rings`

### Task 6: View projection

**Files:**
- Create: `UI/Copilot/Highlights/ViewProjection.cs`
- Test: `Tests/ViewProjectionTests.cs`

**Interfaces:**
- Produces: `public static (double XPct, double YPct)? ToPercent(XYZ p, XYZ corner0, XYZ corner1, XYZ right, XYZ up)`; returns null when the projected width or height is < 1e-9. `XPct = 100·((p−c0)·right)/((c1−c0)·right)`, `YPct = 100·(1 − ((p−c0)·up)/((c1−c0)·up))` with c0 = bottom-left in Revit's zoom corners (sort by dot products first so corner order does not matter).

- [ ] **Step 1: Tests**: plan view right=(1,0,0) up=(0,1,0), corners (0,0,0)-(100,50,0): point (25,25,0) → (25, 50); point (0,50,0) → (0, 0); corners swapped give the same result; corners equal → null.
- [ ] **Step 2–4: fail → implement → pass. Step 5: Commit** `feat(copilot): project model points to view percent`

### Task 7: Overlay toast, callouts, leaders, fade, undo

**Files:**
- Modify: `UI/Copilot/Highlights/HighlightOverlayWindow.xaml`, `HighlightOverlayWindow.xaml.cs`, `HighlightOverlay.cs`, `UI/Copilot/Model/CopilotModels.cs:429` (HighlightMarker gains `long ElementId`, `MarkState State`, `string? GroupName`, `int GroupCount`, `List<ChangeRow>? Rows`)
- Modify: `UI/Copilot/CopilotPanel.xaml.cs` (feed markers; Idling redraw; undo wiring)
- Test: `Tests/HighlightOverlaySourceTests.cs` (source-text assertions, pattern from `Tests/UpdateGatePaneTests.cs:39-45`)

**Interfaces:**
- Consumes: `HighlightSet`, `ViewProjection.ToPercent`, `UndoService.UndoTurnAsync(turnId, ct)`.
- Produces: `HighlightOverlay.Show(UIApplication app, HighlightSet set, string headline, string detail, Action onUndo, Action onClear)`; `HighlightOverlay.SetMode(bool after)`; toast buttons `UndoBtn`, `KeepArea`, `CloseBtn`; callout cards `CalloutLeft`, `CalloutRight`; `LeaderCanvas`.

Behaviour: toast text = headline + detail; after 8 s hide rings' pulse and the toast unless the toast body was clicked (keep). Callouts: up to two groups, each shows type name (last 16 chars), count pill, first 3 rows, "Open Type Properties" (selects one instance, posts `PostableCommand.TypeProperties`). Leader from each callout's top centre to the projected position of the group's first in-view element; skip when the active view is `View3D` with `IsPerspective`. Subscribe to `UIApplication.Idling`; when `GetZoomCorners()` differs from the last tick hide the overlay, when unchanged for 150 ms redraw. Undo: `Clear()` rings and overlay first, then `UndoTurnAsync`, then show an "Undone" toast with Redo disabled (no public redo API) unless `UndoService` exposes one.

- [ ] **Step 1: Source tests**: XAML contains `x:Name="UndoBtn"`, `x:Name="CalloutLeft"`, `x:Name="LeaderCanvas"`; code-behind contains `IsPerspective` and `150`.
- [ ] **Step 2–4: fail → implement → pass; build all TFMs.**
- [ ] **Step 5: Commit** `feat(copilot): overlay toast, callouts and leaders for results`

### Task 8: Result card in the panel, quiet footer

**Files:**
- Modify: `UI/Copilot/Controls/TurnEvidenceView.cs:40` (render `ResultCardModel` first), `UI/Copilot/Screens/ChatView.xaml.cs:771-826` (remove `BuildRatingNudge`), `:856-940` (footer: time · Details toggle · thumbs only), `UI/Copilot/Model/ReceiptTemplates.cs:214-218` (NotSearched → muted note text), `UI/Copilot/Model/TurnEvidence.cs` (expose `Steps`/tools for Details)
- Test: `Tests/ResultCardRenderTests.cs` (source assertions) and extend `Tests/ReceiptTemplateTests.cs`

Rendering (per spec): header (Headline, Detail, Undo button) coloured by kind; for Changes: type tabs (`TypeGroup`) → table Parameter | Before → After | Source; a Source block above the table when the turn had a PDF attachment: "Source · {attachment}" + cells built from `ChangeRow.After` values (no image); for Created: rows Number · Name · Open (`uidoc.ActiveView = view` or `ShowElements`); Findings: Title · Count · Show · Fix (Fix sends the turn's suggested fix prompt back through the composer); Selection: Type · Count · Select · Zoom. Action row: Highlight in model (re-show rings), Open Type Properties, Export (existing `Export`), Save as command (existing). Footer: `{time} · Details · {tools} tools · {notes} notes ▸` toggle expanding to steps (ProgressTrail), tools, notes; thumbs up/down only. Delete the Rate banner, the duplicate Export chips (tables keep theirs), the second Save as command, and the `EvidenceLine`/`NotSearchedLine` rows (move them into Details).

- [ ] **Step 1: Tests**: `ReceiptTemplates.NotSearchedLine` returns text starting "Not loaded:" (En) / "Tidak dimuat:" (Ms); `ChatView.xaml.cs` source no longer contains `BuildRatingNudge(`; `TurnEvidenceView.cs` contains `ResultCardModel`.
- [ ] **Step 2–4: fail → implement → pass; build.**
- [ ] **Step 5: Commit** `feat(copilot): result card and quiet footer`

### Task 9: Deploy and verify live

**Files:** none (build + scp + swap, per `project_lean_harness_test_setup` memory)

- [ ] **Step 1:** `~/.dotnet/dotnet build RevitWebAppSync.csproj -c Debug -p:Version=0.0.88`; scp DLL+PDB to `jumble-deploy/` on binacloudpc; `powershell -File jumble-deploy\swap.ps1` (update the version folder in swap.ps1 to 0.0.88 or keep 0.0.87 and overwrite).
- [ ] **Step 2:** Run the brochure prompt from the demo script on a fresh unsaved model with `scratchpad/turn.py`; expect: no confirm card, result card with 10 rows in 2 type tabs, toast + green rings in the view, Undo clears them and reverts values (check Type Properties).
- [ ] **Step 3:** Run the isolate prompt; expect Selection card with 54 and three type rows.
- [ ] **Step 4:** Save screenshots to `scratchpad/report/v2_*.png`; note anything that failed in the plan file.
- [ ] **Step 5: Commit** any fixes; push branch; open PR to `develop` (do not merge).

### Task 10 (optional, needs contract change): per-value source

**Files:** `contracts/copilot/tools.json` (both repos), `BinaVibe/Copilot/Tools/SetParametersTool.cs`, `tests/assistant/test_contracts.py` hashes.

Add optional `source` string arg to `set_parameters` ("page 2, row 1"), echoed in `data.source`; `ChangeRow.Source` uses it when present; the Source block shows "View page N" when it parses `page N` and the attachment is a PDF (opens page text in a modal; image rendering stays out of scope). Only do this after Tasks 1–9 are verified live.
