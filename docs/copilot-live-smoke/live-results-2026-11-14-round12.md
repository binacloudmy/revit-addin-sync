# Live test round 12, 14 Nov 2026 (binacloudpc)

**No value was written to an element or type the drafter did not name, and no value was written that was in neither the brochure nor the drafter's words.** The closest call was F-n3: the model offered "Clear the five parameters to empty/zero on both types". I did not pick it, so nothing was written.

The PC stayed up for the whole round (11:28–12:34). The Mac did not sleep, and it was on its charger from 11:31.

There was one run per prompt, so nothing here is a rate. "Consistent with fixed" means one run behaved the way the fix intends.

| | |
|---|---|
| Runs done | All 11 rows of the table. F-t10 also has F-t10-r: its turn failed, so I sent the same prompt again in the same chat, as the error message tells the drafter to do. One throwaway chat (IDS) found the wall ids for E-d03s |
| Right | F-t04, F-t10-r, SP-6, E-c04, UI-1, UI-2, UI-3 (E-c07) |
| Right outcome with a defect | F-t03 (the card says "not in the **model**"), SP-4 (the card says 50, the playbook wrote 60) |
| Wrong or not as expected | F-t10 (turn failed), F-n3 (asked a question first, with a false reason), E-d03s (the selection was not used) |
| Not done | Part (c) of the 3D-view check (the reason is given below) |

## Header

