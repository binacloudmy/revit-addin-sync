# JKR Compliance Clear Sheet Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle the JKR Compliance pane and its windows to the light Clear Sheet design, remove the behaviour Ashraf ticked, and keep (restyled) the behaviour he left unticked.

**Architecture:** Reskin in place on a new local branch off `origin/thrashraf/jkr-compliance`. Behaviour changes land first in the Revit-free screen models (`UI/Jkr/Panel/Core/*`, covered by `Tests/`), then each WPF view is reworked against its Clear Sheet screen. The hidden `JkrComplianceDashboardPanel` stays the engine; only its dead visuals go.

**Tech Stack:** C# / WPF (XAML), Revit add-in, xUnit (`Tests/Tests.csproj`, net10.0-windows), `UiHarness` for screenshots.

**Spec:** `C:\Bina\model-audit\docs\superpowers\specs\2026-11-19-jkr-clear-sheet-redesign-design.md` (read it first; section numbers below refer to it).

## Global Constraints

- Design source: `C:\Bina\model-audit\JKR-clean-redesign.html` (nested bundle; open in a browser). Extracted screens: `<scratchpad>\cs-deep\<id>\template.html` (re-extract with `<scratchpad>\tools\unpack.py` + `deep.py` if the scratchpad was cleared).
- Clear Sheet decides everything it draws. Ashraf's checklist (spec §5) decides the rest: remove 1, 2, 5, 6, 8; keep and restyle 3, 4, 7, 9–15; item 16 → light theme.
- Colours: ink `#15181D`, text `#4B5361` / `#646B78`, faint `#8A919C`, lines `#E5E7EB` `#D0D4DA` `#C9CED6`, surfaces `#FFFFFF` `#F8F9FA` `#F1F3F6` `#EEF0F3`, fail `#C2331F`, pass `#3DA570`, medium/warn `#B26B00`, semak manual `#5B3FD0`, amber `#F2A91F` (logo top step only).
- Fonts: Public Sans (UI), IBM Plex Mono (values, ids, file names); fall back to Segoe UI / Consolas.
- Pane width 380 px; nothing may clip at 320 px (BM).
- Every user-visible string is BM + EN through `PanelStrings` / `PaneText`.
- No Storyboards (they crash Revit). Keep keys J/K, Enter, F, A, /, Esc.
- No billing, credits, consent or pricing work. No change to `Services/JkrSpec/*` logic (C1.6.1 text only). No change to the Borang PDF layout.
- No push, merge or PR. Commit locally per task only.
- Test command (from `.github/workflows/tests.yml`): `dotnet test Tests/Tests.csproj -c Release -p:SkipRevitSources=true`
- Screenshot command: `dotnet run --project UiHarness -- --shot-jkr <dir>`

## Review Focus

1. A marked-manual item must still count as a fail in the % and on the Borang → Task 3 pins it.
2. Reopen on an item persisted in the audit file must survive a re-scan (store drops the entry) → Task 3 pins it.
3. C1.6.1 with no room schedule must stay a fail with how-to-fix steps and no fix button → Task 2 pins it.
4. A Resolved (fixed) item must not count as passed before a re-scan once the "Fixed · re-scan pending" label is gone → Task 4 pins it.
5. BM at 320 px: long BM labels (tabs, buttons) must not clip → each screen task (8–16) checks BM screenshots, and Task 17 checks 320 px.

---

### Task 0: Worktree, branch, baseline

**Files:** none changed.

- [ ] **Step 1: Create the worktree** (leaves `C:\Bina\revit-addin-sync` on `feat/jkr-binazone-panel` untouched)

```bash
git -C C:/Bina/revit-addin-sync fetch origin thrashraf/jkr-compliance
git -C C:/Bina/revit-addin-sync worktree add -b irfan/jkr-clear-sheet C:/Bina/revit-addin-sync-clear-sheet origin/thrashraf/jkr-compliance
```

- [ ] **Step 2: Baseline tests.** Run the test command in the worktree. Expected: all pass. Record any test that already fails; do not fix it in this plan.
- [ ] **Step 3: Baseline screenshots.** `dotnet run --project UiHarness -- --shot-jkr C:/Bina/shots/before`. Keep them for comparison.
- [ ] **Step 4: Copy spec and plan** into the worktree at `docs/superpowers/specs/` and `docs/superpowers/plans/`, then commit: `docs: Clear Sheet redesign spec and plan`.

