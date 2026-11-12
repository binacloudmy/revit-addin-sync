# Sheets live test, 12 November 2026, round 3

## Round 3: backend cc1f1954, add-in 0.0.98

- Backend: `cc1f1954` (`feat/copilot-runnable-playbooks`), run locally on 127.0.0.1:8005, reached from the PC through a reverse SSH tunnel. Model `deepseek-flash` as in round 2.
- Add-in: 0.0.98 Debug (same code as `c23d989`), loader log "loaded 0.0.98.0".
- Revit 2027.3 on MrBiggie, model `C:\Users\Public\rel-test\sheets-test\sheets-test.rvt` (saved file time 2026-11-12 11:10:14, the same file for every run; checked after each reopen).
- Names as in round 2: title block `(i205a) A1 Landscape`; two-plan level `Aras Tanah` (`Aras Tanah (01w_WIP)`, `Pelan Tapak`); levels with no floor plan `Aras Pentas`, `Aras Langkan`; level with no ceiling plan `Aras Bumbung`. FP1 = 3.
- Each run: Revit closed without saving and the model reopened by the scheduled task, new chat, the prompt typed into the Copilot box, the result checked in Revit (Project Browser filter, then each new sheet opened).
- Date and time of the runs: 12 November 2026, 15:48 to 16:31.

### Navisworks on the PC

Navisworks Manage was open on the same PC during all runs, with a Clash Detective "Report" dialog open, and was in front when this session started. Nothing was sent to it. Before every click, keystroke and scroll (207 inputs), a screenshot was checked: the active app on the taskbar had to be Revit, and for clicks in the Copilot pane the pane had to be visible. Revit was brought to the front only through the window list.

- The check blocked an input twice: 15:49:31 (my own check was too strict about a Revit drop-down menu covering the pane; Revit was in front) and 16:24:52 (RustDesk, not Navisworks, was the active app during the run 8 check in Revit). Both times Revit was brought to the front again, checked, and only then was the input sent.
- No input went to Navisworks or to RustDesk. Navisworks did not come to the front during any run.

### Results

