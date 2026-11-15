# Live test round 17 (pipes), 15 Nov 2026 (binacloudpc)

**No value was written to an element, type or parameter the prompt did not name. No model was saved.**

The only write that was not a copilot write was my own slip. At 12:51 I opened a pipe's Pipe Segment dropdown, and closing it committed "Modify element attributes" with the same value (Copper - K). I undid it at once from Revit's Undo list, and the list was empty afterwards.

Headlines:
- **Snowdon Towers Sample HVAC has no pipes.** Copilot said so correctly (P1-hvac). With ningyo-94's OK, the pipe runs moved to a copy of **Snowdon Towers Sample Plumbing**.
- **The P2 write was right (126 of 126), but Revit did not select the pipes at the turn's end.** The log says "select turn-end n=126: Revit holds 0". The bar's Select all then selected all 126.
- **P4 was right:** nothing was written, and the SKIP lines gave the reason and the count. The turn was long-winded, though (8 steps, 2 failed calls).
- **P3 was not applicable.** No pipe type in the Plumbing model routes more than one segment.
- **KD3 was not done.** The E-c07 turn hit the 120 s script limit (162 s), so there was no "need a look" result to step through. A person was on the PC just before the timeout, and Windows-MCP was unavailable from 13:10.

Each prompt ran once, so nothing here is a rate. "Pass" means one run behaved as intended, which is consistent with working.

## Header

| | |
|---|---|
| PC | binacloudpc, Revit 2027.3, display at 200 %. Before every run: boot time 2026-11-14 01:51:0x and minidump `111426-50265-01.dmp` unchanged, SAMSUNG monitor present, session Active, tunnel health 200.<br>WMI's boot time read :02 at first and :05 later. That is jitter, not a reboot: the logon time and Revit 18200 were unchanged. |
| Windows-MCP | At 12:13–12:18 it said CONTROL_UNAVAILABLE (gen 4100, unchanged since last night).<br>At 12:19 I restarted it on ningyo-94's instruction (Stop/Start-ScheduledTask `windows-mcp-server` and its own python/cmd processes). It came back ready, gen 1.<br>Later a person was on the PC (RustDesk from 192.168.100.81): USER_CONTROL/unavailable at 12:46–12:49 and 13:06–13:07, and unavailable from 13:10:39 to the end. I sent no input while it was held.<br>RustDesk was not touched. |
| Mac | On battery the whole round, from 100 % down to 88 %. No sleep after 11:52:37. `caffeinate -dimu` ran. The tunnel (`ssh -R 18007`) was restarted at 12:13. |
| Add-in | `C:\Users\Public\rel-test\r16-addin-gabung5`. The DLL is `0.0.116+e88e2fbbb24e…`, and the loader log said "loaded 0.0.116.0 from '…r16-addin-gabung5\net10.0-windows'" on every start.<br>Revit was started with `BINA_SYNC_PLUGIN_DIR` only, except for KD3, which used `r15\open-hold.cmd` with `BINA_VIEW_HOLD_TEST_SECONDS=3`. |
| Backend | `3e1727a6` on the Mac on 8005 (`lh/lt-r2`), unchanged. |
| Models | `rel-test\snowdon\Snowdon Towers Sample HVAC.rvt` (67 warnings, 5 unresolved links, ignored).<br>`rel-test\snowdon\Snowdon Towers Sample Plumbing.rvt`, copied at 12:31 from Revit's Samples folder on ningyo-94's OK (39 warnings, 3 unresolved links, ignored).<br>`sheets-test.rvt` for KD3.<br>The mtimes of all three are unchanged after the round. |
| Exports | `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/133cf0f6-d046-4b44-b954-ab589fae2879/scratchpad/runs/R17/`:<br>`notes.md` has every run in detail; `*.json`/`*.events.json` are the turns; `*.clicks.txt` are the add-in's log lines; `shots/` holds the screenshots (`P2-turn-end-4k.png` is 3840×2160). |

## Results

