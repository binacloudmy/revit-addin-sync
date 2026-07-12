# BINA Revit AI Copilot — Test Plan

**Version:** 1.0
**Last updated:** 15 May 2026
**Branches under test:**
- `revit-addin-sync@feat/copilot-saved-commands` (HEAD `fe5e213` or newer)
- `bina-ai@feat/copilot-prd` (HEAD `5345688` or newer)

This document walks a tester through every functional area of the Copilot in
the order it should be exercised. Every test has explicit pass / fail criteria
so a non-developer can run it. Pass means the actual behaviour matches the
**Expected** line word-for-word in spirit (not necessarily verbatim text).

> The Copilot's main function is **editing the Revit model from a natural-
> language prompt**. Sections 4 (Code Generation), 7 (8-Tool Architecture), and
> 8 (End-to-End Killer-Feature Scenarios) are the headline tests.

---

## 1. Prerequisites

| Item | Required |
|---|---|
| Revit 2026 (the addin targets `net8.0-windows`, `RevitVersion=2026`) | Yes |
| A loaded Revit project (recommended: the JKR `Copy of jkrAR24_5a...` model) | Yes |
| Backend reachable (`/api/revit-ai/health` returns `{"status":"ok"}`) | Yes |
| Active login (Login ribbon button → green "Connected" footer) | Yes |
| 40 seeded public commands loaded (visible in **Saved Commands** expander) | Yes |
| Internet connectivity (the chat path uses an LLM round-trip) | Yes |
| Roughly 30 minutes for the full pass | — |

## 2. Build + setup

### 2.1 Pull the latest

```bash
cd C:\developer\revit-addin-sync
git checkout feat/copilot-saved-commands
git pull
```

### 2.2 Build (Windows)

```bash
dotnet build RevitWebAppSync.csproj `
  -c Release `
  -p:TargetFramework=net8.0-windows `
  -p:RevitPath="C:\Program Files\Autodesk\Revit 2026" `
  -p:RevitVersion=2026 `
  -t:Restore,Build
