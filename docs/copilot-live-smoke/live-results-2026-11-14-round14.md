# Live test round 14, 14 Nov 2026 (binacloudpc)

**No value went to an element, a type or a parameter the drafter did not name.** In all three F-t10 runs, only the five named parameters were written, all as type parameters. In SETP-b only Type Comments was written, never the doors' instance Comments. In CLICK the model again split "Wattage Comments" into "Wattage" + "Comments", and the backend put it back to Wattage Comments before writing.

What is new or worse this round:
- **SEL:** the drafter never sees the door answer. The model's reply ended "…: your 3 doors have Mark 02 (Fire Rating 2 jam) and Mark 01 twice (Fire Rating T/B)", but the pane shows only "I selected all 828 Walls on Aras 01."
- **CLICK:** the type tile's arrow still does not show the selection in Revit.
- **KEY:** the hidden-result pill sits bottom-left, not in the key's corner.
- **SETP-b:** the card's Undo got stuck on "an undo is already running", although Revit had undone the write.

The PC stayed up for the whole round (16:08–17:05). The Mac did not sleep and was on its charger. Each prompt ran once, so nothing here is a rate. "Consistent with fixed" means one run behaved the way the fix intends.

## Header

| | |
|---|---|
| PC | binacloudpc, Revit 2027.3. Boot time **2026-11-14 01:51:02** and newest minidump `111426-50265-01.dmp`: unchanged before every run. Monitor **SAMSUNG present** before every run. Display scale **200 %** (AppliedDPI 192). The user session was Active. Windows-MCP reported USER_CONTROL at 16:39:21–16:41:29, while I was typing the SETP-b prompt. I stopped input and polled: control was "ready" from 16:41:48 and stayed so for 4 min. No window or chat I had not opened appeared, so I went on. |
| Add-in | **0.0.114** = `66d2d82f` ("The key sits in its corner on a scaled display: the overlay uses the display's real scale"; the brief's `66d2d82e` does not exist, and ningyo-6d corrected it to `66d2d82f`). It includes `c1ccf237` ("A click on a mark shows its one element as Revit's selection").<br>Built Debug with the staging feed: 0 errors.<br>Folder `C:\Users\Public\rel-test\r14-addin-0.0.114`, loaded through `BINA_SYNC_PLUGIN_DIR` (`r7\open.cmd` sets nothing else, so no probe).<br>The DLL reads `0.0.114+66d2d82f…`, and the loader log reads "loaded 0.0.114.0 from 'C:\Users\Public\rel-test\r14-addin-0.0.114\net10.0-windows'" on every open. |
| Backend | **`4b753b41`** ("Set-parameter: the ALSO line says how many longer names it left out"). Runs on the Mac on `127.0.0.1:8005` from `lh/lt-r2` (detached), reached through the reverse tunnel on 18007. Against `8d991cb3`: no migration and no dependency change. |
| Tunnel | Found down at the 16:35 pre-check (after CLICK; that turn had ended at 16:25). The Mac had not slept, the backend answered 200 and the DB was up. Restarted at 16:35:48. |
| Fixtures | `sheets-test.rvt`; `downlight-model.rvt`. I reopened the fixture without saving before each run, except KEY and SEL, which reopened and then opened a plan, sending with `--noreopen`. **No model was saved**: the file times are unchanged (sheets-test 2026-11-11 16:26, downlight 2026-11-13 14:28). |
| Exports | `/private/tmp/claude-501/-Users-ashraf-orca-workspaces-revit-addin-sync-copilot-live-test/e4bd1d06-6273-45a8-b2a2-1e91debb504b/scratchpad/runs/R14/` holds:<br>`<run>.json` and `<run>.events.json`;<br>`notes.md` (every run's detail);<br>`KEY.log.txt` (the add-in's log lines for KEY);<br>`shots/` (`*-4k.png` are 3840×2160). |

## Round 13 / round 14

| Run | Round 13 | Round 14 |
|---|---|---|
| **KEY** (E-c07 → result key) | Key always top-left (assumed 100 % scale) and covered 1–2 rings. Rings 36 px at 4K | **Key: consistent with fixed. Pill: not.** Details below |
| **CLICK** (type tile → arrow → ring → Undo) | The arrow said "38 dipilih", but Revit showed "3D View", `:0` until a click in the canvas. A ring click gave 38, not 1 | **The arrow defect is still there.** None of the three allowed tries shows the selection. **A ring click selects one fixture**, but only on the second click. **The card's Undo works.** Details below |
| **SETP** (a) Comments=Done, (b) Type Comments=Ujian | SP-4: 60 doors, read back 60 of 60 | **(a) Consistent with fixed.** No question, 60 doors, read back 60 of 60. The script prints `ALSO Comments \| Type Comments`.<br>**(b)** The model sent `{"Type Comments": "Ujian"}`, so it got the normal type-parameter flow: "Change all 61 instances? This is a type parameter: it changes all 61 instances of 10 types…". After "Change the type", only Type Comments was written (10 types, read back 10 of 10).<br>**The card's Undo got stuck** (see below) |
| **SEL** (3 selected doors + highlight walls) | Not done | **The model was right but the drafter does not see it.** `inspect` was sent with `target {selection: true}`, beside the find, and returned the 3 selected doors, not walls. The reply was cut (see below) |
| **F-t10** (5 typed values, 2 downlight types) | `8d991cb3` e1–e5: 5 of 5 clean | **3 of 3 clean** (CLICK, F-t10-2, F-t10-3). Only the five parameters were written, all type_param true, on 38 + 14 ids |

### KEY in detail (0.0.114, sheets-test, plan Aras Tanah, AG off)

- **The turn:** ask "Change 205 walls?" → "Change 205 walls" → Revit rolled back ("Can't keep elements joined" ×10, invalid dimension references, two door types it could not make). 4 steps, 66 s (41 s waiting), 1 failed. I touched nothing after the turn ended.
- **The key** opened by itself in the **top-left corner**, measured on the 4K shot `KEY-2-turn-end-4k`:
  - the canvas starts at x=556, y=324;
  - the key's outer border is at x=584, y=352;
  - so the key sits **28 px in from the left and 28 px from the top** (24 expected; the 4 extra px may be the key's border or shadow).
  - No ring is under the key.
- **On the downlight result (CLICK)**, the key **moved to the top-right corner** once the zoom put rings under the top-left corner. That is the first corner with no ring under it.
- **Rings, at once, without touching anything:**
  - files `ring-needslook-52-dk-2.bmp` (104×104), `-40-dk-2.bmp`, `-22-dk-2.bmp`, and `-52-dk-2-bright.bmp` after a row click;
  - the same isolated ring measures 36 px in round 13 (`SHOW111-1-turn-end-4k`, bbox 1585–1620) and **76 px** now (bbox 1565–1640). That is twice the size, with the same centre (1602, 1528).
- **Hide marks:** the rings and the key go. But the **pill "Copilot result hidden · 16 elements need a look [Show again]" sits bottom-left**, 28 px from the left and about 23 px above the canvas bottom. **It is not in the key's corner (top-left).**
- **Show in model:** the key and rings come back at once.
- **Row:** zooms in, shows a label "Can't keep elements joined. · 16 elements" next to the group inside the view, and the key reads "Showing Can't keep elements joined. · 16 of 16 [Show all]". **Show all:** "Showing all 16".
- **Zoom far in** (20 notches): the key reads "None of the 16 is on screen [Zoom to them]".
- **{3D}:** the key reads "in {3D}, this view · Showing all 16", with rings on the building.
- **Back to the plan:** the key reads "in Aras Tanah (01w_WIP) · None of the 16 is on screen". **The key follows the view, as on 0.0.113.**
- **Log, verbatim:**
  ```
  16:17:11.245  marks idle watch on
  16:17:11.412  marks in Aras Tanah (01w_WIP): due 16, added 16, Revit holds 16, first at (42.73, -15.06, 1.48) ft, image ring-needslook-52-dk-2.bmp
  16:17:11.413  marks idle: first call, result shown
  16:17:11.564  marks idle: zoom settled after 1 moves (the repaint's own nudge)
  16:21:07.650  marks idle: view Project View -> {3D}
  16:21:08.227  marks in {3D}: due 16, added 16, Revit holds 16, first at (42.73, -15.06, 1.48) ft, image ring-needslook-52-dk-2.bmp
  16:21:13.011  marks idle: view {3D} -> Aras Tanah (01w_WIP)
  16:21:13.055  marks in Aras Tanah (01w_WIP): due 16, added 16, Revit holds 16, first at (42.73, -15.06, 1.48) ft, image ring-needslook-52-dk-2.bmp
  ```
  - No line came after 16:21:13 while the view was idle, so there is no redraw loop.
  - During the zoom, every notch logged "zoom settled after 1 moves" and re-added the marks (full lines in `KEY.log.txt`).

### CLICK in detail (0.0.114, downlight-model, the F-t10 prompt)

- **The turn was right.** The fill playbook was the first call. The model sent `fields ["Wattage","Comments",…]` and `typed {"Comments":"1X10W",…}`. The backend wrote Wattage Comments, plus the four others, as type parameters on 38 + 14 ids. Nothing was written to Comments. 4 steps, 20 s, 1 failed (the model's `report next/0` was too long).
- Purple rings appeared at once on the 52 fixtures.
- **The 1X13W tile:** my first click only scrolled the pane; the second click on the tile worked. It zoomed in, the label read "…1X13W: Normal · 38 lighting fixtures", and the key read "38 of 52" (log `click focus-group n=38`).
- **The arrow** (log `click focus-select n=38 … ran 169 ms`): the card reads "Dipilih · 38 dipilih dalam Revit", but Revit shows Properties "3D View" and a status count of `:0`. I tried one thing at a time:
  - waiting 10 s → `:0`;
  - (1) moving the pointer over the canvas without a click → `:0`;
  - (2) clicking the Revit title bar → `:0`;
  - (3) one zoom notch over the canvas → `:0`.

  **None of the allowed tries shows the selection.** I did not press Esc.
- **Clicking one ring** (fixture 18):
  - **First click:** Properties "Lighting Fixtures (38)", `:38`, and no `mark-click` line in the log. The first click into the canvas only made Revit show the 38.
  - **Second click, same ring:** log `16:30:28.062  click mark-click n=1`; Properties **"Lighting Fixtures (1)"**, `:1`. The key read "fixture 18 of 38 selected".
  - The card was already on the 1X13W tile, so it did not need to scroll. But its text still read "38 dipilih dalam Revit".
- **Undo on the card:** "Dibuat asal. Model kembali seperti asal. 10 nilai dikembalikan seperti asal pada 2 jenis. Tiada yang lain berubah." In Type Properties:
  - 1X13W reads 1X13W / 0.00 A / 13.00 W / 0.00 V / 0.00 V;
  - 1X8W reads 1X8W / 0.00 A / 8.00 W / 0.00 V / 0.00 V.

  These are the old values.

### SETP in detail

- **(a)** The script's output, from the export:
  ```
  values to set: 1
  WRITE instance | 60 doors on Aras 01 | @0 | Comments = Done
  IDS @0 60 1042809,…
  ALSO Comments | Type Comments
  ```
  - The card shows the Next chip "Check door types' Type Comments separately if needed".
  - Undo on the card: "Undone. The model is back to how it was. 1 value put back to what it was on 60 elements. Nothing else changed."
- **(b)**
  - **What the model sent:** `values {"Type Comments": "Ujian"}`. The branch "NOTHING MADE: the drafter wrote Type Comments, never Comments by itself" was therefore not exercised.
  - **The write:** 10 types, read back 10 of 10, affecting 61 doors in the model. Nothing was written to Comments.
  - **Undo on the card, first click:** the card's "Hide marks" turned to "Show in model", and nothing else changed.
  - **Second click:** "Undo is off: an undo is already running.", with the button greyed out, and it stayed so.
  - **Revit had undone the write:** Revit's Redo arrow was active, and Type Comments of (PTr681a) 1500 x 2100 sd-a was empty.
  - **So the card never learnt that the undo was done.**

### SEL in detail

- **Setup:** Select by ID `1042809;1043374;1043424` in plan Aras 01 (07d_Dokumen). Revit showed Properties "Doors (3)", and the pane read "3 elements selected".
- **The calls, in one step:**
  - `find_elements {level Aras 01, Walls, highlight true}` → 828 walls;
  - `inspect {fields [Mark, Type Mark, Fire Rating, Type], target {selection: true}, include_type true}` → "3 elements (Doors: 3)":

    | Door id | Mark | Fire Rating |
    |---|---|---|
    | 1042809 | 02 | 2 jam |
    | 1043374 | 01 | T/B |
    | 1043424 | 01 | T/B |
- **The model's report:** "I selected all 828 Walls on Aras 01: your 3 doors have Mark 02 (Fire Rating 2 jam) and Mark 01 twice (Fire Rating T/B)."
- **What the pane shows** (and the stored reply): "I selected all 828 Walls on Aras 01." **Everything after the colon is cut.** The card's checks mention the doors' type parameters but not their values.

## New or worse

1. **SEL: the answer to the drafter's question is cut off.** The part of the reply after ":" (the doors' Mark and Fire Rating) does not reach the pane.
2. **CLICK: the type tile's arrow** still does not show the selection in Revit on 0.0.114. Hovering the canvas, clicking the title bar or one zoom notch does not help. Only a click in the canvas does, and that click is then not taken as a ring click.
3. **The first click** into the pane, and the first click into the canvas, **is often lost**:
   - the tile and the SETP-b Undo needed a second click;
   - the first ring click did not reach the add-in.
4. **SETP-b: the card's Undo stuck** on "an undo is already running" while Revit had already undone the write.
5. **KEY: the hidden-result pill sits bottom-left**, not in the key's corner.
6. The model still sends malformed arguments now and then:
   - `report next/0` too long (CLICK);
   - `report` with `rows` (F-t10-3).

   The model also still splits "Wattage Comments" into two fields (CLICK). The backend corrected it.

## What is still running or changed

Nothing below is restored yet.

**PC:**
- Revit is open on `downlight-model.rvt`, fresh and unsaved, with add-in 0.0.114.
- The task `BinaLiveTestR7` points at `r14-addin-0.0.114` through `r7\open.cmd`.
- New this round: the folder `C:\Users\Public\rel-test\r14-addin-0.0.114` and `addin-0.0.114.tgz`.
- `r13\cap.ps1` was reused for the 4K shots, and it overwrites `r13\shot.png`.
- Hardware acceleration and the display are untouched.
- Not touched: port 18002, and the installed 0.0.117 and 0.0.118 folders.

**Mac:**
- The backend on 8005 runs **`4b753b41`** from `lh/lt-r2` (detached).
- The tunnel's ssh process (18007 → 8005) was restarted at 16:35.
- `caffeinate` runs as pid 13628 (this round, at most 6 h). Pid 73749 from round 13 is still running.
- The build worktree `…/b8bbfc99-…/scratchpad/addin-build` is detached at `66d2d82f`.
