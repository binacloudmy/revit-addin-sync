# Detailed Tool-Progress UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a live, phased, per-tool progress trail in the Revit copilot (BIMLogiq-style) that ticks each step off and collapses to a summary when done — labels authored by the backend.

**Architecture:** Extend the existing SSE `status`/`tool` events with optional `step_id`/`phase`/`state`/`label`/`detail` fields (backward-compatible). The backend (bina-ai) builds rich labels from tool name + args and emits running→done pairs; the addin (revit-addin-sync) parses the new fields and renders a step-list that collapses on completion.

**Tech Stack:** Python 3.13 / FastAPI / sse-starlette / Agno 2.6.8 (backend); C# / WPF / System.Text.Json (addin).

**Spec:** `docs/superpowers/specs/2026-06-08-tool-progress-ux-design.md`

**Repos / branches (both off `develop`):**
- bina-ai → `feat/copilot-tool-progress`
- revit-addin-sync → `feat/copilot-tool-progress`

---

## File Structure

**bina-ai (`feat/copilot-tool-progress`):**
- Create `app/agents/vibe/progress_labels.py` — tool→label map + `build_tool_label()` + `Phase` constants. One responsibility: turn (tool name, args) into human text + phase.
- Modify `app/main.py` — the two stream generators (`/generate/stream`, `/tool/generate/stream`) to emit enriched events.
- Create `tests/test_progress_labels.py` — unit tests for the label builder.

**revit-addin-sync (`feat/copilot-tool-progress`):**
- Modify `Services/AIServiceStream.cs` — add fields to `StreamChunk`, parse them in `ParseEvent`.
- Modify `UI/Copilot/CopilotViewModel.cs` — step-list reducer driving the active thinking message.
- Create `UI/Copilot/Model/ProgressStep.cs` — the step row model + collection.
- Create `UI/Copilot/Controls/ProgressStepsCard.xaml(.cs)` — renders the rows + collapse toggle.
- Modify the addin test project (alongside `Tests/AiServiceUrlTests.cs`) — parser + reducer tests.

---

## Phase A — Backend (bina-ai)

> Work in `/Users/techies/bina-ai` on branch `feat/copilot-tool-progress`.
> `git checkout feat/copilot-tool-progress` first (this carries the `.env` with `DEEPSEEK_API_KEY`).

### Task 0: Pin down the Agno streaming event shape (investigation)

The tool path relies on knowing the exact event classes `revit_ai_tool.arun(stream=True, stream_intermediate_steps=True)` yields, and where the tool name / `tool_call_id` / `tool_args` live, plus how tool-start is distinguished from tool-complete. This is a real unknown (flagged in the spec) and must be settled before Tasks 2–3.

- [ ] **Step 1: Inspect the agno run-event classes**

Run:
```bash
cd /Users/techies/bina-ai
python - <<'PY'
import agno, inspect
from agno.run import agent as ra  # adjust if import path differs
print("agno", agno.__version__)
names = [n for n in dir(ra) if "Tool" in n or "Run" in n or "Event" in n]
print(names)
PY
grep -rniE "ToolCallStarted|ToolCallCompleted|RunResponseContentEvent|event_type|tool_call_id|tool_args|class .*Event" \
  "$(python -c 'import agno,os;print(os.path.dirname(agno.__file__))')/run" | head -40
```
Expected: a list of event classes; note the exact names for "tool started" vs "tool completed" and the attribute that holds `tool_call_id` and `tool_args`.

- [ ] **Step 2: Confirm against the live stream**

Add a temporary debug print in `app/main.py` `tool_generate_stream` loop (the `async for ev in revit_ai_tool.arun(...)`), run `uv run fastapi dev app/main.py`, fire one tool request, and log `type(ev).__name__`, `getattr(ev,'tool',None)`, and any `tool_call_id`/`tool_args`. Record the start/complete event names. Remove the debug print before committing.

- [ ] **Step 3: Write the findings into the plan**

Edit this file: replace the placeholders `<<START_EVENT>>`, `<<DONE_EVENT>>`, `<<ID_ATTR>>`, `<<ARGS_ATTR>>` in Task 3 with the confirmed names. (No commit — planning note.)

---

