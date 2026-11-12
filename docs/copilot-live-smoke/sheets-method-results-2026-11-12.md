# Sheets live test results, 12 Nov 2026 (round 2, playbook prompts only)

Prompts s01 to s11 of `sheets-method-test.json` (the `sheet-set` playbook). s12 to s14 are not part of this round. Planned: s01 to s11 once each (s10 answered `tak payah`), s10 again answered `ya, buat`, and s09 again: 13 runs per build.

## Common setup

| | |
|---|---|
| PC | MrBiggie (Windows, 1920×1080), driven from the Mac through Windows-MCP over an SSH tunnel |
| Revit | 2027.3 |
| Model file | `C:\Users\Public\rel-test\sheets-test\sheets-test.rvt` (same file as 11 Nov, same SHA-256; saved time 2026-11-12T11:10:14, unchanged after every run) |
| Backend | local bina-ai on the Mac, `127.0.0.1:8005`, reached from the PC on port 18005 through a reverse tunnel. Same `.env` as the 11 Nov Run B (model `deepseek-flash`) |
| Add-in | Debug build from this repo, loaded through `BINA_SYNC_PLUGIN_DIR` (`C:\Users\binal\rtest-livetest-r2\...`), update feed pinned to staging |

Names used in the prompts, checked in the model's Project Browser (not through the Copilot): title block `(i205a) A1 Landscape`; `<Level A>` / `<Level B>` = `Aras 01` / `Aras 02`; `<N1>` / `<N2>` = 1 / 2; `<Level With Two Plans>` = `Aras Tanah` (floor plans `Aras Tanah (01w_WIP)` and `Pelan Tapak`); `<Plan Name>` = `Pelan Tapak`; `<Level Without Ceiling Plan>` = `Aras Bumbung`; `<Level Without Floor Plan>` = `Aras Pentas`. FP1 = 3 (Aras 01, Aras 02, Aras Bumbung). Floor plans in the model: `Aras Tanah (01w_WIP)`, `Pelan Tapak`, `Aras 01/02/Bumbung (07d_Dokumen)`. Ceiling plans: `Aras 01/02 (07d_Dokumen)` only. The model has no sheets.

How each run was done:
- **Reopen without saving:** Revit killed, then started on the model by the scheduled task `BinaLiveTestOpenR2`. A new process id and an unchanged file save time were checked every time.
- **New chat:** a fresh Revit session opens an empty Copilot chat.
- **Answers:** when the Copilot asked with a form (drop-downs), the form was filled and submitted; the choice is listed per row.
- **Checked in Revit:** the Project Browser was filtered to the new sheet numbers, every new sheet was opened, and each sheet node was expanded to show the views placed on it. Screenshots are in the session scratchpad under `verify/R1-sNN`.
- **Seconds:** from the backend's turn start to turn end. Where the Copilot asked, the time spent waiting for the tester's answer is given separately.
- **Exports:** every turn's `ai.copilot_turns` row and all its `ai.copilot_turn_events` rows, unmodified, are in `runs/R1/sNN.events.json` in the session scratchpad, with a short summary in `runs/R1/sNN.json`.

## Before the merge: backend 2ad0f13d, add-in 0.0.96

Backend bina-ai `2ad0f13d` ("Let rule 6 ask, narrow the view gate and tighten agree words"). Add-in 0.0.96, built from `828b95e` of this branch.

**Stopped after 3 of 13 runs** on the owner's instruction, to move to the merged build. s04 to s11 and the two repeats were not run on this build.

