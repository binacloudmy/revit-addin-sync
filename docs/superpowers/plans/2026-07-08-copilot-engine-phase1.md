# BINA Copilot Engine — Phase 1 (inverted local transport) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run the copilot agent loop as a local "engine" process next to Revit: tools execute synchronously over localhost (no pause/resume rounds), sessions in local SQLite, cloud path untouched.

**Architecture:** Two repos. In **bina-ai**, tools flip from `external_execution=True` (agno pauses, addin polls) to real bodies that POST the addin's local HTTP tool server — gated by `BINA_ENGINE=1` at import so one codebase serves cloud and engine. A lean engine entrypoint composes the app with an explicit SQLite session db. In **revit-addin-sync**, the existing-but-broken `McpServer` (orphaned private job queue) is repaired to route through the shared `McpJobPump`, gains the new wire format + secret + idempotency, and is gated by an `EngineMode` config flag. The addin's cloud ping-pong path stays fully working — engine mode simply never returns `awaiting_revit`, so `ToolLoopRunner` needs no change in Phase 1.

**Tech Stack:** Python 3.12 / FastAPI / agno / httpx (bina-ai); C# .NET 8 Revit add-in, `HttpListener` + Idling pump (revit-addin-sync). Spec: `revit-addin-sync/docs/superpowers/specs/2026-07-08-copilot-engine-colocate-design.md`.

## Global Constraints

- Component name is **engine** everywhere: `app/engine/`, `BINA_ENGINE`, `EngineMode`, `EngineManager`. Never "sidecar".
- Ports: engine API **48810**; addin tool server **48820**. Addin `HttpListener` prefix stays `http://localhost:{port}/` (Windows non-admin URL-ACL rule). Python binds/dials per spec Topology section.
- Local wire: `POST /mcp/tools/{name}`, body `{"tool_call_id": str, "args": {…}}`, headers `Idempotency-Key`, `X-Bina-Secret`. Tool wait cap **50s**.
- `get_agent_db()`'s Postgres guard in `app/models/factory.py` must NOT be weakened; engine db selection is a separate explicit function.
- Cloud behavior must be byte-identical when `BINA_ENGINE` is unset — every bina-ai change is behind that flag.
- bina-ai tests: run ONLY the test files you create/touch (`uv run pytest tests/<file> -v`) — the full suite hangs on staging Postgres at collection (CLAUDE.md §Tests).
- revit-addin-sync does NOT compile on macOS (Windows/Revit gate). C# tasks end at "code staged + self-reviewed"; the Windows build + Revit UAT is a documented follow-up, same as every prior addin change.
- Commits: each task has a commit step; if the operator has not granted blanket commit permission, STAGE (`git add`) and leave the commit line for the user.

---

## Repo A — bina-ai (branch `feat/copilot-engine`)

### Task 1: Engine config module

**Files:**
- Create: `app/engine/__init__.py` (empty)
- Create: `app/engine/config.py`
- Test: `tests/test_engine_config.py`

**Interfaces:**
- Produces: `engine_enabled() -> bool`; `EngineConfig` dataclass with `engine_port:int=48810`, `addin_tool_url:str="http://localhost:48820"`, `secret:str`, `db_path:Path`; `get_engine_config() -> EngineConfig` (cached).

- [ ] **Step 1: Write the failing test**

```python
# tests/test_engine_config.py
import importlib

def _reload(monkeypatch, **env):
    for k in ("BINA_ENGINE", "BINA_ENGINE_PORT", "BINA_ADDIN_TOOL_URL",
              "BINA_ENGINE_SECRET", "BINA_ENGINE_DB"):
        monkeypatch.delenv(k, raising=False)
    for k, v in env.items():
        monkeypatch.setenv(k, v)
    import app.engine.config as cfg
    importlib.reload(cfg)
    return cfg

def test_engine_disabled_by_default(monkeypatch):
    cfg = _reload(monkeypatch)
    assert cfg.engine_enabled() is False

def test_engine_enabled_and_defaults(monkeypatch, tmp_path):
    cfg = _reload(monkeypatch, BINA_ENGINE="1",
                  BINA_ENGINE_SECRET="s3cret",
                  BINA_ENGINE_DB=str(tmp_path / "sessions.db"))
    assert cfg.engine_enabled() is True
    c = cfg.get_engine_config()
    assert c.engine_port == 48810
    assert c.addin_tool_url == "http://localhost:48820"
    assert c.secret == "s3cret"
    assert c.db_path.name == "sessions.db"

def test_missing_secret_fails_loud(monkeypatch):
    cfg = _reload(monkeypatch, BINA_ENGINE="1")
    try:
        cfg.get_engine_config()
        assert False, "expected RuntimeError"
    except RuntimeError as e:
        assert "BINA_ENGINE_SECRET" in str(e)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/test_engine_config.py -v`
Expected: FAIL — `ModuleNotFoundError: app.engine`

- [ ] **Step 3: Write minimal implementation**

```python
# app/engine/config.py
"""BINA Copilot Engine — local-process configuration.

Read once at import; ``BINA_ENGINE=1`` flips tool transport from
external-execution (cloud pause/resume) to synchronous localhost calls.
"""
import os
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path


def engine_enabled() -> bool:
    return os.getenv("BINA_ENGINE", "").strip() == "1"


@dataclass(frozen=True)
class EngineConfig:
    engine_port: int
    addin_tool_url: str
    secret: str
    db_path: Path


@lru_cache(maxsize=1)
def get_engine_config() -> EngineConfig:
    secret = os.getenv("BINA_ENGINE_SECRET", "").strip()
    if not secret:
        raise RuntimeError(
            "BINA_ENGINE_SECRET is required in engine mode — both the addin "
            "and the engine read it from shared config; refuse to run open."
        )
    default_db = Path.home() / ".bina" / "engine" / "sessions.db"
    return EngineConfig(
        engine_port=int(os.getenv("BINA_ENGINE_PORT", "48810")),
        addin_tool_url=os.getenv("BINA_ADDIN_TOOL_URL", "http://localhost:48820").rstrip("/"),
        secret=secret,
        db_path=Path(os.getenv("BINA_ENGINE_DB", str(default_db))),
    )
```

