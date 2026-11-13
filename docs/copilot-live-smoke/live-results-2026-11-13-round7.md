# Live test round 7, 13 Nov 2026 (binacloudpc)

Three parts: the tagging baseline (B), the card retest on sheets (C, with C6 on a new add-in build), and the tagging "after" run (D, stopped early on the brief author's instruction). One run per prompt: nothing here is a rate. "Consistent with fixed" means one run behaved as the fix intends.

## Header

| | |
|---|---|
| PC | binacloudpc (Windows, 3840×2160 at 200 %), driven from the Mac through Windows-MCP on port 48820 and SSH (both worked; Windows-MCP sometimes reported `USER_CONTROL` for a few seconds, see "Anything new") |
| Revit | 2027.3 (Education) |
| Add-in, parts B, C1–C5, D | Debug **0.0.101**, commit `73c90db` merged into this branch (`80c5ee4`), `0.0.101+80c5ee48`. Loaded through `BINA_SYNC_PLUGIN_DIR` (scheduled task `BinaLiveTestR7` → `C:\Users\Public\rel-test\r7\open.cmd`); loader log "loaded 0.0.101.0" before every run, pane menu "Version 0.0.101" |
| Add-in, C6 | Debug **0.0.102**, commit `be3e1da` exactly (built in a temporary worktree, now removed), `0.0.102+be3e1da2`. Loader "loaded 0.0.102.0", pane menu "Version 0.0.102" |
| Backend, B and C | local bina-ai `784395b6` (old tagging playbook), Mac `127.0.0.1:8005`, reverse tunnel to the PC's port 18007 |
| Backend, D | local bina-ai `7ab9dfbb` (`tmp/tags`), same port |
| Login | the add-in config's token belonged to the other backend (port 18002); the first t01 attempt showed "SIGN IN REQUIRED". A local test token (user 9001) was minted from the backend and written into the config (not typed into any window); t01 was then rerun. The void attempt is kept under `runs/R7/void` |
| Exports | every turn's `ai.copilot_turns` row and all its `ai.copilot_turn_events` rows: `<id>.events.json`, summary `<id>.json`, screenshots, filter counts, key log and notes, in `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/c39d60b4-a317-49f4-be44-f4091b39cd62/scratchpad/runs/R7/` (shots in `runs/R7/shots/`) |

### Fixture

`C:\Users\Public\rel-test\sheets-test\sheets-test.rvt` for every run, reopened without saving before each (new process id checked, file time `2026-11-11T16:26:10` unchanged throughout). **No test copy was made and no model was saved**: the two plans used for tagging already had no door or window tags, so `tags-test.rvt` was not needed.

Counted in Revit (zoom to fit, window-select the whole view, Filter dialog), not through the Copilot:

| Placeholder | Value |
|---|---|
| `<Plan A>` | `Aras Tanah (01w_WIP)`: **D = 58** doors, **W = 42** windows, 0 door tags, 0 window tags |
| `<Plan B>` | `Pelan Tapak`: **DB = 58** doors, 0 door tags (same level as Plan A, so the same 58 doors) |
| `<3D View>` | `{3D}` |
| `<Level Without Floor Plan>` | `Aras Pentas` |
| Door tag types | family `jkrAR18t_dor_(t193)-3 Door Tag`: `(t193a) sc1-100 tx5mm r7mm KKP`, `(t193b) sc1-100 tx2.5mm r3.5mm KKP`, `(t193c) sc1-100 tx1.8mm r2.5mm KKP` (default, per the Tag All dialog) |
| `<Door Tag Type B>` | `(t193a) sc1-100 tx5mm r7mm KKP` (t08 is applicable) |
| Window tag types | `jkrAR18t_wdw_(t673) Hexagon`: `(t673a)-3 Hexagon 1.8mm` (default), `(t673b)` 2.5mm, `(t673c)` 3mm |

Because three door tag types are loaded, a tag-type question in t01, t02, t04, t07 (first message) and t10 is **needed**; it was answered with the default `(t193c)` (windows: `(t673a)`).

## Part B: tagging baseline (backend `784395b6`, add-in 0.0.101)

| Prompt | Right or wrong | If wrong, the cause | Playbook named | Own code or stored script | Tags expected / made | Wrong view tagged | Doubled tags | Questions (topic, needed or not) | Wrote before the answer | Failed tool calls | Model steps | Seconds | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| t01 | Right | | `tag-all-in-view` | own code | 58 / 58 | no | no | tag type (needed) | no | 1 | 6 | 38 (15 + 23 waiting) | The model first called `run_revit_code` with `playbook: tag-all-in-view`: "no runnable playbook 'tag-all-in-view'; runnable: sheet-set". Then 33 lines of its own code. Options were "Default door tag type (no type specified)" plus the 3 types. Plan B untouched (0 tags) |
| t02 | Right | | `tag-all-in-view` | own code | 58 / 58 | no | no | tag type, in English (needed) | no | 1 | 6 | 40 (17 + 23) | Same failed playbook call. Reply and card in Malay |
| t03 | Right | | `tag-all-in-view` | own code | 42 / 42 | no | no | window tag type (needed) | no | 2 | 6 | 39 (16 + 23) | 0 door tags. Failed: the playbook call; `check` doc-hygiene with scope Windows ("No rule in doc-hygiene is about 'Windows'") |
| t04 | Right | | `tag-all-in-view` (looked up first) | own code | 58 / 58 in Plan A | no | no | one form, two fields: which plan (needed) and tag type (needed): "The active view is the 3D view, so doors must be tagged in a plan. Which plan, and with which door tag type?" | no (only reads) | 1 | 9 | 142 (78 + 64) | The plan list offered 4 of the 5 floor plans (Aras Bumbung missing). Failed: `find_elements` with an unknown `source` argument. `check` DH-11 then ran on the active `{3D}` view: the card shows an orange "DH-11 62 fail" with a note that it was not verified for the plan and a "Check DH-11 in the plan" button |
| t05 | Right | | `tag-all-in-view` | none | 0 / 0 | no | no | create or skip, in English (needed); also offered "Tag doors in another level's plan instead (tell me which)" | no | 0 | 4 | 29 (6 + 23) | Answered Skip. Reply in Malay as plain text (no `report` call). Nothing changed (filter total = baseline) |
| t06 | **Wrong** | bad code (the turn failed) | `tag-all-in-view` | none | 58 / 0 | no | no | none | no | 2 | 3 | 6 | Reads only; it repeated a `find_elements` on the active view → "Same call with the same arguments as before: not run again.", then error "The AI model wrote malformed output twice; send it again." Turn status failed. The pane then showed a green **"Done"** chip above that error line and offered "Save as command". Nothing made; Plan B 0 tags. Not retried |
| t07 | **Wrong** | wrong understanding | `tag-all-in-view` | none (2nd message) | 0 new / 0 new | no | **no** (still 58) | none | no | 0 | 1 | 2 | First message: tag-type question, 58 tagged. Second message: one step, no read; the model called `report` straight away: "Tagged all 58 untagged doors in Aras Tanah (01w_WIP) with (t193c) ...; doc-hygiene DH-11 passes 58 of 58." It claims it tagged again and never says the doors were already tagged |
| t08 | Right | | `tag-all-in-view` | own code | 58 / 58, all `(t193a)` | no | no | none | no | 0 | 5 | 15 | Checked in Revit: Door Tags (58), Properties type `(t193a) sc1-100 tx5mm r7mm KKP`. Note: its code silently falls back to the default type when the name is not found |
| t09 | Right | | `tag-all-in-view` | none | 0 / 0 | no | no | "There is no Door Tag type named ZZ9-NOTAG in this model — only three ... types exist. Which tag type should the 58 doors in this view get?" (needed) | no | 1 | 4 | 52 (8 + 44) | Options: the 3 types and "Create a new Door Tag type ZZ9-NOTAG first"; no skip option, so `skip` was typed in the message box. Reply "Skipped — no tags placed ...". Failed: `find_elements` for the type (the probe). Nothing changed |
| t10 | Right | | `tag-all-in-view` (looked up first) | own code | 58 / 58 doors only | no | no | category (needed); tag type (needed) | no | 0 | 9 | 108 (23 + 85) | Category options: Doors 58, Windows 42, Plumbing Fixtures 78, Doors and Windows. 0 window tags afterwards |

### Summary the tags page asks for

- **8 of 10 right.** Wrong: t06 (the turn failed: malformed model output after a duplicate-call rejection), t07 (wrong understanding: claimed to tag again instead of saying all were already tagged).
- **Questions:** 8 asked (t04 counted once, as one form with two topics), 0 not needed. None was in Malay.
- **Wrong view: 0. Doubled tags: 0. Wrote before an answer: 0.**
- **Failed tool calls: 8** — `run_revit_code` 3 (the playbook is not runnable on this backend), `find_elements` 3 (unknown `source` argument; a repeated identical call; a type that does not exist), `check` 1 (no doc-hygiene rule for Windows), `model_overview` 1 (not run because the turn stopped).
- **Average model steps 5.3. Average seconds 47** (18.6 working; the rest is the time I took to answer).

## Part C: card retest (sheets, backend `784395b6`)

### Results

| Run | Build | Prompt | Result | Sheets made | Questions | Strip above a card | Reply shown | Failed calls | Steps |
|---|---|---|---|---|---|---|---|---|---|
| C1 | 0.0.101 | s01 | **Right** | 5: A-101 Pelan Tapak, A-102 Aras 01, A-103 Aras Pentas (new floor plan), A-104 Aras 02, A-105 Aras Bumbung | one card (backend, after the script); no second question | none | "Made 5 sheets A-101 to A-105 on (i205a) A1 Landscape, skipping Aras Langkan." | 0 | 8 |
| C2 | 0.0.101 | s01 | **Right** | 5, as C1 | one card | none | "Made 5 sheets A-101 to A-105, one per level, with the (i205a) A1 Landscape title block." | 0 | 7 |
| C3 | 0.0.101 | s09 (Malay) | **Right** | 4: A-101 Pelan Tapak, A-102 Aras 01, A-103 Aras 02, A-104 Aras Bumbung | levels card, then decisions card (both from the backend, English) | none, before either card or at the end | exactly: "Empat sheet A1 dibuat: A-101 Pelan Tapak, A-102 Aras 01, A-103 Aras 02, A-104 Aras Bumbung dengan title block (i205a) A1 Landscape." (the card headline cuts it to three lines ending "A1 L…") | 0 | 7 |
| C4 | 0.0.101 | s01, short window | **Right** | 5, as C1 | one card | none | "Made 5 sheets A-101 to A-105 in (i205a) A1 Landscape, one per level with a plan; Aras Langkan skipped as asked." | 0 | 8 |
| C5 | 0.0.101 | s10 (Malay) | **Right** | 1: A-101 holds a new reflected ceiling plan "Aras Bumbung (07d_Dokumen)" | one card; no second question | none | **"1 Views dan 1 Sheets dicipta."** — the fallback replaced the model's report "Sheet A-101 untuk Aras Bumbung dengan title block (i205a) A1 Landscape telah dibuat, bersama ceiling plan Aras Bumbung (07d_Dokumen) skala 1:100." | 0 | 8 |
| C6a | 0.0.102 | s01 | as expected (not answered) | — | card waiting | none | — | 0 | — |
| C6b | 0.0.102 | s01 | **Right** | 5, as C1 | one card | none | "Made 5 sheets A-101 to A-105 with title block (i205a) A1 Landscape, including a new Floor Plan for Aras Pentas." (report continued "; Aras Langkan skipped as agreed.": cut at the semicolon) | 1 (compile error in the model's view code, fixed next call) | 8 |
| C6c | 0.0.102 | s01 | as expected (not answered) | — | card waiting | none | — | 0 | — |

Result cards and toast (C3 counts as asked): the card rows were "lain-lain 24, Viewports 12, GraphicsStyle 4, Schedule Graphics 4, Sheets 4, SketchPlane 4, Title Blocks 4" for 4 sheets; C1 "other 33, Viewports 16, SketchPlane 7, GraphicsStyle 5, Schedule Graphics 5, Sheets 5, …"; C5 "lain-lain 9, Viewports 4, SketchPlane 2, Sun Path 2, GraphicsStyle 1, Schedule Graphics 1, Sheets 1, Title Blocks 1, Views 1, Work Plane Grid 1". **No floating toast was seen** in any end screenshot (taken about 3 s after the turn ended), so its count could not be recorded.

### Key-by-key record

The s01 decisions card arrived **scrolled to its own bottom** every time (title off-screen); I scrolled up with the mouse wheel before clicking (not a click). Revit's status bar read "Ready" (or a hover hint) after every key: no Revit command started in any C run.

**C1** (0.0.101). Click on the empty space right of the title "Before I make the sheets": no pane scroll, no visible ring. `2` → Pelan Tapak picked, "CHOOSE A PLAN done", outline moves to the Aras Pentas row, "4 sheets so far". `c` → Pentas "Create plan", outline to Aras Langkan, "5 sheets so far". `s` → Langkan Skip, "NO FLOOR PLAN done", "5 sheets". `Enter` → sent ("YOUR ANSWERS: Aras Tanah Pelan Tapak / Aras Pentas Create plan / Aras Langkan Skipped"). No pane scroll on any key; the message box stayed empty.

**C2** (0.0.101). Spot 1, the "CHOOSE A PLAN" heading right of "1 left": click → no ring; the first `Down` was not delivered (Windows-MCP `CONTROL_PREEMPTED`), so it was redone: click → `Down` moved the outline Aras Tanah → Aras Pentas row, no pane scroll. Spot 2, right of the Aras Pentas name: click → `Down` → outline Aras Langkan, no scroll. Spot 3, the "WHAT YOU WILL GET" heading: click → `Down` → outline wrapped to the Aras Tanah group, no scroll. Click on the Pelan Tapak tile → **picked on the first click, the pane did not move**. Click "Create plan" on Pentas, then "Skip" on Langkan → each acted on the first click, no movement. The "Make 5 sheets" button was below the fold: wheel down, one click → sent.

**C3** (0.0.101). Levels card ("Before I make the sheets / Which levels should get a sheet?", All levels [1] and 6 levels [2–7]): click on the title → no scroll; `1` → All levels picked, outline round the list, button "Continue"; `Enter` → sent. Decisions card: `2` pressed **without clicking first** → nothing (card unchanged, message box empty, Revit "Ready"): the focus did not move to the new card. Click on its title → `2` Pelan Tapak, `s` Pentas Skip, `s` Langkan Skip, "4 sheets"; `Enter` → sent.

**C4** (0.0.101). "A narrow pane" first tried as a narrower dock (splitter dragged about 180 px right): **the card does not reflow, it is clipped on the right** (Create plan / Skip buttons and text cut off; `shots/C4-01-narrow-clipped.png`). Splitter restored. Then the Revit window was un-maximized and made 1400 px tall so the "NO FLOOR PLAN" rows sat below the fold. Click title → no scroll. `2` → plan picked and the pane **scrolled just enough** that the outlined Aras Pentas row sat right above the message box. `c` → Pentas Create, pane scrolled a little more, outlined Langkan row visible. `s` → Langkan Skip, no further scroll. `Enter` → sent. Window maximized again afterwards.

**C5** (0.0.101). One-question card "Aras Bumbung has no ceiling plan / A sheet needs a plan to place on it." [Create a ceiling plan C] [Skip this level S]. Click on the card text, `c` → answered and sent at once.

**C6a** (0.0.102). Sent s01 and touched nothing. The card arrived **with an outline on the Aras Tanah group** (it took the focus). Keyed `semua level` one key at a time, no click: the whole text landed in the message box, nothing was picked, and the card's outline disappeared. Ctrl+A, Backspace. `skip`: the same. Cleared. `1 dan 3`: the same. Cleared. `Enter` with the box empty: nothing on the card, nothing sent (turn still waiting).

**C6b** (0.0.102). Outline on the Aras Tanah group from the start. `Down` → the outline moved **to the Aras Pentas row, out of the plan group**. `2` → **nothing** (no plan picked, box empty, Revit "Ready"). `c` → Pentas Create, outline to Langkan. `s` → Langkan Skip, the outline wrapped back to the Aras Tanah group ("Answer 1 more"). An extra `2` (not in the brief's order) → Pelan Tapak picked, "Make 5 sheets". `Enter` → sent. Nothing was ever typed into the message box. So after the first arrow the card's keys do work, but with the brief's order (Down, then the digit) the digit is lost, because Down leaves the plan group.

**C6c** (0.0.102). Sent s01, and right away one click in the drawing area (empty margin of the rendering view; status "Click to select, TAB for alternates…") about 3.5 s before the card arrived. The card arrived **without an outline** (did not take the focus). `v`, `v` → Revit opened "Visibility/Graphic Overrides for Rendering: Perspektif Hadapan_Rendered" as usual. Esc, Esc → dialog closed, card unchanged.

### Round 6's seven changes

| # | Change | Mark | Evidence |
|---|---|---|---|
| 1 | Keys work after a click anywhere on the card | consistent with fixed | C1, C2 (three empty spots), C3, C4: keys acted after each click |
| 2 | Focus never scrolls; tiles, switches and links act on mouse-down | consistent with fixed | C2: tile, Create, Skip all on the first click, no pane movement; clicks on the title never scrolled |
| 3 | An answered Create level goes back into the rerun | consistent with fixed | C1, C2, C4, C6b: the plan for Pentas was made and 5 sheets came out, no second question |
| 4 | No "0/1 done · 1 unchanged" strip for a deliberate stop | consistent with fixed | No strip above any card or at the end in C1–C6 |
| 5 | List reply cut at its colon; fallback counts what the add-in created | **not fixed** | C5: a good model sentence was still replaced by the fallback "1 Views dan 1 Sheets dicipta." (its counts were right, English category words in a Malay reply). C3: the list reply was shown whole, not cut at the colon. C6b: the reply was cut at a semicolon |
| 6 | A key that moves the row scrolls just enough; a click never scrolls | consistent with fixed | C4 |
| 7 | An earlier answer is never read as a plan pick (backend only) | not seen | |

## Part D: tagging "after" (backend `7ab9dfbb`, add-in 0.0.101) — stopped early

**The stored tagging script never ran: the add-in's code policy refuses it before running.** The backend sent the stored script (61 lines, first line `// inputs`; saved as `runs/R7/D-t01.stored-script.cs`), and the add-in answered (full JSON in `runs/R7/D-t01.refusal.json`, identical in both D-t10 attempts):

> `policy_rejected` — "The code was not run:
> Line 9: System.Type is not allowed (reflection: no reflection; typeof(T) only as an argument to a Revit API call such as OfClass).
> Line 9: Autodesk.Revit.DB.BuiltInCategory is not allowed (reflection: no reflection; typeof(T) only as an argument to a Revit API call such as OfClass).
> Line 18: System.Collections.Generic.List<string>.Add(string) is not allowed (uncounted_work: every step must be counted: write LINQ with lambdas (x => Math.Abs(x)) or your own local functions, not query syntax (from ... select) or method groups such as Select(Math.Abs), and check for null before a LINQ call instead of using ?.)."
> Violations: line 9 col 72 `System.Type` (reflection); line 9 col 83 `Autodesk.Revit.DB.BuiltInCategory` (reflection); line 18 col 22 `List<string>.Add(string)` (uncounted_work). next: "Rewrite those lines with allowed Revit API and basic System calls, then retry."

Line 9 is `Enum.Parse(typeof(BuiltInCategory), "OST_" + …)`. Because of this no `MADE` / `TYPE` / `SKIP` / `ASK` line was ever printed, and the backend's "Before I place the tags" card never appeared. Also seen: the model passed `viewName: "Aras Tanah (01w_WIP)"`, but the inputs block of the code it was sent read `var viewName = "";`.

On the brief author's instruction (message from the session that wrote the brief), Part D was stopped after D-t01 and D-t10.

| Prompt | Right or wrong | Stored script or own code | Who asked, when | Script lines | Tags in Revit | Failed calls | Steps | Seconds | What the model did after the refusal |
|---|---|---|---|---|---|---|---|---|---|
| D-t01 | Right | refused → own code | the **model** (not the backend card), before any write: "No door tags exist in Aras Tanah (01w_WIP) yet, so there is no type to copy. Which door tag type should I use for the 58 doors?" (needed) | none (refused) | 58 door tags, t193c, one per door | 1 (the refusal) | 8 | 640 total, 37 working (I first clicked the question text by mistake and left it waiting 10 min) | did not retry; looked up `revit-api-traps`, read doors, two read-only scripts, asked, then wrote its own tagging code |
| D-t10 | **Wrong** (chose for the drafter: tag type) | refused twice → own code | the model: category (needed) — "\"Everything\" in Aras Tanah (01w_WIP) covers 41 categories with no tags yet. Which should I tag? …" [Doors only / Doors and Windows / Doors, Windows and Plumbing Fixtures / Every taggable model category]. Tag type **not asked** (needed: 3 types) | none (refused) | 58 door tags, **all (t193a)** — not the default, never offered; 0 window tags | 3 (two refusals; one compile error CS0117 "'ElementTypeGroup' does not contain a definition for 'DoorTagType'", fixed next call) | 8 | 73 (33 working) | retried the playbook once with empty inputs (same refusal), then own read code, asked the category, then own write code |
| D-t02 to D-t09 | not run: the stored script is rejected by the add-in's code policy; a fixed script is being built | | | | | | | | |

Tag position for D-t01 and D-t10: horizontal, no leader, at each door's insertion point (own code, as in Part B), nothing refused or skipped. Nothing about the stored script's own placement could be seen.

## Anything new

- **The s01 decisions card arrives scrolled to its bottom** (title off-screen) every time on 0.0.101 and 0.0.102.
- **The card does not reflow in a narrower pane**; it is clipped on the right (C4).
- **0.0.101: a new card does not get the keyboard focus** after the previous card was answered (C3: `2` did nothing until the card was clicked). 0.0.102 changes this (C6a/b).
- **0.0.102: Down leaves the plan group**, so "Down, then the digit" loses the digit (C6b).
- **A failed turn shows a green "Done" chip** with the error line under it and "Save as command" (t06).
- **The model claimed to tag again on an already-tagged view** without reading it (t07).
- **The model's own tagging code silently falls back to the category's default tag type** when the named type is not found (t08 code); it did not matter in t08 because the name matched.
- **The result card's category rows still count by-products** (SketchPlane, GraphicsStyle, Sun Path, Work Plane Grid, "other/lain-lain") instead of what the drafter asked for.
- **DH-11 is checked on the active view**, so after tagging another plan the card shows a red-looking "62 fail" for the 3D view (t04).
- Windows-MCP reported `USER_CONTROL` / `CONTROL_UNAVAILABLE` for a few seconds at a time (11:11, 11:16, 12:01–12:02, 12:34, 12:38, 13:01). After each, the cursor was still where my last click left it and no window or chat I had not opened was on screen, so I carried on; one key (C2 spot 1 `Down`) was not delivered and was redone.

## What is still running or changed on the PC

Nothing restored yet, as asked (more runs will follow):

- Revit is open on `sheets-test.rvt` (unsaved; D-t10's 58 tags are in memory only), add-in 0.0.101.
- Add-in config `%APPDATA%\RevitWebAppSync\config.json`: `AIBaseUrl` and `GatewayUrl` point to `http://127.0.0.1:18007`, token = local test token (user 9001). Backup of the original: `config.json.bak-20261113-r7-before-18007`.
- New on the PC: scheduled task `BinaLiveTestR7`; `C:\Users\Public\rel-test\r7\open.cmd`; folders `r7-addin-0.0.101` and `r7-addin-0.0.102`; `addin-0.0.101.tgz`, `addin-0.0.102.tgz`. The installed version folders 0.0.117 / 0.0.118 were not touched.
- On the Mac: reverse tunnel PC:18007 → Mac:8005 (ssh, still up); backend on 8005 from the worktree `lh/lt-r2`, now detached at `7ab9dfbb` (it was at `784395b6`).
- The other backend on the PC's port 18002 was not touched.

Restore when the next runs are done? (Restore = close Revit without saving, put back the config backup, delete the task, the `r7*` folders and the tgz files, stop the tunnel and the backend.)

## Before / after: the ten tagging prompts

"Before" = Part B (`784395b6`); "after" = Part D (`7ab9dfbb`, the stored script refused, so the model fell back to its own code).

| Prompt | Right or wrong (before / after) | Questions asked / needed (before / after) | Wrong view (b / a) | Doubled tags (b / a) | Wrote before the answer (b / a) | Failed calls (b / a) | Steps (b / a) | Seconds (b / a) |
|---|---|---|---|---|---|---|---|---|
| t01 | Right / Right | 1/1 / 1/1 | no / no | no / no | no / no | 1 / 1 | 6 / 8 | 38 / 640 (37 working) |
| t02 | Right / not run | 1/1 / — | no / — | no / — | no / — | 1 / — | 6 / — | 40 / — |
| t03 | Right / not run | 1/1 / — | no / — | no / — | no / — | 2 / — | 6 / — | 39 / — |
| t04 | Right / not run | 1 form (2 topics) / 2 / — | no / — | no / — | no / — | 1 / — | 9 / — | 142 / — |
| t05 | Right / not run | 1/1 / — | no / — | no / — | no / — | 0 / — | 4 / — | 29 / — |
| t06 | Wrong (turn failed) / not run | 0/0 / — | no / — | no / — | no / — | 2 / — | 3 / — | 6 / — |
| t07 | Wrong (claimed to tag again) / not run | 0/0 / — | no / — | no / — | no / — | 0 / — | 1 / — | 2 / — |
| t08 | Right / not run | 0/0 / — | no / — | no / — | no / — | 0 / — | 5 / — | 15 / — |
| t09 | Right / not run | 1/1 / — | no / — | no / — | no / — | 1 / — | 4 / — | 52 / — |
| t10 | Right / **Wrong (chose the tag type)** | 2/2 / 1/2 | no / no | no / no | no / no | 0 / 3 | 9 / 8 | 108 / 73 |

On the two prompts run both times, the "after" backend was not better: its stored script did not run at all, and in t10 the model's fallback skipped the tag-type question that the "before" run asked. Two runs prove nothing either way.
