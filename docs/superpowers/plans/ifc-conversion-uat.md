# IFC → Native Conversion — Staged Integration UAT

Task 7, Step 3 of the IFC → Native Revit Conversion feature
(`docs/superpowers/plans/2026-07-29-ifc-to-native-revit-conversion.md`).

This is a **manual, Revit-coupled** checklist — it cannot be automated in
this environment (no Revit instance here) and must be run on
Windows + Revit against a real document. Record actual results inline as
each step is executed; do not mark a box done from inspection of the code
alone.

Tool under test: `convert_ifc_to_native` (`BinaVibe/Mcp/Tools/ToolRegistry.cs`
→ `IfcConvert.IfcConverter.Preview` / `.Build`), args
`{ scope: "whole"|"level"|"selection", mode: "preview"|"build" }`.

## Prerequisites

- Windows machine with Revit + the BinaVibe add-in loaded (this build).
- A small sample IFC file: a few walls, one slab, one column, one beam —
  small enough to eyeball every converted element by hand.
- Copilot pane available to issue tool calls and read the response JSON.

## Checklist

- [ ] **1. Link/Open the sample IFC.**
  In Revit, Link or Open the small sample IFC (walls + slab + column + beam).
  Confirm every IFC element lands in the model as a `DirectShape` (select one,
  check the Properties palette — category should read as the mapped Revit
  category, but the underlying element is still a DirectShape, not a native
  family instance).
  - Result: _______________________________________________

- [ ] **2. Run `convert_ifc_to_native{scope:"whole", mode:"preview"}`** via
  the copilot pane.
  **Expect:**
  - `ok: true`, `mode: "preview"`.
  - `report.converted` — per-type counts (`Wall`, `Slab`, `Column`, `Beam`)
    matching what's in the sample IFC.
  - `report.keptAsIs` — lists any curved/complex elements that cannot map to
    a native type (empty list is fine if the sample has none).
  - `report.createdTypes` — any new Revit types the mapper had to create
    because no existing type matched within tolerance.
  - No Transaction opened — the model is unchanged after this call (spot
    check: elements are still DirectShapes).
  - Result: _______________________________________________

- [ ] **3. Run `convert_ifc_to_native{scope:"whole", mode:"build"}`.**
  **Expect:**
  - `ok: true`, `mode: "build"`.
  - Native `Wall` elements created with the correct wall type + thickness.
  - Native `Floor` created for the slab, correct type/thickness.
  - Native structural `Column` and `Beam` created, correct type.
  - Curved/complex elements (anything in `keptAsIs` from step 2) remain
    DirectShapes — not force-converted.
  - The whole batch is **one undo**: press `Ctrl+Z` once — all newly created
    native elements disappear and the model returns to the pre-build state
    (still-DirectShape IFC elements included, since Build runs through
    `BatchExecutor`'s single `TransactionGroup`).
  - Result: _______________________________________________

- [ ] **4. Preview/Build parity.**
  Compare `report.converted` counts from step 2 (preview) against the
  `report.converted` counts returned by step 3 (build) — they must be
  identical (same read+map path feeds both, per `IfcConverter.Plan`).
  Also compare `keptAsIs` and `createdTypes` between the two calls for the
  same reason.
  - Preview counts: _______________________________________________
  - Build counts: _______________________________________________
  - Match? [ ] Yes  [ ] No (explain): __________________________

## Notes

- This doc is the record of the manual pass — fill in actual values, don't
  just check boxes.
- If any step fails, do not proceed to commit further work on the
  conversion tool until the discrepancy is understood (see
  `superpowers:systematic-debugging`).