Note: `lru_cache` + `importlib.reload` in tests — `reload` re-creates the cache, so per-test env changes are picked up.

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/test_engine_config.py -v`
Expected: 3 PASS

- [ ] **Step 5: Stage (commit only with user approval)**

```bash
git add app/engine/__init__.py app/engine/config.py tests/test_engine_config.py
git commit -m "feat(engine): config module for local engine mode"
```

### Task 2: Local tool executor

**Files:**
- Create: `app/engine/executor.py`
- Test: `tests/test_engine_executor.py`

**Interfaces:**
- Consumes: `get_engine_config()` from Task 1.
- Produces: `async def call_tool(tool: str, args: dict, tool_call_id: str | None = None) -> dict` — always returns a dict; failures return `{"ok": False, "error": "<typed message>"}` (never raises), matching how tool errors already reach the agent via `apply_results`.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_engine_executor.py
import httpx
import pytest

@pytest.fixture()
def engine_env(monkeypatch, tmp_path):
    monkeypatch.setenv("BINA_ENGINE", "1")
    monkeypatch.setenv("BINA_ENGINE_SECRET", "s3cret")
    monkeypatch.setenv("BINA_ENGINE_DB", str(tmp_path / "s.db"))
    import importlib
    import app.engine.config as cfg
    importlib.reload(cfg)
    import app.engine.executor as ex
    importlib.reload(ex)
    return ex

@pytest.mark.anyio
async def test_success_posts_wire_format(engine_env):
    ex = engine_env
    captured = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["url"] = str(request.url)
        captured["secret"] = request.headers.get("X-Bina-Secret")
        captured["idem"] = request.headers.get("Idempotency-Key")
        captured["body"] = request.read().decode()
        return httpx.Response(200, json={"ok": True, "levels": []})

    transport = httpx.MockTransport(handler)
    result = await ex.call_tool("list_levels", {}, tool_call_id="tc-1", _transport=transport)
    assert result == {"ok": True, "levels": []}
    assert captured["url"].endswith("/mcp/tools/list_levels")
    assert captured["secret"] == "s3cret"
    assert captured["idem"]  # non-empty
    assert '"tool_call_id": "tc-1"' in captured["body"] or '"tool_call_id":"tc-1"' in captured["body"]

@pytest.mark.anyio
async def test_http_error_is_typed_not_raised(engine_env):
    ex = engine_env
    transport = httpx.MockTransport(lambda r: httpx.Response(504, text="tool timed out"))
    result = await ex.call_tool("place_door", {"x": 1}, _transport=transport)
    assert result["ok"] is False
    assert "504" in result["error"]

@pytest.mark.anyio
async def test_connect_error_names_the_addin(engine_env):
    ex = engine_env

    def boom(request):
        raise httpx.ConnectError("refused")

    result = await ex.call_tool("list_levels", {}, _transport=httpx.MockTransport(boom))
    assert result["ok"] is False
    assert "Revit" in result["error"]  # drafter-comprehensible error
```

Add at top of file if the repo's anyio plugin needs it: `pytestmark = pytest.mark.anyio` and fixture `anyio_backend` returning `"asyncio"` (copy the pattern from an existing async test file, e.g. `tests/test_model_sight_tools.py` if present, else define `@pytest.fixture def anyio_backend(): return "asyncio"`).

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/test_engine_executor.py -v`
Expected: FAIL — `ModuleNotFoundError: app.engine.executor`

- [ ] **Step 3: Write minimal implementation**

```python
# app/engine/executor.py
"""Synchronous local tool transport: engine -> addin McpServer.

One POST per tool call; the agno run never pauses. Errors come back as
typed ``{"ok": False, "error": ...}`` dicts so the agent sees them exactly
like any failed tool result and can react (retry, report honestly).
"""
import json
import uuid

import httpx

from app.engine.config import get_engine_config

_TOOL_TIMEOUT_S = 50.0  # mirrors the addin-side job wait cap