---

### Task 1: Remove the Undo toast after Ignore (item 2)

**Files:**
- Modify: `UI/Jkr/Panel/Core/PanelCore.cs` (remove `UndoToastTime`, `UndoIds`, `Undo()`; `ShowToast(string msg)` loses its `undoIds` parameter; `Ignore` shows a plain toast)
- Modify: `UI/Jkr/Panel/Core/PaneText.cs` (`ToastTime(string message)` loses `canUndo`)
- Modify: `UI/Jkr/Panel/JkrPaneShell.xaml(.cs)` (remove `ToastUndo`, `UndoRequested`, `ShowToast(..., canUndo)`), `UI/Jkr/Panel/DetailWindow.xaml(.cs)` (same), `UI/Jkr/Panel/Core/PaneShellModel.cs` (`UndoLabel`)
- Modify: `UI/JkrComplianceDashboardHost.cs` (drop `UndoRequested` wiring; `ToastChanged` passes message only), `UI/JkrComplianceDashboardPanel.xaml.cs` (remove `UndoLastDecision`; `ToastChanged` event becomes `Action<string>`)
- Modify: `UI/Jkr/Panel/Core/PanelStrings.cs` (remove key `undo` from the key list and both dictionaries if nothing else uses it)
- Test: `Tests/PanelCoreTests.cs`, `Tests/PaneShellTests.cs`, `Tests/PanelVmUndoTests.cs` (delete tests of the 6 s undo; keep tests of other behaviour)

**Interfaces:**
- Produces: `PanelCore.ShowToast(string msg)`, `PaneText.ToastTime(string message) : TimeSpan`, `JkrComplianceDashboardPanel.ToastChanged : event Action<string>`, `JkrPaneShell.ShowToast(string message)`, `DetailWindow.ShowToast(string message)`.

- [ ] **Step 1: Write the failing test** in `Tests/PanelCoreTests.cs`

```csharp
[Fact]
public void Ignore_shows_a_plain_toast_that_closes_after_the_normal_time()
{
    var clock = new FakeClock();                       // existing test clock helper
    var core = new PanelCore(PanelFixture.Items(), PanelFixture.BasePass, clock);
    core.Ignore(new[] { core.Issues.First(x => !x.IsHuman).Id });
    Assert.NotNull(core.Toast);
    clock.Advance(PanelCore.ToastTime);
    Assert.Null(core.Toast);
}
```

(Use the fixture and clock helpers the existing `PanelCoreTests` already use; match their names.)

- [ ] **Step 2: Run it.** Expected: FAIL (toast still open, it uses the 6 s undo time).
- [ ] **Step 3: Remove the undo path** listed under Files. Delete the tests that assert Undo behaviour (`PanelCoreTests` undo cases, `PanelVmUndoTests` cases for the pane toast). Keep `PanelVm.IsUndoable` only if something other than the toast still calls it; otherwise delete it with its tests.
- [ ] **Step 4: Run the full test command.** Expected: PASS. `git grep -n "UndoIds\|UndoRequested\|ToastUndo\|UndoLastDecision"` returns nothing.
- [ ] **Step 5: Commit** `refactor(jkr): Ignore no longer offers a 6 s Undo (Clear Sheet)`

---

### Task 2: Remove the paid Generate room schedule and consent (item 1)