### Task 1: Label builder module (`progress_labels.py`)

**Files:**
- Create: `app/agents/vibe/progress_labels.py`
- Test: `tests/test_progress_labels.py`

- [ ] **Step 1: Write the failing test**

```python
# tests/test_progress_labels.py
from app.agents.vibe.progress_labels import build_tool_label, Phase

def test_mapped_tool_with_arg():
    label, phase, detail = build_tool_label("create_wall", {"level": "Level 1"})
    assert label == "Creating wall on Level 1"
    assert phase == Phase.EXECUTING
    assert detail == "Level 1"

def test_mapped_tool_no_args():
    label, phase, detail = build_tool_label("query_doors", {})
    assert label == "Querying doors"
    assert phase == Phase.CLASSIFYING
    assert detail == ""

def test_unmapped_tool_humanized_fallback():
    label, phase, detail = build_tool_label("set_param_calculated", {})
    assert label == "Set param calculated"
    assert phase == Phase.EXECUTING  # default phase for unknown tools
    assert detail == ""
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /Users/techies/bina-ai && uv run pytest tests/test_progress_labels.py -v`
Expected: FAIL — `ModuleNotFoundError: app.agents.vibe.progress_labels`.

- [ ] **Step 3: Write minimal implementation**

```python
# app/agents/vibe/progress_labels.py
"""Human-readable progress labels for the copilot's live step trail.

Single responsibility: turn (tool_name, tool_args) into a friendly label, a
phase bucket, and an optional detail string. Backend owns all phrasing so the
addin stays a dumb renderer and new tools degrade gracefully (humanized name)
without an addin release.
"""

from __future__ import annotations


class Phase:
    CLASSIFYING = "classifying"
    RETRIEVING = "retrieving"
    WRITING = "writing"
    EXECUTING = "executing"
    REVIEWING = "reviewing"


# tool name -> (verb phrase, phase). Extend as tools are added.
TOOL_LABELS: dict[str, tuple[str, str]] = {
    "create_wall": ("Creating wall", Phase.EXECUTING),
    "query_doors": ("Querying doors", Phase.CLASSIFYING),
    "open_view": ("Opening view", Phase.EXECUTING),
    "rename_elements": ("Renaming elements", Phase.EXECUTING),
    "set_parameter": ("Setting parameter", Phase.EXECUTING),
    "export_schedule": ("Exporting schedule", Phase.EXECUTING),
    "select_elements": ("Selecting elements", Phase.EXECUTING),
    "tag_in_view": ("Tagging in view", Phase.EXECUTING),
}

# arg keys, in priority order, that carry a useful "on <X>" target.
_TARGET_KEYS = ("level", "view", "name", "category", "element")


def _humanize(tool_name: str) -> str:
    return tool_name.replace("_", " ").strip().capitalize()


def _pick_detail(args: dict) -> str:
    for k in _TARGET_KEYS:
        v = (args or {}).get(k)
        if isinstance(v, str) and v:
            return v
    return ""


def build_tool_label(name: str, args: dict | None) -> tuple[str, str, str]:
    """Return (label, phase, detail) for a tool call."""
    args = args or {}
    verb, phase = TOOL_LABELS.get(name, (_humanize(name), Phase.EXECUTING))
    detail = _pick_detail(args)
    label = f"{verb} on {detail}" if detail else verb
    return label, phase, detail
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/test_progress_labels.py -v`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add app/agents/vibe/progress_labels.py tests/test_progress_labels.py
git commit -m "feat(copilot): backend progress label builder"
```

---

### Task 2: Emit phase steps on the codegen path

**Files:**
- Modify: `app/main.py` — the `_events()` generator inside `generate_revit_code_stream` (the `agent_mode != "tool"` branch, ~lines 1411–1431).

Replace the three hardcoded generic `status` events with real phase steps carrying `step_id`/`phase`/`state`. Each stage emits a `running` then a `done`.

- [ ] **Step 1: Add a helper at module scope in `app/main.py`**

```python
import json as _json_pl
from app.agents.vibe.progress_labels import Phase

def _status_event(step_id: str, phase: str, label: str, state: str, detail: str = ""):
    return {"event": "status", "data": _json_pl.dumps({
        "step_id": step_id, "phase": phase, "label": label,
        "state": state, "detail": detail,
    })}