async def call_tool(
    tool: str,
    args: dict,
    tool_call_id: str | None = None,
    _transport: httpx.AsyncBaseTransport | None = None,
) -> dict:
    cfg = get_engine_config()
    url = f"{cfg.addin_tool_url}/mcp/tools/{tool}"
    headers = {
        "X-Bina-Secret": cfg.secret,
        "Idempotency-Key": uuid.uuid4().hex,
        "Content-Type": "application/json",
    }
    body = {"tool_call_id": tool_call_id or uuid.uuid4().hex, "args": args or {}}
    try:
        async with httpx.AsyncClient(transport=_transport, timeout=_TOOL_TIMEOUT_S) as client:
            resp = await client.post(url, content=json.dumps(body), headers=headers)
    except httpx.ConnectError:
        return {
            "ok": False,
            "error": "Cannot reach Revit — is the BINA add-in running with "
                     "Engine mode on? (addin tool server not reachable)",
        }
    except httpx.TimeoutException:
        return {"ok": False, "error": f"tool {tool} timed out after {int(_TOOL_TIMEOUT_S)}s"}
    if resp.status_code != 200:
        return {"ok": False, "error": f"tool {tool} failed: HTTP {resp.status_code} {resp.text[:300]}"}
    try:
        return resp.json()
    except ValueError:
        return {"ok": False, "error": f"tool {tool} returned non-JSON: {resp.text[:300]}"}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/test_engine_executor.py -v`
Expected: 3 PASS

- [ ] **Step 5: Stage/commit**

```bash
git add app/engine/executor.py tests/test_engine_executor.py
git commit -m "feat(engine): synchronous local tool executor with typed errors"
```

### Task 3: Flip the tool transport seam in `tools.py`

**Files:**
- Modify: `app/agents/revit/copilot/tools.py` (decorator lines + `_read`/`_mutate` bodies; `_read` is near `:93-125`, `_mutate` at `:459-476` — locate by symbol, lines drift)
- Test: `tests/test_engine_tool_transport.py`

**Interfaces:**
- Consumes: `engine_enabled()` (Task 1), `call_tool` (Task 2).
- Produces: in engine mode every Revit tool is a plain callable whose body executes `call_tool(name, args)`; in cloud mode decorators keep `external_execution=True` and bodies stay never-invoked.

Background for the implementer: today ~50 tools are decorated `@tool(external_execution=True, external_execution_silent=True)` and their bodies `return await _mutate(name, args)` / `await _read(name)` — but agno never calls those bodies; `_mutate`'s docstring says "NEVER executed … if a body ever runs it means external_execution was misconfigured, so fail loud". We are deliberately making the bodies real in engine mode. The two helpers are the entire dispatch seam; the decorator flag is the only other change.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_engine_tool_transport.py
import importlib

def _import_tools(monkeypatch, tmp_path, engine: bool):
    if engine:
        monkeypatch.setenv("BINA_ENGINE", "1")
        monkeypatch.setenv("BINA_ENGINE_SECRET", "s3cret")
        monkeypatch.setenv("BINA_ENGINE_DB", str(tmp_path / "s.db"))
    else:
        monkeypatch.delenv("BINA_ENGINE", raising=False)
    import app.engine.config as cfg
    importlib.reload(cfg)
    import app.agents.revit.copilot.tools as tools
    importlib.reload(tools)
    return tools

def test_cloud_mode_tools_stay_external(monkeypatch, tmp_path):
    tools = _import_tools(monkeypatch, tmp_path, engine=False)
    fn = tools.list_wall_types
    assert getattr(fn, "external_execution", None) is True

def test_engine_mode_tools_are_plain(monkeypatch, tmp_path):
    tools = _import_tools(monkeypatch, tmp_path, engine=True)
    fn = tools.list_wall_types
    assert getattr(fn, "external_execution", None) in (False, None)

def test_engine_mode_body_dispatches_to_executor(monkeypatch, tmp_path):
    tools = _import_tools(monkeypatch, tmp_path, engine=True)
    calls = {}

    async def fake_call_tool(tool, args, tool_call_id=None, _transport=None):
        calls["tool"] = tool
        calls["args"] = args
        return {"ok": True, "echo": True}

    monkeypatch.setattr(tools, "_engine_call", fake_call_tool)
    import asyncio
    result = asyncio.get_event_loop().run_until_complete(
        tools._mutate("isolate_elements", {"element_ids": [1, 2]})
    )
    assert result == {"ok": True, "echo": True}
    assert calls["tool"] == "isolate_elements"

def test_cloud_mode_mutate_still_fails_loud(monkeypatch, tmp_path):
    tools = _import_tools(monkeypatch, tmp_path, engine=False)
    import asyncio, pytest
    with pytest.raises(RuntimeError):
        asyncio.get_event_loop().run_until_complete(tools._mutate("x", {}))
```

Adjust the two `asyncio.get_event_loop().run_until_complete` calls to the repo's existing async-test idiom (`pytest.mark.anyio` + `await`) if cleaner — behavior asserted is what matters. NOTE: if cloud-mode `_mutate` currently returns/raises differently (read the real body first), match the real current behavior in `test_cloud_mode_mutate_still_fails_loud` instead of inventing one.

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/test_engine_tool_transport.py -v`
Expected: FAIL (no `_engine_call`, decorator still hard-coded True)

- [ ] **Step 3: Implement the seam**

3a. Near the top of `tools.py` (after existing imports):

```python
from functools import partial

from app.engine.config import engine_enabled

_ENGINE = engine_enabled()

# Engine mode: tools are plain callables, agno executes the body, the body
# POSTs the addin's local tool server. Cloud mode: unchanged — agno pauses
# the run and the addin executes via the resume loop; bodies never run.
_revit_tool = partial(
    tool,
    external_execution=not _ENGINE,
    external_execution_silent=not _ENGINE,
)

if _ENGINE:
    from app.engine.executor import call_tool as _engine_call
else:
    _engine_call = None  # cloud: never used; _mutate/_read fail loud if hit
```

3b. Mechanical replace across the file (verify count ~50 before/after):

```bash
grep -c "@tool(external_execution=True, external_execution_silent=True)" app/agents/revit/copilot/tools.py
sed -i '' 's/@tool(external_execution=True, external_execution_silent=True)/@_revit_tool/g' app/agents/revit/copilot/tools.py
```

If any decorator lines have extra params or different formatting, fix those by hand — the grep count before and `grep -c "@_revit_tool"` after must match.

3c. Rewrite the two dispatch helpers (keep their docstrings, amend them):

```python
async def _mutate(tool: str, args: dict[str, Any]) -> dict[str, Any]:
    """Tool dispatch. ENGINE mode: executes synchronously against the addin's
    local tool server (POST /mcp/tools/{name}) and returns the real result.
    CLOUD mode: never executed — agno pauses the run instead (external
    execution); if this body runs in cloud mode external_execution was
    misconfigured, so fail loud."""
    if _ENGINE:
        return await _engine_call(tool, args)
    raise RuntimeError(
        f"_mutate({tool}) body executed in cloud mode — external_execution misconfigured"
    )