```

Build should report `Build succeeded`. The output DLL is copied to
`%APPDATA%\Autodesk\Revit\Addins\2026\RevitWebAppSync.dll`.

### 2.3 Sanity check

1. Launch Revit 2026.
2. Open the JKR project.
3. Click the **AI Assistant** ribbon button.
4. Expected: the Copilot window opens, footer shows **"Connected"** in green,
   the Saved Commands expander contains **40** items.

If any of the above fails, **stop** — fix the build / backend before running
the rest of the tests.

---

## 3. Test sections at a glance

| # | Area | PRD section | Tests |
|---|---|---|---|
| 4 | Code Generation | 6.4 (FR-024–030) | T-040 to T-046 |
| 5 | @Mention System | 6.1 (FR-001–010) | T-010 to T-019 |
| 6 | Intent Router | 6.2 (FR-011–016) | T-020 to T-025 |
| 7 | 8-Tool Architecture | §3.2 + §6 | T-070 to T-077 |
| 8 | Error Explainer & Fixer | 6.3 (FR-017–023) | T-030 to T-036 |
| 9 | Chat Interface | 6.5 (FR-031–037) | T-050 to T-056 |
| 10 | Context Memory | §3.3 | T-060 to T-062 |
| 11 | Command Library | §1.2 differentiator | T-080 to T-090 |
| 12 | Direct model editing (headline) | §1.4, §3 | T-100 to T-107 |
| 13 | Regression & safety | cross-cutting | T-110 to T-115 |

---

## 4. Code Generation (PRD §6.4, FR-024–030)

### T-040 · Generate from a natural-language prompt (FR-024)

1. Type **`count all the walls in the active document`** → **Send**.
2. **Expected:** a code block appears with a `FilteredElementCollector` over
   `BuiltInCategory.OST_Walls`. The code is valid C# (no red squiggles in
   syntax).

### T-041 · Show before execute (FR-025) ⭐ critical

1. After T-040 the code block is visible.
2. **Expected:** the code has NOT auto-run. Two buttons are shown beneath it:
   **▶ Run** and **Discard**. Revit's model state is unchanged.

### T-042 · Discard path

1. Click **Discard**.
2. **Expected:** chat shows `[OK] Discarded — nothing was run.` Both buttons
   become disabled. No Revit transaction is created.

### T-043 · Execute and report (FR-027 + FR-029)

1. Re-send T-040 → click **Run**.
2. **Expected:** status footer reads "Executing in Revit...", then chat shows
   a green `[OK] Total number of walls: 1458` (number will vary by model).

### T-044 · Progress feedback (FR-028)

1. During step T-043 the footer text changed from "Executing..." to "Ready".
2. **Expected:** the user could see the in-flight state — never frozen with
   no indication of activity.

### T-045 · Transaction-based undo (FR-030)

1. Run a write — e.g. **`set comments on all walls to "QA pass"`** → Run.
2. After `[OK]`, press **Ctrl+Z**.
3. **Expected:** the comments revert. A single Undo step suffices because the
   addin wraps every Run in one named Transaction.

> Works whether the Copilot window or Revit has keyboard focus. The Copilot
> intercepts `Ctrl+Z` while its input is empty and routes it to Revit's Undo
> via the same channel the **↶ Revert last change** button uses. If the
> input has text in it, `Ctrl+Z` follows WPF default behaviour (undoes typing).

### T-046 · Compile-error self-heal

1. Send **`use the FooBarBaz API on every wall`** → Run.
2. **Expected:** code compile error → addin shows `[Warning] Auto-fixing
   (attempt 1/2)…` then a new code block is generated; chat eventually shows
   `[OK]` OR the error-explainer card (Section 8).

---

## 5. @Mention System (PRD §6.1, FR-001–010)

### T-010 · Trigger autocomplete on `@` (FR-001)

1. Click into the prompt input.
2. Type a single `@`.
3. **Expected:** popup opens within ~100 ms listing levels, views, grids,
   rooms, MEP systems, categories, and the special `@all_*` / `@here` /
   `@selected` items. No empty popup, no crash.

### T-011 · All 8 mention types resolve (FR-002)

For each prefix, type it and confirm matches appear:

| Type | Token to try | Pass criterion |
|---|---|---|
| Level | `@Aras` | Shows Aras Tanah / Aras 01 / Aras 02 etc. |
| View | `@Floor Plan` or `@SECTION` | Shows view names |
| Grid | `@A` | Shows grid names (A, B, 1, 2 etc.) |
| Room | `@1` (any room number) | Shows rooms |
| Type (family type) | `@Type_` | Lists family types if seeded |
| Category | `@Doors` | Shows the bulk category mention |
| MEP System | `@HVAC` or any system name | Shows MEP systems |
| Bulk | `@all_walls` | Single bulk match |

### T-012 · Shortcut mentions (FR-004)

1. Type `@selected` → Insert.
2. Type `@here` → Insert.
3. **Expected:** both insert as chips. `@selected` is the current Revit
   selection; `@here` is all elements in the active view.

### T-013 · Context-aware sort (FR-005) ⭐ check

1. Open a floor plan called **Aras 02** in Revit.
2. In the Copilot, type just `@`.
3. **Expected:** **Aras 02** is at or near the top of the popup (active view /
   active level promoted).

### T-014 · Recently-used (FR-006)

1. Insert `@Aras 02` once → close popup.
2. Open the popup again (`@` alone).
3. **Expected:** Aras 02 appears in the **first few** items.

### T-015 · Mention resolution to ElementIds (FR-007)

1. Send **`set comments on @all_walls to "X"`** → Send.
2. Look at the chat — the assistant reply or generated code references the
   resolved category via `BuiltInCategory.OST_Walls`.

### T-016 · Clickable badges (FR-008) ⭐ headline polish

1. Send **`show me @Aras 02`** (don't click Run on any code yet).
2. **Expected:** the user message bubble shows `@Aras 02` rendered as a blue
   pill-shaped chip (not plain text).

### T-017 · Click selects in Revit (FR-008 click action)

1. Click the chip from T-016.
2. **Expected:** Revit switches to the Aras 02 floor plan. Chat shows
   `[OK] Opened: Aras 02`.

### T-018 · Badge colour coding (FR-009)

In the user message bubble for `show me @Aras 02 and select @all_doors`:

| Type | Colour |
|---|---|
| Level | Blue |
| View | Purple |
| Grid | Orange |
| Room | Green |
| System | Pink |
| Category | Yellow |
| `@all_*` | Teal |
| `@here` / `@selected` | Gray |

### T-019 · Hover shows details (FR-010)

1. Hover (don't click) any chip in chat for ~1 s.
2. **Expected:** tooltip shows the type label, element id, and "Click to
   select in Revit".

---

## 6. Intent Router (PRD §6.2, FR-011–016)

### T-020 · 8-category classification (FR-011)

Send each prompt and verify the routed intent (visible in the assistant's
description line):

| Prompt | Expected intent |
|---|---|
| `count the doors` | QUERY |
| `set comments on all walls` | EDIT |
| `select all doors on @Aras 01` | SELECT |
| `show me Aras 02` | VIEW |
| `check this model for JKR compliance` | ANALYZE |
| `create a section at grid A` | CREATE |
| `export the doors to Excel` | EXPORT |
| `is 200mm thick enough for a partition wall?` | CHAT (no Revit operation) |

### T-021 · Parameter extraction (FR-012)

Send **`select all doors on Aras 01`**.

**Expected:** the action params include `{category: "doors", level: "Aras 01"}`.
(Visible in the code that gets generated — uses `BuiltInCategory.OST_Doors`
and `Element.LevelId` filter.)

### T-022 · Confidence scoring + clarification (FR-013 + FR-015)

1. Send **`fix this thing`** (deliberately vague).
2. **Expected:** Copilot asks a clarifying question instead of running
   anything. No code block is produced.

### T-023 · Multi-intent (FR-014)

1. Send **`show me Aras 01 and count the walls there`**.
2. **Expected:** Copilot performs both — opens the view AND returns a count.

### T-024 · Route to correct tool (FR-016)

- `show me @Aras 02` → ViewTool → view actually opens.
- `select all doors` → SelectTool → doors are selected in Revit.
- `check compliance` → AnalyzeTool → Compliance dashboard card appears.
- `export doors to Excel` → ExportTool → .xlsx file lands on Desktop.

### T-025 · CHAT intent answers from context, no Run button

1. Send `count all the walls` → run → result.
2. Send `count the doors too` → run → result.
3. Send **`which is more — walls or doors?`**.
4. **Expected:** plain answer in chat. **No Run button**, no code block.

---

## 7. 8-Tool Architecture (PRD §3.2)

### T-070 · ViewTool — open existing view

1. Send `show me Aras 01`.
2. **Expected:** code synthesised locally (no LLM wait) **and auto-runs**
   immediately — Revit opens the Aras 01 plan, chat shows
   `[OK] View opened: Aras 01 (01w_WIP)`. No Run click needed.

> Opening a view is read-only navigation (no model edit), so the addin
> auto-executes the synthesised code — matches the behaviour of clicking
> an `@<view>` chip directly. Every other intent (CREATE / EDIT / EXPORT /
> SELECT via code-gen, etc.) still gates behind Run/Discard per FR-025.

### T-071 · SelectTool — native dispatch (no LLM round-trip)

1. Send `select all doors`.
2. **Expected:** code block appears within ~1 s (faster than a regular EDIT
   because the dispatcher skips the LLM call). Run → doors are highlighted.

### T-072 · SelectTool — category + level

1. Send `select all doors on Aras 01`.
2. **Expected:** same as T-071 but filtered. Result: `[OK] Selected: 60
   doors on Aras 01` (number from your model).

### T-073 · CreateTool — new 3D view

1. Send `create a new 3D isometric view called Demo 3D`.
2. **Expected:** code uses `View3D.CreateIsometric`, Run creates the view,
   Revit switches to it, the view appears in the Project Browser.

### T-074 · EditTool — bulk parameter update

1. Send `set comments on all walls to "QA-Reviewed"`.
2. **Expected:** code uses `BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS`.
   Run produces the **three-bucket report**: `Updated N. Skipped M in groups.
   Skipped K with a read-only parameter.` All three numbers add up to the
   total wall count.

### T-075 · AnalyzeTool — compliance

1. Send `check this model for JKR compliance`.
2. **Expected:** the chat shows a dashboard card "✓ Compliance check" with an
   **Open Compliance dashboard** button. Click → the JKR Compliance dockable
   panel opens.

### T-076 · AnalyzeTool — cost

1. Send `estimate the cost of the walls`.
2. **Expected:** dashboard card "💰 Cost estimate" with **Open Cost
   dashboard** button. Click → Cost dashboard opens.

### T-077 · ExportTool — Excel

1. Send `export the doors to Excel`.
2. **Expected:** code block is instant (native dispatch), Run → an `.xlsx`
   file appears on the **Desktop** (default `door_schedule.xlsx`). Open it —
   columns: Id, Name, Family, Type, Level.

> **GenerateTool** (FR for "generate 3 layouts") is deferred this cycle. If
> tested, the chat will reply *"Design-variation generation isn't available
> yet."* — that's the documented behaviour, not a bug.

---

## 8. Error Explainer & Fixer (PRD §6.3, FR-017–023)

### T-030 · Catch all execution errors (FR-017)

1. Send a request that produces invalid Revit code, e.g.
   **`change the Volume of every wall to 100`** (Volume is read-only).
2. **Expected:** the addin doesn't crash. Either auto-fix kicks in or you
   get a clean `[OK]` because the agent's defensive code skipped read-only
   parameters.

### T-031 · Plain-English translation (FR-018) ⭐

1. Send **`delete every wall in the active view`** → Run → auto-fix → fail.
2. **Expected:** a **red-tinted card "⚠ That didn't work"** appears with a
   plain-English sentence (not a stack trace).

### T-032 · Root-cause analysis (FR-019)

In the same card from T-031, look for a "**Why:**" line directly below the
explanation. It should describe the actual reason (constraints, pinning,
groups, etc.) — not just restate the error.

### T-033 · Fix options ≥ 2-3 (FR-020)

The card should show **2 or 3** clickable fix buttons, with one marked **★**
(recommended).

### T-034 · One-click auto-fix (FR-021)

1. Click the **★** code-fix.
2. **Expected:** chat shows `[Warning] Regenerating the code…`, then a new
   code block with Run/Discard. Click Run → operation succeeds OR a new
   error card appears (the system never silently does nothing).

### T-035 · Error-pattern learning (FR-022)

1. Trigger the same delete-walls error twice in the same session.
2. **Expected:** on the second occurrence, the card's explanation references
   the prior working fix — *"Last time this worked by ..."* style wording.
   (Subtle — confirms the backend's `error_patterns` lookup is feeding back.)

### T-036 · Undo support (FR-023) ⭐

1. After any successful model-changing run (e.g. T-074), an **`↶ Revert
   last change`** button appears in the chat.
2. Click it.
3. **Expected:** status reads "Reverting…", then Revit performs an Undo on
   the named "AI Assistant" transaction. Chat shows
   `[OK] Reverted the last change.`

---

## 9. Chat Interface (PRD §6.5, FR-031–037)

### T-050 · Split-view layout (FR-031) ⭐ P0

1. Open the Copilot.
2. **Expected:** window is **780 wide** by default, with chat on the left and
   the **Preview** panel on the right. A thin vertical splitter sits between
   them; dragging it resizes both sides.

### T-051 · Toggle preview

1. Click the **×** on the Preview header.
2. **Expected:** Preview collapses, chat takes full width, a small **`◀
   Preview`** floating button appears at top-right of chat.
3. Click `◀ Preview`.
4. **Expected:** Preview re-opens at its previous width.

### T-052 · Rich message rendering (FR-032)

After running a few prompts, the chat should show: code blocks (collapsible),
dashboard cards (blue button), error cards (red), suggestion buttons (small
ghost buttons), **and** mention chips (coloured pills in user messages).

### T-053 · Collapsible code (FR-033)

1. Click `▾ code` above any code block.
2. **Expected:** the code text hides; toggle changes to `▸ code (hidden)`.
   Click again → re-shows.

### T-054 · Result visualisation — DataGrid (FR-034) ⭐

1. Open Saved Commands → run **`Count elements per level`** with
   category=doors.
2. **Expected:** result renders as a **sortable DataGrid** in both chat and
   preview. Click the "Door Count" column header → rows reorder. The same
   grid (or its summary) appears in the preview panel.

### T-055 · Quick action buttons (FR-035)

1. After any successful run, look beneath the result.
2. **Expected:** one or more suggestion buttons (Run again, Export results,
   View in 3D, etc., depending on intent).

### T-056 · Copy transcript (FR-037)

1. Click **Copy transcript** in the footer.
2. **Expected:** chat shows `[OK] Transcript copied to the clipboard.`
   **No** `[Error] Couldn't copy: OpenClipboard Failed` line. Paste into
   Notepad → full conversation appears.

