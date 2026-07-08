# BINA Copilot Engine — Phase 1 Windows UAT runbook

Phase 1 = inverted local transport. The agent loop runs as a local process
(bina-ai `app/engine`) next to Revit; the add-in's `McpServer` executes tools
over `127.0.0.1`; no cloud pause/resume. This runbook is the go/no-go evidence
for the Phase 1 gate: **parity with cloud on outcomes, strictly faster on tool
legs, zero stuck turns.**

macOS note: the add-in does not compile on macOS. All Task 7/8 C# is staged and
self-reviewed; this runbook is executed on a Windows + Revit machine after the
build.

## Build & install

1. Windows machine with Revit 2026: pull `feat/copilot-engine`, build (the
   existing PostBuild copies into `%APPDATA%\Autodesk\Revit\Addins\2026\`).
2. bina-ai on the same machine: pull `feat/copilot-engine`, `uv sync`.

## Configure

3. `%APPDATA%\RevitWebAppSync\config.json` — add (keys match `BinaConfig`
   property names):
   ```json
   {
     "EngineMode": true,
     "EnginePort": 48820,
     "EngineSecret": "<random-string>",
     "AIBaseUrl": "http://localhost:48810"
   }
   ```
4. Start the engine (staging env = model keys + RAG against Azure; the session
   db is local SQLite, no Postgres needed for sessions):
   ```
   set BINA_ENGINE=1
   set BINA_ENGINE_SECRET=<same-random-string>
   set ENVIRONMENT=staging
   uv run uvicorn app.engine.main:app --host 127.0.0.1 --port 48810
   ```
   Prerequisite: the office egress IP must be allowed through the Azure
   Postgres firewall (RAG retrieval still reads pgvector directly until the
   Phase 3 gateway lands).

## Verify transport (before any prompt)

5. Engine health:
   `curl http://127.0.0.1:48810/health` → `{"status":"ok","engine":true}`.
6. Add-in tool server, correct secret:
   ```
   curl -X POST http://localhost:48820/mcp/tools/list_levels ^
     -H "X-Bina-Secret: <random-string>" ^
     -H "Content-Type: application/json" ^
     -d "{\"tool_call_id\":\"t1\",\"args\":{}}"
   ```
   → 200 + real levels from the open model.
7. Wrong/missing secret: repeat step 6 with a bad `X-Bina-Secret` → **401**.
8. Idempotency: repeat step 6 twice with the **same** `Idempotency-Key: k1`
   header → identical response both times, and the add-in log shows **one**
   execution (`[BinaVibe][timing] tool=list_levels` appears once).
9. Modal-dialog check: open a Revit modal dialog, repeat step 6 → typed error
   within ~6s (`"Revit has a dialog open — close it and try again."`), **not a
   hang**.

## UAT prompts (same suite as cloud, run in the pane)

10. `senaraikan semua wall types` — expect an answer with **no `awaiting_revit`
    round**; the pane step trail shows the tool executing inline.
11. `tukar 10 tandas cangkung kepada duduk` (the model-sight suite model) —
    expect parity-or-better vs cloud on the same model.
12. Clarify path: `letak toilet` with no position — expect `awaiting_user_input`
    chips exactly as today (clarify is unchanged; it waits for a human).

## Record & gate

- For each prompt, pull **looks-per-turn** and **per-tool wall-clock** from
  Langfuse; compare against a cloud-mode run of the same prompts.
- **Gate to Phase 2:** parity on outcomes, strictly faster on tool legs, zero
  stuck turns, 401 on bad secret, single execution under a repeated
  idempotency key.

## Known Phase-1 deferrals (not blockers for this gate)

- SSE tool-frame shaping for inline tools is verified here (step 10 pane trail),
  not by an automated test — confirm the trail ticks the tool.
- The engine is started manually (`uvicorn`) in Phase 1. Auto-spawn, per-boot
  secret handoff, health-managed lifecycle, and OTA packaging are Phase 4.
- Inference still uses the model keys in the staging env directly; the metered
  `/gateway/v1` proxy (no key on disk) is Phase 3.
