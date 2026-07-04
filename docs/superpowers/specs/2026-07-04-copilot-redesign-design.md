# BINA AI Copilot — 1:1 WPF redesign

**Date:** 2026-07-04
**Source of truth:** Claude Design project "BINA AI Copilot" (f07d78be-b653-4382-8477-928e479999b4), file `BINA AI Copilot.html` — extracted markup + component logic archived at `docs/superpowers/specs/assets/copilot-design/` (design-markup.html, design-logic.js).
**Goal:** Restyle the entire Copilot WPF UI to match the design 1:1 — chat, history, usage/plan, feedback, and all legacy screens — keeping the existing ViewModel, services, streaming and codegen logic intact.

## Approach (approved)

Token restyle + rebuild of chat surfaces. `CopilotViewModel`, `RevitChatRouter`, `RevitCopilotExecutor`, feedback services and the screen state machine are untouched. `CopilotTokens.xaml` gets the design palette; visually divergent surfaces (chat messages, empty state, composer, thinking line, meter, sheets) are rebuilt; the remaining screens (Library, ToolForm, ToolReview, Running, Result, Saved) inherit the new tokens plus spot alignment fixes.

Branch: `feat/copilot-redesign`.

## 1. Theme tokens

Mechanism unchanged (`CopilotTheme` in-place brush mutation, light/dark, prefs persistence). New palette:

| Token | Light | Dark |
|---|---|---|
| Cp.Bg | #FFFFFF | #131D2B |
| Cp.Sunken | #F3F6F9 | #0C1420 |
| Cp.Menu | #FFFFFF | #1A2433 |
| Cp.Text (–text) | #131C2B | #E8EEF6 |
| Cp.Muted (–text2) | #586273 | #8A94A6 |
| Cp.Faint (–text3) | #99A3B3 | #6B768A |
| Cp.Line (–hair) | rgba(15,27,45,.08) | rgba(255,255,255,.07) |
| Cp.Hair2 | rgba(15,27,45,.16) | rgba(255,255,255,.14) |
| Cp.Hover | #F3F6F9 | rgba(255,255,255,.05) |
| Cp.Accent | #1D4ED8 | #60A5FA |
| Cp.AccentContrast | #FFFFFF | #0C1420 |
| Cp.Green | #10B981 | #34D399 |
| Cp.UserBubble | #EEF1F5 (text #131C2B) | #222E40 (text #E8EEF6) |

- `Cp.AccentGrad`: 135° linear, `mix(accent 60%, white) → accent` light; `mix(accent 78%, white) → accent` dark.
- Meter color ramp: accent, `#F59E0B` ≥80%, `#EF4444` ≥95%.
- Gold star gradient: `#FFE07A → #FBB72B → #E8941A`, stroke `#E8941A`.
- Panel stays fluid-width (Revit pane is resizable); design's fixed 360px is the mock frame, not a constraint. All paddings/font sizes/radii below are 1:1.
- Typography: existing Geist stack; design weights 500/550/600/650/680/720, letter-spacing −0.01/−0.02em on titles.
- Radii: sheets 18 top, cards 13–15, buttons 8–10, bubbles 14/14/4/14.

## 2. Header + tabs

- Header: 24px BINA star logo (gradient `#7DD6FF → #3B8EF7 → #1D5FE0` + radial sheen, drop shadow), title 14px/680, status row: 6px accent dot + "Connected · {ModelName}" 11px muted.
- Right cluster: theme toggle (sun/moon), new-chat (+), kebab (⋮). 26×30 hover-rounded buttons.
- Kebab menu (188px, menuPop animation): "Rate Copilot" (star icon), "Report a bug" (bug icon).
- Header credit pill is REMOVED (meter moves to footer, §4).
- Tabs: `Chat` and `History {count}` — 38px high, active = 620 weight + 2px accent underline; idle = faint.

## 3. Chat surface

### Empty state
Centered: 50px hero star (same gradient, glow drop-shadows) + two satellite stars (17px top-right, 13px bottom-right); "How can I help with your model?" 19px/700; sub "Describe what you need in plain words — I'll turn it into a Revit command you can review and apply." 12.5px muted, max 268px. Below, full-width hairline-divided suggestion rows (13px/550, leading 18px icon, trailing chevron): **Create walls** (wall grid icon), **Generate schedule** (table icon), **Tag rooms** (tag icon). Row click sends the mapped prompt.

