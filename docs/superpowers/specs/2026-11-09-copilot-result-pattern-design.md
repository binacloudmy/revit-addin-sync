# Copilot result pattern — design

Date: 2026-11-09 · Repo: addin-jumble (branch fix/copilot-jumble) · Prototype: https://claude.ai/artifact/UWY6ZyWVTPTEN9K3mMWzfJ · Design canvas: https://claude.ai/artifact/5iBpvvQpQdnQzcD2E6JvsM

## Intent

After a copilot turn, the drafter should see what changed, where, and from what, in the three places they already trust: the Revit view, the result card, and the source document. Replies are one sentence with counts taken from Revit. Writes apply without a confirmation card; every result has Undo.

Why: the 9 Nov 2026 demo replay showed the work was correct but the presentation was not usable: 128-word replies, assumptions buried in bullet three, duplicate footer controls, "Not searched" lines that read like errors, and no way to see the result without opening Type Properties by hand. Competitors (BIMLOGIQ Argus, Agent A) stop at text summaries and "✓ Done" lines; none show provenance, before/after, or the result in the model.

## Goals

1. One result pattern for every task kind: edits, created items (sheets, views, schedules), findings (QA/QC), selection.
2. The result is visible in the model: touched elements marked in the active view with callouts, cleared by Undo, Clear or a timer.
3. Every value shows its source (document page, prompt, rule, calculation).
4. Writes apply immediately. Undo is one click, one Revit undo step, with Redo.
5. Replies are short: one sentence, counts from Revit.
6. The panel's footer is quiet: steps collapse to one line, one Details toggle, one feedback control, one export, one save.

## Non-goals

- Changing the backend protocol (protocol 3) or tool contracts. Everything here is add-in side.
- Teaching content ("How to do this in Revit"). Dropped by product decision.
- Rendering the brochure as an image inside the card. Source cells are text; "View page" opens the page image.
- Perspective 3D leader lines. Rings still appear; leaders are skipped.

## Anti-goals (would make this a failure)

- A number in the reply that disagrees with Revit's status bar.
- A highlight that survives Undo, or an Undo that needs more than one Revit step.
- Model graphics changed by the highlight (overrides left in the view, extra undo entries).
- Any new confirmation step.

## The pattern

Every finished turn renders, top to bottom:

1. **Reply**: one sentence. Counts and names come from the tool envelopes, not from the model's prose. Language follows the prompt.
2. **Assumption note** (amber, optional): shown only when the copilot chose something the user did not specify (row, name, numbering). Carries one action to change it.
3. **Result card**: header with outcome and Undo; a body shaped by the result kind; a contextual action row.
   - `changes`: per type group, a Before → After table (parameter, before, after, source). For document-driven fills, a Source block above it: readable cells of the matched row and "View page" per page used.
   - `created`: list of created items (number, name, contents) with Open on each.
   - `findings`: issues grouped by rule (title, count, rule text) with Show and Fix per row, Fix all in the action row. Fixes are writes and apply without asking.
   - `selection`: type breakdown (type, count) with Select, Zoom, Remove per row.
4. **Footer**: time · "Details · N tools · N notes" toggle · thumbs up/down. Details holds steps with durations, tools, and notes such as "Link X is not loaded; fixtures inside it were not searched" (muted, not a warning).

Removed: the Rate banner, the second Export chip, the second Save as command, "searched host · Lighting Fixtures" lines, "N actions".

## In-model visualisation

Three layers.

**Rings** (in the Revit canvas): one small PNG control per touched element via `Autodesk.Revit.DB.TemporaryGraphicsManager`. States and colours: changed `#4CC77F` green; created or selected `#6B9BFF` blue; fixable issue `#FF7B6B` red; needs-you `#E7A84A` amber (dashed ring image); untouched not drawn. No transaction, no model change, follows pan/zoom/orbit in 2D and 3D. Clicking a ring selects the element; a second click on the same ring posts `PostableCommand.TypeProperties`. Rings are removed on Undo, Clear, view change, or when the fade timer ends (8 s, cancelled if the toast is clicked to keep).

**Overlay** (existing `HighlightOverlayWindow`, transparent, click-through): a toast at the top ("10 values written · 52 fixtures lit green · Undo · ×") and up to two callout cards docked along the bottom (type name, count, first three before → after rows, "Open Type Properties"). A leader line from each card to one ring of that group is drawn only in non-perspective views, using the projection below. The overlay hides while the view navigates and redraws when zoom corners are stable for one Idling tick.

**Projection** (for leaders): `UIView.GetZoomCorners()` gives two model-space corners of the visible rectangle; project the element's location and both corners onto the view's `RightDirection` and `UpDirection`; the ratios give x% and y% of `UIView.GetWindowRectangle()`. Valid for plan, ceiling, section, elevation and orthographic 3D. Skip in `View3D.IsPerspective`.

**Before / After** toggle on the overlay: Before hides rings and dims callout values; After restores them. For deletes, Before shows amber rings at the deleted elements' last positions.

## Before / After in the panel

Revit cannot screenshot its own dialogs. The card renders an Identity Data style table from the envelope's before and after values for the selected type, twice, and a drag handle compares them. Available for every `changes` result, not only when a dialog was open.

## Auto-apply

The W3 preview → confirm → commit flow keeps preview and commit but no longer waits for the user. The preview still feeds the result card (before values, counts). Rejections that today come from the user (No) can only come from the policy layer. The Undo chip and toast are the safety net.

## Constraints

- Revit API 2022+ is required for `TemporaryGraphicsManager`; on older targets rings fall back to the overlay's dot markers at projected positions.
- Overlay stays click-through except on its own controls.
- No new NuGet dependencies.
- Replies in the user's prompt language (remove the hardcoded English at `CopilotTurnRouter.cs:236`).
- Tests run in the scratch harness; WPF-free logic (projection, reply text, card models) is unit-tested; overlay drawing is verified live on the PC.

## Left to the builder

- Ring PNG size and exact drawing (18 px target at 100 % scaling).
- Debounce interval for the navigation redraw (start at 150 ms).
- How callout cards pick which ring to lead to (first element of the group in view).
- Which Revit `View` types count as "orthographic" beyond the listed ones.
