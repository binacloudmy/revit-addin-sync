# Integration Plan — SV branches × our work

Status as of 2026-05-21. This plan integrates the two parallel efforts on the
BINA Revit Copilot:

- Our branches: backend `feat/copilot-prd` @ `265eb91`, addin
  `feat/copilot-saved-commands` @ `92e0085`.
- SV branches: backend `feat/sp6-model-routing`, addin
  `feat/copilot-pane-redesign`.

Companion docs: `GO-LIVE.md` (verification gates), `LIMITATIONS.md` (honest
boundaries), `COPILOT-TESTING.md` (Gate 3 test plan).

The merge is **not** a `git merge`. The two branches diverged on architecture,
not just files. This document is the deliberate-choice plan that lets two
people divide the work without colliding.

---

## 1. Goal & non-goals

**Goal:** one branch that has SV's architecture (structured router, model
escalation, observability, Docker deploy, dockable pane, vetted synthesizers,
tests) **plus** our reliability hardening (agent-prompt safety, static
lint + invisible auto-fix, retry self-review, defensive addin rewrites,
deterministic decoding), re-verified in real Revit at the same Gate 3 bar.

**Non-goals (this round):**
- Re-litigating either architecture. SV's structured-output dispatch wins
  on the router; our hardening wins on agent prompt + code-gen safety.
- Reverifying every PRD feature already shipped; only re-run Gate 3 + the
  prompt-safety scenarios we hit.

---

## 2. Strategic decisions (architecture choices)