```

Apply the same pattern to `_read` (it takes just the tool name today — keep its signature: `if _ENGINE: return await _engine_call(tool, {})`). Preserve whatever the current cloud-mode body does if it differs from `raise` (read it first; the docstring says fail loud).

- [ ] **Step 4: Run the new tests AND the existing tool tests**

Run: `uv run pytest tests/test_engine_tool_transport.py -v`
Expected: 4 PASS
Run the existing DB-free tool tests to prove cloud mode unchanged (pick the files that exist on this branch): `uv run pytest tests/test_model_sight_tools.py tests/test_sight_enforcement.py -v 2>/dev/null || uv run pytest tests/ -k "tool" --co -q | head` — run whichever tool test files collect without DB. Expected: all previously-green files still green.

- [ ] **Step 5: Stage/commit**

```bash
git add app/agents/revit/copilot/tools.py tests/test_engine_tool_transport.py
git commit -m "feat(engine): tools execute synchronously via local executor in engine mode"
```

### Task 4: Engine session db — import-safe without Postgres

**Files:**
- Modify: `app/models/factory.py` (add `get_engine_db()`; do NOT touch `get_agent_db()`)
- Modify: `app/agents/revit/revit_ai.py:172-174` (the `_db = get_agent_db()` import-time call)
- Test: `tests/test_engine_db.py`

**Interfaces:**
- Produces: `get_engine_db() -> SqliteDb` in factory; `revit_ai` imports cleanly with `BINA_ENGINE=1` and no `DATABASE_URL`.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_engine_db.py
import importlib
import sys

def test_engine_db_is_sqlite(monkeypatch, tmp_path):
    monkeypatch.setenv("BINA_ENGINE", "1")
    monkeypatch.setenv("BINA_ENGINE_SECRET", "s")
    monkeypatch.setenv("BINA_ENGINE_DB", str(tmp_path / "sessions.db"))
    import app.engine.config as cfg
    importlib.reload(cfg)
    from app.models import factory
    importlib.reload(factory)
    db = factory.get_engine_db()
    from agno.db.sqlite import SqliteDb
    assert isinstance(db, SqliteDb)
    assert (tmp_path / "sessions.db").parent.exists()  # parent dir auto-created

def test_revit_ai_imports_without_database_url(monkeypatch, tmp_path):
    monkeypatch.setenv("BINA_ENGINE", "1")
    monkeypatch.setenv("BINA_ENGINE_SECRET", "s")
    monkeypatch.setenv("BINA_ENGINE_DB", str(tmp_path / "sessions.db"))
    monkeypatch.delenv("DATABASE_URL", raising=False)
    for mod in list(sys.modules):
        if mod.startswith("app.agents.revit.revit_ai") or mod == "app.models.factory":
            del sys.modules[mod]
    import app.engine.config as cfg
    importlib.reload(cfg)
    import app.agents.revit.revit_ai as revit_ai  # must not raise
    assert revit_ai._db is not None
```

CAVEAT for implementer: `revit_ai.py` import pulls model/provider config too — if import fails on a missing model env var (not DB), set the minimal extra env the factory needs in the test (mirror whatever `tests/test_model_factory.py` sets). Do not weaken the assertion that no `DATABASE_URL` is set.

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/test_engine_db.py -v`
Expected: test 1 FAIL (`get_engine_db` missing); test 2 FAIL (RuntimeError from `get_agent_db()` — the documented guard)

- [ ] **Step 3: Implement**

In `app/models/factory.py`, add (near `get_agent_db`, leaving it untouched):

```python
def get_engine_db():
    """Session db for the LOCAL engine process (BINA Copilot Engine).

    Single-process, single-user desktop app -> SQLite is the correct tier.
    This deliberately does NOT relax get_agent_db()'s Postgres guard: cloud
    deployments still hard-require DATABASE_URL=postgres (the SQLite fallback
    there broke cross-instance /tool/resume — ClickUp 86ey3q5vg). The engine
    has no cross-instance problem: there is exactly one process.
    """
    from agno.db.sqlite import SqliteDb

    from app.engine.config import get_engine_config

    db_path = get_engine_config().db_path
    db_path.parent.mkdir(parents=True, exist_ok=True)
    return SqliteDb(db_file=str(db_path))
```

In `app/agents/revit/revit_ai.py`, replace the two lines at `:172-174`:

```python
from app.models.factory import get_agent_db

_db = get_agent_db()
```

with:

```python
from app.engine.config import engine_enabled
from app.models.factory import get_agent_db, get_engine_db

