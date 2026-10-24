# BINA Copilot — Lean Harness Design

**Date:** 2026-10-24 · **Status:** proposed, revision 3 (after a builder check, five specialist reviews — Revit API, cloud backend, security, agent quality, solo delivery — and the owner's answers to Q1–Q5), awaiting owner approval
**Scope:** both repos — add-in `revit-addin-sync` and backend `bina-ai`. This file lives in the add-in repo; the backend gets a pointer when work starts.
**Evidence** (owner's folder `~/Desktop/Copilot Improvement/`):
- `01-audit-findings.md` — 30 days of Langfuse plus a code audit.
- `03-evaluation.md`, `04-redesign-and-implementation-plan.md`.
- `05-github-landscape.md` — source audit of about 25 open-source Revit AI projects plus Blender MCP.

**Revision 2 in one paragraph.** Every flaw the reviewers found is fixed here:
- **Transport:** HTTP + SSE with turn state in Postgres, instead of a new WebSocket. It survives deploys, needs no hub, and the add-in already has the client.
- **Preview/commit:** bound to a *plan hash*, not element ids. Revit gives re-created elements new ids, and the preview's own rollback moves the revision counter.
- **Ledger:** keyed by phase, so a preview can't satisfy the commit.
- **`run_revit_code`:** deny-by-default at the symbol level, with a loop budget, and moved later in the plan.
- **Confirm card:** built only from what the add-in measured.
- **Server-decided path switch:** ships in the first signed release, because the production fleet runs the local engine today.
- **Other products:** shared backend code is extracted before anything is deleted.

---

## 1. Why

Drafters say the copilot "bluffs". The audit shows most of it is the system misreporting to itself, then patching the reply:

- **The add-in lies to the model.**
  - 60 bare `Commit()` calls after `StartSwallowing` in `Mutators.cs` (plus 8 other files) report `ok:true` after Revit rolled back.
  - `ToolLoopRunner.cs:802` marks any job that didn't throw as `Ok = true`, even when it returned `{ok:false}`.
  - Revit warnings are deleted and never returned (`TxGuard.cs:84-97`).
- **The backend then rewrites the reply.** `_emit_pending_or_done` (`app/services/revit_turn.py:880` to the end of the file, about 1,060 lines) stacks guards named after past incidents: four anti-bluff continuation triggers, a truth gate, a "repair" pass using the C# compile-fix agent, raw-dump synthesis and a narration clamp. They collide: correct answers turn into robotic Malay, raw dumps ship as answers, stop messages get overwritten.
- **It is heavy.**
  - 23.5k-char prompt, about 105 tool schemas (about 82k chars) per call, 39 `REVIT_*` flags.
  - Agno LearningMachine, compression and in-turn summaries: 32.5% of model calls are housekeeping.
  - p50 18 s of model time per turn.
- **Paused runs live only in memory.** Agno keeps a paused run in a per-process dict, so production must run `--workers 1`, and confirms still got lost ("run not found", `bina-ai/Dockerfile:162-178`).
- **Cloud changes don't reach drafters today.** Production drafters run the *local engine* (`BinaConfig.cs:565-567` turns engine mode on whenever a gateway URL is set; CI bakes one in). Behaviour ships only with signed add-in releases.
- **It regrew once already.** The June "lean codegen rebuild" (`bina-ai` `e4708f5`, 2026-06-05) had become the pipeline above by October.

**What works elsewhere:**
- **Blender MCP** (32k★): 9 tools, 1.5k-char instructions locked by tests, code that runs inside the loop and returns tracebacks, and a `look` tool that returns real images.
- **The most truthful Revit projects** (Horizun, bimwright, KenLP, tzur): they check commit status, return warnings, read back after writes, and bind approval to a real dry run.
- **No project does both.**

## 2. What it is (owner-approved description)

A **personal drafter inside Revit** that handles all four kinds of request drafters make today: check the model (~35%), find/highlight (~30%), bulk edits (~15%), build/model (~15%). It should feel like a colleague who has been on the project from the start:

- **Context:** it knows what the drafter is looking at (active view, level, selection) and what's in the model (levels, grids, rooms, families and types, sheets, links, warnings).
- **Rules:** it knows the project's rules: the JKR standard, naming, required parameters and firm habits.
- **Memory and language:** it remembers earlier work and speaks the drafter's language (BM or EN).
- **Productivity:**
  - It does the tedious parts faster than by hand, and simple requests take seconds.
  - Results come with proof: highlight, picture, a real diff, and what was and wasn't searched. The drafter doesn't have to re-check.
  - When something is unclear, it asks one question with real options.

## 3. Goals, non-goals, anti-goals

**Goals**
- **G1 Truthful.** Every success shown is backed by Revit's own record. Every "none found" says what was searched.
- **G2 Context-aware.** It understands context automatically, without asking the drafter for an element id.
- **G3 All four request kinds** on one loop.
- **G4 Fast.** Find/highlight in ≤ 2 model calls, p50 ≈ 6–8 s wall time. Edits p50 ≈ 12–18 s, excluding the drafter's thinking time. The Phase 1 baseline replaces these starting targets.
- **G5 Light.** One path, a small loop, and size fences enforced by tests and CI (§12).
- **G6 Controlled from the cloud.** Prompts, tools, models, plan limits and the copilot path itself can change without an add-in release. This becomes true once the path switch ships (§15, P0).
- **G7 Measurable.** An eval on real drafter requests gates every change.

**Non-goals**
- Not a public MCP server.
- No background or unattended changes: every write happens in a turn the drafter started.
- Doesn't replace the JKR, BOMBA and cost dashboards.
- No local engine.
- No WebSocket in v1. It can be revisited after cutover if real cancel or progress needs justify it.

**Anti-goals** (failure even if it "works")
- Claiming something happened that Revit didn't record.
- Changes the drafter didn't approve, or an Undo that reverts the drafter's own work.
- Robotic replies, raw dumps, or rewritten answers.
- Regrowth into a pipeline of guards.

## 4. Constraints

- **One maintainer** (the owner) for both repos. Simplicity beats completeness.
- **Revit 2023–2027:**
  - `net48` → 2023/2024, `net8.0-windows` → 2025/2026, `net10.0-windows` → 2027.
  - Version gaps: `ElementId.Value` and `CreationGUID` exist only from 2024; `SelectionChanged` from 2023.
- **Revit API facts this design obeys** (confirmed by the Revit API review):
  - All API work runs on Revit's UI thread through `ExternalEvent`/`Idling`, which fire only when Revit is idle. Code there runs synchronously and can't be aborted.
  - A `Transaction` or `TransactionGroup` can't outlive its callback. `Assimilate` merges everything into one undo entry named after the group. Nested groups fold into the outer one.
  - There is no Undo API. `PostCommand(Undo)` runs after the API context ends, one per context, against the *active* document's top undo entry.
  - Re-created elements get new ids. A preview's own commits and its group rollback raise `DocumentChanged`. Change sets are inflated by regeneration.
  - Temporary isolate/hide needs a transaction. `ExportImage` doesn't, and works on views that aren't open.
  - `DialogBoxShowing` fires before a modal opens: answer it, or the callback blocks until the drafter answers.
  - Some writes can't run inside a group or be previewed: sync with central, reload latest, save, link load/unload, export, relinquish.
- **Releases:** add-in releases are slow (production signing is operator-only on the Windows signing box, about one per week). Behaviour that changes often belongs in the cloud; the add-in should be a stable, honest executor. Plan for **four signed releases** in total (§15).
- **Backend hosting:** Docker on a VPS behind kamal-proxy (`config/deploy*.yml`, `drain_timeout: 30`), with Cloudflare in front. Today it runs one uvicorn worker. bina-ai has **no CI**.
- **Tenancy is per user (owner decision, Q5).** `tenancy.py:27-60` already resolves the tenant to `user_<id>`. Project memory, receipts, sessions and settings belong to one drafter; nothing is shared across a firm in v1. If firms are added later, only the key prefix changes.
- **Data residency: no customer restriction (owner answer, Q1).** DeepSeek is the provider for everyone. Cloudflare in front is fine.

## 5. Decisions

| # | Decision | By | Why |
|---|---|---|---|
| D1 | The agent loop runs in the **cloud** (bina-ai) | Owner | Control and monetization; changes without add-in releases; no backend source on drafters' PCs (today's engine bundle ships `app/` source) |
| D2 | Model family: **DeepSeek** | Owner | — |
| D3 | **`deepseek-flash`** (V4.1-Flash-1017) for every call; thinking off by default with the bounded policy in §8.2; `deepseek-v4-pro` only as an eval-proven fallback | My call | Flash beats V4-Pro on agentic/tool-use benchmarks (DeepSeek-reported), accepts images (needed for `look`), and costs about 4× less on input and 3× less on output. Peak pricing falls in Malaysian working hours |
| D4 | **Our own loop** replaces Agno *for the copilot* (Agno stays for other agents and the knowledge layer) | My call | With our own durable turn state, Agno's pause/resume and extras are the bloat |
| D5 | **Transport: HTTP + SSE (protocol 3), with turn state in Postgres.** The loop is a pure function of the persisted turn: each request loads it, runs until the next tool request or the end, persists, and streams events | My call (revised) | Survives deploys and blips. Any worker can continue a turn. Nothing is held in memory while a drafter decides. The add-in already has an SSE client. Avoids building a WebSocket hub and rewriting the dead tunnel client |
| D6 | **The add-in owns approval and safety:** which tools write, the confirm card's content, and the code policy. The cloud can't skip or reword them | My call | Today the card depends on the backend's `mutate` flag (`ToolLoopDtos.cs:224`) and the backend's `AutoApprovable` (`RevitChatRouter.cs:336`) |
| D7 | **The writes from one model step form one batch.** Preview callback (rolled back), then the card, then a commit callback in one named TransactionGroup. Approval is bound to a **plan hash** computed before any change | My call (revised) | This is the only grouping Revit allows across a decision, and ids can't be compared between preview and commit |
| D8 | **`run_revit_code` stays, but arrives late** (Phase 4), deny-by-default at the symbol level, with a loop budget, and the add-in decides the mode | My call (revised) | Act-see-fix is what makes Blender MCP feel smart, but read mode is not inherently read-only and a runaway loop freezes Revit |
| D9 | **Every model call goes through one provider client** built from config (base URL from a staff-only allowlist), with an unused per-user override field | My call | No customer needs another provider today (Q1). One seam keeps it a config change if that ever changes. V4.1-Flash weights are MIT-licensed |
| D10 | **Size fences enforced by tests and a 1-minute CI job** (§12) | My call | The June rebuild regrew without fences, and bina-ai has no CI |
| D11 | **A server-decided copilot path** (per user, in the database), shipped in the first signed release; it skips the engine when the path is the lean one, and falls back to the old path automatically if the new one fails | My call (new) | This is what makes G6 true and gives a rollback that needs no signed release |
| D12 | **Extract shared code before deleting anything;** delete the old path only after 14 straight days with zero legacy turns, and at least 6 weeks after the flip | My call (new) | `revit_ai.py`, `revit_response_shaping.py` and the engine are imported by other products and by add-ins still in the field |

## 6. Architecture

```
 Drafter
   │
┌──▼──────────────── Revit add-in (C#) ─────────────────────────────┐
│ PANE      chat · ask cards · confirm card (add-in-measured        │
│           preview) · receipt + evidence strip · Undo · busy state │
│ CLIENT    path switch (server-decided) · protocol-3 HTTP+SSE      │
│           client · executes tool requests only for its own turn   │
│ TOOLS     tool host on Revit's thread (McpJobPump)                │
│           · 12 core tools + library of 176 typed tools            │
│           · add-in-owned write list · code policy · turn budget   │
│ GUARD     CommitOrThrow · collect warnings + dialogs · read-back  │
│           · diff (touched first) · plan hash · TransactionGroup   │
│ CONTEXT   event-maintained snapshot (doc, view, level, selection, │
│           revision, units, phase, worksharing, links, doc kind)   │
│ LEDGER    idempotency keyed by (key, phase)                       │
└──▲────────────────────────────────────────────────────────────────┘
   │  POST /copilot/turns · POST …/results · GET …/events · POST …/cancel
   │  SSE events: progress · reply_delta · tool_request · ask · turn_end
┌──┴───────────────── bina-ai cloud (Python) ───────────────────────┐
│ TURNS     durable turn_events (Postgres) · any worker continues   │
│ LOOP      load turn → model → tool requests → persist → … → reply │
│           nothing edits the reply                                 │
│ TOOLBOX   fixed per session: 12 core + call_tool; library schemas │
│           as text (preloaded by retrieval or via find_tools)      │
│ SERVER    lookup_knowledge (+ exact-id linking) · get_skill       │
│ TOOLS     · ask_user · remember (pane-confirmed)                  │
│ MODEL     one provider client · deepseek-flash                    │
│ BILLING   charge per model call from usage (cache hit/miss)       │
│ QUALITY   manual Langfuse generations (masked) · eval simulator   │
└───────────────────────────────────────────────────────────────────┘
```

## 7. Add-in

### 7.1 Path switch and client
- **Path switch.** At pane open and on "New chat", the add-in asks the backend which path this user is on: `legacy` (today's behaviour, including engine mode) or `lean`.
  - `lean` skips the engine preflight and uses the protocol-3 client.
  - If the path call or the first `lean` request fails within 5 s, the add-in falls back to `legacy` for that session.
  - The seam already exists: `RevitChatRouter : IChatRouter`.
- **Protocol-3 client:**
  - It extends the existing SSE client used by `ToolLoopService`; no WebSocket.
  - The token goes in the `Authorization` header, never in a URL.
  - Release builds accept HTTPS only, to hosts on a compiled-in allowlist; config can't point the copilot elsewhere in release builds.
- **Turn binding.** The add-in executes a `tool_request` only if it arrives on the response stream of a turn the pane itself created, before that turn's `turn_end` or cancel.
  - Per-turn budgets are compiled in and enforced in the add-in: tool calls, bytes returned, images. They're shown in the step list.
  - The cloud can only narrow these limits, never raise them.
- **Version gate.** The protocol version travels with every request. A mismatch gets the existing "update the add-in" flow (`UpdateGate`).

### 7.2 Tool host
- **Dispatch** goes through `ToolRegistry.Invoke` on Revit's thread via `McpJobPump`, keeping its busy/modal fast-fail watchdog.
- **The write list is the add-in's own.** A static read/write classification is generated with the manifest (`scripts/gen-tool-manifest.py`). A test fails if a registered tool is missing from it. A write arriving in a read request is refused.
- **Every tool returns the envelope** (§10). A shared adapter wraps tools that return bare dictionaries today.
- **Unknown tools.** A tool not in this build's manifest, or not allowed for the user's plan, returns `failed` with code `not_allowed`.
- **New path, typed writes only when verified.** The new path offers only write tools that have a verification declaration (§7.3). It starts with the ~12 most-used write tools from Langfuse, beginning with the tools that already have `dry_run` plus a re-read check (`set_parameter_by_filter`, `swap_type_by_filter` and others in `BulkEdit.cs`, `SpatialEdit`, `Dimensioning`, about 24 in `Mutators.cs`).
- **Existing features move over.** The vetted catalog, tool forms and `/open-view` currently run through `RevitCopilotExecutor`; they move onto this tool host.

### 7.3 Guard (every write)
1. **Commits are checked.**
   - Every `Transaction` uses `TxGuard.StartSwallowing` plus **`CommitOrThrow`**.
   - `CommitOrThrow` fails on *any* status other than `Committed`, not only `RolledBack`.
   - Fence: no `Commit(` anywhere under `BinaVibe/Mcp/Tools` outside `TxGuard` (plan A01).
2. **Warnings are collected, then deleted.** The failure preprocessor collects warnings (text, definition id, severity, element ids), then deletes them so no modal appears; errors roll back and their text is captured (plan A03). A `FailuresProcessing` backstop is subscribed during batches for transactions that bypass our preprocessor.
3. **Read-back.** Each write tool declares how it verifies: re-read parameters, element exists or is gone, or count delta.
   - A test-enforced catalogue fails the build if a write tool has no declaration (the Horizun `WriteVerificationCatalog` rule).
   - `ok` means committed **and** verified; a mismatch makes the status `partial`.
4. **Diff.** A `DocumentChanged` recorder collects the changes and is scoped by document and by a *callback flag* (§7.4).
   - It subtracts ids that were reverted and filters known bookkeeping elements.
   - `touched` holds the ids the tool reports; everything else is summarized by category (`side_effects`).
   - If nothing real was recorded, the status is `noop`.
5. **Edit modes.** Edit modes (group, sketch, in-place family) make `Transaction.Start` throw; that maps to `revit_busy` with a plain reason.

### 7.4 Preview and commit
The cloud sends the writes from one model step as one `tool_request{kind: write}`. Reads in the same step run first, separately, with no confirmation.

```
1. plan:      resolve targets BEFORE any change → plan hash =
              hash(tool, canonical args, resolved ids of existing targets, intended values,
                   expected added/deleted counts per category, code text if any)
2. preview:   [callback A, flag=PREVIEW] TransactionGroup → run batch → envelopes
              (diff, warnings, read-back) → image or TemporaryGraphics markers of created
              elements → RollBack → THEN capture external revision R
3. card:      §7.5, built from step 1–2 measurements
4. commit:    [callback B, flag=COMMIT] external revision == R ? else back to 2 (once) →
              TransactionGroup "BINA: <tool label>" → re-run the STORED batch (never a
              re-sent one) → compare result shape to the plan: modified/deleted by exact
              id, added by multiset (category, type, level, rounded bbox) →
              match: Assimilate · mismatch: RollBack, one re-preview, then fail `diff_drift`
```
- **External revision.** This counts only commits, undos and redos raised **outside** our own preview, read and `look` callbacks. Our callbacks set a flag while they run. Without this, a preview's own events would move `R` and the commit would re-preview forever (Revit API review).
- **Ledger** (`OperationLedger`): keyed by `(idempotency_key, phase)`. Only the commit phase is recorded and reconciled, so a preview can never satisfy a commit.
- **Worksharing.** Before previewing, check the targets with `WorksharingUtils.GetCheckoutStatus`. Elements owned by others return `failed: not_owned` with editable candidates, without borrowing anything.
- **Writes that can't be previewed** (save, sync with central, link load/unload, export) get an *estimate-only* card that says so plainly.
- **Nothing is held open while the drafter decides.** The turn is persisted in the cloud and the add-in holds the stored batch. Tidak returns `rejected` with `ok:true` (a decision, not a failure).
- **Cost.** Preview plus commit costs about 2–3× the regeneration. Turn-scoped build approval (Q2) controls how often builds pay that.

### 7.5 Confirm card (cannot be spoofed by the cloud)
- **The headline and body come only from add-in measurements:**
  - counts by category, with **deletions first and in red**;
  - before→after values from read-back;
  - side effects;
  - warnings;
  - tool labels the add-in owns;
  - a thumbnail or markers of created elements;
  - code shown in full for `run_revit_code`.
- **Cloud or model text** appears only on a separate line labelled "Assistant's description".
- **Ya is bound** to the plan hash of the batch the add-in holds.
- **A hard-warning style** is used when there are deletions or side effects larger than the touched set.

**Approval modes** (replace today's backend-driven "Auto" toggle; my call, Q2):

| Mode | When | Behaviour |
|---|---|---|
| **Ask each change** (default, and the only mode for edits) | Every write batch | Preview → card → Ya/Tidak, as above |
| **Approve this build** | Offered on the *first* card of a turn whose batch contains only create tools (walls, floors, levels, grids, doors, windows, roofs…) | One tap approves the remaining create-only batches **of this turn**. The add-in enforces the scope: the same turn only, declared create tools only, **no deletes, no edits to existing elements, no `run_revit_code` commit**, at most 500 new elements in total. Each batch still previews, still verifies, still gets a receipt and its own Undo entry. Anything outside the scope gets a normal card. The scope ends at `turn_end` |

- Reads never ask.
- The old Settings toggle is removed. Drafters who had Auto on see a one-time note: "Auto approval is now 'Approve this build', offered on build jobs."
- Why this works:
  - The approval-fatigue problem lives in builds (4–8 batches per turn).
  - The risky changes are edits and deletes of existing work, and those always ask.
  - The cloud can't widen the scope, because the add-in checks every batch against it.

### 7.6 Undo
- **One entry per batch.** Each committed batch is one named entry: "BINA: Set ceiling offsets L2". A single prefix is used everywhere; today's recorder filters on "BinaVibe" and must change.
- **Modelled stack.** The add-in keeps a per-document stack (Committed/Undone/Redone) from `DocumentChanged` operations.
  - Only groups that actually committed count; a no-op group leaves no entry, and undoing it would revert the drafter's own step.
  - Our own tint and clear transactions must not sit on top: highlight with selection only, or name the tint "BINA:" and include it.
- **The Undo button** posts one `PostCommand(Undo)` per BINA entry in the turn, newest first, one per Idling.
  - Before each post: the active document's fingerprint matches, and `CanPostCommand` is true.
  - After each post: it waits for `DocumentChanged(TransactionUndone)` and checks the undone transaction name equals the expected group name. If not, it posts `Redo` at once and stops.
- **When it's disabled:**
  - any non-BINA commit since ours;
  - the drafter's own undo or redo of our entries;
  - `DocumentSynchronizedWithCentral` / `DocumentReloadedLatest`;
  - another add-in committing in reaction to our change (safe, but expect it).

  The card then explains that the drafter should use Revit's Undo list, where the entries are named.
- **No compensating "reverse" transactions.**
- **Replaces** `TurnReceiptService.cs:289-295`, which today undoes 1 of N (audit #21).

### 7.7 `look`
- **Touched elements or a region:**
  - TransactionGroup → one transaction creates a temporary `View3D` with a section box around the target bounding box → commit → `ExportImage(SetOfViews)` with no transaction open → `group.RollBack()`.
  - This leaves no undo entry and doesn't touch the drafter's view (Horizun `VerifyChangesSnapshot.cs`).
- **Active view:** export with `VisibleRegionOfCurrentView`, which needs no transaction.
- **Image settings:** `PixelSize = 1024`, temp files cleaned up, about 1–5 s. If export fails (no GPU, RDP), the call returns `failed: no_gpu`.
- **Thumbnails.** The drafter sees a thumbnail of every image the model saw.
- **Limits.** The tool description says `look` is for orientation, never for counts or dimensions.

### 7.8 `run_revit_code` (Phase 4)
- **Input:** a C# statement list for a method body. The wrapper is built as a syntax tree, and the build asserts that the model's text is *only* statements inside that body: no member or type declarations, attributes, `unsafe`, pointers, `stackalloc`, `dynamic`, `extern`, `lock`, or async.
- **Policy: deny by default.** The bound IOperation tree is walked; every member call, constructor, conversion, extension method, delegate and type reference is checked against **one checked-in policy file**.
  - **Allowed assemblies:** RevitAPI, RevitAPIUI, CoreLib/mscorlib, System.Linq, System.Collections. Nothing from the add-in or third parties.
  - **Allowed types:** primitives, `string`, `Math`, `Convert`, `StringBuilder`, generic collections, `Enumerable`. `typeof(T)` is allowed only as an argument (e.g. `OfClass`), with no member access on `Type`.
  - **Denied Revit members:**
    - any parameter that is a path, `ModelPath` or external resource;
    - `Export*`, `Save*`, `Open*`, `Close`, link and load-family, `SynchronizeWithCentral`, `ReloadLatest`, `WorksharingUtils.*Ownership`/`Checkout*`, `RevitLinkType.Load/Unload/Reload`, `PrintManager`;
    - `.Application`, `PostCommand`, `ExternalEvent`, `UpdaterRegistry`;
    - any event `+=`;
    - `TaskDialog`;
    - construction of `Transaction`, `TransactionGroup` or `SubTransaction` (the wrapper owns transactions).
  - The I/O denials apply in every mode.
  - **Changing the policy:** each denied category has a golden test, and the policy changes only together with a test. The policy and budgets are compiled into the signed add-in; the cloud can't configure them.
- **The mode is decided by the add-in,** whatever the request says:
  - Any symbol that could modify the document routes the call through §7.4.
  - Otherwise it runs read-only, *inside a TransactionGroup that is always rolled back*, and fails if the external revision moved.
- **Budget.** At every loop back-edge and every method or lambda entry, an inserted counter and clock check throws when the budget is exceeded, with a depth cap. On Revit's UI thread this is the only real limit. Print output and collections are size-capped (16 KB).
- **Errors:**
  - Compile errors carry line numbers *relative to the model's code*. The offset is computed, not hard-coded.
  - Runtime exceptions are unwrapped from `TargetInvocationException` (type, message, top frames in the model's code).
  - Policy rejections name the symbol and line so the model can rewrite.
- **Cleanup.** The Excel helpers are removed from the wrapper; spreadsheet I/O becomes a typed tool with a file picker.

### 7.9 Context snapshot
- **Kept current by events** (`SelectionChanged`, `ViewActivated`, `DocumentChanged`, `DocumentClosed`/`Opened`) rather than computed on demand. It's sent with every turn request, with a `stale` flag if Revit couldn't refresh it.
- **Fields:**
  - `doc{kind (project|family), title, path_hash, is_workshared, is_central/local/detached/read_only, units, phase, active_design_option, version (GetDocumentVersion), session_nonce}`
  - `view{name, type, level, scale, is_template}`
  - `selection{count, by_category (capped), ids ≤ 50, link_refs}`
  - `worksharing{active_workset}`
  - `links{name, loaded}[]`
  - `warnings_count` (cached)
  - `rooms` (names + levels when ≤ 150)
- **Dropped:** `owned_by_me_count` (needs a full model walk); ownership is checked per target in §7.4.
- **Overview cache.** The full `model_overview` is precomputed on Idling after the pane opens and cached by `(tenant, doc identity, session_nonce, GetDocumentVersion)`. Project identity never relies on `ProjectInformation.UniqueId` alone, because projects made from one template share it.
- **Links.** Handles to linked elements include the link instance id, because linked ids collide with host ids.

### 7.10 Dialogs and busy
- **Known dialogs.** A table of known dialog ids with safe answers (`OverrideResult`).
- **Any other dialog:** send `progress{revit_busy, text}` and let the call wait; it can't be aborted.
- **Revit thread busy:** when Revit isn't idle, the `McpJobPump` watchdog fails fast with `revit_busy`. The model tells the drafter to close the dialog; there's no retry loop.
- **The pane can't repaint during a long preview,** because WPF shares Revit's UI thread. The card shows "previewing…" *before* the callback starts.

### 7.11 Pane changes
- **Keep** the Copilot UI and its screens.
- **Evidence strip** on read answers, rendered from read envelopes: count, what was searched (host or links, categories, an echo of the filter), `not_searched` (e.g. unloaded links), and highlighted/visible counts.
- **Receipt headline** from envelopes, always as fractions: "30/34 done · 4 failed (not owned)". **A caution** appears whenever any write in the turn isn't `done`. The pane never reads the reply text to decide this.
- **Receipts in the drafter's language,** from localized templates over structured fields.
- **Ask cards** are labelled as coming from the assistant and styled unlike confirm cards. Options can't contain approval words, and the pane never accepts credentials.
- **Markdown** renders without remote images; link targets are shown.
- **Remove** the dependency on the backend `mutate` flag and `AutoApprovable`. The Auto toggle is replaced by the approval modes in §7.5.

## 8. Cloud (bina-ai)

New code lives in a folder named for what it does, e.g. `app/services/assistant/` (never `v2`). Routes stay thin.

### 8.1 Turns (HTTP + SSE, durable)
- **Routes:**
  - `POST /copilot/turns` (text, snapshot, attachments, client-generated `turn_id`);
  - `POST /copilot/turns/{id}/results` (envelopes or ask answers for a `request_id`; idempotent by `request_id`);
  - `GET /copilot/turns/{id}/events?after=<seq>` (replay after a dropped stream);
  - `POST /copilot/turns/{id}/cancel`.
- **Durable state:** append-only `turn_events(turn_id, seq, kind, jsonb)` rows, written in short transactions off the streaming path. Any worker can continue a turn after a deploy.
- **Workers.** The new path needs no per-process state, but the process keeps one worker for now: other products (the MS Project pause, `idempotency.py`, `burst_guard.py`) and a ~2 GB reranker per worker still assume one. Scaling is a later, separate decision.
- **Deploys.** A deploy mid-turn loses at most the in-flight model call. On the next request the loop re-runs from the last persisted state, and only completed calls are metered. Deploys still prefer `active_turns == 0` (exposed on `/v1/health`).

### 8.2 Loop
```
on POST turns / results:
  turn = load(turn_id); check plan + budget (every request, not once per session)
  loop while active_time < 120 s and steps < 20:
      out = model.stream(prefix + turn.messages + tail)        # §8.3
      if no tool calls: emit reply; emit turn_end(done); break
      server tools (lookup_knowledge, get_skill, call_tool validation) run inline
      reads  → persist, emit tool_request{kind: read}  → return (await results)
      writes → persist, emit tool_request{kind: write} → return (await results)
      stop if: same tool + same args twice · 3 consecutive failures with the same code ·
               cancel → reply from envelopes so far
```
- **Time budgets** count active time only. Waiting for confirm and `ask_user` is excluded.
- **Liveness, not fixed timeouts.** A read with no `progress` event for 60 s is failed. Results for unknown request ids are ignored.
- **Model streams** have an idle-read timeout.
- **Nothing edits the model's text.** The only deterministic additions are pane-rendered cards built from envelopes (§7.11).
- **Known-error hints.** A small table maps known compile and runtime error signatures (unit traps, common API misuse, missing `Regenerate`) to a hint added to that envelope's `next`, before the model sees it. It lives in the cloud and can change without a release.
- **Thinking policy.** Thinking is on for **one** step when:
  - the previous envelope was `failed` or `partial`, or code failed to compile;
  - `get_skill` was called this turn;
  - or the step number is ≥ 4.

  Otherwise `thinking:{type:disabled}` is sent explicitly. Reasoning tokens are capped. When thinking was used, `reasoning_content` is passed back on later calls (the API requires it when tools are present). Starts as an experiment flag that the eval A/B tests.

### 8.3 Context and caching
DeepSeek caches exact prefixes only, and a cache hit is 50× cheaper than a miss. The order is:

1. **Static system prompt**, ≤ 2 KB of principles:
   - read before writing;
   - numbers only from tool results;
   - never say "none" without stating what was searched;
   - meanings of JKR codes, parameters and naming come from `lookup_knowledge`, never from memory;
   - one question with real options when unclear;
   - verify after writing;
   - reply in the drafter's language;
   - say what failed.
2. **A fixed `tools` array for the session:** 12 core tools plus `call_tool(name, args)`.
3. **Skill catalogue:** names plus one-liners, ≤ 600 tokens.
4. **Project memory and the model summary,** frozen at session start and refreshed only on `remember` or an explicit refresh.
5. **Append-only history.** A turn's tool results collapse to their envelope `summary` when *that* turn closes. Compaction happens in large chunks; there's no LLM summarization.
6. **Tail:**
   - the snapshot;
   - **preloaded library schemas as text** (top 6–8 by deterministic retrieval over the user text, the selection's categories and the latest results, re-scored each step);
   - **linked knowledge entries** (§8.5);
   - the user text.

Fence: the static prefix (1–3) is ≤ 6k tokens, and each core-tool description is ≤ 600 chars.

### 8.4 Toolbox
- **Core tools (§11) are fixed per session.** Library tools are called through `call_tool(name, args)`. The cloud validates the args against the library schema before sending anything to the add-in.
- **Preloading.** Retrieval preloads the likely library schemas into the tail as text (§8.3), so common edits need no extra hop. `find_tools(query)` remains for misses, and its hop rate is an eval metric.
- **Search covers BM and EN:** Malay synonyms for tools *and* for model values (tandas, surau, aras, siling…).
- **One source of truth.** Library schemas come from a generated, checked-in `tool_schemas.json`, built from the backend tool signatures plus `tool_help.py` and intersected with the add-in's `InstalledToolManifest`. Server-side adapters (`apply_name_fixes`, `suggest_name_fixes`, `search_family_library`, …) are inventoried there too.

### 8.5 Server tools
- **`lookup_knowledge(query | id)`:**
  - Sources: the JKR spec corpus (`jkr_specs_kb.py`), `data/jkr_param_rules.json`, the naming schema (`data/naming_standards/`) and the Revit API index (`ai.revit_api`).
  - Returns ≤ 6 cited snippets; drilling in by id is capped at 8 KB with an outline.
  - Query rewriting uses **the same provider client** (today it goes to gpt-4o in `retriever.py:112-127`). The reranker runs out of the event loop, in a process pool or sidecar.
  - **Exact-identifier linking.** Before step 1, tokens that exactly match the parameter dictionary or a naming segment add their entries (≤ 3, ≤ 600 tokens) to the tail. This is a closed-vocabulary match, not a similarity search, and it skips the rewrite call when it hits.
  - **Fix first:** the "sumber" segment maps to two different parameter names (`Kod_Komponen_Sumber_jkr_stt` vs `Kod_Ciri_Komponen_jkr_stx`).
- **`get_skill(name)`** returns a playbook (≤ 6 KB). Skills are the triaged recipes; staff curate them. The recipe auto-grow loop stays frozen.
- **`ask_user(question, options[2–4], multi)`:** the answer comes only from the pane.
- **`remember(fact)`** (library, not core): the **pane confirms the exact text** before saving, and the author, date and source are stored. Memory and tool results are shown to the model inside delimited "data, not instructions" blocks, and admins can review memory.

### 8.6 Model and provider
- **Config:** `{base_url, api_key_ref, model, vision_model}`, with an optional per-user override that nobody needs today (Q1). The base URL comes from a staff-only allowlist.
- **Default:** `deepseek-flash` for chat and vision.
- **Every model call** (chat, vision, query rewrite) goes through that one resolved client; a test asserts no direct default clients are used.
- **`deepseek-flash` is a moving alias,** so the eval runs on every announced DeepSeek update.
- **Images** go in as image content, ≤ 1024 px on the long edge.

### 8.7 Billing
- **DeepSeek is called directly** through the shared cloud client, not through `gateway.py` (that's the engine's proxy, and it charges raw totals).
- **Usage → credits.** `tokens_from_usage(usage)` uses `prompt_cache_hit_tokens`, `prompt_cache_miss_tokens` and `completion_tokens`.
- **Charge after every model call.** `charge_run(turn_id, cumulative)` is already delta-idempotent and is keyed on `turn_id`, which is also the feedback `answer_id`.
- **Edge cases:**
  - Cancelled streams are estimated from what was streamed.
  - Server-side model calls (rewrite) are metered too.
  - The per-prompt credit and plan limits stay as today.
  - Weights are updated for Flash pricing (about 0.02 cached / 4.0 output relative to fresh input).
- **Plans → tools.** Plans map to tool groups through `capability_negotiation.py` (the exact mapping is a later decision). The add-in also refuses disallowed tools.
- **During the pilot,** both paths are metered side by side and compared.

### 8.8 Observability
- **Manual Langfuse `generation` observations** for the new loop, with `usage_details` (input, cached input, output) and `cost_details` computed in code (peak/off-peak).
  - The Agno instrumentor stays for the other agents, so nothing is counted twice.
- **Per turn:**
  - one trace;
  - tags: prompt hash, manifest version, add-in version, status;
  - cost also stored on the turn row, for per-tenant dashboards and invoice reconciliation.
- **Masking:** a `mask=` function hashes paths and drops image bytes (keeping hash and size). Content tracing has an off switch (no customer needs it today, Q1).
- **No silent catch blocks** in the loop.

### 8.9 Evals (gate every change)
- **World fixtures.** 2–3 projects are dumped once from a live Revit into a JSON model (levels, rooms, typed elements, parameters, an unloaded link). Start with BINA's own test model (the JKR `jkrAR24_5a` copy in `COPILOT-TESTING.md`); customer models only with consent (Q3).
- **Simulator.** A Python simulator answers the core tools for *any* arguments and applies writes as diffs to produce envelopes. Library tools use a cassette keyed by (tool, canonical args), filled by the live battery; an unrecorded call scores `unrecorded`.
- **Fault injection:** rollback, partial, `not_owned`, `revit_busy`, zero matches.
- **Corpus:** 60 cases.

  | Kind | Cases |
  |---|---|
  | Find | 18 |
  | Check (incl. 6 knowledge) | 18 |
  | Edit | 10 |
  | Build | 6 |
  | How-to | 4 |
  | Chat | 4 |

  At least 40% BM, 12 with injected faults, 6 ambiguous. The owner reviews the expected facts and the must-not-claim lists.
- **Graders, on outcomes:**
  - the fixture's end state and the expected facts;
  - must-not-claim;
  - every number in the reply appears in an envelope (normalized: 1,013 vs 1.013, units, names such as "Aras 2" or "D4");
  - `ask_user` is called on ambiguous cases;
  - a citation is present on knowledge cases;
  - language match, and no dumps;
  - model calls and wall time at p50 and p90.

  Each case runs 3 times. An LLM judge is used only for binary questions and is calibrated against about 30 human labels. **Zero tolerance on unbacked claims.**
- **Baseline.** Today's *recorded production replies* are graded on the same rubric, plus one live run of the corpus on the engine path.
- **Live battery.** The corpus runs on a real Revit before each cutover step and each DeepSeek update. Machine, in order of preference:
  1. The VMPal Windows VM, if a spike proves Revit runs there (Q3).
  2. A cloud Windows x64 GPU VM, started only for battery runs.
  3. The owner's Windows PC, as a manual run.
- **Data hygiene:** the corpus is built from consenting users only, scrubbed, and kept out of git.
- **The production numbers check** ("every number appears in an envelope") runs as a **trace-only metric**. A test asserts it can't change anything sent to the pane.

## 9. Protocol 3 (HTTP + SSE)

```
POST /copilot/turns            {turn_id, session_id, text, snapshot, attachments[], protocol: 3}
POST /copilot/turns/{id}/results {request_id, results[{call_id, envelope}] | answer{...}, snapshot}
GET  /copilot/turns/{id}/events?after=<seq>
POST /copilot/turns/{id}/cancel

SSE events (each with seq):
  progress      {kind: thinking|running|awaiting_confirm|revit_busy, text}
  reply_delta   {text}
  tool_request  {request_id, kind: read|write, calls[{call_id, idempotency_key, tool, args}]}
  ask           {request_id, question, options[], multi}
  turn_end      {status: done|cancelled|failed|limit, receipt_ids[]}
  error         {code, message}
```
- **The stream ends** after a `tool_request` or `ask`. The add-in executes, then POSTs the results, which opens the next stream.
- **Idempotency.** Results are idempotent by `request_id`; writes carry `idempotency_key` for the add-in ledger.
- **Session ids** are issued by the server and bound to user and device.
- **Caps** on both sides: request size, frequency, concurrent turns per user (1).

## 10. Envelope

```json
{
  "ok": true,
  "status": "done | partial | failed | rejected | noop | revit_busy | timeout",
  "phase": "read | preview | commit",
  "summary": "Set Offset = 2700 mm on 34 ceilings (Level 2).",
  "data": {},
  "count": 34,
  "handle": "h_7f3a",
  "searched": {"sources": ["host"], "categories": ["Ceilings"], "filter": "Level = 'Level 2'", "not_searched": ["link: STR_v3 (unloaded)"]},
  "changes": {
    "touched": {"added": [], "modified": [{"id": 123, "field": "Height Offset From Level", "before": "2400 mm", "after": "2700 mm"}], "deleted": []},
    "side_effects": {"modified_by_category": {"Rooms": 6, "Schedules": 2}}
  },
  "verified": {"method": "param_readback", "checked": 34, "mismatches": []},
  "plan_hash": "sha256:…",
  "highlight": {"selected": 34, "visible_in_view": 30},
  "warnings": [{"text": "…", "severity": "warning", "ids": [123]}],
  "dialogs": [],
  "error": {"code": "not_found", "message": "No ceiling type 'CL-02'", "candidates": ["CL-02A", "CL-20"]},
  "next": "Use handle h_7f3a to inspect or edit them."
}
```
- **`ok`:** committed and verified for writes; ran without error for reads.
- **Values** are in display units, with the unit.
- **Long lists:** ≤ 50 rows, plus `count` and a `handle`.
- **Empty results** always carry `searched` and near matches.
- **A runtime check** asserts that every `not_found` has `candidates`.

## 11. Core tools (fixed per session, 12)

| Tool | Where | R/W | Purpose |
|---|---|---|---|
| `model_overview(sections[])` | add-in | R | Digest of the model, by section, with truncation hints |
| `find_elements(query?, filter?, source=host\|link:<name>\|all, handle?, highlight?, zoom?)` | add-in | R | Free-text fuzzy match (name, type, mark, room, key parameters, BM↔EN) and/or a structured filter. Several queries per call. `highlight:true` selects and zooms in the same call, so find + highlight is one call |
| `inspect(ids \| handle, fields[])` | add-in | R | Instance/type parameters in display units |
| `compare(a, b, kind: grids\|levels\|rooms\|types)` | add-in | R | Deterministic deltas across host and links, with transforms applied (generalizes `check_grid_alignment`, `compare_levels`) |
| `set_parameters(target: filter\|handle, param, value)` | add-in | W | The most common edit as a core typed write |
| `look(target, mode)` | add-in | R | §7.7 |
| `run_revit_code(code)` | add-in | R/W | §7.8 (Phase 4; until then the slot is unused) |
| `call_tool(name, args)` | cloud → add-in | per tool | Runs a library tool; args validated against its schema |
| `find_tools(query)` | cloud | — | Library schemas as text, for misses |
| `lookup_knowledge(query \| id)` | cloud | — | §8.5 |
| `get_skill(name)` | cloud | — | §8.5 |
| `ask_user(question, options)` | pane | — | §8.5 |

Existing tools are reused underneath:
- `get_scene_overview` → `model_overview`
- `find_elements_by_filter` and `select_by_filter` → `find_elements`
- `get_element_parameters` → `inspect`
- `check_grid_alignment` and `compare_levels` → `compare`
- `set_parameter_bulk` and `set_parameter_by_filter` → `set_parameters`
- `capture_view_image` → `look`, which now returns the image

`open_view`, `override_color`, tagging, dimensions, schedules and sheets stay in the library and are preloaded by retrieval.

## 12. Fences (CI job + tests)

A 1-minute CI job (or a pre-push hook) runs these in bina-ai, which has no CI today. The add-in runs them in its existing workflow.

- **Prompt and schemas:** static prefix ≤ 6k tokens (prompt ≤ 2 KB, core-tool descriptions ≤ 600 chars each); 12 core tools; library schemas in the tail ≤ 8.
- **Code size:** the whole `assistant/` package ≤ 2,500 lines.
- **Message log:** between model calls, only tool results and ask answers may be appended (a test on the message log). The reply the pane receives equals the model's text.
- **Commits:** no `Commit(` under `BinaVibe/Mcp/Tools` outside `TxGuard`.
- **Write tools:** every write tool has a verification declaration and is in the add-in write list.
- **Code policy:** every denied category has a golden test.
- **Flags:** a registry with expiry dates; `os.getenv("REVIT_…")` is banned outside it; at most 5 flags on the copilot path.
- **Eval fixtures:** a PR touching the prompt or the toolbox must change an eval fixture (a diff check).
- **Runtime checks** (envelope shape, `not_found` ⇒ `candidates`) run in tests and log in production.

## 13. Security and data

- **A compromised cloud can't write.** Writes need the add-in's own card, built from its own measurements (§7.5). The add-in decides what counts as a write and enforces the code policy (§7.8). Budgets and policy are compiled in, and the cloud can only narrow them.
- **A compromised cloud can't read freely.** The add-in executes requests only inside turns the drafter started, within per-turn budgets (§7.1).
- **Prompt injection** from DWG, PDF, parameter values or links can't approve anything. Confirm and ask answers come only from the pane, and memory writes need a pane confirm.
- **Auth:** token in the `Authorization` header; release builds are HTTPS-only with a host allowlist; server-issued session ids; no certificate pinning (Cloudflare rotates, and government networks inspect TLS). A test ensures no validation override exists.
- **Tenant isolation:** every store key starts with the tenant from the token. Postgres row-level security on copilot tables. The document fingerprint is only a lookup hint, with project access checks.
- **Until retired,** `McpServer` gets a constant-time secret compare and rejects requests with an `Origin` header.

**Data inventory** (shown to customers on request)

| Data | Sent to | Retention |
|---|---|---|
| Chat text, snapshot (path hashed), tool envelopes, code | bina cloud (Postgres), model provider | turn store: 90 days |
| `look` images | model provider; not stored (hash only) | none |
| Model inputs/outputs | Langfuse (masked) | Langfuse retention; can be switched off |
| Knowledge queries | model provider (rewrite), bina cloud | logs only |
| Everything above in transit | Cloudflare (terminates TLS) | Cloudflare's policy |

No customer restricts where data goes (Q1), so one policy applies to everyone: DeepSeek as provider, paths hashed, images not stored, Langfuse masked, 90-day turn retention. The inventory stays in the design and is shown to customers on request, so a future restriction only needs a config change.

## 14. Failure modes

| Situation | Behaviour |
|---|---|
| Stream drops / backend deploys mid-turn | Add-in re-fetches `events?after=seq`; results POSTs are idempotent; in-flight model call re-runs from persisted state |
| Revit busy / unknown modal | `revit_busy` progress; call waits or fails fast per §7.10; model tells the drafter; no retry loop |
| Doc switched or closed mid-turn | Every call checks the doc identity from the turn's snapshot → `failed: doc_changed`; turn ends asking to resend |
| External change between preview and Ya | External revision ≠ R → one re-preview; card updates |
| Commit shape ≠ plan | RollBack, one re-preview, then `diff_drift` |
| Element owned by another user | Pre-checked in §7.4 → `failed: not_owned` with editable candidates; nothing borrowed |
| DeepSeek slow / down | One retry with backoff, then a plain "AI service unavailable" turn end |
| New path misbehaves in production | Backend flips the user back to `legacy` (DB row), or drops capabilities via allowed tools (read-only mode); add-in also auto-falls back on failure |
| Add-in older than the protocol | Version gate → "update the add-in" |

## 15. Delivery (one person, four signed releases)

| Phase | Repo(s) | Signed release? | Effort | Exit |
|---|---|---|---|---|
| **P0** Truth fixes + path switch | add-in (+ 1 backend route) | **R1** | ~2.5 wk | A01, A02, A03 warnings half; add-in-owned write list on today's card; `copilot_path` honoured (default `legacy`); a rolled-back write shows ✗ with Revit's text |
| **P1** Baseline + extract | backend | — | ~2.5 wk | Eval simulator + 60-case corpus; baseline graded; tool args traced; model seam moved to `app/models/factory`; `compile_gate`/judge decoupled from code to be deleted; fence CI job |
| **P1.5 Thin slice** | backend only | — | ~2 wk | Lean loop for *read* turns on the existing generate/resume routes, driven by the R1 add-in through a cloud-side envelope adapter; 4 existing tools as core; manual Langfuse spans; edits routed to legacy; **staging only** (no production pilot, Q4), UAT by the owner on staging |
| **P2** Protocol 3 + reads in the add-in | both | **R2** | ~4 wk | Add-in envelopes, snapshot, evidence strip, core read tools incl. `find_elements` v2 and `compare`; find/check ≥ baseline accuracy and ≤ baseline time on the live battery; kill switch tested |
| **P3** Knowledge + memory | backend | — | ~3 wk | `lookup_knowledge` with linking; skills from triaged recipes; JKR answers cited ≥ 90% |
| **P4** Writes + `look` + `run_revit_code` | both | **R3** | ~7–8 wk | Plan-hash preview/commit, card, Undo model, ledger phases, verified write tools; zero false "done"; Undo verified on live Revit; policy + budget tests green; add-in protocol frozen |
| **P5** Cutover | backend flag | — | 1 wk + 4–6 wk soak | Hard gate on truth metrics; latency and cost within +20% of target |
| **P6** Delete | both | **R4** | ~1.5 wk | 14 straight days with zero legacy turns, ≥ 6 weeks after the flip; 410 "update the add-in" stubs on old routes; `revit_turn` and `app/engine` removed together |

**Totals.** About 20–24 weeks of work. Critical path: P1 → P2 → P4 → P5.

**Where a solo maintainer stalls:**
- live-Revit test runs (Q3);
- wiring the pane (`ChatView.xaml.cs` is about 3.6k lines);
- labelling the corpus;
- verification declarations for write tools;
- supporting two copilots in the field at once.

**Thin slice.** R1 reaches production drafters in about 2.5 weeks: today's copilot stops showing false "done" and shows ✗ with Revit's reason. P1.5 gives the owner fast, honest find/check answers on **staging** within about 4 weeks. The new path stays on staging until cutover (Q4); the server switch is used only for cutover and rollback. The P1.5 loop is the permanent loop; only its cloud-side envelope adapter is thrown away when R2 moves envelopes into the add-in.

**Rollback at every stage:**
- the DB-row path switch;
- automatic fallback in the add-in;
- `allowed_tools` as a per-capability kill switch;
- the legacy client stays in the binary until R4;
- additive-only Postgres migrations;
- `kamal rollback` (`retain_containers: 3`) for the backend.

## 16. Reuse map (verified by the reviews)

**Add-in**
- **Execution:**
  - `McpJobPump.cs` (thread hand-off and busy watchdog);
  - `ToolRegistry.cs`, `ToolManifest.g.cs` and `scripts/gen-tool-manifest.py`;
  - `BatchExecutor.cs` (TransactionGroup and `$refs`).
- **Write safety and evidence:**
  - `TxGuard.cs` (after A01/A03, with the non-Committed fix);
  - `TurnReceiptService.cs` (recorder, flash/zoom, badges; needs scoping and a prefix fix);
  - `OperationLedger.cs` (add the phase to the key);
  - `DocumentRevisionTracker.cs` (fingerprint; the revision tracker is switched off in the field today, so treat it as new);
  - `TypeCandidates.cs`;
  - the existing `dry_run` + re-read tools in `BulkEdit.cs` / `Mutators.cs`.
- **Code, UI and auth:**
  - `CodeExecutor.cs` (Roslyn compile; the policy walk and loop budget are new);
  - the Copilot pane (`UI/Copilot/*`) and the `RevitChatRouter : IChatRouter` seam;
  - `ToolLoopService`'s SSE client;
  - `BinaVibe/Auth/*` (tokens in Credential Manager);
  - `UpdateGate`.
- **Not reused:** `McpTunnelClient.cs`. Its jobs never reach the pump's drain, writes carry no mutate flag, and the token sits in the URL.

**Backend**
- **Billing:** `credits_meter.py` / `charge_run` (delta-idempotent), `capability_negotiation.py`, `app/models/factory.py` (`get_deepseek`).
- **Knowledge and help:**
  - `jkr_specs_kb.py`, `revit_api_kb.py` (+ `ai.revit_api`), `jkr_param_rules.json`, `data/naming_standards/*`;
  - `tool_help.py`;
  - the recipes, as skill sources.
- **Tests and tracing:**
  - `app/observability.py` (keep the Agno instrumentor for other agents);
  - the frozen contract in `evals/revit_copilot_replay.py`;
  - the `RoslynGate` sidecar (optional early check);
  - the old cloud tunnel at `33db509^:app/agents/revit/copilot/tunnel_manager.py`, only if a WebSocket is ever revisited.

## 17. Extract, then delete

**Extract in P1** (other products depend on these):
- **Shared model code:** `revit_ai._deepseek` and `get_model` move to `app/models/factory.py`. They're used by `cmms/assistant.py:16`, `kpkr/assistant.py:255`, `jkr_autofix.py:120` and `jkr_domain_agents.py:58`.
- **Agents in use elsewhere:** the codegen and repair agents are used by `compile_gate.py:374-379`, `codegen_retry.py:33` and `revit_codegen_lint.py:252`. Decouple them, or keep them as a separate module.
- **Shaping helpers:** `revit_response_shaping` helpers are used by `tool_dispatch.py:402,420`, `codegen_judge.py:517` (which the replay eval calls) and `revit_stream_events.py:191`.
- **Shared knowledge plumbing:** the embedder and DB URL that `jkr_specs_kb.py:22` / `revit_api_kb.py:26` import from `revit_recipes_kb`.
- **App wiring:** `AgentOS(agents=[revit_ai, …])` in `main.py:79-82` and `lifecycle.py:50`.
- **Feedback:** `revit_feedback.py` is keyed by `answer_id`, which becomes the `turn_id`.

**Delete at P6** (after the 14-day zero-legacy rule):
- **Backend:**
  - `app/services/revit_turn.py`, the `app/routers/revit_turn/*` turn routes (replaced by 410 stubs), and the truth-gate, repair, anti-bluff and synthesis paths;
  - the five keyword routers, recipe push and API block;
  - the copilot's LearningMachine, compression and summaries, and its `REVIT_*` flags;
  - `app/engine/*` together with `revit_turn` (`engine/main.py:48` and `mcp_facade.py:62-63` import it), and `/gateway/*` once no engine is in the field.
- **Add-in:**
  - the `ToolLoopRunner` pending/resume path;
  - `RevitCopilotExecutor` and `CodeExecutionHandler` (after-reply codegen);
  - the `ConfirmGate` dependency on the backend flag;
  - `McpServer`, `McpTunnelClient`, and `EngineManager` / `EnginePreflight` / the engine bundle;
  - the `DEPRECATED_*` files.

## 18. Owner answers and what's still open

| # | Question | Answer (2026-10-24) | Effect on the design |
|---|---|---|---|
| Q1 | Do customers restrict sending data to DeepSeek or to BINA's storage? | **No** | One provider and one data policy for everyone (§4, D9, §13) |
| Q2 | What happens to the Auto approval toggle? | **Owner asked for a suggestion** → replaced by "Ask each change" + "Approve this build" (§7.5) | My call; check it |
| Q3 | Which machine runs live Revit tests, and which models? | **Open:** owner asked whether VMPal can run Revit. The VM is Windows 11 ARM64 (5 CPUs, 12 GB RAM, GPU acceleration on); Revit is x64-only and not supported on ARM by Autodesk, but runs on Apple-silicon Macs under Windows-on-ARM x64 emulation in Parallels. Unproven on VMPal → a spike decides (§8.9) | Fallbacks: cloud Windows GPU VM, then the owner's PC |
| Q4 | Production pilot before cutover? | **No** | New path stays on staging until cutover (§15) |
| Q5 | Is a tenant a firm or a person? | **Person** | Everything keyed per user (§4) |
| — | Which plans get which tool groups? | Later, by P5 | — |

## 19. Left to the builder

- Module and class names; frame fields beyond §9; the Postgres schema (`turn_events`, memory, receipts) following `bina-alembic` conventions.
- Policy-file details within the denied categories of §7.8.
- Retrieval scoring for preloaded schemas, within the cap.
- Card and evidence-strip visuals, using existing components and tokens.
- The known-dialog table contents.
- Exact latency targets once the P1 baseline exists.