---

## 10. Context Memory (PRD §3.3)

### T-060 · Pronoun resolution — "those"

1. Send `count the rooms on Aras 01` → run.
2. Send `export those to Excel`.
3. **Expected:** the export references the same rooms (not all rooms).

### T-061 · Re-reference last result

1. After T-060 send `what was that count again?`.
2. **Expected:** Copilot answers from the conversation, not a fresh query.

### T-062 · Chained operations

1. Send `count all the walls`.
2. Send `count the doors too`.
3. Send `which is more — walls or doors?`.
4. **Expected:** as in T-025 — direct text answer, no Run button.

---

## 11. Command Library (PRD §1.2 differentiator)

### T-080 · Public seed catalog

Open **Saved Commands**.

**Expected:** 40 entries spanning categories (QA, Selection, Rooms, etc.).
Each row's second line shows `<Category>  ·  Public` in blue. No ⚡ marker.

### T-081 · Search / filter

Type `door` into the search box.

**Expected:** the list filters to commands whose name / category /
description contains "door".

### T-082 · Run a public command

1. Double-click **`Count elements per level`**.
2. **Expected:** variable dialog opens → set `category = doors` → OK → AI
   generates code → Run → DataGrid result.

### T-083 · Save prompt as a command

1. Type any prompt into the input.
2. Click **Save prompt** in the toolbar.
3. Dialog opens — pre-filled with your prompt. Fill the Name field → Save.
4. **Expected:** new entry appears in the list with `… · Mine` badge in
   blue. No ⚡ marker yet.