| Run | Prompt | Answers | Right or wrong | Questions (who, when, choices) | Model step between `NOTHING MADE` and the question | Read-back after `MADE` | Yellow warning | Reply as shown | Result card | Toast | Failed calls | Model steps | Seconds (working + waiting) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | s02 | none | Right: A-101 Aras 01, A-102 Aras 02, one floor plan each | none | n/a | no | no | **Replaced by a template: "30 objects created."** The `report` call's sentence was "Made 2 A1 sheets with title block (i205a) A1 Landscape: A-101 for Aras 01 and A-102 for Aras 02, each with its floor plan at 1:100." | Category counts (other 12, Viewports 6, ...), 2 sheet rows with placed plans, "Check 2 things" (sheet names, plotter size) | Created 26 (backend: 12 added; card count 30) | 0 | 3 | 17 |
| 2 | s01 | Form: Pelan Tapak / Skip Aras Pentas / Skip Aras Langkan | Right: A-101 Pelan Tapak (1:200), A-102 Aras 01, A-103 Aras 02, A-104 Aras Bumbung | 1, from the backend (`server_ask`), after the script. One form, three drop-downs: "Aras Tanah has 2 floor plans" [Aras Tanah (01w_WIP), Pelan Tapak, Skip Aras Tanah]; "Aras Pentas has no floor plan" [Create a FloorPlan for Aras Pentas, Skip Aras Pentas]; the same for Aras Langkan. Matches the three `ASK` lines | no | no | no ("0/1 done · 1 unchanged" while waiting, gone at the end) | Whole: "Made 4 sheets A-101 to A-104 with (i205a) A1 Landscape, one plan each; Aras Pentas and Aras Langkan skipped as asked." | 4 sheet rows; checks: "Aras Pentas and Aras Langkan were skipped (no FloorPlan); no sheets for them." | Created 50 (backend 24; card 60) | 1 (`report` next too long, fixed) | 5 | 26 + 80 |
| 3 | s01 | Form: Pelan Tapak / **Create** Aras Pentas / Skip Aras Langkan | Right: one new floor plan `Aras Pentas (07d_Dokumen)`, none for Langkan; A-101 Pelan Tapak, A-102 Aras 01, A-103 Aras Pentas, A-104 Aras 02, A-105 Aras Bumbung | Same form as run 2, same choices | no | no | no | Whole: "Made 5 sheets A-101 to A-105 with the (i205a) A1 Landscape title block; Aras Langkan skipped as you asked." | 5 rows; checks: Langkan skipped; Pentas plan created; check its view range (warning) | Created 59 (backend 9 + 30; card 85) | 1: the model's first code made plans for **both** Pentas and Langkan; the backend refused it ("the drafter picked Skip for it") and the model then made Pentas only | 6 | 32 + 38 |
| 4 | s04 | Form as run 2 | Right: S-201 Pelan Tapak, S-202 Aras 01, S-203 Aras 02, S-204 Aras Bumbung; no A- sheet | 1, from the backend, after the script, same choices as run 2. **No question before the script** | no | no | no | Whole: "Created 4 sheets S-201 to S-204 on (i205a) A1 Landscape; Aras Pentas and Aras Langkan were skipped as chosen." | 4 rows; checks list Pentas and Langkan skipped (warning) | Created 50 (backend 24; card 60) | 1 (`report` next too long, fixed) | 5 | 29 + 84 |
| 5 | s09 | Form: Pelan Tapak / Langkau / Langkau | **Sheets right, questions not as expected:** same 4 sheets as run 2, verified. No levels question: the model ran the stored script with an empty level list (all levels) straight after its reads | 1, from the backend, after the script, in Malay: "Aras Tanah ada 2 pelan lantai" [Aras Tanah (01w_WIP), Pelan Tapak, Langkau Aras Tanah]; "Aras Pentas tiada pelan lantai" [Buat pelan lantai untuk Aras Pentas, Langkau Aras Pentas]; same for Langkan. Matches the `ASK` lines | no | no | no | Whole, Malay: "4 helaian dibuat: A-101 Pelan Tapak, A-102 Aras 01, A-103 Aras 02, A-104 Aras Bumbung, semuanya dengan (i205a) A1 Landscape." | 4 rows; checks: "Aras Pentas dan Aras Langkan dilangkau..." | Created 50 (backend 24; card 60) | 0 | 4 | 22 + 60 |
| 6 | s10 | `tak payah` typed | Right: no sheet, no new view | 1, from the backend, after the script: "Aras Bumbung tiada pelan siling." [Buat pelan siling untuk Aras Bumbung, Langkau Aras Bumbung]. Matches the `ASK` line | no | n/a | no; but an issue card (see below) | Whole: "Tiada apa dibuat: Aras Bumbung tiada pelan siling dan anda minta langkau, jadi tiada sheet dicipta." | No result card. **Issue card "1 isu" with "The code ran but changed nothing. warning"** | none | 0 | 3 | 7 + 15 |
| 7 | s10 | Clicked "Buat pelan siling untuk Aras Bumbung" | Right: one new reflected ceiling plan `Aras Bumbung (07d_Dokumen)` (template `jkrAR_07d_Dokumen_(Pelan Siling)`) and A-101 holding it | Same buttons as run 6 | no | no (one `inspect` of the existing ceiling plans before making the new one) | no | Whole: "Sheet A-101 Aras Bumbung dibuat dengan title block (i205a) A1 Landscape dan pelan siling Aras Bumbung (07d_Dokumen) skala 1:100." | 1 row; check: the ceiling plan was made with the same type and template as Aras 01 and 02 | Created 21 (backend 8 + 6; card 24) | 0 | 7 | 21 + 25 |
| 8 | s11 | Clicked "Skip Aras Pentas" | Right: no sheet, no new view | 1, from the backend, after the script: "Aras Pentas has no floor plan." [Create a FloorPlan for Aras Pentas, Skip Aras Pentas]. Matches the `ASK` line | no | n/a | no; same issue card as run 6 | Whole: "Nothing was made: Aras Pentas has no FloorPlan, and you chose to skip it, so no sheet was created. Next: create a floor plan for Aras Pentas, then rerun the sheet for it." | No result card. Issue card "1 issue / The code ran but changed nothing. warning" | none | 0 | 3 | 6 + 21 |
| 9 | s08 | none | Right: A-101 Manual unchanged and empty; A-102 Aras 01, A-103 Aras 02 | none | n/a | no | no | Whole, Malay: "Dua sheet dibuat: A-102 untuk Aras 01 dan A-103 untuk Aras 02, kerana A-101 sudah digunakan." | 2 rows; check: "A-101 dilangkau: nombor itu sudah digunakan..." | Created 26 (backend 12; card 30) | 0 | 3 | 15 |

The rows give seconds as Enter to the final card. "Waiting" is the time the question was open, which includes my own time to fill in the form.

### What changed and what did not

