# Handoff: BINA AI Copilot — Streaming Reasoning ("Thinking") UI

## Overview
The BINA AI Copilot panel docked inside Autodesk Revit currently shows only the final answer plus repeated "Sahkan tindakan" confirmation cards. Users want to see *what the Copilot is thinking* while it works — DeepSeek-style: a small, muted, collapsible reasoning stream above a normally-sized final answer.

This handoff covers:
1. A streaming **reasoning block** (live, small text, auto-collapsing).
2. A rebuilt **approval card** (one card, numbered write-steps, undo).
3. A **result card** summarising what changed in the model.
4. A refreshed panel shell (header, tabs, composer).

## About the Design Files
The `.dc.html` files in this bundle are **design references created in HTML** — prototypes showing intended look and behaviour, not production code to copy. Recreate them in the target codebase's existing environment (the Copilot panel is presumably a WebView2/React or WPF surface inside a Revit add-in) using its established component patterns, state layer and styling approach. If no front-end environment exists yet, pick the most appropriate framework and implement there.

Each file opens directly in a browser. They contain a self-contained fake stream (timers) purely to demonstrate timing and motion — real implementation must bind to the actual token/SSE stream from the model backend.

## Fidelity
**High fidelity.** Colours, type, spacing, radii, motion timings below are final and should be matched closely. Copy is in Malay/English mix matching the product's existing voice — keep it.

`BINA Copilot Thinking v2.dc.html` is the **canonical design**. `v1` is an earlier, plainer pass included only for reference; ignore it if in doubt.

## Screens / Views

There is one view: the docked Copilot side panel. Design width ≈ 440–760px (fluid); height fills the dock. Vertical stack:

```
┌ Header (fixed)            ~55px
├ Tab bar (fixed)           ~45px
├ Conversation (scrolls)    flex:1
└ Composer (fixed)          ~110px
```

Root: `background #ffffff`, `border-left 1px solid #e7e7ea`, font `Instrument Sans`, colour `#0b0b0f`.

### 1. Header
- Padding `13px 16px`, bottom border `1px solid #f0f0f2`, flex row, gap `11px`.
- Avatar: 28×28, radius 9px, `linear-gradient(150deg,#6366f1,#4f46e5 55%,#7c3aed)`, shadow `0 2px 6px rgba(79,70,229,.28)`, white `◈` glyph 13px (replace with the real BINA mark).
- Title "BINA Copilot" — 14px / 600 / letter-spacing -0.015em.
- Subline: 5px green dot `#10b981` with halo `0 0 0 3px rgba(16,185,129,.14)`, then `JetBrains Mono` 10.5px `#8b8b95` — "Main Model · Snowdon Towers" (model + active document name, live).
- Right: three 29×29 icon buttons (theme, new chat, overflow), radius 8px, colour `#8b8b95`, hover `background #f5f5f7; color #0b0b0f`, transition `all .15s`.

### 2. Tab bar
- Segmented control: container `background #f4f4f6`, radius 9px, padding 3px. Active pill `background #fff`, radius 7px, shadow `0 1px 2px rgba(11,11,15,.07)`, 12.5px/600. Inactive `#6e6e78` 12.5px. Tabs: Chat / History / Library.
- Right side: "↻ Replay" ghost button (demo only — in production this slot should hold something real or be dropped).

### 3. Conversation
Padding `20px 16px 28px`, `display:flex; flex-direction:column; gap:22px`, custom scrollbar (11px, thumb `#d9d9df`, radius 99px, 4px white border).

**User message** — right-aligned, max-width 80%, `background #0b0b0f`, white text, radius `14px 14px 5px 14px`, padding `10px 14px`, 13.5px/1.5.

**Reasoning block (the core feature)**
- Container: `border 1px solid #ebebef`, radius 13px, `background #fcfcfd`, `overflow:hidden`.
- Header row (click toggles): padding `10px 13px`, gap 9px, `cursor:pointer; user-select:none`, hover `background #f6f6f8`.
  - While streaming: 12×12 spinner — `border 1.5px solid #e0e0e6`, `border-top-color #4f46e5`, radius 50%, `animation: spin .7s linear infinite`.
  - When done: `✦` 11px `#a5a5ae`.
  - Label 12.5px/500 `#4a4a55`: streaming → `Berfikir…  {elapsed.toFixed(1)}s` (timer ticks every 100ms); done → `Berfikir {seconds}s`.
  - When done also: mono 10px badge `{n} langkah` on `#f1f1f4`, radius 5px, padding `2px 6px`.
  - Far right: chevron `▲`/`▼`, 9px, `#a5a5ae` (swap for a real icon).
