# IFC → Native Revit Conversion (v1) — Design

**Date:** 2026-07-29
**Repos:** `revit-addin-sync` (C# add-in — the engine) + `bina-ai` (copilot trigger only)
**Source:** JKR AI-functions list, item 15 (*"IFC to Revit Conversion, convert to revit file, convert [to] native revit"*) — AI Discussion with Master Khairi.
**ClickUp:** 86eyeh4y0 (space: Revit AI Copilot)

## Goal

Convert IFC data that has been imported into Revit from **frozen `DirectShape` solids** into **true native, editable Revit elements** (real category + family/type + parametric geometry). v1 covers the **structural shell**: walls, floors/slabs, columns, beams.

## Problem

JKR/gov deliverables arrive as IFC (open, portable, archival). Revit's built-in *Open/Link IFC* produces `DirectShape` — solids tagged with a category but **not** native: not parametric, can't join, can't host, can't edit like a drawn wall. The team works *in* Revit and needs to keep designing, so they need the imported IFC turned into genuine native elements.

## Decisions (locked during brainstorming)

1. **Runs inside Revit, in the add-in.** Not headless/Design-Automation/ODA. Reuses BINA's existing live add-in + native-creation `Mutators`. The Revit API is the only thing that gives true native parametric elements, and a person is already in Revit when they open a received IFC.
2. **v1 scope = structural shell** — `IfcWall`, `IfcSlab`, `IfcColumn`, `IfcBeam`. No hosting (doors/windows) yet.
3. **Read Revit's IFC import, do NOT parse raw `.ifc`.** The user does Revit's normal Link/Open IFC first; the resulting `DirectShape`s still carry the IFC entity type (`IfcExportAs`) + property sets (Psets). We read those via the Revit API. Revit already did the hard STEP parsing — we never ship an IFC parser.
4. **Type resolution = match-or-create.** For each element, match an existing project type by thickness (+name); if none fits, auto-create a type from the IFC Psets (thickness, material). No external dependency. JKR family-library matching is a **v2 seam** (ties to source items #8/#9).
5. **Un-convertible geometry = keep original + report.** Curved/warped/non-planar geometry that can't become a clean native element is **left as its original `DirectShape`** (never deleted/approximated), with the reason recorded. Nothing silently disappears — required for a JKR deliverable.
6. **Deterministic engine, copilot trigger, preview→confirm.** The LLM-per-element path is ruled out (`revit_turn.py` caps the tool loop at `_TOOL_MAX_ROUNDS = 3`; a building has hundreds of elements). The conversion is a deterministic C# pipeline. The copilot is the **trigger + scope context** (natural language + `selection`/`active_level`), emitting **one** `convert_ifc_to_native` external-execution tool call. Flow: **trigger → deterministic dry-run preview → user confirms → one atomic build → report.**

## What already exists (reuse, do NOT rebuild)

Grounded via graphify on `revit-addin-sync/graphify-out/graph.json`:

- **`BinaVibe/Mcp/Tools/BatchExecutor.cs`** — `Run()` (L19): executes a multi-element plan in **one `TransactionGroup`** (single undo/regen), resolving `$<index>.<field>` references so a later step can reference an element created earlier. This is the atomic-build machinery the converter emits into.
- **`BinaVibe/Mcp/Tools/ToolRegistry.cs`** — `Invoke()` (L24) dispatch, `RemapArgs()` (L164) `$index` resolution, and generic seams `CreateLineElement` (L201), `CreateSurfaceElement` (L222), `CreatePointElement` (L182) — a wall is a line element, a floor a surface element, a column a point element.
- **`BinaVibe/Mcp/Tools/Mutators.cs`** — `CreateWall` (L494), `CreateFloor` (L1958), `BuildCurveLoop` (L2162), `CreateLevel` (L966), `SetParameter`/`SetParameterBulk` (L31/L65), `FamilyTypesOf`/`LoadFamily`/`SwapElementType`.
- **`BinaVibe/Mcp/Tools/MutatorsStructure.cs`** — `CreateBeam` (L94), `CreateBeamSystem` (L19); structural family placement via `NewFamilyInstance` + `StructuralType`.
- **Copilot side (`bina-ai`)** — `app/services/revit_turn.py` tool loop, `app/agents/revit/revit_ai.py` agent + `serialize_pending`/`apply_results` external-execution wiring; `RevitModelContext` (`app/routers/revit_turn/schema.py`) already carries `selection`, `active_level_name`, `units` for scope.

**Genuinely new surface:** reading imported-IFC elements (there is *zero* `DirectShape`/`IfcExportAs`/Pset reading today — `QueryGeometry` reads native elements only) and the IFC→native mapping.

## Architecture

```
Revit (user has linked/opened the IFC → DirectShapes with IfcExportAs + Psets)
   │  "convert this IFC to native"      (natural language)
   ▼
bina-ai copilot  ── emits ONE external-execution tool call ──▶  convert_ifc_to_native { scope, mode }
   │                                                                    (scope: selection | level | whole ;
   │                                                                     mode:  preview | build)
   ▼
revit-addin-sync — deterministic IfcConvert pipeline
   IfcReader → IfcMapper → IfcConverter(mode)
      preview:  read-only → ConversionReport (counts + kept-as-is)   ──▶ pane shows, user confirms
      build:    emit batch plan → BatchExecutor.Run() (1 TransactionGroup) → ConversionReport
```

The agent emits **one** tool call; the hundreds of element creates happen deterministically **inside** that call. This is what keeps it under the 3-round cap.

## Components (new — `revit-addin-sync/BinaVibe/Mcp/Tools/IfcConvert/`)

### 1. `IfcReader`
Enumerate imported-IFC elements in scope and produce a neutral `IfcElement` record per source element.
- **Select:** `DirectShape`s (and IFC-imported family instances) carrying an IFC entity tag. Entity type from the `IFC_EXPORT_ELEMENT*` / `IfcExportAs` parameter or the element's IFC schema data; Psets from the element's parameter groups (`Pset_WallCommon` thickness, material, etc.).
- **Geometry:** pull the `Solid` (via `GeometryElement`); derive the native inputs — walls: base curve (location line) + height from the solid's vertical extent + thickness from face offset; slabs: bottom-face boundary `CurveLoop`; columns/beams: insertion point / axis line + level.
- **Output:** `IfcElement { sourceId, entity (Wall|Slab|Column|Beam|Other), locationCurve|point|loop, height, thickness, material, level, psets, convertible: bool, reason? }`. Sets `convertible=false` + `reason` when geometry can't yield clean native inputs (curved/non-planar/degenerate).

### 2. `IfcMapper`
`IfcElement` → `NativeSpec` (a `ToolRegistry` call descriptor), using **match-or-create**:
- `IfcWall` → `create_wall` (`CreateLineElement`/`CreateWall`): location line, level, height; wall **type** = match an existing `WallType` by thickness (±tol) + name, else create a new `WallType` from thickness/material.
- `IfcSlab` → `create_floor` (`CreateSurfaceElement`/`CreateFloor` + `BuildCurveLoop`): boundary loop, level, floor type match-or-create by thickness.
- `IfcColumn` → structural column `FamilyInstance` at point + level; type match-or-create.
- `IfcBeam` → `CreateBeam` along axis; type match-or-create.
- Missing level → resolve against project levels by elevation, else `CreateLevel`.
- **v2 seam:** a `ITypeResolver` interface with the v1 `MatchOrCreateTypeResolver`; a future `JkrFamilyLibraryResolver` (calls bina-ai `family_library`) drops in without touching the reader/converter.

### 3. `IfcConverter` (orchestrator)
- `Preview(scope)` → run `IfcReader` + `IfcMapper` **read-only**, return `ConversionReport` (no writes).
- `Build(scope)` → same, then emit the `NativeSpec` list as a **BatchExecutor plan** → `BatchExecutor.Run()` (one `TransactionGroup`). Convertible elements become native; un-convertible ones are left as their `DirectShape` untouched. On any build exception the whole `TransactionGroup` rolls back (single undo).
- **Optional cleanup (config, default off):** after a successful build, the original `DirectShape` of a *converted* element may be deleted so it isn't duplicated with its native twin. Default keeps both and tags the source, so v1 never deletes anything without an explicit flag.

### 4. `ConversionReport`
Structured result surfaced in the copilot pane and returned by the tool:
`{ converted: {wall, slab, column, beam counts}, keptAsIs: [{sourceId, entity, reason}], createdTypes: […], warnings: […] }`. `Preview` and `Build` return the **same shape** — the preview is a truthful dry run of the build.

### 5. Copilot trigger (`bina-ai`)
- Register a `convert_ifc_to_native(scope, mode)` external-execution tool on the Revit agent. Intent recognition maps "convert this IFC / make it native / turn the IFC into real Revit" → this tool. `scope` derives from `selection` (a picked IFC link/elements) or `active_level` or whole-model; `mode` = `preview` first, then `build` on confirm.
- The agent **never** enumerates elements — it emits the single tool call and renders the returned `ConversionReport`.

## Data flow (happy path)

1. User links/opens IFC in Revit → DirectShapes.
2. User: *"convert this IFC to native."*
3. Agent → `convert_ifc_to_native{ scope: whole, mode: preview }`.
4. Add-in: `IfcConverter.Preview` → report *"342 walls, 88 slabs, 56 columns, 12 beams → 443 native, 55 kept-as-is (curved/complex, listed)."*
5. Pane shows preview; user confirms.
6. Agent → `convert_ifc_to_native{ scope: whole, mode: build }`.
7. Add-in: `IfcConverter.Build` → one `TransactionGroup` → native elements created; report returned.

## Error handling

- **Un-convertible geometry** (curved/warped/non-planar/degenerate solid) → keep original `DirectShape`, `convertible=false` + reason, count in `keptAsIs`. Never delete, never approximate.
- **No IFC metadata** on a candidate element (bad export) → skip + report `reason: "no IFC entity data"`.
- **Type/level unresolvable** → match-or-create; if genuinely impossible → keep original + report.
- **Build failure** → `TransactionGroup` rollback; whole conversion is a single atomic undo. Report the failure; model unchanged.
- **Preview/Build divergence is a bug** — they run the same reader/mapper; tested (below).

## Testing

- **Unit (C#, `IfcMapper`):** mock `IfcElement` (solid-derived inputs + Psets) → asserts correct `NativeSpec` — wall location line + height + thickness, floor boundary loop, match-vs-create type decision. Geometry edge cases: curved wall / non-planar slab → `convertible=false` with reason (kept-as-is).
- **Preview/Build parity:** the counts in a `Preview` report equal the actual results of `Build` on the same input (the trust guarantee).
- **Integration (staged, needs Revit — run like other add-in tests):** a small sample IFC (a few walls + a slab + a column + a beam) opened in Revit → run pipeline → assert native `Wall`/`Floor`/column `FamilyInstance`/beam created with correct type + params, and that intentionally-curved elements remain `DirectShape` per policy.
- **bina-ai:** intent recognition emits **one** `convert_ifc_to_native` tool call (never per-element); `preview` precedes `build`.

## Out of scope (v1)

- Doors / windows (hosted `FamilyInstance`s — needs walls first) → v2.
- Roofs, ceilings, railings, stairs, MEP → later.
- **JKR family-library type matching** — v2, via the `ITypeResolver` seam (source items #8/#9).
- Raw `.ifc` file parsing — we deliberately reuse Revit's importer.
- Headless / batch / Design Automation — explicitly rejected for v1.
