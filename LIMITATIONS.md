# BINA Revit Copilot — Current Limitations

Status as of 2026-05-21. This is an honest, comprehensive list of what the
Copilot does **not** do today, what carries residual risk, and where the
real gaps sit before unsupervised production use. It exists so a reviewer
(SV, sponsor, prospective adopter) knows the system's boundaries before
agreeing to a deployment scope.

Cross-reference: `GO-LIVE.md` (gates 1–3 + hardening H.1–H.4). This file
explains *why* those gates exist.

Companion baseline: backend `feat/copilot-prd` @ `265eb91`,
addin `feat/copilot-saved-commands` @ `b486acf`.

---

## 1. Infrastructure / hosting

- **Backend runs on a laptop via an ngrok tunnel** (Gate 2 unmet). If the
  laptop sleeps, loses network, or the tunnel rotates without
  `BinaConfig.AIBaseUrl` being updated, the Copilot is offline. This is
  not production hosting.
- **No persistent server-side logs off-host.** Errors that occurred
  before a restart cannot be retrieved afterwards.
- **No automated rollback path.** Recovery from a bad deploy is manual:
  revert the commit, redeploy the DLL.
- **Single-region Azure dependency.** The Copilot is offline if Azure
  GPT-5.2 in your tenant is unavailable or throttled.

## 2. Code-generation reliability (LLM-inherent)

- **Not deterministic across providers.** `temperature=0` strongly reduces
  run-to-run variance but does not eliminate it (Mixture-of-Experts
  routing and batch effects on the provider side).
- **Static "smell" lint covers a finite set of defect classes.** It
  catches known issues (placeholder coords, missing group-skip, bare
  `return;`, fake APIs like `ExportOptionsExcel` / `CURVE_LENGTH` / 2-arg
  `HashSet`+LINQ). It cannot catch: novel hallucinated APIs, subtle
  logic errors, wrong-filter / wrong-category bugs, or Roslyn binding
  mismatches under the addin's specific reference set.
- **Roslyn compile-time gaps surface only in real Revit.** During Gate 3
  testing alone, three real compile-time bugs surfaced that no static
  verification could have caught (the transaction-word substring, the
  2-arg HashSet constructor, bare `return;` in `object Execute`).
  Expect similar surprises on each new command class until it has been
  exercised against the addin's actual compiler.
- **Convergence is verified for *known* error classes.** The
  retry-converges-in-one promise holds on the patterns codified into
  the self-review checklist. A genuinely new failure shape may still
  need a prompt-pattern update before it converges cleanly.
- **Underspecified prompts produce technically-correct-but-unhelpful
  results.** Example: "create a wall 5000 mm long on the lowest level"
  — no location given, so the agent placed the wall at world origin,
  far from the visible model. The agent does not always clarify when
  it should.

## 3. Capability gaps (deferred or out of scope)

