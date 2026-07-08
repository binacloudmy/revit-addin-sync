# BINA Copilot Engine — Phase 3 (metered gateway) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or executing-plans. Checkbox (`- [ ]`) steps.

**Goal:** The engine reaches the cloud only through subscription-checked APIs — no model key, no prompt IP, no DB credential on the desktop. This is also the phase that makes "just the AI part" a clean cut: after it, the engine's only cloud contact is `/gateway/*`.

**Architecture:** A thin cloud gateway on the existing bina-ai app. `/gateway/v1/chat/completions` = OpenAI-compatible inference proxy (validates the drafter JWT, checks+meters credits, forwards to the real Azure/DeepSeek, streams back). `/gateway/retrieve` = RAG over HTTPS (embed the query server-side, search pgvector, return hits) so the engine's KB layer stops needing a Postgres connection. Engine side: the model's `base_url` points at the gateway with the JWT as `api_key`; a `gateway_client` and an HTTPS-backed KB retriever.

**Tech Stack:** FastAPI / httpx / agno (bina-ai). Depends on Phases 1-2 (`feat/copilot-engine`). Spec: `docs/superpowers/specs/2026-07-08-copilot-engine-colocate-design.md` (Phase 3).

## Global Constraints

- Branch `feat/copilot-engine`. Stage-only (no commits). Cloud behavior byte-identical when `BINA_ENGINE` unset.
- The engine holds NO `AZURE_OPENAI_API_KEY` / `DEEPSEEK_API_KEY` / `DATABASE_URL` / `BINA_AI_JWT_SECRET`. Only a BINA JWT.
- Auth reuses the existing `require_tenant` dependency (`app/auth/tenancy.py`) and `BINA_AI_JWT_SECRET` (`app/auth/native_auth.py`) — the gateway validates; the engine treats the token as opaque.
- Credit metering reuses the existing ai-credits path (`app/routers/credits.py`) — do NOT invent a second ledger.
- bina-ai tests: only the files you touch. revit-addin-sync: none in this phase.
- **Billing safety:** the `/gateway/v1` inference proxy is money-path code. It must be integration-tested against a real (staging) upstream and reviewed before merge — NOT stacked blindly. Build behind a flag; validate before it becomes the engine default.

---

### Task 1: `/gateway/retrieve` — RAG over HTTPS (the safe, self-contained piece first)

**Files:**
- Create: `app/routers/gateway.py` (the `/gateway` router; retrieve endpoint)
- Modify: `app/main.py` (mount the gateway router — cloud app only)
- Test: `tests/test_gateway_retrieve.py`

**Interfaces:**
- Produces: `POST /gateway/retrieve` body `{"kb": "recipes"|"revit_api"|"jkr", "query": str, "top_k": int=5}`, `require_tenant`-gated, returns `{"ok": true, "hits": [{"text": str, "score": float, "meta": {...}}]}`. The engine's KB layer (Task 2) calls this instead of PgVector.

- [ ] Step 1: failing test — POST with a fake tenant dep override, assert shape `{ok, hits:[{text,score,meta}]}`; a bad `kb` → 400. (Use FastAPI `TestClient` + dependency_overrides for `require_tenant`; monkeypatch the KB search to return canned hits so the test doesn't need pgvector.)
- [ ] Step 2: run-fail.
- [ ] Step 3: implement `gateway.py`: `get_gateway_router()` factory (repo convention), `retrieve` endpoint mapping `kb` → the existing KB accessor (`get_recipes_kb` / `get_revit_api_kb` / `get_jkr_specs_kb`), embed+search server-side, shape the hits. Validate `kb` against an allowlist (400 otherwise).
- [ ] Step 4: run-pass. Mount in `app/main.py` next to the other routers.
- [ ] Step 5: stage.

### Task 2: Engine KB retriever over HTTPS

**Files:**
- Create: `app/engine/retrieval.py` (HTTPS-backed retriever)
- Modify: the engine's knowledge wiring (where `revit_ai.py` builds `_recipes_kb` — Phase 2 made it lazy) so engine mode uses the HTTPS retriever instead of PgVector
- Test: `tests/test_engine_retrieval.py`

**Interfaces:**
- Consumes: `/gateway/retrieve`, `get_engine_config()` (add `gateway_url` + `jwt`).
- Produces: `async def retrieve(kb: str, query: str, top_k: int = 5) -> list[dict]` — POSTs the gateway, returns hits; never raises (empty list + logged warning on failure, like the executor).

- [ ] Steps: failing test (MockTransport → canned hits; error → []), implement, wire engine-mode recipe retrieval to it (guard on `engine_enabled()`), pass, stage. Add `gateway_url`/`jwt` to `EngineConfig` (env `BINA_GATEWAY_URL`, `BINA_ENGINE_JWT`).

### Task 3: `/gateway/v1/chat/completions` — metered inference proxy (money path — flagged)

**Files:**
- Modify: `app/routers/gateway.py` (add the v1 proxy)
- Test: `tests/test_gateway_inference.py`

**Interfaces:**
- Produces: `POST /gateway/v1/chat/completions` — OpenAI-compatible. `require_tenant`-gated. Flow: validate JWT → check credit balance (existing credits path) → forward to the real upstream (Azure/DeepSeek, keys server-side) → stream the response back → meter usage on completion. 402 on insufficient credits.

- [ ] Step 1: failing tests — (a) insufficient credits → 402 (monkeypatch balance); (b) sufficient → forwards to a MockTransport upstream and streams back; (c) usage metered exactly once on completion (assert the credit-deduct call fired with the returned token counts). NO real upstream in tests.
- [ ] Step 2-4: implement behind `GATEWAY_INFERENCE_ENABLED` flag; forward with httpx streaming; meter on the completion event; map upstream errors to clean statuses. Reuse the credits module's deduct function — do not reimplement.
- [ ] Step 5: stage. **Do NOT make this the engine default until the integration test (real staging upstream + real credit deduction) passes in UAT.**

### Task 4: Engine model + prompts + Langfuse through the gateway

**Files:**
- Modify: `app/engine/main.py` / the engine agent build — set the model `base_url = {gateway_url}/gateway/v1`, `api_key = jwt`; fetch the system prompt at startup over the gateway; relay Langfuse events to a gateway endpoint.
- Test: `tests/test_engine_gateway_wiring.py`

**Interfaces:** engine-mode agent uses the gateway base_url; no provider key read in engine mode.

- [ ] Steps: failing test (in engine mode, the built agent's model base_url is the gateway, api_key is the jwt, and no AZURE/DEEPSEEK key is read), implement the engine-mode model seam (a small `get_engine_model()` mirroring `get_model()` but pointed at the gateway), pass, stage. Prompt-fetch + Langfuse relay can be minimal stubs wired to gateway endpoints (full impl optional this phase).

## Self-review
- Coverage: retrieve API (T1) + engine retriever (T2) sever the RAG→Postgres/key dependency; inference proxy (T3) severs the model-key dependency; model/prompt/telemetry wiring (T4) points the engine at the gateway. JWT-at-gateway + no-key-on-disk satisfied.
- Order: T1/T2 (safe, self-contained) first; T3 (money path) built behind a flag, validated in UAT before default. T4 ties it together.
- Deferred: prompt-fetch and Langfuse relay may ship minimal; session-sync (optional) is Phase 3+.