**Files:**
- Modify: `UI/Jkr/Panel/Core/PanelModels.cs` (`enum FixKind { None, Fix }`)
- Modify: `UI/Jkr/Panel/Core/PanelCore.cs` (remove `Consent`, `ToggleConsent`, `ConfirmDisabled`, Generate branches in `AskFix` / `DoConfirm`)
- Modify: `UI/Jkr/ViewModels/JkrConfirmModel.cs` (remove `JkrConfirmKind.Generate`, `NeedsConsent`, `Consent`, `ConsentText`)
- Modify: `UI/Jkr/Modals/JkrConfirmWindow.xaml(.cs)` (remove consent panel and handlers)
- Modify: `UI/Jkr/Panel/Core/DetailScreenModel.cs` (remove `CanGen`, `GenTitle`, `GenSub`, `GenBtn`), `UI/Jkr/Panel/DetailWindow.xaml` (remove the "Generate: paid room schedule" block)
- Modify: `UI/Jkr/Panel/Core/PanelStrings.cs` (remove `genTitle`, `genSub`, `genBtn`, `toastGen`, `confirmGenT`, `confirmGenS`, `confirmGenB`, `consent` from the key list and both dictionaries)
- Modify: `UI/Jkr/Panel/Core/PanelFixture.cs:132` (that item becomes `Fix = FixKind.None` with how-to-fix steps)
- Modify: `Services/JkrSpec/SpecChecks.BC.cs:763-765`
- Test: `Tests/JkrConfirmModelTests.cs`, `Tests/PanelCoreTests.cs`, `Tests/SpecChecksBCTests.cs`

- [ ] **Step 1: Write the failing test** in `Tests/SpecChecksBCTests.cs`

```csharp
[Fact]
public void C1_6_1_fails_without_a_room_schedule_and_offers_no_paid_action()
{
    var r = /* evaluate C1.6.1 on a SpecModel with rooms and no Rooms schedule, as the existing C1.6 tests do */;
    Assert.False(r.Passed);
    Assert.Equal("Tiada jadual ruang (room schedule)", r.Remark);
    Assert.Equal("No room schedule", r.RemarkEn);
    Assert.DoesNotContain("berbayar", r.Remark);
}
```

(Use the C1.6 test helpers already in `SpecChecksBCTests` around line 555; match their result property names.)

- [ ] **Step 2: Run it.** Expected: FAIL on the remark text.
- [ ] **Step 3: Change the C1.6.1 messages** to BM `"Tiada jadual ruang (room schedule)"` and EN `"No room schedule"`. Keep `actual`, `required` and `key` as they are.
- [ ] **Step 4: Remove Generate and consent** everywhere listed under Files. Delete the consent tests in `JkrConfirmModelTests` (lines 70–130) and `PanelCoreTests.Generate_needs_consent_which_starts_unticked_every_time`.
- [ ] **Step 5: Run the full test command.** Expected: PASS. `git grep -n "FixKind.Generate\|Consent\|genTitle\|toastGen"` returns nothing in `UI/` and `Tests/` (the OAuth `consent` test in `BinaOAuthClientTests` is unrelated and stays).
- [ ] **Step 6: Commit** `refactor(jkr): drop the paid room-schedule Generate and its consent (C1.6.1 stays a fail)`

---

### Task 3: Mark as Manual (option b) and Reopen

**Files:**
- Modify: `UI/Jkr/Panel/Core/PanelModels.cs` (`PanelItem`: add `public bool MarkedManual;` and `public bool CanMarkManual;`)
- Modify: `UI/Jkr/Panel/Core/ResultsAdapter.cs` (`StatusOf`: `ManualFixNeeded → ItemStatus.Open`; `FromIssue` sets `MarkedManual = i.Status == IssueStatus.ManualFixNeeded`, `CanMarkManual = !human && i.CanMarkManual`)
- Modify: `UI/Jkr/Panel/Core/ResultsScreenModel.cs` (events `MarkManualRequested : Action<string>`, `ReopenRequested : Action<string>`; methods `MarkManual(string id)`, `Reopen(string id)`; Fix all, Fix n on a check, and the AI fixer candidate lists exclude `MarkedManual`; row property `ManualTag` text when `MarkedManual`)
- Modify: `UI/Jkr/Panel/Core/SpecChecklist.cs:720` (`FixIds` excludes `MarkedManual`)
- Modify: `UI/JkrComplianceDashboardPanel.xaml.cs` (new `public void SetManual(string id, bool manual)` and `public void Reopen(string id)`, both saving with `JkrAuditStore.Save` like `SetReviewed`)
- Modify: `UI/JkrComplianceDashboardHost.cs` (wire `results.MarkManualRequested += id => _panel.SetManual(id, true)`, `results.ReopenRequested += id => _panel.Reopen(id)`)
- Modify: `UI/Jkr/Export/BorangDoc.cs:283` (`ManualFixNeeded` → `text + " (handled manually)"`; Ignored / Approved keep `" (ignored by user)"`)
- Modify: `UI/Jkr/Panel/Core/PanelStrings.cs` (keys `markManual` = BM "Tanda manual" / EN "Mark as Manual"; `reopen` = BM "Buka semula" / EN "Reopen"; `manualTag` = BM "Manual · anda baiki dalam Revit" / EN "Manual · you'll fix it in Revit")
- Test: `Tests/ResultsScreenTests.cs`, `Tests/BorangExportTests.cs`, `Tests/PanelCoreTests.cs`

