# Live test round 9, 13 Nov 2026 (binacloudpc), second attempt

Round 9 has three parts: tagging "after" (D), the first run in Revit of dimensions, change type and schedule (E), and the brochure fill (F). There was one run per prompt, so nothing here is a rate. Where this report says "consistent with fixed", one run behaved the way the fix intends.

## Header

| | |
|---|---|
| PC | binacloudpc (Windows, 3840×2160 at 200 %), driven from the Mac through Windows-MCP (port 48820, task `windows-mcp-server` already running) and SSH |
| Revit | 2027.3 (Education) |
| Add-in | Debug **0.0.101** (commit `73c90db`, merged as `80c5ee4`), loaded through `BINA_SYNC_PLUGIN_DIR` (task `BinaLiveTestR7`). The loader log read "loaded 0.0.101.0" before every run. Not rebuilt |
| Backend | local bina-ai **`3302ee55`** (`feat/copilot-runnable-playbooks`, worktree `lh/lt-r2`, detached), Mac `127.0.0.1:8005`, reverse tunnel to the PC's port 18007. No migration or dependency change from `3c537c8d` |
| Fixtures | D, E: `C:\Users\Public\rel-test\sheets-test\sheets-test.rvt`. F: `C:\Users\Public\rel-test\brochure\downlight-model.rvt` with `PCO_broucher_Downlight_2026.pdf`, copied there by scp (same sizes as on the Mac). Every run reopened the model without saving. **No model was saved** |
| Exports | for each turn, its `ai.copilot_turns` row and all its `ai.copilot_turn_events` rows (`<id>.events.json`, summary `<id>.json`), plus screenshots, filter counts and `notes.md`, in `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/628e0efd-deec-45d4-bf24-1e2ea85c8a96/scratchpad/runs/R9/`. Shots are in `runs/R9/shots/`; the first attempt's files are in `runs/R9/attempt1/` |

### What happened before this run

- **First attempt (session `b84e8b4d`, backend `3c537c8d`).** It started at 13:22. It ran D-t01 and D-t02, both right: the stored script ran, made 58 of 58 tags, and the backend asked "Which tag type?". It also saw the strip say "50/50 done" for 58 tags, and the Malay reply replaced by "58 objek dicipta.". D-t03 was in progress when the PC crashed.
- **The PC crashed with a blue screen.** Bugcheck `0x133 DPC_WATCHDOG_VIOLATION` (a Windows driver fault), minidump `111326-24734-01.dmp`. The PC was last alive at 13:33:59 and back up at 13:41:38. The same PC has another minidump, from 12 November 18:13. This is a driver fault of the PC, not something the run did.
- **Restart.** Ashraf signed in at 14:26. This attempt started at about 14:27 and ran until 17:50. **The PC did not crash or reboot again**: boot time stayed 13:41:38.
- **Windows-MCP reported `USER_CONTROL`** from 15:46:42 to 15:47:20 (three retries). No window or chat that I had not opened was on screen, and Revit was responding, so I carried on. Nothing else like it happened.

## Part D: tagging, the "after" run (backend `3302ee55`)

Plan A = `Aras Tanah (01w_WIP)` (58 doors, 42 windows, baseline Filter total 1785). Plan B = `Pelan Tapak`. Three door tag types are loaded. Answers were the same as round 7 Part B: `(t193c)` for doors, `(t673a)` for windows, Skip for t05 and t09, Doors for t10. Counts are from Revit (window-select the whole view, then the Filter dialog).

In every run the code the add-in got started with `// inputs` (the stored tagging script, 61 lines), the add-in accepted it, and **every question came from the backend card** (0 model tokens). The cards are titled by their question ("Which tag type?" and so on), not "Before I place the tags".

