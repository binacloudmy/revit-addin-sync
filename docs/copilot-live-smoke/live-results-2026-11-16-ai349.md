# AI-349 live proofs: results of 16 and 17 November 2026

One run per prompt shows that it can work. These are not success rates. The model `sheets-test.rvt` was never saved.
Machine: the tester's laptop, Revit 2027.3, backend on the same machine (Docker Postgres, uvicorn).

## Builds

| Part | Items 1 to 4 | Item 5 |
|---|---|---|
| Add-in (revit-addin-sync) | `feat/copilot-decision-card` `4eb8e8dd` | `fix/hv-final-selection` `2d0eb8e0` |
| Backend (bina-ai) | `feat/copilot-runnable-playbooks` `3b78981f` | `merge/hv-combined-develop-4` `51beb20c` |

Item 5 ran on Aiman's branches, which are not in `develop` yet (AI-348 was still In Progress on 16 November). Run it again once the merge is in.

## Results

| # | Item | Result | Evidence |
|---|---|---|---|
| 1 | Pipe segment changed to another allowed segment | Run, with gaps. One defect (below). | See "Item 1" |
| 2 | Keyboard safety of the bar's stepper | Pass | See "Item 2" |
| 3 | "205 walls" turn time | Pass | 59 to 71 s over six runs |
| 4 | "Undone" after undoing a type change | Pass | Under about 5 s |
| 5 | Result view on the merged build | Pass on the pre-merge branches, 2 gaps | See "Item 5" |

## Item 1: pipes

The sample model has one pipe type (`UPVC`, one segment), so the test setup was built in the model:

- `UPVC` got a second routing segment, `Plastic - Schedule 80` (two segments).
- A second type, `UPVC-ONE-SEGMENT`, routes only `Plastic - Schedule 40`.
- The 125 mm size was removed from `Plastic - Schedule 80` in Mechanical Settings.
- On Aras 01: marks 11, 13, 15 changed to `UPVC-ONE-SEGMENT`; marks 17 and 21 set to 125 mm. The other 17 pipes stayed at 100 mm. (The type change and the two diameter changes were made with Copilot's own write steps, so the setup is not fully independent of what was tested.)

Prompt 11, "Change the pipe material on Aras 01 to Plastic - Schedule 80" (Plastic names, not the page's PVC names):

- No question. 19 pipes changed in 10 s. Card: "3 pipes: not in the routing preferences of UPVC-ONE-SEGMENT"; "a pipe's material is its instance Pipe Segment; use the Undo in the receipt to revert all 19".
- Read back: marks 11, 13, 15 stayed `Plastic - Schedule 40` at 100 mm. Per segment on Aras 01: Schedule 80 on 19 pipes, Schedule 40 on 3.
- **Defect:** marks 17 and 21 (125 mm) were not skipped. They are now `Plastic - Schedule 80` at 125 mm, a size that segment no longer has. The "no size" skip in the script did not apply.

Prompt 12 (by hand):

- For mark 11, Revit's own Pipe Segment dropdown lists only `Plastic - Schedule 40`, so skipping the routing case matches Revit.
- The odd-size case was **not** tried by hand, so it is not known whether Revit itself refuses a 125 mm pipe on Schedule 80. Copilot's write was accepted.

Not captured: the script's WRITE and SKIP lines, and the family and type of the fittings before and after. Undo was not pressed on the 19 pipes.

## Item 2: stepper keyboard

Revit started with `BINA_VIEW_HOLD_TEST_SECONDS=3`. After the "205 walls" turn (bar "16 elements need a look"), one click on `›` and then `zz` typed on a real keyboard within about 2 seconds, no other click:

- Nothing started in Revit; the selection stayed.
- clicks log: `view activation: keyboard back in the window that had it after the test's 3 s, 3078 ms, 6 key(s) dropped`.

Keys sent by script (SendKeys) showed `0 key(s) dropped` three times; that was the script, not the add-in. The count of 6 is more than the 2 letters typed; likely auto-repeat or key-up events are counted. It does not change the result.

## Item 3: "205 walls" timing

Prompt: "Change all walls of type DB1 Batu bata 110mm to the DB2 Batu bata 55mm type in the whole model". Question "Change 205 walls?" answered with "Change 205 walls".

Card times: 71, 60, 59, 71, 59 s (and 70 s on the item 5 build). The two 71 s runs came right after a Revit start. Each run ended with Revit rolling the change back ("Can't keep elements joined" x10, invalid dimension references, two door types it can't make), 205 walls unchanged, 16 elements flagged. The laptop was not quiet (backend, Docker, and the tester driving it). None came near the 120 s limit.

## Item 4: time to "Undone"

20 doors changed to another type, then Undo on the card. The first screenshot after the click already showed "Undone. The model is back to how it was." (under about 5 s; the ticket reports 10 to 25 s).

The change went through a question card. Typing the answer worked. Clicking an option on that list card did not send the answer (the run sat on "Needs input" for about 3 minutes until the answer was typed); the "Change 205 walls" button on another card worked. Not repeated.

## Item 5: result view (Aiman's branches)

| Check | Result |
|---|---|
| Selection at the end of a turn with no click in the canvas | Pass: Revit showed `Doors (58)` and `Walls (1458)`, matching the cards |
| `zz` typed with no click afterwards | Pass: both letters in the pane's input, no Revit command |
| Click on a card row | Not testable on a count table: the 10-row door-type card has no highlight, the click did nothing |
| Stepper `›` and counter | Pass ("5 of 16 on screen", wall selected and zoomed) |
| Click on a dot or number badge | Pass (selects one wall; tooltip "Change wall's orientation") |
| Zoom to all, Revit zoom in and out | Pass (badges follow the view) |
| Hide, Show again | Pass (bar reads "Copilot result hidden · 16 elements need a look") |
| Amber numbered badges with the stepper | Pass (16, 14, 13+, 12, 6+, 8+, 10+) |
| English labels for a Malay prompt | Labels, buttons, tooltips English; the reply and notice are Malay |
| Undo from the bar | Pass: "Undid 1 BINA change(s) in Revit: BINA: Set parameter Comments", Redo active |

The 6 unloaded links were reported as not searched in each find.

## Anything that failed

- Prompt 11 changed the two 125 mm pipes instead of skipping them (item 1).
- An option click on a question card did not send the answer (item 4). Needs a repeat before it is called a defect.
- Selecting from the pane showed "Selected in Revit: 22" while Properties lagged until a click in the canvas, on the pre-merge add-in build only.