| Run | Result | Evidence |
|---|---|---|
| **P1-hvac** "How many pipes are in this model, by level and by pipe type?" | **Pass** | find_elements then model_overview, 3 steps, 6 s, 0 failed.<br>Reply: "The host model holds 0 pipes: only the pipe type PVC - DWV exists, with 0 instances … The plumbing link … is unloaded and was not searched".<br>Revit check: {3D}, select all, then Filter lists Air Terminals 509, Duct Fittings 997, Ducts 1053, Flex Ducts 31, Mechanical Equipment 37, Grids, Levels and Scope Boxes, and **no Pipes**. |
| **P1b** (extra) "How many ducts are in this model, by level?" | **Pass on the total**; the per-level number was not proven | 2 steps, 8 s.<br>Reply: "1053 Ducts … L3 319, L4 269, L5 183, L2 182, L1 - Block 37 76, L1 - Block 35 14, L1 - Block 43 5, R1 4, M1 1". The sum equals Revit's Filter count, 1053.<br>The ceiling plan of L1 - Block 35 holds 27 ducts whose Reference Level reads `<varies>`. So the 14 is not contradicted, but it is not checked either. |
| **P1** (Plumbing copy) | **Pass**, with a clumsy reply | find_elements group_by level, type: 2 steps, 3 s.<br>3051 pipes: L4 766, L3 581, Parking 516, L2 381, L5 346, L1 - Block 37 160, L1 - Block 35 126, L1 - Block 43 66, R1 64, R2 45.<br>Revit check: Select All Instances of Cast Iron in the entire project gives "Pipes (107)". That equals Copilot's data (Parking 35 + L5 8 + R1 64).<br>**Clumsy:** the reply gives no per-type totals although they were asked for ("by type Copper, PVC - DWV and Cast Iron make up those counts"). From its own data they are Copper 2267, PVC - DWV 677 and Cast Iron 107. It then offers "break down by level and type together" as a Next step. |
| P1, one pipe (L1 - Block 35, 126 pipes) | — | Copper; Pipe Segment **Copper - K**; Diameter 1/2"; Domestic Hot Water. **The dropdown offers only Copper - K.**<br>Mechanical Settings > Segments and Sizes lists 16 segments: Copper K/L/M; Iron, Ductile 22/30/Class 150–350; PVC Rigid Sch 40/80; Stainless 5S/10S; Steel, Carbon Sch 40/80.<br>Routing preferences: Copper routes Copper - K only, PVC - DWV routes PVC Rigid Sch 40 only, and Cast Iron routes Steel, Carbon Sch 40 only. |
| **P2** "Set Comments to Checked on the pipes on L1 - Block 35." | **Write: pass. Turn-end selection: fail** | set-parameter printed "WRITE instance \| 126 pipes on L1 - Block 35 \| @0 \| Comments = Checked" and "ALSO Comments \| Type Comments".<br>The wide-write question "Before I write: Comments = Checked on 126 elements." was answered with Write to all.<br>set_parameters: 126 changed, read back 126 of 126. 3 steps, 37 s (23 s waiting), 0 failed.<br>The bar sits top left on one line: "● 126 pipes changed · 12 of 126 in this view \| Select all \| Hide \| Undo \| ✕", with 5 "2" badges and 2 dots for the 12 in view.<br>**Revit did not select:** Properties stayed "Floor Plan", and the status bar read :0. The log has "select turn-end n=126: Revit holds 0, view activated (answer 1) …".<br>The bar's Select all gives "Pipes (126)", Reference Level L1 - Block 35, and **Comments "Checked" with no `<varies>`**, so the count in Revit equals the card.<br>Undo on the bar: the marks went at once, and the pane said "Undid 1 BINA change(s) in Revit: BINA: Set parameter Comments." Revit's Undo list was empty afterwards. |
| **P3** pipe segment, allowed | **Not applicable** | No pipe type routes a second segment; see the routing row above. The types Standard and Chilled Water exist but have no pipes. |
| **P4** "Change the pipe segment of the pipes on L1 - Block 35 to Copper - L." | **Pass** (nothing written, said plainly), but long-winded | set-parameter printed "values to set: 0", "SKIP 39 pipes: not in the routing preferences of PVC - DWV" and "SKIP 87 pipes: not in the routing preferences of Copper".<br>Reply: "Nothing changed: 126 pipes skipped; Copper - L not in their pipe types' routing preferences."<br>There was no bar and no marks, and Revit's Undo list was empty.<br>**But:** 8 steps and 23 s, with 2 failed calls (see below). |
| **KD3** (stepper + `zz` inside a 3 s hold) | **Not done** | The E-c07 turn asked "Change 205 walls?", I answered Change 205 walls, and the change-type run then **timed out**: "Stopped at line 48 after 162.0 s: the code passed the time limit of 120 s."<br>The same-call retry was refused, and the model's own code was refused too ("nothing it did was kept. Do not do this job with your own code …"). The reply said plainly that nothing changed and suggested a smaller scope. 6 steps, 208 s, 3 failed.<br>In R16 the same prompt rolled back in about 19 s with 16 "need a look".<br>A person was on the PC until 13:07:32, and the script ran from 13:07:38. Windows-MCP was unavailable from 13:10:39, so there was no stepper, no keys and no screenshot.<br>Revit was closed over ssh without saving, and the task no longer sets the hold variable. |

