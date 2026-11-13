# AI-345: CAD to BIM walls, doors and windows

Branch `ai345-onto-flow`: the AI-345 build change (first written on `origin/develop` @ d2bf39d7) moved onto the AI-344 four-step flow (Layers → Preview → Build → Review, base `24e8baaf`). The builder keeps AI-344's id recording, real skip reasons, plan views and Undo this build; the pane gains a **TYPES** section in step 3 Build, and the Review shows what AI-345 made or could not do.

## In the four-step flow

**Where.** Step **3 Build**, between WALLS and PLANS ON SHEET (the `BuildTypes` spot AI-344 left). Mockup: `2026-11-12-cad-to-bim-step3-build.png`.

**What it shows.** One row per size the next build creates. Each row is two lines (readable in the 280 px column): the kind and full size with the count on the right, then the type dropdown full width. A door or window type shows its own name first (`0915 x 2134mm`) and its family faint after it; the full name is in the tooltip. Render: `2026-11-13-types-labels.png`.

- `Wall 125 mm ×12` → wall type. One row per wall width as the builder rounds it (5 mm), thinnest first. With more than 6 widths only the 6 most used are listed; "Show N more wall widths" lists the rest.
- `Door 910 × 2100 ×5` → door type (width from the swing, height from `DoorHeightMm`).
- `Window 1220 × 1200 ×4` → window type (height from `WindowHeightMm`).
- Openings under 300 mm get no row (the builder skips them as too narrow).

**What is selected at first.** Exactly what the builder would choose by itself (`BuildTypePlan.Row` runs `BuildSizing.Pick` on the same type list, in the same order, with the same tolerance):

- a type already that size → that type;
- none, but one can be copied and resized → **`New: CAD Wall 125 mm`** / **`New: CAD 910 x 2100 mm`**, listed first, then every existing type;
- nothing resizable → the closest type, used at its own size;
- no type of that kind at all → one greyed entry, "No door family (skipped)" (the build skips those with "no door family in the model").

**What the drafter's pick does.** An existing type picked in a row goes to the builder in `BuildRequest.TypeChoices` and is used **as it is** (not copied, not resized) for every wall of that width or opening of that size. "New: …" is left to the builder. Picks are remembered by size while the pane is open, so erase, brush, pick or going back and forth do not reset them. A type deleted from the model after the pane read it falls back to the builder's own choice.

**Where the type list comes from.** `Cad2BimBuilder.ReadTypes(doc)`, read with the levels when the pane is opened (ribbon or chat). Types made by a build are not added to that list; the next build still reuses them (the row says "New: CAD Wall 125 mm" and the builder finds the existing one of that width).

**Save as new .rvt.** The template's types are unknown before the build, so no dropdowns: the section says "New file: each size gets a template type of that size, or a new CAD type." and the builder chooses as before.

**Review (step 4).** WARNINGS now include, when they apply:

- "New wall types made: CAD Wall 125 mm, CAD Wall 230 mm and 3 more"
- "New door/window types made: CAD 910 x 2100 mm, …"
- "N doors or windows keep their family's size (no type could be resized)"
- "Wall joins rolled back: walls stand unjoined, as drawn"
- "N doors could not be turned to match the drawing" (Revit refused flipFacing/flipHand, or the family cannot flip)

SKIPPED uses AI-344's real reasons for openings too: host wall not built (also when the joins step removed the host), narrower than 300 mm, no door/window family, Revit would not place.

**Undo this build** deletes the walls, openings, rooms, levels and views as before but **keeps the new types** (a later build may already use them). Revit's own Ctrl+Z removes them with the build.

## What changed

| Gap on develop | Now |
|---|---|
| Every wall used the first Basic wall type (`DefaultWallType`). | Each wall gets a type of its detected thickness, rounded to 5 mm. The builder uses a Basic type already that width. If none exists, it duplicates the closest one as `CAD Wall <N> mm`, with one structural layer of exactly N mm in the source's core material. |
| Doors and windows used the first symbol in the category (`FirstSymbol`). | A swing means door, anything else means window. Width is the drawn width rounded to 10 mm. For a door that is the swing radius, the leaf. Height comes from the settings (door 2100, window 1200). The builder uses a type already that size (within 5 mm). If none exists, it duplicates the closest resizable type as `CAD <W> x <H> mm` and sets Width/Height. Wide openings start from a double-door family if the model has one. |
| Wall joins were turned off at both ends of every wall. | After all walls of a plan are built, joins are allowed at both ends in a separate transaction, before any opening is placed. |

