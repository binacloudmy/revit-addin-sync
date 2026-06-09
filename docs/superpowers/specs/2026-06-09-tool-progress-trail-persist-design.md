# Tool-Progress Trail — Persist into Final Bubble (#1) + Genuine Review Phase (#2)

**Date:** 2026-06-09
**Branch:** `feat/copilot-tool-progress` (both `revit-addin-sync` and `bina-ai`)
**Status:** Design approved; ready for implementation plan.

## Background

The Bina Revit copilot streams a live, multi-row phased progress trail while a
prompt runs (BIMLogiq-style: `✓ Understanding your request / ✓ Collecting
information / ✓ Generating answer / ✓ Analyzing the model …`). This was built and
live-verified in Revit 2026 on Windows (addin `21c8d89`, bina-ai `0127357`).

**Problem observed (two user screenshots):** the rich live trail renders correctly
while streaming, but when the run *finishes* the bubble collapses to the OLD
summary card — `1 STEP / ✓ Analyzing the model`. The live trail and the final card
are two different renderers:

- **Live trail:** `ProgressTrail.Render` → `OnProgress(string)` → the thinking bubble.
- **Final card:** `ChatView.xaml.cs::ToolTracePanel` (~L449), rebuilt from
  `RouteResult.ToolCallTrace` — a flat list of REAL tool names only (here just
  `analyze_model_statistics` → "Analyzing the model" via `_toolLabels`).

On completion `ClearProgress()` wipes the live trail, and the final card ignores
the phase rows, so 4 live rows degrade to "1 STEP".

## Goals

1. **#1 — Persist the trail.** Carry the real `ProgressStep` rows out to the final
   message and render them in place of `ToolTracePanel`, preserving the full
   phased trail after completion.
2. **#2 — Genuine review phase.** Add an honest "Checking the result" phase
   bracket on the backend tool stream after the agent run completes. No fake tool
   calls (user explicitly rejected padding).

## Design Decisions (user-approved 2026-06-09)

| Decision | Choice |
| --- | --- |
| Final-bubble appearance | **Always expanded** — all `✓` rows stay visible above each answer (no collapse/click-to-expand). |
| Which rows to keep | **Full trail** — backend phase brackets AND real per-tool rows interleaved. |
| #2 review-phase label | **"Checking the result"** |

## Architecture

### 1. Data plumbing (addin, C#)

Carry the real `ProgressStep` rows — not just tool-name strings — out to the final
message. The existing `ToolCallTrace` (`List<string>`) stays for backward-compat.

- Add `IReadOnlyList<ProgressStep> Steps` (nullable) to:
  - `ToolLoopOutcome` (`Services/ToolLoopRunner.cs:25`)
  - `RouteResult` (`UI/Copilot/Model/ChatRouter.cs:7`)
  - `ChatMessage` (`UI/Copilot/Model/CopilotModels.cs:127`)
- `ToolLoopRunner.RunAsync` snapshots its `trail` (`ObservableCollection<ProgressStep>`,
  L68) into `outcome.Steps` at completion (snapshot to a `List<ProgressStep>` so the
  final message is immutable, not a live collection).
- `RevitChatRouter` sets `RouteResult.Steps` on **both** paths:
  - tool path (`RevitChatRouter.cs:186`) ← `outcome.Steps`
  - codegen path (`RevitChatRouter.cs:357`) ← the local `trail` (`:239`)

### 2. Rendering (addin, `ChatView.xaml.cs`)

- New `ProgressTracePanel(IList<ProgressStep>)`: same quiet rounded card style as the
  existing `ToolTracePanel` (`#fafafa` bg, `#eef0f3` border, 8px radius), header
  `"N STEPS"` (where N = row count), but one row per `ProgressStep`:
  - **State-aware glyph/color** from `StepState`: `✓` green (`#16a34a` on `#dcfce7`)
    for `Done`; `▶` neutral for `Running`; `✗` red for `Error`.
  - Row text = `ProgressStep.Label` (fall back to `StepId` when label empty).
  - Always expanded (no collapse control).
