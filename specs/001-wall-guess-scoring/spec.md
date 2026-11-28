# Feature Specification: Wall guess confidence and ranked questions

**Feature Branch**: `ai-629-spec`

**Created**: 2026-11-28

**Status**: Draft

**Input**: User description: Linear AI-629 (CAD to BIM v1, epic AI-621). "The detection engine emits pass/fail geometry. Nothing records 'this was a guess, here is why, and here is how bad it is if wrong', which the ranked-question model needs." Add a confidence record per detected wall (uncertainty, basis, items affected, cost to fix later), one central scoring step that ranks candidate guesses into questions, wire it into four wall heuristics (layer-hint mismatch, defaulted wall thickness, defaulted type, unjoined ends), keep the basis text with the wall for hover evidence and Review copy, and keep it all deterministic with no AI. Screens 6 and 7; blocks G9 and G11. Full ticket text: `/tmp/ai629-ticket.md`.

## Clarifications

### Session 2026-11-28

Non-interactive run: nobody answered. Each question below was resolved with its recommended default, which the rest of this spec now uses. They stay listed under "Open questions" for the product owner to confirm or overturn.

- Q: What fixed uncertainty, cost level and cost weight does each wall heuristic get? → A: Defaulted type 0.6 / High; defaulted thickness 0.5 / Medium; layer-hint mismatch 0.4 / Medium; unjoined ends 0.3 / Low. Weights Low 1, Medium 2, High 3.
- Q: How close must a wall's thickness be to a wall type's thickness to count as a match, and how are thicknesses grouped into one question? → A: Group by thickness rounded to the nearest 5 mm; a type matches when its thickness is within 2.5 mm of the group value.
- Q: Is "unjoined ends" one question per wall, or one per drawing? → A: One question per plan sheet ("N walls have an end that meets no other wall"); items affected = walls with at least one loose end.
- Q: Where does confidence live for "the session store (G6 contract)"? → A: In the existing in-memory CAD to BIM session, carried across re-detect by centreline; no disk or server persistence until G6 is written.
- Q: Does read_dwg_plan (the bina-ai tool) return confidence in this ticket? → A: No. Its contract is unchanged; exposing confidence to bina-ai is a paired change in both repos, planned separately.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Know which wall guesses matter most (Priority: P1)

A drafter detects walls on a real drawing (ASWARA). Besides the walls themselves, the engine says which walls it guessed about and puts those guesses in order: the guess that would cost the most to get wrong, across the most walls, comes first. On ASWARA the first question is about the 175 mm walls that have no matching wall type. The type check needs a wall-type catalogue. In this ticket the catalogue is supplied by tests and the headless harness. The pane gets one in a follow-up (see Assumptions), so until then the pane ranks the other three guesses only.

**Why this priority**: The ranked-question model (G11) and the Review screens (6, 7) need an ordered list of guesses. Without it, every detected wall looks equally certain.

**Independent Test**: Run detection on ASWARA (env-gated real-sheet test) with a known wall-type catalogue and check that the ranked list is non-empty, ordered by score, and that its first item is the "thickness 175 mm has no matching type" guess.

**Acceptance Scenarios**:

1. **Given** the ASWARA drawing and a wall-type catalogue with no 175 mm type, **When** walls are detected and guesses are ranked, **Then** the first ranked question is the defaulted-type guess for 175 mm walls, and every question's score is greater than or equal to the next one's.
2. **Given** two guesses with the same score, **When** they are ranked, **Then** they come out in the same documented order on every run.
3. **Given** a drawing where no heuristic fires, **When** guesses are ranked, **Then** the list is empty and no wall carries a guess.

---

### User Story 2 - Read why a wall is a guess (Priority: P1)

For every guessed wall, the engine keeps a short, plain-language reason ("basis") that the canvas can show on hover and the Review screen can copy as-is, e.g. "thickness 175 mm has no matching type" or "found on layer 0, not a wall layer".

**Why this priority**: The basis is the evidence the drafter acts on. The canvas design forbids confidence percentages, so the basis is the only way a guess explains itself.

**Independent Test**: Unit-test each heuristic on a small synthetic wall set and assert the exact basis string.

**Acceptance Scenarios**:

1. **Given** a wall whose faces lie only on layers with no wall role, **When** confidence is computed, **Then** its basis names the layer and says it is not a wall layer.
2. **Given** any guess, **When** its basis is read, **Then** it contains no percentage, no "%", no probability wording and no 0–1 number.
3. **Given** a wall with no guesses, **When** its basis is read, **Then** it states what proved it (e.g. "two faces 115 mm apart on layer A-WALL").

---

### User Story 3 - Guesses survive the session (Priority: P2)

