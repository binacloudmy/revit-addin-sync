# Colocate the copilot — localhost engine, live geometry, vibe modeling

**Date:** 2026-07-08
**Repo:** revit-addin-sync (primary) + bina-ai (engine packaging + gateway; noted per phase)
**Status:** design v3 — adversarial review (2 blockers, 5 major, 4 minor) folded
in at v2; blocker solutions brainstormed with user 2026-07-08 and locked at v3:
**inverted synchronous transport** (user chose clean end-state over
smallest-diff loopback ping-pong) and **lean engine composition root + SQLite
session db** (no `get_agent_db()` changes, no direct Postgres from desktops).
Approved by user 2026-07-08; implementation started on `feat/copilot-engine`
(both repos, branched from develop).
**Naming:** the local agent process is the **BINA Copilot Engine** ("engine"
everywhere: `app/engine/`, `bina-engine.exe`, `EngineManager.cs`,
`BINA_ENGINE_PORT`). "Sidecar" was rejected by the user as confusing jargon.
**Supersedes:** the model-sight solver approach (`feat/copilot-model-sight` / `feat/model-sight-phase-1-2` — declared dead end 2026-07-08; placement-facts data shape and sight-capture chokepoint are salvaged, solvers are not)

## Problem

The copilot is blind by architecture, and the blindness has two causes that need
different fixes:

1. **Data never flows back.** Per turn the LLM sees names, ids, categories,
   level elevations, one crop box (`RevitChatRouter.BuildContext`,
   `UI/Copilot/RevitChatRouter.cs:606-659`). Read tools return ids + names +
   parameters — no bbox, location, facing, host, room.
2. **Looking is too expensive to do often.** Every mid-turn geometry read is a
   full cloud round-trip: agent pauses in Azure, `pending_tool_calls` come down,
   the addin executes and POSTs `tool/resume`, the run rehydrates and continues
   (`ToolLoopRunner.DriveAsync`, `Services/ToolLoopRunner.cs:145-259`, bounded
   at `MaxRounds = 8`). Cost per look: **seconds**. So the rational agent grabs
   the sparse snapshot once and fills the rest with priors — it guesses facing
   from the family name, and the drafter catches the error, not the agent.

The model-sight branch attacked cause 1 only (addin-computed placement facts +
deterministic C# placement solvers + VLM screenshots) while leaving cause 2
intact. With looking still expensive, correctness had to be decided one-shot, so
the solvers had to encode all drafter spatial judgment up front — and each UAT
round found the geometry case they hadn't met yet (backing-wall misfire, 90°
face_door bug), while the verify step shared the placement rule and certified
its own bugs. Dead end declared 2026-07-08.

**The reframe:** the copilot doesn't need to be right on the first try. It needs
to be able to **check** cheaply enough to be right by the second. That is a
latency-budget question, therefore an architecture question: move the agent loop
onto the drafter's machine.

## Evidence (verified 2026-07-08, 104-agent research sweep + repo scouts)

- Every working Revit/BIM LLM bridge colocates execution desktop-side with only
  inference in the cloud: **revit-mcp-python** (pyRevit Routes REST inside the
  Revit process on `localhost:48884` + a local FastMCP server forwarding tool
  calls; `execute_revit_code` for arbitrary geometry questions) and the
  **TU Munich Vectorworks copilot** (arXiv 2406.16903, in-process Python engine
  behind an AST sandbox). No working cloud-tunnel Revit copilot found. (3–0)
- **BIM-Edit** (arXiv 2606.20146): frontier models with full programmatic read
  access still average only **49.5%** spatial correctness, <3.4% of tasks fully
  solved → residual errors are guaranteed; the verify-retry loop and honest
  reporting must carry correctness, permanently.
- Same benchmark: self-discovered spatial reasoning costs **1.6–1.8×** output
  tokens vs being handed geometry (11.4k/9.9k vs 6.2k per task) → a per-turn
  scene digest pays for itself regardless of transport.
- Naive self-hosted SignalR reproduces the exact cross-instance affinity bug we
  already have (Microsoft SignalR scaling docs, 3–0) — the tunnel fallback, if
  ever needed, is Azure SignalR Service only. Not this spec.
