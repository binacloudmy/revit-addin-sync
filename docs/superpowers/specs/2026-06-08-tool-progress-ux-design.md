# Detailed Tool-Progress UX ("BIMLogiq-style") — Design

**Date:** 2026-06-08
**Branch:** `feat/copilot-tool-progress` (off `develop`) in both `revit-addin-sync` and `bina-ai`
**Status:** Approved design — pending implementation plan

## Problem

The Bina Bina AI copilot shows generic progress while it works — a single
overwritten line such as "Analyzing your request…" / "Generating code…", and on
the tool-calling path a bare raw tool name (`create_wall…`). Users can't see
what the agent is actually doing step by step. BIMLogiq shows a live, specific,
phased trail ("Querying doors on Level 1", "Creating wall", "Compiling C#") with
each step ticking off as it completes. We want the same transparency.

## Goals

- Show **specific, human-readable** progress per step, authored by the backend.
- Show **phases** (Classifying → Retrieving → Writing → Executing → Reviewing)
  **and** per-tool detail under them.
- Apply to **both** agent paths: codegen (`/generate/stream`) and tool-calling
  (`/tool/generate/stream`).
- Keep completed steps visible with a checkmark; **collapse** the trail to a
  one-line summary when the run finishes.
- No protocol break: an un-upgraded addin or backend keeps working.
- No overlap with the Agno Learning SDK work (separate effort).

## Non-goals

- No change to the actual codegen/tool execution logic or results.
- No new streaming transport — we extend the existing SSE events.
- No localization of labels in this pass (English; phrasing lives backend-side
  so it can be localized later without an addin release).

## Decisions (locked during brainstorming)

1. **Label source:** backend authors the rich label from tool name + args; the
   addin just displays it.
2. **Detail level:** phased **and** per-tool detail.
3. **Scope:** both agent paths.
4. **Step history:** keep the list, mark each done, collapse to a summary when
   finished.
5. **Protocol:** extend the existing `status` + `tool` SSE events
   (backward-compatible), rather than a new/parallel event type.

## Current state (verified on `develop`)

