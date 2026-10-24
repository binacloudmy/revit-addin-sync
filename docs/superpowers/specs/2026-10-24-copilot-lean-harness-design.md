# BINA Copilot — Lean Harness Design

**Date:** 2026-10-24 · **Status:** proposed, awaiting owner approval
**Scope:** both repos — add-in `revit-addin-sync` and backend `bina-ai`. This file lives in the add-in repo; the backend gets a pointer when work starts.
**Evidence this design rests on** (owner's folder `~/Desktop/Copilot Improvement/`):
`01-audit-findings.md` (30 days of Langfuse + code audit of our harness), `03-evaluation.md`, `04-redesign-and-implementation-plan.md`, `05-github-landscape.md` (source audit of ~25 open-source Revit AI projects + Blender MCP).

---

## 1. Why

Drafters say the copilot "bluffs". The audit shows most of it is the system misreporting to itself, then patching the reply:

- **The add-in lies to the model.**
  - 60 bare `Commit()` calls after `StartSwallowing` in `Mutators.cs` (+8 other files) report `ok:true` after Revit rolled back.
  - `ToolLoopRunner.cs:802` marks any non-throwing job `Ok = true`, even `{ok:false}`.
  - Revit warnings are deleted and never returned (`TxGuard.cs:84-97`).
- **The backend then rewrites the reply.** `_emit_pending_or_done` (`app/services/revit_turn.py:880` to end of file, ~1,060 lines) stacks incident-named guards: anti-bluff continuations (four triggers), a truth gate, a "repair" pass using the C# compile-fix agent, raw-dump synthesis and a narration clamp. They collide: correct answers become robotic Malay, raw tool dumps ship as answers, stop messages get overwritten.
- **It is heavy.** 23.5k-char prompt, ~105 tool schemas (~82k chars) per call, 39 `REVIT_*` flags, Agno LearningMachine + compression + in-turn summaries (32.5% of model calls are housekeeping), p50 18 s of model time per turn.
- **The HTTP pause/resume design forces one worker and loses confirms.** A paused run lives in a per-process dict, so production must run `uvicorn --workers 1`. Even then, confirms were lost ("run not found", `bina-ai/Dockerfile:162-178`).
- **It regrew once already.** The June "lean codegen rebuild" (`bina-ai` `e4708f5`, 2026-06-05, "collapse to ONE agent") had become the pipeline above by October. Without fences, a rebuild regrows.

What works elsewhere:
- **Blender MCP** (32k★): 9 tools, 1.5k-char instructions locked by tests, code that runs inside the loop and returns tracebacks, and a `look` tool that sends a real image to the model.
- **The most truthful Revit projects** (Horizun, bimwright, KenLP, tzur) check commit status, return warnings, read back after writes, and bind approval to a real dry-run.
- **No project does both.** That is the opening.

## 2. What it is (owner-approved description)

A **personal drafter inside Revit** that handles all four kinds of request drafters make today: check the model (~35%), find/highlight (~30%), bulk edits (~15%), build/model (~15%). It should feel like a colleague who has been on the project from the start:

- It knows what the drafter is looking at (active view, level, selection — "this wall", "here").
- It knows what's in the model (levels, grids, rooms, families/types, sheets, links, warnings).
- It knows the project's rules (JKR standard, naming, required parameters, firm habits).
- It remembers what it did this session and on earlier days, and speaks the drafter's language (BM/EN).
- **Productivity:** it does the tedious parts faster than by hand. Simple requests take seconds. Results come with proof (highlight, picture, real diff), so the drafter doesn't re-check. When unclear, it asks one question with real options.

## 3. Goals, non-goals, anti-goals

**Goals**
- **G1 Truthful.** Every success the drafter sees is backed by Revit's own record (commit status, read-back, DocumentChanged).
- **G2 Understands context automatically.** No "please give me the element id".
- **G3 All four request kinds** on one loop.
- **G4 Fast.** Find/highlight in ≤ 2 model calls. Starting targets (eval sets the real ones): find/highlight p50 ≤ 8 s wall, edits p50 ≤ 20 s excluding drafter think time.
- **G5 Light.** One path, a small loop, size limits enforced by tests (§12).
- **G6 Controlled and monetizable from the cloud.** Prompts, tools, models and plan limits change without an add-in release.
- **G7 Measurable.** An eval on real drafter prompts gates every change.

**Non-goals**
- Not a public MCP server for third-party clients (possible later on the same tools; not in scope).
- No background or unattended changes; every write happens in a turn the drafter started.
- Not replacing the existing JKR/BOMBA compliance and cost dashboards; the copilot can call into them later.
- No local engine process.

**Anti-goals** (failure even if it "works")
- Claims something happened that Revit didn't record.
- Changes the drafter didn't approve, or an Undo that reverts the drafter's own work.
- Robotic replies, raw data dumps, rewritten answers.
- Regrowth into a pipeline of guards.

## 4. Constraints

- **One maintainer** (the owner) for add-in and backend. Simplicity beats completeness.
- **Revit 2023–2027** (`net48` → 2023/2024, `net8.0-windows` → 2025/2026, `net10.0-windows` → 2027; `RevitWebAppSync.csproj:10-14`).
- **Revit API rules this design must obey:**
  - All API work happens on Revit's UI thread through `ExternalEvent`, which fires only when Revit is idle.
  - A `Transaction`/`TransactionGroup` cannot stay open after the callback returns, so it cannot span a network round trip or a drafter's decision.
  - There is no Undo API. `PostCommand(PostableCommand.Undo)` undoes whatever is on top of the stack, one step per post, after the API context ends.
  - `DocumentChanged` change sets are inflated by regeneration (two walls can report 100+ modified elements).
- **Add-in releases are slow:** production signing is operator-only on the Windows signing box. Behaviour that changes often belongs in the cloud. The add-in should be a stable, honest executor.
- **Backend hosting:** Docker on a VPS behind kamal-proxy (`config/deploy.yml`, `response_timeout: 120`), with Cloudflare in front. Long-lived WebSockets must pass both (see §15).
- **Data:** strict per-tenant isolation. Some customers (government, JKR) may restrict where model data goes (open question Q1).

## 5. Decisions

| # | Decision | By | Why |
|---|---|---|---|
| D1 | The agent loop runs in the **cloud** (bina-ai) | Owner | Control and monetization; prompts and models change without an add-in release; source code is no longer shipped to PCs (the engine bundle ships `app/` source today, `EngineManager.cs:1-10`) |
| D2 | Model family: **DeepSeek** | Owner | — |
| D3 | Model: **`deepseek-flash`** (V4.1-Flash-1017) for every call, thinking **off** by default; `deepseek-v4-pro` only as an eval-proven fallback | My call | Flash beats V4-Pro on agentic/tool-use benchmarks (DeepSWE 74.2 vs 62.7, DeepSeek-reported). Only Flash accepts images (needed for `look`). ~4× cheaper input, 3× output. Peak pricing (01–04 and 06–10 UTC) = Malaysian working hours. Thinking on v4-pro measured 45–60 s per call (`revit_ai.py:113-140`) |
| D4 | **Replace Agno with our own loop** (~500 lines) on the OpenAI-compatible chat API | My call | With synchronous tool calls over a socket, Agno's main value to us (pause/resume external execution) is gone; the rest of what we use is the bloat |
| D5 | **One outbound WebSocket per Revit session** carries everything (chat, tool calls, results, progress); the turn runs in the process holding that socket | My call | No inbound ports; works on locked-down networks; no cross-worker paused state, so `--workers 1` and lost confirms go away |
| D6 | **The add-in owns approval and safety**: which tools write, the confirm card, the code allowlist. The cloud cannot skip them | My call | Today the confirm card depends on the backend's `mutate` flag (`ToolLoopDtos.cs:224`, `ConfirmGate.cs`). A compromised or buggy cloud must not be able to write unapproved |
| D7 | **Writes in one model step = one batch** = one preview callback + one commit callback + one named TransactionGroup | My call | The only grouping the Revit API allows across a drafter decision |
| D8 | **Keep `run_revit_code`**, with an allowlist enforced in the add-in, three modes (read / preview / commit), and approval for commit | My call | Act-see-fix in one turn is the main reason Blender MCP feels smart. pyRevit's own agent runtime also runs scripts, with approval in Revit |
| D9 | **Model provider is a per-tenant setting** (base URL, key reference, model id) | My call | Data-residency answers (Q1) can be met without code changes; V4.1-Flash weights are MIT, so non-China hosting or self-hosting is possible |
| D10 | **Size limits enforced by tests** (§12) | My call | The June rebuild regrew without them |
| D11 | **Retire the local engine and the HTTP pause/resume path** at cutover | My call | Follows from D1, D5 |

## 6. Architecture

```
 Drafter
   │
┌──▼──────────────── Revit add-in (C#) ─────────────────────────────┐
│ PANE      chat · question cards · confirm card (real preview)     │
│           · receipt card · Undo · "waiting for Revit" state       │
│ LINK      one outbound WSS socket (443), BINA login token,        │
│           heartbeat, reconnect + reconcile                        │
│ TOOLS     tool host on Revit's thread (McpJobPump)                │
│           · ~11 core tools + library of 176 typed tools           │
│           · add-in-owned read/write list · code allowlist         │
│ GUARD     TxGuard (CommitOrThrow) · collect warnings + dialogs    │
│           · read-back · diff (touched first) · TransactionGroup   │
│ CONTEXT   snapshot every message: doc, view, level, selection,    │
│           revision, units, phase, worksharing, links              │
│ LEDGER    idempotency keys → never apply a call twice             │
└──▲────────────────────────────────────────────────────────────────┘
   │  frames: hello · user_message · tool_call/tool_batch · tool_result
   │          · progress · reply · turn_end · cancel · reconcile · heartbeat
┌──┴───────────────── bina-ai cloud (Python) ───────────────────────┐
│ HUB       /copilot/socket — auth, one socket per Revit session    │
│ LOOP      context → model → tool calls → results → … → reply      │
│           nothing edits the reply                                 │
│ TOOLBOX   core schemas + find_tools (≤ 24 schemas per call)       │
│ SERVER    lookup_knowledge · get_skill · remember · ask_user      │
│ TOOLS                                                             │
│ MODEL     per-tenant provider config; default deepseek-flash      │
│ BILLING   metering (gateway accounting) + plan → allowed tools    │
│ STORES    sessions · receipts · project memory · knowledge        │
│           (pgvector) · skills — Postgres                          │
│ QUALITY   Langfuse (full turn trace) · eval runner                │
└───────────────────────────────────────────────────────────────────┘
```

## 7. Add-in

### 7.1 Link
- Reuse `BinaVibe/Mcp/McpTunnelClient.cs` (`ClientWebSocket`, built into .NET, no new dependency). Its frame shape already matches §9 in spirit.
- **Connect** when the pane opens and the drafter is logged in. Authenticate with the existing BINA login token (`BinaVibe/Auth/`).
- **Heartbeat** every 20 s (under Cloudflare's ~100 s idle cut). On reconnect: exponential backoff, then send `reconcile` with the idempotency keys of any calls in flight (§7.8).
- `hello` carries: add-in version, protocol version, Revit version, `InstalledToolManifest` (names + manifest version), and the first context snapshot.
- `UpdateGate` stays: a forced update blocks all tool calls, as today.

### 7.2 Tool host
- Dispatch through the existing `ToolRegistry.Invoke` on Revit's thread via `McpJobPump`, keeping its 6 s busy/modal fast-fail watchdog.
- **The read/write classification lives in the add-in.** It's a static list generated with the manifest (`scripts/gen-tool-manifest.py`), and a test fails if a registered tool is missing from it.
- Every tool returns the **envelope** (§10). Where a tool returns a bare dictionary today, a shared adapter wraps it.
- A tool name the cloud sends that isn't in this build's manifest, or isn't allowed for the session's plan (sent in `hello` ack), returns `failed` with code `not_allowed`. Defence in depth: the cloud also leaves it out of the toolbox.

### 7.3 Guard: every write
1. Each `Transaction` uses `TxGuard.StartSwallowing` + **`CommitOrThrow`**. That's plan A01: replace the 60+ bare commits, and add a test that fails on `.Commit();` in any file using `StartSwallowing`.
2. The failure preprocessor **collects** warning texts (definition id, severity, element ids), then deletes them so the commit can proceed without a modal (plan A03). Errors roll back and their text is captured. Both go into `warnings[]` / `error`.
3. A `DialogBoxShowing` handler active during the batch records any dialog Revit tried to show (`dialogs[]`). Dialogs it can't safely dismiss end the call with status `revit_busy` and the dialog's text.
4. **Read-back:** each write tool declares how it verifies (re-read parameters, element exists/gone, count delta). A test-enforced catalogue (Horizun's `WriteVerificationCatalog` rule) fails the build if a write tool has no declaration. `ok` = committed **and** verified; a mismatch makes the status `partial`.
5. **Diff:** a `DocumentChanged` recorder (reuse the `TurnReceiptService` recorder) collects added/modified/deleted ids. `touched` = ids the tool itself reports; everything else is summarized by category counts (`side_effects`). Nothing recorded → status `noop`.

### 7.4 Preview and confirm (writes)
The cloud sends all write calls from one model step as one `tool_batch`. Reads in the same step run first, separately, without confirmation.

```
callback A (preview):  TransactionGroup → run every call in the batch → capture envelopes
                       (diff, warnings, read-back) + doc revision R → RollBack
pane:                  confirm card renders the PREVIEW envelopes: "34 ceilings 2400→2700,
                       0 warnings", touched elements highlighted, code shown if run_revit_code
drafter:               Ya / Tidak / no answer in 10 min (= rejected, reason timeout)
callback B (commit):   current revision == R ? else re-preview (back to A, card updates)
                       → TransactionGroup "BINA: <summary>" → re-run same args
                       → diff matches preview (same touched ids, same counts)? else RollBack
                         and re-preview with "the model changed, please check again"
                       → Assimilate → return final envelopes (status done | partial)
```
- While the card is open, the add-in sends `progress{awaiting_confirm}` so the cloud extends its wait. Nothing is held open in Revit while the drafter decides.
- Tidak returns status `rejected` with `ok:true` (a decision, not a failure), as `ConfirmGate.Rejected` does today.
- Large batches (preview > 10 s) still preview. The card shows a progress state, and the eval tracks the cost.

### 7.5 Undo
- Each approved batch is **one named entry** in Revit's undo stack ("BINA: Set ceiling offsets L2").
- **Undo button:** posts **one** `PostCommand(Undo)` per BINA group in the turn, newest first, **one at a time**. It waits for `DocumentChanged` with operation `TransactionUndone` before posting the next.
- It is offered only while the turn's groups are still on top of the stack. The recorder tracks every committed transaction after ours; any non-BINA transaction since then disables the button. The card then says "You've changed the model since. Use Revit's Undo list: the BINA entries are named."
- **No compensating "reverse" transactions.** They can't restore deleted elements, joins or geometry faithfully, and a fake undo is a bluff.
- Replaces the current loop at `TurnReceiptService.cs:289-295` (audit #21: it undoes 1 of N).

### 7.6 `run_revit_code`
- **Input:** C# method body + `mode ∈ {read, preview, commit}`. A thin wrapper gives the code `doc`, `uidoc`, `view`, and `Print(obj)`. The wrapper starts no transaction in read mode; it provides a helper for writes.
- **Allowlist (enforced in the add-in, before compile)**, with a Roslyn semantic walk:
  - **Allowed:** `Autodesk.Revit.DB.*`, the selection/view parts of `Autodesk.Revit.UI` given via `uidoc`, `System`, `System.Linq`, `System.Collections.Generic`, `System.Text`, `System.Math`.
  - **Rejected:** `System.IO`, `System.Net`, `System.Diagnostics.Process`, `System.Reflection`, `System.Runtime.InteropServices`, `Assembly.Load*`, `Environment`, `System.Threading` (except `CancellationToken`), `dynamic`, unsafe code, and `Application.OpenDocumentFile` / `Document.Save*` / `Close` (saving is its own approved tool).
  - A rejection is returned as `failed` with the offending symbol and line, so the model rewrites.
  - The backend `RoslynGate` sidecar may run the same check early, but the add-in check is the one that counts.
- **read:** runs with no transaction open; any write attempt fails in Revit. Returns `Print` output + return value as JSON (capped 16 KB with a "truncated, narrow your query" note).
- **preview / commit:** runs inside the §7.4 batch flow; commit needs the confirm card, which shows the code and the preview diff.
- **Errors:**
  - Compile errors carry line numbers **relative to the model's code**. The wrapper offset is computed, not hard-coded; zexus and Sparx both got this wrong.
  - Runtime exceptions are unwrapped from `TargetInvocationException`: type + message + the top frames inside the model's code.
- **Limits:** 30 s per run (soft; reported as `timeout`, work done stays rolled back in preview). Output 16 KB.
- **Replaces:** the "C# runs after the reply" path (`RevitCopilotExecutor`, `CodeExecutionHandler`), at cutover.

### 7.7 Context snapshot (sent with every `user_message` and on request)
`doc{title, path, fingerprint, revision, is_workshared, is_central_or_local, units, phase, active_design_option}`, `view{name, type, level, scale, is_template}`, `selection{count, by_category, ids≤50}`, `worksharing{active_workset, owned_by_me_count, checkout_conflicts_in_selection}`, `links{name, loaded}[]`, `warnings_count`, `change_seq`.
The snapshot must be cheap: no full-model walks on the hot path. `model_overview` is the on-demand digest, cached in the cloud by `(fingerprint, revision)`.

### 7.8 Ledger and idempotency
- Every `tool_call` carries an `idempotency_key` (cloud: hash of turn id + call id).
- The existing `OperationLedger` refuses to start a key it has already seen: it returns the cached envelope, or `ambiguous` if the call started but never finished.
- `reconcile{keys}` answers per key: `completed(envelope)` / `failed` / `never_started` / `ambiguous`. A cloud retry after a dropped socket can then never apply a write twice.

### 7.9 Pane changes
- **Keep** the Copilot UI and its screens.
- **Change:**
  - the confirm card renders the preview envelopes;
  - the receipt card renders the final envelopes;
  - question cards come from `ask_user`;
  - a visible "Revit is busy / a dialog is open" state;
  - the Undo rules from §7.5.
- **Remove** the dependency on the backend `mutate` flag.

## 8. Cloud (bina-ai)

New code lives in a folder named for what it does, e.g. `app/services/assistant/` (never `v2`). Routes stay thin; logic lives in services, per the repo's CLAUDE.md.

### 8.1 Hub
- **Endpoint:** WebSocket at `/copilot/socket`. Auth uses the BINA login token, then resolves tenant, user and plan.
- **One active socket per (user, Revit session).** A second socket for the same session replaces the first.
- **The turn runs inside the process that holds the socket.** No cross-worker state, so `--workers 1` is no longer required for the copilot.
- Turn state (messages, envelopes) is written to Postgres at each step, so a reconnect can show what happened. Turns are not resumed across a dropped socket in this design: the turn ends `interrupted`, and `reconcile` reports the truth for in-flight writes.

### 8.2 Loop
```
on user_message:
  messages = assemble_context(turn)                      # §8.3
  for step in 1..MAX_STEPS (20):
      out = model.chat(messages, tools=toolbox(turn))    # §8.4, streams text to the pane
      if out has no tool calls: send reply(out.text); break
      reads, writes = split(out.tool_calls)              # by the manifest read/write list
      results  = run server tools locally; send reads as tool_call frames
      results += send writes as ONE tool_batch frame     # add-in previews, confirms, commits
      append results (envelopes) to messages
      stop early if: 3 consecutive failures with the same error code, or wall time > 180 s,
                     or cancel received → reply with what happened so far (from envelopes)
  record turn (trace, receipts, metering)
```
- **Nothing post-processes the model's text.** The only deterministic additions are built from envelopes and rendered by the pane, not mixed into the text:
  - the receipt card;
  - one fixed caution line when the reply contains no failure wording but every write in the turn ended `failed`/`rejected`/`noop`.
- **Known-error hints.** A small table maps known compile and runtime error signatures (wrong unit assumptions, common API misuse, missing `Regenerate`) to a hint appended to that tool's `next` field before the model sees it. This is the harnessbim idea; it lives in the cloud so it changes without an add-in release.

### 8.3 Context (target ≤ 6k tokens before tool results)
- **System prompt ≤ 2 KB of principles:**
  - read before writing;
  - numbers only from tool results;
  - one question with real options when unclear;
  - verify after writing;
  - reply in the drafter's language;
  - say what failed.
- **Snapshot** (§7.7), rendered compactly.
- **Model summary:** levels, grid count, room count, top categories, link state, warning count, from the cached `model_overview` (only when the cache is fresh).
- **Project memory** for (tenant, project fingerprint), ≤ 1.5k tokens.
- **Session receipts:** one deterministic line per earlier turn ("Set ceiling offset on 34 ceilings, L2, done").
- **Recent history:** the last 3 turns in full. Older tool results are replaced by their envelope's `summary` line. This is deterministic; no LLM compression or summaries.
- **Skill catalogue:** names + one-liners, ≤ 600 tokens, cache-stable at the end of the static prefix.

### 8.4 Toolbox
- **Core schemas (≤ 12) are always sent** (§11).
- `find_tools(query)` returns the best library matches (name + one line + why). Their full schemas are added to the **next** call. At most 24 schemas per call; least recently used extras drop first.
- **The search covers BM and EN.** The index includes Malay synonyms (the audit found "mana surau" returned unrelated tools).
- **Library schemas and help come from one source:** the add-in's manifest + `app/agents/revit/copilot/tool_help.py`. A tool missing from the add-in's `InstalledToolManifest` is never offered.

### 8.5 Server tools
- **`lookup_knowledge(query, kind=auto)`**
  - **Sources:** JKR spec corpus (`app/knowledge/jkr_specs_kb.py`), `data/jkr_param_rules.json`, the naming schema (`data/naming_standards/`), the Revit API index (`ai.revit_api`, `app/knowledge/revit_api_kb.py`).
  - **Steps:** first expands the query into domain/API terms with one cheap Flash call (the RevitGeminiRAG idea), then retrieves.
  - **Returns** ≤ 6 snippets, each with `{source, title, cite, snippet ≤ 600 chars, id}`. `lookup_knowledge(id=…)` drills into one entry; replies are capped at 8 KB with an outline when longer (Blender Lab pattern).
  - **Fix first:** the known data bug where the "sumber" segment maps to two different parameter names (audit §C.5).
- **`get_skill(name)`** returns a playbook (markdown ≤ 6 KB). Skills are the triaged recipes (audit 03 §4: merge, convert or delete; freeze auto-grow).
- **`remember(fact)`** writes project memory for (tenant, project fingerprint). Facts are shown to the drafter in the reply ("Saved: this project uses 900 mm doors") and can be removed from the pane.
- **`ask_user(question, options[2–4], multi=false)`** — the pane renders tappable options; the answer returns as the tool result.

### 8.6 Model
- **Provider config per tenant:** `{base_url, api_key_ref, model, vision_model, thinking}`. Default `deepseek-flash` for chat and vision, thinking off.
- **Thinking is an experiment flag,** not a route. The eval decides whether "thinking on for the step after `get_skill` or a write preview" earns its latency.
- **Flash is a moving alias.** `deepseek-flash` follows DeepSeek releases, so the eval runs on every announced DeepSeek update before the version is trusted.
- **Images:** `look` images go in as image content; downscaled to ≤ 1024 px long edge in the add-in.

### 8.7 Billing and plans
- **Metering:** every model call goes through the existing gateway accounting (`app/routers/gateway.py`, `app/services/subscription.py`).
- **Plans map to tool groups**, e.g. find/check on lower tiers, bulk edit/build/`run_revit_code` commit on higher (exact mapping: open question Q3). The cloud leaves disallowed tools out of the toolbox, and the add-in refuses them (§7.2).
- **Usage warnings and upgrade sheets** reuse the existing pane components (`UsageWarningBanner`, `UpgradeSheet`, `TierBadge`).

### 8.8 Observability
- **One Langfuse trace per turn**, linked by session id, recording:
  - every model call's input/output (system prompt by hash);
  - every tool call's args and envelope (truncated to 4 KB);
  - latency per step;
  - token cost.
- **Environment labels** are set correctly per deployment (audit #14).
- **No silent catch blocks** in the loop: every exception becomes an envelope or a trace error.

### 8.9 Evals (gate for every change)
- **Corpus:** ≥ 60 real drafter prompts from Langfuse, BM + EN, all four request kinds, each with expected tools, expected facts and a "must not claim" list. Hard cases from the audit are included (Kod_Komponen meaning, Toilet OKU, fire doors, ceiling offsets, grids, house from brief).
- **Deterministic runs:** a fake add-in replays recorded envelopes per prompt (fixture frames). It runs in CI-less one-command form (`scripts/test-copilot.sh`, plan B01).
- **Graders:**
  - right tools used;
  - every number in the reply appears in an envelope;
  - reply language matches;
  - no dumps or code in the reply;
  - model calls per turn;
  - wall time.
- **Live battery:** the same corpus against a real Revit on a Windows box, run before each cutover step and each DeepSeek update. Autodesk Design Automation can later run tool-level checks headless; it's not needed for v1 of this design.
- **Baseline first:** run the corpus against today's copilot before building, so "better" is a number.

## 9. Protocol (frames, JSON over WSS)

Client → server
```
hello        {token, addin_version, protocol: 3, revit_version, manifest{version, tools[]}, snapshot}
user_message {turn_id, text, attachments[], snapshot}
tool_result  {call_id, envelope}                 # one per call, also for each call in a batch
progress     {turn_id, call_id?, kind: running|awaiting_confirm|revit_busy, text}
cancel       {turn_id}
reconcile_result {items[{key, state, envelope?}]}
heartbeat    {ts}
```
Server → client
```
hello_ack    {session_id, plan, allowed_tools[], limits}
tool_call    {turn_id, call_id, idempotency_key, tool, args, timeout_s}
tool_batch   {turn_id, batch_id, calls[{call_id, idempotency_key, tool, args}], summary}
reply        {turn_id, text_delta | text, final: bool}
ask          {turn_id, call_id, question, options[], multi}    # rendered by the pane
turn_end     {turn_id, status: done|interrupted|cancelled|failed, receipt_ids[]}
reconcile    {keys[]}
heartbeat    {ts}
error        {code, message}
```
- `protocol` is bumped on breaking changes. `hello_ack` refuses mismatches with a clear "update the add-in" message (reuse `UpdateGate` flow).
- Reads time out at 50 s by default (`timeout_s`). A batch waits for the confirm with no fixed limit while `awaiting_confirm` heartbeats arrive, up to the add-in's 10-minute confirm expiry.

## 10. Envelope

```json
{
  "ok": true,
  "status": "done | partial | failed | rejected | noop | revit_busy | timeout",
  "summary": "Set Offset = 2700 mm on 34 ceilings (Level 2).",
  "data": {},
  "count": 34,
  "handle": "h_7f3a",
  "changes": {
    "touched": {"added": [], "modified": [{"id": 123, "field": "Height Offset From Level", "before": "2400 mm", "after": "2700 mm"}], "deleted": []},
    "side_effects": {"modified_by_category": {"Rooms": 6, "Schedules": 2}}
  },
  "verified": {"method": "param_readback", "checked": 34, "mismatches": []},
  "warnings": [{"text": "…", "severity": "warning", "ids": [123]}],
  "dialogs": [],
  "error": {"code": "not_found", "message": "No ceiling type 'CL-02'", "candidates": ["CL-02A", "CL-20"]},
  "next": "Use handle h_7f3a to highlight them."
}
```
- **`ok`** = committed and verified for writes; ran without error for reads. Never "didn't throw".
- **Units:** values are in the project's display units with the unit stated (fixes `set_parameter` echoing feet, audit #11).
- **Long lists** return ≤ 50 rows + `count` + `handle`; the handle can be passed to `highlight`, `inspect` or writes, so ids never round-trip through the model.
- **Every "not found"** carries `candidates` (reuse `TypeCandidates.Nearest`; test-enforced).

## 11. Core tools (always in the toolbox)

| Tool | Where | R/W | Purpose |
|---|---|---|---|
| `model_overview(sections[])` | add-in | R | Digest: levels, grids, rooms (name + level), types per category, views/sheets, links, warnings. Section-selectable, truncation hints |
| `find_elements(filter)` | add-in | R | By category / level / type / parameter / room / link / in-view; returns count + rows + handle |
| `inspect(ids \| handle, fields[])` | add-in | R | Instance/type parameters, in display units |
| `highlight(ids \| handle, zoom)` | add-in | R | Select + zoom; no transaction |
| `look(target, mode)` | add-in | R | Image of active view / a view / the touched elements (temporary isolate + export, settings restored); caption with real size |
| `run_revit_code(code, mode)` | add-in | R/W | §7.6 |
| `find_tools(query)` | cloud | — | Pull typed library tools into the next call |
| `lookup_knowledge(query \| id)` | cloud | — | §8.5 |
| `get_skill(name)` | cloud | — | §8.5 |
| `ask_user(question, options)` | pane | — | §8.5 |
| `remember(fact)` | cloud | — | §8.5 |

The typed library (176 tools today) stays. Typed writes like `set_parameter_bulk`, `create_wall`, `place_door` and `rename_elements` are preferred over code for common jobs because their verification is declared and tested. Existing tools map onto the core: `get_scene_overview` → `model_overview`, `find_elements_by_filter` → `find_elements`, `get_element_parameters` → `inspect`, `select_elements` → `highlight`, `capture_view_image` → `look` (now returns the image instead of a file path).

## 12. Fences (tests that fail the build)

- System prompt ≤ 2,048 bytes.
- Core tools ≤ 12; schemas per model call ≤ 24.
- Loop module (`assistant/loop.py` or equivalent) ≤ 600 lines.
- No function in the cloud path may modify model output text (a test asserts the reply sent equals the model's text).
- No `.Commit();` in any add-in file that uses `StartSwallowing`.
- Every write tool has a verification declaration; every write tool is in the add-in read/write list.
- Every "not found" throw goes through the candidates helper.
- New tool, prompt rule, skill or flag: the PR must include an eval case it improves.
- Feature flags: at most 5 for the copilot path, each with a removal date.

## 13. Security and trust

- **Cloud compromise must not equal silent writes.** Writes need the add-in's own confirm card. The add-in decides what counts as a write. Code runs only through the add-in allowlist.
- **Socket:** WSS only. Token from BINA login, per-session scope from `hello_ack` (plan, allowed tools). No inbound listener in the add-in: retire `McpServer` and its `HttpListener` at cutover.
- **Prompt injection** (text in DWG/PDF/linked models, project parameters) cannot approve anything. `ask_user` and confirm answers come only from the pane.
- **Tenant isolation:** project memory, receipts, sessions and traces are keyed by tenant; tests cover cross-tenant reads.
- **Data sent to the model:** documented per tenant (snapshot fields, tool results, images when `look` is used). The provider is per tenant (D9).

## 14. Failure modes

| Situation | Behaviour |
|---|---|
| Socket drops mid-turn | Turn ends `interrupted`; pane shows what completed (from envelopes it already has). On reconnect the cloud sends `reconcile` for in-flight keys; nothing is re-applied |
| Revit busy / modal dialog | `McpJobPump` watchdog fails fast → envelope `revit_busy` with the dialog text; the model tells the drafter to close it; no retry loop |
| Doc switched or closed mid-turn | Every tool call checks the doc fingerprint from the turn's snapshot; mismatch → `failed` code `doc_changed`; turn ends asking the drafter to resend |
| Model changed between preview and Ya | Revision mismatch → re-preview, card updates (§7.4) |
| Commit diff ≠ preview diff | RollBack, re-preview with an explanation |
| Element owned by another user (workshared) | Envelope `failed`, code `not_owned`, `candidates` = elements that are editable; the snapshot's worksharing fields let the model warn before trying |
| DeepSeek slow / rate-limited / down | One retry with backoff, then the turn ends with a plain "the AI service is unavailable" message; no fallback model unless configured for the tenant |
| Add-in older than the protocol | `hello_ack` refuses with the update message (existing `UpdateGate`) |
| Reply claims success but writes failed | Pane shows the fixed caution line above the receipt (§8.2); the receipt shows the truth |

## 15. Migration (one path at a time; the old copilot keeps working until cutover)

**Phase 0 — truth fixes on today's copilot.** These help now and are reused later.
- A01 (`CommitOrThrow` everywhere + test), A02 (failed tools show ✗; `NotImplemented` returns `ok:false`), A03 (return warnings; display units).
- Make the confirm card depend on an add-in-owned write list.
- *Exit:* lint test green; a rolled-back write shows ✗ in the pane.

**Phase 1 — baseline.**
- B01 one-command backend tests.
- The eval corpus + fake add-in (§8.9), run against today's copilot.
- Verify WebSockets through kamal-proxy and Cloudflare on staging (idle timeout, 120 s `response_timeout`).
- *Exit:* baseline numbers recorded; a test socket holds for 30 min on staging.

**Phase 2 — read-only copilot on the new path** (per-user flag, staging only).
- Hub, loop, envelope adapter, snapshot, core read tools (`model_overview`, `find_elements`, `inspect`, `highlight`), `find_tools` over read tools, `ask_user`, tracing.
- *Exit:* find/check prompts ≥ baseline on accuracy and ≤ baseline on wall time.

**Phase 3 — writes.**
- `tool_batch`, the preview/commit flow, confirm and receipt cards from envelopes, Undo (§7.5), ledger keys on the socket, typed write tools via `find_tools`.
- *Exit:* no false "done" on the eval's edit cases; one-click Undo verified on a live Revit.

**Phase 4 — act-see-fix.**
- `run_revit_code` with the allowlist and three modes; `look` with images.
- *Exit:* code-requiring eval cases pass with in-loop self-fix; allowlist tests green.

**Phase 5 — knowledge and memory.**
- `lookup_knowledge` (JKR, params, naming, API), skills from triaged recipes, `remember`.
- *Exit:* JKR "what does X mean / what is required" cases answered with a citation ≥ 90%.

**Phase 6 — cutover.**
- Flip the default for all users once every eval metric is ≥ baseline, keeping the old path for 2 weeks as a per-user fallback.
- Then delete (§17).

## 16. Reuse map (don't rebuild)

**Add-in**
- **Link and tool execution:** `McpTunnelClient.cs` (socket client), `McpJobPump.cs` (thread hand-off + busy watchdog), `ToolRegistry.cs` + `ToolManifest.g.cs` + `scripts/gen-tool-manifest.py`.
- **Write safety:** `TxGuard.cs` (after A01/A03), `BatchExecutor.cs` (TransactionGroup + `$refs`), `TurnReceiptService.cs` (DocumentChanged recorder, flash/zoom, badges), `OperationLedger.cs` (+ `reconcile`), `DocumentRevisionTracker.cs` (fingerprint, revision, `changes_since`), `TypeCandidates.cs`.
- **Code and UI:** `CodeExecutor.cs` (Roslyn compile + fail handler; add the allowlist walk), `UpdateGate`, the Copilot pane (`UI/Copilot/*`), `BinaVibe/Auth/*`.

**Backend**
- **Billing:** gateway accounting (`app/routers/gateway.py`), `app/services/subscription.py`.
- **Knowledge and help:** `app/knowledge/jkr_specs_kb.py`, `app/knowledge/revit_api_kb.py` (+ `ai.revit_api` pgvector), `data/jkr_param_rules.json`, `data/naming_standards/*`, `app/agents/revit/copilot/tool_help.py`, recipes (`app/knowledge/revit_recipes/*.md`) as skill sources.
- **Observability and tests:** `app/observability.py` (Langfuse), `evals/revit_copilot_replay.py` fixtures, `tests/test_tunnel_protocol.py` (frame-shape tests to extend), `RoslynGate` sidecar.

## 17. Delete at cutover

**Backend**
- `app/services/revit_turn.py` (the turn pipeline incl. `_emit_pending_or_done`) and `app/routers/revit_turn/*` turn routes.
- The truth gate / repair / anti-bluff / synthesis paths in `revit_response_shaping.py`.
- The five keyword routers; recipe push + API block injection.
- Agno `LearningMachine`, tool-result compression and session summaries.
- The Agno agent factories in `revit_ai.py`, and most `REVIT_*` flags.
- `app/engine/*` once no add-in in the field uses engine mode (keep `mcp_facade.py` only if the MCP door is wanted later).

**Add-in**
- `ToolLoopRunner` pending/resume path, `RevitCopilotExecutor` + `CodeExecutionHandler` (after-reply codegen), and `ConfirmGate`'s backend-flag dependency.
- `McpServer` (`HttpListener`), `EngineManager` / `EnginePreflight` / engine bundle, and the `DEPRECATED_*` files.

## 18. Open questions (owner)

- **Q1 Data residency.** Do government/JKR customers restrict sending project data to DeepSeek (China)? The design supports a per-tenant provider either way; the answer decides whether a non-China host is needed at launch.
- **Q2 Why was the WSS tunnel switched off in June** (2026-06-04, `bina-ai` `33db509` says "in favour of external_execution" with no reason)? If it was a transport problem (proxy, Cloudflare, timeouts), Phase 1 must prove the fix first.
- **Q3 Plans → tool groups.** Which request kinds and tools belong to which paid plan?

## 19. Left to the builder

- Exact module and class names, frame field names beyond those listed, and the Postgres schema for sessions/receipts/memory (follow `bina-alembic` conventions).
- Allowlist edge cases, as long as the rejected categories in §7.6 stay rejected.
- `find_tools` scoring method (keyword, embedding or both), within the 24-schema cap.
- Pane visuals for the new cards, reusing existing components and design tokens.
- Starting latency targets in G4 may be replaced by the Phase 1 baseline.
