# Live test round 10, 13–14 Nov 2026 (binacloudpc): stopped by a blue screen

**No value was written to a type the drafter did not name.** The brochure fill wrote only to the types asked for, in all three runs (F-t04 twice, F-t01).

The round ended at 00:39 on 14 November, when the PC **blue-screened**: bugcheck `0x4E`, dump `111426-50265-01.dmp`. That was the second unclean restart in 25 minutes. As the rules say, I stopped all input and did not try again.

| | |
|---|---|
| Runs done | 12 in total: 3 on add-in 0.0.103, 9 on 0.0.104 |
| Right | 10 |
| Wrong | 2 (E-c07 rolled back, E-d03 bad output) |
| Not done | 9 (listed at the end) |

There was one run per prompt, so nothing here is a rate. "Consistent with fixed" means one run behaved the way the fix intends.

## Header

| | |
|---|---|
| PC | binacloudpc, driven from the Mac through Windows-MCP (port 48820) and SSH. Revit 2027.3 (Education) |
| Builds, first part (19:21–20:16) | add-in Debug **0.0.103** (`f15d646a` exactly) and backend **`2f331aa1`**. Runs: F-t04, F-t01 |
| Builds, second part (23:20–00:38) | add-in Debug **0.0.104** (`a47b1018` exactly) and backend **`02d3541e`**. Runs: F-t04 (again), E-c07, E-d01, E-d07, E-d03, E-d09, E-c01, E-s07, D-t07 (two messages; the first equals D-t01), G1 |
| How the add-in was built | each in a detached worktree with the staging update feed, copied to a new folder `C:\Users\Public\rel-test\r10-addin-0.0.10x`, and loaded through `BINA_SYNC_PLUGIN_DIR` (task `BinaLiveTestR7`). The loader log showed "loaded 0.0.103.0" / "loaded 0.0.104.0" before each run. The pane menu showed "Version 0.0.103". The 0.0.101 folder is kept |
| Backend | local bina-ai, worktree `lh/lt-r2`, detached, on Mac `127.0.0.1:8005`, through a reverse tunnel to the PC's port 18007. Neither `2f331aa1` nor `02d3541e` has a migration or dependency change from `3302ee55` |
| Fixtures | `downlight-model.rvt` with `PCO_broucher_Downlight_2026.pdf`, and `sheets-test.rvt`. Each run reopened the model without saving. **No model was saved** |
| Exports | turn rows and events, screenshots and `notes.md` are in `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/b8bbfc99-95cf-49ed-a7f8-32e08adf1161/scratchpad/runs/R10/` |

## Interruptions (facts)

| Time | What happened |
|---|---|
| 19:39–20:10 | Windows-MCP refused input with `USER_CONTROL` / `CONTROL_UNAVAILABLE`. The F-t01 prompt was typed into the Attach dialog's File name box; nothing was sent. At 20:10 the screen showed only my windows. This was before the Mac slept, and the cause is unknown. A RustDesk viewer is the likely cause: one was seen connected at 21:42 |
| 20:23:55–23:18:02 | **The Mac slept**: its lid was closed on battery ("Clamshell Sleep", from `pmset -g log`). It woke only for short maintenance wakes. Runs hung, the reverse tunnel dropped, and OrbStack's Postgres stopped answering. The F-t03 attempts (20:21–21:36) and the first E-c07 attempt (22:06) never reached the backend. I wrongly blamed a person on the PC at first; it was the Mac |
| 23:18 | Lid opened. The containers restarted by themselves. I restarted the tunnel and moved the backend to `02d3541e`. From here a check ran before every run (the tunnel exists, a DB query answers, no new sleep), and `caffeinate` ran |
| short, while Revit started | `USER_CONTROL` for a few seconds whenever Revit started (19:24, 20:12–20:16, 23:24, 00:38). The guard waited each time |
| **~00:15** | **First PC restart**, during D-t07's reopen. Boot 00:16:42; Kernel-Power 41 ("rebooted without cleanly shutting down"). **No minidump.** Ashraf signed in at 00:18 and said to continue ("bro cont the testing, pc restart just now"). I started the tunnel again |
| **~00:39** | **Second PC restart, a blue screen**, during F-n1's reopen of `downlight-model.rvt`. Boot 00:39:21. Event 1001: "rebooted from a bugcheck. The bugcheck was: 0x0000004e (0x98, 0x6f0196, 0x5560001, 0x6f0196)", dump `C:\WINDOWS\Minidump\111426-50265-01.dmp` (00:40:02). At 00:49 nobody was signed in. **I stopped and did not try again** |