Two geometry fixes came out of the sample runs. Without them the joins and openings would still look wrong:

- **Corners meet.** A traced centreline stops at the other wall's inside face, half a thickness short. Each wall end now moves along its own line onto the centreline of the wall it runs into: an L, a T, or an overshoot trimmed back. The limit is half that wall's thickness plus 100 mm. Nearly parallel pieces (under 20°) are left alone, so doorway gaps stay open. See `BuildSizing.MeetAtCorners`.
- **Doors sit on their leaf and have a wall to cut.** The symbol reader centres a door on the hinge and clips its width to the host piece. On the samples that gave widths of 0 to 460 mm for 760 to 914 mm doors. The builder now:
  - takes the width from the swing radius;
  - centres the door between the hinge and the end of the arc that lies on the wall (the closed leaf);
  - lengthens a host that stops at the hinge across the doorway, up to the next piece of the same run (`BuildSizing.CoverOpenings`).
- Windows also get `INSTANCE_SILL_HEIGHT_PARAM` set, not only the point's z.

### How joins and openings are ordered

For each plan:

1. Walls transaction: create the walls, with joins disallowed.
2. Joins transaction: `AllowWallJoinAtEnd` on both ends of every wall.
3. Openings and rooms transaction: `Regenerate`, place doors and windows, `Regenerate` again, then turn each door to its drawn swing (`flipFacing` / `flipHand`, see "Door swing").

Why this order:

- Doors cut the final, joined geometry, so the wall is not re-joined under an opening that is already hosted.
- If Revit refuses the joins, only the joins transaction rolls back. The walls stay, unjoined as before, the openings are still placed, and `BuildReport.JoinsSkipped` is set.
- `SilenceJoinFailures` stays on all three transactions. It deletes join warnings and resolves errors with Revit's default resolution, which for joins means unjoin.
- The build already counts walls by asking the model which ones are still present. If a failure resolution ever deletes a wall, the count stays honest, and openings check `host.IsValidObject` before placing.
- `JoinWalls = false` in settings brings back the old unjoined behaviour.

### Door swing

The first Revit 2027 test (demo-plan.dxf, `M_Door-Exterior-Single-Two_Lite`) placed the door swinging into the left room; the DWG arc opens into the right one. Revit places a hosted door by the wall's own sides, not by the drawing. Now, for every door:

1. **Read the swing** (`BuildSizing.SwingOf`, Revit-free). Of the arc's two drawn ends, the one lying along the wall is the closed leaf (strike jamb). The other, about a leaf off the wall, is the open leaf. The open leaf's side of the wall is where the door opens. The arc centre is the hinge jamb. Drawn end points are used, not the mid angle, so clockwise, negative or over-360° angles give the same answer. `DoorCentre` uses the same strike, so a door's position and its turning always use the same leaf.
2. **Leave alone** doors whose arc shows no clear closed and open leaf (the open leaf must stand at least `SwingMinOpenDegrees` = 20° off the wall). No guessing. Each leaf of a double door is its own swing and its own single-door instance, centred on its own leaf, so each is turned to its own arc.
3. **Turn** (`Cad2BimBuilder.TurnDoors`, same transaction as the openings). After all doors are placed: `Regenerate` once, read each door's `FacingOrientation` / `HandOrientation` once and decide both flips from that read, apply `flipFacing()` / `flipHand()`, `Regenerate` once more, then check each door once. A refused flip, a family that cannot flip, or a door that still reads wrong after the check counts in `BuildReport.DoorsNotTurned` ("N doors could not be turned to match the drawing" in Review). It never fails the build.
4. **Log** one line per door to `%LOCALAPPDATA%\Bina\RevitSync\logs\cad-to-bim.log` (the file the pane already logs to), under a timestamped "door swing, N door(s):" header. Each line holds the family and type, the hinge in drawing mm, facing and hand as placed (before any flip), what the drawing wants (opens toward, hinge toward), the flips applied, the result, and the convention in force. Example: `door M_Door-Exterior-Single-Two_Lite: 0915 x 2134mm hinge (5200,2500) | placed facing (-1,0) hand (0,1) | drawing opens (1,0) hinge toward (0,-1) | flipped facing+hand | matches drawing | convention SwingAlongFacing=1 HingeAlongHand=1`.

