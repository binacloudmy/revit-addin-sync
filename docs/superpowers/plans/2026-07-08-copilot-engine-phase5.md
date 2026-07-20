# BINA Copilot Engine — Phase 5 (MCP facade) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: subagent-driven-development or executing-plans. Checkbox steps.

**Goal:** Expose the engine's tool surface as a standard MCP server, so any MCP client (Claude Desktop, future BINA products, partner tools) can drive the drafter's Revit through BINA's typed, transacted, billable tools — interop as a product feature, zero effect on the pane.

**Architecture:** A FastMCP server inside the engine process that re-exports the same tools the agent already calls: each maps to an `executor.call(name, args)` over the local `127.0.0.1:48820` tool server (the exact Phase-1 transport). Optional resource subscription on document-change later. Off the critical path — the pane keeps using the turn API; MCP is an additional door onto the same room.

**Tech Stack:** FastMCP / agno (bina-ai). Depends on Phases 1-3. Spec: Phase 5 section of the colocate design.

## Global Constraints

- Branch `feat/copilot-engine`. Stage-only. Cloud unaffected — the MCP facade runs only in the engine process.
- Reuse the Phase-1 `executor.call` transport and the SAME tool names/schemas the agent uses (`query_geometry`, `find_elements_by_filter`, the mutators, …). Do NOT fork tool definitions.
- **No raw code execution.** Do NOT expose an `execute_revit_code`-style tool (revit-mcp-python's own docs call theirs draft/no-auth). Typed tools only. A read-only sandboxed query escape hatch, if ever wanted, is a separate spec.
- Secret-gated like the tool server (the MCP door needs the same `X-Bina-Secret` / per-boot secret).

---

### Task 1: Tool manifest — one source, two consumers

**Files:** Create `app/engine/tool_manifest.py`; test `tests/test_tool_manifest.py`.

**Interfaces:** Produces `TOOL_MANIFEST: list[{name, description, parameters_schema}]` derived from the SAME agno tools the agent uses (`INSPECT_TOOLS + MUTATE_TOOLS`), so the MCP facade and the agent never drift.

- [ ] Failing test: manifest includes `query_geometry` and a known mutator; each entry has `name`, `description`, non-empty `parameters_schema`; the set equals the agent's `ALL_TOOLS` names.
- [ ] Implement: introspect the agno tool objects (name, docstring, JSON schema from the signature) into the manifest. One derivation, no hand-written duplication.
- [ ] Pass, stage.

### Task 2: FastMCP server exposing the manifest

**Files:** Create `app/engine/mcp_facade.py`; test `tests/test_mcp_facade.py`.

**Interfaces:** `build_mcp_server() -> FastMCP` — each manifest entry registered as an MCP tool whose handler calls `executor.call(name, args)` and returns the result; secret-gated.

- [ ] Failing test: the built server lists the manifest tools; invoking one routes to a patched `executor.call` with the right name/args and returns its result; a call without the secret is rejected.
- [ ] Implement: loop the manifest, register each as a FastMCP tool with a thin handler delegating to `executor.call`. Wire the secret check. Do NOT re-implement any tool logic — it all goes through the executor to the addin.
- [ ] Pass, stage.

### Task 3: Mount the facade in the engine (optional endpoint)

**Files:** Modify `app/engine/main.py` (mount the MCP server on a sub-path, e.g. `/mcp`, gated by a `BINA_ENGINE_MCP=1` flag so it's opt-in); test `tests/test_engine_mcp_mount.py`.

- [ ] Failing test: with the flag on, the engine app serves the MCP endpoint; with it off, it does not (default off — the pane path never needs it).
- [ ] Implement the flagged mount. Pass, stage.

### Task 4: Docs

**Files:** Create `docs/engine-mcp.md` — how to point an MCP client at the engine (URL, secret header), the tool list, the "typed tools only, no raw code exec" boundary, and the security note (loopback + secret).

- [ ] Write, stage.

## Self-review
- Coverage: manifest from the single tool source (T1) → FastMCP facade over the executor (T2) → opt-in mount (T3) → docs (T4). No tool-def fork, no raw code exec, secret-gated, off by default.
- This is the interop cherry on top — everything routes through the Phase-1 executor and the addin's typed ToolRegistry, so the MCP door has exactly the safety/billing properties of the pane.