| Prompt | Right or wrong | Playbook named | Code starts `// inputs` | Script output | Questions (topic, needed?) | Wrote before the answer | Made something not asked for | Refused code | Failed tool calls | Seconds | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|
| s01 | Right | `sheet-set` | yes | `MADE` × 4 (no `SKIP` lines: the skips were decided in the question) | One form, three fields: which plan for Aras Tanah (needed); "Aras Pentas: no floor plan exists. Create one?" and the same for Aras Langkan (not needed: in an every-level prompt these should be listed as skipped, not asked) | no (only reads before the question) | no | no | 0 | 89 (22 working, 67 waiting for the answer) | Answered in the form: Pelan Tapak, Skip, Skip. Made A-101 Pelan Tapak, A-102 Aras 01, A-103 Aras 02, A-104 Aras Bumbung, one floor plan on each. The reply lists Pentas and Langkan as skipped. Result card header says "50 created" while the backend counted 24 added, and lists most rows as "Element 2248359" etc. |
| s02 | Right | `sheet-set` | yes | `MADE` × 2 | none | no | no | no | 0 | 16 | A-101 Aras 01, A-102 Aras 02, floor plans, one each |
| s03 | Right | `sheet-set` | yes | `MADE` × 2 | none | no | no | no | 1 | 14 | A-101 and A-102 hold the reflected ceiling plans of Aras 01 and Aras 02, one each; no floor plan placed. The failed call is the model's own read-back after the script: it sent malformed JSON to `inspect` and fixed it on the next call. Result card "26 created", most rows shown as element ids |

Tester error in s01, recorded for completeness: after the restart the Project Browser was closed, and the text typed to filter it went into the 3D rendering view as Revit keyboard shortcuts (a Toposolid Smooth Shading dialog opened and the result card then said "Undo is off: the model changed after this turn"). It touched the view display only, not the sheets, and the model was discarded by the next reopen. The Project Browser was opened before every later check.

## After the merge: backend 42dcc0a5, add-in 0.0.97