- **The backend asked the script's question itself in every run where the script stopped** (runs 2 to 8). In the stored events the question comes straight after the `NOTHING MADE` result, as an `assistant` row with `server_ask`, with no model step in between. The choices were the script's: the plan names plus "Skip <level>", or "Create a <type> for <level>" / "Skip <level>". In Malay prompts they were "Buat ... untuk <aras>" / "Langkau <aras>". One `ASK` line gave buttons; three gave one form.
- **No question before the script** in s01, s04 or s09. In s04 this is new: round 2 asked an unneeded question about the levels with no plan before the script.
- **No read-back after `MADE`** in any run.
- **No yellow "Not every change in this turn was applied" warning** in any run. While the question was open, the pane showed "0/1 done · 1 unchanged", and that line was gone when the turn ended.
- **Replies with "(i205a)" were shown whole** in runs 2, 3, 4, 5 and 7. In round 2 some were cut at "(". With one run per prompt this is only **consistent with** the fix.
- **New in run 1: the reply was replaced by "30 objects created."** The `report` call carried a full sentence that contained "(i205a)" and a colon, but the stored `turn_end` result and the pane both show the template. Round 2's s02 showed the model's sentence.
- **The skipped levels appeared in the checks in every case** (runs 2, 3, 4, 5). In runs 2, 3 and 4 they were also named in the reply sentence, which the brief says should not happen; run 5 kept them out of it.

### Problems found

1. **s09 did not ask which levels (run 5).** It took all levels itself: the stored script ran with `levelNames: []`. The sheets were right because "all levels" was the answer the table gives. However, the expected levels question never came. In round 2, s09 asked it both times.
2. **Run 3: the model's first code made a floor plan for a level that the drafter had skipped.** After the answers "Create Pentas, Skip Langkan", it wrote code for both levels with the intent "Create floor plans for Aras Pentas and Aras Langkan as the drafter asked". The backend refused it before it ran ("policy_rejected"), and nothing was made for Langkan. The guard held, but the model misread the form answer.
3. **Run 1: the reply sentence was replaced by a count template** ("30 objects created.").
4. **A "1 issue" card after a deliberate stop (runs 6 and 8).** After the drafter chose to skip and nothing was made, the pane showed an issue card: "The code ran but changed nothing. warning", with "Highlight in model" and "Save as command". It is the same misleading signal as the old yellow warning, in another form. In run 6 the line inside the Malay card was in English.
5. **The counts still disagree.** Toast vs backend "added" vs card `counts.created`: 26/12/30, 50/24/60, 59/39/85, 21/14/24. These are the same patterns as round 2.
6. **`report` rejected for a "next" suggestion that was too long** (runs 2 and 4). It was fixed on the next call.
7. While the stored script ran in run 7, the pane showed a passing "Apa yang akan saya tulis ... Pratonton" (what I will write, preview) card. It was gone when the question appeared, and nothing asked for confirmation.

### Setup for s08, and one void attempt

The first try at run 9 is void and not counted. The setup message "Create one new sheet numbered A-101 named Manual with title block (i205a) A1 Landscape. Nothing else." (the round 2 wording) was sent to the `sheet-set` playbook with an empty level list. The script then asked the every-level form instead of making one empty sheet. The setup turn was cancelled by the new chat, A-101 did not exist, and s08 then made A-101 and A-102. Round 2 handled the same wording correctly. Run 9 was redone with the setup text "Create one empty sheet: number A-101, name Manual, title block (i205a) A1 Landscape. Do not place any view on it and do not make any other sheet." That made A-101 Manual with no viewport, in its own chat (different session id).

### Tester errors

In run 1's check, a Revit drop-down closed when my check brought Revit to the front, so the Project Browser was not open. The filter text "A-10" was then typed into the rendering view as Revit shortcuts. The view went blank and the card showed "Undo is off: the model changed after this turn". The sheets were not touched. The model was discarded at the next reopen, and run 1 was then checked with the Project Browser open. After that, the Project Browser was opened in steps, with a screenshot check before any typing.

### Round 3 compared with round 2

| Prompt | Round 2 (merged build) | Round 3 |
|---|---|---|
| s01 | Right. Script first, then asked only which plan for Aras Tanah. The model asked; Pentas and Langkan were `SKIP` | Right, twice (Skip/Skip and Create/Skip). One form from the backend covering all three levels, as now designed |
| s02 | Right, no question | Right, no question; reply replaced by "30 objects created." |
| s04 | Right, with an unneeded question about the levels with no plan before the script | Right, no question before the script, one form after it: **consistent with the change** |
| s08 | Right, no question | Right, no question (after a second setup message; see above) |
| s09 | Right in both repeats: asked which levels, then the form | Sheets right, but **no levels question**: worse than round 2 on this point |
| s10 `tak payah` | Right; the model asked before any code ran | Right; the script ran first and the backend asked. An issue card was shown after |
| s10 create | Right | Right |
| s11 `no` / Skip | Right | Right; the same issue card |

One run per prompt shows no rate.

Raw data: every turn's `ai.copilot_turns` row and all `ai.copilot_turn_events` rows, unmodified, with screenshots, are in the session scratchpad `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/30f55bca-5f0d-43fe-8c78-ba44a71f8a1f/scratchpad/runs/R3/` (`<run>.events.json`). The checks inside Revit are in `verify/R3-*`, the void attempt is in `runs/R3-void/`, the unverified run 1 from the previous session is in `runs/R3-unverified/`, and the per-input log is `guard.log`.
