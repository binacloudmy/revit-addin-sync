# Copilot stream v2 — Hermes-parity rendering (addin work package)

**Date:** 2026-08-19 · **Repos:** revit-addin-sync (primary), bina-ai (protocol additions)
**Depends on:** branch `feat/profile-store-off-and-mutate-batching` deployed (context fixes — the content BEHIND this UI)

## Why

Side-by-side with the Hermes agent UI (2026-08-19 recording), the copilot pane loses on
three rendering behaviors, none architectural. Colocate already removed the structural
blocker: Langfuse trace `50770989` shows one agent run executing tools in-process,
back-to-back (`set_section_box` 0.06s inline, 7 model legs, 22.8s total) — the wire is
already Hermes-shaped. The pane isn't:

1. **One growing blob.** Backend streams per-leg narrative ("I'll scope…" → "The active
   view isn't a 3D view…" → "Done."), pane concatenates into a single bubble — UAT
   screenshot 2026-08-18 shows `…level L3.The` glued mid-sentence.
2. **Invisible tool results.** Structured diffs, durations, ok/error exist per call;
   pane shows a one-line headline in the thinking card. Hermes shows a collapsible
   terminal card: command, streamed output, exit badge, duration, copy.
3. **Stream teardown on confirm.** Mutate-confirm ends the SSE; the resume opens a new
   one. Visually the thread dies and restarts.

Non-goals: DeepSeek reasoning tokens stay OFF (measured 45-60s/leg); no WebView2
rewrite this package (recorded below as the eventual big lever); cloud-path gets the
same rendering via addin-side synthesis, no cloud backend change.

## Wire protocol v2 (bina-ai, additive — old addins unaffected)

SSE endpoints unchanged (`/agents/revit-ai/tool/generate/stream`, `/tool/resume/stream`).
Parser: `Services/AIServiceStreamParser.cs`. All additions are NEW FIELDS on existing
events or NEW event names — both are ignored by shipped addins (proven pattern: the
`ask_user` structured twin rode `awaiting_user_input` the same way).

### V2.1 `reply_partial` gains segment identity

```json
event: reply_partial
data: {"text": "The active view isn't a 3D view, so I'll create one first.",
       "segment": "leg-2"}
```

- `segment` = stable id per model leg (backend: leg counter in the stream generators,
  `app/routers/revit_turn/router.py`). Same segment → append text; NEW segment →
  the pane closes the current narrative block and opens a new one.
- No `segment` field (old backend) → addin falls back to today's single-bubble append.
  This field is the v2 feature-detect: first frame carrying it flips the pane into
  segmented rendering for that turn.

### V2.2 new event `tool_result` (engine-emitted; cloud = addin-synthesized)

```json
event: tool_result
data: {"tool_call_id": "call_00_…", "tool": "set_section_box",
       "ok": true, "duration_ms": 448,
       "args_digest": "{\"view_id\": 123, \"level\": \"L3\"}",
       "result_digest": "{\"ok\": true, \"applied\": …}",   // ≤2KB, pre-clamped
       "segment": "leg-2"}
```

- Engine mode: emit right after each in-process execution — hook where the run-event
  stream already surfaces tool completion in `tool_generate_stream` (the same place
  tool headlines become `reasoning` frames today). Digests reuse `_clamp_tool_result`
  budgets; hard 2KB per digest, honest `"…truncated"` tail.
- Cloud mode: backend emits nothing new. `ToolLoopRunner.cs` executed the batch itself
  and holds args/result/duration — it synthesizes the identical `ToolResultEvent`
  locally before POSTing the resume. One rendering path, two producers.

### V2.3 confirm continuity — NO protocol change

Keeping one SSE open across a mutate-confirm means server-side parked generators and
a second unblock channel — rejected for this package (state, timeouts, Azure affinity).
Instead the ADDIN renders continuity: the confirm card mounts inside the current
turn thread, the thinking card freezes (no spinner reset), and the resume stream's
frames append to the SAME visual thread keyed by `run_id` (already on every terminal
frame). Teardown becomes invisible instead of absent.

## Addin work (revit-addin-sync)

### T1 — Segmented message model
`UI/Copilot/Model/CopilotModels.cs`: turn body becomes an ordered block list:
`Narrative(segment_id, text)` | `ToolCard(ToolResultEvent)` | `ConfirmCard` |
(thinking card stays a single block pinned first). `CopilotViewModel` routes
`OnReplyPartial(text, segment)` → append or new block. No segment → legacy path
byte-identical.

### T2 — Stream parser additions
`Services/AIServiceStreamParser.cs`: parse `segment` on `reply_partial`; new case
`tool_result` → typed `ToolResultEvent`. Unknown-field tolerance as today.

### T3 — ToolCard control
New `UI/Copilot/Controls/ToolResultCard.cs` (pattern: ReasoningTimelineView):
- Header row: tool name · duration (`0.4s`) · badge `✓`/`✗` (theme tokens from
  `CopilotTokens.xaml`) · expander chevron · copy button (copies result_digest).
- Body (collapsed by default, **auto-expanded on `ok:false`**): monospace,
  args then result, 2KB each, vertical scroll cap ~14 lines.
- Thinking-card dedupe: when a turn renders v2 tool cards, the reasoning strip
  suppresses its tool-headline rows (phases + notes stay) — one flag in
  `ReasoningReducer`, keyed off the same v2 feature-detect.

### T4 — Cloud-side ToolResultEvent synthesis
`Services/ToolLoopRunner.cs`: in the per-round `foreach (var c in turn.Pending)`
executor, wrap each execution with a stopwatch and raise the same `ToolResultEvent`
to the pane through the existing progress callback channel before the resume POST.

### T5 — Confirm continuity
`CopilotViewModel` + `ChatView.xaml.cs`: confirm card renders as an in-thread block
(T1 model); on approve/decline, resume-stream frames append to the same turn view-model
matched by `run_id`; thinking-card timer pauses during the confirm (freeze
`StartedUtc` accounting — do NOT count drafter decision time; the 457s lesson,
2026-08-18).

### T6 — Backend protocol emitters (bina-ai, small)
- Segment ids on `reply_partial` in both stream generators.
- `tool_result` events in the engine tool loop.
- Keep every new string inside the byte-stable-prefix rules (no timestamps in frames;
  DeepSeek prompt caching unaffected — these are output frames, not prompt).

## Acceptance (UAT on colocate box, then cloud)

1. "section box the view to level L3 only" renders live as: narrative → tool card
   (create_3d_view ✓ 0.5s) → narrative → tool card (set_section_box ✓) → narrative.
   No glued sentences, no single blob.
2. A failing tool renders its card auto-expanded with the real error text; the model's
   self-correction streams as the NEXT narrative block under it.
3. Mutate-confirm turn: thread visually continuous; thinking timer excludes decision
   time.
4. Old addin (≤ current release) against v2 backend: rendering identical to today.
   New addin against old backend: identical to today (no `segment` → legacy).
5. Frame overhead: `tool_result` ≤ 2.5KB; no additional model legs; turn wall time
   unchanged ±5%.
6. Standing 6-prompt UAT replay passes; smoke suite items 1-7.

## Gates & order

1. T6 backend first (additive, deployable alone — old addins ignore it).
2. T1-T5 addin behind one internal flag; Windows build + compile-gate on this Mac
   (net48 `ElementId.Value=int` trap noted).
3. Same-batch deploy backend+addin per standing rule; recipe ingest not involved.

## Explicitly deferred

- Single-SSE confirm (server parked-generator design) — revisit only if the T5
  illusion tests badly.
- WebView2 pane replacement — the real Hermes-class ceiling (markdown, virtualized
  scrollback, web tooling for free). Deserves its own package with migration plan
  for existing WPF cards; feat/copilot-pane-redesign learnings apply.
- Reasoning-tokens-on mode for demos (`-Thinking on` exists; 45-60s/leg).