The confidence of each wall stays with that wall while the drafter works: after erasing, brushing, re-detecting the same drawing or confirming a build, the guesses for walls still in play are unchanged and available to later steps (Review, G11).

**Why this priority**: The confidence data is useless to Review if it is lost between detect and Confirm.

**Independent Test**: Session unit tests: set walls with confidence, erase, re-detect, check the confidence for surviving walls.

**Acceptance Scenarios**:

1. **Given** a session with detected walls and their confidence, **When** the same drawing is re-detected, **Then** each wall that matches a previous wall by centreline keeps a confidence record equal to the freshly computed one, and walls that no longer exist drop theirs.
2. **Given** a guessed wall the drafter erased, **When** ranked questions are read, **Then** that wall no longer counts towards any question's items affected.

---

### User Story 4 - No AI in the numbers (Priority: P1)

Every uncertainty, basis, items-affected count, cost and score is computed by fixed engine rules and is the same whether AI features are on or hard-disabled, offline included.

**Why this priority**: AI scope note on the ticket (2026-11-27): AI never estimates, adjusts or ranks a score.

**Independent Test**: The engine has no AI switch and takes no AI input (the only AI setting in the add-in, `AIBaseUrl`, is never read by the CAD to BIM engine). So "AI enabled vs hard-disabled" is proven in two parts: (a) a fixture computes the ranked questions twice from scratch, under two cultures, and compares them for exact equality; (b) a source check shows the scoring code references no network or AI component.

**Acceptance Scenarios**:

1. **Given** the same walls and inputs, **When** scoring runs twice from scratch, **Then** the ranked order and every basis string are identical. Because the scoring code has no AI or network reference, this is the same result as AI hard-disabled.
2. **Given** no network, **When** scoring runs, **Then** it completes with the same result.

---

### Edge Cases

- A wall triggers more than one heuristic (e.g. no matching type and an unjoined end): it carries one confidence record per guess, and its most serious guess is the one shown first on hover.
- Equal scores: ties break by cost to fix later (H before M before L), then items affected, then heuristic order (defaulted type, defaulted thickness, layer-hint mismatch, unjoined ends), then the guess key in ordinal order.
- No wall-type catalogue supplied: the defaulted-type heuristic does not fire (it cannot tell), and the rest still runs.
- A drawing with no wall-named layer at all (engine already warns "walls were paired from every layer"): every wall would be a layer-hint mismatch; they are grouped into one question, not hundreds.
- Thicknesses are grouped to the nearest 5 mm (174.6 and 176.0 are both the 175 mm group); a catalogue type matches a group when it is within 2.5 mm.
- Brushed walls (single-line or box) get a thickness from the drawing's median, not from two faces: they are defaulted-thickness guesses.
- Very large sheets (elec.dwg, hundreds of loose ends): scoring adds no noticeable time to detection.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Each detected wall MUST be able to carry zero or more confidence records, each with: uncertainty (a number from 0 to 1), basis (text), items affected (whole number ≥ 1), cost to fix later (Low, Medium or High) and which heuristic produced it.
- **FR-002**: The basis MUST be a filled template from a fixed set (one template per heuristic plus a "proven" template), with only measured facts substituted (thickness in whole millimetres, layer name, count). It MUST NOT contain a percentage or a probability.
- **FR-003**: A single scoring component MUST compute `score = uncertainty × items_affected × cost weight` for each candidate guess, where the cost weight is a fixed number per cost level.
- **FR-004**: The scoring component MUST group guesses that ask the same question (same heuristic and same key) into one ranked question whose items affected is the number of active walls in the group. Keys: defaulted type and defaulted thickness by 5 mm thickness group; layer-hint mismatch by layer name; unjoined ends one group per sheet.
- **FR-005**: Ranked questions MUST be ordered by score, highest first, with the deterministic tie-break listed under Edge Cases.
- **FR-006**: The engine MUST detect these four wall guesses:
  - **Layer-hint mismatch**: the wall's faces are not on a layer whose role is Wall.
  - **Defaulted wall thickness**: the thickness was not measured between two faces (it came from the drawing median or a default).
  - **Defaulted type**: the wall's thickness has no matching wall type in the supplied catalogue, so the build would fall back to a default type.
  - **Unjoined ends**: one or both wall ends meet no other wall.
