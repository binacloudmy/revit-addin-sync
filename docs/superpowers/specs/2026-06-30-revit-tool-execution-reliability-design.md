# Revit Copilot tool-execution reliability — kill the ExternalEvent freeze

**Date:** 2026-06-30
**Repo:** revit-addin-sync (addin-side only; bina-ai unchanged)
**Status:** design / awaiting review

## Problem

The tunnel-free tool loop (`ToolLoopRunner`) executes each backend-requested
tool in Revit by enqueuing an `McpJob`, raising `McpToolEvent`
(`ExternalEvent`), then **block-waiting** on `McpJob.Completed`
(`ManualResetEventSlim`) for up to **600 s** (`JobMaxWait`).

A Revit `ExternalEvent.Execute()` only runs **during a Revit Idling session**.
Per Autodesk's External Events guide, in default mode idle sessions occur only
"when the mouse stops moving for a moment or a command completes," and "if the
user is not active in the user interface, Revit may not invoke additional idling
sessions for quite some time." The Copilot is a **modeless `DockablePane`**, so
while the user is typing/clicking in the pane (not the Revit canvas) Revit
generates **no idle sessions** → the pending tool event is never serviced. A
**modal dialog blocks idle generation entirely.**

Result: the addin sits in its 600 s wait → the pane shows "executing…" → no
`/tool/resume` is ever posted. Observed live (Langfuse, 2026-06-30 11:19 MYT,
prompt "sembunyikan bumbung dan siling"): 3 fresh generate attempts, **zero
resumes**, each paused on `find_elements_by_filter` (external-execution INSPECT)
and frozen.

This is the standard Revit modeless-execution hazard, not a one-off. It can hit
**any** external-execution tool call (or codegen run), regardless of which tool
the agent picked.

### Sources

- Autodesk — External Events (Revit API Developer Guide)
- The Building Coder — "Idling Enhancements and External Events"
- RevitAPIDocs — `IdlingEventArgs.SetRaiseWithoutDelay`
- Revit.Async (KennanChan) — TAP/`TaskCompletionSource` wrapper
- BIM Matters — "Revit API – External events & modeless dialogs"

## Goal & success bar

Every tool handoff must reach a definite outcome quickly; nothing hangs.

1. With the user idle in the pane (not the canvas), a queued tool runs **promptly**
   (forced idling), not "eventually / never."
2. With a Revit **modal dialog open**, the tool call returns a **clear error
   within seconds** ("Revit has a dialog open — close it and retry"), never a
   600 s freeze.
3. On any genuine stall, a **bounded timeout (~30–45 s)** returns a typed error.
4. No silent UI freeze; the pane always reaches done / clear-error.

## Current wiring (verified)

- `App.cs:163` — `McpToolEvent = ExternalEvent.Create(McpToolHandler)` (always
  created, independent of the gated tunnel).
- `McpExternalEventHandler.Execute()` — drains the `ConcurrentQueue<McpJob>`,
  runs `ToolRegistry.Invoke`, sets `job.Completed`. Clean; honours
  `job.Abandoned`.
- `McpJob.Completed` — `ManualResetEventSlim` (the block-wait signal).
- `ToolLoopRunner.ExecuteOneAsync` — `evt.Raise()` then
  `Task.Run(() => job.Completed.Wait(600s))`.
- **Existing precedents to reuse:** `UpdateService` already does the
  `Idling += / work / Idling -=` subscribe-drain-unsubscribe pattern; `App.cs:109`
  already runs a diagnostic Idling handler that logs
  `[BinaVibe][idle] UI thread blocked Nms`. Neither uses `SetRaiseWithoutDelay`.
- **Not wired today:** `SetRaiseWithoutDelay`, `DialogBoxShowing`.

## Design — layered fix

No single mechanism covers both "user inactive" and "modal dialog open," so the
fix is layered. Layers 1–2 are the root-cause reliability fix; 3–4 are the
robustness net for the modal case idling physically cannot beat.

### Layer 1 — forced-Idling drain (core cure)

Replace "raise an ExternalEvent and hope an idle session comes" with a
self-driving Idling drainer:

- When a job is enqueued, subscribe a dedicated handler to `UIApplication.Idling`
  (mirror `UpdateService`'s pattern).
- In every callback: drain the `McpJob` queue via the existing
  `ToolRegistry.Invoke` (Idling handlers run in a valid API context and may
  modify documents), and while the queue is non-empty call
  `e.SetRaiseWithoutDelay()` so Revit keeps raising Idling continuously — "even if
  the user is totally inactive."
- When the queue drains, **unsubscribe** (stop forcing idling → no CPU spin at
  rest).

This guarantees prompt execution while the user is in the pane, and sidesteps the
documented "Raise() Accepted but Execute() never fires" Revit edge bugs.
`ToolRegistry.Invoke` and the abandoned-job handling are reused unchanged. Retire
`McpToolEvent` for the tool path (single drain path), keeping the
`McpExternalEventHandler` type only if `McpServer`/tunnel (gated off) still
references it.

**Cost / mitigation:** forced idling keeps the CPU engaged — bounded to the
in-flight window only (subscribe on enqueue, unsubscribe on drain).

### Layer 2 — TAP, stop block-waiting

Replace `McpJob.Completed` (`ManualResetEventSlim`) with
`TaskCompletionSource<JobResult>`. `ExecuteOneAsync` `await`s the TCS instead of
`Task.Run(() => Wait(600s))` — no threadpool thread pinned, clean async (the
pattern Revit.Async standardises). The drainer completes the TCS with
success/error.

### Layer 3 — modal-dialog guard, fail fast

A modal dialog blocks even forced idling. Subscribe `UIApplication.DialogBoxShowing`
to track a `_modalOpen` flag (detect only — never auto-dismiss the user's
dialog). When a job is pending and a modal is open (or no idle has fired within a
short grace window), complete the job's TCS immediately with a typed error:
"Revit has a dialog open — close it and retry."

### Layer 4 — bounded timeout + feedback (safety net)

`JobMaxWait` 600 s → **~45 s** (config-overridable). On timeout, complete the TCS
with "Revit didn't respond — it may be busy or have a dialog open." The existing
`CancellationToken` (Stop button) path still marks `Abandoned`.

## Data flow (new)

```
backend awaiting_revit + pending calls
  → ToolLoopRunner enqueues McpJob(s), subscribes forced-Idling drainer
  → Revit raises Idling repeatedly (SetRaiseWithoutDelay)
  → drainer runs ToolRegistry.Invoke per job → completes each job's TCS
  → ExecuteOneAsync await returns results → POST /tool/resume
  → queue empty → unsubscribe Idling

modal open / 45s timeout / cancelled
  → job TCS completed with typed error (fast) → /tool/resume with error
  → agent reports it (never silent)
```

## Components changed (revit-addin-sync)

| File | Change |
|---|---|
| `BinaVibe/Mcp/McpJob.cs` | `ManualResetEventSlim Completed` → `TaskCompletionSource<JobResult>`; keep `Abandoned`, timings |
| `BinaVibe/Mcp/McpExternalEventHandler.cs` | Replace ExternalEvent drain with an Idling-driven drainer using `SetRaiseWithoutDelay`; keep abandoned-job handling and `[BinaVibe][timing]` logs |
| `App.cs` | Wire the Idling drainer (subscribe on enqueue / unsubscribe on drain); subscribe `DialogBoxShowing` for `_modalOpen`; retire `McpToolEvent` for the tool path if unused elsewhere |
| `Services/ToolLoopRunner.cs` | `ExecuteOneAsync` awaits the TCS; `JobMaxWait` 600→45 s; map modal/timeout/cancel to typed errors |
| `UI/Copilot/*` | Render the "Revit busy / dialog open" error clearly in the pane |

## Error handling

Every job **always** completes its TCS — success, tool error, abandoned (Stop),
timeout, or modal-blocked. The drain stays idempotent (`ConcurrentQueue.TryDequeue`
is atomic). No path leaves `ExecuteOneAsync` waiting.

## Testing

- **Unit (cross-platform):** `McpJob` TCS completion; abandoned job drains as
  error; timeout completes as error; modal-flag short-circuits to error.
- **Integration (Windows/Revit only — cannot run on macOS):**
  1. User idle, focus in the pane → tool runs within ~1 s (forced idling).
  2. Revit modal dialog open → tool returns the "dialog open" error within seconds.
  3. Long/blocked Revit → 45 s timeout error, no freeze.
  4. Stop mid-wait → clean cancel, next prompt unaffected.

## Out of scope

- bina-ai `set_category_visibility` tool (separate spec — reduces round-trip
  frequency but is not the reliability fix).
- Serving INSPECT reads from the cloud mirror / live snapshot (frequency
  reducer, not reliability).

## Risks

- **Forced-idling CPU:** mitigated by subscribing only while a job is in flight.
- **`DialogBoxShowing`:** detect-only; must not dismiss the user's dialogs.
- **Modal dialog fundamentally blocks execution:** unsolvable by idling — Layer 3
  fail-fast is the correct, honest answer (clear error, not a hang).
- **Tunnel path coupling:** `McpServer`/`McpTunnelClient` (gated off) reference
  the handler/event; verify before retiring `McpToolEvent`.
