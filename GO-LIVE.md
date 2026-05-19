# BINA Revit Copilot — Go-Live Checklist

Status as of 2026-05-19. This checklist gates the move from
*supervised pilot / demo-with-a-driver* to *real-user use*. Nothing here is
"tick it from memory" — every item has a concrete verification action and a
"Done when" pass criterion.

Parity baseline (the versions everything below must match):

- Backend `bina-ai` branch `feat/copilot-prd` @ `8e87761`
- Addin `revit-addin-sync` branch `feat/copilot-saved-commands` @ `32a14b3`

Sign-off at the bottom. Do not flip a gate to PASS until its criterion is met.

---

## Gate 1 — Deploy parity (RED until verified)

The running system must equal the reviewed/verified branches. Today the addin
DLL on the Windows machine is **older than the branch** (at minimum it predates
`32a14b3`, the transaction-wrap fix), so verified fixes are not actually live.

- [ ] **1.1 Backend = branch.** On the backend host:
      `git -C bina-ai rev-parse HEAD` → equals `8e87761` (or newer on
      `feat/copilot-prd`), working tree clean (`git status -s` empty).
      Done when: HEAD matches and tree is clean.
- [ ] **1.2 Backend actually serving that code.** Confirm the running process
      was (re)started after the last pull / autoreload picked it up.
      `GET {base}/api/revit-ai/health` → `{"status":"ok"}`.
      Done when: health is OK *and* a known-fixed prompt (e.g. "create a grid
      line named G") returns clean code through `/api/revit-ai/route`.
- [ ] **1.3 Addin rebuilt from branch.** On Windows, from a clean checkout at
      `32a14b3`:
      `dotnet build RevitWebAppSync.csproj -c Release -p:TargetFramework=net8.0-windows -p:RevitPath="C:\Program Files\Autodesk\Revit 2026" -p:RevitVersion=2026`
      Done when: build succeeds with 0 errors.
- [ ] **1.4 DLL deployed.** Copy the built `RevitWebAppSync.dll` to
      `%APPDATA%\Autodesk\Revit\Addins\2026\` and restart Revit.
      Done when: file timestamp at the deploy path matches the fresh build,
      and Revit loads the add-in with no load error.
- [ ] **1.5 Deployed-build smoke.** In Revit, run the smoke subset from
      `COPILOT-TESTING.md` (one prompt per intent: VIEW, SELECT, QUERY, EDIT,
      CREATE, EXPORT, ANALYZE, CHAT).
      Done when: all smoke prompts behave as that doc describes.

## Gate 2 — Stable backend hosting (RED until done)

A laptop + ngrok + autoreload is a demo tunnel, not infrastructure for real
users. It dies on sleep, network change, or a clean server restart.

- [ ] **2.1 Always-on host.** Backend runs on a machine/container that does
      not sleep and restarts the service on boot/crash (systemd, container
      restart policy, or PaaS).
      Done when: killing the process results in automatic restart within a
      defined window, verified once by actually killing it.
- [ ] **2.2 Stable endpoint.** A fixed URL (not a per-session ngrok URL) that
      the addin's `_baseUrl` points at. If a tunnel is still used, it is a
      reserved/stable domain.
      Done when: addin config URL survives a backend restart with no addin
      change.
- [ ] **2.3 Secrets & model access.** `.env` (Azure keys) present on the host,
      not in the repo; model deployment quota sufficient for expected usage.
      Done when: 20 consecutive `/route` calls succeed with no auth/quota
      error.
- [ ] **2.4 Restart-from-clean test.** Stop the service, redeploy from a clean
      `git clone` at the pinned commit, start it.
      Done when: health OK and a code-gen prompt works — proving nothing
      depended on uncommitted working-tree state.
- [ ] **2.5 Basic observability.** Logs are persisted off the box (or at least
      retained), and there is a way to see `route`/`retry` errors after the
      fact.
      Done when: a deliberately failing prompt's server-side log line is
      retrievable after a restart.

## Gate 3 — In-Revit execution pass (YELLOW — partially proven)

The 44/44 suite proves generated code is *clean* (no placeholders, group-safe,
correct intent). It does **not** prove it *executes correctly* against a real
model — that gap is structural and only closes by running it in Revit on the
deployed build.

- [ ] **3.1 Core EDIT.** On a real model: a parameter edit and a geometry edit
      that each touch grouped + ungrouped + read-only elements.
      Done when: counts reported (updated / skipped-in-group / skipped-
      read-only) are correct and the model reflects the change.
- [ ] **3.2 Core CREATE.** Create: a plan view, a sheet, a wall, a hosted
      family (door/window — with the family loaded), and one long-tail item
      (grid / text note / floor / column).
      Done when: each element actually appears and is findable; underspecified
      requests produce a clarify message, not a broken element.
- [ ] **3.3 QUERY / SELECT / VIEW / EXPORT.** One real prompt each against the
      model.
      Done when: results match independently-checked ground truth (e.g. a
      manual element count) and exports open.
- [ ] **3.4 Failure → one-retry convergence.** Force a failure (e.g. a prompt
      needing an unloaded family), apply the suggested fix once.
      Done when: it works after a single fix — no fix→fail→fix spiral.
- [ ] **3.5 Snapshot determinism.** Save a working run as a ⚡ snapshot, replay
      it.
      Done when: replay produces the identical result with no LLM call.
- [ ] **3.6 Revert.** After a write, use Revert / Ctrl+Z.
      Done when: the change is undone and the window behaves (no
      focus/flicker regression).

## Before UNATTENDED real users (hardening — not demo blockers)

- [ ] **H.1 Concurrency.** agno agents are module-level singletons; fire
      ~5 simultaneous `/route` requests with different sessions.
      Done when: no cross-session bleed (one user's context in another's
      result) and no crash.
- [ ] **H.2 Auth / multi-tenant.** Verify OrgId command sharing isolates teams
      (user A cannot see user B's private commands).
      Done when: a cross-org access attempt returns nothing/forbidden.
- [ ] **H.3 Rollback plan.** A written "if it misbehaves in prod" step:
      previous-good addin DLL kept, backend revertable to a prior commit.
      Done when: the previous DLL + the prior backend commit hash are recorded
      and the revert is tested once.
- [ ] **H.4 Cost ceiling.** Know the per-prompt token cost (auto-fix ~doubles
      it on flagged prompts) and set an expectation/limit.
      Done when: a rough cost-per-100-prompts figure is documented.

## Consciously accepted out-of-scope (not gates)

These are known-deferred; going live means accepting their absence:

- [ ] #33 GenerateTool (AI design variations) — not available
- [ ] #35 dockable pane (Copilot is a modeless window)
- [ ] #31 real syntax highlighting in code blocks
- [ ] Soft router nuances: "pin all the grids and levels" → no-op MULTI;
      "delete everything" → safe clarify (acceptable, but known)

---

## How to test (step-by-step)

`{base}` = the backend URL the addin points at (see `BinaConfig` /
addin settings). Replace it in every command below.

### Pre-flight & Gate 1 — is the right code running?

1. **Backend health (1.2).** In a browser open `{base}/api/revit-ai/health`
   — expect exactly `{"status":"ok"}`. Or:
   `curl -s {base}/api/revit-ai/health`
2. **Backend serves the verified code (1.1 / 1.2).** On the backend host:
   `git -C <bina-ai path> rev-parse HEAD` → must be `8e87761` or a later
   commit on `feat/copilot-prd`; `git -C <bina-ai path> status -s` → empty.
   Then prove it end-to-end:
   ```
   curl -s -X POST {base}/api/revit-ai/route \
     -H "Content-Type: application/json" \
     -d '{"message":"create a grid line named G"}'
   ```
   PASS when the JSON `actions[0].code` contains C# that has **no**
   `new XYZ(0, 0, 0)` and **no** "adjust as necessary" / "replace with your".
   (This is the fix from `8e87761` proving it is actually live.)
3. **Addin built from branch (1.3).** On Windows, clean checkout at
   `32a14b3`, run the `dotnet build` line from Gate 1.3. PASS = 0 errors.
4. **DLL deployed (1.4).** Copy the built DLL to
   `%APPDATA%\Autodesk\Revit\Addins\2026\RevitWebAppSync.dll`. Right-click →
   Properties: the **Modified** time must equal the build you just ran.
   Restart Revit. PASS = add-in loads with no error dialog and the Copilot
   window shows a connected/ready state.
5. **Deployed-build smoke (1.5).** In the Copilot, type each and confirm it
   does the right thing (one per intent):
   - `switch to the 3D view` (VIEW — view changes, no Run click needed)
   - `how many walls are in the model?` (QUERY — returns a number)
   - `select every door wider than 900mm` (SELECT — selection changes)
   - `export a wall schedule` (EXPORT — a file is produced)
   - `what is the difference between a basic and stacked wall?` (CHAT — text
     answer, no code)
   PASS = each behaves as `COPILOT-TESTING.md` describes.

### Gate 3 — does the generated code actually run in Revit?

Run these in order on a real model. For each: type the prompt, click **Run**
when shown the code, then check the model — not just that text appeared.

| # | Prompt | PASS when… | FAIL looks like |
|---|---|---|---|
| 3.1a | `increase every wall's height by 500mm` | walls get taller; the reply reports updated / skipped-in-group / skipped-read-only counts | a Revit modal pops, or nothing changes, or it claims success but heights are unchanged |
| 3.1b | `set the phase of all furniture to Existing` | furniture phase changes; grouped items reported as skipped, not erroring | group-edit modal appears |
| 3.2a | `create a floor plan view for every level` | one new plan per level, names unique, listed in the reply | "view not found" / duplicate-name error / only one created |
| 3.2b | `create a sheet with the floor plan on it` | a sheet with a titleblock + the viewport | placeholder titleblock / empty sheet |
| 3.2c | `add a door in the middle of the longest wall` | a door appears hosted in that wall **(family must be loaded)** | if no door family loaded: a clear "load a door family first" message, NOT a crash |
| 3.2d | `create a grid line named G` | a new grid offset from the last one, named G | grid at origin / "no grids" with grids present |
| 3.3 | `which is the largest room by area?` | the named room matches a manual check | wrong room / no answer |
| 3.4 | a prompt needing an unloaded family (pick one your model lacks) → when it fails, click the suggested fix **once** | works after **one** fix | fix → still fails → fix again (spiral = FAIL) |
| 3.5 | run 3.3 again, click **Save as command** (⚡), then replay it from the saved list | identical result, no "Thinking…", token count does **not** increase (no LLM call) | it re-generates / result differs |
| 3.6 | after 3.1a, click **Revert last change**; separately, with the input box empty press **Ctrl+Z** | the height change is undone both ways; window doesn't flicker or drop behind Revit | change not undone / focus lost |

3.4 is the most important: it directly verifies the "one retry converges"
promise. If it spirals, that is a go-live blocker regardless of other passes.

### Gate 2 / Hardening — hosting & scale

- **Kill test (2.1).** On the host, stop the backend process. Within your
  defined window it should auto-restart; re-run the health check (step 1).
  PASS = health OK again with no manual start.
- **Clean-clone restart (2.4).** `git clone` the backend fresh into a new
  folder at the pinned commit, set up `.env`, start it, point `{base}` there,
  re-run pre-flight step 2. PASS = works — proves nothing relied on
  uncommitted local files.
- **Concurrency (H.1).** Fire 5 simultaneous requests with different
  sessions:
  ```
  for i in 1 2 3 4 5; do curl -s -X POST {base}/api/revit-ai/route \
    -H "Content-Type: application/json" \
    -d "{\"message\":\"how many walls?\",\"sessionId\":\"s$i\"}" & done; wait
  ```
  PASS = all 5 return a sensible answer, none contains another session's
  data, no 500s.
- **Cost (H.4).** Note `tokensUsed` from ~10 mixed prompts (a flagged one
  that triggers the silent auto-fix will be ~2×). Record a rough
  cost-per-100-prompts figure.

### Record the outcome

Fill the Go/No-Go table below: status, who verified, date. A gate is PASS
only when **every** checkbox under it meets its "Done when" criterion.

---

## Go / No-Go

| Gate | Status | Verified by | Date |
|---|---|---|---|
| 1 Deploy parity | PASS | backend: verified 90b5f79 clean; addin: rebuilt + 3-command smoke OK | 2026-05-19 |
| 2 Stable hosting | NO-GO | | |
| 3 In-Revit execution | NO-GO | | |
| H hardening (unattended only) | N/A for supervised demo | | |

Rule of thumb:
- **Supervised SV demo, you driving, known model:** Gate 1 + Gate 3 smoke
  (3.1–3.3, 3.5) is the realistic minimum.
- **Real architects, unattended:** all of Gates 1–3 PASS **and** H.1–H.4 done.