### T-084 · Save run as a command (snapshot ⚡)

1. After any successful run, click **💾 Save this run as a command** in
   chat.
2. Same dialog opens — pre-filled with prompt **and** code snapshot. Save.
3. **Expected:** new entry appears with a small **⚡** after the name.

### T-085 · Snapshot re-run skips the LLM

1. Double-click the ⚡ command from T-084.
2. **Expected:** chat says *"Running saved snapshot of <name> (skipping
   AI)."* — code block appears **instantly** (no LLM wait) with Run /
   Discard. Click Run → same result as before.

### T-086 · Auto-backfill on first run

1. Save a prompt-only command (no ⚡) via T-083.
2. Double-click it → AI generates code → Run → success.
3. Re-open Saved Commands.
4. **Expected:** that command now has ⚡ (the addin silently PUT'd the
   working code).

### T-087 · Export

1. Click **Export** in the Saved Commands toolbar → save the .json file.
2. **Expected:** the file contains a `version: 1` bundle with every command
   visible to the user, including `generated_code` for ⚡ entries.

### T-088 · Import (idempotent)

1. Click **Import** → pick the same file.
2. **Expected:** hint shows `Imported 0, skipped N, out of N` because every
   command already exists by name.

### T-089 · Edit / Delete own command

1. Right-click your own ⚡ command → Edit → change description → Save.
2. **Expected:** list refreshes with the new description.
3. Right-click → Delete → confirm.
4. **Expected:** command removed.