# Engine (local desktop) -> SQLite; cloud -> Postgres (guarded, unchanged).
_db = get_engine_db() if engine_enabled() else get_agent_db()
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/test_engine_db.py tests/test_model_factory.py -v`
Expected: new tests PASS; `test_model_factory.py` (the factory's existing suite) still green.

- [ ] **Step 5: Stage/commit**

```bash
git add app/models/factory.py app/agents/revit/revit_ai.py tests/test_engine_db.py
git commit -m "feat(engine): SQLite session db via lean seam; Postgres guard untouched"
```

### Task 5: Engine entrypoint app

**Files:**
- Create: `app/engine/main.py`
- Test: `tests/test_engine_app.py`

**Interfaces:**
- Consumes: everything above; existing routers `app/routers/revit_turn` (mounted as-is).
- Produces: `create_engine_app() -> FastAPI` and module-level `app` — `/health` returns `{"status":"ok","engine":true,"version":...}`; the revit-turn routes work against the engine app. Run command: `BINA_ENGINE=1 uv run uvicorn app.engine.main:app --host 127.0.0.1 --port 48810`.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_engine_app.py
import importlib
import sys

import pytest
from fastapi.testclient import TestClient

@pytest.fixture()
def engine_app(monkeypatch, tmp_path):
    monkeypatch.setenv("BINA_ENGINE", "1")
    monkeypatch.setenv("BINA_ENGINE_SECRET", "s")
    monkeypatch.setenv("BINA_ENGINE_DB", str(tmp_path / "s.db"))
    monkeypatch.delenv("DATABASE_URL", raising=False)
    for mod in [m for m in sys.modules if m.startswith("app.")]:
        del sys.modules[mod]
    import app.engine.main as engine_main
    return engine_main.create_engine_app()

def test_health(engine_app):
    client = TestClient(engine_app)
    r = client.get("/health")
    assert r.status_code == 200
    body = r.json()
    assert body["engine"] is True

def test_revit_turn_routes_mounted(engine_app):
    client = TestClient(engine_app)
    # route exists (405/422 acceptable for wrong-method/empty-body probe;
    # 404 means the router is NOT mounted)
    r = client.post("/agents/revit-ai/tool/generate", json={})
    assert r.status_code != 404
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/test_engine_app.py -v`
Expected: FAIL — `ModuleNotFoundError: app.engine.main`

- [ ] **Step 3: Implement**

```python
# app/engine/main.py
"""BINA Copilot Engine — local desktop entrypoint (lean composition root).

Composes ONLY what the tool loop needs. Never imports app.main (the cloud
composition root): no credits/auth routers, no JKR agents, no AgentOS —
and therefore no import-time Postgres requirement.

Run:  BINA_ENGINE=1 uv run uvicorn app.engine.main:app --host 127.0.0.1 --port 48810
"""
from fastapi import FastAPI

from app.engine.config import engine_enabled, get_engine_config


def create_engine_app() -> FastAPI:
    if not engine_enabled():
        raise RuntimeError("Engine entrypoint requires BINA_ENGINE=1")
    get_engine_config()  # fail loud early on missing secret

    # Imported here (not module top) so the flag is checked first and the
    # revit agent module composes itself in engine mode.
    from app.routers.revit_turn.router import get_revit_turn_router

    app = FastAPI(title="BINA Copilot Engine", docs_url=None, redoc_url=None)
    app.include_router(get_revit_turn_router())

    @app.get("/health")
    def health() -> dict:
        return {"status": "ok", "engine": True}

    return app


app = create_engine_app() if engine_enabled() else None
```

IMPLEMENTER NOTE: verify the actual router factory name/prefix in `app/routers/revit_turn/router.py` (repo convention is `get_<domain>_router`) and how `app/main.py` mounts it (any prefix arg) — mirror exactly, so the addin's existing `AiUrl.Build` paths (`/agents/revit-ai/tool/generate` etc.) resolve identically. If `revit_feedback` router is needed for `/outcome` posts from the pane, mount it too — check which endpoints `ToolLoopService`/`RevitChatRouter` actually call and mount exactly those routers, nothing more.

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/test_engine_app.py -v`
Expected: 2 PASS

- [ ] **Step 5: Stage/commit**

```bash
git add app/engine/main.py tests/test_engine_app.py
git commit -m "feat(engine): lean FastAPI entrypoint — boots without Postgres"
```

### Task 6: Local-mode turn flow — no `awaiting_revit`, tool frames stream

**Files:**
- Modify: `app/services/revit_turn.py` (`_emit_pending_or_done`, locate by symbol) — assertion + local-mode reduction only
- Test: `tests/test_engine_turn_flow.py`

**Interfaces:**
- Consumes: engine mode from Tasks 1–5.
- Produces: in engine mode a turn NEVER returns `status:"awaiting_revit"` (tools ran inline); `awaiting_user_input` (clarify) still passes through; SSE stream carries tool events for inline tools.

Background: in engine mode agno executes tools inline during `arun`, so `run.is_paused` for external tools should be structurally impossible — but `_emit_pending_or_done` still contains that branch. Make the invariant explicit and loud rather than silently dead.

- [ ] **Step 1: Write the failing test**

```python
# tests/test_engine_turn_flow.py
import importlib
import sys
import types

import pytest

@pytest.fixture()
def engine_env(monkeypatch, tmp_path):
    monkeypatch.setenv("BINA_ENGINE", "1")
    monkeypatch.setenv("BINA_ENGINE_SECRET", "s")
    monkeypatch.setenv("BINA_ENGINE_DB", str(tmp_path / "s.db"))
    for mod in [m for m in sys.modules if m.startswith("app.")]:
        del sys.modules[mod]
    import app.services.revit_turn as rt
    return rt

def test_awaiting_revit_impossible_in_engine_mode(engine_env):
    rt = engine_env
    # a paused run with external tools reaching the emitter in engine mode
    # is a misconfiguration — the service must raise, not park the run
    fake_run = types.SimpleNamespace(
        is_paused=True,
        tools_awaiting_external_execution=[types.SimpleNamespace(tool_name="place_door")],
        tools_requiring_user_input=[],
    )
    with pytest.raises(RuntimeError, match="engine mode"):
        rt.assert_no_external_pause(fake_run)

