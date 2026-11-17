# CAD electrical layout to BIM — design

**Date:** 2026-11-17
**Repos:** `revit-addin-sync` (tools, engine), `bina-ai` (tool schemas, the interpretation prompt)
**Status:** draft for review. Written from reading `main` (worktree `thrashraf/cusk`); `main` lags `develop`, so file names must be re-checked against `develop` before any build. Nothing here has been built or run in Revit.

## 1. Goal

A finished Revit model exists. The drafter has an electrical layout DWG for one of its floors. Today they re-model every socket and conduit by hand. With this feature they attach the DWG in Copilot and get the sockets placed on the right walls at the right height, and the drawn conduit runs created, after reviewing one plan.

The AI does one job: say what each block and layer **means**. Code does everything that has a coordinate in it.

## 2. Scope

In scope:

- Point devices drawn as block inserts: sockets first, then switches, isolators, distribution boards, light fittings.
- Conduit and trunking drawn as lines or polylines on their own layer.
- One DWG or DXF, attached in the Copilot pane, holding one or more floor plans.
- Placement on top of an existing model with walls, rooms and levels.

Out of scope for this design:

- Generating conduit routes that are not drawn (schematic wiring arcs give connectivity only). See §12.
- Electrical circuits, panel schedules, load calculation.
- Symbols that are exploded linework with no block. Planned as a later vision fallback, §11 phase 3.
- CAD that exists only as a Revit link with no file on disk. Revit exposes no block names or text for it (`docs/cad-to-bim-limitations.md`).
- The CAD to BIM pane (`UI/CadToBim`). It stays the architectural tool. Its engine is reused, its UI is not touched.

## 3. Assumptions

These are guesses. Each one changes the design if it is wrong; §12 lists them as questions.

1. Electrical symbols are block inserts with names, on electrical layers.
2. The electrical DWG includes the architect's plan as background linework (bound xref or copied in).
3. Conduit, where modelled at all, is drawn as lines on a layer of its own.
4. The current Copilot model is text-only for this purpose. No image goes to the model in phases 1 and 2.
5. Block names, layer names and drawing text may be sent to the backend. Drawing geometry and pixels may not, in line with the statement in `DwgScratchCache.cs` that no DWG bytes leave the machine.

## 4. Flow

```
attach DWG ──► read_cad_electrical ──► AI fills mapping table ──► plan_cad_electrical ──► drafter reviews
 (existing)     census, no writes       (the only AI step)        align + resolve,         one card
                                                                  returns plan_id
                                                                        │
                              place_socket_points(plan_id) ◄────────────┤
                              route_conduit(plan_id)       ◄────────────┘
```

Three reads, then writes that take a `plan_id` and never coordinates. This is the split `SocketCandidates.cs` and `SocketPlacement.cs` already use, for the same reason: the drafter's confirmation and the committed geometry cannot drift apart.

## 5. Components

| # | Component | Where | Revit needed | New or existing |
|---|---|---|---|---|
| 1 | Symbol-aware DWG walk | `Cad2Bim/cad2bim/Services/CadRenderSource.cs` | no | extend |
| 2 | `ElectricalCensus` | `BinaVibe/Mcp/Tools/Electrical/` | no | new |
| 3 | `CadModelFit` (alignment) | `BinaVibe/Mcp/Tools/Electrical/` | no | new |
| 4 | `CadElectricalResolver` | `BinaVibe/Mcp/Tools/Electrical/` | yes (boundary only) | new |
| 5 | Plan cache | `Electrical/SocketPlanCache.cs` | no | extend |
| 6 | `place_socket_points` | `Electrical/SocketPlacement.cs` | yes | extend slightly |
| 7 | `route_conduit` | `MutatorsMepRouting.cs` | yes | new, modelled on `RoutePipe` |
| 8 | Tool schemas + mapping prompt | `bina-ai` | — | new |

Components 1–3 and 5 are pure C#. They are unit-tested on the Mac like `SocketLayout` and `SocketPlanCache` are today.

### 5.1 Symbol-aware DWG walk

`CadRenderSource.Walk` already resolves nested blocks, mirrored inserts and unit scale correctly, but `ICadSink` only receives `Polyline`, `Arc` and `Text`, so a block's identity is lost. `CadFileReader.Extract` keeps block names but reads top-level entities only and does no unit handling.

Change: one reader. Add an optional second interface next to `ICadSink`:

```csharp
public interface ICadSymbolSink {
    // Called for every Insert at any nesting depth, in world coordinates (drawing units).
    void Symbol(string blockName, string layer, double x, double y, double rotationRad,
                double scaleX, double scaleY, int depth,
                IReadOnlyDictionary<string, string> attributes,
                (double W, double H) localExtent);
}
```