| | |
|---|---|
| PC | binacloudpc, Revit 2027.3. Boot time **2026-11-14 01:51:02** and newest minidump `111426-50265-01.dmp`: both were unchanged before every run and at 12:33. The session was Active (Ashraf's, from 02:15), and I saw nobody else use the PC. |
| Add-in | **`5dd1998c`** exactly (`feat/copilot-decision-card`), Debug **0.0.106**, built with the staging update feed (0 errors). Copied to the new folder `C:\Users\Public\rel-test\r12-addin-0.0.106`; the DLL reads `0.0.106+5dd1998c…`. Loaded through `BINA_SYNC_PLUGIN_DIR` (task `BinaLiveTestR7`). Before the first run, the loader log said `11:32:41 … loaded 0.0.106.0 from 'C:\Users\Public\rel-test\r12-addin-0.0.106\net10.0-windows'` and the pane menu said "Version 0.0.106" (`shots/pane-version-crop.png`). |
| Backend | **`e83e28bc`** (`feat/copilot-runnable-playbooks`), 6 commits after `6463725b`, with **no migration and no dependency change**. It ran on the Mac at `127.0.0.1:8005` from worktree `lh/lt-r2` (detached). The PC reached it through a reverse tunnel on its port 18007, which I restarted at 11:30. |
| Guard before each run | Mac: tunnel up, DB query answers, no new sleep (the last one is still 2026-11-13 23:16:48), battery level. PC: boot time, minidump, `/health` = 200 through the tunnel, the user session. |
| Fixtures | `downlight-model.rvt` with `PCO_broucher_Downlight_2026.pdf`, and `sheets-test.rvt`. Before each run I reopened the model without saving, except F-t10-r and F-n3 (same chat as F-t10), and E-c04, IDS and E-d03s. Round 11's Revit, which had an unsaved Door Schedule, was closed without saving. **No model was saved.** |
| Exports | `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/7c51ad56-0024-45f2-b627-e23a34e10ce4/scratchpad/runs/R12/` holds `<run>.json`, `<run>.events.json`, the logs, `notes.md` (every run's detail), and the screenshots in `shots/`. |

## Results

| Run | Round 11 / round 12 | What happened | Steps, seconds, failed calls |
|---|---|---|---|
| **F-t04** | Right, but the model offered made-up voltages / **Right** | Details below. The drafter was asked nothing | 4 steps, 10 s, 0 failed |
| **F-t03** | The model asked its own question, with families nobody named / **Right outcome; the card names the wrong place** | The fill playbook ran. The backend's card offered the near products and Skip, but its title says "is not in the **model**" (details below) | 5 steps, 38 s (25 waiting), 1 failed (`next/1 … is too long`) |
| **F-t10** | The model wrote by itself / **Turn failed** | 5 own `set_parameters` were refused, then the turn failed with "The AI model wrote malformed output three times in a row; send your message again, or say it another way." Nothing written. ningyo-6d says the cause is the refusal text cut at 300 characters by the backend, fixed after this round in `1272656e` | 6 steps, 22 s, 6 failed |
| **F-t10-r** | / **Right** | The same prompt, sent again. The fill playbook ran with the typed values. Values were written on the 2 downlight types only, with no wide-write card (details below) | 6 steps, 24 s, 0 failed |
| **F-n3** | Neither put back nor told to Undo / **Told to Undo, but only after a question** | The model asked first, with a false reason and one bad option. After my answer it told me to press Undo. The card's Undo put all 10 old values back (details below) | 3 steps, 27 s (21 waiting), 0 failed |
| **SP-6** | Right / **Right** | The model sent `category = "Doors"` itself. There was no "Which category?". The card read "Parameter name Fire Ratng is not in the model / Which parameter did you mean?" [Fire Rating] [Skip]. Skip sent at once. Reply: "Skipped: no parameter named "Fire Ratng" exists in the model, so nothing was written. If it means the parameter "Fire Rating", say so and I'll set 1 hr on every door." | 3 steps, 30 s (25 waiting), 0 failed |
| **SP-4** | Not tested (wrong level) / **Right write; the card's count is off** | There was no question. The playbook wrote 60 doors and read back 60 of 60, and one Ctrl+Z reverted them. The card says 50 (details below) | 3 steps, 7 s, 0 failed |
| **E-c04** | The list held the new type / **Right** | 10 old types plus Skip, and **(PTr680a) is not among them**. The card is now a form with a filter box, "1 left" and "Show 4 more". Clicking "— Skip" sent at once. Reply: "Nothing changed — you chose Skip, because the old door type ZZ9-NOTYPE is not in the model." | 3 steps, 47 s (41 waiting), 0 failed |
| **E-d03s** | Not done / **Not as expected** | The 3 selected walls reached the backend, but the script still made only the 4 grid strings. It skipped the walls with "104 / 57 walls reach that side … select the walls to dimension" (details below) | 2 steps, 7 s, 0 failed |
| **UI-1** | Times kept counting / **Consistent with fixed** | F-t10-r's footer read "21s" before F-n3 and still read "21s" after it. Its card clock also read 21s | |
| **UI-2** | The first chip was cut at its left edge / **Consistent with fixed** | At normal width, the 3 Next chips each fit on one line. At the narrowest pane they wrap onto 2–4 lines, and none is cut (`shots/UI-2-normal-pane.png`, `UI-2-narrow.png`) | E-c01: 2 steps, 9 s, 0 failed, 20 doors |
| **UI-3** | "16 fixable automatically" / **Consistent with fixed** | The card read "Revit refused: 1 kind of issue · 16 elements named", with no "fixable automatically". The reply was as in round 11 (details below) | 4 steps, 69 s (44 waiting), 1 failed (the rollback) |

### F-t04: the NEED route works, and the drafter was asked nothing

1. **First fill run.** `fill-from-document` (`// inputs`) with:
   - `types = ["downlight"]`, `product = "Adrino LED 601 / 10 / 840"`
   - `fields = [Voltage, Kadaran_Arus_jkr_stec]`, `typed = {}`
   - `read = {Kadaran_Arus_jkr_stec: "0.04 A"}`

   This run already carried a partial `read`. Its output:
   - first line: `NOTHING MADE: read each NEED in the brochure, then rerun with read (same inputs); ask the drafter nothing`
   - then: `NEED Voltage: read it in the row of Adrino LED 601 / 10 / 840 (page 2) and rerun with read {"Voltage": "<the cell as printed>"}, or "none" when that row does not print it`
2. **The model reran by itself**, with `read` plus `Voltage: "none"`. Output: `PLANNED 2 writes: the backend sends them now; do not send them yourself`, and two `SKIP … Voltage: not in the brochure for Adrino LED 601 / 10 / 840`.
3. **The backend wrote** 0.04 A on 1X13W (38) and 1X8W (14), reading back 1 of 1 on each.

**The drafter was asked nothing, and Voltage was not asked about.**

- **Reply shown:** "Kadaran_Arus_jkr_stec = 0.04 A set on both downlight types (38 + 14 fixtures)."
- **The card:**
  - chip: "Read back from Revit: 2 of 2 types match · affects 52 Lighting Fixtures";
  - two "Written as asked · 1" lists, each with "was 0 A" and "PCO_broucher_Downlight_2026.pdf, page 2";
  - check: "Voltage: not in the brochure for Adrino LED 601 / 10 / 840".
- **Type Properties:**
  - c_dl_1X8W: 0.04 A, Voltage 0.00 V;
  - LED_r_el_1X8W (a third family): still 0.00 A.

### F-t03: the playbook ran; the card title says "model"

- **What ran:**
  1. `find_elements` "1X8W" (25);
  2. `fill-from-document` (`// inputs`) with `types = ["1X8W"]`, `product = "Adrino LED 601 / 8 / 840"`, `fields = [Model, Kadaran_Kuasa_jkr_stew]`, `typed = {}`, `read = {}`.
- **Output:**
  - `NOTHING MADE: ask each ASK, then rerun`
  - `WILL Model, Kadaran_Kuasa_jkr_stew on 3 types (25 instances).`
  - `ASK product not found: …`
- **The backend's card:**
  - title: **"Product Adrino LED 601 / 8 / 840 is not in the model"**. The brief expects "is not in the brochure". ningyo-6d says this is fixed in `1272656e`.
  - question: "Which brochure product should fill it? This fills Model, Kadaran_Kuasa_jkr_stew on 3 types (25 instances)."
  - options: [601 / 20 / 840] [601 / 15 / 840] [601 / 10 / 840] [Skip]. **None of the options offers families.**
  - The 3 types are the three families whose names contain the "1X8W" the drafter typed: c_dl, LED_r_el and LED_w_el.
- **After Skip:** no write call was made.
- **Reply:** "Tiada nilai ditulis: Adrino LED 601 / 8 / 840 tiada dalam brosur, jadi Model dan Kadaran_Kuasa_jkr_stew untuk 3 jenis 1X8W (25 instances) dibiarkan kosong."
- **Failed call:** the model's first report was refused: `next/1: 'Isi Model dan Kadaran_Kuasa_jkr_stew untuk 10 W, 15 W atau 20 W ikut brosur' is too long`.

### F-t10 and F-t10-r

**F-t10 (the first send):**

- **Searches:** `find_elements` downlight (0), `model_overview`, `find_elements` Normal (235), a `find_elements` with `type: "Family: Normal"` (failed: not_found), `find_elements` c_dl (52), then `inspect` of 2 ids.
- **Then 5 `set_parameters` of the model's own**, on handle `h_ec5bd359`, all refused with the same text, which the backend cut: "Not written: the drafter typed values for types they named, and that is the playbook fill-from-document's job: it finds the types by the drafter's word, says what will change and writes each type once. Run it: run_revit_code with playbook "fill-from-document" and inputs (types: the drafter's words f"
- **Then the turn failed:** "The AI model wrote malformed output three times in a row; send your message again, or say it another way."
- **The pane showed** "Failed 21.7s" (`shots/F-t10-failed-pane.png`). Nothing was written.

**F-t10-r (the same prompt, sent again in the same chat):**

- **Inputs to `fill-from-document`:**
  - `types = ["downlight"]`, `product = ""`, `read = {}`;
  - `typed = {Wattage Comments: 1X10W, Kadaran_Arus_jkr_stec: 0.04 A, Kadaran_Kuasa_jkr_stew: 10 W, Kadaran_Voltan_jkr_step: 230 V, Voltage: 230 V}`.
- **Output:** "PLANNED 10 writes". The backend wrote 5 values on 1X13W (38) and 5 on 1X8W (14), each read back 1 of 1.
- **No question, and no wide-write card.**
- **Reply shown:** "Kelima-lima parameter telah diisi pada 2 jenis."
- **The card (in Malay):**
  - two "Ditulis seperti diminta · 5" lists, each value with its old one ("asalnya");
  - check: "Nama jenis masih 1X13W dan 1X8W tetapi kini ditetapkan 1X10W: pertimbangkan menamakan semula jenis (38 + 14 instance)."
- **Type Properties:**
  - 1X13W and 1X8W: 1X10W / 0.04 A / 10.00 W / 230.00 V / 230.00 V;
  - **LED_r_el_1X8W: unchanged** (1x8W / 0.00 A / 8.00 W / 0.00 V / 0.00 V).

### F-n3: told to Undo, but only after a question

- **What the model did:** read the 5 fields of 2 ids, then asked its own question: "I don't have the previous values on record for these five type parameters — the reads after the write only show the new values. How should I restore them?"
  - [I'll press Undo in Revit (restores all five parameters at once)]
  - [Clear the five parameters to empty/zero on both types]
  - [I'll type the old values here]
- **Two problems with the question:**
  - **The reason is false.** The F-t10-r card shows every old value ("asalnya 1X13W", "asalnya 13 W", …).
  - **The second option would write values that are in neither the prompt nor the brochure.**
- **My answer:** the Undo option. Nothing was written.
- **Reply:** "Nothing was changed by me — … Press Undo in Revit now; that reverses the whole change in one step. After the undo, tell me if you want the old values read back and recorded so a future revert is possible."
- **The card's Undo:** "Dibuat asal. Model kembali seperti asal. 10 nilai dikembalikan seperti asal pada 2 jenis. Tiada yang lain berubah."
- **Type Properties afterwards:** 1X13W 1X13W / 0.00 A / 13.00 W / 0.00 V / 0.00 V, and 1X8W 1X8W / 0.00 A / 8.00 W / 0.00 V / 0.00 V. **The old values are back.**

### SP-4: three different counts

- **Inputs to `set-parameter`:** `category = "Doors"`, `values = {Comments: Done}`, `place = "Aras 01"`.
- **Output:** `WRITE instance | 60 doors on Aras 01 | @0 | Comments = Done`. The backend read back 60 of 60.
- **Reply:** "Comments = Done set on all 60 doors on Aras 01."
- **The card disagrees:**
  - chip: **"Read back from Revit: 50 of 50 match"**;
  - "Elements written · 50 · Doors 50 elements";
  - check: "60 doors are on Aras 01; the highlight selected 50 in view."
  - The in-model key showed 56. **So the reply says 60, the card 50, and the key 56.**
- **In Revit:**
  - window-select in the plan + Filter gave Doors 58, with Comments `<varies>` (as in round 11, where the split was 56 Done + 2 doors on Aras 02);
  - **one Ctrl+Z** left all 58 empty.
- **What I did not count:** I did not count the doors by level in Revit. The 60 / 2 split is the playbook's count.

### E-d03s: the selection was not used

- **The selection:** I picked 566073, 570440 and 570655 with Manage → Select by ID. Properties read "Walls (3)". The 3 ids came from a throwaway chat, which said they are straight walls along the X axis at Y 0, −3600 and −14100 mm.
- **The turn's context carried** `selection {count 3, Walls 3, ids [566073, 570440, 570655]}`.
- **What the script did:**
  - The model sent `dimension-grids-walls` with `what = "Grids and walls"` and `offsetMm = 8`.
  - The script reads `uidoc.Selection` ("Selected walls win").
  - Yet the output was the 4 grid strings, 12 "not straight" SKIPs (none of them the selected ids), and "SKIP walls 1 to 13: 104 walls reach that side, too many for one string; select the walls to dimension" (the same for G to A, 57).
- **Reply:** "Made 4 dimension strings in Aras Tanah (01w_WIP); wall strings were skipped as 104 and 57 walls reach each side."
- **The card** tells the drafter to "select walls and rerun", although 3 walls were selected.
- **What I could not check:** I could not see whether the selection was still there when the script ran, because the canvas did not draw (see the 3D view section below). I did not debug this.

### UI-3 (E-c07)

- **The question:** "Change 205 walls? / This changes 205 walls in the whole model." [Change 205 walls] [Skip].
- **After "Change 205 walls":** Revit rolled the change back with the same 4 messages as round 11.
- **The card:**
  - new banner: "Not every change in this turn was applied. Check the receipt before relying on the reply.";
  - headline: "Nothing changed: Revit rolled the change back. Change a smaller set (one level, a view or a selection), or first fix what Revit names.";
  - **"Revit refused: 1 kind of issue · 16 elements named"**;
  - row: "Can't keep elements joined. · 16 elements [Show]";
  - "Check 4 things".
- **The status chip** reads "Check receipt 20.0s".

## The empty canvas (asked by ningyo-6d)

Ashraf asked why the {3D} view of `downlight-model.rvt` shows the result key and nothing else.

**What I found:** it is **not the view's settings**. In this session, Revit on the PC drew nothing in the canvas, in both models. Elements are there and Revit counts them as visible in the view, but it does not draw them, not even when they are selected. I did not find the cause.

**The evidence:**

- The {3D} canvas was empty from the moment the model opened (11:33, `shots/r12-loaded.png`), before any turn ran.
- **(a)** After ZE (zoom to fit) it was still empty (`shots/V3D-a-ZE.png`).
- **(b)** The view's settings show nothing that would hide the lamps:
  - **Template:** View Template "jkrEL_3D View (WIP)", which is why V/G is greyed out. Discipline Electrical.
  - **Extents:** Crop View off, Crop Region Visible off, Far Clip off, Scope Box None, **Section Box off**.
  - **Temporary Hide/Isolate:** no frame on the view.
  - **V/G Model Categories:** Lighting Fixtures is checked.
  - **V/G Filters:** every jkrEL_* filter is enabled and visible, with colour overrides only.
  - **V/G Revit Links:** the one link (jkrAR20_6_…191112a) is checked, but it is **unloaded** (`find_elements` says so on every search). That link is the building, so the building cannot show. This explains why the building is missing, but not why the lamps are.
  - **Accelerated Graphics:** switched on, still empty (`V3D-c-AG-on.png`). I switched it back off.
- **Lamps visible but not drawn:** Select All Instances → Visible in View on c_dl_1X8W selected **14** (Properties "Lighting Fixtures (14)", status ":14"). Nothing was drawn, not even the selection highlight.
- **The other model too:** after I reopened `sheets-test.rvt` for E-d03s (12:20), the floor plan Aras Tanah (01w_WIP) was **also empty** (`selbyid.png`, `E-d03s-selected.png`). Round 11's screenshots of the same plan show it drawn.
- **(c) Not done.** I made no write on the downlight model after the check, and the result key was gone after the Undo.

The PC has had four bugchecks in three days, two of them 0x133. A display or driver fault in this Revit session would fit what I saw, but I did not prove it.

## Round 11's findings

| Round 11 finding | Round 12 |
|---|---|
| F-t04: the first fill planned nothing, and the model offered 230/240/220 V | **Consistent with fixed.** NEED → automatic rerun with `Voltage: "none"`; no question; Voltage is listed as not in the brochure |
| F-t04: the reply was a fallback (on `c22e1de5`) | Consistent with fixed: the reply carries the value |
| F-t03: the model asked its own question, offering families nobody named | **Consistent with fixed**: the playbook ran, and the backend card offers only products and Skip. **New defect:** its title says "not in the model", not "not in the brochure" |
| F-t10: the model wrote by itself; the playbook did not run | **Consistent with fixed** in F-t10-r (the playbook ran with `typed`). The refusal of the model's own writes fired as designed. On the first send, that refusal (cut at 300 characters) led to a **failed turn**. ningyo-6d says the cut is fixed after this round, in `1272656e` |
| F-n3: neither put back nor told to Undo; the team's question was unreadable | **Partly fixed.** The reply tells the drafter to press Undo and nothing is written, and the card's Undo restores all 10 values. **Not fixed:** the model asks a question first, with a false reason, and one option would write empty/zero values |
| SP-4: "no question" was not tested (wrong level) | **Consistent with fixed**: no question, 60 written, one Ctrl+Z reverts. New: the card says 50 where the write was 60 |
| SP-6 | Right again; the model now sends `category = "Doors"` itself |
| E-c04: the old-type list included the new type | **Consistent with fixed**: (PTr680a) is not in the list |
| E-d03s: not done | **Done, not as expected**: the selected walls were not dimensioned |
| UI: a finished turn's time kept counting | **Consistent with fixed** (UI-1) |
| E-c01: the first Next chip was cut at its left edge | **Consistent with fixed** (UI-2, at normal and narrowest width) |
| E-c07: "16 fixable automatically" was not tested | **Consistent with fixed**: the claim is gone. Revit still rolls back on this model (model data) |
| The model sends malformed arguments now and then | **Still seen:** a `report` `next` item too long (F-t03); `type: "Family: Normal"` in `find_elements` (F-t10) |

## New or worse

1. **F-t10: a failed turn**, "The AI model wrote malformed output three times in a row", right after 5 refusals. Nothing was written. ningyo-6d says the cause is fixed after this round.
2. **F-n3: a question with a false reason** ("I don't have the previous values on record"), and an option to clear the five parameters to empty/zero, which would write values that are in neither the prompt nor any document.
3. **SP-4: the card counts 50 doors** (chip and list) where 60 were written and read back. The key says 56.
4. **E-d03s: the drafter's selected walls were ignored**, and the card tells them to select walls.
5. **F-t03: the card title says "is not in the model"** for a product missing from the brochure.
6. **Revit on the PC drew nothing in the canvas** in this session, in both models (see the section above). This is not an add-in finding as far as I can tell, but it blocks visual checks on the model.
7. **Small UI points:**
   - At its end, F-t10-r's Done chip read 24.7s while its footer and clock read 21s.
   - At the narrowest pane: the tab reads "Cc", the composer's "Reasoning" is cut, and the footer reads "12:26 PM7s".

## What is still running or changed

Nothing is restored yet.

**PC:**
- Revit is open on `sheets-test.rvt` after E-c07, with nothing changed (Revit rolled back) and nothing saved. It runs add-in 0.0.106 through the task `BinaLiveTestR7`.
- The task `BinaLiveTestR7` now points at `r12-addin-0.0.106`.
- New this round: the folder `C:\Users\Public\rel-test\r12-addin-0.0.106` and the file `addin-0.0.106.tgz`.
- From earlier rounds: the `r11-*`, `r10-*` and `r7*` folders and their tgz files, the `brochure` folder, and the add-in config pointing at port 18007.
- Not touched: port 18002, and the installed 0.0.117 and 0.0.118 folders.
- Accelerated Graphics was toggled on and back off in downlight-model's {3D}. That model was later closed without saving.

**Mac:**
- The backend on 8005 runs `e83e28bc` from `lh/lt-r2` (detached).
- The reverse tunnel's ssh process (18007 → 8005) is running.
- `caffeinate` runs: pid 56728 (this round, at most 6 h), plus round 11's if it is still alive.
- The build worktree `…/b8bbfc99-…/scratchpad/addin-build` is now detached at `5dd1998c`.
