# BINA Copilot Engine — Phase 2 (give it eyes: query_geometry + scene digest) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the copilot actually *see* geometry — one generic `query_geometry` read tool (xyz, facing, host, room, bbox, nearest-walls, clash) plus a per-turn scene digest of the working set — so on the cheap Phase-1 transport the agent can act → read real geometry → verify against intent → fix.

**Architecture:** Phase 1 made looking cheap; Phase 2 gives it something worth looking at. In **bina-ai**: a `query_geometry` tool (same engine/cloud transport seam as every other tool) + scene-digest fields in the per-turn context shaping + loop recipes that teach act→verify→fix. In **revit-addin-sync**: a `QueryGeometry.cs` inspector built on the salvaged `PlacementFacts` helper, wired into `ToolRegistry`, plus placement facts folded into the `BuildContext` scene digest. **We salvage the DATA (placement-facts contract + the PlacementFacts C# helper) from the dead model-sight branch; we do NOT revive its solver zoo (RoomSolver/place_in_room/ResolveFacing).**

**Tech Stack:** Python 3.12 / agno / FastAPI (bina-ai); C# .NET 8 Revit API (revit-addin-sync). Depends on Phase 1 (`feat/copilot-engine`, already staged). Spec: `docs/superpowers/specs/2026-07-08-copilot-engine-colocate-design.md` (Phase 2 section).

## Global Constraints

- Branch: `feat/copilot-engine` (both repos) — Phase 2 continues on the same branch as Phase 1.
- The placement-facts contract is authoritative and already recovered to `bina-ai/docs/contracts/placement-facts.md` (Task 1). Fields, verbatim: `xyz [x,y,z] feet` · `rotation_deg float CCW-from-+X` · `bbox [[x1,y1,z1],[x2,y2,z2]] feet` · `host_id int|null` · `room str|null` · `level str|null` · `facing [x,y] unit|null`. Missing/inapplicable → **null, never omitted, never fabricated**. Units feet + degrees.
- `query_geometry` is a **read** (no Transaction). It uses the SAME transport seam as Phase 1: `@_revit_tool` decorator, body dispatches via `_read`/executor in engine mode, `external_execution` in cloud mode. Do NOT special-case it.
- Do NOT revive from the dead branch: `RoomSolver.cs`, `ResolveFacing`, `place_in_room`, `place_relative`, `get_room_geometry`, `get_elements_near`, `look_at`. Phase 2 is ONE generic read tool, not the solver zoo.
- Salvage sources (read via `git show`, do not merge the branch):
  - C# `PlacementFacts(Document, Element)` helper: `git show feat/model-sight-phase-1-2:BinaVibe/Mcp/Tools/Inspectors.cs` (self-contained; computes xyz/rotation_deg/bbox/host_id/room/level/facing).
  - Contract doc: `git show c03c62e:docs/contracts/placement-facts.md` (in bina-ai).
- bina-ai tests: run ONLY the files you create/touch (`uv run pytest tests/<file> -v`). Never the full suite (hangs on staging Postgres).
- revit-addin-sync does NOT compile on macOS. C# tasks end at "staged + self-reviewed"; Windows build + Revit UAT is the documented follow-up.
- Stage-only: `git add`, NO commits (user commits himself).
- Cloud behavior byte-identical when `BINA_ENGINE` unset.

---

## Repo A — bina-ai (Python, testable now)

### Task 1: Recover the contract + add the `query_geometry` tool

**Files:**
- Create: `docs/contracts/placement-facts.md` (recover from `c03c62e`)
- Modify: `app/agents/revit/copilot/tools.py` (add `query_geometry`, register in the tool list)
- Test: `tests/test_query_geometry_tool.py`

**Interfaces:**
- Produces: an agno tool `query_geometry(element_ids: list[int], aspects: list[str] | None = None) -> dict` that in engine mode dispatches `_read`/executor for the `query_geometry` tool name, and in cloud mode is `external_execution`. Later tasks and the addin rely on the tool NAME `query_geometry` and the arg shape `{element_ids, aspects?}`.

- [ ] **Step 1: Recover the contract doc**

Run: `git show c03c62e:docs/contracts/placement-facts.md > docs/contracts/placement-facts.md`
Verify it contains the 7-field table (`xyz`, `rotation_deg`, `bbox`, `host_id`, `room`, `level`, `facing`).

- [ ] **Step 2: Write the failing test** (`tests/test_query_geometry_tool.py`):

```python
"""query_geometry: generic geometry read, same transport seam as other tools."""
import importlib


def _tools(monkeypatch, tmp_path, engine: bool):
    if engine:
        monkeypatch.setenv("BINA_ENGINE", "1")
        monkeypatch.setenv("BINA_ENGINE_SECRET", "s")
        monkeypatch.setenv("BINA_ENGINE_DB", str(tmp_path / "s.db"))
    else:
        monkeypatch.delenv("BINA_ENGINE", raising=False)
    import app.engine.config as cfg
    importlib.reload(cfg)
    import app.agents.revit.copilot.tools as tools
    importlib.reload(tools)
    return tools


def test_query_geometry_registered(monkeypatch, tmp_path):
    tools = _tools(monkeypatch, tmp_path, engine=False)
    assert hasattr(tools, "query_geometry")
    # exported in the agent's tool list
    assert tools.query_geometry in tools.ALL_TOOLS


def test_cloud_mode_query_geometry_is_external(monkeypatch, tmp_path):
    tools = _tools(monkeypatch, tmp_path, engine=False)
    assert getattr(tools.query_geometry, "external_execution", None) is True


def test_engine_mode_query_geometry_dispatches(monkeypatch, tmp_path):
    tools = _tools(monkeypatch, tmp_path, engine=True)
    calls = {}

    async def fake_call(tool, args, tool_call_id=None, _transport=None):
        calls["tool"] = tool
        calls["args"] = args
        return {"ok": True, "elements": []}

    monkeypatch.setattr(tools, "_engine_call", fake_call)
    import asyncio
    result = asyncio.run(tools._read("query_geometry", {"element_ids": [7]}))
    assert result == {"ok": True, "elements": []}
    assert calls["tool"] == "query_geometry"
```

CAVEAT: verify the real export list name in `tools.py` (it may be `ALL_TOOLS`, `REVIT_TOOLS`, or a list passed to the agent). Match the real one. Also confirm `_read`'s real signature (Phase 1 made it `_read(tool, args)` — verify it accepts args; if `_read` takes only a name, add `query_geometry` via `_mutate`-style dispatch or extend `_read` to accept optional args, matching the Phase 1 seam).

- [ ] **Step 3: Run to verify it fails**

Run: `uv run pytest tests/test_query_geometry_tool.py -v`
Expected: FAIL — `query_geometry` doesn't exist.

- [ ] **Step 4: Implement the tool** in `tools.py` (place with the other read tools, use the Phase-1 `@_revit_tool` decorator):

```python
@_revit_tool
async def query_geometry(element_ids: list[int], aspects: list[str] | None = None) -> dict[str, Any]:
    """Read REAL geometry for elements from the live model — the copilot's eyes.
    Returns, per element, the placement facts: ``xyz`` (feet), ``facing`` (unit
    vector, which way it points), ``host_id`` (the wall a door/window sits in),
    ``room`` (which room it's in), ``bbox``, ``rotation_deg``, ``level``.

    Use it to SEE before and after you act — never guess orientation or room
    from a family name. Optional ``aspects`` narrows/extends the read:
    ``"nearest_walls"`` (k nearest wall ids + inward normals), ``"clashes"``
    (overlapping element ids). Omit ``aspects`` for the plain placement facts.

    Get ``element_ids`` from find_elements_by_filter / get_current_selection.
    After a mutation (swap/move/place), call this on the changed ids and CHECK
    the result against what the user asked (right room? faces the door?) before
    reporting success."""
    args: dict[str, Any] = {"element_ids": element_ids}
    if aspects:
        args["aspects"] = aspects
    return await _read("query_geometry", args)
```

Then add `query_geometry` to the exported tool list (the same list the other read tools are in — grep for where `find_elements_by_filter` is listed and add it there).

- [ ] **Step 5: Run to verify it passes**

Run: `uv run pytest tests/test_query_geometry_tool.py -v`
Expected: 3 PASS.
Also run the Phase-1 transport test to confirm no seam regression: `uv run pytest tests/test_engine_tool_transport.py -v` → still green.

- [ ] **Step 6: Stage**

```bash
git add docs/contracts/placement-facts.md app/agents/revit/copilot/tools.py tests/test_query_geometry_tool.py
```

### Task 2: Scene digest — placement facts in the per-turn context

**Files:**
- Modify: `app/services/revit_response_shaping.py` (`_context_payload`, ~:171-233)
- Test: `tests/test_scene_digest.py`

**Interfaces:**
- Consumes: the per-turn `RevitModelContext` snapshot the addin pushes (Task 5 adds the facts add-in-side; until then the fields are absent and the digest must degrade to "not present", never error).
- Produces: when the snapshot carries a `sceneDigest` / `selection` with placement facts, `_context_payload` renders them under a `scene` key (working-set elements with `id`, `xyz`, `facing`, `room`, `host_id`); absent → key omitted, no error.

- [ ] **Step 1: Write the failing test** (`tests/test_scene_digest.py`):

```python
"""Scene digest: placement facts for the working set ride the per-turn context."""
from app.services.revit_response_shaping import _context_payload


def test_scene_digest_rendered_when_present():
    ctx = {
        "activeViewName": "Level 2",
        "sceneDigest": [
            {"id": 101, "xyz": [10.0, 5.0, 0.0], "facing": [0.0, 1.0],
             "room": "Tandas 1", "host_id": 55},
        ],
    }
    out = _context_payload(ctx)
    assert "scene" in out
    assert out["scene"][0]["id"] == 101
    assert out["scene"][0]["room"] == "Tandas 1"


def test_scene_digest_absent_is_safe():
    ctx = {"activeViewName": "Level 2"}
    out = _context_payload(ctx)
    assert "scene" not in out   # omitted, not errored
```

CAVEAT: read `_context_payload`'s real return shape first (it builds a dict of `active_view`, `levels_by_elevation`, etc.). Match its existing style (snake_case keys, caps). The camelCase→snake mapping: the addin pushes `sceneDigest` (camel), the alias generator or manual read converts — check how `selection`/`views` are read today and mirror it. Cap the scene list (e.g. 60) like the other lists.

- [ ] **Step 2-5:** run-fail → implement (add a `scene` block in `_context_payload` reading `sceneDigest`, cap 60, per-element `{id, xyz, facing, room, host_id}`, omit when absent) → run-pass → stage.

Run: `uv run pytest tests/test_scene_digest.py -v` (2 PASS), then `uv run pytest tests/test_engine_app.py -v` (no regression).

```bash
git add app/services/revit_response_shaping.py tests/test_scene_digest.py
```

### Task 3: Loop recipes — act → query_geometry → verify → fix

**Files:**
- Create: `app/knowledge/revit_recipes/verify_after_mutation.md`
- Test: `tests/test_loop_recipe_contract.py`

**Interfaces:** retrieval-served recipe text; re-ingest required to take effect.

- [ ] **Step 1: Write the failing test** (`tests/test_loop_recipe_contract.py`):

```python
"""The loop recipe teaches act -> query_geometry -> verify-from-intent -> fix."""
from pathlib import Path

RECIPE = Path("app/knowledge/revit_recipes/verify_after_mutation.md")


def test_recipe_teaches_the_loop():
    text = RECIPE.read_text()
    assert "query_geometry" in text
    assert "rotate_elements" in text          # the fix move for wrong facing
    for phrase in ["faces", "room", "before you report"]:
        assert phrase in text.lower()
    # honesty: never claim success without reading back
    assert "unverified" in text.lower()
```

- [ ] **Step 2-3:** run-fail → write the recipe. It must teach: after any swap/move/place, call `query_geometry` on the changed ids; build the check from the USER's intent (right room? faces the door? no clash?) — an oracle independent of the placement rule; if a fact is wrong (e.g. `facing` points away from the door), fix with `rotate_elements` 180° and re-read; report verified vs unverified counts, never claim orientation success without reading `facing` back. Use the `tukar tandas cangkung → duduk` case as the worked example (the model-sight failure this replaces).

- [ ] **Step 4:** `uv run pytest tests/test_loop_recipe_contract.py -v` → PASS.

- [ ] **Step 5: Re-ingest + stage**

Run: `uv run python scripts/ingest_revit_recipes.py` (needs local pgvector; if unreachable, note for the user — retrieval serves the old set until this runs).

```bash
git add app/knowledge/revit_recipes/verify_after_mutation.md tests/test_loop_recipe_contract.py
```

---

## Repo B — revit-addin-sync (C#, staged for Windows build)

### Task 4: `QueryGeometry.cs` inspector + ToolRegistry wiring

**Files:**
- Create: `BinaVibe/Mcp/Tools/QueryGeometry.cs`
- Modify: `BinaVibe/Mcp/Tools/ToolRegistry.cs` (add the `query_geometry` case)

**Interfaces:**
- Consumes: `ToolRegistry.Invoke(UIApplication, "query_geometry", JsonElement args)` where `args = {"element_ids":[int,...], "aspects":[str,...]?}`.
- Produces: `{ok, elements:[{id, xyz, rotation_deg, bbox, host_id, room, level, facing, (nearest_walls?), (clashes?)}]}`, one row per resolvable id; unresolvable ids skipped with a note. Read-only, NO Transaction.

- [ ] **Step 1: Salvage the PlacementFacts helper.** Copy the `internal static Dictionary<string, object?> PlacementFacts(Document doc, Element el)` method verbatim from `git show feat/model-sight-phase-1-2:BinaVibe/Mcp/Tools/Inspectors.cs` into `QueryGeometry.cs` (it is self-contained — computes xyz/rotation_deg/bbox/host_id/room/level/facing, matches the contract exactly). Keep it as a `static` helper in the new class.

- [ ] **Step 2: Write the handler** in `QueryGeometry.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    // query_geometry — the copilot's eyes. Reads REAL placement facts for
    // element ids from the live model. Read-only (no Transaction).
    internal static class QueryGeometry
    {
        public static Dictionary<string, object?> Run(UIApplication app, JsonElement args)
        {
            var doc = app.ActiveUIDocument.Document;
            var ids = new List<long>();
            if (args.TryGetProperty("element_ids", out var idArr) && idArr.ValueKind == JsonValueKind.Array)
                foreach (var e in idArr.EnumerateArray())
                    if (e.TryGetInt64(out var v)) ids.Add(v);

            var aspects = new HashSet<string>();
            if (args.TryGetProperty("aspects", out var aArr) && aArr.ValueKind == JsonValueKind.Array)
                foreach (var e in aArr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) aspects.Add(e.GetString()!);

            var rows = new List<object?>();
            foreach (var id in ids)
            {
                var el = doc.GetElement(new ElementId(id));
                if (el == null) continue;
                var row = new Dictionary<string, object?> { ["id"] = id };
                foreach (var kv in PlacementFacts(doc, el)) row[kv.Key] = kv.Value;
                if (aspects.Contains("nearest_walls")) row["nearest_walls"] = NearestWalls(doc, el);
                if (aspects.Contains("clashes")) row["clashes"] = Clashes(doc, el);
                rows.Add(row);
            }
            return new Dictionary<string, object?> { ["ok"] = true, ["elements"] = rows };
        }

        // ... PlacementFacts (salvaged, verbatim) ...
        // ... NearestWalls / Clashes: implement with FilteredElementCollector +
        //     BoundingBoxIntersectsFilter around the element bbox; keep simple. ...
    }
}
```

IMPLEMENTER NOTES: (a) match `ToolRegistry`'s existing return type (`Dictionary<string,object?>`); (b) `NearestWalls` = collect `OST_Walls` within an expanded bbox, return `[{id, normal:[x,y]}]` (k=4); `Clashes` = `FilteredElementCollector` with `ElementIntersectsElementFilter(el)` or a bbox-intersect filter, return overlapping ids excluding self; if either is non-trivial, ship the plain facts first and leave the two aspects returning `[]` with a `// TODO Phase 2.1` note — the plain facts are the core deliverable. (c) NO Transaction — this is a read.

- [ ] **Step 3: Wire ToolRegistry.** Add to the `switch` in `ToolRegistry.Invoke` (`ToolRegistry.cs:31-116`):

```csharp
"query_geometry" => QueryGeometry.Run(app, args),
```

- [ ] **Step 4: Self-review the diff.** `git diff` — read-only (no `Transaction`), matches contract fields exactly, PlacementFacts copied verbatim (no behavioral drift), ToolRegistry case added. Stage.

```bash
git add BinaVibe/Mcp/Tools/QueryGeometry.cs BinaVibe/Mcp/Tools/ToolRegistry.cs
```

### Task 5: Scene digest in `BuildContext`

**Files:**
- Modify: `UI/Copilot/RevitChatRouter.cs` (`BuildContext`, ~:606-659) + the `ModelContext` model (`Models/AIRequest.cs`)

**Interfaces:**
- Produces: `ModelContext.SceneDigest` — a bounded list (cap ~40) of `{id, xyz, facing, room, host_id}` for the working set (current selection ∪ active-view elements of the relevant categories), serialized as `sceneDigest` (camelCase) so bina-ai Task 2 reads it.

- [ ] **Step 1:** Add `SceneDigest` to `ModelContext` (`Models/AIRequest.cs`) — `List<Dictionary<string,object?>>` (Newtonsoft serializes as `SceneDigest`; match the existing casing convention the other fields use — if they're camelCased via a resolver, follow it so bina-ai sees `sceneDigest`).
- [ ] **Step 2:** In `BuildContext`, after the existing selection/view collection, build the digest: for each working-set element call `QueryGeometry.PlacementFacts(doc, el)` (reuse Task 4's salvaged helper — do NOT duplicate it) and keep `{id, xyz, facing, room, host_id}`. Cap at ~40, prefer selected elements first. Runs on the UI thread (BuildContext already does).
- [ ] **Step 3:** Self-review — cap enforced, helper reused not duplicated, no Transaction, no per-element exception escapes (wrap in try/skip). Stage.

```bash
git add UI/Copilot/RevitChatRouter.cs Models/AIRequest.cs
```

### Task 6: Phase 2 UAT runbook

**Files:**
- Create: `docs/engine-phase2-uat.md`

- [ ] **Step 1: Write the runbook** covering: (a) `query_geometry` transport check — `curl -X POST .../mcp/tools/query_geometry -H "X-Bina-Secret: ..." -d '{"tool_call_id":"g1","args":{"element_ids":[<a real door id>]}}'` → returns real `xyz`/`facing`/`host_id`/`room`; (b) the `tukar 10 tandas cangkung → duduk` prompt — expect the agent to call `query_geometry` after the swap, read `facing`, and either confirm from geometry or rotate + re-read (NOT self-assessed `facing_confidence`); (c) scene digest present in the turn context (Langfuse trace shows `scene` block); (d) gate: the facing bug that killed model-sight is now caught by the agent reading real `facing`, not by a solver. Stage.

```bash
git add docs/engine-phase2-uat.md
```

---

## Self-review (plan-writing time)

- **Spec coverage:** Phase 2 spec items → tasks: `query_geometry` generic read (T1 Python + T4 C#), scene digest (T2 Python + T5 C#), placement-facts contract salvage (T1 recovers doc, T4 salvages helper), loop recipes + independent verify (T3), NOT reviving solver zoo (constraints + T4 note). UAT (T6).
- **Deliberately deferred to Phase 2.1:** rich relational aspects (`nearest_walls`/`clashes`) may ship as `[]` stubs if non-trivial (T4 note) — the plain placement facts are the core. Auto-sight screenshot net (from the dead branch `CaptureImage`/`AttachMutationSight`) is a separate later task; Phase 2 is geometry-first, vision stays a net for later.
- **Type consistency:** tool name `query_geometry` and arg shape `{element_ids, aspects?}` identical across T1 (Python), T4 (C# handler), T6 (UAT). `PlacementFacts` helper defined once in T4, reused (not duplicated) in T5. Contract fields identical to `docs/contracts/placement-facts.md`.
- **Placeholder scan:** implementer-verify notes are bounded (named file + what to confirm); the two relational aspects have an explicit ship-stub-first fallback.