## What looks wrong or clumsy

1. **P2: the turn-end selection is not held** ("Revit holds 0" for n=126). The bar's Select all works on the same Revit.
   - Two things differ from R16, where the selection was held. The Project Browser still had a type ("Cast Iron") selected from my earlier check. And 114 of the 126 pipes are outside the active view.
   - Which of the two matters, if either, is not proven.
2. **P1: a "by type" question gets no type totals.** The reply only names the types, although the data has the per-type counts.
3. **P4: too many steps after a clear answer.** The first set-parameter already gave both SKIP lines. The model then:
   - reran set-parameter with an odd key, `values {"segment": …}`;
   - sent find_elements with `group_by`/`sum` inside the query, which failed with invalid_args;
   - ran find_elements again, then inspect and a knowledge lookup;
   - ran free code that failed with CS1061 `'PipeType' does not contain a definition for 'Segments'`;
   - ran free code again, read only.

   The card's 5 checks say the two SKIPs twice in other words.
4. **KD3: E-c07 timed out at 162 s** where it took about 19 s in R16. The cause is unknown (a person on the PC, the 3 s hold variable, or the PC itself).
   - The refusal path worked: no retry, no own code, and a plain "nothing changed".
   - But this "need a look" setup could not be run.
5. **The Undo wording changed:** "Undid 1 BINA change(s) in Revit: BINA: Set parameter Comments." comes as a new pane message instead of the earlier "Undone. The model is back to how it was …" card.
6. A clicks-log line "12:17:24 select mark-click n=1" in the old Revit (18200) was a person's click while Windows-MCP was unavailable, not mine.
7. Snowdon HVAC is a duct model; pipe prompts need the Plumbing sample.

## R17b recheck (13:20–13:33)

**Builds:**
- Add-in `4eb8e8dd` ("A selection Revit did not take is set again once the drawing view is active"), Debug 0.0.116, 0 errors, staging feed.
  - DLL `0.0.116+4eb8e8ddedd4…`, folder `C:\Users\Public\rel-test\r17-addin-gabung6`. The loader log confirmed this folder, and no test variable was set.
- Backend `1615accd` on 8005: `0777ab07` and `1615accd` after `3e1727a6`, changing `copilot_find.py`, `copilot_set.py`, `copilot_fill_turn.py` and one test file. No migration or dependency change.
- Windows-MCP was unavailable (gen 296), so I restarted it the same way as before; it came back ready.
- Model: the Plumbing copy, not saved.