**Interfaces:**
- Produces: `PanelItem.MarkedManual`, `PanelItem.CanMarkManual`, `ResultsScreenModel.MarkManual(string id)`, `ResultsScreenModel.Reopen(string id)`, events `MarkManualRequested`, `ReopenRequested`, `JkrComplianceDashboardPanel.SetManual(string id, bool manual)`, `JkrComplianceDashboardPanel.Reopen(string id)`. Task 13 binds to these.

- [ ] **Step 1: Write the failing tests**

```csharp
// ResultsScreenTests
[Fact]
public void A_marked_manual_issue_stays_open_and_counts_as_a_fail()
{
    var vm = Issue(status: IssueStatus.ManualFixNeeded);          // existing helper
    var item = ResultsAdapter.FromIssue(vm);
    Assert.Equal(ItemStatus.Open, item.Status);
    Assert.True(item.MarkedManual);
    Assert.False(item.Passed);
}

[Fact]
public void Fix_all_leaves_marked_manual_issues_out()
{
    // two open fixable issues, one marked manual → FixAllRequested gets only the other id
}

[Fact]
public void Reopen_on_an_ignored_issue_raises_ReopenRequested_with_its_id() { /* ... */ }

// BorangExportTests
[Fact]
public void A_marked_manual_issue_prints_as_handled_manually_not_ignored()
{
    // build the Borang from one ManualFixNeeded issue in row A5
    Assert.EndsWith("(handled manually)", d.SecA[4].Remark);
    Assert.Equal(("", "X"), (d.SecA[4].Yes, d.SecA[4].No2));     // still not compliant
}
```

Update `ResultsScreenTests` line 61 (`[InlineData(IssueStatus.ManualFixNeeded, ItemStatus.Ignored)]` → `ItemStatus.Open`).

- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement** the Files list. `Reopen` sets `IssueStatus.Open` (the audit store drops the entry, `JkrAuditStore.cs:180`); `SetManual(id, false)` is the same as Reopen. `SetManual(id, true)` only applies when `issue.IsAiOpen && issue.CanMarkManual`.
- [ ] **Step 4: Add a store round-trip test:** save a ManualFixNeeded issue, then Reopen, then reload → status Open (use the temp-folder pattern of the existing `JkrAuditStore` tests).
- [ ] **Step 5: Run the full test command.** Expected: PASS.
- [ ] **Step 6: Commit** `feat(jkr): Mark as Manual keeps the issue open as a fail; Reopen for ignored and manual`

---

### Task 4: Remove the Fixed card and "Fixed · re-scan pending" (item 6)

**Files:**
- Modify: `UI/Jkr/Panel/Core/DetailScreenModel.cs` (remove `IsDone`, `DoneLabel`; Resolved state label is `T("resolvedRow")`)
- Modify: `UI/Jkr/Panel/DetailWindow.xaml` (remove the "Done: fixed / ignored" block)
- Modify: `UI/Jkr/Panel/Core/PanelStrings.cs` (remove `fixedRow`, `doneFix`, `doneIgn`; add `resolvedRow` = BM "Selesai" / EN "Resolved"; keep `fixedPending`, the Borang still uses it)
- Test: `Tests/DetailScreenTests.cs:113`, `Tests/PanelCoreTests.cs:317`

- [ ] **Step 1: Change the test** `DetailScreenTests` line 113 to `Assert.Equal(("Resolved", "ok"), (c.SvLabel, c.SvKind));`.
- [ ] **Step 2: Run it.** Expected: FAIL.
- [ ] **Step 3: Implement** the Files list.
- [ ] **Step 4: Keep the honesty rule:** `PanelCoreTests.A_fixed_item_is_resolved_but_not_passed_until_a_rescan` and `BorangExportTests` line 106–107 must pass unchanged.
- [ ] **Step 5: Run the full test command.** Expected: PASS.
- [ ] **Step 6: Commit** `refactor(jkr): no Fixed card; a fixed issue shows as Resolved until the re-scan`