| Axis | Choice | Why |
|---|---|---|
| Router | **SV** (`revit_router.py` + `RouteDecision` + `validate_action`) | Typed action params, no free-form dicts, validated outputs reject 500s. Stronger than our intent-router. |
| Model layer | **SV** (mini → full escalation via `route_with_escalation`) | Real cost/latency win; our temperature pinning slots underneath. |
| Prompt cache discipline | **SV** (date in user turn via `build_revit_ai_user_input`) | Byte-stable system prefix → Azure prompt cache hits across days. |
| Agent prompt safety content | **Ours** (NO PLACEHOLDERS, per-level try/catch, group skip, return null;, HashSet shape, common BuiltInParameter mapping, hosted-family pattern, Excel export, etc.) | Hard-won from 3+ Gate-3 rounds in real Revit. SV's prompt has the structure, not the safety. |
| Decoding | **Ours** (`temperature=0`) | Direct fix for run-to-run variance. SV's `get_model` doesn't set it — must port back. |
| Static lint + invisible auto-fix | **Ours** | Catches `ExportOptionsExcel` / `CURVE_LENGTH` / bare `return;` / placeholder-coord / no-group-skip classes before they ship. |
| Retry self-review checklist | **Ours** (with SV's validate_action layered ahead) | Convergence-in-one-pass discipline. validate_action catches schema bugs first; checklist catches code-content bugs. |
| Tool dispatch | **SV** (vetted synthesizers `BuildSetParameter` / `BuildRenameElements` / `BuildExportSchedule` + tool-first dispatch) | Deterministic first-try beats LLM. Our native dispatchers (`BuildNativeSelectionCode` / `BuildNativeExportCode`) collapse into this. |
| UI shell | **SV** (dockable pane `UI/Copilot/Screens/*`) | Resolves #35. Our edits to the retired `AIAssistantWindow.xaml.cs` re-port to the new screens. |
| Addin code execution | **SV's CodeExecutor base + our defensive rewrites** | Our `return;` → `return null;` regex and real-`new Transaction(` regex are bug-class immunity; layer on top. |
| HTTP timeout | **Ours** (180 s) | SV may have a different value; ours was raised from 90 s to absorb Azure spikes. |
| Hosting | **SV** (containerised, App Service staging) | Closes Gate 2. |
| Observability | **SV** (Langfuse auto-instrumentation) | Closes part of H.4. |
| Tests | **SV** (pytest suites) | Closes part of section 8 (LIMITATIONS — verification coverage). |
| Docs | **Ours** (`GO-LIVE.md`, `LIMITATIONS.md`, `COPILOT-TESTING.md`) | Already calibrated to honest position; keep. |

---

## 3. Phase plan (do these in order)

### Phase 0 — Prep & alignment (≈ ½ day, both people)
- [ ] Both sides read this plan and agree (or amend) the strategic table.
- [ ] Both sides skim `GO-LIVE.md` and `LIMITATIONS.md` so the bar is shared.
- [ ] Cut integration branches off the SV branches:
      `bina-ai` → `feat/integrate-pane-routing` (from `feat/sp6-model-routing`)
      `revit-addin-sync` → `feat/integrate-pane-routing` (from
      `feat/copilot-pane-redesign`)
- [ ] Tag a known-good point: `git tag pre-integrate-our-branch-tip` on
      each of our branches so revert is one command if needed.

### Phase 1 — Backend integration on the SV base (≈ 1–1½ days, one person)

Order matters — items below build on each other.

1. **Port `temperature=0` into `get_model()`** (`app/agents/revit_ai.py`)
   - Add `temperature=0` kwarg to the three model constructors (Azure
     primary, Azure fallback, OpenAI fallback). Match our commit `b33c117`.
   - Verify: `import` test, then a probe of `route_with_escalation` should
     still succeed (mini + full both accept temperature on chat models).
   - **Risk:** GPT-5.2 reasoning variants reject temperature ≠ 1. If SP6's
     mini model is a reasoning variant, set temperature only on the full
     model and let mini default. Test before committing.

2. **Port the agent prompt safety content into SV's `revit_ai.py` instructions list.**
   - Concretely, the safety blocks to merge in (each as one or more list
     entries in the instructions array — preserving SV's structure):
     - 🚨 NO PLACEHOLDERS rule + the 5 sub-rules (commits `dc0fc45`,
       `93ad242`)
     - 🚨 NEVER open own Transaction + group-member-skip top rules (`2fa904a`)
     - Common BuiltInParameter mappings (Comments=ALL_MODEL_INSTANCE_COMMENTS
       etc.) (`8c3022c`)
     - Three-bucket reporting on bulk edits (`9b89c19`)
     - Finding a view via `ViewPlan.GenLevel` not name (`679281a`)
     - Collision-safe view creation + no unrequested level filter +
       Browser-Search tip (`a07442a`, `bb4da5d`)
     - NON-NEGOTIABLE per-level try/catch + safe HashSet shape + return null
       for early exit (`265eb91`)
     - Exact hosted-family placement pattern + terminal on missing family
       (`8130607`)
     - Sheet/viewport pinned pattern (`406d544`)
     - Pinned Excel-export pattern (no `ExportOptionsExcel`, use WriteExcel,
       length-from-geometry, no `CURVE_LENGTH`) (`90b5f79`)
     - OpenView() must run outside a Transaction (`c457382`)
   - **Cache discipline:** every block goes into `instructions=[...]` (the
     byte-stable system prefix). NOTHING that varies per run (dates, model
     state, user names) belongs there — that's SV's SP1 invariant. Verify
     with their `tests/test_revit_ai_prompt_cache.py` after porting.

3. **Port `_static_code_smells` and `_auto_fix_code` into `main.py`.**
   - Find the code-gen path in SV's main.py (around the `/agents/revit-ai/route`
     endpoint at line ~530 + wherever they call `revit_ai.run` /
     `revit_ai.arun`).
   - Insert the lint → auto-fix wrap: after the agent produces code, run
     `_static_code_smells(code)`; if non-empty, do ONE silent
     `_auto_fix_code` pass before returning. Same pattern as our
     `_generate_revit_code`.
   - Keep `_context_suffix` as the single helper for the context block; SV's
     route uses validated typed context (RouteActionParams) — the suffix can
     pass through their already-typed shape rather than json-dumping.
   - Lint must include all 4 categories now: placeholder coord (`new XYZ(0,0,0)`),
     placeholder phrases, no-group-skip bulk write (broadened — `el.Group`,
     `GetGroupId`, `for`/`.ForEach`), bare `return;`.

4. **Port the retry self-review checklist into SV's retry endpoint.**
   - SV has `/agents/revit-ai/retry` (or the equivalent under the new
     namespace). Add the same self-review block we built in
     `revit_ai_retry`:
     - no type/family guessed by name
     - no placeholder coords
     - no instruction comments
     - no NON-EXISTENT API (`ExportOptionsExcel`)
     - no GUESSED BuiltInParameter (`CURVE_LENGTH`)
     - unique view/sheet/schedule names
     - per-level try/catch on batch view creation
     - safe HashSet shape
     - early exit uses `return null;`
     - FamilySymbol Activated + doc.Regenerate
     - mm → feet (/304.8)
     - terminal ShowMessage on truly missing values

5. **Decide the route-namespace shape and stick to it.**
   - SV exposes `/agents/revit-ai/route` (and per `feat(ab1)`, the addin
     points all calls at `/agents/revit-ai`). Our code uses `/api/revit-ai/...`.
   - **Decision:** keep SV's namespace (`/agents/revit-ai/...`). Delete or
     stub our `/api/revit-ai/*` aliases — one canonical surface.
   - Confirm the addin's `_baseUrl` + per-call paths match. SV's `AIService`
     was already updated for this.

6. **Run SV's existing tests + add lint tests:**
   - `pytest tests/test_revit_router*.py tests/test_revit_actions.py
     tests/test_revit_ai_*.py` — must stay green.
   - Add `tests/test_static_code_smells.py` covering: placeholder coord
     case-insensitive, `el.Group` skip not false-flagged, `for`-loop bulk
     write flagged, `.GroupBy(` not fooling regex, bare `return;` flagged,
     `return null;` not flagged.

### Phase 2 — Addin integration on the SV pane (≈ 1–1½ days, one person)

7. **Port `CodeExecutor` defensive rewrites into SV's `CodeExecutor`.**
   - Add the regex `\breturn\s*;` → `return null;` (commit `e462a5a`).
   - Make `selfManagesTransaction` detect a real `new Transaction(` /
     `TransactionGroup(` / `SubTransaction(` via regex, not the bare word
     "Transaction" (commit `32a14b3`).
   - Both belong in `WrapCode` before `InjectFailureHandlingIntoUserTransactions`.
   - **Risk:** SV's CodeExecutor may have changed signature/wrap shape for
     vetted-tool dispatch — read it first, then port. The behaviour is
     additive (defensive rewrites), not architectural.

8. **Bump `HttpClient.Timeout` to 180 s** (`Services/AIService.cs`).
   - One-line change with the same comment block we have in commit `b486acf`.
   - If SV already set it ≥ 180 s, leave alone.

9. **Port view-resolution and export-schedule fixes into the new pane.**
   - SV "retired the old window" — our edits to `AIAssistantWindow.xaml.cs`
     don't apply to a file that's gone. Locate the new equivalents:
     - `open_view` action handling — should be in one of
       `UI/Copilot/Screens/ChatView.xaml.cs` or a vetted-tool synthesizer.
       Add the view-TYPE fallback: if name match fails AND `n` looks like
       a view kind ("3d"/"elevation"/"section"/"plan"/"ceiling"/"legend"/
       "drafting"), resolve by ViewType / View3D. Match commit `e1667da`.
     - `export` / `export_schedule` — check SV's `BuildExportSchedule`
       vetted synthesizer. If it only triggers on `format=excel`, relax the
       gate the same way commit `e1667da` did: only true binary/CAD
       formats (pdf/ifc/dwg/dwf/bcf/nwc/fbx/image/gbxml) fall through to
       LLM; everything else (excel/csv/schedule/unspecified) takes the
       deterministic data-dump path.
   - If `BuildExportSchedule` already does the right thing, just verify.

10. **Confirm saved commands (⚡ snapshots) still work in the new pane.**
    - The "(skipping AI)" replay was a key Gate 3 PASS criterion. Verify
      the new `SavedView`/`HistoryView` preserves it; if not, port from
      the old window's snapshot machinery.

### Phase 3 — Re-verify in real Revit (≈ ½–1 day, you driving)

11. **Gate 1 re-run on the integrated build:**
    - Backend: confirm the integrated branch is the deployed commit;
      `git rev-parse HEAD` matches; `/agents/revit-ai/health` OK; one
      `curl` of `/agents/revit-ai/route` returns a clean validated action.
    - Addin: rebuild + redeploy DLL; restart Revit; smoke the 5 intents.

12. **Gate 3 re-run on the integrated build:**
    - All 6 rows of `GO-LIVE.md` Gate 3 table, in order, on the dewan model.
    - Specifically re-run the scenarios that surfaced bugs the first time
      (force a failure → one fix converges; snapshot replay "(skipping AI)";
      `create a floor plan view for every level` produces "Created N,
      skipped M"; `export a wall schedule` produces a valid .xlsx).
    - For each, confirm: model actually changed (don't trust `[OK]` alone).

13. **Run SV's pytest suite + the new lint tests** — must stay green.

14. **Update `GO-LIVE.md` Go/No-Go table** with the new commit hashes,
    `Gate 3: PASS` re-affirmed on the integrated branch.

15. **Update `LIMITATIONS.md` § 7 + § 8**: cross out the items SV's work
    closes (containerised deploy → Gate 2; Langfuse → part of H.4; pytest
    suite → part of section 8 coverage). Be honest: don't claim H.1
    (concurrency) closed until it's actually load-tested with the new
    singletons.

---

## 4. File-by-file disposition

### Backend

| File | Disposition | Notes |
|---|---|---|
| `app/agents/revit_ai.py` | **SV base + port our safety blocks + temperature=0** | Respect SV's cache-prefix discipline (date in user turn). |
| `app/agents/intent_router.py` | **Delete (superseded)** | SV's `revit_router.py` with `RouteDecision` replaces it. |
| `app/agents/revit_router.py` | **SV — keep as-is** | This is the structured router. |
| `app/agents/error_explainer.py` | Keep ours if SV didn't change it; else SV's | Verify diff first. |
| `app/main.py` | **SV base + port lint+autofix into the code-gen path + retry checklist** | Largest merge file. Map our `_generate_revit_code` / `_auto_fix_code` / `_static_code_smells` / `_context_suffix` into the SV route. |
| `app/services/revit_actions.py` | **SV — keep as-is** | `validate_action` is part of the structured-router story. |
| `app/services/error_patterns.py` | **Ours — port over** | SV doesn't have it; FR-022 error-pattern learning. |
| `app/services/command_templates.py` | **Ours — port over** | Saved commands CRUD + `generated_code` column. |
| `app/observability.py` | **SV — keep as-is** | Langfuse auto-instrumentation. |
| `Dockerfile` / `.dockerignore` / `pyproject.toml` / `uv.lock` | **SV — keep as-is** | Hosting story. |
| `tests/test_revit_*` | **SV — keep + add `test_static_code_smells.py`** | Coverage. |

### Addin

| File | Disposition | Notes |
|---|---|---|
| `AIAssistantWindow.xaml{,.cs}` | **Delete (SV retired)** | New pane replaces it. |
| `UI/Copilot/Screens/*` | **SV — keep as-is** | Dockable pane = #35. |
| `Services/CodeExecutor.cs` | **SV base + port `return;`→`return null;` regex + real-Transaction regex** | Defensive rewrites layer on top. |
| `Services/AIService.cs` | **SV base + 180 s timeout** | Transport namespace already SV's. |
| `Services/VettedToolCode*.cs` / `Build*Synthesizer.cs` | **SV — keep as-is** | Tool-first dispatch. |
| `Handlers/CodeExecutionHandler.cs` | Verify SV didn't break Undo path; keep our changes if not | Read first. |
| `GO-LIVE.md` / `LIMITATIONS.md` / `INTEGRATION-PLAN.md` / `COPILOT-TESTING.md` | **Ours — keep, update in Phase 3** | Operational docs. |

---

## 5. Risks & mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| GPT-5.2 mini model rejects `temperature=0` (reasoning variant) | Med | Set temperature only on full model; let mini default. Probe before committing. |
| SV's cache-prefix invariant breaks when we add safety blocks | Med | Their `tests/test_revit_ai_prompt_cache.py` is the canary; run after each safety block port. |
| Our static lint produces false positives on SV's vetted-synthesizer output | Low | Lint only runs on LLM-generated code path; vetted-synthesizer output skips it entirely. Wire-up matters — confirm at integration. |
| Routing namespace inconsistency between addin and backend | Med | Decide once (`/agents/revit-ai/*`), grep both repos for stale `/api/revit-ai/` paths, delete. |
| Port-of-3D-view-fallback misses a new path in the pane | Med | Test row 3.2's `switch to the 3D view` smoke prompt on the integrated build; if it fails, port the type-fallback. |
| Snapshot determinism breaks under new SavedView | Med | Run Gate 3.5 on integrated build; the "(skipping AI)" tag is the canary. |
| Hosting move surfaces env / secret issues (Azure keys, ngrok URL) | Med | Follow Gate 2 steps in `GO-LIVE.md` exactly: clean-clone restart test catches missing env vars. |
| Concurrency regression with vetted-tool dispatch + singletons | Low–Med | Run H.1 (5-parallel `curl` loop with different sessionIds) before declaring Gate 2 closed. |

---

## 6. Effort breakdown (honest)

| Phase | Effort | Owner |
|---|---|---|
| 0 — Prep & alignment | ½ day | both |
| 1 — Backend integration | 1–1½ days | backend owner |
| 2 — Addin integration | 1–1½ days | addin owner |
| 3 — Re-verify in Revit | ½–1 day | you |
| **Total** | **3–5 focused days** | — |

This assumes one person who already knows their side does the merge for
their side. Concurrent work is possible (backend and addin in parallel),
gating only at Phase 3.

---

## 7. Single biggest acknowledgement to make to your SV

They independently shipped four of the items `LIMITATIONS.md` flags as
gaps: **dockable pane (#35), vetted tool synthesizers, observability, and
containerised deploy (Gate 2)**. Their architecture also fixes one of the
honest caveats we couldn't address from our side (cache-prefix discipline
for the 1500-line system prompt). The integration is high-value because
you're converging from opposite ends on the same destination.