- **GenerateTool (AI design-variation generation): not available** (#33).
- **Dockable pane:** Copilot is a modeless WPF window, not a Revit
  dockable pane. It can lose focus when Revit reclaims input (#35).
- **Syntax highlighting in code blocks:** plain text, not language-aware
  (#31).
- **Excel is the only natively deterministic file export.** PDF / IFC /
  DWG / DWF / BCF / NWC / FBX / image exports fall through to LLM
  code-gen and inherit all the code-gen risks of section 2.
- **Excel output is a flat data dump.** Headers + string rows via
  `WriteExcel`. No formatting, grouping, conditional formatting,
  formulas, multi-sheet, or styled cells. It is **not** a Revit
  ViewSchedule rendered to Excel.
- **CSV is not natively supported.** Falls to LLM code-gen.

## 4. Element-creation limits

- **A loaded family is required** to place doors, windows, structural
  columns, furniture, MEP fixtures, etc. The agent fails gracefully
  with a "load a … family first" message, but cannot create families.
- **Group members are intentionally skipped** by bulk EDIT operations —
  the safety trade-off that prevents Revit's group-edit modal. To
  change grouped elements, the user must edit the group manually.
- **Reference / non-story levels** may be reported as `skipped` when
  creating plan views per level. That is the system being safe, not a
  failure, but those levels will not get plans automatically.
- **No deterministic placement inference for free-form prompts.**
  "Create a wall 5 m long" needs an anchor; without one, default
  placement is arbitrary (often the world origin).

## 5. Intent-routing nuances

- **Some prompts classify into intents the user might not expect.**
  "Dimension the overall building width" → QUERY. "Tag every plumbing
  fixture" → SELECT. The generated code may still do something
  reasonable, but it is not necessarily what the user pictured.
- **"Pin all the grids and levels"** classifies as MULTI no-op (the
  router does not currently know how to ground both atomically).
- **"Delete everything"** is intercepted as a safety clarification
  rather than executed (intentional, but a known classification quirk).
- **Saved commands replay literally.** If the model state has changed
  in a way that makes the stored code unsuitable, the snapshot still
  runs it. No semantic re-check.

## 6. UI / UX

- **Status during code-gen is a generic "Thinking…"** — no breakdown of
  which phase (intent / generation / auto-fix) is currently running.
  A flagged prompt with the silent auto-fix can sit at "Thinking…"
  for 15–25 s with no progress detail.
- **Worst-case latency is real.** `/route` stacks up to three LLM calls
  (intent + generation + invisible auto-fix). Under Azure capacity
  spikes a single call can take 30–60 s. The HTTP timeout is now 180 s
  (raised from 90 s), but a transient Azure outage can still time out.
- **Window focus can flicker** under certain Revit-initiated commands.
  Mitigated, not eliminated.
- **Error-card UX is "fix or cancel"** — no built-in diff view of what
  changed between the failed and regenerated code.

## 7. Multi-user / production-readiness gaps

- **Agno agents are module-level singletons.** No formal verification
  that simultaneous requests from different sessions do not interfere
  (Hardening H.1 unmet).
- **OrgId / "My team" command sharing is wired but not stress-tested
  for isolation** (H.2 unmet). An auth-filter bug could expose private
  commands cross-org and no current test would catch it.
- **No rate limiting per user.** A single user can hammer the LLM as
  fast as the HTTP client allows.
- **No per-user cost tracking or cap** (H.4 unmet). Token spend is
  visible per session in the addin UI but not aggregated.
- **No rollback playbook** (H.3 unmet). The "if a deploy regresses,
  here is what to revert to and how" procedure is not written down or
  tested.

## 8. Verification coverage

- **No automated regression test against real Revit.** Every fix
  requires manual Gate-3 re-verification by a human. CI catches
  nothing on the in-Revit side.
- **Static-quality variety suites (44/44, hallucination sweep, etc.)
  are not execution proofs.** They prove the generated *code text* is
  clean against a known-bad-pattern denylist. Whether each prompt
  *runs and does the right thing* against a real model is only proved
  by Gate 3 testing.
- **Gate 3 was supervised, single-user, single model** (a JKR dewan
  project), single Revit version (2026). Behaviour against other
  models, other Revit versions, or under multi-user load is not yet
  proved.

## 9. Model / domain limits

- **Tested only on Revit 2026.** Other Revit versions are untested;
  API differences may break the addin.
- **JKR custom Browser Organization** can make new views land in
  unexpected groups; the agent now lists created view names and a
  Browser-Search tip to mitigate, but it remains a UX caveat for any
  project with a non-default browser configuration.
- **UBBL / JKR compliance results come from the existing dashboards**,
  routed via the ANALYZE intent. Code-gen-based ANALYZE fallbacks
  (clash detection, QTO, cost via LLM) are less reliable than the
  routed dashboard paths.
- **Agent knowledge has a cut-off.** Revit API changes after the
  model's training data may be unknown; new Revit features can produce
  guessed code.

## 10. Operational

- **Token cost is real and unbounded per prompt.** A flagged prompt
  costs ~2× a normal one (the silent auto-fix); a user-visible retry
  adds another generation. Heavy daily use is not free.
- **No telemetry on which commands succeed / fail / spiral** in
  production. Once unsupervised users are on the system, we would be
  flying blind on real-world quality.

---

## Bottom line for review

The Copilot is at *supervised-pilot quality* — verified Gate 1 + Gate 3,
with the reliability mechanisms (one-retry convergence, deterministic ⚡
snapshots, group-safe edits, graceful family-missing failures) working
as designed. The honest gaps before *unsupervised* deployment are
mostly **infrastructure and operational** (sections 1, 7, 8), not core
code-gen capability.

The residual long-tail LLM variance (section 2) is **bounded by the
safety mechanisms, not eliminated** — that is an inherent property of
LLM-based code generation, not a defect to fix.