Both restarts happened while the driver was killing Revit and starting it again. The 13 November crash was bugcheck `0x133` with the same driver.

## Part F: the brochure fill

| Run (build) | Round 9 / round 10 | Stored script | Values written (checked in Revit) | Wrong value | Questions | Wide-write | Reply as shown | Failed | Steps | Seconds |
|---|---|---|---|---|---|---|---|---|---|---|
| F-t04 (0.0.103) | **Wrong** (0.04 A on 10 types) / **Right** | `fill-from-document`, code starts `// inputs` (52 lines). Read **not cut** (`truncated: false`, 36654 steps) | Kadaran_Arus_jkr_stec = 0.04 A on 1X13W (38) and 1X8W (14) only. **Type Properties:** 2X14W, 1X28W and 2X18W read 0.00 A; Voltage is 0.00 V on both downlights | no | none | did not appear (right) | "Kadaran_Arus_jkr_stec = 0.04 A set on both downlight types (1X13W and 1X8W); Voltage was skipped, not in the brochure." | 0 | 3 | 9 |
| F-t04 (0.0.104) | / **Right** | same; read not cut (37185 steps) | same two types. **Type Properties:** 2X14W 0.00 A, 1X8W 0.04 A | no | none | did not appear | "Kadaran_Arus_jkr_stec = 0.04 A set on both downlight types (38 + 14 instances); Voltage was skipped: the brochure prints no supply voltage." | 0 | 3 | 21 |
| F-t01 (0.0.103) | **Wrong** (turn failed) / **Right** | `fill-from-document` (52 lines), not cut | on 1X13W only: Model "Adrino LED 601 / 10 / 840" (page 2), URL "www.pcolite.com" (page 8), Description "LED Recessed Downlight" (prompt). **Type Properties:** 1X13W holds all three; 1X8W's are empty | no | none | did not appear | "Type parameter Model, Description dan URL bagi type Normal (jkrEL_lit-fx_LED_c_dl_1X13W) sudah diisi." | 1: `lookup {"playbook":"fill-from-document"}` → `"arguments: Additional properties are not allowed ('playbook' was unexpected)"`. Next the model called `lookup {"id":…}`, which worked (fixed in backend `02d3541e`, not re-run) | 5 | 15 |
| F-t03, F-t10, F-n1, F-n2, F-n3 | **not done**: the Mac slept, then the blue screen | | | | | | | | | |

In every fill, the backend sent the writes itself ("PLANNED n writes: the backend sends them now").

**Result card, 0.0.103 (F-t04):**
- Chip: "Read back from Revit: 38 of 38 match". Wrong: 52 fixtures changed.
- "TYPES WRITTEN 38 elements" with one row, "Normal 38 elements". No family is named and the 14 of 1X8W are missing.
- "Written as asked · 2": two rows, "Kadaran_Arus_jkr_stec 0.04 A, was 0 A, page 2", with no type named.
- "Check 4 things": Voltage not in the brochure, once per type (×2); a SKIP line; the per-type instance counts.
- In-model popup: "38 elements in 3 views".
- F-t01's card (Malay) had the same shape: "Dibaca balik dari Revit: 38 daripada 38 sepadan"; one row "Normal 38 elemen"; "Ditulis seperti diminta · 3".