- Body (only when expanded): top border `1px solid #f2f2f5`, padding `2px 14px 14px`, steps stacked with `gap:14px`.
  - Each step is a 2-column grid `14px 1fr`, gap 10px:
    - Column 1: 6px dot (accent `#4f46e5` for the first step, `#eab308` for an approval-related step, otherwise `#a5a5ae`), `margin-top:5px`, then a 1px vertical rule `#eeeef1` filling remaining height.
    - Column 2: mono label 10px, `letter-spacing .06em`, uppercase, `#a5a5ae`; body 12.5px / line-height 1.7 / `#7a7a86`, `white-space:pre-wrap; text-wrap:pretty`.
  - The currently-streaming step appends a caret: 5×11px `#a5a5ae`, `animation: bk 1s step-end infinite` (blink keyframe: `0%,45%{opacity:1} 50%,100%{opacity:0}`).
- **Behaviour**: expanded and streaming by default; **auto-collapses** the moment the final answer begins — unless the user has toggled it manually during the turn (then respect their choice). Re-expandable at any time, including on completed historical turns (persist the reasoning text with the message).

**Approval card** (replaces the three duplicate "Sahkan tindakan" cards in the current build — one card per turn, all steps inside it)
- `border 1px solid #ecebf0`, radius 13px, white, shadow `0 1px 2px rgba(11,11,15,.04), 0 8px 24px -12px rgba(11,11,15,.14)`, entrance `rise .28s cubic-bezier(.2,.8,.2,1)` (`from{opacity:0;translateY(8px)}`).
- Head: padding `10px 13px`, bottom border `1px solid #f3f3f6`, `background linear-gradient(180deg,#fffdf8,#fff)`; 18×18 amber badge (`#fef3c7` bg, `#a16207` "!"), title "Perlu kebenaran" 12.5px/600, right mono `2 WRITES`.
- Step rows: grid `auto 1fr auto`, padding `8px 9px`, radius 9px, hover `#fafafb`. Mono index `01/02` 10px `#b4b4bd`; label 13px; right-hand mono metric 10.5px `#8b8b95` (`1,053`, `3 sistem`).
- Actions: primary "Benarkan" — `#0b0b0f` bg, white, 12.5px/500, padding `8px 14px`, radius 9px, hover `#26262e` + `translateY(-1px)`; includes mono `⌘⏎` hint at 55% opacity (bind the shortcut). Secondary "Tolak" — 1px `#eaeaee` border, hover `#f5f5f7`. Right: "Boleh undo" 11.5px `#a5a5ae`.

**Answer** — plain block, 14px / line-height 1.68 / `#15151b`, `letter-spacing -0.008em`, `white-space:pre-wrap; text-wrap:pretty`. Blinking 7×14px black caret while streaming. Deliberately *larger and darker* than reasoning text — that contrast is the whole point of the feature.

**Result card** (after a successful model write)
- `border 1px solid #ebebef`, radius 13px. Head: mono uppercase 10px view name + right-hand total (`1,053`).
- Rows: grid `11px 84px 1fr auto`, gap 10px, gap-y 9px — 11px colour swatch (radius 3px), label 12.5px, 5px proportion bar (track `#f0f0f3`, radius 99px, fill in the system colour), mono count 11px `#6e6e78`.
- Colours: Supply `#2563eb`, Return `#dc2626`, Exhaust `#eab308`, unassigned `#d4d4da`.
- Follow-up chips: 12.5px, 1px `#eaeaee` border, radius 9px, padding `6px 11px`, hover `background #f5f5f7; border-color #dededf`.
- Feedback row: 11.5px `#a5a5ae` — Berguna? / ↑ / ↓ / ⧉, right-aligned mono total `{t}s · 2 tindakan`. (Belongs under the answer, once per turn — the current build shows it above.)

### 4. Composer
- Card: `border 1px solid #e6e6ea`, radius 14px, white, shadow `0 1px 2px rgba(11,11,15,.05), 0 10px 24px -16px rgba(11,11,15,.25)`, padding `11px 12px 9px 13px`. Container above it has top border `#f0f0f2` and `linear-gradient(180deg,#fff,#fcfcfd)`.
- Placeholder 13.5px `#a5a5ae`: "Tanya Copilot atau taip / untuk tools…"
- Tool row: 26×26 icon buttons (attach, @-reference), a "Reasoning" toggle chip (5px `#4f46e5` dot + 11.5px label) controlling whether reasoning is streamed/shown, and a 28×28 send button `#0b0b0f`/white, radius 9px.
- Hint row: mono 10px `#b4b4bd` — "@ level · view · selection" left, "BINA-1 · Free" right.