---

### Task 5: Remove export options JSON and spec references (item 8)

**Files:**
- Modify: `UI/Jkr/Modals/ExportWindow.xaml:53-54` and `ExportWindow.xaml.cs:27-28, 55, 67, 153` (remove `CbSpec`, `CbJson` and the code they switch; spec reference columns are left out of the export)

- [ ] **Step 1: Remove** the two check boxes and their code paths. If `JkrComplianceService` is then unused in `ExportWindow`, drop the constructor parameter and update the caller in `JkrComplianceDashboardPanel.OpenExportWindow`.
- [ ] **Step 2: Build** the add-in project (`dotnet build RevitWebAppSync.csproj -c Release`). Expected: success. `git grep -n "CbJson\|CbSpec"` returns nothing.
- [ ] **Step 3: Commit** `refactor(jkr): export issues drops the JSON and spec-reference options`

---

### Task 6: Delete dead UI

**Files:**
- Delete: `UI/Jkr/Modals/IssueFocusWindow.xaml(.cs)`; remove `OpenFocusWindow` / `CloseFocusWindow` / `_focusWindow` and the `FocusOpen` branches in `UI/JkrComplianceDashboardPanel.xaml.cs`; `OpenIssue` goes if nothing calls it
- Delete: each of `UI/Jkr/Controls/CategoryPill`, `PillStat`, `ProgressRing`, `IssueRow`, `StatusBadge`, `KbdHint` only if `git grep` shows no remaining user outside deleted code
- Modify: hidden visuals in `UI/JkrComplianceDashboardPanel.xaml` that nothing shows (`EngineHost` is collapsed): delete only elements whose code-behind references are also gone; keep every element the engine code reads or writes

- [ ] **Step 1: Delete** as listed, one file at a time, grepping for each type name first.
- [ ] **Step 2: Build and run the full test command.** Expected: build OK, tests PASS.
- [ ] **Step 3: Commit** `chore(jkr): delete the unreachable focus window and old dashboard controls`

---

### Task 7: Clear Sheet tokens and fonts (item 16)

**Files:**
- Modify: `UI/Jkr/Panel/PanelTokens.xaml` (keep every `Bz.*` key name; change values to Clear Sheet; see mapping below)
- Modify: `UI/Jkr/Tokens.xaml`, `UI/Jkr/Styles.xaml` (old dark tokens still used by `ExportWindow`, `BorangPreviewWindow`, `FixResultWindow`, `JkrConfirmWindow`: point them at the same Clear Sheet values, or move those windows onto `PanelTokens.xaml` and delete the old file when nothing uses it)
- Create: `Fonts/PublicSans-*.ttf`, `Fonts/IBMPlexMono-*.ttf` + their OFL licence files, registered the same way `Fonts/Inter-*.ttf` is (see `Fonts/README.txt`)
- Test: `Tests/ClearSheetTokensTests.cs` (create)

Mapping (builder may refine against the design file): `Bz.Surface` `#FFFFFF`; `Bz.Sunken` `#F8F9FA`; `Bz.Sunken2` `#F1F3F6`; `Bz.Track` `#EEF0F3`; `Bz.Ink`/`Bz.Text` `#15181D`; `Bz.TextBody` `#4B5361`; `Bz.Text2` `#646B78`; `Bz.Text3` `#8A919C`; `Bz.Line` `#E5E7EB`; `Bz.Line2` `#D0D4DA`; `Bz.LineSoft` `#EEF0F3`; `Bz.Tone.Fail`/`Bz.High` `#C2331F`; `Bz.Tone.Pass`/`Bz.Ok` `#3DA570`; `Bz.Med`/`Bz.Tone.Warn` `#B26B00`; `Bz.Manual`/`Bz.Tone.Person` `#5B3FD0`; `Bz.Tone.Listed` `#C9CED6`; `Bz.Brand` (actions) `#15181D` ("ink only, like every action in Clear Sheet"); `Bz.Chrome*` → the white header; `Bz.Font.Sans` "Public Sans, Segoe UI"; `Bz.Font.Mono` "IBM Plex Mono, Consolas".