`EmitInsert` calls it when the sink implements it. Existing sinks are unaffected. Units go through `Units.Resolve`, so everything downstream is millimetres. `CadFileReader` stays as it is for the door and window tools; converging them is a separate clean-up.

### 5.2 `ElectricalCensus`

Turns one walk into a table small enough for a prompt. No coordinates of individual instances.

Per block name:

- instance count, layers it appears on, nesting depth
- local extent in mm (a socket symbol is about 200–600 mm on paper scale; a title block is not)
- attribute tags and up to three sample values
- up to five distinct texts found within 1.5× its extent, with counts (for example `13A`, `S/S`, `WP`)
- how many instances lie within 400 mm of background wall linework

Anonymous blocks (`*U…`, `*D…`) are grouped by a geometry signature (sorted primitive counts and extent rounded to 10 mm) and reported as one class with a generated key.

Per layer:

- entity counts by kind, total line length, count of open polylines longer than 1 m
- whether its lines pair into walls under `CadClassifier.ClassifyWalls` (a hint that it is architectural background)

Whole drawing:

- unit scale and whether it came from the header or was inferred
- plan clusters from `CadClassifier.ClusterPlans` with their title texts
- legend rows: texts that sit beside an isolated symbol instance inside a small cluster (a legend is a column of symbol + description pairs)
- AEC object warning from `CadSourceVerdict`

Caps: 150 block classes, 150 layers, 40 legend rows, each with `truncated` flags.

### 5.3 The AI step: the mapping table

Input: the census, the project's electrical family types (`list_family_types`), conduit types, and levels. Output: one JSON object, tens of rows.

```json
{
  "symbols": [
    {"block": "SSO-13A", "role": "socket", "family_type": "Socket Outlet : 13A Single",
     "mount_height_mm": 300, "host": "wall", "evidence": "legend: '13A SWITCHED SOCKET OUTLET'"},
    {"block": "DB",      "role": "distribution_board", "family_type": null, "host": "wall",
     "evidence": "attribute TAG=DB-1"},
    {"block": "A$C1F2",  "role": "unknown", "evidence": "no legend row, no text"}
  ],
  "layers": [
    {"layer": "E-COND",  "role": "conduit", "conduit_type": "Conduit with Fittings : PVC", "diameter_mm": 20},
    {"layer": "A-WALL",  "role": "background"},
    {"layer": "E-WIRE",  "role": "wiring_schematic"},
    {"layer": "E-TEXT",  "role": "ignore"}
  ],
  "plans": [
    {"cluster": 0, "level": "Level 1", "evidence": "title 'PELAN LANTAI TINGKAT 1'"}
  ]
}
```

Rules for the prompt:

- `role` is one of a fixed list. `unknown` is a valid answer and is preferred over a guess.
- `family_type` must be a name from the list given, or `null`.
- `mount_height_mm` must come from the legend or drawing text; otherwise `null`. No standard value is baked into the add-in, the same policy `SocketCandidates.cs` states in its header.
- Every row carries `evidence`: the legend row, text or attribute it was read from.

The model never sees or returns a coordinate, and never issues one call per element. This avoids the failure recorded in `docs/cad-walls-session-handoff.md`, where 391 walls made the model stall.

Rows with `role: unknown`, a `null` family type or a `null` mount height become one question to the drafter before planning, listing only those rows.

### 5.4 `CadModelFit` — alignment

Finds the transform from drawing millimetres to model millimetres for one plan cluster and one level. Pure function over two sets of 2D segments.

Inputs:

- CAD side: wall centrelines from `CadClassifier.ClassifyWalls` over the layers mapped `background`. If the mapping names none, layers whose names match `CadToBimSettings.WallLayerHints`.
- Model side: location lines of walls on the target level, read once at the Revit boundary and converted to mm.

Method:

1. Scale is fixed by `Units.Resolve`. Rotation is tried at 0°, 90°, 180°, 270°, plus the angle between the two sets' dominant wall directions.
2. For each rotation, every pair (CAD wall, model wall) with the same direction and length within 5% votes for a translation in 50 mm bins.
3. The winning bin is refined to the median residual of its voters.
4. Score = share of CAD wall length lying within 75 mm of a model wall after the transform.

Outcome:

| Score | Action |
|---|---|
| ≥ 0.80 | accept, report the score |
| 0.50–0.80 | accept as provisional; the plan is flagged and the review card says so |
| < 0.50 | no plan; return `alignment_failed` with the fallback below |

Level choice: when the mapping gives no level for a cluster, fit the cluster against every level and take the best. If two levels score within 0.05 of each other (typical floors), ask the drafter.

