# Copilot v5 — Agent Activity & Query Progress (PRD)

**Date:** 2026-08-19 · **Design source:** `docs/design/bina-copilot-v5.dc.html` (claude.ai/design project "Bina AI Copilot Redesign", page *Bina Copilot v5*) · **Reference recording:** screen capture 2026-08-19 9:30 PM (design-canvas playback of the same file)
**Branch:** `feat/copilot-v5-agent-activity` · **Base:** `develop` · **Depends on:** stream v2 hermes-parity (PR #79, merged), engine autospawn/preflight (PR #80, merged)

---

## 1. What the design shows

The v5 design file plays one scripted turn — *"list all doors in this model"* — and demonstrates a rendering vocabulary, not a new product surface:

1. **Streamed thinking** — narrative text streams character-by-character into an "Agent activity" card with a blinking caret, then freezes with a duration chip (`2s`).
2. **Five-step timeline** — Read the request → Query model → Count by type → Validate results → Compose answer. Each row: pending ○ → active spinner → done ✓, with a per-step duration on the right.
3. **Tool cards inside steps** — `find_elements_by_filter` / `count_by` cards mount under their step, ARGS stream in, a **determinate progress bar** runs `Scanning elements… N / 62` as matches accumulate, RESULT streams in, spinner flips to ✓ with duration, card auto-collapses.
4. **Live found-count** — the N/62 counter is mirrored in a viewport badge and door markers light up as they are found.
5. **Answer streams after the trail** — headline, table rows appearing one by one, then an action row (Create schedule in Revit · Copy table · CSV) and a meta line (`2 tool calls · 8.9s · bina-agent-1`).
6. **Chrome** — status tag in the session header (Thinking… / Query model — Doors… · 2/5 / Done), live elapsed timer, Stop button, ⌘K palette, suggestion chips after a run, welcome cards, nav rail (Chat/History/Library/Model/Settings), right rail with Viewport / Elements / Logs tabs.

## 2. Where the addin already is

Stream v2 (PR #79) already ships most of the streaming vocabulary:

| v5 behaviour | Status in addin |
|---|---|
| Streamed thinking text + card + caret | ✅ `reasoning` SSE (`text_delta`) → `ReasoningReducer` → `ReasoningTimelineView` |
| Step trail, running→done, per-step timing | ✅ `status`/`tool` events keyed by `step_id` → `ProgressReducer`/`ProgressTrailView` (backend-authored rows, not a fixed 5) |
| Tool result cards (name · duration · ✓/✗ · ARGS/RESULT · copy, auto-expand on error) | ✅ `tool_result` event / local synthesis → `ToolResultCard` (mounted post-completion only) |
| Segmented narrative (no glued blobs) | ✅ `reply_partial.segment` → `TurnBlocks` |
| Stop button, cancelled-turn line | ✅ `PromptBar.Busy` → `CancelSendCommand` |
| Welcome screen | ✅ `ChatView.EmptyState()` (prompt cards + chips exist but are commented out) |
| Command palette widget | ✅ `CommandPalette` — opened by `/` only, no Ctrl+K |
| History / Library nav | ✅ tabs exist; Saved view built but not reachable |
| Highlight-in-model primitive | ✅ element-id click → local `select_elements` McpJob (works today, has no button) |

**What does not exist anywhere:**

- A **determinate progress event**. The wire has `status`/`tool` (tri-state) and `tool_result` (atomic, post-hoc). No event carries `current/total`. This is the single genuine protocol gap behind "the progressive bar when querying element".
- In-flight tool cards (spinner state, streamed args/result). Cards mount only after completion.
- A clock-driven elapsed timer (today's numbers only move when a frame arrives).
- Ready/Running/Done status tag; Ctrl+K binding; a Highlight button.
- Right rail (Viewport/Elements/Logs), Model view, Settings view.

## 3. Feasibility — direct answer

**Can we achieve the streaming?** Yes — it is already shipped (stream v2). Thinking, narrative segments and tool cards stream today against both engine and cloud.

**Can we achieve the progressive bar when querying elements?** Yes, with one additive SSE event plus plumbing:

- The **UI side is cheap** and follows the exact pattern v2 proved: new event → parser case → pure reducer → callback → render. Old addins ignore unknown events; new addin without the event falls back to today's indeterminate row. No compatibility risk.
- The **honest-counts side is the real work**: element enumeration runs as one synchronous `FilteredElementCollector` pass inside the Idling-pump job. Emitting real increments needs an `IProgress<(int current,int total)>` handle through `McpJob`, with the tool chunking its scan. Phase A ships the addin-side contract + local synthesis for addin-executed tools; Phase B does the engine/backend emitters.
- We do **not** ship a cosmetic fake bar (interpolating 0→count after the fact). The repo has consistently rejected cosmetic progress; the bar appears only when a producer reports real counts, otherwise the row stays indeterminate exactly as today.

**"And everything"** — the remaining v5 chrome splits by cost: quick wins (Ctrl+K, ticking timer, status tag, chips, Highlight button, Saved tab) land in Phase A; the right rail / Model / Settings views are Phase C and should follow the WebView2 decision the stream-v2 spec already flagged, because live element lists and log feeds are precisely what is expensive in the current rebuild-per-tick WPF thread and cheap in a webview.

## 4. Wire protocol addition (additive, v2-style)

### New event `progress`

```
event: progress
data: {"step_id":"t2","tool":"find_elements_by_filter",
       "current":36,"total":62,"unit":"elements",
       "label":"Scanning elements…","segment":"leg-1"}
```

- `step_id` **(required)** — pairs the bar with the trail row / tool card of the same id (same pairing rule as `status`/`tool`).
- `current` **(required)**, `total` (optional) — `total` present → determinate bar `current / total`; absent → counter only (`36 elements…`).
- `unit`, `label` — display strings; label falls back to `ToolLabels.cs` mapping.
- Terminal state comes from the existing `tool`/`tool_result` events — `progress` never terminates a row; a `done` on the same `step_id` freezes the bar at full.
- Frequency: producer throttles to ≤10 events/s per step_id; addin coalesces regardless (last value wins per render tick).
- Feature-detect mirrors v2: the event's absence is the legacy path. Old addins hit the parser's `default: return null` and ignore it.

### Producers

1. **Engine (bina-ai / BinaVibe tools)** — Phase B. `IProgress` plumbed through `McpJob`; `find_elements_by_filter`, `filter_elements`, `find_mep_elements`, `count_by` chunk their collector pass and report every ~250 ms.
2. **Addin-local execution (cloud path)** — Phase A. `ToolLoopRunner`'s per-call executor already stopwatches each call and synthesizes `ToolResultEvent` (T4 precedent); the same seam raises `ProgressCountEvent` when the executing tool exposes counts.

## 5. Requirements

### Phase A — this branch (addin only, no backend dependency)

| ID | Requirement |
|---|---|
| **A1** | Parse `progress` → new `StreamChunkKind.Progress` + `ProgressCountEvent` DTO; unknown-field tolerant; unit tests beside `StreamV2Tests`. |
| **A2** | `ProgressReducer`/`ProgressStep` carry `Current`/`Total`; reducer applies count frames by `step_id`; pure, unit-tested. |
| **A3** | `ProgressTrailView` renders a determinate bar under the active row when `Total` is known (nested-`Border` fill, **no Storyboard** — Revit pane constraint), `N / total · unit` right-aligned, `Cp.Reasoning.BarTrack` + accent fill tokens; indeterminate rows unchanged. |
| **A4** | `ToolLoopService.HandleStreamEvent` case `progress` → reducer + existing `onSteps` callback (no 6th callback needed — counts ride the step list). |
| **A5** | `ToolLoopRunner` local seam: executor forwards tool-reported counts through the same reducer path before the resume POST. |
| **A6** | Elapsed timer ticks — `DispatcherTimer` (250 ms) in the live thinking/trail card while a turn runs; stops on done/error/cancel; respects `ReducedMotion` (still updates text, no animation). |
| **A7** | Header status tag: Ready / Running (with active-step label `· k/n`) / Done / Stopped, promoted from `ToolActivity`; theme tokens, no new state machine beyond what the VM already knows. |
| **A8** | Ctrl+K (and Cmd+K) opens `CommandPalette` from anywhere in the pane; Esc closes; existing `/` path untouched. |
| **A9** | "Highlight in model" action row on answers that carry element ids — wraps the existing `select_elements` McpJob path (today's element-id click), count-labelled (`62 elements · Doors`). |
| **A10** | Re-enable suggestion chips after a completed run (uncomment + restyle the existing block); clicking inserts the prompt (respecting the existing insert-not-send behaviour). |
| **A11** | Compile-gated on Mac (official SDK, all three TFMs), reducer/parser tests green, UiHarness screenshot of the determinate bar. |

### Phase B — backend/engine (bina-ai + BinaVibe, separate PR)

- `progress` emitters in both stream generators; `IProgress` through `McpJob` + Idling pump; chunked collectors in the four query tools; throttle ≤10/s; byte-stable-prefix rules respected (no timestamps in frames).
- Canonical phase mapping so trails read like the design's 5 steps: backend groups its `step_id`s under `phase` values `interpreting · retrieving · executing · reviewing · writing`; addin's `FriendlyStep` renders phase headers. (The addin does not invent narration — unchanged principle.)
- Tool-card lifecycle events (`tool_call_start`, optional args digest at start) so cards mount with a spinner instead of appearing post-hoc; requires `BlocksPanel` to reconcile cards instead of rebuilding per tick (fixes today's expand-state loss too).

### Phase C — surface expansion (own package, after WebView2 decision)

- Right rail: Elements tab (live element list model), Logs tab (`McpCallLog` promoted from file-append to observable feed), Viewport tab (needs the receipt screenshot machinery generalised).
- Model view (file/version/levels/warnings/last-sync + Resync), Settings view (surface `CopilotPrefs`).
- Explicit WebView2 go/no-go first — the stream-v2 spec's deferred item; Phase C items are the ones that get cheap in a webview and expensive in the current imperative WPF thread.

## 6. Non-goals

- No cosmetic/interpolated progress bars — determinate UI only with real counts.
- No fixed client-side 5-step script — steps remain backend-authored; the design's 5 steps are the target *shape* for Phase B's phase mapping.
- No WebView2 rewrite in this package; no DeepSeek reasoning-tokens-on; no single-SSE confirm (all previously deferred, still deferred).
- No `v2`-suffixed parallel files — behaviour changes land in the existing classes.

## 7. Acceptance (Phase A)

1. Against a `progress`-emitting producer: active trail row shows `Scanning elements… 36 / 62` with a moving determinate bar; row freezes full on `done`; bar never regresses.
2. Against today's backend (no `progress` frames): rendering byte-identical to current develop.
3. Old addin against a `progress`-emitting backend: unchanged (event ignored).
4. Elapsed label visibly ticks during a silent decode leg; stops on done/cancel.
5. Ctrl+K opens the palette with focus in its filter; Esc restores composer focus.
6. Answer with element ids shows the Highlight row; clicking selects the elements in Revit (existing McpJob path), no backend round-trip.
7. Status tag transitions Ready → Running (`Query model… · 2/5` when phase info exists) → Done; Stopped on cancel.
8. `dotnet build` clean on net48/net8.0-windows/net10.0-windows; new reducer/parser tests pass; existing StreamV2/ProgressReducer/ProgressTrail suites untouched-green.

## 8. Test plan

- **Unit (Tests/):** `progress` parse (full, minimal, garbage, no-total), reducer apply/coalesce/out-of-order/unknown-step_id, freeze-on-done, never-regress clamp.
- **Harness:** UiHarness mock stream replaying the door-query sequence from the design (thinking → 5 steps → counts 1→62 → tool results → answer) for visual check + screenshots.
- **UAT:** standing 6-prompt replay + COPILOT-TESTING §9 chat items; new items: T-090 determinate scan bar (engine), T-091 legacy backend fallback, T-092 Ctrl+K, T-093 highlight row.