**Known gap: mirrored blocks.** A door inside a reflected block insert never reaches `SwingOf`. The reader keeps an arc as an arc only under a rotation and uniform scale (`CadRenderSource` `Xform.TryConformal`). A reflected arc becomes chords, so that door is not detected at all. The fix belongs in the reader and is not part of this change.

**Convention (one place: `BuildSizing.SwingAlongFacing`, `HingeAlongHand`, both +1).** The leaf opens toward `FacingOrientation`, and the hinge is at the `HandOrientation` end. Reasoning (in the code comment too):

- `FacingOrientation` is the family's +Y in plan, `HandOrientation` its +X. `flipFacing` reverses only facing, `flipHand` only hand (Micrographics "Revit Door Hand & Facing Orientations"; Revit API guide, FamilyInstances).
- The door template puts the exterior (To Room) above the wall (+Y), and the international (metric) Autodesk library draws its swings on that side (RevitCity thread 21748). Revit sets the room on the swing side as To Room.
- An unflipped Autodesk door is "left hand reverse" (Ideate). Reverse means it swings toward the person on the exterior side. Left hand means it is hinged on their left. Facing −Y from +Y, the left is +X, the hand end.
- The demo-plan test agrees: the wall is drawn bottom to top, so its exterior is −X, and the unflipped door swung −X. That test cannot separate this rule from its exact opposite, and the hinge was not checked there. Hence the Revit check below, and the log, which shows facing and hand as placed.
- The rule is for Autodesk's own content. A type picked in the TYPES section from a non-Autodesk family may follow the other convention. Its doors would then be turned exactly wrong, with no warning. The log names the family per door.

Per door, from `cad2bim-headless <file> --sizes` ("-- door swings the Revit build will match --"):

