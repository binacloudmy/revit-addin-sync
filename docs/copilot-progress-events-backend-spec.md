# Copilot thinking-trail — progress event spec (bina-ai backend)

The Revit add-in already renders a **live, one-by-one step checklist** in the chat's
"thinking" bubble (spinner `▶` → `✓` done / `✗` error, collapses when the answer
starts streaming). The add-in does **not** invent this narration — it renders
whatever progress events the backend streams. To make the indicator show detailed
real-time activity (instead of a single "Generating answer"), the **agent must emit
a progress event at the _start_ of every discrete action**, and a matching
completion event.

This is purely additive to the existing SSE contract — no add-in change is required
for these to appear; they render as soon as the backend sends them.

## Transport

Endpoint: `POST /agents/revit-ai/generate/stream` · `Accept: text/event-stream`.
Standard SSE framing (`event:` + `data:` lines, blank line terminates an event).

Relevant event types (already parsed by the add-in — see `AIServiceStreamParser.cs`):

| event    | when                                   |
|----------|----------------------------------------|
| `status` | a phase / lookup / non-Revit step      |
| `tool`   | a tool call (Revit or server-side)     |
| `reply_partial` | answer markdown streaming (collapses the trail) |
| `done` / `error` | terminal                       |

## Event shape

Emit **one event when the action starts** (`state:"running"`) and **one when it
finishes** (`state:"done"` or `"error"`), reusing the **same `step_id`** so the add-in
updates the existing row in place instead of adding a duplicate.

```
event: status
data: {"step_id":"s3","phase":"retrieving","label":"Reading project standards…","state":"running"}

event: status
data: {"step_id":"s3","phase":"retrieving","label":"Reading project standards…","state":"done"}
```

```
event: tool
data: {"step_id":"tc_7","tool":"list_grids","phase":"executing","label":"Reading grids from Main Model…","state":"running"}

event: tool
data: {"step_id":"tc_7","tool":"list_grids","state":"done"}
```

Fields:

- `step_id` **(required to pair rows)** — stable id for the action; running→done must share it.
- `label` — the human-friendly line shown to the user. **Author it backend-side** with
  the key parameter included where useful, e.g. `"Reading levels from Main Model + 2 linked models…"`.
  If omitted on a `tool` event, the add-in maps the raw `tool` name through its own
  table (`ToolLabels.cs`) as a fallback — so `list_grids` still renders as
  "Reading grids…" — but a backend-authored label with parameters is richer.
- `tool` — raw tool name on `tool` events (drives the add-in's fallback label).
- `phase` — coarse bucket, free-text. Suggested: `interpreting` · `retrieving` ·
  `executing` · `writing` · `reviewing`.
- `state` — `running | done | error`. On `error`, the row shows `✗`.
- `detail` (optional) — a short secondary string (e.g. `"Level 1"`).

## What to emit an event for

Emit a `running` event **as the action starts** (not after it finishes) for:

1. **Every tool / Revit API call** — `list_grids`, `get_levels`, `query wall types`, …
   → `"Reading grids from Main Model…"`, `"Querying wall types…"`.
2. **Every document / knowledge lookup** — RAG / recipe / standards retrieval
   → `"Searching CIDB recipes…"`, `"Reading project standards…"`.
3. **Long internal phases** — `"Interpreting CAD blocks…"`, and `"Generating answer…"`
   as the final `writing` step.

Fallback: if a phase produces no detailed events, still emit its generic step
(`"Generating answer…"`) so the indicator is never empty.

## Rules the add-in relies on

- **Order = arrival order.** Rows render in the order events arrive; emit start
  events in the order work actually begins.
- **Pair by `step_id`.** A `done`/`error` with a known `step_id` updates that row;
  a new `step_id` appends a row.
- **Trail collapses on answer.** When `reply_partial` starts streaming the markdown
  answer, the add-in swaps the trail for the streaming reply — so send progress
  events *before* the answer text, not interleaved with it.
- **Revit-executed tools.** When the agent hands a tool to the add-in to run, the
  add-in emits its own `running`/`done` for that `step_id` (labelled via
  `ToolLabels.cs`). If the backend already streamed a richer `label` for that
  `step_id`, the add-in preserves it — so prefer sending the label first.

## Example sequence (for "how long is the roof?")

```
status  {step_id:"p1", phase:"interpreting", label:"Understanding the request…",        state:"running"}
status  {step_id:"p1", state:"done"}
tool    {step_id:"t1", tool:"get_project_info", label:"Reading project info…",           state:"running"}
tool    {step_id:"t1", state:"done"}
tool    {step_id:"t2", tool:"find_elements_by_filter", label:"Finding roof elements…",   state:"running"}
tool    {step_id:"t2", state:"done"}
status  {step_id:"w1", phase:"writing", label:"Generating answer…",                      state:"running"}
reply_partial {delta:"The roof perimeter is …"}
done    {…}
```

## Add-in side (already done)

- `Services/ToolLabels.cs` — single map of raw tool name → readable label (+ key
  arg), used both for locally-executed Revit tools and as the fallback for backend
  `tool` events without a label. **Edit labels there.**
- `AIServiceStreamParser.cs` / `RevitChatRouter.cs` / `ProgressStep.cs` — parse,
  reduce, and render the trail. No changes needed to support the events above.