## Interactions & Behavior
- **Turn lifecycle**: `thinking → action (awaiting approval) → answering → done`. Reasoning streams during `thinking`; the approval card appears when the first tool call is proposed; the answer streams after approval/tool completion.
- **Auto-scroll**: stick-to-bottom flag, default true. On every token tick, if sticky, `scrollTop = scrollHeight`. An `onScroll` handler sets sticky `= scrollHeight - scrollTop - clientHeight < 40`, so scrolling up to read frees the view and scrolling back re-pins. Also re-pin after the reasoning block collapses/expands (layout height changes without a token). *(This was a real bug in the first pass — a one-shot distance threshold latches off and never re-pins. Don't repeat it.)*
- **Timers**: elapsed reasoning seconds shown to 0.1s while streaming, rounded when collapsed; total turn duration in the feedback row.
- **Approval**: ⌘⏎ / Ctrl+Enter approves; Esc rejects. Buttons disable after a decision and the card should render its resolved state (approved/rejected) in history.
- **Motion**: entrances `rise .28s cubic-bezier(.2,.8,.2,1)`; hovers `all .15s`; spinner `.7s linear`; caret blink `1s step-end`. Respect `prefers-reduced-motion` — drop the rise and blink, keep the spinner or swap for static text.
- **Streaming cadence in the prototype**: reasoning ~4 chars / 15ms, answer ~3 chars / 17ms, 850ms pause between reasoning end and answer start. Real streams are token-based — buffer to ~1 rAF per paint rather than re-rendering per token.
- **Empty/edge**: if the model returns no reasoning, omit the block entirely (don't render an empty collapsed shell). If reasoning is very long, cap the expanded body at ~40vh with internal scroll.

## State Management
Per assistant turn:
- `phase: 'thinking' | 'action' | 'answering' | 'done' | 'error'`
- `reasoningSteps: { label, text, dotColor }[]` — appended to as the stream produces step boundaries; the backend should emit step labels, or the client segments on blank lines.
- `answerText: string`
- `reasoningOpen: boolean`, `userToggled: boolean` (suppresses auto-collapse)
- `elapsedReasoningMs`, `elapsedTotalMs`
- `pendingActions: { index, label, metric }[]`, `approvalState: 'pending' | 'approved' | 'rejected'`
- `result: { viewName, total, rows: { label, colour, count }[] }`
- Panel-level: `stickToBottom: boolean` (a ref, not state — it must not trigger re-renders).

Data: SSE/websocket stream from the model backend carrying `reasoning` and `content` deltas separately; tool-call proposals gated on user approval; Revit-side execution returns the result summary.

## Design Tokens

Colour
| Token | Hex |
|---|---|
| ink | `#0b0b0f` |
| ink-hover | `#26262e` |
| text-primary | `#15151b` |
| text-secondary | `#4a4a55` |
| reasoning-text | `#7a7a86` |
| text-muted | `#8b8b95` |
| text-faint | `#a5a5ae` / `#b4b4bd` |
| border | `#e6e6ea` / `#eaeaee` / `#ebebef` |
| border-subtle | `#f0f0f2` / `#f3f3f6` |
| surface | `#ffffff` |
| surface-sunken | `#fcfcfd` / `#f4f4f6` |
| hover | `#f5f5f7` / `#fafafb` |
| accent | `#4f46e5` (grad `#6366f1 → #4f46e5 → #7c3aed`) |
| success | `#10b981` |
| warn-bg / warn-fg | `#fef3c7` / `#a16207` |
| system-supply | `#2563eb` |
| system-return | `#dc2626` |
| system-exhaust | `#eab308` |
| system-none | `#d4d4da` |

Type — `Instrument Sans` 400/500/600 UI; `JetBrains Mono` 400/500 for metadata, counts, timers, IDs. Scale: 14 answer · 13.5 user msg / composer · 13 list rows · 12.5 UI labels & reasoning body · 11.5 tertiary · 10–10.5 mono meta. Tracking: -0.015em titles, -0.008em answer, -0.005em body.

Spacing — 4 / 6 / 8 / 9 / 10 / 11 / 13 / 14 / 16 / 20 / 22px. Radius — 3 (swatch) / 5 / 7 / 8 / 9 (buttons) / 13–14 (cards) / 99 (bars, dots).

Shadow — card `0 1px 2px rgba(11,11,15,.04), 0 8px 24px -12px rgba(11,11,15,.14)`; composer `0 1px 2px rgba(11,11,15,.05), 0 10px 24px -16px rgba(11,11,15,.25)`; segmented pill `0 1px 2px rgba(11,11,15,.07)`.

## Assets
- Fonts: Instrument Sans + JetBrains Mono (Google Fonts). Self-host in the add-in — the WebView may be offline.
- Icons: the prototype uses text glyphs (`◈ ☾ ＋ ⋯ ✦ ▲ ▼ ↑ ⧉ @`) as placeholders. Replace with the product's icon set; BINA logo mark to come from brand assets.
- `current-state-revit-panel.png` — screenshot of the existing panel in Revit, for before/after context.
- No images in the design.

## Files
- `BINA Copilot Thinking v2.dc.html` — **canonical design**, full panel with live streaming demo.
- `BINA Copilot Thinking v1.dc.html` — earlier simpler pass (single reasoning paragraph rather than a step timeline).
- `current-state-revit-panel.png` — current production UI.

Open the HTML files in any browser; they replay a full turn on load and via the Replay button.