### Backend — `bina-ai/app/main.py`
- `POST /agents/revit-ai/generate/stream` (codegen): emits three **hardcoded
  generic** `status` events ("Analyzing your request…", "Collecting
  information…", "Generating code…") then `done`.
- `POST /agents/revit-ai/tool/generate/stream` (tool-calling): runs
  `revit_ai_tool.arun(stream=True, stream_intermediate_steps=True, …)` and emits
  `{event: "tool", data: {name}}` — **raw tool name only**, no args, no per-tool
  completion, no phases.
- Other events already in use: `meta`, `reply_partial`, `code_partial`, `done`,
  `error` (via `sse_starlette.EventSourceResponse`).

### Addin — `revit-addin-sync`
- `Services/AIServiceStream.cs`: SSE parser already maps `status`/`tool`/`meta`/
  `reply_partial`/`code_partial`/`done`/`error` to a typed `StreamChunk`
  (carries `ToolName`, `StatusLabel`). For `tool` it builds `"<tool> (<status>)…"`.
- `UI/Copilot/CopilotViewModel.cs`: renders progress as a single
  `ChatMessage { Kind = Thinking }` that is **overwritten** via
  `ReplaceLastThinking(...)` — no list, no checkmarks, no collapse.

## Design

### 1. Event protocol — extend `status` and `tool` (backward-compatible)

Add four **optional** fields to the existing event payloads. Absent fields ⇒
current behavior, so old/new addin↔backend combinations keep working.

| Field     | Type                                   | Meaning |
|-----------|----------------------------------------|---------|
| `step_id` | string                                 | Stable id grouping a running→done pair. Tool path: the agno `tool_call_id`. Codegen path: the phase key. |
| `phase`   | enum: `classifying \| retrieving \| writing \| executing \| reviewing` | Bucket the step belongs to. |
| `state`   | enum: `running \| done \| error`       | Lifecycle. Absent ⇒ treat as `running` (compat). |
| `label`   | string                                 | Backend-authored human text, e.g. "Creating wall on Level 1". |
| `detail`  | string (optional)                      | Secondary context, e.g. "Level 1 · 3.2 m" or "30 found". |

`meta`, `reply_partial`, `code_partial`, `done`, `error` are unchanged.

### 2. Backend — label builder (new module)

`bina-ai/app/agents/vibe/progress_labels.py` (exact location confirmed against
codebase conventions during planning):

- `TOOL_LABELS: dict[str, tuple[verb: str, phase: str]]` — maps known tool names
  to a friendly verb + phase (e.g. `create_wall → ("Creating wall", "executing")`,
  `query_doors → ("Querying doors", "classifying")`).
- `build_tool_label(name: str, args: dict) -> tuple[label: str, phase: str, detail: str]`
  — formats with key args (e.g. a `level` arg ⇒ "… on Level 1"); falls back to a
  humanized tool name (`snake_case` → "Snake case…") + a default phase when the
  tool is unmapped, so new tools degrade gracefully without an addin release.
- A small `Phase` set of constants reused by the codegen path.

### 3. Backend — emit changes (`app/main.py`)

- **Tool path** (`/tool/generate/stream`, and the non-stream twin's trace where
  relevant): iterate agno `stream_intermediate_steps` events and distinguish
  **tool-start** from **tool-complete**. On start emit `tool` with
  `state:running`, `step_id = tool_call_id`, and the rich `label/phase/detail`
  from `build_tool_label`. On complete emit `tool` with the same `step_id` and
  `state:done` (optionally enrich `detail` with a result summary).
- **Codegen path** (`/generate/stream`): replace the three hardcoded generic
  statuses with real **phase** steps emitted around the actual stages
  (classify → render recipes → generate → judge), each a `status` event with a
  stable `step_id`, `phase`, and `state` running→done.

### 4. Addin — parser (`Services/AIServiceStream.cs`)

- Extend `StreamChunk` with `Phase`, `StepId`, `State` (enum running|done|error),
  `Detail`.
- Parse the new fields from `status` and `tool` events tolerantly: missing
  `state` ⇒ `running`; missing `step_id` ⇒ synthesize a transient id so the step
  still renders (old-backend compat). Keep existing label fallbacks.

### 5. Addin — UI (`CopilotViewModel` + new `ProgressStepsCard`)

- Replace the single overwritten `Thinking` line with a **step-list reducer** on
  the active thinking message: an `ObservableCollection<ProgressStep>` where
  `ProgressStep { StepId, Phase, Label, Detail, State }`.
  - `state:running` with a **new** `step_id` ⇒ append a row (spinner).
  - `state:running` with an existing `step_id` ⇒ update its label/detail.
  - `state:done` ⇒ mark that row complete (checkmark), stop its spinner.
  - overall `done` event ⇒ **collapse** the list to "Done — N steps"
    (expandable to show the full trail).
- New control `UI/Copilot/Controls/ProgressStepsCard.xaml(.cs)` (or extend the
  existing `ToolCard`) renders the rows (per-row spinner/check icon + label +
  optional detail) and the collapse toggle, styled with existing
  `CopilotTheme`/`CopilotTokens` resources.

### 6. Error handling

- `error` event or `state:error` ⇒ mark the current running step failed (✗),
  stop spinners, surface the message inline; do not collapse (leave the trail
  visible for debugging).
- **Compat — new addin vs old backend:** events arrive without `step_id`/`state`;
  each renders as a transient running line (today's behavior), so nothing breaks.
- **Compat — old addin vs new backend:** the extra fields are ignored by the old
  parser; it still reads `label`/`tool` as before.
- Late/unmatched `done` for an unknown `step_id` ⇒ ignored.

### 7. Testing

- **Backend (pytest):**
  - `build_tool_label`: mapped tools, arg formatting, unmapped fallback.
  - Stream generators: feeding a faked agno intermediate-step sequence yields the
    expected running/done `tool` events with matching `step_id`s; codegen path
    yields the four phase steps.
- **Addin (existing test project):**
  - `AIServiceStream.ParseEvent`: enriched `status`/`tool` events parse all new
    fields; missing-field compat defaults.
  - ViewModel step-list reducer: append-on-running, update-on-same-id,
    complete-on-done, collapse-on-overall-done, error marking.

## Affected files (anticipated)

**bina-ai (`feat/copilot-tool-progress`):**
- `app/agents/vibe/progress_labels.py` (new)
- `app/main.py` (two stream blocks)
- tests under `tests/`

**revit-addin-sync (`feat/copilot-tool-progress`):**
- `Services/AIServiceStream.cs` (StreamChunk + parser)
- `UI/Copilot/CopilotViewModel.cs` (step-list reducer)
- `UI/Copilot/Controls/ProgressStepsCard.xaml(.cs)` (new) or `ToolCard` extension
- addin test project

## Risks / open questions

- agno `stream_intermediate_steps` event shape on the pinned `2.6.8`: confirm the
  exact start vs complete event types and where `tool_call_id`/`tool_args` live
  (settle during planning by reading the agno event classes).
- DeepSeek is now the primary model (key just added). Confirm tool-call streaming
  emits intermediate steps the same way under DeepSeek as under Claude/Azure.
- Phase taxonomy for the codegen path is fixed/static (no tools) — verify the
  four phases map cleanly onto the real stages in `/generate/stream`.

## Out of scope / follow-ups

- Localization of labels.
- Persisting the step trail into history/audit.
- Any Agno Learning SDK work (tracked separately).