| Sample | Doors | Read | Left as placed |
|---|---|---|---|
| `demo-plan.dxf` | 1 | Hinge (5200,2500), r 900: opens +X (right room, YOTOQXONA), hinge at the lower (−Y) jamb. Matches the drawing. | 0 |
| `2D_FloorPlan_and_Elevation.dwg` | 12 | 7 open +X (hinge −Y ×4, +Y ×3), 2 open −X (hinge +Y), 1 opens +Y (hinge −X), 1 on the diagonal wall opens up-right. | 1: door at (4100,14548), r 577, arc 58..122° (symmetric about the wall's normal, so no closed leaf). |

### Files

- `Services/CadToBim/BuildSizing.cs` (new, Revit-free). Rounding, names, `Pick` (exact, else closest resizable, else closest), `UniqueName`, `KindOf`, `OpeningSize`, `OpeningWidthMm`, `DoorCentre`, `MeetAtCorners`, `CoverOpenings`.
- `Services/CadToBim/Cad2BimBuilder.cs`. `WallTypeCache`, `OpeningTypeCache`, `JoinWalls`, sized `CreateOpenings`, and walls drawn from the met and covered centrelines.
- `BuildTypes.cs`: `DoorHeightMm`, `WindowHeightMm`, `JoinWalls`, `TypeChoices` (+ `TypeSlot`, `TypeChoice.Find`).
- `BuildReport.cs`: `NewWallTypes`, `NewOpeningTypes` (+ their names), `UnsizedOpenings`, `JoinsSkipped`.
- `Services/CadToBim/BuildTypePlan.cs` (new, Revit-free): TYPES rows and defaults, picks → `TypeChoices`, the warning line.
- `Cad2BimBuilder.ReadTypes`: the model's types for the pane, from the same lists the builder picks from. The type caches honour `TypeChoices` first.
- `CadToBimSettings.cs`: `DoorHeightMm = 2100`, `WindowHeightMm = 1200`, `JoinWalls = true`. No UI fields yet; set them in `config.json` under `cadToBim` if needed.
- `UI/CadToBim/CadToBimViewModel.cs`: TYPES rows (`TypeRows`, `ShowAllTypes`, `TypesNote`), the request fields, Review warnings. `CadToBimPanel.xaml(.cs)`: the TYPES section; `RefreshLevels` also reads the types.
- `Tests/Cad2BimBuildTypePlanTests.cs` (11 tests) and 5 view-model tests in `Tests/CadToBimFlowTests.cs`.
- `Cad2Bim/Cad2Bim.Headless`: a `--sizes` flag that prints what a build will choose (links `BuildSizing.cs`).
- `Tests/Cad2BimBuildSizingTests.cs`: 34 tests.
- Door swing: `BuildSizing.cs` (`DoorSwing`, `SwingOf`, `DoorFlips`, `DoorTurnLine`, the convention constants; `DoorCentre` uses `SwingOf`'s strike), `Cad2BimBuilder.TurnDoors` and its log, `BuildReport.DoorsNotTurned`, the Review warning, the headless per-door listing, and `Tests/Cad2BimDoorSwingTests.cs` (35 tests: demo-plan's door, mirrored and unnormalised arcs, horizontal and diagonal walls in both directions, partly open swings, hinge on a wall face, no-guess cases, double-door leaves on one wall and on two collinear pieces, centre agreeing with the swing, flips, log line, warning).

## What each sample will get

From `cad2bim-headless <file> --sizes`. The headless pipeline matches the pane's (same wall hints, same symbol opening reader), but the pane's brush or erase edits can change the counts. "Type" means `CAD Wall N mm` unless the model already has a Basic type of exactly N mm (for example `Generic - 200mm`).

| Sample | Walls | Main thicknesses → wall types | Corners met | Doors | Windows | Hosts lengthened |
|---|---|---|---|---|---|---|
| `2D_FloorPlan_and_Elevation.dwg` (**best test**) | 160 | 50 ×46, 150 ×33, 230 ×27, 75 ×7, then 29 more widths with 1–4 walls each (33 types in total) | 78 of 160, median 51 mm, max 207 mm | 12: 910×2100 ×5, 760×2100 ×3, 610×2100 ×2, 580×2100 ×1, 660×2100 ×1 | 15: 610×1200 ×5, 1220×1200 ×4, 1520×1200 ×4, 760×1200 ×1, 1830×1200 ×1 | 9 walls. 8 of 27 openings ran past their host and 1 is still past (stopped at the next piece). Doors moved from hinge to leaf centre: median 228 mm, max 572 mm. |
| `demo-plan.dxf` (small, quick) | 11 | 180 ×8, 50 ×2, 150 ×1 | 7 of 11, 90 mm each | 1: 900×2100. Before this change it was 450 wide, centred on the hinge. | 0 | 1 wall, across the doorway |
| `Res/Wall Base.dwg` | 510 | 50 ×405 (hatch-outline walls), 115 ×22, 120 ×21, 165 ×9, 205 ×9, then 22 more widths (27 types in total) | 123 of 510, median 60 mm, max 172 mm | 0 | 13: 1220×1200 ×6, 610×1200 ×3, 2440×1200 ×2, 1520×1200 ×2 | 0 |
| `residential_floor_plan.dwg` | 18 | 375 ×7, 350 ×6, 395 ×5 | 0 | 0 (no arcs) | 0 | 0 |
| `Floor Plan Sample.dwg` | 7 | 55 ×4, 305 ×3 | 0 | 0 | 0 | 0 |
| `2011-DM-0-A04-6A1.dwg` | 139 | 55 ×49, 75 ×31, then 26 more widths (28 types in total) | 31 of 139, median 38 mm, max 229 mm | 0 (782 arcs, none read as a swing on a wall) | 0 | 0 |
| `CAD.V01-CAFM-Plan-DE.dwg` | 303 | 80 ×75, 100 ×70, 110 ×64, 95 ×18, then 33 more widths (37 types in total) | 24 of 303, median 99 mm, max 250 mm | 0 | 0 | 0 |

Recommended for the "Done when" check: `2D_FloorPlan_and_Elevation.dwg` (doors, windows and joins), `demo-plan.dxf` (one door across a doorway, easy to see), and `Res/Wall Base.dwg` (windows plus many thicknesses).

## Revit test checklist

Use Revit 2027 (net10 build) or 2025/2026 (net8). Load the add-in from this branch the usual way. Open a project (template with a few basic wall types and the default door/window families). For each of the three samples:

1. **Before.** Build from `develop` with "New file" first, or use an earlier build's RVT. Screenshot the same views as below.
2. Ribbon → CAD to BIM → pick the DWG. Step **1 Layers** → *Next* → **2 Preview** → *Next* → **3 Build**. Storeys "Keep drawing position", target "Add to this project".
2a. **TYPES section.** Between WALLS and PLANS ON SHEET. Expect: a size count on the right ("12 sizes"); rows like `Wall 230 mm ×27`, `Door 910 × 2100 ×5`, `Window 1220 × 1200 ×4` (size on top, dropdown below); a template type selected where the model has that exact width (e.g. `Generic - 200mm` for 200 mm) and `New: CAD Wall 125 mm` / `New: CAD 910 x 2100 mm` otherwise; "Show N more wall widths" on 2D_FloorPlan (33 widths). The dropdowns are readable in the 280 px column (door and window types show the type name first, family faint after it; full text in the tooltip). Screenshot it.
2b. **Pick a type.** Set one wall width to an existing type of a different width (e.g. `125 mm` → `Generic - 200mm`) and one door size to an existing door type. Go back to Preview and forward again: the picks are still there.
2c. **New file.** Switch target to "Save as new .rvt": the dropdowns go and the note reads "New file: each size gets a template type of that size, or a new CAD type." Switch back.
2d. Press **Build**.
3. **Wall types.** Select a few walls of visibly different thickness. In Properties the type should read `CAD Wall <N> mm`, or a template type of that exact width, or the type you picked in 2b for that width (at that type's own thickness). Measure one with the Measure tool against the linked DWG. Screenshot Properties together with the plan.
4. **Joins.** Zoom into 3–4 L corners and T junctions in plan at Fine detail. The walls should be cleaned up, with no notch at the outside corner and no overlapping hatch. Screenshot one L and one T, before and after.
5. **Doors.** Select a door. Its type should be `CAD <W> x 2100 mm` with W matching the DWG swing radius (910 or 760 on 2D_FloorPlan). The leaf should sit between the two jambs, not straddling the hinge. A door in a doorway gap should cut the wall, which now runs across the gap. Screenshot plan plus Properties.
5a. **Door swing.** On demo-plan the door opens into the room shown by the DWG arc (the right room, YOTOQXONA), with its hinge on the arc-centre jamb (the lower one). On 2D_FloorPlan, compare 3–4 doors against the linked DWG the same way. Review must not say "doors could not be turned". If every door opens to the wrong side, set `BuildSizing.SwingAlongFacing` to −1. If every hinge is at the wrong jamb, set `HingeAlongHand` to −1. If only some are wrong, note the family and the door. Then open `%LOCALAPPDATA%\Bina\RevitSync\logs\cad-to-bim.log` and copy the last "door swing" block into AI-345: it shows facing and hand as placed and the flips applied, which settles both constants from this one build. Screenshot plan with the DWG underlay.
6. **Windows.** Select a window. Its type should be `CAD <W> x 1200 mm`, and Sill Height should be 900. Check in a 3D view or an elevation that it cuts the wall. Screenshot 3D.
7. **Joins with openings.** Find a door or window near a corner (2D_FloorPlan has several). The opening should still cut the wall after the join. A "can't cut instance out of wall" result would show as a missing door, and the pane's skipped count would rise.
8. **Review (step 4).** CREATED counts compare with the table above. WARNINGS list "New wall types made: CAD Wall …" and "New door/window types made: CAD … mm" (only the sizes not picked in 2b); "… keep their family's size" if a family could not be resized; "Wall joins rolled back" should not appear. SKIPPED shows real reasons per group (e.g. "host wall not built", "narrower than 300 mm"); click a group to ring the items in the viewport. *Select in model* → Doors selects the placed doors.
9. **Undo.** a) *Undo this build* in Review: walls, openings, rooms, new levels and plans go in one Revit undo step; the new `CAD …` types stay in the type list (by design). b) Build again, then Ctrl+Z once in Revit: the whole build goes, including the new types.
10. **Add to project twice.** Build into an open project, *Build more*, brush a few more walls, and build again. The second build should reuse the existing `CAD Wall` and `CAD … mm` types (no `(2)` duplicates), and new walls should join the old ones.