| Prompt | Right or wrong | Script lines (first run → rerun) | Questions (who, topic) | Tags in Revit | Reply as shown | Failed | Steps | Seconds (working) |
|---|---|---|---|---|---|---|---|---|
| t01 | Right | `NOTHING MADE … ASK which tag type? (t193a)\|(t193c)\|(t193b)` (727 steps) → `MADE 58 Doors tags in Aras Tanah (01w_WIP) … type=(t193c)` + 10 `TYPE` lines (1095 steps) | backend: tag type | 58 door tags, 0 window tags (total 1843) | "Added 58 door tags in Aras Tanah (01w_WIP) using (t193c) sc1-100 tx1.8mm r2.5mm KKP, all 58 verified." | 0 | 4 | 81 (55; the first script run after Revit started took 47 s) |
| t02 | Right | as t01 | backend: tag type, in English | 58 / total 1843 | the model's Malay sentence: "58 tag pintu ditambah dalam Aras Tanah (01w_WIP) dengan (t193c) sc1-100 tx1.8mm r2.5mm KKP." (attempt 1 showed the fallback "58 objek dicipta.") | 0 | 4 | 29 (8) |
| t03 | Right | `ASK which tag type? (t673a)…` → `MADE 42 Windows tags … (t673a)-3 Hexagon 1.8mm` | backend: window tag type | 42 window tags, **0 door tags** (total 1827) | "Made 42 Windows tags in Aras Tanah (01w_WIP) with (t673a)-3 Hexagon 1.8mm, all visible in the view." | 0 | 4 | 32 (9) |
| t04 | Right | `ASK which view? Aras 01 (07d_Dokumen) \| Aras 02 … \| Aras Bumbung … \| Aras Tanah (01w_WIP) \| Pelan Tapak` → `ASK which tag type?` → `MADE 58` | backend: view (form with a filter box), then backend: tag type | 58 in Plan A (total 1843) | **fallback "58 objects created."**; the model's report was "Made 58 door tags in Aras Tanah (01w_WIP) with (t193c) …, covering 10 door types." | 0 | 7 | 127 (60) |
| t05 | Right | `ASK Aras Pentas: has no FloorPlan` | backend: "Aras Pentas has no floor plan / Tags need a plan to go in." [Create a floor plan] [Skip this level] | none (total 1785) | "Aras Pentas tiada FloorPlan, jadi tiada tag dibuat — langkau seperti diminta." | 0 | 3 | 46 (5) |
| t06 | **Right** (round 7: wrong) | `ASK which tag type?` → `MADE 58 Doors tags in Pelan Tapak \| view_id=1684067` | backend: tag type | Plan B: 58 door tags (total 1709). Plan A: 0 (total 1785) | "Tagged 58 doors in Pelan Tapak with (t193c) sc1-100 tx1.8mm r2.5mm KKP." | 0 | 8 | 84 (41) |
| t07 (2nd message) | **Wrong** (wrong understanding, as in round 7) | none: no tool call, no read | none | still 58, nothing doubled (total 1843) | "58 door tags were added in Aras Tanah (01w_WIP) using (t193c) sc1-100 tx1.8mm r2.5mm KKP, and all 58 are selected." It never says the doors were already tagged | 0 | 1 | 1 |
| t08 | Right | no question; `MADE 58 … type=(t193a) sc1-100 tx5mm r7mm KKP` | none | 58 (total 1843), visibly the large 5 mm tags | "Tagged 58 doors in Aras Tanah (01w_WIP) with (t193a) sc1-100 tx5mm r7mm KKP." | 0 | 2 | 10 |
| t09 | Right | `ASK tag type not found: ZZ9-NOTAG ? (t193a) \| (t193c) \| (t193b)` | backend: "Tag type ZZ9-NOTAG is not in the model" with the 3 types **and Skip** | none (total 1785) | "Skipped: tag type ZZ9-NOTAG doesn't exist in the model, so no door got tagged and the view is unchanged. The available door tag types are (t193a), (t193c) and (t193b) if you want to rerun with one of those." | 0 | 3 | 49 (5) |
| t10 | Right | `ASK which category? Doors \| Windows \| Plumbing Fixtures \| Walls` → `ASK which tag type?` → `MADE 58` | backend: category, then backend: tag type | 58 door tags, 0 window tags (total 1843) | "Made 58 Doors tags in Aras Tanah (01w_WIP) with type (t193c) sc1-100 tx1.8mm r2.5mm KKP." | 0 | 7 | 99 (16) |

**Summary.** 9 of 10 right. t07 was wrong (wrong understanding). Questions: 11, all needed, all from the backend, all in English. Wrong view 0, doubled tags 0, wrote before an answer 0, failed calls 0. Average model steps 4.3. Average seconds 58, of which 21 working.

**The team's wide-write question never appeared** in Part D. Each tagging run creates 42 to 58 new tags and changes no existing element, so it should not have appeared.

**Where the tags land** (zoomed screenshots `D-t01-zoom`, `D-t03-zoom`, `D-t08-zoom`):
- Tags sit horizontally in a rotated plan, centred on each door or window, with no leader. Many sit on the wall line.
- With the small (t193c) tags I saw no tag on top of another.
- With the large (t193a) tags in t08, tags in the dense south-west cluster (P3, P6, P6, P3) touch and overlap each other.
- The result card says the same in t02 ("semak kedudukan tag yang bertindih") and t04 ("Tags sit on top of each other in dense areas").