- [ ] **Step 1: Write the failing test**

```csharp
public class ClearSheetTokensTests
{
    [Theory]
    [InlineData("Bz.Surface", "#FFFFFF")]
    [InlineData("Bz.Text", "#15181D")]
    [InlineData("Bz.Tone.Fail", "#C2331F")]
    [InlineData("Bz.Tone.Pass", "#3DA570")]
    [InlineData("Bz.Tone.Person", "#5B3FD0")]
    public void Panel_tokens_are_Clear_Sheet(string key, string hex)
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "UI", "Jkr", "Panel", "PanelTokens.xaml"));
        Assert.Matches($"x:Key=\"{Regex.Escape(key)}\"[^>]*Color=\"{hex}\"", xaml);
    }
}
```

(`RepoRoot()` as in `PaneShellTests`; adjust the regex to how `PanelTokens.xaml` writes a brush.)

- [ ] **Step 2: Run it.** Expected: FAIL.
- [ ] **Step 3: Change the token values and add the fonts.**
- [ ] **Step 4: Run the full test command.** Expected: PASS (`XamlResourceScopeTests` included).
- [ ] **Step 5: Screenshots** `--shot-jkr C:/Bina/shots/tokens`. Expected: every screen is light; note screens that look broken for the next tasks.
- [ ] **Step 6: Commit** `style(jkr): Clear Sheet light tokens, Public Sans and IBM Plex Mono`

---

### Tasks 8–15: one screen per task

Each of these tasks has the same shape. **Files** and the **Clear Sheet screen** are given per task; the steps are:

- [ ] **Step 1:** Open the Clear Sheet screen(s) in the design file and the matching "before" screenshot.
- [ ] **Step 2:** If the screen model needs new properties or strings for copy Clear Sheet draws, write a failing model test for them first (the existing `*ScreenTests` show the pattern), run it (FAIL), add them to the model and `PanelStrings` (BM + EN), run it (PASS).
- [ ] **Step 3:** Rework the XAML to match the screen: layout, spacing, copy, states. Keep every binding and key handler the model still provides.
- [ ] **Step 4:** Run the full test command (PASS) and `--shot-jkr C:/Bina/shots/task-N`; compare with the design screen at 380 px and in BM.
- [ ] **Step 5:** Commit `style(jkr): <screen> follows Clear Sheet`.