Backend bina-ai `42dcc0a5` (the accuracy work merged with `origin/integration/ai-277-all`), same port, same `.env`. Add-in 0.0.97, Debug, built from `22aa801` of this branch (the playbook branch merged with the team's new Copilot UI), update feed pinned to staging. Before the first run: the loader log said "loaded 0.0.97.0", the pane's menu showed "Version 0.0.97", the pane drew the new look, and the add-in logs had no exception.

All 13 runs were done. One extra attempt at s11 is void and not counted: while it ran, someone at the PC typed "can you open 3d viewer fot his model" into the Copilot box (Windows-MCP refused the typing with `PHYSICAL_INPUT_HELD`), that message was sent instead of the prompt, and the Copilot opened the `{3D}` view, read only. s11 was then run again.

Answers: questions with buttons or a form were answered by clicking; s10 and s11 were answered by typing the table's words (`tak payah`, `ya, buat`, `no`).

| Prompt | Repeat | Right or wrong | Playbook named | Code starts `// inputs` | Script output | Questions (topic, needed?) | Wrote before the answer | Made something not asked for | Refused code | Failed tool calls | Ended with `report` | Seconds (working + waiting for the answer) | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| s01 | 1 | Right | `sheet-set` | yes | `NOTHING MADE`, `ASK` Aras Tanah, `SKIP` Pentas and Langkan; then `MADE` × 4 + `SKIP` × 2 | 1: which plan for Aras Tanah (needed) | no | no | no | 1 | yes | 41 (25 + 15) | The script ran first and the Copilot asked only what the script asked. Pelan Tapak clicked. A-101 Pelan Tapak, A-102 Aras 01, A-103 Aras 02, A-104 Aras Bumbung |
| s02 | 1 | Right | `sheet-set` | yes | `MADE` × 2 | none | no | no | no | 1 | yes | 19 | A-101 Aras 01, A-102 Aras 02 |
| s03 | 1 | Right | `sheet-set` | yes | `MADE` × 2 | none | no | no | no | 1 | yes | 14 | Reflected ceiling plans on A-101/A-102, no floor plan placed |
| s04 | 1 | Right | `sheet-set` | yes | `NOTHING MADE`, `ASK` Aras Tanah; then `MADE` × 4 | 2: (a) before the script: "Aras Pentas and Aras Langkan have no floor plan. What should I do for them?" (not needed: they should be listed as skipped); (b) after the script: which plan for Aras Tanah (needed) | no | no | no | 1 | yes | 71 (31 + 40) | Skip, then Pelan Tapak. S-201 Pelan Tapak, S-202 to S-204; no A- sheet |
| s05 | 1 | Right | `sheet-set` | yes | `MADE` × 2 | none | no | no | no | 2 | yes | 23 | "level 1 / level 2" read as Aras 01 / Aras 02 |
| s06 | 1 | Right | `sheet-set` | yes | `MADE` × 2 | 1: which title block (needed) | no | no | no | 2 | yes | 51 (26 + 25) | The buttons offered 4 of the 6 title block types (`(03a)` and `(LS02a)` missing). (i205a) clicked |
| s07 | 1 | Right | `sheet-set` | yes | `MADE` × 1 | none | no | no | no | 0 | yes | 12 | A-101 with Pelan Tapak only |
| s08 | 1 | Right | `sheet-set` | yes | `MADE` × 2, `SKIP A-101: number in use` | none | no | no | no | 0 | yes | 16 | Setup as on 11 Nov: A-101 "Manual" made by one Copilot message in its own chat, then "+" new chat (a different session id was checked). A-101 Manual left empty and unchanged; A-102 Aras 01, A-103 Aras 02; the reply says A-101 was in use |
| s09 | 1 | Right | `sheet-set` | yes | `NOTHING MADE`, `ASK` Aras Tanah, `SKIP` Pentas and Langkan; then `MADE` × 4 + `SKIP` × 2 | 2, one after the other: (a) which levels (needed); (b) a form: which plan for Aras Tanah (needed), and "Aras Pentas / Aras Langkan tiada FloorPlan. Buat satu?" (not needed: the script had already skipped them) | no | no | no | 0 | yes | 103 (30 + 73) | "Semua aras", then Pelan Tapak / Skip / Skip. Same 4 sheets as s01. The level buttons offered Aras Tanah, Aras 01, Aras 02 and "Semua", not Aras Bumbung |
| s10 | 1 (`tak payah`) | Right | `sheet-set` | no code ran | none | 1: "Aras Bumbung tiada ceiling plan. Buat satu?" (needed) | no | no | no | 0 | no (plain reply, nothing was changed or checked) | 19 (4 + 15) | Asked after the three reads, before any code. Reply: "Tiada apa dibuat: Aras Bumbung tiada ceiling plan dan anda pilih untuk tidak membuatnya, jadi tiada sheet dijana." No sheet, no new view in the Project Browser |
| s11 | 1 | Right | `sheet-set` | no code ran | none | 1: "Aras Pentas has no Floor Plan view. Create one for the sheet?" (needed) | no | no | no | 1 | yes | 24 (7 + 17) | Answered `no`. Reply: "No sheet was made: Aras Pentas has no Floor Plan view and you chose to skip it." No sheet, no new plan |
| s10 | 2 (`ya, buat`) | Right | `sheet-set` | the view: no (the model's own code); the sheet: yes | `MADE` × 1 | 1: "Aras Bumbung tiada ceiling plan (siling). Cipta satu?" (needed) | no | no (the view was agreed to) | no: the view-making code ran after the answer | 1 | yes | 31 (15 + 16) | The model wrote `ViewPlan.Create` code for the ceiling plan (one compile error, `ViewPlan.LevelName`, fixed on the next call), named the new view "Aras Bumbung (07d_Dokumen)", then ran the stored script: A-101 holds it. In Revit: one new reflected ceiling plan for Aras Bumbung (the browser files it under 01w_WIP) and A-101 |
| s09 | 2 | Right | `sheet-set` | yes | `NOTHING MADE`, `ASK` Aras Tanah, `ASK` Aras Pentas, `ASK` Aras Langkan; then `MADE` × 4 | Same two as repeat 1 | no | no | no | 3 | yes | 95 (34 + 61) | This time the model passed all six levels by name, so the script asked about Pentas and Langkan instead of skipping them; the form question that followed was then expected from the script's point of view. Same 4 sheets as repeat 1 |

**13 of 13 right.** No run wrote before the answer, made something not asked for, chose a plan for the drafter, or asked about sheet numbers. The playbook named was `sheet-set` in all 13. Every script output was readable (`MADE` / `SKIP` / `ASK`, and `NOTHING MADE` when it stopped). After every `NOTHING MADE` the Copilot asked the drafter; it never sent `report` instead. No tool result started "Not run: this code makes a new view" in any run. The Copilot wrote no text before or between tool calls in any run.

Questions: 11 asked in 13 runs, 3 of them not needed by the page's table: s04 (a), and the Pentas/Langkan fields of the form in both s09 repeats (in repeat 2 the script itself asked for them, because the model named those levels). The pattern is the same each time: in a prompt for every level, the Copilot offers to create a floor plan for the two levels that have none, where the page wants them listed as skipped.

Failed tool calls (13 in all) were all the model's own calls, never the stored script: malformed or wrong arguments to `inspect` (invalid JSON, `ids` or `fields` in the wrong place), `report` rejected because a `next` suggestion was too long or a `checks` action had the wrong shape, and one compile error in the s10 view code. Each was fixed on the next call.

### What the new UI did in these runs

- **Result card:** after every sheet run a card appeared with the new sheets as rows (number, name, title block where given, scale such as 1:100 or 1:200, "Open sheet in Revit") and the placed plan under each ("Open view"), followed by "Check N things", "Next" suggestions, "Undo all" and "Save as command". In Malay prompts the card was in Malay.
- **Floating result toast: it appeared over the model view in every run that changed the model** (11 of 13; not in s10 `tak payah` or s11), with a line drawn into the view. Its count is not the backend's: "Created 26 elements" when the backend counted 12 added (s02, s03, s05, s06, s08), 13 for 6 (s07), 21 for 14 (s10 repeat 2), and 50 for 24 (s01, s04, s09; possibly a cap).
- **Card category counts look wrong too:** in s03 the card listed "other 12, Viewports 6, GraphicsStyle 2, Schedule Graphics 2, Schedules 2, Sheets 2, SketchPlane 2, Title Blocks 2" for two sheets, while the backend counted 12 added.
- **The reply is cut at the first "(":** the `report` reply "Made 2 sheets: A-101 for Aras 01 and A-102 for Aras 02, each with its ceiling plan and the (i205a) A1 Landscape title block." was shown and streamed as "...each with its ceiling plan and the." Same in s04 ("...floor plan on."), s05 ("2 sheet A1 dengan title block.") and s09 repeat 2 ("...dengan title block."). In s06, s07, s08, s09 repeat 1 and s10 repeat 2 a reply that contained "(i205a)" was shown whole, so the cut is not every time.
- **Misleading banner on a deliberate stop:** when the script printed `NOTHING MADE` and the Copilot asked, the pane showed a yellow "Not every change in this turn was applied. Check the receipt before relying on the reply." and "0/1 done · 1 unchanged" (s01, s04, s09 ×2). Nothing had failed: stopping to ask is the intended path.
- **A clipped button label:** in s04 the first answer button read "a Floor Plan for Aras Pentas and Aras Langkan, then sheet all 6" (the word "Create" cut off).
- After a sheet run, the created elements were left selected in the model (60 in s01).

## Comparison

### Before the merge against after the merge (s01 to s03, the only prompts run on both)

| Prompt | 0.0.96 / 2ad0f13d | 0.0.97 / 42dcc0a5 |
|---|---|---|
| s01 | Right. Asked before running the script, one form with three fields, two of them not needed (create floor plans for Pentas/Langkan) | Right. Ran the script first, then asked only which plan for Aras Tanah: no unneeded question |
| s02 | Right, no question, 16 s | Right, no question, 19 s |
| s03 | Right, no question, 14 s | Right, no question, 14 s |

Same results; the merged build asked one unneeded question fewer in s01. The merged build's replies are one sentence and every write ended with `report`, as intended.

### Both builds against 11 November (add-in 0.0.95, other PC, backends 725ff18c / e9fad014)

| Prompt | 11 Nov Run A | 11 Nov Run B | 12 Nov after the merge |
|---|---|---|---|
| s01 | Right | Right | Right (also right before the merge) |
| s02 | Right | Right | Right (also right before the merge) |
| s03 | Right, 1 unneeded question (sheet numbers) | Right, same unneeded question | Right, no question (same before the merge) |
| s04 | Right | Right | Right, 1 unneeded question (levels with no plan) |
| s05 | Right, 1 unneeded question (sheet number) | Right, 1 unneeded question based on wrong facts | Right, no question |
| s06 | Right | Right | Right |
| s07 | Right | Right | Right |
| s08 | Right, no question | Right, asked a question | Right, no question |
| s09 | **Wrong**: picked `Aras Tanah (01w_WIP)` itself | **Wrong**: same | Right in both repeats: asked which levels, then which plan, and used Pelan Tapak |
| s10 | **Wrong**: created a ceiling plan without asking | **Wrong**: same, and did not use the playbook | Right in both repeats: asked first; `tak payah` made nothing, `ya, buat` made one ceiling plan and its sheet |
| s11 | not in that test | not in that test | Right: asked, `no` made nothing |

s09 and s10, wrong in both runs on 11 November, were right in both repeats on the merged build: **consistent with the fix**. This is one or two runs per prompt on one model (`deepseek-flash`), so it shows no rate. Nothing that was right on 11 November was wrong on 12 November. Unneeded questions about sheet numbers (s03, s05, s08 on 11 Nov) did not appear. One kind of unneeded question remains: offering to create a floor plan for levels that have none, in prompts for every level (s01 before the merge, s04, s09).

### What the Copilot did on s09, s10 and s11 (merged build)

- **s09, repeat 1:** nothing existed before either answer. It asked "Aras mana perlu dibuat sheet?" with buttons Aras Tanah / Aras 01 / Aras 02 / "Semua aras (semua)"; answered "Semua aras". The script then ran and made nothing (`NOTHING MADE`, `ASK Aras Tanah: which FloorPlan? Aras Tanah (01w_WIP) | Pelan Tapak`, `SKIP Aras Pentas`, `SKIP Aras Langkan`). It then asked one form: the plan for Aras Tanah (both plans named, plus Skip), and "tiada FloorPlan. Buat satu?" for Pentas and Langkan; answered Pelan Tapak / Skip / Skip. After: A-101 Pelan Tapak, A-102 Aras 01, A-103 Aras 02, A-104 Aras Bumbung, one plan each; the other Aras Tanah plan not placed.
- **s09, repeat 2:** the same two questions ("Semua aras (6)" this time), the same answers and the same four sheets. The only difference: the model passed all six level names, so the script reported `ASK` rather than `SKIP` for Pentas and Langkan.
- **s10, `tak payah`:** after reading levels, ceiling plans and title blocks, it asked "Aras Bumbung tiada ceiling plan. Buat satu?" (Buat / Langkau). Nothing existed. After `tak payah` it replied that nothing was made. After: no sheet, no new view.
- **s10, `ya, buat`:** the same question; nothing existed. After `ya, buat` it created one reflected ceiling plan for Aras Bumbung with its own code, then the stored script made A-101 holding it. After: exactly that view and that sheet.
- **s11:** it asked "Aras Pentas has no Floor Plan view. Create one for the sheet?"; nothing existed. After `no`: no sheet, no new view, and the reply said why.

Raw data for the merged runs (turn rows and all turn events, unmodified, and screenshots) is in the session scratchpad under `runs/R1`, `runs/R2` and `verify/R*-sNN`; the runs before the merge under `pre/`; the void s11 attempt under `runs/void`.