- APS / AEC Data Model disqualified as a live channel: publish-gated extraction
  (minutes to an hour), Revit 2024+ only, upload-path coverage gaps, writes
  limited to extension properties. (3–0)

## Goal & success bar

Turn one drafter machine into the whole loop: **addin ↔ engine over
127.0.0.1**, cloud reduced to inference + RAG + billing. Vibe modeling = the
drafter types intent, watches the model change, gets a report the agent has
actually verified.

1. **Looks are free and used.** A mid-turn geometry read costs milliseconds,
   and telemetry shows looks-per-turn rising from ~0 today toward ≥1 per
   mutation. *This is the go/no-go metric for the whole bet (Phase 0).*
2. **Pause/resume dies.** No parked runs, no `tool/resume` over the internet,
   no `MaxRounds = 8` ceiling on how many times the agent may look.
3. **The agent reports what it verified, not what it hopes.** Every mutation is
   followed by a read-back of actual geometry and deterministic asserts derived
   from user intent; the final reply distinguishes verified from unverified.
4. **Business model intact.** No model API key on the customer machine; every
   inference call passes through BINA's metered cloud gateway; prompts are
   fetched per-session, never shipped in the installer.
5. **No regression for the drafter:** same pane, same streaming UX, same
   checkpoints/undo; offline behavior degrades to a clear "can't reach BINA"
   message (inference is cloud, so no-network = no copilot — unchanged from
   today).

## Current wiring (verified by scout, file:line)

What exists **already in the addin** — this spec is mostly un-gating and
rewiring, not greenfield:

- **Local tool server (the pyRevit-Routes role — scaffolded, NOT working):**
  `BinaVibe/Mcp/McpServer.cs` — `HttpListener` on `http://localhost:8080/`,
  `POST /mcp/tools/{name}` (body = bare args, tool name in URL). Started at
  `App.cs:296-309`, hard-gated off unless `BINA_VIBE_TOOLPATH=1`
  (`App.cs:281-294`). **Review finding (blocker if un-gated as-is): its job
  queue is orphaned** — it constructs a private `McpExternalEventHandler`
  (`McpServer.cs:45-47`) that the Idling pump never drains (the pump drains
  its own `McpToolHandler`, wired at `App.cs:193-197`), so every request would
  hang to the 600s `_jobTimeout` (`McpServer.cs:34-36`) and 504. It also
  bypasses `McpJobPump.Enqueue`, so the 6s modal/busy watchdog never arms for
  its calls. Phase 1 is therefore a **repair-and-rewire**, not an un-gate.
  (The stale comment at `McpExternalEventHandler.cs:8-9` claiming a shared
  queue is itself wrong — don't trust it.)
- **UI-thread execution pump:** `BinaVibe/Mcp/McpJobPump.cs` — Idling-driven
  drain with `SetRaiseWithoutDelay()` (`:98-113`), 6s modal/busy watchdog
  (`:122-151`); `McpExternalEventHandler.DrainOnce` →
  `ToolRegistry.Invoke` on the UI thread (`McpExternalEventHandler.cs:40-82`).
- **Tool surface:** `BinaVibe/Mcp/Tools/ToolRegistry.cs:31-116` — ~75 typed
  tools (Inspectors/Mutators/BatchExecutor), each mutator in its own
  `Transaction`, `TxGuard.StartSwallowing` for dialogs, `TransactionGroup` for
  batches.
- **Cloud transport to be retired:** `Services/ToolLoopService.cs` +
  `Services/ToolLoopRunner.cs` — generate → `awaiting_revit` → execute →
  `tool/resume` ping-pong, SSE twins, 620s HttpClient
  (`RevitChatRouter.cs:47-48`).
- **Base URL guardrails (review-corrected):** `ResolvedAIBaseUrl`
  (`BinaConfig.cs:110-125`) filters **only ngrok** — a loopback `AIBaseUrl` in
  `config.json` passes through today, so the spike needs **no addin change**
  for the URL. The loopback rejection lives on `ResolvedApiBaseUrl`
  (`:127-135`) and `ResolvedLoginWebUrl` (`:137-154`) — login/credits
  surfaces, which keep pointing at the cloud anyway.
- **WSS tunnel client:** `BinaVibe/Mcp/McpTunnelClient.cs` (gated off) — not
  used by this design; delete or leave gated (see Non-goals).
- **Sight code to salvage (branch `feat/model-sight-phase-1-2`, 20 ahead / 14
  behind develop):** `Inspectors.PlacementFacts` (`Inspectors.cs:600`),
  `CaptureImage` (`:236`), `AttachMutationSight` (`:364`, wired at
  `ToolRegistry.cs:49`). **Not salvaged:** `RoomSolver.cs` /
  `ResolveFacing` and the solver-consuming paths in `Mutators.cs:186,1146`.
- **Packaging rails that make a engine shippable:** versioned loader
  (`BinaLoader/LoaderApp.cs` — newest semver folder under
  `%LocalAppData%\Bina\RevitSync\versions\`), OTA `UpdateService.cs`
  (`version.json` feed, staged zips, sha256), Inno-Setup installer
  (`installer/RevitCopilot.iss`), CI release workflow.
- **Auth:** browser OAuth/PKCE → `BinaConfig.AccessToken`, sent as Bearer on
  every AI call (`ToolLoopService.cs:195-196`). No refresh flow (known gap,
  unchanged by this spec).

Backend (bina-ai) side, for reference: pause/resume machinery
`app/services/revit_turn.py:220-349`, tool defs
`app/agents/revit/copilot/tools.py` (VOLATILE INSPECT answered from pushed
snapshot; `external_execution=True` for the rest), context rendering
`app/services/revit_response_shaping.py:171-250`.

**Hard desktop-runtime constraints (review findings — these gate Phases 1/3):**

- `get_agent_db()` (`app/models/factory.py:27-53`) **raises unless
  `DATABASE_URL` is Postgres** — the SQLite fallback was deliberately removed
  (ClickUp 86ey3q5vg: it silently broke cross-instance `/tool/resume`). It runs
  at import time (`app/main.py:59`, `revit_ai.py:174`), so the app will not
  even start on a machine without a reachable Postgres.
- RAG is **not** an HTTPS call today: retrieval is direct SQL via agno
  `PgVector(db_url=DATABASE_URL)` (`app/knowledge/revit_recipes_kb.py:29,104`)
  and query embeddings hit Azure OpenAI directly with a key. "Outbound HTTPS
  only / no key on disk" therefore requires the Phase 3 retrieval endpoint —
  it is new gateway work, not a config change.
- `/tool/resume-input` (clarify) **shares the paused-run machinery** — its
  handler calls `_load_paused_run` (`router.py:483`) and the clarify pause is
  parked via `_stash_paused_run` (`revit_turn.py:282`). So "delete the
  paused-run store" is wrong as a blanket statement: it shrinks to an
  in-process clarify stash (fine in a single-process desktop); what actually
  dies is the cross-instance **db rehydrate** and the external-execution
  resume path.

## Design

### Resolved design decisions (brainstormed with user, 2026-07-08)

1. **Transport shape: inverted synchronous** (engine → addin McpServer per
   tool; run never pauses). Loopback ping-pong (keep today's addin-driven
   generate/execute/resume over localhost) was evaluated and would capture the
   same user-visible latency with far less work — the user explicitly chose
   the clean end-state architecture over the smallest diff, accepting the
   McpServer repair, new wire format, and SSE-emission move as the price.
   Ping-pong remains the Phase 0 spike topology (zero work, good enough for
   measurement).
2. **Session db: local SQLite**, composed explicitly by a lean engine
   entrypoint. Direct Postgres from desktops rejected (credentials on customer
   machines, firewall management, connection scaling). All shared data —
   recipes/JKR/Revit-API vectors, credits, learning signals, traces — stays in
   bina-ai cloud Postgres, reached via the gateway. Optional async session
   sync to cloud in Phase 3+.

### Topology (end state)

```
DRAFTER MACHINE ──────────────────────────────────────────────┐
│                                                             │
│  Revit process                     Engine process          │
│  ┌──────────────────────┐          ┌─────────────────────┐  │
│  │ BINA addin           │  POST    │ bina-ai agno loop   │  │
│  │  McpServer           │◄─────────│  (FastAPI, local)   │  │
│  │  127.0.0.1:48820     │  tools   │  127.0.0.1:48810    │  │
│  │  /mcp/tools/{name}   │─────────►│                     │  │
│  │  McpJobPump→Registry │  result  │  turn API for pane  │  │
│  └──────────┬───────────┘          └──────────┬──────────┘  │
│             │ pane POSTs turn, consumes SSE   │             │
│             └───────────────►─────────────────┘             │
└─────────────────────────────────────────────────────────────┘
                                                │ outbound HTTPS only
                                                ▼
                              BINA cloud gateway (unchanged host)
                              auth · credit meter · inference proxy
                              pgvector RAG · prompts · Langfuse · learning
```

- Addin keeps two roles: **UI** (pane posts the turn to the engine, consumes
  the same SSE stream) and **tool executor** (McpServer serves synchronous tool
  calls from the engine).
- The engine is the existing bina-ai agno loop relocated. Tool calls that are
  `external_execution=True` today become a **synchronous local HTTP call** to
  `127.0.0.1:48820/mcp/tools/{name}` — the run never pauses.
- Loopback binding is **platform-split** (review-corrected; the draft's blanket
  "always 127.0.0.1" rule was wrong): the **engine** (uvicorn, raw sockets)
  binds `127.0.0.1`. The **addin's** `HttpListener` keeps its
  `http://localhost:{port}/` prefix — on Windows a non-admin process may bind a
  `localhost` prefix without a URL ACL, while an explicit `127.0.0.1:` prefix
  requires `netsh http add urlacl` (elevation). http.sys matches on the Host
  header, so the engine **dials `http://localhost:48820`** to match the
  prefix. The OrbStack localhost-hijack is a macOS dev-box hazard only —
  handle with a dev-only env override, never a production rule. (Fallback if
  ACLs bite anyway: register the urlacl from the already-elevated Inno-Setup
  installer.)
- Ports: engine **48810**, addin McpServer **48820** (move off the
  collision-prone 8080 default; both overridable — `BINA_ENGINE_PORT`,
  existing `BINA_VIBE_MCP_PORT`).

### Where MCP and pyRevit fit (asked explicitly)

- **pyRevit:** we do **not** take a pyRevit dependency. pyRevit Routes' role —
  a REST server inside the Revit process with UI-thread marshalling — is
  already implemented natively as `McpServer` + `McpJobPump`. revit-mcp-python
  is the pattern proof, not a component we import.
- **MCP:** the addin's local surface already uses MCP-ish naming
  (`/mcp/tools/{name}`). Phase 5 makes it real: the engine exposes a standard
  MCP server facade over the same registry, so any MCP client (Claude Desktop,
  future BINA products, partner tools) can drive the drafter's Revit through
  BINA's typed, transacted, billable tool surface. Interop for free once the
  local topology exists; explicitly not on the critical path.

### Phase 0 — Spike: prove the thesis before packaging anything

*Repos: config + one tiny addin PR (`MaxRounds`). Days, not weeks.*

1. Run the existing bina-ai backend on a drafter Windows box:
   `ENVIRONMENT=staging uv run fastapi dev app/main.py` — staging, not dev,
   because `.env.dev` points `DATABASE_URL` at local docker pgvector `:5433`
   and drafter boxes have no docker. Prerequisites named honestly: office
   egress IP allowed through the Azure PG firewall, uv/Python toolchain on a
   possibly locked-down machine. This is the spike's first deliberate contact
   with the desktop-runtime risk.
2. Point the addin at the local backend via `config.json`
   `AIBaseUrl: "http://localhost:48810"` — **no addin change needed**;
   `ResolvedAIBaseUrl` filters only ngrok (review-corrected). Login/credits
   (`ApiBaseUrl`) keep hitting the cloud.
3. One small addin PR: make `MaxRounds` (`ToolLoopRunner.cs:55`, `const 8`)
   config-overridable and set 32 for the spike — the look ceiling is exactly
   the variable under test, and changing a const needs a rebuild anyway.
4. Everything else unchanged — same generate/resume ping-pong, every leg
   loopback. This is the exact topology of every UAT to date (Revit → ngrok →
   local FastAPI) minus ngrok.
5. **Measure:** looks-per-turn (external-execution INSPECT calls per turn) and
   wall-clock per look, from Langfuse traces. Success bar: the agent measurably
   looks more when looks are cheap. If it does not, stop here and fix
   recipes/prompting before building any packaging.

### Phase 1 — Kill pause/resume: engine calls the addin

*Repos: bina-ai (transport), revit-addin-sync (un-gate + slim). The core phase.*

**bina-ai:**
- **Tools become plain synchronous functions (locked at v3):** in the engine
  build, Revit tools are real implementations — the tool body calls
  `executor.call(tool_name, args)` → POST
  `http://localhost:48820/mcp/tools/{name}` → returns the result dict to agno.
  `external_execution=True` and run-pausing for tools disappear entirely; the
  loop is bounded by the model's judgment plus a spend guard, never a transport
  constant. The binding happens at the agent-builder seam (cloud root binds
  external-execution stubs as today; engine root binds executor-backed
  implementations). **Local wire format** (review finding — nothing existing
  matches): body `{tool_call_id, args}`, headers `Idempotency-Key` +
  `X-Bina-Secret`.
- **Lean engine composition root (locked at v3 — supersedes v2's
  "reinstate SQLite in `get_agent_db()`"):** `app/engine/main.py` composes
  only what the loop needs — the revit tool agent (via a shared agent-builder
  both roots call), an explicit
  `SqliteDb(%LocalAppData%\Bina\RevitSync\sessions.db)`, the executor, the
  gateway client, `/health`. It never imports the cloud composition root, so
  `get_agent_db()`'s Postgres guard stays untouched (no reversal of ClickUp
  86ey3q5vg needed) and the v2 import-time-audit task is moot — the engine
  never runs those imports. Side benefit: a much smaller PyInstaller bundle.
  SQLite is correct at this tier: the "Postgres in production" rule exists for
  multi-process cloud servers; a single-process single-user desktop app is the
  textbook SQLite case. Direct Postgres from drafter desktops was considered
  and **rejected** (raw DB credentials on customer machines, per-office
  IP-allowlist firewall management, 200 drafters × pool = connection blow-up).
  Optional Phase 3+: async session sync — after each turn the engine POSTs
  the transcript to the gateway, which writes it into bina-ai Postgres for
  roaming/support visibility; zero DB creds on desktops, zero turn latency.
- **Streaming emission moves here (review finding):** today the backend stream
  *ends* at terminal `awaiting_revit` (`ToolLoopService.cs:337`); the addin
  ticks executing-tool rows itself (`ToolLoopRunner.cs:214-235`) and folds the
  reply across rounds (`AppendRound`). With no rounds, the engine must newly
  emit per-tool `tool`/`status` frames around each local `/mcp/tools` hop —
  keyed by `tool_call_id` so the pane's `ProgressReducer` keying survives —
  and continuous `reply_partial` across what used to be resume legs.
- Timeout budget: 50s per local tool call; the McpServer rewire (below)
  restores the pump's 6s modal/busy watchdog for these calls. A typed error
  string comes back and the agent sees it like any tool error.
- Delete (once the engine transport is the only live path): the
  cross-instance **db rehydrate**
  (`aget_run_output` branch of `_load_paused_run`), `serialize_pending` /
  `apply_results` external-execution resume folding, `/tool/resume` +
  `/tool/resume/stream` routes. **Keep:** the in-process clarify stash —
  `/tool/resume-input` runs on it (`router.py:483`), and that pause is for a
  human, not for transport. **Keep** `_TOOL_MAX_ROUNDS` too (review-corrected:
  it bounds the in-process name-preflight loop, `revit_turn.py:247-250`, not
  transport — the ceiling that dies is the addin's `MaxRounds`).

**revit-addin-sync (repair-and-rewire, not un-gate):**
- Fix the orphaned queue: `McpServer.HandleRequest` routes through
  `McpJobPump.Enqueue` (`McpJobPump.cs:82-95`) instead of its private
  never-drained handler (`McpServer.cs:45-47,117-120`) — restores the 6s
  modal/busy watchdog and shared timing. Replace the 600s `_jobTimeout` with a
  50s request wait.
- New wire parse: `{tool_call_id, args}` body + `Idempotency-Key` header, with
  dedup — move `McpIdempotencyCache` wiring out of `McpTunnelClient.cs:38`
  (today its only consumer, and a component this spec retires) into
  `McpServer.HandleRequest`. This is new work; the draft wrongly implied it
  existed.
- Replace the `BINA_VIBE_TOOLPATH=1` gate (`App.cs:281-294`) with the engine
  config flag; default port → 48820; **keep the `localhost` HttpListener
  prefix** (Windows non-admin binding — see Topology).
- Interim loopback secret for Phases 1–3 (there is no spawn yet to pass a
  secret through — that arrives in Phase 4): both processes read it from a
  shared config location (`%APPDATA%\RevitWebAppSync\config.json` +
  engine config); `McpServer` validates it per request. Phase 4 upgrades to
  the per-boot spawn secret.
- `ToolLoopRunner` shrinks: no `awaiting_revit` branch, no client-side tool
  execution, no resume posting — a turn is one POST + one SSE stream until
  `done` or `awaiting_user_input`; `ToolTurn` parsing drops the
  `awaiting_revit` terminal.  `ToolLoopService` keeps generate/stream +
  `tool/resume-input` (clarify), drops `tool/resume`.
- Pane UI unchanged (bubbles, step trail, clarify cards) — but the *emission*
  of executing-tool events moves to the engine (see bina-ai list); the addin
  stops manufacturing its own executing rows.

**Idempotency & safety on the local hop:** keep `idempotency_key` on every tool
POST; `McpServer` deduplicates on it (retry-safe if the engine times out and
retries once). Mutations stay wrapped in the addin's own `Transaction` +
`TransactionGroup` machinery — the engine never gets a raw-code write path.

### Phase 2 — Give it eyes: generic reads + scene digest (salvage, not revive)

*Repos: revit-addin-sync (new inspector + cherry-picks), bina-ai (tool defs + prompt).*

- **`query_geometry` (new inspector, the workhorse):** one read-only tool
  replacing the solver zoo. Input: element ids (or a filter) + optional aspect
  list. Output per element: `xyz`, `bbox`, `facing` (unit vector),
  `rotation_deg`, `host_id`, `room`, `level`, plus relational asks:
  `nearest_walls(k)`, `distance_to(id|point)`, `clashes_with(ids)`. Contract =
  the placement-facts shape — **first cherry-pick
  `docs/contracts/placement-facts.md` from `c03c62e` onto bina-ai develop**
  (review finding: it exists only on the unpushed dead branch; delete that
  branch and the contract evaporates). The dead branch's data design was
  right; it was attached to the wrong loop.
- **Cherry-pick from `feat/model-sight-phase-1-2`:** `PlacementFacts`
  (`Inspectors.cs:600`) folded into inspector rows; `CaptureImage` (`:236`) +
  `AttachMutationSight` (`:364`) + the `ToolRegistry.cs:49` chokepoint as the
  screenshot **net** (weirdness detector, never the orientation authority —
  VLMs measured weakest exactly at orientation). **Do not merge the branch**
  (it drags a UI refactor and is 14 behind develop); cherry-pick the three
  symbol groups onto develop. `RoomSolver.cs` / `ResolveFacing` and their
  `Mutators.cs` call sites stay dead.
- **Scene digest:** extend `BuildContext` (`RevitChatRouter.cs:606-659`) with a
  working-set digest — selection + active-view elements (capped), each with
  placement facts, plus the `rooms[]` block from the dead branch's schema.
  Claws back the measured 1.6–1.8× discovery-token tax on every turn.
- **Verify becomes an independent oracle:** post-mutation asserts are built
  from the *user's intent* (in-room? faces the stall opening? clash count 0?
  moved < 50mm?) by calling `query_geometry` on the *result* — never by
  re-running the rule that placed. Recipes (bina-ai
  `app/knowledge/revit_recipes/`) get rewritten for loop behavior: act small →
  query_geometry → assert → fix → then scale out using the first verified
  placement as the reference example. Honesty contract stays: report verified
  vs unverified counts, never "dikekalkan" without a read-back.

### Phase 3 — The gateway: keep the business model server-side

*Repo: bina-ai (new thin service or routes on the existing app).*

- Engine holds **no model key**. Inference goes through an OpenAI-compatible
  proxy surface — `base_url = https://<bina-cloud>/gateway/v1`, `api_key` =
  the drafter's Bearer JWT — so the agno model config needs nothing custom.
  The gateway validates the JWT, checks credits, forwards to Azure
  OpenAI/DeepSeek, meters usage (existing ai-credits tables), traces to
  Langfuse, returns the stream.
- **Langfuse relay:** Langfuse keys stay server-side; the engine batches
  trace events to a gateway relay endpoint instead of talking to Langfuse
  directly.
- **Optional session sync (from the v3 db decision):** per-turn transcript
  POST → gateway → bina-ai Postgres, for roaming/support visibility. Not
  launch-blocking.
- **Retrieval-over-HTTPS endpoint (new gateway work — review finding):** today
  RAG is direct SQL (`PgVector(db_url=DATABASE_URL)`) plus a direct embedder
  call with a key; neither may run from the desktop under "outbound HTTPS only
  / no key on disk". The gateway grows `/gateway/retrieve` (recipes,
  revit_api, jkr_specs; query embedded server-side) and the engine's KB layer
  calls it instead of Postgres.
