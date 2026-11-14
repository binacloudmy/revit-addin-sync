# Live test round 13, 14 Nov 2026 (binacloudpc)

**One run wrote a value to a parameter the drafter did not name.** In F-t10-d (backend `2267606d`, 15:35), the model split "Wattage Comments" into "Wattage" + "Comments". The backend then wrote `Comments = 1X10W` on the **instance** parameter Comments of all 52 downlights. The card and the reply did not say so. I undid it with Ctrl+Z at once (the card read "Dibuat asal … 10 nilai dikembalikan"), and the model was never saved. ningyo-6d fixed it in `8d991cb3`. On that backend, five more runs of the same prompt (F-t10-e1 … e5) were all clean. In e4 the model sent "Comments" again and the backend put it back to "Wattage Comments". Apart from F-t10-d, no run wrote a value that is in neither the brochure nor the drafter's words, and no run wrote to an element or type the drafter did not name.

The PC stayed up for the whole round (13:42–16:05). The Mac did not sleep, and it was on its charger. There was one run per prompt, so nothing here is a rate. "Consistent with fixed" means one run behaved the way the fix intends.

## Header

| | |
|---|---|
| PC | binacloudpc, Revit 2027.3. Boot time **2026-11-14 01:51:02** and newest minidump `111426-50265-01.dmp`: both were unchanged before every run. The user session was Active. Windows-MCP reported USER_CONTROL briefly at 13:49–13:50 and 14:29–14:31, while someone used the PC. |
| Add-in builds | All built Debug with the staging feed and 0 errors, each in its own folder `C:\Users\Public\rel-test\r13-addin-<v>`, loaded through `BINA_SYNC_PLUGIN_DIR`. The loader log confirmed each version every time.<br>**0.0.107** = `b6f5186a` (the brief's build).<br>Then, at ningyo-6d's request: **0.0.108** `8fee6a7e` (marks as .bmp), **0.0.109** `48bcb50b` (key follows the view), **0.0.110** `78c9853d` (diagnostic, with `BINA_MARK_PROBE=1` via `r13\open-probe.cmd`), **0.0.111** `7e3aeddd` (repaint nudge), **0.0.112** `33513085` (idle log, tooltip, selection paint), **0.0.113** `59c0466d` (one permanent Idling subscription).<br>The pane read "Version 0.0.107" and later "Version 0.0.108" (`shots/pane-version-0108.png`). |
| Backend builds | Run on the Mac on `127.0.0.1:8005` from `lh/lt-r2` (detached), and reached from the PC through the reverse tunnel on 18007.<br>**`3feab08d`** (the brief's build), then `049e61b1`, `2267606d` and `8d991cb3` from ningyo-6d.<br>Against the previous build, none of them has a migration or a dependency change. |
| Fixtures | `sheets-test.rvt`; `downlight-model.rvt` with `PCO_broucher_Downlight_2026.pdf`. Before each run I reopened the fixture without saving, except where a run says "same chat" or `--noreopen`. **No model was saved.** |
| Exports | `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/bbb01e98-3a04-4d70-8d95-40332ac39f15/scratchpad/runs/R13/` holds:<br>`<run>.json` and `<run>.events.json` for each run;<br>`notes.md`, with every run's detail;<br>`shots/` (115 screenshots; `*-4k.png` are 3840×2160). |

## Step 0: why the view showed nothing (the answer to Ashraf's screenshots)

Three causes, one after another. Each hid the drawing or the marks.

1. **The PC had no monitor.** ningyo-6d found (read-only over SSH) that the SAMSUNG monitor was removed at 2026-11-14 **11:18:53**, ten minutes before round 12 began. After that:
   - every monitor read `CM_PROB_PHANTOM`;
   - the session ran at 1024×768 on the AMD adapter, and the RTX 5060 had no display mode;
   - Revit drew nothing in any canvas.

   **What I tried, in the brief's order:**
   - (1) A screenshot of round 12's Revit as it was: the key was over an empty plan (`S0-1`). I closed it without saving.
   - (2) A fresh Revit with 0.0.107: the Rendering view and the plan were empty, before any turn (`S0-3`, `S0-4`). The pixel diff against the background showed only the toggle and a tooltip (`S0-4-diff`).
   - (3) Win+Ctrl+Shift+B: the display fell to 1024×768 on the AMD adapter, and the canvas was still empty (`S0-5`, `S0-6`).
   - (4) Options → Hardware: "Use hardware acceleration" was **on**, card **NVIDIA GeForce RTX 5060** (`S0-7`). I switched it off and restarted Revit: still empty (`S0-8`).
   - (5) Not done. The loader falls back to the installed `versions` folder when the dev folder is empty (`BinaLoader/LoaderApp.cs:225`). Starting without the add-in would mean touching the installed folders, which the rules forbid.

   **Our overlay window is ruled out** (ningyo-6d's test). With the key closed and our `HighlightOverlayWindow` hidden (ShowWindow SW_HIDE), the canvas stayed empty. The status bar named "Grids : Grid : E" under the pointer, so the elements were there but not drawn (`S0-2`, `S0-2b`, `S0-2c`).

   **The monitor came back around 14:23** (SAMSUNG present, 3840×2160), and Revit drew again. Hardware acceleration was still off at that point (`S0-9`, `S0-10`). I put it back **on** at 14:24 (`S0-11`) and restarted Revit. The plan was drawn (`S0-12`). **So the monitor alone brings the drawing back.**
2. **The marks were PNG files, and Revit's in-canvas control takes only .bmp** (ningyo-6d). Every AddControl threw, and the error was swallowed.
   - Evidence on 0.0.107 with the monitor on: "Show in model" put up the key, but no mark appeared. The diff outside the key was 52 px, the key's edge only (`SHOW-2c-diff`). The marks folder held only `.png` files.
   - Fixed in 0.0.108: `.bmp` files were written (`ring-needslook-52-dk-1.bmp`, 8166 B), but still nothing was drawn (`SHOW108-0-diff-overlay`: 0 teal, no ring).
3. **Revit paints added controls only at the next real redraw.** The 0.0.110 probe log reads:
   `marks in Aras Tanah (01w_WIP): due 16, added 16, Revit holds 19, first at (42.73, -15.06, 1.48) ft, image ring-needslook-52-dk-1.bmp`
   - After the rollback, RefreshActiveView and ZF, nothing was drawn.
   - **One scroll-zoom notch** later, the rings and the magenta probe squares appeared (`SHOW110-1-ZF-4k`, `SHOW110-2-zoom-middle-4k`). In 3D they showed at once (`SHOW110-5`).
   - With Accelerated Graphics on, they were drawn at once but tinted pale (`SHOW110-9`).
   - **Fixed in 0.0.111** with a 0.1 % zoom nudge: the rings appear with the key, and nobody touches anything (`SHOW111-1-turn-end-4k`). The view shift is 1 px at 4K. After five Hide/Show cycles the shift is (0, 0): no creep.

What a drafter sees now (0.0.113): the result lands, and amber dashed rings sit on the 16 walls at once, with clean edges and no teal box.
- The rings come in two sizes.
- The key sits top-left and covers 1–2 rings at the top of the building; it never moves.
- On a downlight result, the rings are purple, on all 52 fixtures.
- Hovering a ring shows "Can't keep elements joined · click to select" (0.0.112+, `SHOW112-6-tooltip-4k`). On 0.0.111 it said "Control".

## Round 12 / round 13

| Run | Round 12 | Round 13 |
|---|---|---|
| **SHOW** (E-c07 → Show) | Empty canvas | **Consistent with fixed, on 0.0.113.** Rings at once. The key follows the view ("in {3D}, this view" / back in the plan) and the zoom ("None of the 16 is on screen · Zoom to them"); "Zoom to them" zooms to them. There is no redraw loop: the log stayed at 1195 lines over 20 s idle. Details below |
| **E-d03s** (3 selected walls) | Selection ignored | **Consistent with fixed** (0.0.107, `3feab08d`). Context `selection {ids [566073, 570440, 570655]}`; script `DIM walls G to A: 12 spans` (7 grids + 3 walls × 2 faces); no "too many" SKIP. 5 strings, 2 steps, 7 s, 0 failed. Seen only from the script's lines (the canvas was not drawn then) |
| **SP-4** | Card said 50 | **Consistent with fixed.** Chip "Read back from Revit: 60 of 60 match", "60 doors changed", "Doors 60 elements · 50 of 60 listed". Arrow → Properties "Doors (60)". One Ctrl+Z → "Undone … on 60 elements" [Redo]. On 0.0.110, Ctrl+Z also takes the key and the rings away at once; Ctrl+Y leaves the card at "60 doors changed [Show in model]" with no key |
| **F-t10** | Turn failed | **`3feab08d`: turn failed again.** 10 own `set_parameters` with `handle` → `invalid_args … 'handle' was unexpected` → `repeated_errors` "Stopped after the same tool error repeated…". Nothing written.<br>**`049e61b1` (F-t10-b) and `2267606d` (F-t10-c): right.** The fill playbook is the first call, 10 writes on the 2 types; the third family `LED_r_el_1X8W` is untouched.<br>**`2267606d` (F-t10-d): wrong parameter**, see the first line.<br>**`8d991cb3` (e1–e5): clean, 5 of 5** |
| **F-n3** | A question with a false reason | **`3feab08d`:** 10 own writes with `source kind "rule"` were refused with the whole text "Not written: to put values back … press Undo …"; then the turn failed (`repeated_errors`).<br>**`049e61b1`:** no question, nothing written, but the reply says "I did not record them" (false), "0 A" (Revit had 0.04 A), and "tell me the previous values".<br>**`2267606d` (F-n3-c): right.** The reply was exactly "To put the old values back, press Undo on that change's result card (or Ctrl+Z in Revit): it restores every value exactly. Nothing was changed in this turn." The model's own question was refused.<br>The card's Undo on F-t10-b restored all 10 values (Type Properties checked) |
| **F-t03** | Title said "model" | **Consistent with fixed.** "Product Adrino LED 601 / 8 / 840 is not in the **brochure**". Skip → nothing written.<br>`3feab08d`: 2 invalid fill inputs before a valid one.<br>`049e61b1`: valid on the first call, 0 failed |
| **CARD** | — | Row click: zooms, the pill and the key show "38 of 52". Tab/↓ move the focus visibly. A click on a ring selects (0.0.110: wall 1 element).<br>**Defect, still on 0.0.112:** the type tile's arrow says "Dipilih · 38 dipilih dalam Revit", but Revit shows "3D View" with selection `:0` until the pointer clicks in the canvas (then "Lighting Fixtures (38)", `:38`).<br>The row's arrow on the 16 walls works (Walls (16), `:16`) |
| **ASK-5** | — | **Consistent with fixed.**<br>Q1, the backend card from the sheet-set script: "Which levels should get a sheet?"<br>Q2, the backend card: plan for Aras Tanah / no plan for Pentas and Langkan.<br>Q3, the model's own, after the run: wip/tapak × create/skip.<br>I answered all three by typing "semua level". The script made A-101 Aras 01, A-102 Aras 02 and A-103 Aras Bumbung; no plan was picked and no view was made. The reply does not list the open levels, but the card's checks and Next chips do. A card check reads "belum dibuat **apex** helaian" (garbled). 3 questions in all; the 4th-question refusal did not fire. The first attempt was invalid (the PC's clipboard text, a URL, was sent instead of the prompt) and was redone |
| **SEL** | — | **Not done**: time went to the mark fixes |
| **PIPE** | — | **Not done**: I did not look for a pipe fixture |

### SHOW in detail, per build

| Build | Rings | Key follows view / zoom |
|---|---|---|
| 0.0.107 | none (PNG) | the key text changed on a row click only |
| 0.0.108 | none (bmp written, no redraw) | — |
| 0.0.109 | only after a view switch | **yes** (3D ↔ plan; "None of the 16 is on screen · Zoom to them") |
| 0.0.110 | after a zoom notch | — |
| 0.0.111 | **at once** | **no** (regression) |
| 0.0.112 | at once | no: log "marks idle watch refused: InvalidOperationException: Can not subscribe to an event during execution of that event!" |
| 0.0.113 | at once | **yes**: "marks idle watch on" once (still on in a second chat), "view {3D} -> Aras Tanah (01w_WIP)", "zoom settled after 1 moves" |

The first click on "Show in model" on 0.0.107 did nothing; the second opened the key. This was during a USER_CONTROL hold, so it may be input loss.

## New or worse

1. **F-t10-d: an unnamed instance parameter was written** (see the first line). Fixed in `8d991cb3`, and clean in 5 runs.
2. **F-t10 on `3feab08d` failed** on the `handle` argument. The backend diff had dropped the prompt line telling the model to use `target:{handle}`.
3. **The type tile's arrow** does not show the selection in Revit until the pointer enters the canvas (0.0.107–0.0.112; not checked on 0.0.113).
4. **The key covers rings** at the top-left of the view.
5. **Malformed model arguments, still seen:**
   - `report next/2 … is too long` (SHOW-113b);
   - `inspect {ids}` and `find_elements group_by` inside queries (F-n3-b);
   - two compile errors in the model's own read scripts (F-n3-c).
6. **The ASK-5 card** reads "belum dibuat apex helaian".

## What is still running or changed

Nothing below is restored yet.

**PC:**
- Revit is open on `downlight-model.rvt`, unsaved, with add-in 0.0.113.
- The task `BinaLiveTestR7` points at `r13-addin-0.0.113` through `r7\open.cmd`.
- New this round:
  - the folders `r13-addin-0.0.107` … `r13-addin-0.0.113` and their `addin-0.0.10x.tgz` / `addin-0.0.11x.tgz` files;
  - `C:\Users\Public\rel-test\r13\`, which holds `open-probe.cmd`, `cap.ps1` and `shot.png`.
- **Hardware acceleration:** I switched it off at 13:55 and back **on** at 14:24, as it was ("Draw visible elements only" is on too).
- The Accelerated Graphics toggle was set on once in a sheets-test plan (0.0.110). That model was closed without saving, and every later open showed it off.
- The display ran at 1024×768 from my Win+Ctrl+Shift+B until the monitor came back at 3840×2160.
- Not touched: port 18002, and the installed 0.0.117 and 0.0.118 folders.

**Mac:**
- The backend on 8005 runs **`8d991cb3`** from `lh/lt-r2` (detached).
- The tunnel's ssh process (18007 → 8005) is running.
- `caffeinate` runs as pid 73749, for at most 6 h.
- The build worktree `…/b8bbfc99-…/scratchpad/addin-build` is detached at `59c0466d`.