def test_clarify_pause_still_allowed(engine_env):
    rt = engine_env
    fake_run = types.SimpleNamespace(
        is_paused=True,
        tools_awaiting_external_execution=[],
        tools_requiring_user_input=[types.SimpleNamespace(tool_name="get_user_input")],
    )
    rt.assert_no_external_pause(fake_run)  # must NOT raise
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/test_engine_turn_flow.py -v`
Expected: FAIL — `assert_no_external_pause` doesn't exist

- [ ] **Step 3: Implement**

In `app/services/revit_turn.py` add near `_emit_pending_or_done`:

```python
from app.engine.config import engine_enabled


def assert_no_external_pause(run) -> None:
    """Engine-mode invariant: tools execute inline, so a run paused on
    external execution means the transport seam is misconfigured (a tool
    kept external_execution=True). Fail loud — a parked run on a desktop
    with no resume path is the old stuck-run bug reborn."""
    if not engine_enabled():
        return
    pending = getattr(run, "tools_awaiting_external_execution", None) or []
    if getattr(run, "is_paused", False) and pending:
        names = ", ".join(getattr(t, "tool_name", "?") for t in pending)
        raise RuntimeError(
            f"engine mode: run paused awaiting external execution ({names}) — "
            "external_execution should be off for all Revit tools in engine mode"
        )
```

Then call `assert_no_external_pause(run)` at the top of `_emit_pending_or_done` (both the blocking and streaming paths if they emit separately — find every place that builds `status:"awaiting_revit"` and guard it).

Also in this task, VERIFY (and fix if needed) stream shaping for inline tools: run the app manually (Step 4b) and confirm the SSE stream emits tool events for inline-executed tools (agno emits ToolCallStarted/Completed run events; the existing stream reducer already translates run events into `tool` frames for VOLATILE INSPECT tools — inline external tools take the same path once external_execution is off). If frames are missing, extend the reducer where it filters event types — keep frame shape identical to today's (`tool` frames keyed so the pane's `ProgressReducer` continues to work); the addin parser must need zero changes.

- [ ] **Step 4a: Run test to verify it passes**

Run: `uv run pytest tests/test_engine_turn_flow.py -v`
Expected: 2 PASS

- [ ] **Step 4b: Manual smoke — real engine boot + fake addin**

```bash
# terminal 1 — fake addin tool server
python3 - <<'EOF'
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
class H(BaseHTTPRequestHandler):
    def do_POST(self):
        n = self.path.rsplit('/', 1)[-1]
        body = {"ok": True, "tool": n, "fake": True,
                "levels": [{"name": "Level 1"}]}
        data = json.dumps(body).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json")
        self.end_headers(); self.wfile.write(data)
HTTPServer(("127.0.0.1", 48820), H).serve_forever()
EOF

# terminal 2 — engine (staging env for model keys + RAG; session db local)
BINA_ENGINE=1 BINA_ENGINE_SECRET=dev ENVIRONMENT=staging \
  uv run uvicorn app.engine.main:app --host 127.0.0.1 --port 48810

# terminal 3 — one real turn (real LLM, fake Revit)
curl -sN -X POST http://127.0.0.1:48810/agents/revit-ai/tool/generate/stream \
  -H 'Content-Type: application/json' \
  -d '{"prompt":"senaraikan semua wall types","context":{"projectName":"smoke","levels":["Level 1"]},"user_id":1}'
```

Expected: stream shows `tool` frame(s) for `list_wall_types` executed INLINE (no `awaiting_revit` anywhere), then a final `done`/reply. Paste the observed frame sequence into the task notes. (Schema reminder from past smokes: `levels` = list[str], `user_id` = int; use `127.0.0.1`, not `localhost` — OrbStack hijacks `localhost` on this dev Mac.)

- [ ] **Step 5: Stage/commit**

```bash
git add app/services/revit_turn.py tests/test_engine_turn_flow.py
git commit -m "feat(engine): forbid awaiting_revit in engine mode; inline tool frames verified"
```

---

## Repo B — revit-addin-sync (branch `feat/copilot-engine`) — code-only on macOS, Windows build gate applies

### Task 7: Engine config in `BinaConfig` + gated startup

**Files:**
- Modify: `BinaConfig.cs` (add properties + config.json load; follow the exact pattern of existing entries like `AllowNgrokAIBaseUrl` at `:38` and the `LoadConfigJson` block at `:167-188`)
- Modify: `App.cs:281-309` (the transport gate + McpServer start)

**Interfaces:**
- Produces: `BinaConfig.EngineMode: bool` (default false), `BinaConfig.EnginePort: int` (default 48820), `BinaConfig.EngineSecret: string` — all readable from `%APPDATA%\RevitWebAppSync\config.json` keys `engineMode`, `enginePort`, `engineSecret`. Task 8 consumes all three.

- [ ] **Step 1: Add the three settings to `BinaConfig`**

Follow the existing property + json-load pattern (each existing config key has: a static property with default, a parse line in the config.json loader, optional env override). Add:

```csharp
/// <summary>BINA Copilot Engine mode: the agent loop runs as a local
/// process and calls back into this add-in's local tool server. Off by
/// default — cloud ping-pong transport unchanged.</summary>
public static bool EngineMode { get; private set; } = false;

/// <summary>Port this add-in's local tool server listens on in Engine
/// mode. HttpListener prefix stays "localhost" (non-admin URL ACL rule).</summary>
public static int EnginePort { get; private set; } = 48820;

