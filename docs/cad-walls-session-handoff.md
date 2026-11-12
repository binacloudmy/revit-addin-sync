# CAD walls-to-centerlines — session handoff

Snapshot for continuing the `cad_walls_to_centerlines` work in a fresh Claude
session (any machine). Point-in-time as of 2026-07-30.

## Environment / where things run
- Branch: **`feat/cad-walls-to-centerlines`** (revit-addin-sync).
- Revit **2027** runs the dev build via BinaLoader + `BINA_SYNC_PLUGIN_DIR` override, version baked to **999.0.0** to pass the OTA update gate. Build TFM **net10.0-windows**. (Revit is Windows-only; the Tests project is `net10.0-windows` + Revit API — it does NOT build on macOS.)
- `%APPDATA%\RevitWebAppSync\config.json`: **`EngineMode:false`** → the agent loop AND the tool schema live in the **bina-ai backend** reached over the ngrok `AIBaseUrl` (`AllowNgrokAIBaseUrl:true`). So schema changes happen in bina-ai, not the add-in; after a bina-ai change, restart that server and open a FRESH Copilot chat so the new schema is fetched.
- Build/run recipe and gotchas are in Claude project memory (`MEMORY.md`, `dev-run-workflow.md`) — but memory lives under `~/.claude` and does NOT travel to another machine; this doc does.

## Done & verified
- **cad_walls_to_centerlines v1 works**: centerlines land on the DWG wall lines; side-by-side floor plans place correctly; per-wall thickness measured from the CAD face-gap.
- **Corner fix landed** (commit `5ffa245`): rewrote `CadCenterlineSolver` corner pass — axial classification (correct end on skew/overshoot), trim/extend gated by new `CornerReachFt` (decoupled from snap), through-wall/T detection, multi-wall junction consolidation. **The old L-renders-as-T defect is fixed** (confirmed in Revit).
- **Tests: 10/10 on Windows** — 6 original facts + 4 new (`Overshoot_beyond_snap_is_trimmed_to_an_L`, `Undershoot_is_extended_to_an_L`, `Genuine_T_is_preserved`, `Three_wall_junction_consolidates_to_one_node`). Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CadCenterlineSolverTests"` (add `-p:SkipRevitSources=true` with no Revit install).
- **bina-ai schema** now exposes all 6 solver knobs (added `angle_tol_deg`, `overlap_min_ratio`, `min_wall_length_mm`, `snap_mm`, `corner_reach_mm`) — additive, defaults-preserving. The add-in already reads them via `ArgsHelp.GetDouble`; no C# change was needed for that.

## Empirical results (same DWG: CONTOH LAYOUT BANGUNAN 4 TINGKAT_ARKITEK, WALL layer)
- Full trace: **~391 proposed walls, 175 unpaired (invariant), dominant thickness ~114mm brick**. The DWG is **5 floor plans side-by-side** (not stacked) — creating all on Level 1 lays 5 footprints; that's expected.
- Tuning sweep (read-only): snap_mm↑ merges fragments → fewer walls (700→326, 500→391, 250→418); the 175 unpaired never change (true single-face lines). Corners are snap-tunable only up to the overshoot limit — the code fix (now landed) was the real answer.

## Open items
1. **Full-set build loops.** Prompting a 5-way thickness→type map over 391 walls makes the model manually bucket values in text and stall (never reaches create_wall). WORKAROUND: prompt **one thickness range → one type** (the pilot pattern), or build the whole set as a **single type** for corner checks, with explicit guardrails: "do NOT bucket, do NOT enumerate per wall, do NOT write a custom script — just filter proposed_walls and create." Proven single-band prompt created 36 walls cleanly.
2. **Batch-create ergonomics gap (highest-value next fix).** Creation is per-wall model-driven (391 create_wall calls + per-wall type decision) — doesn't scale. Fix: give `cad_walls_to_centerlines` a **create mode** — accept an optional `type_name` and/or `thickness_to_type` map and create the walls itself in ONE gated tool call. Needs revit-addin-sync tool work + a bina-ai schema param. Kills the loop failure entirely.
3. **Residual overshoots** beyond `corner_reach_mm` (default 500mm) aren't trimmed — bump `corner_reach_mm` (e.g. 900) if a stubborn corner shows a stub/gap. Judge corners by LOCATION LINES, not the ~100mm clash penetrations (those are normal Revit join overlap on 200mm walls).
4. Pilot-artifact caution: judging corners on ONE thickness band shows "dangling" ends because the perpendicular partner wall (a different band) wasn't created. Build the full set to judge corners fairly.

## Key facts about the tools
- `extract_cad_geometry` **times out unfiltered** on big DWGs — the cost is an O(n^2) endpoint-clustering over every layer (`CadExtract.cs` ~line 419). `layer_filter` is THE fix (shrinks the O(n^2) input). It is **substring match** (case-insensitive), no exact option — `"WALL"` also matched `17-lwall`, `Hatch-wall`, `LIFTWALL`, `WALLEXIST`. Use `get_dwg_layer_detail` for cheap layer names. Segment cap is 2000 (watch `truncated`).
- `cad_walls_to_centerlines` is 2D (x/y, ignores z) — stacked floor plans would collapse into duplicates; far-from-origin/huge-Z DWGs hurt precision.
- A Revit wall is a single straight element; an L-corner is ALWAYS two wall objects (one per leg). A straight run split into multiple objects = CAD drawn in segments / openings; `MergeCollinear` fuses collinear pieces only within `snap_mm` end-gap.

## Ready-to-use prompts
Single-type full-set corner check (new chat; avoids the loop):
> Run cad_walls_to_centerlines on layer_filter WALL, level Level 1. Then create every proposed wall whose thickness_mm is between 90 and 400 as a single type Generic - 100mm on Level 1. IMPORTANT: do NOT bucket by thickness, do NOT enumerate walls one by one, do NOT write a custom script or call extract_cad_geometry — just filter proposed_walls by the 90–400 range and create each. Report only the final count.

Delete a created band before rebuilding (avoid duplicates):
> Delete the walls you just created (the <band> Generic - <X>mm on Level 1) — I'm rebuilding fresh.

## Outstanding task prompts (drafted, not yet applied)
- **revit-addin-sync**: `CadCenterlineSolver` corner fix — DONE (`5ffa245`). Next: batch-create mode (item 2).
- **bina-ai**: schema params exposed — DONE. Next: `type_name`/`thickness_to_type` param + create-mode forwarding (item 2).