Where to put screenshots: attach them to AI-345 as before/after pairs per sample (walls+types, one L, one T, one door, one window).

## Risks

- **Many wall types.** Traced thicknesses are noisy, so 2D_FloorPlan gets 33 widths and CAFM 37. That matches the drawing but clutters the type list. A coarser step or snapping to a standard list (100/115/150/200/230/300) is a one-line change in `BuildSizing.WallStepMm` or `WallWidthMm` if the team prefers.
- **50 mm walls** on Wall Base (405) and 2011-DM come from hatch outlines and are probably not real walls. They will now be 50 mm thick and will join. This is a detection issue, not a build issue.
- **Corner meeting is geometric.** An end within reach of an unrelated angled wall will move to it, at most half that wall's thickness plus 100 mm. The samples show moves of at most 172–250 mm.
- ~~Door hand and facing are not set from the swing.~~ Closed: doors are turned to the drawn swing (see "Door swing"). Remaining risk: the convention is checked against sources and one Revit test, not yet against the hinge. A family drawn the other way round (some regional libraries draw swings on the interior) would be turned exactly wrong, without a warning. That family needs the constants flipped.
- **Revit API names** (`DOOR_WIDTH`, `WINDOW_WIDTH`, `FAMILY_WIDTH_PARAM`, `INSTANCE_SILL_HEIGHT_PARAM`, `CreateSingleLayerCompoundStructure`) compile on all three targets. Whether a given template family takes the values is only provable in Revit.
- Walls built by the old code on an earlier Confirm keep their joins disallowed, so a corner between an old wall and a new one may stay unjoined.
- **TYPES list is read once**, when the pane is opened (with the levels). Types added or deleted by hand afterwards are not seen until the pane is reopened; a deleted pick falls back to the builder's own choice.
- **TYPES XAML** compiles on all three targets but has not been seen inside Revit yet (layout at 280 px, dark theme, dropdown height).

