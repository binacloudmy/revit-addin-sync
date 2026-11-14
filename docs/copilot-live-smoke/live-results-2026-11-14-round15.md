# Live test round 15, 14 Nov 2026 (binacloudpc), with the "Gabung" result view

**One run wrote a WRONG value to a named parameter.** It was UNDO-c, at 17:41 on backend `5d38bbe3`. The model sent typed `{"Wattage":"10 W","Comments":"1X10W"}`, and the backend renamed the field to "Wattage Comments" but kept the first part's value. So it wrote **Wattage Comments = 10 W** (the drafter wrote 1X10W) on both downlight types.
- I undid it at once with the card's Undo, and Type Properties showed the old values.
- ningyo-9c was told at once and fixed it in `3b064bfe`.
- On that backend and every later one: 9 more F-t10 runs (f1–f5, ARROW2, g1, PROBE, KB1/KB2 and the gabung runs) never wrote 10 W.

No other run wrote to a parameter, type or element the drafter did not name. No model was saved.

Other headlines:
- **The selection is now shown at a turn's end without a click** (gabung3 `999c6052` and later). In earlier builds Revit held the selection made from the pane but did not show it until the drafter clicked in the canvas. Probe 3 found the cause: the view must receive WM_MOUSEACTIVATE and keep the keyboard until Revit is idle. The keyboard stays with the pane: `zz` landed in the input every time.
- **The new result view works:** dots, count badges, a slim bar, a stepper for "needs a look", and English labels. Ten findings came out of it; the five sent at G-B..G-H were fixed in `e88e2fbb`, and four of the five were confirmed on the PC (R1–R4).
- **Not done:** KD3 (stepper + keys inside the hold) and P1 (pipes). From 21:52 the PC reported CONTROL_UNAVAILABLE while RustDesk was connected from Ashraf's Mac, so I stopped all input.

Each prompt ran once (F-t10 more), so nothing here is a rate. "Pass" means one run behaved as intended.

## Header

