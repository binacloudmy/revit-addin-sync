# Live test round 11, 14 Nov 2026 (binacloudpc)

**No value was written to an element or type the drafter did not name.** The worst result was F-s1 on backend `c22e1de5`: it wrote Type Comments to the type of the selected lamp, changing all 38 lamps of that type, without asking first. On backend `6463725b` the same prompt asked "Change all 38 instances?" first, both times.

The PC stayed up for the whole round (02:17–04:55), and the Mac did not sleep.

There was one run per prompt, so nothing here is a rate. "Consistent with fixed" means one run behaved the way the fix intends.

| | |
|---|---|
| Runs done | 26: 6 on `c22e1de5`, 20 on `6463725b`. E-d03, F-s1 and F-t04 ran on both backends. E-c01's first send never reached the chat, so it was sent again |
| Right | 18 |
| Wrong or not as expected | 7. On `c22e1de5`: E-d03 (walls dropped), F-s1 (no question). On `6463725b`: F-n3 (neither put back nor told to Undo), F-t10 (right values, but the playbook did not run), SP-9 (slope not set: "read-only"), F-t04-B (right in the end, by a new and worse path), E-c04 (its type list differs from E-c05's) |
| Still wrong for the same reason as round 10 | 1. E-c07: the question is right now, but Revit rolls the change back (model data) |
| Not done | E-d03s |

## Header

| | |
|---|---|
| PC | binacloudpc, driven from the Mac through Windows-MCP (port 48820) and SSH. Revit 2027.3 (Education). Boot time **2026-11-14 01:51:02**. It did not change during the round. The newest minidump is still `111426-50265-01.dmp` (round 10's). Ashraf was signed in at 02:15. |
| Add-in | Debug **0.0.105**, built from `94f020cb` exactly, with the staging update feed. Copied to the new folder `C:\Users\Public\rel-test\r11-addin-0.0.105` and loaded through `BINA_SYNC_PLUGIN_DIR` (task `BinaLiveTestR7`). Before the first run, the loader log said "loaded 0.0.105.0" (02:21:00) and the pane menu said "Version 0.0.105". |
| Backend, part 1 (02:25–03:20) | **`c22e1de5`** (`feat/copilot-runnable-playbooks`): no migration or dependency change from `02d3541e`. Runs: E-d03, E-c07, F-t04, F-s1, E-c01, E-d01 |
| Backend, part 2 (03:22–04:55) | **`6463725b`** (11 commits later): no migration or dependency change from `c22e1de5`. Runs: F-s1, F-s1b, E-d03, F-t04, SP-3, SP-4, SP-6, SP-7, SP-9, F-t10, F-n3, E-d09, F-n1, F-n2, G2, D-t04, F-t03, E-c05, E-c04, E-s01. Another session (ningyo-ce) asked for this switch, and for the order, during the round. |
| Where the backend ran | Local bina-ai in worktree `lh/lt-r2` (detached), on the Mac at `127.0.0.1:8005`, reached through a reverse tunnel to the PC's port 18007. Before each run, a check confirmed: the tunnel was up, a DB query answered, there was no new Mac sleep, the PC's boot time and newest dump were unchanged, and the PC's `/health` returned 200. |
| Fixtures | `sheets-test.rvt`; `downlight-model.rvt` with `PCO_broucher_Downlight_2026.pdf`. Each run reopened the model without saving (except F-n3, F-n2 and SP-7, which followed in the same chat as the run before). **No model was saved.** |
| Exports | Turn rows and events (`<run>.json`, `<run>.events.json`), logs, `notes.md`, and screenshots under `shots/`, all in `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/d2ef876d-0a82-4eea-aa36-09b4b9471478/scratchpad/runs/R11/`. Runs on the second backend that repeat a prompt are named `-B` (`E-d03-B`, `F-s1-B`, `F-t04-B`). |

**Interruptions.** Windows-MCP refused input with `USER_CONTROL` / `CONTROL_UNAVAILABLE` in short bursts, each under 15 s; the guard waited them out. A RustDesk viewer on this Mac (192.168.100.81) was connected to the PC from 02:17. Two problems came from these bursts:

- The attach check failed twice (F-t04, F-t04-B). Both times the dialog was in fact open, and I attached the file by hand.
- The first E-c01 prompt was never sent: the chat box stayed empty during a burst at 03:09. I resent it on the same unchanged model.

## Results

| Run | Backend | Round 10 / round 11 | What happened | Steps, seconds, failed calls |
|---|---|---|---|---|
| **E-d03** | c22e1de5 | Wrong (1,424-span mesh, 410 s) / **Wrong, in a new way** | Fast, but the walls were dropped without saying so (details below) | 2 steps, 8 s, 0 failed |
| **E-d03-B** | 6463725b | / **Right** | Took the SKIP route the brief allows (details below) | 2 steps, 7 s, 0 failed |
| E-d03s | | / **not done** | I could not pick three parallel walls by mouse in the rotated, dense plan: points I tried hit roof, soffit, gutter, fascia and section marks, including with Tab-cycling. Tried for about 25 min, then moved on | |
| **E-c07** | c22e1de5 | Wrong (rolled back) / **question right; still rolled back** | Card and reply right; Revit still rolls the change back (details below) | 4 steps, 65 s (41 waiting), 1 failed (the rollback) |
| **F-t04** | c22e1de5 | Right / **Right values; card right; reply is the fallback** | Card defects fixed; reply shown is the fallback (details below) | 3 steps, 16 s, 0 failed |
| **F-t04-B** | 6463725b | / **Reply right; new problem on the way** | Reply right; the fill first wrote nothing and the model offered made-up voltages (details below) | 7 steps, 62 s (44 waiting), 1 failed |
| **F-s1** | c22e1de5 | new / **Wrong: written with no question first** | Wrote to the type, 38 lamps, without asking (details below) | 6 steps, 11 s, 1 failed (the refusal) |
| **F-s1-B** | 6463725b | / **Right** | Asked before writing; one type written after the answer (details below) | 5 steps, 53 s (42 waiting), 0 failed |
| F-s1b | 6463725b | new / **Right** | Same selection, same prompt, same card. Answered Skip, and the model made no further call. Reply: "Type Comments tidak diisi — perubahan jenis telah dilangkau, jadi tiada nilai ditulis." 1X13W Type Comments was still empty in Type Properties | 3 steps, 23 s, 0 failed |
| **E-c01** | c22e1de5 | Right / **Right** | `change-type` (210 lines), no question: "types changed: 20". Counted in Revit: PTh300a 0, PTr680a 28. **Card row:** "jkrAR18_dor_PTh_…(PTh300a) 750 x 2100 sd-d → jkrAR18_dor_PTr_…(PTr680a) 900 x 2100 sd-a · 20 [Select them in Revit]". **Key:** "Changed (PTh300a) 750 x 2100 sd-d → (PTr680a) 900 x 2100 sd-a · 20 / Showing all 20", in the names' real case. Small defect: the first Next chip is cut at its left edge ("iew the 20 doors' openings…") | 2 steps, 9 s, 0 failed |
| **E-d01** | c22e1de5 | Right / **Right** | 4 strings (12 + overall, 6 + overall). The card shows the 4 rows and **no "the assistant expected N" warning** | 2 steps, 6 s, 0 failed |
| **E-d09** | 6463725b | Right / **Right** | The card lists Diagonal - 2.5mm Arial, #277, #288, #23763 and Skip. **One click on Skip sent at once.** Reply: "Nothing made: ZZ9-NODIM is not in the model, and you chose to skip, so no grid dimensions were added." | 2 steps |
| **F-n1** | 6463725b | not done / **Right** | `find_elements` for type "jkrEL_lit-fx_LED_c_dl_1X13W: Normal" returned **38**, matching Revit's Select All Instances count. Reply: "38 light fixtures are of type jkrEL_lit-fx_LED_c_dl_1X13W: Normal, all on E-01 Aras Satu…" | 1 step |
| **F-n2** | 6463725b | not done / **Right count** | **235** (E-01 145, E-02 90). Reply: "235 lighting fixtures have type Normal, spread across 10 families…". It says 10 families but names none. Same chat as F-n1 | 1 step |
| **F-t10** | 6463725b | not done / **Right values; playbook did not run** | Right values on the 2 types; the model wrote by itself (details below) | 7 steps, 40 s, 1 failed |
| **F-n3** | 6463725b | not done / **Wrong** | Neither put back nor told to press Undo (details below) | 4 steps, 80 s (68 waiting), 2 refused |
| **G2** | 6463725b | not done / **Right** | The question appeared once, in English, with 205 once: "Needs your OK / Before I write: Comments = check on 205 elements." [Write to all (205 elements)] [Cancel, write nothing]. The footnote is English too. Answered Write to all: read back 205 of 205. In Revit, all 205 walls had Comments "check". **One Ctrl+Z** brought them back to `<varies>`, which matches the fresh model | 3 steps, 48 s (40 waiting), 0 failed |
| **D-t04** | 6463725b | not done / **Right** | Backend asked "Which tag type?" with 3 types; picked (t193c); 58 tags made. The reply shown is the model's sentence: "Tagged 58 untagged doors in Aras Tanah (01w_WIP) with (t193c) sc1-100 tx1.8mm r2.5mm KKP, across 10 door types." It is not "58 objects created." | 5 steps, 53 s (42 waiting), 0 failed |
| **F-t03** | 6463725b | not done / **Right outcome; fill playbook not run** | Nothing written and the drafter is told the row does not exist; the question offers products and families nobody asked for (details below) | 5 steps, 97 s (83 waiting), 0 failed |
| **E-c05 / E-c04** | 6463725b | not done / **Lists differ** | The two type lists hold different types (details below) | 2 + 2 steps |
| **E-s01** | 6463725b | not done / **Right** | Door Schedule, 62 doors, fields Family and Type, Mark, Level, Width, Height. No question | 2 steps, 7 s, 0 failed |

### E-d03 (c22e1de5): walls dropped without a word

- **Speed:** finished in seconds, not minutes.
- **What the model sent:** `dimension-grids-walls` with **no inputs**. The script's default is `what = "Grids"`, so it made the 4 grid strings and printed **no wall line at all**: no DIM walls and no SKIP walls.
- **Reply:** "Made 4 dimension strings in Aras Tanah (01w_WIP) at an 8 mm offset: grids 1–13 and G–A plus their overall dimensions."
- **The card's false reason:** "Wall dimensions were not made: no wall strings qualified, so only the 4 grid strings exist." This sentence came from the model's report call and is **untrue**: the script never tried walls.
- **The plan:** readable (`E-d03-view.png`). A drafter would keep the grid strings, but asked for walls and got none.

### E-d03-B (6463725b): the SKIP route

- **What the backend did:** the model again sent no inputs. The backend put `what = "Grids and walls"` into the script.
- **Output:**
  - 4 grid strings, as in E-d01.
  - "SKIP walls 1 to 13: 104 walls reach that side, too many for one string; select the walls to dimension"
  - "SKIP walls G to A: 57 walls reach that side, too many for one string; select the walls to dimension"
  - 20 walls "not straight".
- **Reply:** "Made 4 dimension strings on the grids in Aras Tanah (01w_WIP); no wall strings were made."
- **Card checks:** "Wall string 1 to 13 skipped: 104 walls reach that side, too many for one string." and the same for G to A (57). The card's text drops "select the walls to dimension"; the Next chip says "Select the walls to dimension, then rerun for walls only".
- **The plan:** clean, 4 grid strings (`E-d03-B-view.png`). A drafter would keep it.

### E-c07 (c22e1de5): the question is right, Revit still rolls back

- **The question:** the card read "Change 205 walls?" / "This changes 205 walls in the whole model." with the options [Change 205 walls] [Skip], exactly as expected.
- **The rollback:** after the answer, Revit rolled the change back with the same 4 messages as round 10. Counted in Revit afterwards: DBb_bb01 205, DBb_bb02 99 (unchanged).
- **What the model did next:** it wrote no code of its own and did not rerun.
- **Reply:** quotes Revit's 4 messages word for word, then: "Run it on a smaller set instead — one level, the active view, or a selection — or fix those joined walls and invalid dimensions first, then I can rerun the whole model."
- **The card:**
  - headline: "Nothing changed: Revit rolled the change back. Change a smaller set (one level, a view or a selection), or first fix what Revit names."
  - "Check 4 things": the 4 Revit messages, each prefixed "Revit:".
  - "1 kind of issue · 16 fixable automatically", with one row "Can't keep elements joined · 16 elements". The "fixable automatically" claim was not tested.

### F-t04 (c22e1de5): card fixed, reply is the fallback

- **Values:** 0.04 A on 1X13W (38) and 1X8W (14) only. Type Properties: c_dl_1X8W 0.04 A and Voltage 0.00 V. Two other families still read 0.00 A: flou_r_aplr_2X14W and LED_r_el_1X8W.
- **Card:**
  - chip "Read back from Revit: 2 of 2 types match · affects 52 Lighting Fixtures" **wraps** onto two lines;
  - the two tiles "jkrEL_lit-fx_LED_c_dl_1X13W: Normal · 38" and "…1X8W: Normal · 14" each fit on one line at this pane width, so the break-at-"_" rule was not exercised;
  - **two "Written as asked" lists, "For jkrEL_lit-fx_LED_c_dl_1X13W: Normal" and "For …1X8W: Normal"**, both visible without a click;
  - one check: "Voltage: not in the brochure for Adrino LED 601 / 10 / 840 (2 types)".
- **In-model key:** "52 elements changed / Changed type jkrEL_lit-fx_LED_c_dl_1X13W: Normal 38 / Changed type jkrEL_lit-fx_LED_c_dl_1X8W: Normal 14 / Showing all 52 · 2 groups in the pane". The names are full and the old "52 not in this view" line is gone.
- **Reply shown:** the fallback "Set Kadaran_Arus_jkr_stec on 2 types (52 Lighting Fixtures). 2 skipped." The model's own sentence, which carried the value, was: "Set Kadaran_Arus_jkr_stec to 0.04 A on both downlight types (… 1X13W and … 1X8W), affecting 38 and 14 instances."

### F-t04-B (6463725b): reply right, new problem on the way

- **Reply shown:** **"Kadaran_Arus_jkr_stec = 0.04 A now sits on both downlight types, covering 52 Lighting Fixtures."** This is a cut of the model's sentence and keeps the value, as expected.
- **First fill run: nothing written.** With the same inputs that gave "PLANNED 2 writes" on `c22e1de5`, the first `fill-from-document` run now said **"NOTHING MADE: nothing to fill"**.
- **The model's question (not the backend's):** "The brochure gives the current for Adrino LED 601 / 10 / 840 (0.04 A) but prints no voltage for any model. What should Voltage be set to?" with [230 V (Malaysian mains supply)] [240 V] [220 V] [Skip Voltage…]. **The three voltages come from neither the brochure nor the prompt.** Picking one would write a made-up value; I did not try that.
- **After Skip:** the second run planned 2 writes, and the backend wrote 0.04 A on 1X13W (38) and 1X8W (14).
- **Failed call:** the model's first report was refused: `next/1: "Check whether the 8 W type should carry the 10 W row's current" is too long`.

### F-s1 (c22e1de5): written with no question first

**Tool calls, in order:**

1. `inspect` selection: 1 lamp, id 1319303, 1X13W.
2. `set_parameters` {Type Comments, `type_param: true`, ids [1319303]}. **Refused by the backend:** "Not written: the target is the drafter's selection and a type parameter changes every instance of the type, so it is written by the playbook fill-from-document, which says how many instances change and asks when that is more than the selection. Run it: run_revit_code with playbook "fill-from-documen" (the message is cut there).
3. `lookup` `fill-from-document`.
4. `find_elements` family 1X13W, type Normal: 38.
5. `run_revit_code` `fill-from-document` with `types = ["jkrEL_lit-fx_LED_c_dl_1X13W"]`, typed {Type Comments: LED Recessed Downlight}, `scope = ""`. **The model named the family, not the selection, so the script did not ask.** It printed "PLANNED 1 writes".
6. The backend's `set_parameters` on all 38 ids: one type, 38 instances.

**Afterwards:**

- Type Properties: 1X13W Type Comments = "LED Recessed Downlight".
- The card (in Malay) said afterwards: "Ini parameter jenis: semua 38 instance jenis Normal berubah, bukan hanya 1 yang dipilih."
- Reply shown (fallback): "Type Comments ditetapkan pada 1 jenis (38 Lighting Fixtures)."

### F-s1-B (6463725b): asked before writing

- **Inputs:** `set-parameter` (328 lines, code starts `// inputs`) with {values {Type Comments}, scope Selection}.
- **The question, before anything was written:** "Change all 38 instances?" / "This is a type parameter: it changes all 38 instances of jkrEL_lit-fx_LED_c_dl_1X13W: Normal, not only the ones you pointed at." [Change the type] [Skip]. English, as expected.
- **After "Change the type":** 1 type, read back 1 of 1, 38 instances.
- **Type Properties:** 1X13W "LED Recessed Downlight"; 1X8W empty.
- **Reply shown:** the model's sentence: "Type Comments ditetapkan kepada "LED Recessed Downlight" pada jenis jkrEL_lit-fx_LED_c_dl_1X13W: Normal, mempengaruhi 38 instance lampu."

### F-t10 (6463725b): right values, but the playbook did not run

- **What ran:** **`set-parameter` did not run.** The model searched (3 empty or wide searches), then sent 5 `set_parameters` calls of its own on a handle for the 2 downlight types: Wattage Comments 1X10W, Kadaran_Arus 0.04 A, Kuasa 10 W, Voltan 230 V, Voltage 230 V. Each read back "on 2 types … 52".
- **No question** was asked and **no wide-write card** appeared.
- **Type Properties:**
  - 1X13W holds all five values;
  - LED_r_el_1X8W is unchanged (1x8W, 0.00 A, 8.00 W);
  - 2X14W is unchanged (2X14W, 0.00 A, 28.00 W, 240.00 V).
- **Reply:** "5 parameter jenis dikemas kini pada 2 jenis downlight, meliputi 52 keluarga lampu." The numbers are right. "keluarga" means families, but the 52 are fixtures.
- **Failed call:** `inspect` with `ids` was refused (invalid_args).

### F-n3 (6463725b): neither put back nor told to Undo

- **What the model tried:** only 1 of the 5 parameters. It sent 2 `set_parameters` calls of its own: Wattage Comments = "1X13W" and = "1X8W", each filtered by family.
- **The backend held them with the team's question:** "Before I write: 2 changes reach 0 elements (all to the same elements). • Wattage Comments = 1X13W: ? (type parameter; its families are not known) • …" [Run it] [Cancel, write nothing]. **A drafter cannot tell what Run would do here.**
- **My answer:** Cancel.
- **Reply:** "The revert was rejected, so nothing was written; both downlight families still hold 1X10W, 10 W, 230 V and 0.04 A." There was no Undo hint.

### F-t03 (6463725b): right outcome, fill playbook not run

- **What happened:** the model did not run the fill playbook. It asked its own two-field question: "The brochure has no 8 W Adrino model: Adrino LED 601 comes only in 10, 15 and 20 W. Which product and which 1X8W families should I fill?"
- **The fields offer things the prompt never asked for:**
  - 3 other products;
  - "All five 1X8W families — 25 instances", which includes families the drafter did not name.
- **My answers:** "Do not fill Model…" + "Only jkrEL_lit-fx_LED_c_dl_1X8W" + Continue. Nothing was written.
- **Reply:** "Brosur tidak ada model 8 W: Adrino LED 601 hanya 10, 15 dan 20 W (halaman 2), jadi tiada apa ditulis…"

### E-c05 / E-c04 (6463725b): the two type lists differ

- **E-c05 (new type):** the card lists 14 types (every door type except PTh300a) plus Skip. Skip sent at once: "Skipped as you asked: nothing changed…"
- **E-c04 (old type):** the card lists 11 types plus Skip.
  - It **lacks** (PTp600a) 1950 x 2400 sd-ky, (PTp600b) 1800 x 2100 sd-ky, (PTp600b) … @PT00 and (PTt760a). These are likely types with no placed doors, but the card does not say so.
  - It **includes (PTr680a)**, the new type itself.
- **Skip** sent at once on both.

## Added part: set-parameter (backend `6463725b`)

Another session (ningyo-ce) asked for this part during the round. It ran right after the backend switch.

| Run | Prompt | Result |
|---|---|---|
| F-s1-B, F-s1b | see above | **Right** both |
| E-d03-B, F-t04-B | see above | E-d03 **right**. F-t04: reply **right**, path worse |
| **SP-3** | "Set Type Comments of jkrEL_lit-fx_LED_c_dl_1X8W: Normal to Toilet light." (`downlight-model.rvt`) | **Right.** No question. `set-parameter` gave "WRITE type \| 1 type, 14 instances", then read back 1 of 1. Type Properties: c_dl_1X8W "Toilet light"; c_dl_1X13W empty. The LED_r_el_1X8W row was hidden by a tooltip in the screenshot. Reply: "Type Comments is now "Toilet light" on type Normal of jkrEL_lit-fx_LED_c_dl_1X8W, changing all 14 Lighting Fixtures of that type." 3 steps, 7 s |
| **SP-4** | "Set Comments to Done on the doors on Aras Tanah." | **My prompt named the wrong level.** "Aras Tanah" is the plan's level, but the doors are hosted on Aras 01 (60) and Aras 02 (2). The playbook printed "SKIP no doors on Aras Tanah". The model then asked its own question in English: "There are no doors on level Aras Tanah. Where should Comments = Done be set?" [Aras 01 (60 doors)] [Aras 02 (2 doors)] [Both (62)] [Only the doors I have selected]. Answered Aras 01: 60 doors, read back 60 of 60. **In Revit:** the 58 doors in the plan read `<varies>`, which fits 56 Done plus 2 on Aras 02; the key said "56 of 60 visible in this view". **One Ctrl+Z** emptied all 58. One failed call: `find_elements` with `group_by` inside the query (invalid_args); the retry worked. 8 steps, 57 s (41 waiting). So "no question" could not be tested, because the level held no doors |
| **SP-6** | "Set the parameter Fire Ratng of every door in the whole model to 1 hr." | **Right.** Card: "Parameter name Fire Ratng is not in the model / Which parameter did you mean?" [Fire Rating] [Skip]. Only one nearest name was offered. Answered Skip, and nothing was written. Reply: "Nothing changed: "Fire Ratng" matched no parameter, and "Fire Rating" was skipped, so 0 doors were set." The model's first report was refused (`next/1 … is too long`) |
| Pipes in the model | (checked in Revit before SP-7) | One pipe type, **UPVC**. **88 pipes**, all Pipe Segment "Plastic - Schedule 40", 100 mm. Segments in Mechanical Settings: Carbon Steel 40/80, Copper A/B/C/D, Ductile Iron 22/30, Plastic 40/80, Stainless 5S/10S. Pipes by level, from the copilot: Aras 01 22, Aras 02 66. I did not count them by level in Revit |
| **SP-7** | "Change the pipe material on Aras Tanah to Plastic - Schedule 80." | **My level was wrong again** (no pipes on Aras Tanah). The model asked its own question: [Aras 01 only (22)] [Aras 02 only (66)] [Both (88)] [Skip]. Answered Aras 01: "WRITE instance \| 22 pipes on Aras 01 \| Pipe Segment = 137332" (the segment's element id), then read back 22 of 22. The card shows "Pipe Segment Plastic - Schedule 80, was Plastic - Schedule 40". **In Revit:** all 88 pipes are still type **UPVC**, and Pipe Segment is now `<varies>`. **I did not check the fittings one by one** (83 in the plan, several families). 8 steps, 63 s |
| **SP-9** | "Set the slope of the pipes on Aras 01 to 1%." | **Not as expected.** The playbook printed "SKIP 22 pipes: Slope is read-only". Reply: "Nothing was changed: Slope is read-only on all 22 UPVC pipes on Aras 01." No question, nothing written. In this model the Properties palette also shows Slope greyed for these pipes, so "read-only" matches what Revit shows here |

## Round 10's findings

| Round 10 finding | Round 11 |
|---|---|
| E-d03: wall strings useless (1,424 / 1,486 spans), and the script ran 410 s unstopped | **Consistent with fixed** on `6463725b`: 7 s, and the walls were skipped with "104 / 57 walls reach that side, too many for one string; select the walls to dimension". On `c22e1de5` it was **not fixed in a new way**: the walls were dropped silently, and the card gave a false reason |
| E-c07: rolled back; reply 10 vs card 16; no next step for the drafter | **Partly fixed.** The question now reads exactly as specified; the model wrote no code of its own and did not rerun; the reply and card give Revit's words and a next step. **Still not fixed:** Revit rolls the change back on this model |
| E-c07: backend asked "Which scope?" although the prompt said "whole model" | **Consistent with fixed:** "Change 205 walls?" |
| F-t04 card: chip cut | **Consistent with fixed** (it wraps) |
| F-t04 card: only one "Written as asked" list | **Consistent with fixed** (two lists, both shown) |
| F-t04 key: names cut, "52 not in this view" | **Consistent with fixed** |
| F-t04 tiles: names wrap mid-word | **Not tested**: the names fit at this pane width |
| E-d01: false warning "expected 5" | **Consistent with fixed** |
| E-c01: row named only the old type; key in lower case | **Consistent with fixed** ("old → new · 20", real case) |
| G1: question in English, frame in Malay | G2's frame is English, but G2's prompt was English too, so a Malay frame was not tested. F-s1-B's card, after a Malay prompt, was English |
| E-d09 / E-s07: single choice needs a second click on Continue | **Consistent with fixed** for E-d09, E-c05 and E-c04 (Skip sent at once). A two-field form (F-t03) still needs Continue, as it should |
| The model sends malformed arguments now and then | **Still seen:** `group_by` inside a query (SP-4, SP-7), `ids` in inspect (F-t10), and `next` items too long in report (F-t04-B, SP-6, SP-9). It recovered each time |
| Fallback reply replaces the model's sentence (round 9) | Still on `c22e1de5` (F-t04, F-s1). **Consistent with fixed** on `6463725b` (F-t04-B, F-s1-B, D-t04) |
| F-t03, F-t10, F-n1–3, E-s01, E-c05, E-c04, D-t04, G2 | All done this round; results above |

## New or worse

1. **F-s1 on `c22e1de5`:** a type parameter for a selected lamp was written to all 38 lamps **without a question**. It is consistent with fixed on `6463725b`.
2. **F-t04 on `6463725b`:** the first brochure fill now plans **nothing** ("nothing to fill"), where `c22e1de5` planned 2 writes from the same inputs. The model then offers **voltages that are in neither the brochure nor the prompt** (230/240/220 V). Picking one would write a made-up value.
3. **F-n3:** "Put the old values back" put nothing back, did not suggest Undo, and restored only 1 of the 5 values. The team's question it raised is unreadable: "2 changes reach 0 elements … its families are not known".
4. **F-t10 and F-t03:** the model wrote, or asked, by itself instead of running `set-parameter` / `fill-from-document`. The values were right, but F-t03's question offered "All five 1X8W families — 25 instances", which includes families nobody asked for.
5. **E-d03 on `c22e1de5`:** the card stated a false reason, written by the model: "no wall strings qualified".
6. **E-c04:** the old-type list offers the new type itself, (PTr680a), and differs from E-c05's list by four types.
7. **SP-9:** the slope could not be set ("read-only").
8. **UI:** a finished turn's time keeps counting while the next turn runs in the same chat ("384s", "435s", "262s", "337s" under Done turns).
9. **E-c01:** the first Next chip is cut at its left edge.

## Not done

E-d03s. I could not pick three parallel walls by mouse in the rotated plan of `sheets-test.rvt`. A plan with fewer overlapping categories, or Select by ID with known wall ids, would make this run possible.

## What is still running or changed

Nothing is restored yet.

**PC:**
- Revit is open on `sheets-test.rvt` with E-s01's Door Schedule in memory only, not saved. It runs add-in 0.0.105 through the task `BinaLiveTestR7`.
- The task `BinaLiveTestR7` now points at `r11-addin-0.0.105`.
- New this round: the folder `C:\Users\Public\rel-test\r11-addin-0.0.105` and the file `addin-0.0.105.tgz`.
- From earlier rounds: the `r10-*` and `r7*` folders and their tgz files, the `brochure` folder, and the add-in config pointing at port 18007.
- Not touched: port 18002, and the installed 0.0.117 and 0.0.118 folders.
- The RustDesk viewer from this Mac is connected.

**Mac:**
- The backend on 8005 runs `6463725b` from `lh/lt-r2` (detached).
- The reverse tunnel's ssh process (18007 → 8005) is running.
- `caffeinate` runs twice: pid 30813 (this round, at most 12 h) and pid 34050 (round 10, at most 6 h).
- The build worktree `…/b8bbfc99-…/scratchpad/addin-build` is detached at `94f020cb`.
