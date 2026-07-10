# BINA Copilot Engine — MCP facade (Phase 5)

The engine can expose its Revit tools as a standard **MCP server**, so any MCP
client (Claude Desktop, partner tools, other BINA products) can drive the
drafter's Revit through BINA's typed, transacted, billable tools — the same
tools the copilot pane uses, over the same safe path.

## What it is (and isn't)

- **Is:** an MCP door onto the engine's existing tool surface. Every tool
  (`query_geometry`, `find_elements_by_filter`, the mutators, …) is re-exported
  from the ONE source (`ALL_TOOLS`) so the MCP door and the agent can never
  drift. Each call routes through the Phase-1 executor to the add-in's
  `ToolRegistry` — so it has exactly the safety/transaction/billing properties
  of the pane.
- **Isn't:** a raw-code-execution endpoint. There is deliberately no
  `execute_revit_code`-style tool (revit-mcp-python's own docs call theirs
  draft / no-auth). Typed tools only. A read-only sandboxed query escape hatch,
  if ever wanted, is a separate spec.

## Enabling it

Always on — the engine mounts the MCP server unconditionally at startup
(no flag; a facade build failure degrades to engine-without-MCP):

```
BINA_ENGINE=1 BINA_ENGINE_SECRET=<secret> \
  uv run uvicorn app.engine.main:app --host 127.0.0.1 --port 48810
```

Or use the helper (reads GLM_API_KEY from .env, preflights the add-in,
optional -Ngrok prints the claude.ai connector URL):

```
pwsh scripts/start-engine.ps1 -Secret <secret> -Ngrok
```

The MCP server mounts at **`http://127.0.0.1:48810/mcp`** (streamable-http).

## Pointing a client at it

Configure the MCP client with the engine URL. Because the engine is
loopback-only, the client must run on the same machine (or tunnel to it
deliberately). Every tool's arguments are documented in its description
(the parameter JSON schema is embedded there); call a tool by name with its
`args` object, e.g. `query_geometry` with `{"element_ids": [12345]}`.

## Security

- Loopback only (`127.0.0.1`) — never bind a public interface.
- The tool calls still flow through the add-in's secret-gated local tool server
  (`X-Bina-Secret`), so a tool invocation ultimately requires the shared
  secret the engine holds.
- Same billing path as the pane: inference (if the client also uses the
  gateway model) is metered; tool execution is free/local.

## Status

Manifest + facade + flagged mount are implemented and unit-tested
(`tests/test_tool_manifest.py`, `test_mcp_facade.py`, `test_engine_mcp_mount.py`).
The streamable-http lifespan wiring should be validated with a real MCP client
before production use — mount it, connect Claude Desktop, list tools, invoke
`query_geometry` on a real element.
