# query_geometry — parametric relational primitives (deterministic spatial truth, zero hardcoding)

**Date:** 2026-07-09
**Repos:** revit-addin-sync (`QueryGeometry.cs`) + bina-ai (tool doc + prompt)
**Status:** design / awaiting review
**Branch:** `feat/copilot-engine`

## Problem

The MCP round-trip is proven (swap → query_geometry → rotate → move, end-to-end
on real Revit). But correctness of placement/facing still leans on the LLM
reasoning about **raw geometry**:

- `query_geometry` returns `facing: [0.02, 0.99]`, `xyz: [10.2, 5.1, 0]`. The
  agent must then do **trigonometry in the LLM** ("is that vector pointing at
  the door?") — and LLMs are unreliable at trig. This is exactly where the 90°
  facing bug lived: an **angle-computation** error, not a reasoning error.
- A per-scenario verdict (`faces_door: true`) would be deterministic but
  **hardcoded**: a window needs `on_exterior_wall`, a door needs
  `swings_into_room` — each new drafting task = new C#. That is the model-sight
  solver-zoo trap.

## Principle (the test for "is this hardcoded")

> **A new use case must NOT require new C#.**

- `faces_door` → "window on exterior wall" needs new C#. **Hardcoded.**
- `nearest:<category>` + `angle_from_facing_deg` → "window on exterior wall" is
  `nearest:exterior_wall` + `hosted_on`, no new C#. **Not hardcoded.**

The line is **relational primitives** (a *closed, universal* set — there are
only so many geometric relations: nearest, angle, distance, containment,
intersection, host, alignment) vs **scenario verdicts** (an *open, unbounded*
set — one per drafting task, forever). Primitives never grow with use cases.

## Design

### 1. The tool: parametric, returns NUMBERS not verdicts

`query_geometry(element_ids, want=[...])`. The add-in computes only the
requested primitives and returns **exact numbers** — the agent does trivial
comparisons, never trig, never receives a baked judgment.

`want` vocabulary — the closed, universal set (nothing scenario-specific):

| `want` item | Returns (numbers) | Revit native API (ground truth) |
|---|---|---|
| `nearest:<category>` | `{id, distance_mm, angle_from_facing_deg, direction:[x,y]}` | `FilteredElementCollector` + exact `Distance`/vectors |
| `in_room` | `{room, number, is_inside: bool}` | `Room.IsPointInRoom(location)` |
| `clashes` | `[{id, category, penetration_mm}]` | `InterferenceOutputResults` / solid-solid (not bbox) |
| `angle_to:<target>` | `{angle_from_facing_deg}` | `FacingOrientation` · vector math |
| `distance_to:<target>` | `{distance_mm}` | exact point/curve distance |
| `hosted_on` | `{host_id, host_category}` | `FamilyInstance.Host` |
| `aligned_with:<target>` | `{parallel: bool, angle_deg}` | direction vectors |

`<category>` = a Revit category (`door`, `wall`, `window`, `column`, …) or
`exterior_wall`. `<target>` = `element:<id>` \| `point:[x,y]` \|
`nearest:<category>` \| `room_center` \| `host`.

**Base facts always returned** (no `want` needed): `xyz`, `facing`, `bbox`,
`rotation_deg`, `level`, and — critically — **`location_known: bool`**.

### 2. Kill the axis-0,0 footgun (Tier 1 #2)

- Unresolved location → **`xyz: null`, NEVER `[0,0,0]`**; `location_known:
  false`. `[0,0,0]` is a valid coordinate (project origin); it must never double
  as a sentinel.
- Every derived primitive that depends on an unknown location returns `null`
  for that field (never a fabricated number).
- The agent is forbidden (prompt rule) from claiming a placement is correct when
  `location_known` is false — it says "couldn't verify position for #N".

### 3. Independent oracle by construction (Tier 1 #3)

- The agent picks `want` items **from the user's intent** ("faces the door" →
  `nearest:door` + read `angle_from_facing_deg`).
- The add-in computes them against the **live model AFTER the mutation**, via
  Revit's own APIs.
- Placement code and `query_geometry` share **no** facing/room computation — so
  the check is an independent measurement, not a replay of the placer's math. It
  can actually catch the placer being wrong (the model-sight lesson).

### 4. How the agent consumes it (the whole point)

Trivial number comparison, never trig:

```
# user: "tukar tandas cangkung → duduk, hadap pintu"
swap(101)
q = query_geometry([101], want=["nearest:door", "in_room", "clashes"])
# q[101] = {facing:[...], location_known:true,
#           nearest_door:{id:7, distance_mm:800, angle_from_facing_deg:92},
#           in_room:{room:"Tandas 1", is_inside:true},
#           clashes:[]}
if q[101].nearest_door.angle_from_facing_deg > 25:   # 92 > 25 → wrong way
    rotate_elements(101, 180); re-query          # re-measure, confirm < 25
report: "1 swapped, facing verified (angle 4°), in room, 0 clashes"
```

The `92` was computed once, correctly, in C#. The LLM only compares `92 > 25` —
which it never gets wrong. The 90° bug cannot recur.

## Escape hatch (future — NOT this build)

For the rare spatial question the primitives don't cover, a **sandboxed,
read-only geometric expression** the agent authors and an evaluator runs
exactly. Full generality, but off the hot path (95% of drafting is covered by
the primitives), so its sandbox complexity isn't paid on every turn. Separate
spec if/when a real case needs it.

## Migration from the current `aspects` shape

`QueryGeometry.cs` (f5905c0) has `aspects=["nearest_walls","clashes"]` returning
raw wall lists. Evolve, don't rewrite:
1. Accept `want` (new) alongside `aspects` (kept as alias for one release).
2. `nearest_walls` → `nearest:wall`; enrich each `nearest` hit with
   `distance_mm` + **`angle_from_facing_deg`** (the derived number that removes
   the LLM trig — the key add).
3. `clashes` → keep, but compute via `InterferenceOutputResults` where feasible
   (the current bbox+`CLASH_TOL_FT` is a good approximation; native is truth).
4. Add `location_known` + null-not-zero everywhere.

## Build tasks

**revit-addin-sync (`QueryGeometry.cs`) — Windows-gated:**
1. Parse `want[]`; dispatch each primitive to its computer.
2. Implement primitives via Revit native APIs (table above); return numbers;
   `angle_from_facing_deg` on every `nearest`/`angle_to`.
3. `location_known` + null-not-zero audit across `PlacementFacts` + primitives.

**bina-ai (Python) — testable now:**
4. `query_geometry` tool docstring: teach the `want` vocabulary and the rule
   **"read the numbers and compare — NEVER compute an angle yourself."**
5. Prompt (`revit_ai_tool.md`): honesty rule for `location_known:false`; the
   act→query(want)→compare→fix loop.
6. Tests: tool accepts `want`, dispatches; contract test the docstring teaches
   compare-not-trig + the location_known honesty rule.

## Self-review
- Zero scenario C#: every use case is a composition of the closed primitive set.
- Deterministic: all trig/containment/clash in C# via Revit native APIs; LLM
  only compares numbers.
- Footgun killed: `location_known` + null-not-zero.
- Independent oracle: agent picks `want` from intent, C# measures live model,
  no shared code with the placer.