### T-090 · Team scope

1. Click **Login** ribbon → set **Team ID** (any positive integer) → Save.
2. Open Copilot → save a new command from a run.
3. **Expected:** the **My team** radio is now enabled in the save dialog
   (previously disabled).
4. Save with **My team** → list shows the new command with `… · Team`
   badge.

---

## 12. Direct model editing (headline) ⭐

These are the SV's main acceptance scenarios. Each should succeed end-to-end
on the actual model.

### T-100 · Edit by prompt (the headline)

1. Send **`set comments on all walls to "Reviewed-15May2026"`** → Run.
2. **Expected:** three-bucket result line. Open any wall's Properties →
   **Comments** field reads "Reviewed-15May2026".
3. Click **↶ Revert last change**.
4. **Expected:** Comments reverts.

### T-101 · Add a single-flush door

> Prerequisite: a family containing "Single-Flush" is loaded in the project.

1. Send **`add a single-flush door to the first wall on Aras 01`** → Run.
2. **Expected:** new door appears on a wall on Aras 01. Chat shows the
   placement. If the family isn't loaded, the chat shows `[OK] No
   single-flush door type found` (graceful failure).

### T-102 · Add a column at a grid intersection

> Prerequisite: a structural column family is loaded; grids exist.

1. Send **`add a column at the intersection of grid A and grid 1, on Aras
   01`** → Run.
2. **Expected:** new column at the intersection. If no column family is
   loaded → graceful error, not a crash.

### T-103 · Create a new 3D isometric view

1. Send **`create a new 3D isometric view called Demo 3D`** → Run.
2. **Expected:** new view "Demo 3D" appears in the Project Browser; Revit
   switches to it.

### T-104 · Create a section at a grid

1. Send **`create a section view that cuts through Aras 01 along grid A`**
   → Run.
2. **Expected:** new section view created; Revit opens it.

### T-105 · Bulk rename / re-mark

1. Send **`set the Mark of every wall to W-{Index}`** (or similar) → Run.
2. **Expected:** walls get sequential Marks. Revert undoes.

### T-106 · Tag everything in active view

1. Open a floor plan with rooms.
2. Send **`tag every room in the active view`** → Run.
3. **Expected:** room tags appear on each room.

### T-107 · Open / navigate existing 3D view

1. Send **`show me the default 3D view`**.
2. **Expected:** Revit switches to the {3D} view.

---

## 13. Regression & safety

### T-110 · Group-member skip (no warning modal)

1. Send `set comments on all walls` on a model with group-walls (the JKR
   model has ~352 group walls).
2. **Expected:** **No** Revit warning dialog interrupts the run. Result
   shows `Skipped N in groups`.

### T-111 · Read-only parameter skip (third bucket)

Following T-074 / T-100, verify the success message lists three numbers
(Updated / Skipped in groups / Skipped read-only) and they add to the total
wall count.

### T-112 · Auto-Transaction wrap

Run any write that doesn't explicitly use a Transaction. Revit's Undo stack
should show one new entry called **"AI Assistant"** after the run.

### T-113 · OpenView outside a Transaction

Send `show me @<view>` for any view. **Expected:** no
"Cannot change the active view of a modifiable document" error.

### T-114 · Cancel button mid-flight

1. Send a long-running query.
2. Immediately click **Cancel** (red button next to Send).
3. **Expected:** the round-trip cancels gracefully, status returns to
   "Ready".

### T-115 · Login gate

1. Logout via the Login ribbon.
2. Open the Copilot.
3. **Expected:** the input is disabled with a hint that login is required.
4. Log back in.
5. **Expected:** input re-enables.

---

## 14. Pass / fail summary

Aggregate at the end:

| Section | Tests | Pass | Fail | Notes |
|---|---|---|---|---|
| 4 · Code Generation | 7 | / | / | |
| 5 · @Mention | 10 | / | / | |
| 6 · Intent Router | 6 | / | / | |
| 7 · 8-Tool | 8 | / | / | |
| 8 · Error Explainer | 7 | / | / | |
| 9 · Chat Interface | 7 | / | / | |
| 10 · Context Memory | 3 | / | / | |
| 11 · Command Library | 11 | / | / | |
| 12 · Direct editing | 8 | / | / | |
| 13 · Regression | 6 | / | / | |
| **TOTAL** | **73** | / | / | |

**Acceptance criterion (per the PRD):** all P0 tests must pass; P1 tests
should pass except where flagged as deferred; P2 cosmetic gaps are
acceptable.

---

## 15. Reporting failures

For any failing test, attach:

1. **Test ID** (T-NNN) and one-line title.
2. **Screenshot** of the Copilot at the moment of failure (chat history
   visible).
3. **Screenshot** of the Revit journal's last few lines if it's a Revit-side
   issue (`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2026\Journals\
   journal.NNNN.txt`).
4. The **prompt** you typed (verbatim).
5. The **generated code block** (open the `▸ raw error` toggle if it's an
   error card).
6. Your **Revit version** and the **addin commit hash**:
   ```bash
   cd C:\developer\revit-addin-sync
   git rev-parse --short HEAD
   ```

File issues against the `feat/copilot-saved-commands` branch (or merge
target).

---

## 16. Known limitations (not bugs)

These are documented PRD limitations, not failures:

- **FR-026 (real syntax highlighting in code blocks)** — deferred, code is
  monospace blue text.
- **GenerateTool** (`generate 3 layouts for the lobby`) — replies "not
  available yet" by design. This is the next-cycle feature.
- **Cross-model snapshot ⚡ portability** — saved code that references
  element IDs / view names / specific family types is tied to the project
  that created it. Save **prompt only** (no ⚡) for shareable cross-project
  commands.
- **Family symbols must be loaded** — a CREATE prompt for a door type that
  doesn't exist in the project gets a graceful `[OK] No <X> found` message,
  not a successful door placement.
- **The Cancel button cancels the round-trip, not an in-flight Run** — once
  the code is executing in Revit, only Revit can abort it.

---

## 17. Quick smoke test (~5 minutes)

For a fast pre-demo check, run these eight in order:

1. **T-050** — split-view layout shows on open.
2. **T-016** — `show me @Aras 02` renders a blue chip.
3. **T-017** — clicking the chip opens the view.
4. **T-025** — walls/doors comparison answers from context (no Run button).
5. **T-100** — `set comments on all walls to "QA"` → three-bucket result.
6. **T-036** — `↶ Revert last change` undoes it.
7. **T-085** — running a ⚡ snapshot is instant (no LLM wait).
8. **T-031** — `delete every wall in the active view` produces the red
   error-explainer card.

If all eight pass, the demo path is solid.

---

## 18. OSS tool port — Windows smoke (feat/oss-tool-port)

Build the addin, open a test model with levels + a beam family + duct/pipe types, then via the engine or pane run each and check the result in Revit. Every mutate must return new_ids and the elements must be SELECTED/visible when done.

Happy path / failure case per tool:

- [ ] list_phases / list_design_options / list_rvt_links / list_revisions / list_model_groups — each returns ok + plausible counts (failure: none — read-only)
- [ ] get_sheet_viewports (a real sheet_number) / (failure: bogus sheet_number → clear error)
- [ ] list_project_parameters / get_type_parameters (a wall type id) / (failure: bad element_id)
- [ ] list_rooms — area_m2 matches Revit room schedule to 0.01 (failure: none)
- [ ] filter_elements category=Walls visible_in_current_view=true / bbox query around a known wall (failure: no criteria → clear error)
- [ ] create_beam_system 6000x8000mm bay, spacing_mm=1500 — beams appear, actual_spacing_mm ≈1500 (failure: bogus beam_type_name)
- [ ] create_beam between two grid intersections (failure: bogus level)
- [ ] create_duct + create_pipe 3000mm straight run (failure: no MEP types loaded → clear error)
- [ ] create_roof over a 4-point rectangle — flat roof on level (failure: 2-point boundary)
- [ ] create_dimensions on 3 parallel walls, direction perpendicular — dim string appears (failure: 1 element id)
- [ ] create_dimensions in a SECTION view — expect wrong/degenerate result (known plan-view-only scope; verify the T13 docstring warns about it)
- [ ] create_point_element category=Doors → routes to place_door (check McpCallLog shows both names)
- [ ] create_line_element category=Walls / create_surface_element category=Floors
- [ ] store_data key=test → query_data key=test roundtrip; second doc gets its own store
- [ ] store_data with two UNSAVED docs open (both "Project X") — check %APPDATA%/BINA/scratch/ for hash collision (known limitation)
- [ ] UNITS: create_wall with height_mm=3000 → wall is 3000mm; legacy height_ft=10 still works; place_family_instance with legacy x/y/z (feet) still places correctly

Beat-Revit-AI iteration additions:
- [ ] find_elements_by_parameter Tinggi_Siling < 3.0 → returns the 2.6m rooms AND the 0.30m outlier (display-units compare)
- [ ] get_element_parameters on a room → length params carry value_mm + display_value
- [ ] audit_parameters category=Doors group=Data → fill matrix matches manual spot-check; partial_by_type appears for mixed params
- [ ] filter_elements/find_elements_by_filter on a category with >50 elements → matches beyond #50 found (predicate-before-cap)
- [ ] get_project_base_point with architect link loaded → host PBP + link offset in mm
- [ ] check_grid_alignment → per-grid delta_mm sensible; unlink model → clear error
- [ ] UAT REPLAY: the 6 prompts from "REVIT CO PILOT.docx" — clearance answer lists 7 rooms; door audit covers custom jkr params; room list 63/63; no "(N dipapar)"; no internal names

## 19. Pane UX upgrades (feat/oss-tool-port + feat/copilot-engine)
- [ ] Streaming: long Malay prompt — spinner replaced by growing text at first delta; last word completes
- [ ] Trail live: multi-tool prompt — rows tick ✓ with elapsed times while running
- [ ] Trail collapse: after the answer, pill "✓ N langkah · Xs ▸" — tap expands/collapses; survives scroll
- [ ] Tindakan buttons: audit prompt → [Ya, teruskan][Tidak] under the answer; Ya sends the offer + runs act-and-verify; buttons vanish after tap; older messages show no buttons
- [ ] No tindakan → no buttons (plain reply unchanged)
- [ ] Clickable ids: audit table → id underlined; click selects + zooms the element in Revit; non-id numbers (areas, counts) NOT clickable
- [ ] Regression: copy/select text in replies still works; user bubbles unchanged; history tab renders old messages

## 20. Colocate deployment (engine bundle + supervisor)
- [ ] Bundle: `pwsh scripts/build-engine-bundle.ps1 -Version 0.9.0 -Smoke` → smoke OK
- [ ] Cold spawn: place bundle under %LocalAppData%\Bina\RevitSync\engine\0.9.0\, start Revit, open pane → engine healthy, turn works
- [ ] Attach: second Revit instance → no second engine process
- [ ] Crash respawn: kill python.exe → engine back within ~25s; kill 3× fast → pane error state + log link
- [ ] Version gate: set min_addin_version above the addin version → error banner, no spawn
- [ ] Login token: login in addin → config.json gains DeviceToken; engine env has BINA_ENGINE_TOKEN (Process Explorer)
- [ ] Poison-pill: set DEEPSEEK_API_KEY + GatewayUrl → engine refuses, log names the key
- [ ] Update channel: feed with EngineVersion/EngineUrl/EngineSha256 → new dir appears; corrupt sha → rejected, current kept
- [ ] Installer: build with -EngineZip → fresh machine install → cold-spawn checklist passes
- [ ] Signed build: build-installer.ps1 -SignCert <pfx> -SignPassword <pw> — signtool quoting survives ISCC (the /Sbinasign mechanism); installer + uninstaller both signed (signtool verify /pa)
- [ ] OTA feed: version.json with flat engineVersion/engineUrl/engineSha256 fields (NOT the old nested {engine:{...}} shape)