/// <summary>Shared loopback secret; every /mcp/tools request must carry it
/// in X-Bina-Secret. Interim channel (Phases 1-3): both processes read the
/// same config.json. Phase 4 replaces with a per-boot spawn secret.</summary>
public static string EngineSecret { get; private set; } = "";
```

and in the config.json loader: parse `engineMode` (bool), `enginePort` (int), `engineSecret` (string) with the same TryGet pattern the file already uses.

- [ ] **Step 2: Re-gate McpServer startup in `App.cs`**

Replace the `BINA_VIBE_TOOLPATH=1` env gate (currently forcing `transport="off"`, `App.cs:281-294`) with:

```csharp
// Engine mode: start the local tool server for the BINA Copilot Engine.
// The old BINA_VIBE_TOOLPATH env gate is retired; EngineMode comes from
// config.json. The WSS tunnel client is NOT started in engine mode.
if (BinaConfig.EngineMode)
{
    if (string.IsNullOrWhiteSpace(BinaConfig.EngineSecret))
    {
        Log.Warn("EngineMode on but engineSecret missing — tool server NOT started.");
    }
    else
    {
        StartMcpServer(BinaConfig.EnginePort);   // existing start path, App.cs:296-309
    }
}
```

Keep the tunnel client code path unreachable in engine mode (do not delete `McpTunnelClient` in this task — deletion is a separate cleanup once engine mode is proven; keeping the diff reviewable).

Match the file's actual local style: logger name, how the existing start block passes the port (today the port comes from `BINA_VIBE_MCP_PORT` env inside McpServer — change McpServer's constructor to accept the port, see Task 8).

- [ ] **Step 3: Self-review the diff**

`git diff` — check: defaults preserve current behavior (EngineMode false ⇒ identical to today), no dead usings, XML doc comments on public members, nothing else in App.cs touched.

- [ ] **Step 4: Stage/commit**

```bash
git add BinaConfig.cs App.cs
git commit -m "feat(engine): EngineMode config + gated local tool server startup"
```

### Task 8: Repair `McpServer` — shared pump, new wire, secret, idempotency, 50s

**Files:**
- Modify: `BinaVibe/Mcp/McpServer.cs` (whole request path)
- Modify: `BinaVibe/Mcp/McpJobPump.cs` only if `Enqueue` needs an overload (check first — it shouldn't)
- Reference: `BinaVibe/Mcp/McpIdempotencyCache.cs` (existing class, today only constructed in `McpTunnelClient.cs:38`)

**Interfaces:**
- Consumes: `BinaConfig.EngineSecret`, `EnginePort` (Task 7); `McpJobPump.Enqueue(McpJob)` (existing, `McpJobPump.cs:82-95`); `McpIdempotencyCache` (existing).
- Produces: `POST http://localhost:48820/mcp/tools/{name}` accepting body `{"tool_call_id":"…","args":{…}}` with headers `X-Bina-Secret` (401 if wrong/missing) and `Idempotency-Key` (duplicate key ⇒ cached result, no re-execution). 200 + tool JSON on success; 408-style typed JSON on the 6s busy watchdog; 504 typed JSON at 50s.

The three defects being fixed (from the adversarial review): (1) `McpServer.cs:45-47` constructs a PRIVATE `McpExternalEventHandler` and enqueues to `_handler.Pending` (`:117-120`) — a queue nothing drains since the pump refactor (the pump drains `McpToolHandler` wired at `App.cs:193-197`); every request waits the full timeout. (2) `_jobTimeout` is 600s (`:34-36`). (3) No secret, no idempotency, body parsed as bare args.

- [ ] **Step 1: Rewire the request path**

In `McpServer.cs`:

```csharp
// constructor: take the port; DELETE the private handler entirely
public McpServer(int port)
{
    Port = port;
    _idempotency = new McpIdempotencyCache();   // moved here from McpTunnelClient
}

private static readonly TimeSpan JobWait = TimeSpan.FromSeconds(50);

private async Task HandleRequest(HttpListenerContext ctx)
{
    // 1. secret gate — cheap reject before any parsing
    var secret = ctx.Request.Headers["X-Bina-Secret"];
    if (string.IsNullOrEmpty(secret) || secret != BinaConfig.EngineSecret)
    {
        await WriteJson(ctx, 401, new { ok = false, error = "bad or missing X-Bina-Secret" });
        return;
    }

    var toolName = /* existing URL-segment extraction, unchanged */;

    // 2. new wire format: {"tool_call_id": "...", "args": {...}}
    using var doc = JsonDocument.Parse(await ReadBody(ctx));
    var root = doc.RootElement;
    var args = root.TryGetProperty("args", out var a) ? a.Clone()
             : root.Clone();   // tolerate legacy bare-args bodies

    // 3. idempotency: same key => same result, no double execution
    var idemKey = ctx.Request.Headers["Idempotency-Key"];
    if (!string.IsNullOrEmpty(idemKey) && _idempotency.TryGet(idemKey, out var cached))
    {
        await WriteJson(ctx, 200, cached);
        return;
    }

    // 4. THE FIX: shared pump (Idling drain + 6s busy watchdog), not the
    //    orphaned private queue this class used to enqueue into.
    var job = new McpJob(toolName, args);
    McpJobPump.Enqueue(job);

    if (!job.Completed.Wait(JobWait))
    {
        job.Abandoned = true;
        await WriteJson(ctx, 504, new { ok = false, error = $"tool {toolName} timed out after {JobWait.TotalSeconds:0}s" });
        return;
    }

    var result = job.Error != null
        ? (object)new { ok = false, error = job.Error }
        : job.Result;
    if (!string.IsNullOrEmpty(idemKey)) _idempotency.Put(idemKey, result);
    await WriteJson(ctx, job.Error != null ? 500 : 200, result);
}
```