### Messages
- **User**: right-aligned bubble (max 84% width), `Cp.UserBubble`, padding 9×13, radius 14/14/4/14, 13px/1.5. Text taller than 80px clamps with a bottom fade mask and a `Show more`/`Show less` toggle (12px/650 + chevron). Time 10px faint under bubble, right-aligned.
- **AI answer**: NO bubble — plain 13px/1.6 text, full width, left-aligned.
- **Command card** (inside AI message): top hairline separator; header = command name 12.5px/680 + status suffix `· Proposed` (accent) / `· Applied` (green) / `· Dismissed` (faint); param rows key (11.5px faint, left) / value (11.5px/550 muted, right); Proposed → `✓ Apply to model` gradient button (8px radius, 11.5px/600) + `Dismiss` ghost; Applied → green check + "Applied to the model".
- **Rating nudge** after latest Applied card (once, dismissible, hidden after any rating submit): sunken chip — gold star, "How's Copilot doing?", `Rate` accent link, × dismiss.
- **Interrupted**: italic faint line with stop-in-circle icon, text "Interrupted."
- **Micro feedback row** under every AI answer: time (10px faint) … `Was this helpful?` (10.5px faint) + 👍 👎 copy (27×27 icon buttons). 👍 = accent highlight toggle (silent). 👎 = accent highlight + panel: "What was off?" + chips `Not accurate / Wrong elements / Too slow / Other` (single-select toggle), optional note textarea, `Send feedback` gradient + Cancel, footer "Auto-attached · {command} · Copilot {ver} · Revit {ver}". Submit → "Thanks — your feedback helps improve BINA." accent line. Copy → clipboard + green check for 1.6s.
- Entry animation: rise 7px + fade (350ms cubic-bezier(.2,.7,.3,1)) — implement WITHOUT WPF Storyboards on the Revit-hosted pane (existing constraint: storyboards crash Revit dockable panes; use DispatcherTimer/CompositionTarget tick like existing code).

### Thinking indicator (replaces ThinkingTrailView's multi-row trail)
ONE status line: 19px star + 15px area = spinner (accent, 0.7s rotate) while working / popping check (scale bounce) on done; label 12.5px/600 with shimmer (gradient text sweep, 2s loop), label swaps per progress event with rise animation. Event mapping (existing `RevitChatRouter.OnProgress` keys → friendly labels): thinking→Thinking; parse_request/understand→Understanding your request; retrieve_context/search_model/read_model→Looking through the model; plan→Planning the approach; reason→Reasoning it through; generate/compose→Putting together a response; build_command→Preparing the command; validate/verify→Double-checking the result; unknown keys → humanised (snake/camel → Title case). done→Done + check, then 260ms fade-out cross-fading with the answer. Stop button interrupts → "Interrupted." message.

### Composer
- Sunken rounded-13 field: borderless textarea `Ask Copilot…` (13px, max-height 96), @ button (30×30, hover accent), send button 32×32 radius 9: idle = transparent + faint ↑; active = accent-grad + contrast ↑; generating = accent-grad ■ stop.
- Mention popover above field (menuPop): "REFERENCE" header 10px caps, rows = 22px sunken @-tile + label 12.5/550 + type 10px faint. Sources: existing `RevitMentionProvider` (Levels/Categories/Views/Selection).
- Hint line under: "Type **@** to reference a level, category, view, or selection" 10px faint centered.

## 4. Usage / plan (new)

New `IUsageService` abstraction; stub implementation (`StubUsageService`) with configurable plan (`Free`/`Basic`/`Pro`), usage %, atLimit, isAdmin; real adapter later maps `AIService.GetCreditsAsync` (% = Used/Limit) + plan from backend when available.

