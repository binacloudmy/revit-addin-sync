# BINA Copilot Engine — Phase 2 Windows UAT runbook

Phase 2 = the eyes. `query_geometry` returns real placement facts (xyz, facing,
host, room, bbox); a scene digest of the selection rides every turn; recipes
teach act → read → verify → fix. This is where "understands the model" becomes
literally true for the spatial stuff.

Prereq: Phase 1 engine UAT passed (see `engine-phase1-uat.md`). Same
`config.json` (EngineMode + secret) and same start command.
macOS: the C# (QueryGeometry.cs, BuildContext digest) is staged only; run this
on Windows + Revit after the build.

## Setup

1. Rebuild the add-in on `feat/copilot-engine` (now includes `QueryGeometry.cs`
   + the BuildContext scene digest).
2. bina-ai on `feat/copilot-engine`, `uv sync`, and **re-ingest recipes** so the
   loop recipe is retrievable:
   `ENVIRONMENT=staging uv run python scripts/ingest_revit_recipes.py`.
3. Start the engine as in Phase 1
   (`BINA_ENGINE=1 BINA_ENGINE_SECRET=... ENVIRONMENT=staging uvicorn app.engine.main:app --host 127.0.0.1 --port 48810`).

## Verify the eyes (transport-level, before prompts)

4. Pick a real door id in the open model (e.g. from the Revit UI). Then:
   ```
   curl -X POST http://localhost:48820/mcp/tools/query_geometry ^
     -H "X-Bina-Secret: <secret>" -H "Content-Type: application/json" ^
     -d "{\"tool_call_id\":\"g1\",\"args\":{\"element_ids\":[<door id>]}}"
   ```
   Expect `{"ok":true,"elements":[{"id":...,"xyz":[...],"facing":[x,y],
   "host_id":<the wall>,"room":<name or null>,"bbox":[...],"rotation_deg":...,
   "level":...}]}` — real numbers from the live model.
5. Aspects: repeat with `"aspects":["nearest_walls","clashes"]` in args → the row
   gains `nearest_walls` (4 wall ids + normals) and `clashes` (overlapping ids).
6. A bad id → that id appears in `skipped_ids`, the call still returns `ok:true`.

## The real test — the facing bug that killed model-sight

7. Select a few WC fixtures, open the pane, and run
   `tukar 10 tandas cangkung kepada duduk`.
8. Watch the Langfuse trace. **Expected NEW behaviour:**
   - the agent swaps, then calls `query_geometry` on the new ids,
   - reads `facing` / `room` from the result,
   - for any fixture facing the wrong way it calls `rotate_elements` 180° and
     calls `query_geometry` AGAIN to confirm,
   - the reply states orientation was verified **from geometry** (not a
     self-assessed `facing_confidence`), or says "orientation UNVERIFIED" if a
     fact came back null.
9. Scene digest: confirm the turn's context (Langfuse input) contains a `scene`
   block with `{id, xyz, facing, room, host_id}` for the selected fixtures.

## Gate to Phase 3

- `query_geometry` returns real facts for real ids (step 4).
- On the swap prompt, the agent **reads facing back and self-corrects** wrong
  ones — the model-sight failure (agent claimed success while fixtures faced the
  wrong way) does NOT recur, and correctness comes from the read-verify loop, not
  a solver.
- Scene digest present in the turn context.
- No stuck turns; parity-or-better vs Phase 1 on the non-spatial prompts.

## Deferred to Phase 2.1 / later

- `nearest_walls` / `clashes` are a proximity/bbox signal, not precise solids
  clash — refine if UAT needs it.
- Auto-sight screenshot net (the dead branch's CaptureImage / AttachMutationSight)
  is a separate later task; Phase 2 is geometry-first, vision stays a net.
