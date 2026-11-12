# AI-345: CAD to BIM walls, doors and windows

Branch base: `origin/develop` @ d2bf39d7. This change is build side only. The pane is untouched apart from passing three new settings into the request.

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
3. Openings and rooms transaction: `Regenerate`, then place doors and windows.

Why this order:

- Doors cut the final, joined geometry, so the wall is not re-joined under an opening that is already hosted.
- If Revit refuses the joins, only the joins transaction rolls back. The walls stay, unjoined as before, the openings are still placed, and `BuildReport.JoinsSkipped` is set.
- `SilenceJoinFailures` stays on all three transactions. It deletes join warnings and resolves errors with Revit's default resolution, which for joins means unjoin.
- The build already counts walls by asking the model which ones are still present. If a failure resolution ever deletes a wall, the count stays honest, and openings check `host.IsValidObject` before placing.
- `JoinWalls = false` in settings brings back the old unjoined behaviour.

### Files

- `Services/CadToBim/BuildSizing.cs` (new, Revit-free). Rounding, names, `Pick` (exact, else closest resizable, else closest), `UniqueName`, `KindOf`, `OpeningSize`, `OpeningWidthMm`, `DoorCentre`, `MeetAtCorners`, `CoverOpenings`.
- `Services/CadToBim/Cad2BimBuilder.cs`. `WallTypeCache`, `OpeningTypeCache`, `JoinWalls`, sized `CreateOpenings`, and walls drawn from the met and covered centrelines.
- `BuildTypes.cs`: `DoorHeightMm`, `WindowHeightMm`, `JoinWalls`.
- `BuildReport.cs`: `NewWallTypes`, `NewOpeningTypes`, `UnsizedOpenings`, `JoinsSkipped`.
- `CadToBimSettings.cs`: `DoorHeightMm = 2100`, `WindowHeightMm = 1200`, `JoinWalls = true`. There are no UI fields for these, so they don't collide with AI-344. Set them in `config.json` under `cadToBim` if needed.
- `UI/CadToBim/CadToBimViewModel.cs`: three lines copying those settings into the request.
- `Cad2Bim/Cad2Bim.Headless`: a `--sizes` flag that prints what a build will choose (links `BuildSizing.cs`).
- `Tests/Cad2BimBuildSizingTests.cs`: 34 tests.

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

Use Revit 2027 (net10 build) or 2025/2026 (net8). Load the add-in from this branch the usual way. For each of the three samples:

1. **Before.** Build from `develop` with "New file" first, or use an earlier build's RVT. Screenshot the same views as below.
2. Open the DWG in the CAD to BIM pane, Detect, then Confirm with target "New file" and storeys "Keep position".
3. **Wall types.** Select a few walls of visibly different thickness. In Properties the type should read `CAD Wall <N> mm`, or a template type of that exact width. Measure one with the Measure tool against the linked DWG. Screenshot Properties together with the plan.
4. **Joins.** Zoom into 3–4 L corners and T junctions in plan at Fine detail. The walls should be cleaned up, with no notch at the outside corner and no overlapping hatch. Screenshot one L and one T, before and after.
5. **Doors.** Select a door. Its type should be `CAD <W> x 2100 mm` with W matching the DWG swing radius (910 or 760 on 2D_FloorPlan). The leaf should sit between the two jambs, not straddling the hinge. A door in a doorway gap should cut the wall, which now runs across the gap. Screenshot plan plus Properties.
6. **Windows.** Select a window. Its type should be `CAD <W> x 1200 mm`, and Sill Height should be 900. Check in a 3D view or an elevation that it cuts the wall. Screenshot 3D.
7. **Joins with openings.** Find a door or window near a corner (2D_FloorPlan has several). The opening should still cut the wall after the join. A "can't cut instance out of wall" result would show as a missing door, and the pane's skipped count would rise.
8. **Pane status line.** Note walls, doors, windows and openings skipped. Compare with the table above. The new report fields (`NewWallTypes`, `NewOpeningTypes`, `UnsizedOpenings`, `JoinsSkipped`) are not shown in the pane yet (AI-344 owns the pane). Read them in the debugger or Debug output if needed.
9. **Undo.** Ctrl+Z once should remove the whole build, including the new types.
10. **Add to project twice.** Confirm into an open project, brush a few more walls, and Confirm again. The second build should reuse the existing `CAD Wall` and `CAD … mm` types (no `(2)` duplicates), and new walls should join the old ones.

Where to put screenshots: attach them to AI-345 as before/after pairs per sample (walls+types, one L, one T, one door, one window).

## Risks

- **Many wall types.** Traced thicknesses are noisy, so 2D_FloorPlan gets 33 widths and CAFM 37. That matches the drawing but clutters the type list. A coarser step or snapping to a standard list (100/115/150/200/230/300) is a one-line change in `BuildSizing.WallStepMm` or `WallWidthMm` if the team prefers.
- **50 mm walls** on Wall Base (405) and 2011-DM come from hatch outlines and are probably not real walls. They will now be 50 mm thick and will join. This is a detection issue, not a build issue.
- **Corner meeting is geometric.** An end within reach of an unrelated angled wall will move to it, at most half that wall's thickness plus 100 mm. The samples show moves of at most 172–250 mm.
- **Door hand and facing** are not set from the swing. A door may swing the wrong way or open from the wrong side.
- **Revit API names** (`DOOR_WIDTH`, `WINDOW_WIDTH`, `FAMILY_WIDTH_PARAM`, `INSTANCE_SILL_HEIGHT_PARAM`, `CreateSingleLayerCompoundStructure`) compile on all three targets. Whether a given template family takes the values is only provable in Revit.
- Walls built by the old code on an earlier Confirm keep their joins disallowed, so a corner between an old wall and a new one may stay unjoined.

## Open questions

1. **AI-342 repro.** None yet. The tester only wrote "CAD to BIM". Its known gaps are the three fixed here. This needs a file and steps from the tester before AI-342 can be closed against this change.
2. **Engine-side door position.** The pane overlay still draws doors centred on the hinge with the clipped width. The builder corrects this, but the right fix is `CadClassifier.Across` in `Openings.cs`, which would change `Cad2BimOpeningsTests`. Should it move to the engine (a separate task)?
3. **Double doors.** Two swings meeting in the middle are read as two single doors, or merged into one leaf-width door. Is detecting them as one double door in scope?
4. **Wall type step.** Keep 5 mm (exact to the drawing) or snap to a standard list?
5. **Door hand and facing** from the swing. Wanted for this task or a follow-up?
6. **Showing the new report fields** (new types, unsized openings, joins skipped) in the pane. Leave to AI-344?