- **Footer meter** (chat tab only, above hint line): plan name 10.5/600 muted — slim 4px bar (track hair, fill meter-ramp color) — `{pct}%` 10.5/680. Click → usage popover.
- **Usage popover** (anchored above meter, menuPop): plan name, "Plan usage", `Usage — {pct}% used`, 6px bar, `⚡ Upgrade plan` gradient button 32px.
- **Upgrade bottom sheet** (scrim rgba(6,10,18,.5) fadeIn + sheetUp 240ms): title "Choose your plan" 15/720 + "Swipe to compare" + ×. **Peek carousel**: cards 82% viewport width, gap 12, active centered scale 1 / neighbors 0.9 @ 45% opacity, drag with pointer capture (threshold 16% width) + arrow buttons + animated dots (active 18px pill). Cards: Free $0 (outline CTA "Get started") / Basic $20 (1.5px accent border, RECOMMENDED gradient pill, solid CTA "Upgrade to Basic" ↗) / Pro $40 (solid CTA "Upgrade to Pro" ↗); feature checklists (accent checks on recommended, faint otherwise); price 25/780 + "/month". Inactive card CTA = disabled sunken. Footer "See all plans" → opens `https://bina.cloud/pricing`; CTA → `https://billing.bina.cloud/upgrade`.
- **Limit reached** (chat, atLimit, replaces composer; centers in body when thread empty): 68px 3-D padlock illustration (blue gradients, exact SVG from design), "You've reached your usage limit" 14/680, "Upgrade your plan to keep using Copilot." Admin → `⚡ Upgrade plan` gradient CTA (opens upgrade sheet). Member → "Your plan is managed by your workspace admin." + admin card (avatar initials, name, "Workspace admin · email") + `🔔 Notify admin to upgrade` → confirmation "Request sent. {Admin} has been asked to upgrade your plan."
- **Warnings**: none. The design computes 80/95% flags but renders no banner markup (leftover from an older iteration) — out of scope; the meter color ramp (amber ≥80, red ≥95) is the only warning signal.

## 5. Feedback surfaces (restyle existing)

- **Star rating sheet** (kebab → Rate, or nudge → Rate): "How's Copilot working for you?" 15.5/720, "Your rating helps us improve.", 5× 32px stars — hover scale 1.18, gold gradient fill + drop-shadow when active, starPop bounce (360ms) on pick; reaction label 13/660 `#E0941A`: Not great / Could be better / It's okay / Pretty good / Love it!; note textarea "What worked well, or what could be better?"; sunken context row "Copilot {ver} · Revit {ver}"; `Submit rating` 40px (disabled sunken until a star picked) → thanks state ("Thanks for the feedback", Done). Wire to existing `LocalFeedbackService.SubmitRating` + `CopilotPrefs.RatingSubmitted`.
- **Report/feedback sheet** (kebab → Report a bug): title "Report a bug"; TYPE chips Bug/Suggestion/Other (single-select, accent-tint active); DETAILS textarea "Describe what happened or what you'd like to see…"; context row "Auto-attached · Copilot {ver} · Revit {ver} · current view"; `➤ Submit` 40px gradient → thanks ("Thanks for letting us know", "Your report was sent to the BINA team with the current model context attached.", Done). Wire to `LocalFeedbackService.ReportBug`.
- Auto-attached context strings come from real values: addin version, Revit version, active command name where applicable.

## 6. Legacy screens

Library, ToolFormView, ToolReviewView, RunningView, ResultView, SavedView: inherit new tokens; alignment pass — radii to 13/10/8, hairline borders, new typography weights, all primary buttons → accent-grad + contrast, secondary → ghost/outline per design idiom. No structural/flow changes. RunningView's step list may adopt the single-line thinking style where it uses the old trail.

## 7. Error handling & edge cases

- Theme switch mid-thread: all rebuilt surfaces use `DynamicResource`/theme-change re-render like existing code.
- Progress events arriving after Stop / new-chat: generation-id guard (mirror design's `_genId` stale-timer pattern; existing router already cancels — verify).
- Clipboard failures: silent catch (design behavior).
- Usage service unavailable: meter hidden, composer normal (never block on stub errors).
- Long param values in command card: right-aligned, wrap allowed.
- Revit pane constraint: no WPF Storyboards; timers/manual composition only (existing rule).

## 8. Testing & verification

- Build on Mac: `dotnet build` with official SDK (UiHarness + addin TFMs already `EnableWindowsTargeting`).
- Unit: friendly-step label mapper; meter ramp thresholds; usage stub plan/limit flags; show-more clamp measurement logic where extractable.
- Visual: extend `HarnessShots` with states — empty light/dark, thread with proposed+applied command, thinking line, downvote panel, rating sheet, report sheet, upgrade carousel (3 positions), blocked admin/member, footer meter 22/88/97%. Run `UiHarness --shot` on Windows; compare against design renders (archived screenshots).
- In-Revit smoke: theme toggle, streaming answer, apply command, feedback submit.