Fallback when there is no background or the fit fails: the drafter links the same DWG into the plan view and moves it into place, which they already know how to do. The tool then finds the `ImportInstance` by file name (`DwgReader.PathOf`) and uses its transform. No new picking UI is needed. Whether `ImportInstance.GetTotalTransform()` carries the unit scale must be checked in Revit before relying on it.

### 5.5 `CadElectricalResolver`

Runs inside `plan_cad_electrical`. Applies the mapping and the transform to every instance.

For each symbol instance with a placeable role:

1. Transform the insertion point to model mm.
2. Find the room: `Document.GetRoomAtPoint` with the probe raised into the room volume, the same Z caution `SocketCandidates.BlockRuns` notes. Try the insertion point, then the point moved 150 mm along the symbol's facing.
3. Find the host wall when `host` is `wall`: nearest wall location line within reach (half the wall thickness plus 400 mm), using the scoring in `CadDoorsFromAttachment.FindHostWall`.
4. Snap to the room-side face of that wall. Facing is the wall normal pointing into the room. The symbol's own rotation is ignored for facing because drafters rotate symbols for legibility.
5. Height = `SocketCandidates.RoomFloorZMm` + mount height.
6. Emit a `PlannedPoint`.

Flags, each with a count in the result:

| Flag | Meaning | Default |
|---|---|---|
| `no_wall_in_reach` | wall-hosted role, no wall within reach | excluded from placement |
| `no_room` | point is in no room | kept, flagged |
| `wet_room` | room name matches the wet keyword list | kept, flagged (the drawing asked for it) |
| `already_present` | an electrical fixture exists within 150 mm | excluded |
| `linked_wall` | host wall is in a Revit link | placed unhosted if the family allows |
| `provisional_alignment` | fit score 0.50–0.80 | kept, flagged |

For each conduit layer:

1. Merge collinear segments (`CadClassifier.MergeCollinearSegments`) and chain them into polylines by shared endpoints.
2. Transform to model mm. Z = level elevation + `run_height_mm`, which is a required argument (for example above-ceiling height). No default.
3. Where a run end lies within 300 mm of a planned or existing device, add a vertical drop to the device height and record the device as the connect target.
4. Emit a `PlannedRun`.

### 5.6 Plan cache

Extend `SocketPlan` rather than adding a parallel cache.