**Result card (t01).** It said "Check 1 thing: 58 tags added; 50 doors selected in the view, remaining 8 are outside the current crop/zoom." Its "Undo all" button was gone from the card about 2 minutes later, after my Filter and zoom clicks.

### Tagging before / after (round 7 Part B against this Part D)

"Before" is backend `784395b6`, where the model wrote its own code. "After" is `3302ee55`, which runs the stored script.

| Prompt | Right (before / after) | Questions asked / needed (b / a) | Who asked (after) | Wrong view (b / a) | Doubled (b / a) | Wrote before answer (b / a) | Failed calls (b / a) | Steps (b / a) | Seconds (b / a) |
|---|---|---|---|---|---|---|---|---|---|
| t01 | Right / Right | 1/1 / 1/1 | backend | no / no | no / no | no / no | 1 / 0 | 6 / 4 | 38 / 81 |
| t02 | Right / Right | 1/1 / 1/1 | backend | no / no | no / no | no / no | 1 / 0 | 6 / 4 | 40 / 29 |
| t03 | Right / Right | 1/1 / 1/1 | backend | no / no | no / no | no / no | 2 / 0 | 6 / 4 | 39 / 32 |
| t04 | Right / Right | 1 form (2 topics) / 2 cards (view, type) | backend | no / no | no / no | no / no | 1 / 0 | 9 / 7 | 142 / 127 |
| t05 | Right / Right | 1/1 / 1/1 | backend | no / no | no / no | no / no | 0 / 0 | 4 / 3 | 29 / 46 |
| t06 | **Wrong** (turn failed) / **Right** | 0/0 / 1/1 | backend | no / no | no / no | no / no | 2 / 0 | 3 / 8 | 6 / 84 |
| t07 | Wrong / Wrong | 0/0 / 0/0 | — | no / no | no / no | no / no | 0 / 0 | 1 / 1 | 2 / 1 |
| t08 | Right / Right | 0/0 / 0/0 | — | no / no | no / no | no / no | 0 / 0 | 5 / 2 | 15 / 10 |
| t09 | Right / Right | 1/1 / 1/1 (now with Skip) | backend | no / no | no / no | no / no | 1 / 0 | 4 / 3 | 52 / 49 |
| t10 | Right / Right | 2/2 / 2/2 | backend | no / no | no / no | no / no | 0 / 0 | 9 / 7 | 108 / 99 |
| **Total** | **8 / 9** | | | 0 / 0 | 0 / 0 | 0 / 0 | **8 / 0** | 5.3 / 4.3 | 47 / 58 |

- **Consistent with fixed:** the stored script runs in Revit, so the questions now come from the backend; failed calls went from 8 to 0; t06 went from failed to right.
- **Not fixed:** t07. The second message is answered from the chat with no read, so the script's "already tagged" path was never exercised.
- **Seconds rose** only because of the time I took to answer cards. The working seconds are lower.

## Part E: first run in Revit of three more jobs

### Dimensions: ran in Revit yes, plus one timeout

Plan A = `Aras Tanah (01w_WIP)`. It had no dimensions at baseline and has 20 grids: 1 to 13, and A to G (C and E are short). The plan is rotated. Expected E = 4: two chains and two overall strings.