| Run | Result | Evidence |
|---|---|---|
| **P1'** totals by type | **Pass on the totals; one wrong number** | 2 steps, 6 s, 0 failed.<br>The find summary ends "… By type: Copper 2267; PVC - DWV 677; Cast Iron 107."<br>The reply gives "By type: Copper 2267, PVC - DWV 677, Cast Iron 107." too.<br>**But** its level part reads "L5 346 (257 Copper, 82 PVC - DWV, 7 not shown — actually 257+81+8)". The data says PVC 81 and Cast Iron 8, so the reply has wrong numbers and then corrects itself in the same sentence. |
| **P2'** selection after a Project Browser click | **Pass** | Setup: plan "PLAN L1 - Block 35 Water", then one click on the type "Cast Iron" in the Project Browser, and no canvas click.<br>Write to all: 126 of 126 read back. 3 steps, 48 s (39 s waiting), 0 failed.<br>**At the turn's end Revit shows Pipes (126)** (status :126, Modify \| Pipes).<br>Log: "select turn-end n=126: Revit holds 126, view activated (answer 2), the view keeps the keyboard until Revit is idle, keys dropped meanwhile, set again after the activation".<br>There is no "marks idle: … set again" line.<br>Activation lines: "keyboard back in the window that had it (HwndWrapper[…d98936b3…]) after 3 Idling calls, 859 ms, 0 key(s) dropped" and "watched 2 s: the view took the keyboard again 0 time(s)".<br>Bar: "126 pipes changed · 78 of 126 in this view".<br>**`zz` with no click went nowhere visible:** it was not in the pane's input, Revit stayed "Ready" with Pipes (126), no Zoom In Region started, and the view did not change. The last click before was the card's "Write to all" button, which then went away.<br>Undo on the bar gave "Undid 1 BINA change(s) …", and Revit's Undo list was empty. |
| **P4'** report and stop | **Half** | Before the playbook, the model made 5 calls:<br>find_elements; lookup; inspect {handle, limit}, which **failed** (invalid_args); lookup "Copper - L pipe segment"; inspect.<br>Then set-parameter (48 s): "NOTHING MADE: no element can take this value; each SKIP line below says why. Tell the drafter these reasons as they are and stop …", plus SKIP 39 (PVC - DWV) and SKIP 87 (Copper).<br>Then it reported **at once**: no rerun and no code of its own. 5 steps plus the report, 59 s, 1 failed. Nothing was written.<br>The pane headline shows only "Pipe Segment was not changed."; the reason sits in the checks.<br>**One check is wrong:** "Current Pipe Segment on the 126 pipes on L1 - Block 35 is Copper - K." 39 of them are PVC Rigid Sch 40.<br>The report had two Next chips; the pane shows only "Then rerun the Pipe Segment change". |

R17b findings:
1. P1' writes wrong numbers in a level breakdown and then corrects them inside the sentence ("82 … 7 not shown — actually 257+81+8").
2. P4' runs 5 lookups and reads, one of them failing, before the set-parameter playbook. After the playbook, the stop works.
3. P4' has a wrong "current segment" check (it says all 126 are Copper - K).
4. P4' drops the first Next chip, and the headline loses the reason.
5. P2': after answering a card button, typed keys reach neither the input nor Revit. The keyboard is "back in the window that had it", but no control in it has the focus.

## What is still running or changed

Nothing below is restored.

**PC:**
- **Revit pid 15060 is open** on the Plumbing copy with add-in `r17-addin-gabung6` (no test variable). It is unsaved, and the P2' write is undone.
- The task `BinaLiveTestR7` runs `r7\open.cmd` with `r17-addin-gabung6` and the Plumbing copy.
- Windows-MCP was restarted twice (12:19 and about 13:20).
- New folders and files:
  - `C:\Users\Public\rel-test\r17-addin-gabung6` and `addin-gabung6.tgz`;
  - `C:\Users\Public\rel-test\snowdon\Snowdon Towers Sample Plumbing.rvt` (copy, read-only attribute);
  - `C:\Users\Public\rel-test\r15\rmenu.ps1` (an unused right-click helper);
  - `r13\shot.png`, overwritten.
- Not touched: port 18002, the installed 0.0.117/0.0.118 folders, `Desktop\revit-sample-model`, the Samples folder under Program Files (only read, for the copy), and RustDesk.

**Mac:**
- The backend on 8005 runs **`1615accd`** (`lh/lt-r2`, detached).
- The build worktree `…/b8bbfc99-…/scratchpad/addin-build` is detached at `4eb8e8dd`.
- The tunnel's `ssh -R 18007` was started at 12:13.
- `caffeinate -dimu -t 14400` runs (started 12:13).
- The Mac is on battery (85 % at 13:31).