**Result card, 0.0.104 (F-t04):**

| Expected | Seen | |
|---|---|---|
| chip "2 of 2 types match · affects 52 Lighting Fixtures" | "Read back from Revit: 2 of 2 types match · affects 52 Lig" | **cut off at the pane's edge, not wrapped** |
| two "Family: Type" rows, 38 and 14 | "TYPES WRITTEN 52 elements" with two tiles, "jkrEL_lit- / fx_LED_c_dl_1X13 / W: Normal · 38 elements [Select]" and "jkrEL_lit- / fx_LED_c_dl_1X8W / : Normal · 14 elements [Select]" | right; the long names wrap mid-word ("1X13 / W", "1X8W / : Normal") |
| each "Written as asked" list names its type | **one** list only: "Written as asked · 1 / For jkrEL_lit-fx_LED_c_dl_1X13W: Normal", with Kadaran_Arus 0.04 A, was 0 A, page 2 | **1X8W's list is missing**. One click on the 1X8W tile did not switch it |
| one Voltage check "(2 types)" | "Check 1 thing: Voltage: not in the brochure for Adrino LED 601 / 10 / 840 (2 types)" | right |
| source page 2 | "PCO_broucher_Downlight_2026.pdf, page 2" | right |
| in-model key shows two lines | "52 elements changed / in 3 views; 52 in this view / Changed type jkrEL_lit-fx_LED_c_dl_1X13 · 38 / Changed type jkrEL_lit-fx_LED_c_dl_1X8 · 14 / In 2 other views 50 / Showing 52 here · 52 not in this view" | two lines, right; **the names are cut** ("…1X13", "…1X8"), and the last line contradicts itself |

## Part E (0.0.104, `sheets-test.rvt`)