- **FR-007**: Uncertainty and cost to fix later MUST be fixed per heuristic, not computed from free text or learned values: defaulted type 0.6 / High, defaulted thickness 0.5 / Medium, layer-hint mismatch 0.4 / Medium, unjoined ends 0.3 / Low; cost weights Low 1, Medium 2, High 3.
- **FR-008**: The basis MUST be stored on the wall's confidence record so hover text and Review copy read it without recomputing.
- **FR-009**: The scoring component MUST make no network call and have no AI dependency; it MUST take no AI input, so its output cannot differ between AI enabled and AI hard-disabled. This is proven by a repeat-run equality fixture and a source check for network/AI references.
- **FR-010**: Confidence records MUST stay with their walls in the CAD to BIM session through erase, brush, re-detect of the same drawing, and Confirm, and erased walls MUST NOT count towards a question's items affected.
- **FR-011**: Only walls are scored in this feature. Openings, rooms and plan clustering are out of scope (G9).
- **FR-012**: No user-interface change ships in this feature; existing detect, build and read_dwg_plan output stay as they are for callers that ignore confidence.
- **FR-013**: Unit tests MUST cover score ordering, ties, each heuristic's basis text, the no-percentage rule and the AI-on/AI-off equality fixture.
- **FR-014**: The read_dwg_plan tool output and its contract in `contracts/` MUST NOT change in this feature.

### Key Entities

- **Confidence**: one guess about one wall: heuristic, uncertainty, basis, items affected, cost to fix later, score.
- **Ranked question**: a group of walls sharing one guess (same heuristic and key), with its basis, items affected and score; the unit G11 turns into a question.
- **Wall-type catalogue**: the wall thicknesses a target model has types for; input to the defaulted-type heuristic.
- **Detected wall**: the engine's existing wall (two faces, thickness, centreline, layer of each face).
- **Session**: the pane's record of one drawing between detect and Confirm; now also holds each wall's confidence.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On ASWARA with the fixture catalogue, the first ranked question is "thickness 175 mm has no matching type", in 10 out of 10 runs.
- **SC-002**: 100% of basis strings produced on ASWARA and on the unit fixtures match one of the fixed templates and contain no "%".
- **SC-003**: Ranked output is byte-for-byte identical across repeated runs and cultures on every fixture, and the scoring code contains no network or AI reference.
- **SC-004**: Scoring adds under 5% to detection time on ASWARA plan 1 (measured with the headless harness, before and after).
- **SC-005**: After a re-detect of the same drawing, 100% of surviving walls keep confidence equal to a fresh computation.

## Open questions

Recorded by /speckit-clarify in a non-interactive run. Each has a recommended default that this spec already applies; confirm or overturn before implementation starts.

1. **Heuristic numbers.** Are these the right fixed values? Recommended default: defaulted type 0.6 / High, defaulted thickness 0.5 / Medium, layer-hint mismatch 0.4 / Medium, unjoined ends 0.3 / Low; cost weights L=1, M=2, H=3.
2. **Thickness matching.** Is a 5 mm group with a 2.5 mm type-match tolerance right? Recommended default: yes; finer groups split one real wall type into several questions.
3. **Unjoined ends granularity.** One question per sheet or per wall? Recommended default: one per sheet. Per wall would put hundreds of near-identical questions in the list (ASWARA plans 1-3 have 223, 205 and 243 loose ends).
4. **Session store / G6.** The ticket says confidence must survive "to the session store (G6 contract)". No G6 contract exists in this repo (the only "G6" in the repo, in `docs/superpowers/specs/2026-10-24-copilot-lean-harness-design.md`, is an unrelated copilot goal). Recommended default: the in-memory CAD to BIM session, carried across re-detect; G6 to define persistence later.
5. **Wall-type catalogue for the ASWARA check.** Which types does "no matching type" compare against? The builder today puts every wall on the model's first basic wall type, so it never matches by thickness. Recommended default: the ASWARA test passes a fixed catalogue checked into the test, holding the thickness bands measured on ASWARA except 175 mm. If thin bands (50, 70, 80 mm; 221 of 351 walls on plan 1 per the known-limits doc) are also left out of the catalogue, they would outscore the 175 mm question by walls affected, and the acceptance criterion would fail: see Risk 1 in the plan.
6. **read_dwg_plan exposure.** Should bina-ai see confidence now? Recommended default: no; paired change later (constitution: contract changes are paired across both repos).

## Assumptions

- "Detected element" means the engine's existing wall object; there is no separate detected-element type today, and none is needed for walls only.
- "Session store (G6 contract)" means the existing in-memory CAD to BIM session until the G6 contract is written; no file or server persistence is added here.
- The wall-type catalogue is supplied by the caller. In Revit it would come from the document's basic wall types; in tests and the headless harness it is a fixed list. Collecting it from a live Revit model is out of scope here because the builder today always uses one default type.
- The ASWARA acceptance runs through the existing env-gated real-sheet tests (the drawing is not in the repo).
- Display-only rephrasing of basis strings belongs to the review-card text layer (pending #11), not this feature.