`PlannedPoint` gains: `Role`, `SymbolKey`, `FamilyType` (nullable), `Flags`, `Source` (`"rule"` for today's tool, `"cad"` here). Existing fields are unchanged, so `suggest_socket_points` plans keep working.

`SocketPlan` gains: `Runs` (list of `PlannedRun`: index, points in mm, conduit type, diameter, connect targets, flags), `Alignment` (transform, score, level), and `SourceFile`.

The document-key check and the two-hour expiry in `SocketPlanCache.Get` apply unchanged.

## 6. Tools

| Tool | Kind | Args | Returns |
|---|---|---|---|
| `read_cad_electrical` | inspect | `dwg_ref` | the census (§5.2) |
| `plan_cad_electrical` | inspect | `dwg_ref`, `mapping`, `run_height_mm?`, `level?` | `plan_id`, alignment, counts per room and role, flagged items, unmapped classes |
| `place_socket_points` | mutate | existing; `family_type` becomes optional when the plan's points carry one; new `roles` filter | existing |
| `route_conduit` | mutate | either `points_mm` + type args (same shape as `route_pipe`), or `plan_id` + `run_indices` | segments, fittings, `open_connectors`, per-run failures |

Notes:

- Both read tools are registered as inspect so no Ya/Tidak card fires for a call that changes nothing.
- `place_socket_points` with a plan carrying several family types groups by type internally and still commits as one `TransactionGroup`, one undo step.
- `route_conduit` uses `Conduit.Create(doc, conduitTypeId, start, end, levelId)` per leg, sets the diameter, and reuses `CreateElbow`, `ResolveSnap` and `SnapEndpoint`, which already work on any `MEPCurve`. No MEP system type is needed for conduit. Elbows need a conduit type with fittings; a type without them returns a clear error rather than a run with gaps.
- In `plan_id` mode each run is its own transaction inside one group, so one failed elbow does not cost the other runs. This is the per-item tolerance `SocketPlacement.cs` argues for.
- `ToolManifest.g.cs` is regenerated by `scripts/gen-tool-manifest.py`; schemas and the mutate list live in `bina-ai`.

## 7. Review before commit

The drafter sees one plan summary from `plan_cad_electrical`:

- alignment: level, score, provisional or not
- a table: room × role, count in the drawing against count that will be placed
- the flagged list, grouped by flag, with room names
- classes left unmapped and how many instances that drops
- conduit: number of runs, total length, drops, ends that connect to nothing

Then one confirmation covers the write, following the owner decision that the single question before a wide write is accepted. After the write, Revit selects what was created and the result view shows the numbers that need a look in amber.

Excluding items is done by index (`indices`, `run_indices`) or by role, never by re-sending coordinates.

A preview drawn in the Revit canvas before commit would be better than a table. It is not in phase 1. If added later, in-canvas images must be `.bmp`.

## 8. Proof case, end to end

1. The drafter opens the model, attaches `E-01 GROUND FLOOR POWER.dwg` and types "place the sockets and conduit from this drawing on Level 1".
2. `read_cad_electrical` returns 23 block classes, 31 layers, a 9-row legend, one plan cluster, units mm from the header.
3. The model fills the mapping: three socket classes with types and 300 mm from the legend, `DB` as distribution board, `E-COND` as 20 mm conduit, `A-WALL` and `A-DOOR` as background, the rest ignore. Two classes come back `unknown`. The drafter is asked about those two only.
4. The drafter is asked for the conduit run height because the drawing does not state one.
5. `plan_cad_electrical` fits the background to Level 1 (score 0.93), resolves 84 sockets into 19 rooms, flags 3 as `no_wall_in_reach` and 2 as `already_present`, chains 14 conduit runs with 61 drops, and returns `plan_id`.
6. The drafter reads the summary and confirms.
7. `place_socket_points(plan_id)` places 79. `route_conduit(plan_id)` creates 14 runs; 4 drops end unconnected because that socket family has no conduit connector, and are listed.
8. Revit selects the new elements. The result shows 84 drawn, 79 placed, 5 skipped with reasons.

## 9. Errors

| Situation | Behaviour |
|---|---|
| DWG has AEC MEP objects and no plain geometry | refuse with the existing `EXPORTTOAUTOCAD` advice |
| No block inserts on any non-background layer | return the census with `symbols_are_exploded: true`; say plainly that this drawing needs the phase 3 path |
| Alignment below 0.50 | no plan; explain the link-and-position fallback |
| Family type in the mapping not in the document | rejected in `plan_cad_electrical`, before any card |
| Plan used in another document or after expiry | existing `SocketPlanCache.Get` errors |
| Model changed between plan and place | host walls are re-resolved by id at placement; a missing host fails that point only |

## 10. Testing

- Pure units on the Mac: census grouping, anonymous-block signature, legend detection, `CadModelFit` (synthetic plans with known offset, each rotation, a typical-floor tie, a no-background case), run chaining, plan cache round trip.
- Fixture drawings: real electrical DWGs stay out of the repository, as `Cad2Bim/README.md` already requires. Tests use small synthetic DXFs written by the test itself.
- Mapping quality: a fixed set of censuses with hand-written expected mappings, scored per row. Counts drift between identical runs, so any prompt change is judged against a control run at the same time with k ≥ 6.
- In Revit, on staging only: the proof case on one floor that a drafter has already modelled by hand, comparing positions (within 50 mm), hosts and heights. Check what is drawn, not the caption.

## 11. Phases

| Phase | Delivers | Exit check |
|---|---|---|
| 0 | Census only, run against 3–5 real drawings | answers assumptions 1–3 with numbers |
| 1 | Sockets: components 1–6, both read tools, placement | proof case sockets on one floor, ≥ 90% placed within 50 mm, every miss explained by a flag |
| 2 | `route_conduit` and drawn conduit runs | proof case conduit, all runs created or listed with a reason |
| 3 | Exploded symbols: geometry-signature grouping over loose linework, one rendered thumbnail per group to a vision model | only if phase 0 shows exploded symbols are common |

Phase 0 costs little and can cancel or reorder the rest. It should run before anything else is committed to.

## 12. Open questions

1. Is the input always a file on disk, or sometimes only a link inside Revit?
2. Are symbols named blocks (assumption 1)? Phase 0 answers this.
3. Does the electrical drawing carry the architectural background (assumption 2)?
4. Do drawings show real conduit routes, or wiring arcs only? If arcs only, is the wanted output generated conduit, circuits, or nothing beyond devices?
5. Which model does Copilot use on `develop`, and may thumbnails of single symbols leave the machine (phase 3)?
6. Do the project's socket families have conduit connectors?
7. How does a new tool pair enter the current playbook mechanism in `bina-ai`? This design stops at the add-in tool contract.
8. Does `ImportInstance.GetTotalTransform()` include unit scale (needed for the alignment fallback)?
