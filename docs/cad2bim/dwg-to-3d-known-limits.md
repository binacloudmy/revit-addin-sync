# DWG to 3D: known limits

What `read_dwg_plan` (AI-607) and the CAD to BIM pane get wrong today on real drawings. Measured on
`01_FLOOR PLAN_ASRAMA ASWARA.dwg` (ASWARA) and `elec.dwg`, 2026-11-26. Tracked in
[AI-608](https://linear.app/bina-ai/issue/AI-608) (build_from_dwg) and
[AI-610](https://linear.app/bina-ai/issue/AI-610) (these tests). Pinned by `Tests/DwgRealSheetTests.cs`
(set `AI610_ASWARA_DWG` and `AI610_ELEC_DWG`); the `Known_limit_*` tests there are skipped and assert the
behaviour we want.

## Multi-plan sheets

- Plans stacked on one sheet only come apart with `ClusterPlans(..., resplit: true)`. Only
  `DwgPlanReader` (read_dwg_plan) passes it. The pane (`CadToBimViewModel`), `Cad2BimBuilder` and
  `Cad2Bim.Headless` still cluster in one pass, so they can merge stacked floors that read_dwg_plan lists apart.
- ASWARA lists 8 plans, 15 details skipped (untitled, under 20 walls or under 8 m a side) and 46 fragments.
- Asking for a plan that is not there says "plans 1-69" on ASWARA: the count includes the details and
  fragments, not just the 8 listed plans.
- elec: plan 1 has no title while plan 2 is "ARAS BAWAH (2)". Numbering keys on the level when there
  is no title, so the first of the pair gets none.

## Stacked floors still merged

- elec plan 1 is 67.5 x 125.2 m against 14-17 m floors elsewhere on the sheet: it most likely still holds
  several stacked floors.
- elec plan 3 (PELAN LANTAI ARAS BAWAH) is 110.3 x 43.1 m. Floors stacked less than 5 m apart
  (`CadClassifier.PlanGapMm`) are not split, even with resplit.

## Walls

- **Loose wall ends.** Faces seldom meet cleanly. ASWARA plans 1-3 have 223, 205 and 243 loose ends; elec
  plan 3 has 420.
- **No rooms.** Because the walls do not close, ASWARA plans 1-3 find 0 rooms, elec plan 3 finds 1, and
  `outline_closed` is false on all of them.
- **Thin wall groups and false walls.** Every layer whose name says "wall" is read as walls, including
  `n_low wall`, `color wall` and `n_hatching retaining wall` on ASWARA. Pairs 50-80 mm apart become walls:
  on ASWARA plan 1, 221 of 351 walls are 50, 70 or 80 mm thick. These are likely finishes, hatches or
  furniture edges.

## Big sheets and timing

- elec.dwg is a large sheet: Cad2Bim.Headless takes ~270 s on it (read_dwg_plan reads it in ~6 s); sheet_m counts stray hatch fragments (796 m on ASWARA).