| Prompt | Right or wrong | Script lines | Questions | Strings in Revit | Reply as shown | Failed | Steps | Seconds |
|---|---|---|---|---|---|---|---|---|
| d01 | **Wrong** (wrong count, 2 of 4) | `MADE 2 strings in Aras Tanah (01w_WIP) \| … type=Diagonal - 2.5mm Arial` / `DIM grids 1 to 13: 12 spans` / `DIM grids G to A: 6 spans` / **`SKIP overall 1 to 13: already dimensioned`** / **`SKIP overall G to A: already dimensioned`** (2723 steps) | none | Dimensions 2 (total 1787) | "Added 2 grid dimension strings in Aras Tanah (01w_WIP): 12 spans (1–13) and 6 spans (G–A)." | 0 | 2 | 6 |
| d07 (2nd message in d01's chat) | Right as the page states it | no tool call | none | still 2 | "The grid dimensions are already placed: 2 dimension lines in Aras Tanah (01w_WIP), 12 spans on grids 1–13 and 6 spans on grids G–A. …" The model answered from the chat; the script's skip check was not run | 0 | 1 | 1 |
| d04 (active view {3D}) | **Wrong** (wrong count; the question was right) | `ASK which view? Aras 01 (07d_Dokumen) \| … \| Pelan Tapak` → same 5 lines as d01 | backend form "Which view? / Which plan should get the dimensions?"; nothing made before the answer | 2 in Plan A | "Added 2 dimension strings in Aras Tanah (01w_WIP): grids 1–13 (12 spans) and G–A (6 spans), type Diagonal - 2.5mm Arial." | 0 | 4 | 53 (11) |
| d09 (ZZ9-NODIM) | Right (Skip); the options were wrong | `ASK dimension type not found: ZZ9-NODIM ? Diagonal - 2.5mm Arial \| Linear Dimension Style \| Linear Dimension Style \| Linear Dimension Style` | backend form "Dimension type ZZ9-NODIM is not in the model": **three options with the same label "Linear Dimension Style"**, plus Skip | none (total 1785) | "Nothing was made: the dimension type ZZ9-NODIM does not exist in the model, so you chose Skip and no dimensions were added to Aras Tanah (01w_WIP)." | 0 | 3 | 71 (7) |
| d03 (grids and walls, 8 mm) | **Wrong** (bad code: timeout, twice) | none printed | none | none | "I couldn't dimension anything: the playbook run timed out twice at its grid/wall loop and nothing was kept, so no dimensions exist in Aras Tanah (01w_WIP) and no check was made. I won't repeat that same call a third time." | 2 | 3 | 202 |

**d03 timeout, as the model received it (both times):**

> `{"ok":false,"status":"timeout","summary":"Stopped at line 27: the code reached the limit of 30 s.","data":{"limit":"time","steps":38632,"seconds":95.1,"lines":[27,47,52]},"error":{"code":"timeout","message":"The code was stopped at line 27 after reaching the limit of 30 s. Nothing it did was kept. Loops over the whole model must filter first."}}`

The second attempt was the same, with 95.4 seconds. The lines it names:
- line 27: `var have = inV().OfClass(typeof(Dimension)).Cast<Dimension>().Select(d => d.References.Cast<Reference>().Select(r => r.ElementId));`
- line 47: `if (have.Any(h => h.Count(x => ids.Contains(x)) > 1)) R.Add("SKIP " + s + "already dimensioned");`
- line 52: `if (gs.Count > 1) mk("grids", gr, 2, gs);`

**Placement (d01, screenshot `E-d01-zoomAG`).**
- The G–A string (2400, 6000 …) runs just inside bubbles A and B, between the bubbles and the building. It does not cross a bubble.
- The 1–13 string sits inside the end with bubble 1. Its "3900" text lies over a light-fixture symbol.
- The string type is `Diagonal - 2.5mm Arial`.

### Change type: ran in Revit yes, plus one timeout

Placeholders were counted in Revit (Project Browser, Select All Instances > In Entire Project):
- Door Type A = `(PTh300a) 750 x 2100 sd-d`: 20 doors.
- Door Type B = `(PTr680a) 900 x 2100 sd-a`: 8 doors.
- The wall types: `jkrAR_wll_DBb_bb01_110_UMM_ex_(DBb300a)-3 @ DB1 Batu bata 110mm` (205 walls) and `jkrAR_wll_DBb_bb02_55_ANJ_ex_(DBb200a)-3 @ DB2 Batu bata 55mm` (99 walls).
- c06 (a type in Plan A and elsewhere) could not be set up: every door type shown in Plan A has all its instances there.

| Prompt | Right or wrong | Script lines | Questions | Counts in Revit (old / new) | Reply as shown | Failed | Steps | Seconds |
|---|---|---|---|---|---|---|---|---|
| c01 | Right | `types changed: 20` / `MADE 20 doors changed to jkrAR18_dor_PTr_Aa11a1_in_UMM_(PTr680)-3: (PTr680a) 900 x 2100 sd-a in the whole model` / `FROM … (PTh300a) 750 x 2100 sd-d: 20 \| ids=…` | none | before 20 / 8, after **0 / 28**; PTa001a unchanged at 3. **One Ctrl+Z → 20 / 8** | "20 doors changed from (PTh300a) 750 x 2100 sd-d to (PTr680a) 900 x 2100 sd-a across the whole model, none skipped." | 0 | 2 | 15 |
| c05 (new type ZZ9-NOTYPE) | Right | `ASK new door type not found: ZZ9-NOTYPE ? …` (12 types) → MADE 20 | backend form "New door type ZZ9-NOTYPE is not in the model" with a filter box and Skip; picked PTr680a | 0 / 28; no type made (added 0) | "Changed 20 doors from (PTh300a) 750 x 2100 sd-d to (PTr680a) 900 x 2100 sd-a in the whole model." | 0 | 4 | 117 (14) |
| c04 (old type ZZ9-NOTYPE) | Right | `ASK old door type not found: ZZ9-NOTYPE ? …` (11 types) | backend form with Skip; answered Skip | 20 / 8 unchanged | "Skipped: no doors changed, since ZZ9-NOTYPE is not a door type in the model. Name the real old type and the change runs at once." | 0 | 3 | 67 (5) |
| c07 (205 walls, whole model) | **Wrong** (bad code: timeout; asked twice) | `ASK which scope? Whole model (205 walls)` → timeout at line 148, twice → the model's own code timed out at line 17 | backend "Which scope? / Which elements should change type?" [Whole model (205 walls)] [Skip], **then the team's wide-write card**: "Before I write: Change walls of type DB1 Batu bata 110mm to DB2 Batu bata 55mm using a filtered… on 205 elements." [Write to all (205 elements)] [Cancel, write nothing] | 205 / 99 unchanged | "The wall type change could not be completed: the model holds 205 walls of … and every attempt to change them to … was stopped at the 30 second limit, so nothing was changed. Next: run it on one level or a selection, or split it into batches." | 3 | 6 | 190 (109) |

**c07 refusals and errors, exactly as the model received them:**
- **Stored script, both attempts:** `"The code was stopped at line 148 after reaching the limit of 30 s. Nothing it did was kept. Loops over the whole model must filter first."`, with `data {"limit":"time","steps":16489,"seconds":30.2,"lines":[148]}`. Line 148 is `if (e.GetTypeId() == to.Id) no("already " + to.Name, e);`, inside the loop that calls `ChangeTypeId` on each wall.
- **What the model did next:** it retried with an extra input, `"reach": 0`, and got the same timeout. Then it wrote 23 lines of its own code (a filtered collector and `w.ChangeTypeId` per wall). Its first line is `string oldName = "jkrAR_wll_DBb_bb01_110_UMM_ex_(DBb300a)-3 @ DB1 Batu bata 110mm";`.
- **The model's own code:** it timed out at line 17 (`try { w.ChangeTypeId(newT.Id); changed++; }`) after printing `found 205 walls of old type`.

**What the drafter sees in the pane:** a result card does show for change type.
- In c01 its headline row read **"other 2"** although 20 doors changed. Its one row was named by the old type, "(PTh300a) … 20".
- It also said "Check 2 things: The new type is 900 wide against a 750 opening: check each door still fits its wall in plan. / Highlight covers only 2 elements, not the 20 changed."
- In c07 the pane showed "Not every change in this turn was applied …" and "0/2 done · 2 failed (timed out)", although the turn had 3 failed calls.

### Schedule: ran in Revit yes

For each schedule I opened the one Revit made and compared every total the Copilot stated with the schedule's own grand total and footers.

| Prompt | Right or wrong | Script lines | Questions | Schedule in Revit | Totals stated / in the schedule | Failed | Steps | Seconds |
|---|---|---|---|---|---|---|---|---|
| s01 | Right | `MADE Door Schedule \| 62 doors in the whole model \| fields: Family and Type, Mark, Level, Width, Height` / `DEFAULT fields: …` / `TOTAL 62 doors` | none | "Door Schedule" opened (although the script's data said `ui applied: false`). Columns as listed, itemized, Mark empty | 62 / grand total **62** ✓ | 0 | 2 | 8 |
| s07 (no category) | Right; the question came from **the model** | after the answer: `MADE Wall Schedule \| 1458 walls … fields: Family and Type, Base Constraint, Length, Area, Volume` / `TOTAL Length 2333013 mm` / `TOTAL Area 5753 m²` / `TOTAL Volume 289.25 m³` | model: "What should the schedule cover?" [Doors] [Rooms] [Walls] [Windows]; answered Walls | "Wall Schedule" opened, itemized | 1458 / 2333013 mm / 5753 m² / 289.25 m³; grand total row **1458 \| 2333013 mm \| 5753 m² \| 289.25 m³** ✓. 1458 also equals my six wall-type counts added up | 0 | 3 | 54 (12) |
| s06 (Aras Bumbung, no doors) | Right | `schedules made: 0` / `TOTAL 0 doors on Aras Bumbung` / `SKIP no doors on Aras Bumbung` | none | none made (checked in the Project Browser) | "No door schedule was made: level Aras Bumbung has 0 doors." | 0 | 3 | 7 |
| s03 (walls by level, the largest) | Right | `MADE Wall Schedule \| 1458 walls … fields: Type, Base Constraint, Volume \| grouped by Base Constraint` / `TOTAL Volume 289.25 m³` / `GROUP Aras 01: 828 walls \| 173.66 m³` / `GROUP Aras 02: 255 \| 90.33 m³` / `GROUP Aras Langkan: 7 \| 1.37 m³` / `GROUP Aras Tanah: 368 \| 23.89 m³` | none | grouped, with headers, volume footers and a grand total | the groups add up to 1458 and 289.25. Checked in the schedule: the Aras 02 footer **90.33 m³** ✓ and the grand total **1458 \| 289.25 m³** ✓. The other three group footers were not scrolled to | 0 | 2 | 10 |
| s09 (Malay question, total only) | Right | `NOTHING MADE: totals only` / `TOTAL 1458 walls` / `TOTAL Volume 289.25 m³` | none | **no schedule made** (Project Browser) | "Jumlah isipadu semua dinding ialah 289.25 m³ bagi 1458 dinding, tetapi dinding yang isipadunya tidak dikira oleh Revit tidak termasuk dalam jumlah itu." It gives no count of uncomputed walls | 0 | 2 | 7 |

- Units and rounding: volume has 2 decimals in m³, area 0 decimals in m² (rows show "0 m²"), length in mm. They match the schedule.
- The page's `<Door Count>`: my per-type count covered the 10 door types shown in Plan A (58 doors). The schedule shows 62, including four `PT2p` doors (900 × 2325) and two on Aras 02.

## Part F: the brochure fill (attached through the paperclip, which worked)

- **Attaching:** the paperclip opened the "Attach file(s)" dialog after about 5 s. I typed the full path into "File name" and pressed Enter, and the chip "PCO_brou… PDF document" appeared.
- **Before values** (1X13W, "Normal"): Wattage Comments 1X13W, Kadaran_Arus 0.00 A, Kadaran_Kuasa 13.00 W, Kadaran_Voltan 0.00 V, Voltage 0.00 V. Model, Manufacturer, Type Comments, URL and Description are empty.
- The stored fill script (`run_revit_code` with playbook `fill-from-document`) was called in **t04 only**.

| Prompt | Right or wrong | Stored script? | Values written (parameter = value, page) | Wrong value written | Questions | Wide-write card | Reply as shown | Failed | Steps | Seconds |
|---|---|---|---|---|---|---|---|---|---|---|
| t01 (plain, Malay) | **Wrong** (tool error, turn **failed**) | no | none | no | none | no (nothing reached it) | see below | 4 | 5 | 12 |
| t03 (601 / 8 / 840 does not exist) | Right | no | none | no | the model's own form (English): which 1X8W family (3 families matched "1X8W") and which brochure row [601/10/840] [601/10/865] [Skip]; answered c_dl + Skip | no | see below | 1 | 6 | 109 (21) |
| t04 (Voltage is not in the brochure) | **Wrong: a wrong value was written** (wrong target) | **yes, ran in Revit**; its plan said both fields are "not on the family" (wrong) | Kadaran_Arus_jkr_stec = 0.04 A (page 2) on **10 lighting types**, not only the 2 downlights. Voltage unchanged (right) | **yes**: 0.04 A on 8 other families, 197 instances (2X14W fluorescent ×126, 1X28W ×22, …) | wide-write card, then the model's own "how should I fix it?" card | **yes**: "2 changes reach 470 elements" | see below | 2 | 9 | 240 (39) |
| t10 (typed values, no brochure) | Right on values | no | both downlight types (52 instances): Wattage Comments 1X10W, 0.04 A, 10.00 W, 230.00 V, 230.00 V (source: the prompt) | no | none, apart from the wide-write card | **yes, and in Malay** | see below | 1 | 4 | 64 (20) |
| t02, t05 to t09 | not done (four runs per job as briefed) | | | | | | | | | |

**t01, what happened in order:**
1. The model called `lookup {"id": "playbook:fill-from-document"}`. It got knowledge entries back (a duplicate-values warning and JKR rows), not the playbook.
2. It called `inspect` with malformed arguments. The error was `"arguments are not valid JSON"`.
3. It sent three `set_parameters` calls:
   - Model "Adrino LED 601/10/840", page 2
   - Description "LED Recessed Downlight", from the prompt
   - URL "www.pcolite.com", page 8

   Each came back with `"target: Additional properties are not allowed ('category', 'type' were unexpected)"`.
4. The turn stopped with `{"code":"repeated_errors","message":"Stopped after the same tool error repeated. Say it another way or narrow the request."}`.
5. The reply: "Panggilan set_parameters saya ditolak kerana format sasaran saya tidak sah, jadi Model, Description dan URL belum ditulis. …"
6. The pane showed a **green "Done" chip on this failed turn**.

The values it meant to write match the brochure, except that Model kept the prompt's spacing ("601/10/840") instead of the brochure's "601 / 10 / 840".

**t03 reply:** "Ikut pilihan anda, tiada apa diubah: 14 instance jkrEL_lit-fx_LED_c_dl_1X8W dibiarkan, Model_jkr_sit kekal kosong dan Kadaran_Kuasa_jkr_stew kekal 8 W. Brosur hanya ada Adrino LED 601 / 10, / 15, / 20 (840 dan 865), tiada baris 8 W. …" It reads "Model" as `Model_jkr_sit`, an instance parameter, not the type parameter Model. One form label read "14 types instances".

**t04, step by step (the worst result of the round):**
1. **The stored script ran** with `inputs {types ["downlight"], product "Adrino LED 601 / 10 / 840", fields ["Voltage","Kadaran_Arus_jkr_stec"]}`. It read `FOUND downlight | jkrEL_lit-fx_LED_c_dl_1X13W: Normal`, `FOUND downlight | …1X8W: Normal` and the GROUP ids.
2. **The backend's plan** came back as `NOTHING MADE: nothing to fill` / `SKIP Voltage: not on the family` / `SKIP Kadaran_Arus_jkr_stec: not on the family`. That is wrong: both are type parameters of both families, as Type Properties shows.
3. **The model searched by type name.** It looked up type "jkrEL_lit-fx_LED_c_dl_1X13W: Normal" and "…1X8W: Normal", and each search matched **235** fixtures, every family whose type is called "Normal".
4. **The team's card:** "Before I write: 2 changes reach 470 elements. • Kadaran_Arus_jkr_stec = 0.04 A: 235 • Kadaran_Arus_jkr_stec = 0.04 A: 235". I answered Write to all, as the brief says.
5. **The write:** "Set Kadaran_Arus_jkr_stec = 0.04 A on 10 types; read back 10 of 10." **Checked in Revit:** `jkrEL_lit-fix_flou_r_aplr_2X14W` (28 W, 240 V, a fluorescent) now reads 0.04 A. 1X13W reads 0.04 A (right) and Voltage 0.00 V (unchanged, right).
6. **The strip** read "10/20 done · 10 unchanged".
7. **The model then asked:** "Type names are all "Normal", so the set_parameters write hit 10 Lighting Fixture types, not just the 2 downlights. How should I fix it?" I answered **"Undo that batch in Revit and stop there"**.
8. **It ignored the answer** and called `set_parameters` with value "0 A" and source `{"kind":"prompt","quote":"revert"}`. That was refused: `"Not written: '0 A' is not printed for Adrino LED 601 / 10 / 840 (page 2). Leave Kadaran_Arus_jkr_stec empty and tell the drafter it is not in the brochure; never try another value."`
9. **Its report:** "Revert was rejected by the tool, so 10 Lighting Fixture types still read 0.04 A — press Ctrl+Z once in Revit to undo that batch." It named Voltage as not printed in a check. **The reply shown** was the fallback "Set Kadaran_Arus_jkr_stec on 10 elements. 1 failed."
10. **After the turn**, 2X14W still read 0.04 A. Reopening the model without saving discarded it.

**t10:**
- The wide-write card: "Sebelum saya tulis: 5 perubahan menjangkau 260 elemen. • Wattage Comments = 1X10W: 52 • …" [Tulis kepada semua (260 elemen)] [Batal, jangan tulis apa-apa]. It counts 5 parameters × 52 instances, as the page predicted. The page says this card should not appear for 52 elements, and the owner's rule is that the question form is English only.
- Each write read back "on 2 types; read back 2 of 2" (1X8W, 1X13W).
- 1X8W, checked in Type Properties: 1X10W, 0.04 A, 10.00 W, 230.00 V, 230.00 V. My Type Properties check of 1X13W did not open, so that type rests on the tool's read-back.
- The reply shown was the fallback "5 parameter ditetapkan pada 2 elemen." It counts types as "elemen".

**Wrong values written silently: 0. Wrong values written: 1 batch** (t04: 0.04 A on 8 other families). The model said so afterwards, but did not undo it when asked to.

## Ran in Revit: yes / refused / error

| Job | Ran in Revit | Error text |
|---|---|---|
| Tagging | **yes**, 13 runs, no refusal or error (round 7's refusal is gone) | — |
| Dimensions | **yes**; **error** in d03 | timeout at line 27, quoted above |
| Change type | **yes**; **error** in c07 | timeout at line 148, quoted above |
| Schedule | **yes**, no error | — |
| Brochure fill | **yes** in t04 (read-only script). Its plan was wrong, and the model never called it in t01, t03 or t10 | — |

## What scripts got wrong in Revit that offline tests could not see

1. **Dimensions: the overall strings are always skipped.** The script prints `SKIP overall … already dimensioned` for strings it was about to make, because the chains made earlier in the same run already count as existing. Line 27's `have` is a lazy query and is re-run at line 47. So E = 4 became 2, in d01 and d04 alike.
2. **Dimensions with walls time out.** "Grids and walls" ran 95 s, against a limit reported as "30 s", and was stopped at lines 27, 47 and 52 (38632 steps). The same lazy `have` query is re-run inside the loop.
3. **Dimensions: the type list is ambiguous.** Three loaded dimension types are listed under the same label, "Linear Dimension Style".
4. **Change type is too slow for 205 walls.** Wall `ChangeTypeId` in a loop runs past 30 s, both for the stored script (line 148) and for the model's own filtered code. More than 100 walls cannot be changed today.
5. **Change type: the two type lists differ.** The "new type" list held 12 types, including the sd-ky and "@PT00" types, but **not** `(PTt760b) 3700 x 2100 sd-d` (12 doors in the model). The "old type" list held 11 types, including PTt760b.
6. **Brochure fill: the plan says the asked fields are "not on the family".** Voltage and Kadaran_Arus_jkr_stec are type parameters on both downlight types.
7. **Tagging: the first stored-script run after Revit starts takes about 47 s** (727 steps). Later runs take 2 s.

## Anything new or worse

- **A wrong value was written to other families (F-t04).** A type name shared by many families ("Normal") let one `set_parameters` reach 10 types. The wide-write card showed the counts (235 + 235) but not which families. When the drafter chose "Undo that batch", the model tried to write "0 A" instead.
- **Change type asks twice (c07).** The backend asked "Which scope? Whole model (205 walls)", then the team's wide-write card asked again about the same change, once the model had switched to its own code.
- **The wide-write card speaks Malay** for a Malay prompt (F-t10).
- **The fallback reply still replaces good model sentences.** Seen in D-t04 ("58 objects created."), F-t04 and F-t10. D-t02 showed the model's Malay sentence this time.
- **A failed turn shows a green "Done" chip** (F-t01), as in round 7's t06.
- **The change-type result card counts "other 2"** for 20 doors changed (c01).
- **The schedule job's question came from the model, not the backend** (s07). Its options include Rooms; whether the model has rooms was not checked.
- **The model calls tools with bad arguments.** It produced malformed JSON (`{"ids" string="false">[…]}`) twice in Part F, and used `group_by` and `ids` where they are not allowed.
- **Hidden question left open by tagging:** a second "tag all doors" message is answered from the chat with no read, so it still claims the tags "were added" (t07). For dimensions the same pattern gives a right answer (d07).
- **Project Browser:** a double-click on a view's name can start a rename. I cancelled with Esc, and no name changed. My driver now double-clicks the view icon.
- **One stray click** hit the ribbon's Mullion button: "No Curtain Wall Mullions family is loaded …" I answered No; nothing was loaded.

## What is still running or changed on the PC

Nothing is restored yet.

- **Revit** is open on `downlight-model.rvt` with F-t10's values in memory only, not saved. It runs add-in 0.0.101 through the task `BinaLiveTestR7`.
- **The add-in config** `%APPDATA%\RevitWebAppSync\config.json` is as round 7 left it: it points at `http://127.0.0.1:18007` with the local test token. The backup is `config.json.bak-20261113-r7-before-18007`.
- **On the PC, from round 7:** the task `BinaLiveTestR7`, `C:\Users\Public\rel-test\r7\open.cmd`, the folders `r7-addin-0.0.101` and `r7-addin-0.0.102`, and `addin-0.0.101.tgz` and `addin-0.0.102.tgz`.
- **On the PC, new this round:** `C:\Users\Public\rel-test\brochure\`, holding the client's PDF and `downlight-model.rvt`. They went nowhere else.
- **On the Mac:** the reverse tunnel PC:18007 → Mac:8005 (ssh) is still up. The backend on 8005 runs from the worktree `lh/lt-r2`, now detached at `3302ee55` (it was at `3c537c8d`).
- **Not touched:** the backend on the PC's port 18002, and the installed add-in folders 0.0.117 and 0.0.118. `windows-mcp-server` was already running; I did not start or stop it.

A restore would close Revit without saving, put back the config backup, delete the task, the `r7*` folders, the tgz files and the `brochure` folder, and stop the tunnel and the backend.
