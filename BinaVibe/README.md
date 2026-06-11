# BinaVibe — v2 client modules

These C# modules implement the client-side surface of **PRD v2** (the
"vibe modeling" architecture: ambient → plan → execute → review →
learn).

This folder runs **alongside** `UI/Copilot/` — the v1.5 Copilot pane is
unchanged and continues to call `/agents/revit-ai/route` + `/generate`.
v2 routes traffic to `/vibe/conversation/{id}/message` (SSE) and is
gated by a settings flag (`BinaConfig.UseVibeV2`) until each migration
step is proven on staging.

## Modules

- **Mcp/** — embedded HTTP listener (`localhost:8080` by default) that
  exposes the 10 INSPECT tools to the bina-ai backend's Inspector
  preflight. Boots on `App.OnStartup`; jobs are dispatched to Revit's
  main thread via `IExternalEventHandler`. Override port with
  `BINA_VIBE_MCP_PORT`. Matches the protocol the backend's
  `app.agents.vibe.mcp_client.call` speaks (POST
  `/mcp/tools/{name}`, JSON in / JSON out).
- **Bridge/** — HTTP+SSE client for `POST /vibe/conversation/{id}/message`,
  approval POSTs, snapshot uploads. Replaces the v1 `AIService.RouteAsync`
  call path when `UseVibeV2 = true`.
- **Indexer/** — `DocumentChanged` subscriber that ships element deltas
  to `POST /vibe/snapshot/{tenant}/{project}` for Channel 3.
- **Ambient/** — captures the per-turn `AmbientContext` payload
  (selection, view, project, units, role) the Bridge attaches to
  every message.
- **Plan/** — `PlanCardView` + `ApprovalCardView` WPF stubs the
  Copilot pane will host inline once the v2 pipeline lands.
- **Policy/** — local config for v2 enable, tenant id, sovereign flag.

## Test the embedded MCP server (once built + Revit running)

```powershell
# Health
curl http://localhost:8080/mcp/health

# Real Revit data — open a project first
curl -X POST http://localhost:8080/mcp/tools/list_levels -d '{}'
curl -X POST http://localhost:8080/mcp/tools/get_project_info -d '{}'
curl -X POST http://localhost:8080/mcp/tools/get_current_selection -d '{}'

# Tunnel out for the bina-ai backend
ngrok http 8080
# Then on the backend: REVIT_MCP_BASE_URL=https://<ngrok>.ngrok-free.app
```

## Status

Step 1 of 7. All files compile-skeleton only; this branch is unbuilt
(dev on macOS; Windows/Revit 2027 build pending).