```

- [ ] **Step 2: Replace the hardcoded statuses**

In the `agent_mode != "tool"` branch, replace the existing
`yield {"event": "status", ...}` lines with paired running/done phase steps wrapping the real stages. Example shape (adapt to the actual local variable names in that block):

```python
yield _status_event("classify", Phase.CLASSIFYING, "Understanding your request", "running")
# ... existing classify call ...
yield _status_event("classify", Phase.CLASSIFYING, "Understanding your request", "done")

yield _status_event("retrieve", Phase.RETRIEVING, "Looking up Revit recipes", "running")
# ... existing recipe render ...
yield _status_event("retrieve", Phase.RETRIEVING, "Looking up Revit recipes", "done")

yield _status_event("write", Phase.WRITING, "Writing code", "running")
res = ...  # existing generate call
yield _status_event("write", Phase.WRITING, "Writing code", "done")

yield _status_event("review", Phase.REVIEWING, "Reviewing code", "running")
# ... (judge runs in background; emit done immediately) ...
yield _status_event("review", Phase.REVIEWING, "Reviewing code", "done")
```

- [ ] **Step 3: Manual smoke test**

Run: `uv run fastapi dev app/main.py`, then:
```bash
curl -N -X POST localhost:8000/agents/revit-ai/generate/stream \
  -H 'Content-Type: application/json' \
  -d '{"prompt":"rename all doors","session_id":"s1","user_id":1}'
```
Expected: SSE `status` events now carry `step_id`/`phase`/`state` with running then done for each of the 4 phases, then `done`.

- [ ] **Step 4: Commit**

```bash
git add app/main.py
git commit -m "feat(copilot): phase steps on codegen stream"
```

---

### Task 3: Emit running/done tool steps on the tool path

**Files:**
- Modify: `app/main.py` — the `async for ev in revit_ai_tool.arun(...)` loops in `tool_generate_stream` (~line 1464) and the second streaming loop (~line 1749).

Use the event names confirmed in Task 0. Replace `<<START_EVENT>>`, `<<DONE_EVENT>>`, `<<ID_ATTR>>`, `<<ARGS_ATTR>>` with the confirmed values.

- [ ] **Step 1: Add a tool-step emitter helper at module scope**

```python
from app.agents.vibe.progress_labels import build_tool_label

def _tool_event(step_id: str, tool: str, args: dict, state: str):
    label, phase, detail = build_tool_label(tool, args)
    return {"event": "tool", "data": _json_pl.dumps({
        "tool": tool, "step_id": step_id, "phase": phase,
        "label": label, "detail": detail, "state": state,
    })}
```

- [ ] **Step 2: Emit running on tool-start and done on tool-complete**

Replace the current name-only `yield {"event": "tool", ...}` with:

```python
async for ev in revit_ai_tool.arun(user_turn, stream=True,
                                    stream_intermediate_steps=True,
                                    session_id=request.session_id):
    ev_name = type(ev).__name__
    tool_obj = getattr(ev, "tool", None)
    if ev_name == "<<START_EVENT>>" and tool_obj is not None:
        tname = getattr(tool_obj, "tool_name", None) or "?"
        tid = getattr(tool_obj, "<<ID_ATTR>>", tname)
        targs = getattr(tool_obj, "<<ARGS_ATTR>>", {}) or {}
        tool_trace.append(tname)
        yield _tool_event(tid, tname, targs, "running")
    elif ev_name == "<<DONE_EVENT>>" and tool_obj is not None:
        tname = getattr(tool_obj, "tool_name", None) or "?"
        tid = getattr(tool_obj, "<<ID_ATTR>>", tname)
        targs = getattr(tool_obj, "<<ARGS_ATTR>>", {}) or {}
        yield _tool_event(tid, tname, targs, "done")
    # ... keep existing reply/code/done handling ...