- Prompts + recipes: fetched at session start over the same authenticated
  channel and held in memory — prompt IP never ships in the installer.
- **JWT validation moves to the gateway:** the engine treats the drafter's
  Bearer token as opaque and forwards it; `BINA_AI_JWT_SECRET` never lands on
  the desktop.
- Learning loop unchanged: outcome/feedback posts go outbound; the distiller
  and admin drain stay cloud-side.
- Kill switch = deactivate the token at the gateway.

### Phase 4 — Packaging & lifecycle

*Repo: revit-addin-sync (installer, loader, updater) + bina-ai (engine build).*

- **Build:** PyInstaller (or embedded CPython) bundle of the bina-ai app →
  `bina-engine.exe`, versioned into
  `%LocalAppData%\Bina\RevitSync\engine\<ver>\` — same newest-semver pattern
  as `BinaLoader`.
- **Lifecycle:** addin startup (`App.cs`) health-checks
  `127.0.0.1:48810/health`; if absent, `Process.Start`s the newest engine with
  `--port 48810 --secret <handshake>`; kills orphans by pidfile. Engine
  exits when idle with no addin heartbeat for N minutes (no zombie Python on
  drafter machines).
- **Loopback auth:** addin generates a per-boot random secret, passes it to the
  engine on spawn; both directions (pane→engine turn API, engine→McpServer
  tools) require it as a header. Protects against other local processes hitting
  either port; both listeners bound to `127.0.0.1` only.
- **OTA:** extend `version.json` with a `engine{version,url,sha256}` block;
  `UpdateService.StageCoreAsync` pattern reused to stage engine zips. One feed,
  two artifacts, both hot-safe (never overwrite a running version dir).
- **Installer:** Inno-Setup gains the engine payload + a firewall-quiet
  loopback-only note. CI (`release.yml`) builds both artifacts + the manifest.

### Phase 5 — MCP facade (optional, after the loop is live)

*Repo: bina-ai (engine).*

- Expose the engine as a standard MCP server (FastMCP): each ToolRegistry tool
  + `query_geometry` becomes an MCP tool; resource subscription on document
  change events later if wanted. Any MCP client can then drive Revit through
  BINA's safe surface — interop as a product feature, zero effect on the pane.
- Deliberately **not** adopting `execute_revit_code`-style raw code execution
  from the LLM (revit-mcp-python's own docs: draft, no auth, demo-grade). If a
  read-only escape hatch is ever wanted for unanticipated spatial questions, it
  is a separate spec: AST-checked, allowlisted read-only API, no transactions.

## What gets deleted (the payoff ledger)

| Component | Where | Fate |
|---|---|---|
| Cross-instance db rehydrate + external-exec resume folding | bina-ai `revit_turn.py:220-244` (db branch), `revit_ai.py:455-509` | deleted (Phase 1). In-process clarify stash **stays** — `resume-input` runs on it (`router.py:483`) |
| `/tool/resume`, `/tool/resume/stream` | bina-ai `router.py:287-461` | deleted (clarify's `resume-input` stays) |
| `awaiting_revit` branch + client tool loop + `MaxRounds=8` | addin `ToolLoopRunner.cs:145-259` | deleted. bina-ai `_TOOL_MAX_ROUNDS` **stays** (preflight bound, not transport) |
| Cross-instance stuck-run class | both | gone by construction |
| ngrok in dev/UAT | workflow | gone |
| WSS tunnel client | addin `McpTunnelClient.cs` | delete or leave gated; superseded |
| RoomSolver / ResolveFacing solver stack | addin branch | never merged; stays dead |

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| Desktop runtime ownership (packaging Python, locked-down office PCs, version skew) | The main real cost. Rides the existing loader/OTA/installer rails; embedded-Python bundle avoids system Python; pin engine↔addin compatibility in `version.json` (update both or neither). |
| Two-process lifecycle bugs (orphans, port conflicts, AV false positives) | Health check + pidfile + idle exit; fixed uncommon ports, `127.0.0.1`-bound; code-sign the engine exe (CI signing lineage exists: `feat/ci-code-signing`). |
| Prompt/IP exposure on customer hardware | Prompts per-session over auth channel, memory-only. Same exposure class as every desktop AI product. |
| Billing bypass | No key on disk; all inference through the metered gateway; token kill switch. |
| Fleet observability (200 desktops vs 2 App Service instances) | Langfuse traces still central; engine log-shipping + `/health` surfaced in the pane's debug view. |
| Agent doesn't actually look more when looks are free | That's why Phase 0 is first and cheap. If looks-per-turn doesn't rise, fix recipes/prompt before packaging; the spike costs a config flag. |
| Model quality ceiling (BIM-Edit 49.5%) | Not fixable by transport. The product is the verify-retry loop + honest reporting; budget residual errors forever; keep the screenshot net. |
| Multi-user / roaming (drafter switches machines) | Session state is per-machine in Phase 1 (agno db → local SQLite). Cloud session sync deferred; chat history export is a later nicety. |

## Non-goals

- Azure SignalR / MCP-over-WSS tunnel — documented fallback if desktop runtime
  is vetoed by ops; not built here (`McpTunnelClient` stays gated/deleted).
- APS / AEC Data Model integration — disqualified as live channel; possible
  future async mirror for analytics/compliance, separate spec.
- Raw LLM code execution in Revit (`execute_revit_code` style) — separate spec
  if ever; typed tools only in this design.
- Set-of-marks visual prompting — revisit only after the geometry loop is live;
  vision stays a net.
- Token refresh flow — pre-existing gap, unchanged here.

## Sequencing summary

| Phase | Deliverable | Gate to next |
|---|---|---|
| 0 | localhost spike, config-only; looks-per-turn measured | metric rises materially |
| 1 | pause/resume dead; engine→McpServer synchronous tools | UAT parity with today, faster |
| 2 | `query_geometry` + scene digest + loop recipes + honest verify | facing/placement UAT (the cangkung→duduk suite) passes via loop, not solvers |
| 3 | metered gateway; no key/prompt on disk | security review |
| 4 | installer + OTA for engine; lifecycle management | pilot fleet (2–3 offices) stable |
| 5 | MCP facade | — |
