# CAD to BIM pane — Windows smoke checklist

**Date:** 2026-09-08  **Spec:** `docs/superpowers/specs/2026-09-08-cad-to-bim-pane-design.md` §3, §6, §8
**Gate:** every row PASS on both columns before the `feat/cad-to-bim-pane → develop` PR is opened. A FAIL is a finding to fix on the branch, not a note.
**Rig:** Windows, Revit 2024 (net48 payload) and Revit 2025 (net8.0 payload), 100% and 150% display scaling available, .NET 8 SDK installed (for the CLI).

## Prerequisites

1. Build from the feature branch at the commit under test:
   `powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version <x.y.z>` → `RevitCopilot-<x.y.z>-setup.exe` at the repo root.
2. `BINA_SYNC_PLUGIN_DIR` must be **unset** on the rig (a dev box with it set loads the repo `bin\` build instead of the installed payload and the install rows prove nothing).
3. Sample drawings. `Cad2Bim/README.md` §"Bring your own drawing": `*.dwg` is git-ignored in `Cad2Bim/` on purpose ("Copy your test file next to the checkout and pass its path") — the repo names no sample path. The original set lives on the engine author's machine; copy three plans from it to `C:\cad2bim-samples\sample-01.dwg`, `sample-02.dwg`, `sample-03.dwg` and one Civil 3D-authored DWG to `C:\cad2bim-samples\civil3d.dwg`. Record the real file names in the table.
4. Reference counts from the headless CLI, run once per sample from the repo root **before** opening Revit (same SMin/SMax as the pane, default 50 400):
   ```
   dotnet run --project Cad2Bim\Cad2Bim.Headless -- C:\cad2bim-samples\sample-01.dwg 50 400 --faces --symbols
   ```
   Read these lines from its output: `walls     N` (under `-- walls --`, after dedupe), `doors     N` and `other     N` (under `-- openings --`), `rooms     N` (under `-- spaces --`). Paste them into rows 6–8. Mapping: pane Walls ↔ `walls`, Doors ↔ `doors`, Windows ↔ `other`, Rooms ↔ `rooms`.
   The CLI is the *measuring surface*, not ground truth: `Program.cs` pre-filters to wall-named layers, adds `ClassifyWallsElsewhere` + `WallsFromOutlines`, and finds openings with `ClassifyOpeningsFromSymbols`, whereas the pane runs `ClassificationService.Classify` + `Elaborate`. Expect the same band, not identical numbers: walls within 10%, doors/rooms within ±2. A larger gap is a finding — write it in the Notes column with both numbers.
5. A template with no door/window families for row 14: in Revit, New → Project template (Metric), Manage → Purge Unused (all), Save As `C:\cad2bim-samples\NoFamilies.rte`.

## Checklist

Fill each result cell with PASS / FAIL / n/a plus the measured value where the Expected column asks for one.

| # | Step | Expected | 2024 (net48) | 2025 (net8) |
|---|---|---|---|---|
| 1 | Run `RevitCopilot-<x.y.z>-setup.exe`; then check the folders | `%APPDATA%\Autodesk\Revit\Addins\2024\BinaSync.addin` and `...\2025\BinaSync.addin` exist; `%LOCALAPPDATA%\Bina\RevitSync\versions\<x.y.z>\net48\` and `\net8.0\` each contain `RevitWebAppSync.dll` and `ACadSharp.dll`; the `net48` folder also contains `System.Memory.dll` | | |
| 2 | Start Revit at **100%** display scaling; open the Bina tab | Panel "CAD to BIM" sits after BINA AI; one large button "CAD to BIM" with a crisp 32 px icon (not blank, not the generic placeholder); tooltip shows | | |
| 3 | Set display scaling to **150%**, sign out/in, start Revit | Same panel and button; icon not blurred, not clipped; button label wraps to two lines | | |
| 4 | With **no project open**, click CAD to BIM; cancel the dialog; click again and pick `sample-01.dwg` | Button is enabled with no document; first click shows an Open dialog filtered to `*.dwg;*.dxf`; cancel does nothing; second click docks the pane on the right, status "Detecting…", Revit stays responsive, then linework + green walls + blue openings + amber rooms + counts appear | | |
| 5 | Click CAD to BIM and pick `civil3d.dwg` | No crash. Status line shows the unsupported-source text from spec §3 ("run EXPORTTOAUTOCAD first"); pane stays usable and "Change…" opens the dialog again | | |
| 6 | Open `sample-01.dwg` (real name: ______); time from OK to counts | Detect time ______ s (record). Pane counts W/D/Wi/R = ____ / ____ / ____ / ____ vs CLI `walls`/`doors`/`other`/`rooms` = ____ / ____ / ____ / ____; within the band in Prerequisite 4 | | |
| 7 | Open `sample-02.dwg` (real name: ______) | Same as row 6 — record both sets of numbers and the detect time | | |
| 8 | Open `sample-03.dwg` (real name: ______) | Same as row 6 — record both sets of numbers and the detect time | | |
| 9 | On `sample-01`, select Erase, click one wall (within 150 mm of its centerline); then Confirm with "Add to this project" in an open metric project | Wall turns red dashed; Walls −1, Erased 1; openings hosted on it and rooms bounded by it re-elaborate. After build the Revit wall count equals the pane's Walls count and the erased wall is absent at that location | | |
| 10 | Undo the build (Ctrl+Z); select Brush; drag a box over the partition Detect missed; Confirm | A green wall with a cyan box appears inside the drag box; Walls +1, Brushed 1. After build that wall exists in Revit at the brushed location | | |
| 11 | In a project with levels "Level 1" and "Level 2", open `sample-02`, target "Add to this project", level picker = Level 1, Confirm; then press Ctrl+Z **once** | Walls, doors, windows and rooms appear on Level 1 only (check the Level parameter on a wall and a door). One Ctrl+Z removes every created element; one Ctrl+Y restores them all | | |
| 12 | Target "Save as new .rvt", Confirm on `sample-03` | `C:\cad2bim-samples\sample-03.rvt` is created beside the DWG; it becomes the active document with the elements visible; close it; File → Open from disk → same elements present | | |
| 13 | Repeat row 12 with `sample-03.rvt` already on disk; answer Cancel, then repeat and answer OK | Cancel: an overwrite prompt appeared and the existing file's modified time is unchanged, nothing else saved. OK: file replaced (new modified time) and opened | | |
| 14 | Pane ⚙ → template path = `C:\cad2bim-samples\NoFamilies.rte`; target "Save as new .rvt"; Confirm on `sample-01` | Walls and rooms are built; status/report shows skipped openings = the pane's Doors + Windows count (no doors or windows placed, no exception); restore the template setting afterwards | | |
| 15 | After a successful "Add to this project" build, Brush two more walls, Confirm again | Only the two brushed walls are added: Revit wall count rises by exactly 2; no duplicate wall sits on any previously built centerline (select-all walls → Properties count) | | |
| 16 | **2024 only.** After rows 4–6 on Revit 2024, open the newest `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2024\Journals\journal.*.txt` | No `FileLoadException`, no `Could not load file or assembly 'System.Memory`, no `ACadSharp` load error after the CAD to BIM click; Detect completed (row 6 has counts) | | n/a |
| 17 | With the pane open and a drawing loaded: switch the active view to a 3D view and back to the plan; then close the project (pane still open); then open another project | Pane keeps its drawing and counts across the view switch; after the document close the pane stays open and its controls respond; "Add to this project" works in the newly opened project | | |
| 18 | Erase a wall **after** it was built (row 9 project), then Confirm again | The built wall is not deleted (spec §3: undo is the drafter's tool); no new wall is created for it; counts show Erased +1 | | |

## Builder rows (Task 11 — `Cad2BimBuilder`)

Same rig, both columns. Prefixed `C` so the numbered-row checks in Task 22 Step 2 stay at 18.

| # | Row | Expected | 2024 (net48) | 2025 (net8) |
|---|-----|----------|---|---|
| C1 | AddToProject, Level 1, Stack, sample DWG with ≥2 plans, Confirm | Walls on Level 1 and on "CAD Level 2" (elevation = Level 1 + 3000 mm); report Walls/Doors/Windows/Rooms equal what the pane counted minus Skipped*; report Elapsed shown | | |
| C2 | After C1, one Ctrl+Z | Every wall, door, window, room and "CAD Level 2" gone; Undo list shows one entry "CAD to BIM" | | |
| C3 | AddToProject, KeepPosition, same DWG linked at origin in the plan view | Walls sit on top of the linked linework; all on Level 1; no "CAD Level N" created | | |
| C4 | NewFile (Save as new .rvt), default template | `<dwgdir>/<dwgname>.rvt` exists, becomes the active document, reopens after Revit restart; report OutputPath = that path | | |
| C5 | NewFile with Settings → template override pointing at a metric template with no door/window families | Error null, Doors = Windows = 0, SkippedOpenings > 0, walls present | | |
| C6 | AddToProject with no document open | Status shows "Open a project first."; nothing created; controls unlock | | |
| C7 | After C1, Brush a box that adds N walls, Confirm again | Report Walls = N (only the brushed walls); no duplicates of C1 walls; second undo entry "CAD to BIM" | | |
| C8 | NewFile with `TemplatePath` = an existing non-.rte file (forced failure) | Error = innermost Revit message; no extra document in Revit's Switch Windows list; no .rvt written | | |
| C9 | AddToProject on Level 2 (picker), Stack, 2-plan DWG | First plan on Level 2, second on the next existing level above (or "CAD Level 2" at Level 2 + 3000 mm when none) | | |

## Pane rows (Tasks 16–19)

| # | Row | Expected | 2024 (net48) | 2025 (net8) |
|---|-----|----------|---|---|
| E1 | With a drawing loaded, toggle the Copilot pane's moon (dark/light) button, then wheel-zoom in and out | Overlay re-colours to the dark/light palette (green/blue/amber/red/cyan swap to their dark values); wall centrelines stay 2 px and bands never thinner than 3 px at any zoom; room names stay upright and centred | | |
| E2 | Pane ⚙ → set SMin 400 / SMax 300 → OK; then fix to 50 / 400 → OK; reopen ⚙ | First OK: red "Minimum thickness must be smaller than the maximum." and the window stays open. Second OK: window closes, pane re-detects with the new range. Reopen: values persisted (`%APPDATA%\RevitWebAppSync\config.json` has a `cadToBim` key); window is centred on Revit and follows the Copilot dark theme | | |
| E3 | Change a layer's role chip (e.g. `0` → WALL) on a drawing with walls on layer 0; then change the thickness range | Re-detect runs (status "Reading…" → counts); walls previously erased stay erased and walls already built stay out of the next Confirm after the re-detect (carry-forward by centreline) | | |

## Sign-off

| Field | Value |
|---|---|
| Build version (`-Version`) | |
| Branch / commit | `feat/cad-to-bim-pane` @ |
| Tester / date | |
| Revit 2024 build | |
| Revit 2025 build | |
| Verdict (all rows PASS?) | |
| Findings (row → note) | |