- In the AI-row builder (`ChatView.xaml.cs:~96`):
  - if `m.Steps` is non-empty → render `ProgressTracePanel(m.Steps)`
  - else if `m.ToolCallTrace` is non-empty → fall back to `ToolTracePanel(...)` (legacy messages)
  - else → render nothing.
- The live streaming trail (`ProgressTrail.Render` in the thinking bubble) is
  **unchanged**; this change only governs what survives after `ClearProgress()`.

### 3. Backend review phase (bina-ai, `app/main.py`) — #2

- Add a `reviewing` phase bracket (running → done, label **"Checking the result"**)
  emitted after the `run` phase closes, in **both**:
  - `tool_generate_stream` (`/tool/generate/stream`, ~L1865, after the `run` done event)
  - the `generate_revit_code_stream` tool branch
- Reuse the existing `_status_event` helper and the phase pattern already in place.
- **Honest framing:** the bracket wraps the agent's genuine final
  content-finalization pass (after the last `RunContentEvent`). It does NOT invent a
  tool call. It always emits once per run, so even a 0/1-tool query shows it.

## Data Flow

```
backend SSE (status/tool events, incl. new `reviewing`)
  → AIServiceStreamParser  (step_id / phase / label / detail / state)
  → ToolLoopService.HandleStreamEvent
  → ProgressReducer.Apply  → shared ObservableCollection<ProgressStep> trail
     ├─ live:  ProgressTrail.Render → OnProgress → thinking bubble (unchanged)
     └─ final: trail snapshot → outcome.Steps → RouteResult.Steps
               → ChatMessage.Steps → ProgressTracePanel  (NEW, replaces ToolTracePanel)
```

## Error Handling & Backward-Compat

- Error steps (`StepState.Error`) render `✗`/red — already modelled in `ProgressStep`/`ProgressReducer`.
- **Un-upgraded backend** (no phase/status events): trail still populates from tool
  events; renders whatever rows exist.
- **Legacy messages** (only `ToolCallTrace`, no `Steps`): fall back to `ToolTracePanel`.
- **Empty trail:** render no card.
- Both addin paths (tool + codegen) carry `Steps` for parity.

## Testing

- **Addin (unit):** extend the existing `ProgressReducer` tests — assert a completed
  trail with mixed states (`Done`/`Running`/`Error`) yields the expected ordered rows
  and glyph mapping (pure render helper, no XAML).
- **Backend (pytest):** assert the `reviewing` bracket (running → done, label
  "Checking the result") emits on BOTH stream paths (`tool_generate_stream` and the
  `generate_revit_code_stream` tool branch), including for a 0/1-tool query.
- **Manual:** addin C# cannot compile on the dev Mac — Windows `dotnet build` +
  live Revit 2026 E2E required to verify (unchanged constraint). Local E2E backend
  stack documented in the project resume memory.

## Out of Scope

- No change to the live streaming trail renderer or the thinking bubble.
- No collapse/expand control (decision: always expanded).
- No new XAML templates — `ProgressTracePanel` is built in code like the existing panels.
- No changes to bina-be (NestJS) — not in this path.

## Key File Pointers

- `revit-addin-sync/UI/Copilot/Model/ProgressStep.cs` — `ProgressStep`, `ProgressReducer`, `ProgressTrail`.
- `revit-addin-sync/UI/Copilot/Screens/ChatView.xaml.cs` — `ToolTracePanel` (~L449), AI-row builder (~L96), `_toolLabels` (~L502), `Humanize` (~L566).
- `revit-addin-sync/Services/ToolLoopRunner.cs` — `ToolLoopOutcome` (L25), `RunAsync` trail (L68).
- `revit-addin-sync/UI/Copilot/RevitChatRouter.cs` — tool path (L186), codegen trail (L239) + assignment (L357).
- `revit-addin-sync/UI/Copilot/Model/ChatRouter.cs` — `RouteResult` (L7).
- `revit-addin-sync/UI/Copilot/Model/CopilotModels.cs` — `ChatMessage` (L127).
- `bina-ai/app/main.py` — `tool_generate_stream` (~L1811/L1865), `generate_revit_code_stream` tool branch (~L1504), `_status_event` helper.
