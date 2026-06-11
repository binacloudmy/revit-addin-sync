# Tool-Progress Trail — Persist into Final Bubble (#1) + Review Phase (#2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the live multi-row progress trail into the final answer bubble (replacing the collapsing "1 STEP" card), and add a genuine "Checking the result" review phase on the backend tool streams.

**Architecture:** Two repos. **bina-ai** (Python/FastAPI, testable on Mac with pytest) gets a DRY `_review_event` helper wired into both tool-stream paths (#2). **revit-addin-sync** (C#/WPF, builds + tests on **Windows only** — no dotnet on the dev Mac) threads the real `ProgressStep` rows out of the tool loop through `ToolLoopOutcome → RouteResult → ChatMessage` and renders them in a new always-expanded `ProgressTracePanel`, falling back to the legacy `ToolTracePanel` for old messages (#1).

**Tech Stack:** Python 3 / FastAPI / sse-starlette / pytest (bina-ai); C# / WPF / xUnit (revit-addin-sync).

**Verification note:** bina-ai tasks run `uv run pytest` on the Mac and must go green here. revit-addin-sync tasks CANNOT compile or run on the dev Mac — their "run test / build" steps execute on Windows. Author the code + tests on the Mac (brace-balanced, structurally correct), then build + `dotnet test` + live Revit 2026 E2E on Windows. Local E2E backend stack is documented in the project resume memory.

---

## Part A — bina-ai backend (#2 review phase). Branch: `feat/copilot-tool-progress`. CWD: `/Users/techies/bina-ai`.

### Task 1: Add the `_review_event` helper + unit test

**Files:**
- Modify: `app/main.py` (after `_tool_event`, ~L1401)
- Test: `tests/test_review_phase.py` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/test_review_phase.py`:

```python
import json

from app.main import _review_event
from app.agents.vibe.progress_labels import Phase


def test_review_event_running_frame():
    frame = _review_event("running")
    assert frame["event"] == "status"
    data = json.loads(frame["data"])
    assert data["step_id"] == "review"
    assert data["phase"] == Phase.REVIEWING
    assert data["label"] == "Checking the result"
    assert data["state"] == "running"


def test_review_event_done_frame():
    data = json.loads(_review_event("done")["data"])
    assert data["state"] == "done"
    assert data["label"] == "Checking the result"
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/test_review_phase.py -q`
Expected: FAIL — `ImportError: cannot import name '_review_event'`.

- [ ] **Step 3: Add the helper**

In `app/main.py`, immediately after the `_tool_event` function (ends ~L1401), add:

```python
def _review_event(state: str):
    """The genuine post-run 'Checking the result' phase bracket.

    Honest framing: brackets the agent's final content-finalization pass after
    the last RunContentEvent — it does NOT invent a tool call. Emitted once per
    run on BOTH tool-stream paths so even a 0/1-tool query ends with a review row.
    """
    return _status_event("review", Phase.REVIEWING, "Checking the result", state)
```

(`Phase` is already imported at module scope — it is used by `_status_event` callers throughout `main.py`. If a `NameError` appears, add `from app.agents.vibe.progress_labels import Phase` at the top of `main.py`.)

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest tests/test_review_phase.py -q`
Expected: PASS (2 passed).

- [ ] **Step 5: Commit**

```bash
git add app/main.py tests/test_review_phase.py
git commit -m "feat(copilot): add _review_event helper for the post-run review phase"
```

---

### Task 2: Wire the review bracket into `tool_generate_stream`

**Files:**
- Modify: `app/main.py:1864-1865` (inside `tool_generate_stream`, right after the `run` phase is closed)

- [ ] **Step 1: Apply the edit**

In `tool_generate_stream`, the phases are closed before the terminal frame (~L1860-1865):

```python
        # Close any phases still open before the terminal frame so the trail
        # ends fully ticked (✓) rather than stuck on a spinner.
        if not gathered_done:
            yield _status_event("gather", Phase.RETRIEVING, "Collecting information", "done")
        if generating:
            yield _status_event("run", Phase.WRITING, "Generating answer", "done")
```

Add the review bracket immediately AFTER that block (before the `ro = await revit_ai_tool.aget_last_run_output(...)` line):

```python
        # Genuine final phase: bracket the agent's answer-finalization pass.
        # Always emitted (even for a 0/1-tool query) — no fake tool call.
        yield _review_event("running")
        yield _review_event("done")
```

- [ ] **Step 2: Verify the file still imports (smoke)**

Run: `uv run python -c "import app.main"`
Expected: no traceback (exit 0).

- [ ] **Step 3: Run the backend test suite slice**

Run: `uv run pytest tests/test_review_phase.py tests/test_progress_labels.py -q`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add app/main.py
git commit -m "feat(copilot): emit review phase on tool_generate_stream"
```

---

### Task 3: Wire the review bracket into the `generate_revit_code_stream` tool branch

**Files:**
- Modify: `app/main.py:1541-1543` (tool branch of `generate_revit_code_stream`, after the gather phase is closed, before `content = "".join(parts)`)

- [ ] **Step 1: Apply the edit**

In the tool branch of `generate_revit_code_stream`, after the gather-done guard (~L1540-1543):

```python
        if not _gathered_done:
            yield _status_event("gather", Phase.RETRIEVING, "Collecting information", "done")

        content = "".join(parts)
```

Insert the review bracket between the gather-done yield and `content = "".join(parts)`:

```python
        if not _gathered_done:
            yield _status_event("gather", Phase.RETRIEVING, "Collecting information", "done")

        # Genuine final phase, same as tool_generate_stream — brackets the
        # answer-finalization pass. Honest: no invented tool call.
        yield _review_event("running")
        yield _review_event("done")

        content = "".join(parts)
```

- [ ] **Step 2: Verify import smoke**

Run: `uv run python -c "import app.main"`
Expected: no traceback.

- [ ] **Step 3: Run the related test suite slice**

Run: `uv run pytest tests/test_review_phase.py tests/test_progress_labels.py -q`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add app/main.py
git commit -m "feat(copilot): emit review phase on generate_revit_code_stream tool branch"
```

---

## Part B — revit-addin-sync addin (#1 persist trail). Branch: `feat/copilot-tool-progress`. CWD: `/Users/techies/revit-addin-sync`.

> All build/test steps in Part B run on **Windows**. On the Mac, write the code + tests and confirm brace balance by eye; defer `dotnet build` / `dotnet test` to Windows.

### Task 4: Add a pure `ProgressTrail.RowText` helper + unit test

This is the only addin logic that is purely testable (no XAML). The render panel (Task 8) consumes it.

**Files:**
- Modify: `UI/Copilot/Model/ProgressStep.cs` (add `RowText` to `ProgressTrail`)
- Test: `Tests/ProgressTrailTests.cs` (add cases)

- [ ] **Step 1: Write the failing test**

Append to `Tests/ProgressTrailTests.cs`, inside the `ProgressTrailTests` class:

```csharp
        [Fact]
        public void RowText_prefers_label_over_stepid()
        {
            var withLabel = new ProgressStep { StepId = "run", Label = "Generating answer" };
            Assert.Equal("Generating answer", ProgressTrail.RowText(withLabel));
        }

        [Fact]
        public void RowText_falls_back_to_stepid_when_label_empty()
        {
            var noLabel = new ProgressStep { StepId = "gather", Label = "" };
            Assert.Equal("gather", ProgressTrail.RowText(noLabel));
        }
```

- [ ] **Step 2: Run test to verify it fails (Windows)**

Run: `dotnet test Tests/Tests.csproj --filter ProgressTrailTests`
Expected: FAIL — `ProgressTrail` has no `RowText`.

- [ ] **Step 3: Add the helper**

In `UI/Copilot/Model/ProgressStep.cs`, inside the `ProgressTrail` static class (next to `Glyph`), add:

```csharp
        /// <summary>Display text for one trail row: the rich backend label, or
        /// the raw step id when no label was supplied. Pure — unit-testable.</summary>
        public static string RowText(ProgressStep s) =>
            s == null ? "" : (string.IsNullOrEmpty(s.Label) ? s.StepId : s.Label);
```

- [ ] **Step 4: Run test to verify it passes (Windows)**

Run: `dotnet test Tests/Tests.csproj --filter ProgressTrailTests`
Expected: PASS (all `ProgressTrailTests`, including the 2 new).

- [ ] **Step 5: Commit**

```bash
git add UI/Copilot/Model/ProgressStep.cs Tests/ProgressTrailTests.cs
git commit -m "feat(copilot): ProgressTrail.RowText pure helper for trail rows"
```

---

### Task 5: Carry `Steps` out of the tool loop (`ToolLoopOutcome`)

**Files:**
- Modify: `Services/ToolLoopRunner.cs:25-35` (add `Steps` to `ToolLoopOutcome`)
- Modify: `Services/ToolLoopRunner.cs:88-104` (snapshot `trail` into `outcome.Steps` on the done branch)

- [ ] **Step 1: Add the property**

In `ToolLoopOutcome` (`Services/ToolLoopRunner.cs:25`), after `public List<string> ToolsUsed { get; } = new();` add:

```csharp
        // The full phased step trail (backend phases + per-tool rows) accumulated
        // this turn, snapshotted at completion. Null on early-error returns.
        // Surfaced to the final chat bubble so the rich trail survives ClearProgress.
        public IReadOnlyList<ProgressStep> Steps { get; set; }
```

- [ ] **Step 2: Snapshot the trail on the done branch**

In `RunAsync`, the "done" branch returns `outcome` (~L103: `return outcome;`). Immediately BEFORE that `return outcome;`, add the snapshot:

```csharp
                    // Snapshot the live trail into an immutable list so the final
                    // message keeps the rich rows after the live collection is gone.
                    outcome.Steps = new List<ProgressStep>(trail);
                    return outcome;
```

(`ProgressStep` is in `RevitWebAppSync.UI.Copilot.Model`, already imported at `ToolLoopRunner.cs:21`. `List<>` and `ObservableCollection<>` are already in use in this file.)

- [ ] **Step 3: Verify build (Windows)**

Run: `dotnet build`
Expected: build succeeds (0 errors).

- [ ] **Step 4: Commit**

```bash
git add Services/ToolLoopRunner.cs
git commit -m "feat(copilot): snapshot the step trail into ToolLoopOutcome.Steps"
```

---

### Task 6: Carry `Steps` through `RouteResult` (both paths)

**Files:**
- Modify: `UI/Copilot/Model/ChatRouter.cs:7-18` (add `Steps` to `RouteResult`)
- Modify: `UI/Copilot/RevitChatRouter.cs:186` (tool path — from `outcome.Steps`)
- Modify: `UI/Copilot/RevitChatRouter.cs:290-298` (codegen streaming path — from the local `trail`)

- [ ] **Step 1: Add the property to `RouteResult`**

In `UI/Copilot/Model/ChatRouter.cs`, after `public List<string> ToolCallTrace;` (L16) add:

```csharp
        public IReadOnlyList<ProgressStep> Steps;  // full phased trail (phases + tools); preferred over ToolCallTrace when present
```

(`ChatRouter.cs` is in namespace `RevitWebAppSync.UI.Copilot.Model` — same as `ProgressStep` — so no using is needed.)

- [ ] **Step 2: Set `Steps` on the tool path**

In `UI/Copilot/RevitChatRouter.cs`, the tool-path `RouteResult` initializer ends at L186 with `ToolCallTrace = outcome.ToolsUsed.Count > 0 ? outcome.ToolsUsed : null,`. Add a sibling line right after it (still inside the initializer, before the closing `};` at L187):

```csharp
                    Steps = outcome.Steps,
```

- [ ] **Step 3: Set `Steps` on the codegen streaming path**

In `UI/Copilot/RevitChatRouter.cs`, the streaming `RouteResult` returned at ~L290-298 (the `final != null && final.Success` branch) does not currently carry a trace. Add `Steps` from the in-scope `trail` (declared at L239). Inside that `return new RouteResult { ... }` initializer, add:

```csharp
                                Steps = new List<ProgressStep>(trail),
```

(`List<>` and `ProgressStep` are already in scope in this file — `trail` is `ObservableCollection<ProgressStep>` at L239.)

- [ ] **Step 4: Verify build (Windows)**

Run: `dotnet build`
Expected: build succeeds (0 errors).

- [ ] **Step 5: Commit**

```bash
git add UI/Copilot/Model/ChatRouter.cs UI/Copilot/RevitChatRouter.cs
git commit -m "feat(copilot): thread step trail through RouteResult on both paths"
```

---

### Task 7: Carry `Steps` onto `ChatMessage` and map it in the view model

The trace-bearing AI bubble is built in `CopilotViewModel.RouteAndAct` at the reply-only branch (`UI/Copilot/CopilotViewModel.cs:579-585`), which sets `ToolCallTrace = rr.ToolCallTrace`. That is the bubble that renders the trace card (both the tool path's "how many doors" answer and the codegen reply-only answer reach it).

**Files:**
- Modify: `UI/Copilot/Model/CopilotModels.cs:127-141` (add `Steps` to `ChatMessage`)
- Modify: `UI/Copilot/CopilotViewModel.cs:579-585` (map `rr.Steps` onto the new message)

- [ ] **Step 1: Add the field to `ChatMessage`**

In `UI/Copilot/Model/CopilotModels.cs`, after `public List<string> ToolCallTrace;` (L139) add:

```csharp
        public IReadOnlyList<ProgressStep> Steps; // full phased trail; ChatView prefers this over ToolCallTrace
```

(If `ProgressStep` is not resolvable here, add `using RevitWebAppSync.UI.Copilot.Model;` — but `CopilotModels.cs` is already in that namespace, so it should resolve unqualified.)

- [ ] **Step 2: Map `rr.Steps` onto the reply-only message**

In `UI/Copilot/CopilotViewModel.cs`, the reply-only `ReplaceLastThinking(new ChatMessage { ... })` (L579-585) currently sets `ToolCallTrace = rr.ToolCallTrace,`. Add a sibling line:

```csharp
                    Steps = rr.Steps,
```

- [ ] **Step 3: Verify build (Windows)**

Run: `dotnet build`
Expected: build succeeds (0 errors).

- [ ] **Step 4: Commit**

```bash
git add UI/Copilot/Model/CopilotModels.cs UI/Copilot/CopilotViewModel.cs
git commit -m "feat(copilot): carry step trail onto ChatMessage and map from RouteResult"
```

---

### Task 8: Render the persisted trail in `ChatView` (new `ProgressTracePanel` + wiring)

**Files:**
- Modify: `UI/Copilot/Screens/ChatView.xaml.cs:96-97` (prefer `ProgressTracePanel` when `m.Steps` present)
- Modify: `UI/Copilot/Screens/ChatView.xaml.cs` (add `ProgressTracePanel` next to `ToolTracePanel`, ~L497)

- [ ] **Step 1: Add the `ProgressTracePanel` builder**

In `UI/Copilot/Screens/ChatView.xaml.cs`, immediately after the existing `ToolTracePanel(...)` method (ends ~L497), add a state-aware variant that renders the real `ProgressStep` rows (always expanded), reusing the same quiet card style:

```csharp
        // Persisted phased trail (phases + per-tool rows) shown in the FINAL
        // bubble — the same rows the live thinking-bubble trail showed, so a
        // completed run keeps its rich trail instead of collapsing to "1 STEP".
        // Always expanded. State-aware glyph/colour (✓ done / ▶ running / ✗ error).
        private FrameworkElement ProgressTracePanel(
            System.Collections.Generic.IReadOnlyList<RevitWebAppSync.UI.Copilot.Model.ProgressStep> steps)
        {
            var outer = new Border
            {
                Background = CopilotColors.From("#fafafa"),
                BorderBrush = CopilotColors.From("#eef0f3"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 8, 0, 0),
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = steps.Count == 1 ? "1 STEP" : $"{steps.Count} STEPS",
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CopilotColors.From("#9ca3af"),
                Margin = new Thickness(0, 0, 0, 5),
            });
            foreach (var s in steps)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1.5, 0, 1.5) };

                // Glyph swatch colours follow state: green check (done),
                // grey arrow (running/incomplete), red cross (error).
                string dotBg = s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Done ? "#dcfce7"
                             : s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Error ? "#fee2e2" : "#eef0f3";
                string glyphFg = s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Done ? "#16a34a"
                               : s.State == RevitWebAppSync.UI.Copilot.Model.StepState.Error ? "#dc2626" : "#9ca3af";

                var dot = new Border
                {
                    Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
                    Background = CopilotColors.From(dotBg),
                    Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                dot.Child = new TextBlock
                {
                    Text = RevitWebAppSync.UI.Copilot.Model.ProgressTrail.Glyph(s.State),
                    FontSize = 8.5, FontWeight = FontWeights.Bold,
                    Foreground = CopilotColors.From(glyphFg),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(dot);
                row.Children.Add(new TextBlock
                {
                    Text = RevitWebAppSync.UI.Copilot.Model.ProgressTrail.RowText(s),
                    FontSize = 11.5,
                    Foreground = CopilotColors.From("#374151"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                });
                sp.Children.Add(row);
            }
            outer.Child = sp;
            return outer;
        }
```

- [ ] **Step 2: Prefer the new panel in the AI-row builder**

In `UI/Copilot/Screens/ChatView.xaml.cs`, the AI-row builder currently renders the legacy panel (L96-97):

```csharp
            if (m.ToolCallTrace != null && m.ToolCallTrace.Count > 0)
                col.Children.Add(ToolTracePanel(m.ToolCallTrace));
```

Replace those two lines with: prefer the rich persisted trail when present, else fall back to the legacy tool-name panel (backward-compat for old messages):

```csharp
            // Prefer the full persisted phased trail (phases + tools). Legacy
            // messages (no Steps) fall back to the tool-name-only summary card.
            if (m.Steps != null && m.Steps.Count > 0)
                col.Children.Add(ProgressTracePanel(m.Steps));
            else if (m.ToolCallTrace != null && m.ToolCallTrace.Count > 0)
                col.Children.Add(ToolTracePanel(m.ToolCallTrace));
```

- [ ] **Step 3: Verify build (Windows)**

Run: `dotnet build`
Expected: build succeeds (0 errors).

- [ ] **Step 4: Run the full addin test project (Windows)**

Run: `dotnet test Tests/Tests.csproj`
Expected: PASS (all tests, including `ProgressTrailTests` and `ProgressReducerTests`).

- [ ] **Step 5: Commit**

```bash
git add UI/Copilot/Screens/ChatView.xaml.cs
git commit -m "feat(copilot): render persisted phased trail in final bubble (always expanded)"
```

---

### Task 9: Live E2E on Windows (Revit 2026)

**Files:** none (manual verification).

- [ ] **Step 1: Bring up the Mac feat backend + ngrok** per the project resume memory:

```bash
docker start bina-pgvector
cd /Users/techies/bina-ai && git checkout feat/copilot-tool-progress && \
  VIBE_AGENT_MODE=tool VIBE_TENANT_DEV_FALLBACK=true VIBE_DEFAULT_TENANT=default \
  uv run uvicorn app.main:app --port 8000
# separate shell:
ngrok http 8000   # confirm URL matches the addin's AiBaseUrl
```

- [ ] **Step 2: Pull + build the addin on Windows**

```bash
git pull   # get the Part B commits
dotnet build
```

- [ ] **Step 3: Drive the tool path in Revit 2026**

Open the Copilot pane, ask **"how many doors in the model"**.
Expected DURING stream: the live multi-row trail (`▶/✓ Understanding your request / Collecting information / Generating answer / Running analyze model statistics… / Checking the result`).
Expected AFTER completion: the final answer bubble keeps an **always-expanded** card titled **"N STEPS"** with the SAME ✓ rows (NOT collapsed to "1 STEP"), with the answer ("62 pintu (Doors)") below it.

- [ ] **Step 4: Confirm the new review row appears**

Expected: a `✓ Checking the result` row is present in both the live trail and the final card.

- [ ] **Step 5: Update the resume memory** with the live-verified result (commit hashes, date), mirroring the prior verification note.

---

## Self-Review

**Spec coverage:**
- #1 persist trail → Tasks 4-8 (plumb `Steps` through `ToolLoopOutcome` → `RouteResult` → `ChatMessage`, render `ProgressTracePanel`). ✓
- #1 always-expanded, full trail (phases+tools), state-aware glyphs → Task 8. ✓
- #1 legacy fallback to `ToolTracePanel` → Task 8 Step 2. ✓
- #1 both paths carry `Steps` → Task 6 (tool L186 + codegen stream L290). ✓
- #2 review phase "Checking the result" on both backend tool streams → Tasks 1-3. ✓
- Testing: backend pytest (Tasks 1-3), addin xUnit for the pure helper (Task 4) + full suite (Task 8), live E2E (Task 9). ✓

**Scope note (deliberate):** the codegen-WITH-code path renders a Result/code card, not an AiReply trace bubble, so it is out of scope for the trace panel — only the reply-only AiReply bubble (tool path + codegen reply-only) renders the trail, per the observed bug. `Steps` is still threaded on both `RouteResult` paths for parity. This matches the spec's "do BOTH paths for parity" without touching the unrelated result-card UI.

**Placeholder scan:** no TBD/TODO; every code step shows complete code.

**Type consistency:** `IReadOnlyList<ProgressStep> Steps` used identically on `ToolLoopOutcome`, `RouteResult`, `ChatMessage`. `ProgressTrail.RowText`/`ProgressTrail.Glyph`/`StepState` names match `ProgressStep.cs`. `_review_event(state)` signature consistent across Tasks 1-3. Backend label string "Checking the result" identical in helper + tests.
