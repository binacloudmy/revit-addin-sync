# PRD — The Receipt: proof, replay and undo for every Copilot change

**Status:** DRAFT for review · 2026-09-01 · owner: product (Ashraf) · repos: revit-addin-sync (primary), bina-ai (metrics)
**Feature family:** turn receipts — the card that appears after every mutate: counts, before/after captures, [Tunjuk semula], [Undo].

## 1. Why now (evidence)

The receipt is the trust anchor of the whole product — "speak → preview → confirm → **verified** → one undo" ends here. Three UAT incidents show the loop is half-built:

| Date | Incident | Root cause |
|---|---|---|
| 2026-08-18 | [Tunjuk semula] rendered literally nothing from a schedule view | fire-and-forget job + non-graphical active view |
| 2026-09-01 AM | rooms receipt: "diserlah dan dizum" — canvas unchanged | re-show only re-selected; badges/tint cleared each turn; Rooms invisible without fill |
| 2026-09-01 AM | 3D-view receipt: same sentence — nothing to show at all | change set = view/cameras/sun path; no geometry; success claimed anyway |

Pattern: **the card claims more than it verifies.** Each fix closed one case; the family keeps producing. This PRD defines the feature properly so the next gap is a spec violation, not a surprise.

Known-but-unshipped defect found during this review (P0 below): the card's buttons act on a **global** "last receipt" — clicking [Tunjuk semula] or [Undo] on an *older* card silently operates on the *newest* change. Undo on the wrong card is destructive.

## 2. Users and the job

Drafters (JKR fleets, BM-first). Job: *"the AI just touched my model — show me exactly what, prove it, and let me take it back in one gesture."* The receipt is also the artefact a drafter screenshots to a checker — it must stand alone.

## 3. Principles (non-negotiable)

1. **Never claim what didn't render.** Every button's reply states what actually happened, from the tool's own result. (Shipped for re-show in PR #127; becomes the rule for all receipt actions.)
2. **A receipt is an object, not a moment.** Buttons act on *that* receipt's elements, forever — not on whatever happened last.
3. **Ground truth only.** Counts come from DocumentChanged transactions; the model never authors a receipt. (Already true — keep.)
4. **One gesture back.** Undo from the card reverts exactly that operation's transactions, tint included, or refuses honestly (e.g. later changes stacked on top).

## 4. Current state (what exists, verified in code)

- `TurnReceiptService`: DocumentChanged recording → `Epilogue` builds counts + category breakdown, flash/zoom + badges (TemporaryGraphicsManager) + green tint, before/after PNG capture (confirm-gated runs only).
- `ReceiptCard` (pane): counts headline, category line, Sebelum/Selepas thumbnails, [Tunjuk semula]/[Undo] via `RunReceiptJob` (awaited, 12 s timeout, outcome always surfaced).
- PR #127 (open): re-show re-runs full visuals; `ReceiptShape.DecideShow` (zoom / activate-view / honest refusal); pane prints the tool's own note/error.
- Backend: immutable `ai.operation_receipts` (0018) keyed by operation — **not yet joined** to the pane's receipt card.

## 5. Requirements

### P0 — correctness (target: v0.0.67-staging)
- **R1 Per-receipt identity.** Each `ReceiptModel` carries its element ids + tx names + operation_id. Buttons pass that receipt; service statics become a keyed store (session-lifetime). Undo disabled (greyed, with reason) on any receipt that is not the latest undoable operation.
- **R2 Honest actions everywhere.** `DecideShow` governs re-show (done); Undo verifies the undo stack still matches (tx count) before posting, else refuses with why. Merge PR #127 as the base.
- **R3 Feedback lives on the card.** Button outcomes render *inside* the card (status line under the buttons), not as new AI chat bubbles — the current "Perubahan diserlah…" spam pollutes the transcript and the rating signal.

### P1 — completeness (next staging round)
- **R4 Restart survival.** Receipts die with the session today. Persist the last N receipts (ids, counts, captures, operation_id) per document in `%LOCALAPPDATA%`; after restart, cards degrade honestly: re-show works (ids re-validated), Undo disabled ("resit dari sesi sebelum — Undo tidak tersedia").
- **R5 Category labels speak drafter.** "5 (lain)" is not a label. Map view/camera/sun-path/section-box to "Pandangan 3D & aksesorinya"; localize category names BM/EN per the reply-language rule.
- **R6 Capture quality.** Selepas capture of a just-created 3D view is near-white (no zoom-to-fit before export). Capture after zoom; skip captures that are >95% blank and show counts-only card instead.
- **R7 Job receipts.** A resumable job's stages each have receipts; the job's final card shows the stage list with per-stage re-show.

### P2 — trust surface (with the checker in mind)
- **R8 Export.** "Salin resit" copies a text/PNG summary (operation, counts, before/after, timestamp, operation_id) for WhatsApp/email to a checker.
- **R9 Cloud join.** Card links its operation_id to `ai.operation_receipts` so the same evidence is queryable server-side (audit trail; future compliance sign-off).

## 6. Non-goals
- No re-run/redo from the card (that is the saved-commands feature).
- No multi-receipt diffing or timeline scrubbing.
- No screenshots in the cloud receipt (privacy; ids + counts only).

## 7. Success metrics (Langfuse + telemetry)
- Re-show success rate (ok:true with a rendered action) ≥ 95%; refusals are counted separately, not as failures.
- Zero occurrences of the canned-success-over-no-op class (report as `receipt_dishonest` event = tool ok:false with success text — should be structurally impossible after R3).
- Undo-from-card usage and undo-wrong-target reports (target: 0 after R1).
- Thumbs-down rate on turns with a receipt vs without (receipt turns should be better, not worse).

## 8. Rollout
1. Merge PR #127 (base honesty) → v0.0.67-staging.
2. R1+R3 same release if feasible (both are pane+service, no backend).
3. R4–R7 next round; R8–R9 after UAT feedback.
4. UAT script: the three incident scenarios above + old-card undo + restart replay.

## 9. Open questions
- Q1 Undo depth: allow undoing an older receipt when the stack still lines up (posts N undos), or hard-limit to latest? (Product lean: latest-only, simplicity beats cleverness near destructive actions.)
- Q2 Persist captures (PNGs) across restart or counts-only? (Disk + privacy trade-off.)
- Q3 Should the checker export (R8) be gated behind JKR tenant config?