```

Apply the same change to the second loop (~line 1749).

- [ ] **Step 3: Manual smoke test**

Run the backend, fire a request that triggers tool calls (e.g. an INSPECT query), and confirm each tool yields a `running` then a `done` `tool` event with matching `step_id`, a friendly `label`, and `phase`.

- [ ] **Step 4: Commit**

```bash
git add app/main.py
git commit -m "feat(copilot): running/done tool steps on tool stream"
```

---

## Phase B — Addin (revit-addin-sync)

> Work in `/Users/techies/revit-addin-sync` on branch `feat/copilot-tool-progress` (already checked out).
> Match the existing test framework used by `Tests/AiServiceUrlTests.cs` (inspect it first to copy the `[Fact]`/`[TestMethod]` attribute + assertion style).

### Task 4: Parse the new fields in `AIServiceStream`

**Files:**
- Modify: `Services/AIServiceStream.cs` — `StreamChunk` class + `ParseEvent`.
- Test: addin test project (next to `Tests/AiServiceUrlTests.cs`), new `AiServiceStreamParseTests`.

- [ ] **Step 1: Inspect the existing test style**

Run: `sed -n '1,40p' Tests/AiServiceUrlTests.cs` — note the test framework attributes and namespace to mirror.

- [ ] **Step 2: Write the failing test**

```csharp
// Tests/AiServiceStreamParseTests.cs  (mirror the attribute style of AiServiceUrlTests)
using RevitAddinSync.Services; // adjust namespace to match the project

public class AiServiceStreamParseTests
{
    [Fact] // or [TestMethod]
    public void Tool_event_parses_new_fields()
    {
        var raw = "{\"tool\":\"create_wall\",\"step_id\":\"tc_1\",\"phase\":\"executing\"," +
                  "\"label\":\"Creating wall on Level 1\",\"detail\":\"Level 1\",\"state\":\"running\"}";
        var chunk = AIServiceStreamExtensions.ParseEvent("tool", raw);
        Assert.Equal(StreamChunkKind.Tool, chunk.Kind);
        Assert.Equal("tc_1", chunk.StepId);
        Assert.Equal("executing", chunk.Phase);
        Assert.Equal("Creating wall on Level 1", chunk.StatusLabel);
        Assert.Equal("Level 1", chunk.Detail);
        Assert.Equal("running", chunk.State);
    }