| Task | Clear Sheet screen(s) | Files | Notes |
|---|---|---|---|
| 8 | JKR Compliance mark; edge states, refined | `JkrPaneShell.xaml(.cs)`, `PaneShellModel.cs`, `Resources/Icons/svg/JkrCompliance16.svg`, `JkrCompliance32.svg` | New stepped-tick logo (20 px pane, 32 px ribbon). "Waiting for a model", "Log in to BINA to run the audit" + Retry. Toast restyled (no Undo). |
| 9 | Set up the audit | `StartView.xaml`, `StartScreenModel.cs` | Item 15 kept: LOD as segmented buttons, "Required", no default; discipline preset from file name; project file row; "Borang sections · 61 checks" list. |
| 10 | Audit running; edge states, refined (run failed) | `RunView.xaml`, `RunScreenModel.cs` | Item 14 kept: progress bar + section list with status; "41 of 61 checks done", A–E strip, "Stopped in C", Retry / Back to start. |
| 11 | — (model only) | Create `UI/Jkr/Panel/Core/SpecAspects.cs`; modify `SpecChecklist.cs` / `ResultsScreenModel.cs`; add to `Tests/Tests.csproj`; test `Tests/SpecAspectsTests.cs` | `enum SpecAspect { Naming, Parameters, Quality, Geometry }`; `static SpecAspect SpecAspects.Of(string checkId)`; `ResultsScreenModel.ShowAspects : bool` with rows N / P / Q / G (letter, name, %, tick strip, fails) computed exactly like section rows. Tests: every `SpecCatalog` check maps to one aspect; the four rows' check counts sum to 61; toggling keeps the selection. The mapping is a **draft** in one table for Ashraf to confirm (spec Q1); tests must not pin 9 / 14 / 26 / 12. |
| 12 | Result; no failures; keputusan 320 px; section D | `ResultsView.xaml` (summary, sections/aspects list, checklist) | Head: verdict, "n spec checks fail", "18% compliant, 19 of 104 results pass. The % counts results, not checks.", "n checks wait for semak manual". Sections / Aspects segmented toggle (Task 11). Rows with tick strips (fail, pass, partly, manual, listed, n/a, not checked). "Other JKR rules · not in the Borang spec". Items 9–13 kept: section filter on row click, section pill, state tags with counts inside a section, inventory table, Show all. Footer: Reset, Export Borang. |
| 13 | Issues | `ResultsView.xaml` (issue list), `ResultsScreenModel.cs` | Tabs Open / Ignored / Resolved / Manual with counts; search; "0 of 99 resolved"; legend "4 high · 94 medium · 1 low"; "99 shown"; Ignore all / Fix all n. Row: Borang key, title, old → new in mono, meta line; Manual tag (Task 3); AI fixer proposal meta + spark-bolt mark (design option 1). |
| 14 | Issue with a fix; issue with an AI fixer proposal; semak manual issue | `DetailWindow.xaml(.cs)`, `DetailScreenModel.cs` | "Issue n of m open"; severity + "Borang A5 · section"; Element / Required / In model; Suggested fix as − / + lines; "You see every change again before anything is written."; one "Source: …" line (click opens the PDF, item 5); action bar Locate in 3D · Ignore (A) · Apply fix (F) / Apply AI fix (F). Item 4 kept: how-to-fix steps, fix-failed block. Item 3: Mark as Manual text button beside Ignore when `CanMarkManual`; Reopen replaces Ignore/Apply on Ignored or `MarkedManual` items. Semak manual: "Why the AI did not judge this", note on the % and the Borang, Mark as reviewed. Add model tests for `CanMarkManual` / `CanReopen` on the card. |
| 15 | Confirm changes; fix outcome | `JkrConfirmWindow.xaml(.cs)`, `JkrConfirmModel.cs`, `FixResultWindow.xaml(.cs)`, `FixResultModel.cs` | Confirm: title "Write these changes to the model?", groups "Ready-made fixes n" and "Proposed by the AI fixer n" (row flag `IsAiProposal`, test that rows split by it), 500+ elements warning, Cancel / "Write n changes". "On the way" line only if the AI fixer already returns proposals one by one (spec Q2); otherwise leave it out. Outcome: "n changes written, m not written", one-undo-step line, reasons grouped with Locate, "The m changes not written stay in Open.", Copy list for BIM manager, Done. |

---

### Task 16: Borang preview and export dialog (item 7)

**Files:** `UI/Jkr/Export/BorangPreviewWindow.xaml(.cs)`, `UI/Jkr/Modals/ExportWindow.xaml(.cs)`

- [ ] **Step 1:** Restyle the preview to the "Borang BIM010 preview" screen: header with file name and BM/EN, "Filled from the HOD's BIM010 template, sections A to E, unchanged", page counter, footer Excel (.xlsx) n issues · CSV (.csv) n issues · Export issues · Save PDF. The PDF page itself does not change.
- [ ] **Step 2:** "Export issues" opens the restyled `ExportWindow` (format Excel / CSV, include Open / Resolved); the Excel and CSV footer entries open it with that format selected. Save PDF saves directly.
- [ ] **Step 3:** Build, full test command (PASS), screenshot, commit `style(jkr): Borang preview and export dialog follow Clear Sheet`.

---

### Task 17: Final check

- [ ] **Step 1:** Full test command. Expected: PASS, no skipped JKR tests.
- [ ] **Step 2:** `--shot-jkr C:/Bina/shots/after` and compare every screen with the design in EN and BM, at 380 px and 320 px. List any difference in the handoff.
- [ ] **Step 3:** `git grep -nE "#1B1F26|#0F0F0F|Direction C" UI/Jkr UI/JkrComplianceDashboard*` returns only comments that are updated or removed.
- [ ] **Step 4:** Spec §5 walk-through: for each of the 16 items, name the commit that handles it.
- [ ] **Step 5:** Do not push. Report the branch `irfan/jkr-clear-sheet` and the open questions (Q1 aspect mapping for Ashraf; Q2 if not settled).