| Run | Round 9 / round 10 | Script lines | Questions | Count in Revit | Reply as shown | Failed | Steps | Seconds |
|---|---|---|---|---|---|---|---|---|
| E-c07 | Wrong (timeout) / **Wrong (Revit rolled back)**, no timeout | `change-type` (210 lines). Preview 3 s; commit run 15 s, then `rolled_back` (text below) | backend: "Which scope? / Which elements should change type?" [Whole model (205 walls)] [Skip], although the prompt said "in the whole model". Answered Whole model. **No wide-write card** | 205 / 99 unchanged (Select All Instances). Ctrl+Z not needed | "Nothing changed: all 205 walls rolled back on 10 join errors and invalid dimension references." | 1 | 5 | 56 (30 working) |
| E-d01 | Wrong (2 of 4) / **Right** | `MADE 4 strings` / DIM grids 1–13 (12 spans) / DIM overall 1–13 / DIM grids G–A (6 spans) / DIM overall G–A | none | Filter: Dimensions 4, total 1789 (baseline 1785) | "Made 4 grid dimension strings in Aras Tanah (01w_WIP): 12 spans 1–13 plus overall, and 6 spans G–A plus overall." | 0 | 2 | 7 |
| E-d07 (2nd message) | Right (answered from the chat) / **Right**, and **the script ran** | `dimensions made: 0`, 4 × `SKIP … already dimensioned` | none | Dimensions 4, total 1789 | "Nothing new to make: all 4 grid dimension lines (1 to 13, G to A, plus overalls) already exist in Aras Tanah (01w_WIP)." | 0 | 2 | 5 |
| E-d03 | Wrong (timeout) / **Wrong (bad output)**, no timeout | `MADE 6 strings`: grids and overall as in d01, plus **DIM walls 1 to 13: 1424 spans** and **DIM walls G to A: 1486 spans**; SKIP 12 walls "not straight". 47795 steps. **The script ran 410 s** (15:50:57 → 15:57:47 UTC), past the 120 s limit, and was not stopped | none | 6 strings; the plan is covered in a dense mesh of dimension lines (`E-d03-view.png`) | "Made 6 dimension strings in Aras Tanah (01w_WIP): grids and walls 1–13 and G–A, plus overall strings." | 1: the model's first call `"arguments are not valid JSON"` | 3 | 417 |
| E-d09 | Right; three identical labels / **Right** | `ASK dimension type not found: ZZ9-NODIM ?` | backend: "Dimension type ZZ9-NODIM is not in the model / Which dimension type should I use?": Diagonal - 2.5mm Arial · **Linear Dimension Style #277 · #288 · #23763** · Skip. Answered Skip; it needed a second click on "Continue" | none made | "Skipped: dimension type ZZ9-NODIM is not in the model, so no grid dimensions were made." | 0 | 3 | 212 (5 working) |
| E-c01 | Right; card "other 2" / **Right** | `types changed: 20` / `MADE 20 doors changed to … (PTr680a)` / `FROM … (PTh300a): 20` | none | PTh300a **0**, PTr680a **28** | "20 doors changed from …(PTh300a) 750 x 2100 sd-d to …(PTr680a) 900 x 2100 sd-a in the whole model." | 0 | 2 | 8 |
| E-s07 | Right; asked by the model / **Right; asked by the backend** | `schedule` (313 lines): `ASK which category?` → `MADE Wall Schedule \| 1458 walls`, TOTAL Length 2333013 mm, Area 5753 m², Volume 289.25 m³ | **backend** card "Which category? / Which category should the schedule list?" with a filter box: Doors, Windows, Walls, Floors, Ceilings, Roofs, Casework, Plumbing Fixtures, Pipes, Generic Models, Railings. No Rooms, no Skip. Answered Walls + Continue | totals as in round 9 (checked then against the schedule's grand total; not reopened this time) | "Wall Schedule made for 1458 walls: Length 2333013 mm, Area 5753 m², Volume 289.25 m³." | 1: the model's own question, refused by the backend (text below) | 5 | 80 (25 working) |
| E-s01, E-c05, E-c04 | **not done** (blue screen) | | | | | | | |

**E-c07 rollback, as the model received it:**

> `Revit error — Can't keep elements joined.; One or more dimension references are or have become invalid.; Can't make type "jkrAR18_dor_PTm_Aa11c2_ex_BGH_(PTm480)-3 Dua Daun Ram Tetap : (PTm480a) 1000 x 2100 sp-d".; Can't make type "jkrAR18_dor_PTt_Aa11d6_ex_DWN_(PTt760)-3  Lipat Separa Kaca dan Ram Kayu Tetap (6 Daun) : (PTt760b) 3700 x 2100 sd-d". Revit rolled the change back; nothing was changed.`

- **What the model did next:** a knowledge lookup on the warnings, then its report. It did not write its own code, and no time-limit message appeared.
- **The card:** chip "Check receipt"; "Not every change in this turn was applied. Check the receipt before relying on the reply."; "16 elements fail"; "1 kind of issue · 16 fixable automatically"; one row "Can't keep elements joined. error · 16 elements [Show]".
- **Problems:** the reply says 10 join errors and the card says 16. The reply does not say what the drafter can do.

**E-s07 refusal of the model's own question:**

> `Not asked: the drafter asked for schedule, a job with a stored script, and its script asks what is open itself (live s07: a question asked first offered a category the script does not support). Run it now: run_revit_code, playbook "schedule", inputs only from the drafter's words, the rest left out.`

**E-d01 card:**
- A false warning: "Revit raised 1 warning / The change was saved. Check what Revit flagged below in the model. • The code made 4 additions; the assistant expected 5."
- "Check 1 thing: Dimensions use type Diagonal - 2.5mm Arial, the view's default; check it matches your standard."

**E-c01 card:**
- No "other 2" any more.
- One row only, named by the **old** type: "jkrAR18_dor_PTh_…(PTh300a) 750 x 2100 sd-d · 20 [Select them in Revit]". There is no old → new row.
- In-model key: "20 jkrar18_dor_pth_aa11a1_in_tds_(pth300)-3 compact laminate: (pth300a) 750 x 2100 sd-d changed / in Aras Tanah (01w_WIP), this view / Changed jkrar18_dor_pth_aa11a1_in_tds_ · 20 / Showing all 20". It is **all lower case**, named by the old type, and covers all 20.

## Part D (0.0.104)

| Run | Round 9 / round 10 | Script | Questions | Tags in Revit | Strip | Reply as shown | Steps | Seconds |
|---|---|---|---|---|---|---|---|---|
| D-t07, 1st message (= D-t01) | Right / **Right** | `tag-all-in-view` (155 lines): `ASK which tag type?` → `MADE 58 Doors tags … (t193c)` + 10 TYPE lines | backend "Which tag type?", 3 types; picked (t193c) | — | **green "58/58 done"** (round 9: "50/50") | "Made 58 door tags in Aras Tanah (01w_WIP) with type (t193c) sc1-100 tx1.8mm r2.5mm KKP." | 4 | 62 (22 working) |
| D-t07, 2nd message | **Wrong** (answered from the chat) / **Right** | **the script ran**: `tags made: 0`, `SKIP 58 already tagged` | none | Filter: Door Tags 58, total 1843 (nothing doubled) | — | "All 58 doors in Aras Tanah (01w_WIP) are already tagged, so nothing was added." | 2 | 5 |
| D-t04 | **not done** | | | | | | | |

On the card of the first message, the (PTh300a) 20 row has no "Select them in Revit" button; the other rows have one.

## Part G (0.0.104)

| Run | Result |
|---|---|
| G1 (Malay prompt, Cancel) | **Right.** `find_elements` found 205 walls. The team's question appeared **once**: "Before I write: Comments = semak on 205 elements." [Write to all (205 elements)] [Cancel, write nothing]. The question and buttons are English and say 205 once. **But its frame is Malay**: a badge "Perlu persetujuan anda" and the footnote "Copilot hanya bertanya apabila penulisan lebih luas daripada yang arahan nyatakan, memadam elemen, atau menjalankan kod. Pengisian biasa terus dilaksanakan dengan Buat asal." I answered Cancel. The write was refused: "The drafter chose not to write. Nothing was changed." No write call ran in Revit; the Comments of the 205 walls were not checked one by one. Reply: "Penulisan ditolak, jadi tiada apa yang berubah: 205 dinding jenis itu kekal tanpa Comments "semak". Beritahu saya jika mahu saya cuba sekali lagi." 3 steps, 45 s |
| G2 | **not done** |

## Round 9's findings

| Round 9 finding | Round 10 |
|---|---|
| F-t04: 0.04 A written to 8 other families (shared type name "Normal") | **consistent with fixed**, twice (0.0.103 and 0.0.104), checked in Type Properties |
| F-t04: the plan said the fields are "not on the family" | **consistent with fixed** |
| F-t04: the fill read was cut | **consistent with fixed** |
| F: the fill playbook was not called (t01, t03, t10) | **consistent with fixed** in t01; t03 and t10 not tested |
| F-t01: the turn failed, and a green Done chip showed on it | **consistent with fixed** |
| F-t01: Model kept the prompt's spacing | **consistent with fixed** ("601 / 10 / 840") |
| F-t04: the drafter's "undo that batch" was ignored | not tested (no wrong write happened) |
| F-t10: the wide-write card was in Malay, appeared for 52 fixtures, and the reply said "2 elemen" | not tested. Related, in G1: the question is English now, but its frame is still Malay |
| D-t01: the strip said 50/50 for 58 | **consistent with fixed** ("58/58 done") |
| D-t07: answered from the chat, never said "already tagged" | **consistent with fixed** |
| D-t04: fallback "58 objects created." | not tested. The model's own sentence showed in every run this round |
| E-d01/d04: the overall strings were always skipped (2 of 4) | **consistent with fixed** (4) |
| E-d07: answered from the chat | consistent with fixed: the script now runs and skips |
| E-d03: timeout at 30 s | **no timeout, but not fixed**: it ran 410 s, past the 120 s limit, unstopped, and the wall strings are unusable (1424 and 1486 spans) |
| E-d09: three options with the same label | **consistent with fixed** (#277, #288, #23763) |
| E-c07: timeout, asked twice, the model wrote its own code | **timeout gone, asked once, no own code, no wide-write card**. But the change **did not complete**: Revit rolled it back |
| E-c01: card "other 2"; highlight on 2 elements | **consistent with fixed** (one row, 20; highlight on all 20). The row names only the old type, not old → new |
| E-c05/c04: the two type lists differ | not tested |
| E-s07: the category question came from the model | **consistent with fixed** (the backend's card; Rooms gone) |
| E-s01: right (62) | not tested |

## New or worse

1. **The PC blue-screened** (`0x4E`, dump `111426-50265-01.dmp`) and had another unclean restart 23 minutes earlier, with no dump. Both happened while Revit was being killed and restarted.
2. **E-d03:** the wall dimension strings are useless (1424 and 1486 spans), and the stored script ran 410 s **without being stopped** at the 120 s limit. The pane sat on "Previewing the change in Revit…" for 7 minutes.
3. **E-c07:** the change of 205 walls is rolled back by Revit: join errors, invalid dimension references, and two door types that "can't make type". So the job still cannot change these walls. The reply says 10 errors where the card says 16, and it gives the drafter no next step.
4. **The result card in 0.0.104 (F-t04):**
   - the chip is cut at the pane's edge;
   - only one type's "Written as asked" list is shown;
   - the in-model key cuts the type names;
   - the key's last line reads "Showing 52 here · 52 not in this view".
5. **The E-d01 card warns falsely:** "The code made 4 additions; the assistant expected 5."
6. **The E-c01 key is all lower case**, and the card row names only the old type.
7. **G1:** the question is English, but its badge and footnote are Malay.
8. **E-c07:** the backend asks "Which scope?" although the prompt said "in the whole model".
9. **E-d09 / E-s07:** a single-choice form needs a second click on "Continue" after picking.
10. **The model still sends malformed arguments now and then** (E-d03: "arguments are not valid JSON"). It recovered each time.
11. **The Mac test setup:** with the lid closed on battery, the Mac sleeps, and the tunnel and the DB fail silently while the backend's `/health` stays 200. The driver now checks the tunnel, a DB query and `pmset` before each run.

## Not done

F-t03, F-t10, F-n1, F-n2, F-n3, E-s01, E-c05, E-c04, D-t04 and G2.
- F-t03 was lost to the Mac sleeping. The others were not reached before the blue screen.
- E-d09 was answered with Skip only; there was no time to pick each label.

## What is still running or changed

Nothing is restored yet.

- **PC:**
  - After the blue screen, nobody is signed in and Revit is not running.
  - The task `BinaLiveTestR7` points at `r10-addin-0.0.104` and `downlight-model.rvt`.
  - New this round: the folders `r10-addin-0.0.103` and `r10-addin-0.0.104`, and `addin-0.0.103.tgz` and `addin-0.0.104.tgz`.
  - From earlier rounds: `r7`, `r7-addin-0.0.101`, `r7-addin-0.0.102`, their tgz files, the `brochure` folder, and the add-in config pointing at port 18007 (backup `config.json.bak-20261113-r7-before-18007`).
  - Not touched: port 18002, and the installed 0.0.117 and 0.0.118 folders.
- **Mac:**
  - The backend on 8005 runs `02d3541e` from `lh/lt-r2`.
  - The reverse tunnel's ssh process is gone (it died with the PC).
  - `caffeinate` runs (pid 34050, at most 6 hours).
  - The build worktree `…/scratchpad/addin-build` (detached at `a47b1018`) is registered in the add-in repo's worktree list.