    [Fact]
    public void Tool_event_without_state_defaults_to_running()
    {
        var raw = "{\"tool\":\"open_view\"}";
        var chunk = AIServiceStreamExtensions.ParseEvent("tool", raw);
        Assert.Equal("running", chunk.State);          // compat default
        Assert.False(string.IsNullOrEmpty(chunk.StepId)); // synthesized id
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test` (from the test project dir). Expected: FAIL — `StreamChunk` has no `StepId`/`Phase`/`State`/`Detail`.

- [ ] **Step 4: Extend `StreamChunk` and `ParseEvent`**

In `Services/AIServiceStream.cs`, add to `StreamChunk`:

```csharp
public string StepId { get; init; } = "";
public string Phase  { get; init; } = "";
public string State  { get; init; } = "running"; // running | done | error
public string Detail { get; init; } = "";
```

In `ParseEvent`, for the `"status"` and `"tool"` cases, read the new fields tolerantly and include them on the returned chunk. For `tool`:

```csharp
string state = root.TryGetProperty("state", out var st) ? (st.GetString() ?? "running") : "running";
string stepId = root.TryGetProperty("step_id", out var sid) ? (sid.GetString() ?? "") : "";
string phase = root.TryGetProperty("phase", out var ph) ? (ph.GetString() ?? "") : "";
string detail = root.TryGetProperty("detail", out var de) ? (de.GetString() ?? "") : "";
string richLabel = root.TryGetProperty("label", out var lb) ? (lb.GetString() ?? "") : "";
if (string.IsNullOrEmpty(stepId)) stepId = string.IsNullOrEmpty(tool) ? System.Guid.NewGuid().ToString("N") : tool;
string label = !string.IsNullOrEmpty(richLabel)
    ? richLabel
    : (string.IsNullOrEmpty(tool) ? "Working…" : (string.IsNullOrEmpty(tstatus) ? $"{tool}…" : $"{tool} ({tstatus})…"));
return new StreamChunk {
    Kind = StreamChunkKind.Tool, ToolName = tool, StatusLabel = label,
    StepId = stepId, Phase = phase, State = state, Detail = detail, RawData = raw,
};
```

Apply the analogous additions to the `"status"` case (it has no `tool`; synthesize `step_id` from the `step_id` field, defaulting to a guid).

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test`. Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Services/AIServiceStream.cs Tests/AiServiceStreamParseTests.cs
git commit -m "feat(copilot): parse step_id/phase/state/detail in stream chunks"
```

---

### Task 5: Step-list model + reducer

**Files:**
- Create: `UI/Copilot/Model/ProgressStep.cs`
- Modify: `UI/Copilot/CopilotViewModel.cs` — replace single-line `ReplaceLastThinking` progress with a step collection + a `ReduceProgress(StreamChunk)` method.
- Test: addin test project — `ProgressReducerTests`.

- [ ] **Step 1: Inspect the current thinking-message rendering**

Run: `sed -n '520,650p' UI/Copilot/CopilotViewModel.cs` and `grep -n "ReplaceLastThinking\|CpMsgKind\|class ChatMessage" UI/Copilot/**/*.cs` — note the `ChatMessage`/`CpMsgKind` model so the steps attach to the active thinking message.

- [ ] **Step 2: Create the step model**

```csharp
// UI/Copilot/Model/ProgressStep.cs
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace RevitAddinSync.UI.Copilot.Model // match the project's namespace
{
    public enum StepState { Running, Done, Error }

    public sealed class ProgressStep : INotifyPropertyChanged
    {
        public string StepId { get; init; } = "";
        public string Phase  { get; init; } = "";
        private string _label = "";
        public string Label { get => _label; set { _label = value; Raise(nameof(Label)); } }
        private string _detail = "";
        public string Detail { get => _detail; set { _detail = value; Raise(nameof(Detail)); } }
        private StepState _state = StepState.Running;
        public StepState State { get => _state; set { _state = value; Raise(nameof(State)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>Pure reducer: applies a parsed chunk to a step collection.</summary>
    public static class ProgressReducer
    {
        public static void Apply(ObservableCollection<ProgressStep> steps, string stepId,
                                 string phase, string label, string detail, StepState state)
        {
            ProgressStep existing = null;
            foreach (var s in steps) { if (s.StepId == stepId) { existing = s; break; } }
            if (existing == null)
            {
                steps.Add(new ProgressStep { StepId = stepId, Phase = phase, Label = label, Detail = detail, State = state });
            }
            else
            {
                if (!string.IsNullOrEmpty(label)) existing.Label = label;
                if (!string.IsNullOrEmpty(detail)) existing.Detail = detail;
                existing.State = state;
            }
        }
    }
}
```

- [ ] **Step 3: Write the failing reducer test**

```csharp
// Tests/ProgressReducerTests.cs
using System.Collections.ObjectModel;
using RevitAddinSync.UI.Copilot.Model;

public class ProgressReducerTests
{
    [Fact]
    public void Running_appends_then_done_completes_same_step()
    {
        var steps = new ObservableCollection<ProgressStep>();
        ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Running);
        Assert.Single(steps);
        Assert.Equal(StepState.Running, steps[0].State);

        ProgressReducer.Apply(steps, "tc1", "executing", "Creating wall", "", StepState.Done);
        Assert.Single(steps);                       // same id -> no new row
        Assert.Equal(StepState.Done, steps[0].State);
    }

    [Fact]
    public void Distinct_ids_append_separate_rows()
    {
        var steps = new ObservableCollection<ProgressStep>();
        ProgressReducer.Apply(steps, "a", "writing", "Writing code", "", StepState.Running);
        ProgressReducer.Apply(steps, "b", "reviewing", "Reviewing", "", StepState.Running);
        Assert.Equal(2, steps.Count);
    }
}
```

- [ ] **Step 4: Run test to verify it fails, then build to pass**

Run: `dotnet test`. Expected: FAIL until `ProgressStep.cs` compiles, then PASS.

- [ ] **Step 5: Wire the reducer into `CopilotViewModel`**

In the streaming consumer (where `GenerateCodeStreamAsync` chunks are handled), map `StreamChunk.State` strings to `StepState`, call `ProgressReducer.Apply(...)` on the active thinking message's `Steps` collection for `Status`/`Tool` chunks, mark `Error` on `Error` chunks, and on `Done` set a collapsed summary flag (`"Done — {Steps.Count} steps"`). Add a `public ObservableCollection<ProgressStep> Steps` + `bool StepsCollapsed` to the thinking `ChatMessage` (or a dedicated progress view-model bound by the card).

- [ ] **Step 6: Commit**

```bash
git add UI/Copilot/Model/ProgressStep.cs UI/Copilot/CopilotViewModel.cs Tests/ProgressReducerTests.cs
git commit -m "feat(copilot): step-list reducer for live progress"
```

---

### Task 6: ProgressStepsCard UI control

**Files:**
- Create: `UI/Copilot/Controls/ProgressStepsCard.xaml` + `.xaml.cs`
- Modify: the chat message template/`CopilotPanel` to render the card for thinking messages that carry `Steps`.

- [ ] **Step 1: Inspect an existing control for style**

Run: `sed -n '1,60p' UI/Copilot/Controls/ToolCard.xaml` and `UI/Copilot/CopilotTokens.xaml` — copy the brush/spacing token references so the card matches the theme.

- [ ] **Step 2: Build the card**

Create `ProgressStepsCard.xaml` with an `ItemsControl` bound to `Steps`, each row showing a state icon (spinner when `Running`, check when `Done`, ✗ when `Error`), the `Label`, and a muted `Detail`. Add a header that, when `StepsCollapsed` is true, shows "Done — N steps" with a toggle to expand. Use a `BooleanToVisibility`/state converter (see `UI/Copilot/Controls/CopilotConverters.cs`).

- [ ] **Step 3: Render it for thinking messages**

In the chat list template, when a message has a non-empty `Steps`, host a `ProgressStepsCard` instead of the plain thinking text.

- [ ] **Step 4: Build the addin**

Run: `dotnet build` (or the project's build command). Expected: builds clean.

- [ ] **Step 5: Commit**

```bash
git add UI/Copilot/Controls/ProgressStepsCard.xaml UI/Copilot/Controls/ProgressStepsCard.xaml.cs UI/Copilot/CopilotPanel.xaml
git commit -m "feat(copilot): progress steps card with collapse"
```

---

### Task 7: End-to-end manual test on Windows

- [ ] **Step 1: Run the backend** reachable from the Windows machine (note the URL the addin's `AiUrl`/`BinaConfig` points at; update if needed). Confirm `DEEPSEEK_API_KEY` is loaded.
- [ ] **Step 2: Build + load the addin** in Revit on Windows (the normal addin deploy).
- [ ] **Step 3: Codegen path** — type a simple request (e.g. "rename all doors"); confirm the 4 phase steps appear, tick to done, and collapse to "Done — 4 steps".
- [ ] **Step 4: Tool path** — type a request that triggers tool calls; confirm each tool shows a friendly running line ("Querying doors…", "Creating wall on Level 1") that ticks to a checkmark, and the trail collapses when finished.
- [ ] **Step 5: Error path** — force a failure (e.g. backend down mid-run); confirm the running step shows ✗ and the error message, and the trail does not collapse.
- [ ] **Step 6: Compat check** — point the new addin at an un-upgraded backend; confirm progress still renders as transient lines (no crash).

---

## Self-Review

- **Spec coverage:** protocol fields (Task 4), backend labels (Task 1), codegen phases (Task 2), tool running/done (Task 3), addin parse (Task 4), step-list + collapse (Tasks 5–6), error handling (Tasks 5–7), testing (Tasks 1,4,5,7), both paths (Tasks 2,3). All spec sections mapped.
- **Open dependency:** Task 0 must resolve the agno event names before Task 3 (explicitly sequenced; placeholders are intentional investigation outputs, not plan gaps).
- **Type consistency:** `StreamChunk.{StepId,Phase,State,Detail}` defined in Task 4 are consumed in Task 5; `ProgressStep`/`ProgressReducer.Apply` signature defined in Task 5 is used in Tasks 5–6; `build_tool_label`/`Phase` defined in Task 1 are used in Tasks 2–3. Names align.

## Notes
- Namespaces in the C# snippets (`RevitAddinSync.*`) are placeholders — match the actual project namespaces when implementing.
- Commit messages omit any Co-Authored-By trailer (project convention).