IMPLEMENTER NOTES (verify against real code, don't trust this sketch blindly): (a) `McpJob`'s actual constructor/fields — reuse exactly what `ToolLoopRunner.ExecuteOneAsync` builds (`ToolLoopRunner.cs:270-311`), including `Abandoned` semantics; (b) `McpJobPump.Enqueue` may be instance or static — match; (c) `McpIdempotencyCache`'s real API (`TryGet`/`Put` names may differ — read the class); (d) keep `Prefix => $"http://localhost:{Port}/"` EXACTLY — localhost, not 127.0.0.1 (Windows non-admin URL ACL); (e) the 6s busy/modal watchdog lives in the pump and fires `job.Completed` with `job.Error` set — the wait above surfaces it as a typed error well before 50s; (f) update the class-header comment to describe the engine architecture.

- [ ] **Step 2: Grep for leftovers**

```bash
grep -n "_handler" BinaVibe/Mcp/McpServer.cs        # expect: no hits
grep -n "McpIdempotencyCache" BinaVibe/Mcp/*.cs      # expect: McpServer.cs + McpIdempotencyCache.cs + (McpTunnelClient.cs unchanged)
grep -n "600\|_jobTimeout" BinaVibe/Mcp/McpServer.cs # expect: no hits (JobWait=50s replaced it)
```

- [ ] **Step 3: Self-review the diff**

`git diff BinaVibe/Mcp/McpServer.cs` — checklist: private handler gone; every response is JSON with `ok`; 401 path doesn't leak the expected secret; legacy bare-args tolerated; XML docs updated; stale "shares the same queue" comment in `McpExternalEventHandler.cs:8-9` corrected while you're here (one-line comment fix, include in this commit).

- [ ] **Step 4: Stage/commit**

```bash
git add BinaVibe/Mcp/McpServer.cs BinaVibe/Mcp/McpExternalEventHandler.cs
git commit -m "fix(engine): McpServer routes through shared pump — orphaned queue deleted; secret+idempotency+50s"
```

### Task 9: Windows verification runbook (no code)

**Files:**
- Create: `docs/engine-phase1-uat.md`

**Interfaces:** consumes everything above; produces the go/no-go evidence for the Phase 1 gate ("UAT parity with today, faster").

- [ ] **Step 1: Write the runbook** — exact contents:

```markdown
# Engine Phase 1 — Windows UAT runbook

## Build & install
1. Windows machine with Revit 2026: pull `feat/copilot-engine`, build
   (existing PostBuild copies into %APPDATA%\Autodesk\Revit\Addins\2026\).
2. bina-ai on the same machine: pull `feat/copilot-engine`,
   `uv sync`.

## Configure
3. %APPDATA%\RevitWebAppSync\config.json — add:
   { "engineMode": true, "enginePort": 48820, "engineSecret": "<random>",
     "AIBaseUrl": "http://localhost:48810" }
4. Start engine:
   set BINA_ENGINE=1 && set BINA_ENGINE_SECRET=<same random> && set ENVIRONMENT=staging
   uv run uvicorn app.engine.main:app --host 127.0.0.1 --port 48810
   (staging env = model keys + RAG against Azure; office IP must be allowed
   through the Azure PG firewall.)

## Verify transport (before any prompt)
5. Engine health: curl http://127.0.0.1:48810/health -> {"engine": true}
6. Addin tool server: curl -X POST http://localhost:48820/mcp/tools/list_levels
   -H "X-Bina-Secret: <random>" -H "Content-Type: application/json"
   -d "{\"tool_call_id\":\"t1\",\"args\":{}}"
   -> 200 + real levels from the open model. Wrong secret -> 401.
   Same request twice with the same Idempotency-Key header -> identical
   response, addin log shows ONE execution.
7. Modal-dialog check: open a Revit modal dialog, repeat step 6 ->
   typed error within ~6s (watchdog), NOT a hang.

## UAT prompts (same suite as cloud, in the pane)
8. "senaraikan semua wall types" — expect: answer with NO awaiting_revit
   round; pane step trail shows the tool executing.
9. "tukar 10 tandas cangkung kepada duduk" (the model-sight suite model) —
   expect parity-or-better vs cloud on the same model.
10. Clarify path: "letak toilet" with no position — expect
    awaiting_user_input chips exactly as today (clarify is unchanged).

## Record
- looks-per-turn and per-tool wall-clock from Langfuse for each prompt;
  compare against a cloud-mode run of the same prompts.
- Gate: parity on outcomes, strictly faster on tool legs, zero stuck turns.
```

- [ ] **Step 2: Stage/commit**

```bash
git add docs/engine-phase1-uat.md
git commit -m "docs(engine): Phase 1 Windows UAT runbook"
```

---

## Self-review (done at plan-writing time)

- **Spec coverage:** Phase 1 spec items → Task map: inverted transport (T2/T3), lean root + SQLite (T4/T5), no-awaiting_revit invariant + SSE (T6), McpServer repair + wire + secret + idempotency + 50s (T7/T8), localhost-prefix rule (T8 note d), clarify preserved (T6 test 2, T9 step 10). Deliberately deferred from Phase 1: deleting `/tool/resume` routes + `ToolLoopRunner` collapse (cloud path must keep working until engine is proven — the spec's deletion ledger executes after the Phase 1 gate, as its own cleanup task), gateway (`Phase 3`), packaging/spawn (`Phase 4`).
- **Placeholder scan:** implementer-verify notes are bounded (specific file + what to confirm), no TBDs.
- **Type consistency:** `call_tool` signature identical in T2 (def) and T3 (use); config field names identical T1→T2/T4/T5; wire format identical T2 (client) and T8 (server); `EngineSecret`/`EnginePort` identical T7 (def) and T8 (use).