| | |
|---|---|
| PC | binacloudpc, Revit 2027.3. Display at 200 %. Before every run: boot time 2026-11-14 01:51:02 and minidump `111426-50265-01.dmp` unchanged, SAMSUNG monitor present, session Active. A person was on the PC (USER_CONTROL) at 18:29, 21:09–21:10, 21:16 and 21:38–21:39, and it was unavailable from 21:52. I held input each time. |
| Mac | On the charger at 100 %. No sleep after 2026-11-13 23:16:48. The tunnel and DB were up before every run. |
| Add-in builds | All Debug with the staging feed, 0 errors, loaded through `BINA_SYNC_PLUGIN_DIR`. The loader log and the pane's version were checked for each.<br>**0.0.115** `cd7a8a19`.<br>**0.0.116** `4acd1ede`.<br>Probes as 0.0.116 with `BINA_SELECT_PROBE=1`: `09b46e5f` (probe), `b6a217f6` (probe2, also used without the variable for B1, B2, C1, D1, KB1, KB2), `94721aaf` (probe3).<br>Gabung, all 0.0.116: `935755d1` (gabung), `19141f60` (gabung2), `999c6052` (gabung3), `e88e2fbb` (gabung5). |
| Backends | On the Mac on 8005 from `lh/lt-r2`, in this order: `5d38bbe3`, `3b064bfe`, `b7607ed5`, `0ce8ca3f`, `85ded5ff`, `c8742bfd`, `3e1727a6`. None has a migration or a dependency change. |
| Exports | Under `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/e4bd1d06-6273-45a8-b2a2-1e91debb504b/scratchpad/runs/`:<br>`R15/` holds SEL … PROBE3, B1, B2 and C1;<br>`R16/` holds the KB, G, R, KD and D runs.<br>Each folder has `notes.md` (every run in detail), `*.clicks.txt` (the add-in's log lines) and `shots/`, where `*-4k.png` are 3840×2160. |

## Round 14 / round 15

| Run | Round 14 | Round 15 |
|---|---|---|
| **SEL** (3 selected doors) | The answer was cut from the pane | **Pass.** The door answer (Mark 02 / 2 jam, Mark 01 ×2 / T/B) is the card's first check line. The headline is "828 walls on Aras 01 are highlighted (676 visible in this view)." `inspect` was sent with `target {ids:[the 3 doors]}` |
| **Card Undo** | Stuck on "an undo is already running" | **Pass on 0.0.115** (UNDO-a type, UNDO-b instance, UNDO-c type): "Undone" on the first click, with no input needed. Type Comments was empty again. Later builds also undid at once. C1 (type change): "Undone" appeared about 10–25 s after the click |
| **F-t10** | 3 of 3 clean | **UNDO-c wrote 10 W** (`5d38bbe3`; see the first line).<br>`3b064bfe` f1–f5: no wrong value. f4 wrote only 8 of 10, because the model left out the Wattage Comments value ("SKIP … no product", and the card wrongly said "no product").<br>`0ce8ca3f`/`c8742bfd`: the new NEED step worked (KB1: "NEED Wattage Comments: the drafter wrote a value after its name", the model reran, 10 written). The "'rows' was unexpected" refusal is gone |
| **Arrow / selection shown in Revit** | Arrow: Revit "3D View", `:0` | **0.0.115/0.0.116:** the log said "Revit holds N", but Revit showed nothing until a real click in the canvas.<br>PROBE: 9 of 9 remedies showed the selection, but only because my canvas click came first.<br>PROBE2: with no canvas touch, 10 of 10 failed (including focus on the main window and on the view window).<br>**PROBE3: `mouse-activate` worked** (Tab then went to Revit).<br>gabung (activate + hand back at once): selection shown, but `zz` went to Revit.<br>gabung2 (no activation while typing): `zz` OK, but nothing shown.<br>**gabung3: shown at turn end AND `zz` in the input**; tiles show 14 and 38. Typing through the turn (G-A2, 29 keys): all reached the pane, and Revit did nothing |
| **First click lost** | Tile and Undo needed two clicks | **0.0.116:** the first tile click acts (ARROW2-A). A first ring or dot click: Revit only showed the held selection on 0.0.116; on gabung3 a dot click selects 1 at once |
| **Brochure fill** B1, B2 | — | **Pass ×2.** Model = "Adrino LED 601 / 10 / 840" (brochure p.2), URL = www.pcolite.com (p.8), Description from the prompt. 3 type writes on 1X13W (38). Checked against the PDF's text |
| **Change type** C1 | — | **Pass.** 20 doors changed PTh300a → PTr680a with no question. Revit counted 28 (8 + 20); after Undo, 8 |
| **Dimensions** D1/D2 | — | **Pass.** 5 strings; the wall string has 12 spans (the 3 selected walls only); 12 SKIP lines for walls not selected.<br>D1: large square "new" frames covered the A–G chains.<br>D2 (gabung5): small green dots and badges, no frames, Dimensions (5) shown at turn end |
| **Key drop** KD/KD2 | — | Nothing ever reached Revit:<br>4 "a" keys at the turn's end and 2 "z" keys after a tile click, all inside a 3 s test hold. All landed in the pane or went nowhere.<br>The hook counted **0 dropped** both times, so the drop path itself is untested |
| **KD3** (stepper + keys) | — | **Not done**: the PC was taken (see above) |
| **P1** (pipes) | — | **Not done**: the PC was taken |

## The new result view (gabung3 and gabung5), as a drafter sees it

- **Turn end:** small purple dots (20 px real) on the 52 fixtures. When dots overlap they become count badges (about 30 px, dark digit, readable but blocky). The slim bar reads "● 52 elements changed | Select all | Hide | Undo | ✕". Revit's selection is shown (52) and the camera does not move.
- **Clicks:**
  - an empty canvas click clears the selection and the dots stay;
  - a dot click selects 1;
  - a badge click selects N (tooltip "2 marks here · click to select them");
  - a tile click selects that type and zooms, other dots go dim, and the table follows ("For …1X8W: Normal").
- **Zoom:** badges merge and split after the zoom settles, and the log stays still at idle. "None of the 52 is on screen | Zoom to them" works.
- **The bar:** Hide gives the pill "Copilot result hidden · 52 elements changed | Show again | ✕"; ✕ clears everything; the card's "Show in model" redraws the dots and keeps Revit's selection; Undo gives "Undone" at once.
- **Needs a look (E-c07):** amber numbered badges (1+, 2+, 6+, 8+, 10+, 12, 13+, 14, 16) and a stepper `‹ N of 16 ›`. Each step selects one wall and zooms to it.
- **Labels:** all fixed labels are English. Malay appears only in the model's own sentences.

**Findings on gabung3**, fixed in `e88e2fbb` and rechecked:

| Finding | Recheck |
|---|---|
| The bar changed corner with the view | R1: stays top-left, pill too, across plan/3D/Hide/Show |
| The stepper's › moved under the pointer when the text grew | R2: six presses at one spot gave 1→6 |
| The stepped number was hidden in a merged badge | R3: "3+" and "4+" sit on the stepped wall |
| After a Project Browser click the bar said "None of the 52 is visible in this view" while dots were drawn | R4: the text is kept |
| The first step waited "1 s without Revit going idle" | **R5: still so on the first step** ("5262 ms"); later steps took 85–197 ms |

**Other findings:**
- G-K: a find writes no `select find …` line, only the activation lines.
- G-J: the old bar was gone after the next turn; I did not catch the moment it left.
- KD: at a turn's end the keyboard never leaves the pane.

## What is still running or changed

Nothing below is restored.

**PC:**
- Revit (pid 18200) is open on `sheets-test.rvt` with the E-c07 result (rolled back, nothing written), unsaved. It was started with **`BINA_VIEW_HOLD_TEST_SECONDS=3`** through `r15\open-hold.cmd`, with add-in folder `r16-addin-gabung5`.
- The task `BinaLiveTestR7` now runs `r15\open-hold.cmd` with `r16-addin-gabung5`.
- New folders under `C:\Users\Public\rel-test\`: `r15-addin-0.0.115`, `r15-addin-0.0.116`, `r15-addin-0.0.116-probe`, `-probe2`, `-probe3`, `r16-addin-gabung`, `-gabung2`, `-gabung3`, `-gabung5`, each with its `.tgz`.
- New files: `r15\open-probe.cmd`, `r15\open-hold.cmd`, `r15\ckz.ps1`. `r13\shot.png` is overwritten.
- Not touched: port 18002, the installed 0.0.117/0.0.118 folders, and hardware acceleration.

**Mac:**
- The backend on 8005 runs **`3e1727a6`** (`lh/lt-r2`, detached).
- The tunnel's ssh process (pid 64082) is up.
- `caffeinate` pid 13628 is running, plus round 13's pid 73749 if it is still alive.
- The build worktree `…/b8bbfc99-…/scratchpad/addin-build` is detached at `e88e2fbb`.
- A copy of the brochure PDF is in the scratchpad's `broch/`.