## Open questions

1. **AI-342 repro.** None yet. The tester only wrote "CAD to BIM". Its known gaps are the three fixed here. This needs a file and steps from the tester before AI-342 can be closed against this change.
2. **Engine-side door position.** The pane overlay still draws doors centred on the hinge with the clipped width. The builder corrects this, but the right fix is `CadClassifier.Across` in `Openings.cs`, which would change `Cad2BimOpeningsTests`. Should it move to the engine (a separate task)?
3. **Double doors.** Two swings meeting in the middle are read as two single doors, or merged into one leaf-width door. Is detecting them as one double door in scope? (Until then each leaf is turned to its own arc.)
4. **Wall type step.** Keep 5 mm (exact to the drawing) or snap to a standard list?
5. ~~Door hand and facing from the swing.~~ Done (see "Door swing"). Still open: should the convention follow the family, for example a per-family setting, if the team's own door families swing the other way?
6. ~~Showing the new report fields in the pane.~~ Done: Review WARNINGS (see "In the four-step flow").
7. **A picked type is used as it is.** Picking `Generic - 200mm` for 125 mm walls builds them 200 mm thick (centred on the traced centreline). Should a pick instead mean "copy this type at the drawn width"?
8. **Undo this build keeps new types.** Fine, or should it delete those no other element uses?
