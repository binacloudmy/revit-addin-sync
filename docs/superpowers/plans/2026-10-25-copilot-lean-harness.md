# BINA Copilot Lean Harness — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement your workstream task by task. Steps use checkbox (`- [ ]`) syntax for tracking. You own ONLY the files listed for your workstream (§3). If you need a change in a file you don't own, or in a contract, stop and report it; don't edit it.

**Goal:** Replace the over-engineered Agno copilot with a light, honest harness: a ~500-line cloud loop on `deepseek-flash`, exactly 9 tools, an add-in that tells the truth (commit-checked, warnings returned, real preview, one Undo), a `check` rule engine, playbooks, and an eval gate. Delete the old copilot pipeline in the same branches.

**Architecture:**
- **Transport:** the add-in (C#, Revit 2023–2027) talks to bina-ai (Python/FastAPI) over HTTP + SSE ("protocol 3"), with turn state persisted in Postgres.
- **Loop:** the cloud loop calls the model with a fixed 9-tool array and streams `tool_request` events. The add-in executes them on Revit's thread and posts envelopes back.
- **Writes:** previewed (run, then rolled back), approved in the add-in's own card, then committed in one named TransactionGroup.

**Tech stack:**
- **Add-in:** C# (LangVersion latest), Revit API via Nice3point packages, System.Text.Json for every new copilot contract, Roslyn (existing `CodeExecutor`), WPF pane, xUnit (`Tests/Tests.csproj`).
- **Backend:** Python 3.13, FastAPI, `openai` SDK against DeepSeek's OpenAI-compatible API, Postgres (migrations in the `bina-alembic` repo), Langfuse, pytest via `uv`.

**Spec:** `docs/superpowers/specs/2026-10-24-copilot-lean-harness-design.md` (revision 4, approved 2026-10-25). Read it first; this plan points to its section numbers instead of restating them. Background evidence is in `~/Desktop/Copilot Improvement/01…06*.md`. The older single-fix plans are in `~/Desktop/Copilot Improvement/plans/A01…B05*.md`; they hold exact line-level detail that W1 and W9 reuse.

## Global Constraints

- Revit **2023–2027**. All three add-in TFMs must build: `net48` (2023/2024), `net8.0-windows` (2025/2026), `net10.0-windows` (2027). Guard version gaps: `ElementId.Value` and `CreationGUID` exist only from 2024 (`#if REVIT2023_24` pattern in `RevitWebAppSync.csproj:19`).
- The model sees **exactly 9 tools** (spec §11): `model_overview, find_elements, inspect, check, set_parameters, look, run_revit_code, lookup, ask_user`. No other tool schema is ever sent.
- **Size limits:**
  - System prompt ≤ **2,048 bytes**.
  - Each tool description ≤ **600 chars**.
  - Static prefix (prompt + tools + playbook catalogue) ≤ **6,000 tokens**.
  - `app/services/assistant/` ≤ **2,500 lines**, excluding tests and data.
  - Playbooks ≤ **6 KB** (target 4 KB).
  - `run_revit_code` output ≤ **16 KB**.
  - Images ≤ **1024 px** long edge.
- **Model:** `deepseek-flash`, with thinking disabled explicitly (`thinking: {type: "disabled"}`) except under the thinking policy (spec §8.2).
- **No code path may change the model's reply text.** Cards and the caution line are pane-rendered from envelopes.
- **No `Commit(`** under `BinaVibe/Mcp/Tools` or `BinaVibe/Copilot` outside `TxGuard.cs`.
- **New C# files** start with `#nullable enable`, live under `BinaVibe/Copilot/<Area>/`, and use namespace `BinaVibe.Copilot.<Area>`.
- **Names:** never add a `v2`/`V2` suffix or folder. Name things by purpose and replace old code.
- **Git:**
  - Work only in your own worktree on your own `feat/copilot-lh-*` branch.
  - Run `git branch --show-current` before every commit.
  - Stage explicit paths. Never `git add -A`, never `git stash`.
  - Never push to `develop`/`main`, never merge PRs, never touch the owner's main checkouts (`~/development/bina/revit-addin-sync`, `~/development/bina/bina-ai`) except to add a worktree.
- **No production calls** of any kind. Live checks only against staging, and only when the task says so.
- **Licences:** take ideas from audited repos; rewrite all text and code. Never copy from GPL (aec-model-bridge, blender-lab, bpy-dev) or unlicensed (shuotao) sources.
- **Language:** model-facing text is English. Drafter-facing UI and receipt templates exist in both **EN and BM**.

## Review Focus

The five conditions most likely to bite a drafter that the spec implies and no ordinary unit test would hit. Each has its pinning test in the owning task.

1. **Re-created elements get new ids between preview and commit.** The commit check must compare shape (category, type, level, rounded bbox), never added ids. Pinned by W3 Task 2 `ShapeComparer_MatchesAddedByMultisetNotIds`.
2. **"None found" while a link is unloaded or a workset is closed.** The result must say `not_searched`, and `check` must report `not_verified`, never a pass. Pinned by W2 Task 3 `FindResult_ZeroMatches_ListsNotSearchedAndNearMatches` and W6 Task 2 `Evaluate_UnreadableValue_IsNotVerifiedNeverPass`.
3. **BM/mixed units in values** ("2.7m", "2700", "270 cm", "2,700 mm"). They must parse to the same length, and a bare number must take the parameter's display unit. Pinned by W3 Task 4 `ValueParser_ParsesMixedLengthForms`.
4. **The drafter switches document or view mid-turn,** or Revit shows a modal. Tools return `doc_changed` / `revit_busy`, and the loop stops retrying. Pinned by W3 Task 3 `Runner_DocFingerprintMismatch_FailsDocChanged` and W7 Task 4 `Loop_RevitBusyTwice_StopsAndReplies`.
5. **The network drops after a write committed.** The results POST is retried, events replay `after=seq`, and the write is never applied twice. Pinned by W1 Task 4 `Ledger_CommitPhaseSeen_ReturnsCachedEnvelope`, W5 Task 2 `Client_ResumesEventsAfterSeqAndRepostsResultsIdempotently` and W7 Task 2 `TurnStore_ResultsPostIsIdempotentByRequestId`.

---

## 1. Branches, worktrees, integration

- **Integration branches**, both named `feat/copilot-lean-harness`:
  - add-in: cut from `origin/develop` (`8cb9cce`), with the four spec commits (`7467b3b`, `8a4f5f4`, `c20bb08`, `3e9a8e1`) and this plan's commit cherry-picked on top;
  - bina-ai: cut from `origin/develop` (`5f38f5e`).
- **W0 (the orchestrator)** commits the shared contracts (§2) and skeletons to both integration branches before any workstream starts.
- **Each workstream** creates its own worktree and branch from the integration branch:
  - add-in: `git -C ~/development/bina/revit-addin-sync worktree add ~/development/bina/lh/addin-w<N> -b feat/copilot-lh-w<N>-<slug> feat/copilot-lean-harness`
  - bina-ai: `git -C ~/development/bina/bina-ai worktree add ~/development/bina/lh/ai-w<N> -b feat/copilot-lh-w<N>-<slug> feat/copilot-lean-harness`
  - bina-alembic (W7 only): `git -C ~/development/bina/bina-alembic worktree add ~/development/bina/lh/alembic-w7 -b feat/copilot-lh-w7-turns origin/develop`
- **Integration:** when a workstream is done, the orchestrator merges its branch into `feat/copilot-lean-harness`:
  - add-in order: W1 → W2 → W6 → W3 → W4 → W5;
  - backend order: W8 → W7 → W9;
  - after each merge the orchestrator builds and runs tests, then opens PRs to `develop` for the owner to merge.
- **Contract changes** go through the orchestrator only. If your work needs one, stop and report.

**Commands**

| Purpose | Command | Pass |
|---|---|---|
| Add-in build (all TFMs, macOS) | `~/.dotnet/dotnet build RevitWebAppSync.csproj -p:EnableWindowsTargeting=true` | `Build succeeded`, 0 errors |
| Add-in tests compile | `~/.dotnet/dotnet build Tests/Tests.csproj -p:EnableWindowsTargeting=true` | 0 errors |
| Add-in pure tests (run) | scratch xUnit project in `/tmp/lh-tests-w<N>/` targeting `net10.0-windows`, `EnableWindowsTargeting=true`, `EnableDefaultCompileItems=false`, `<Compile Include>` absolute paths to your Revit-free sources + tests; `~/.dotnet/dotnet test /tmp/lh-tests-w<N>` | all pass |
| Backend tests | `uv run pytest tests/assistant -q` (and the files your tasks name) | all pass |
| Backend copilot suite (after W9 Task 1) | `bash scripts/test-copilot.sh` | exit 0 |

Notes for the add-in:
- Logic that touches `Autodesk.Revit.*` types can't run in the scratch harness; Nice3point packages are compile-only. Keep testable logic in Revit-free files. Exemplar: `AuditMatching.cs` was split out of `AuditCheckers.cs`.
- `Tests/Tests.csproj` uses explicit `<Compile Include>` links, so link every new Revit-free source you test.

---

## 2. Shared contracts (W0 writes these; everyone codes against them)

Files live in **both repos** at `contracts/copilot/`, byte-identical:
- `envelope.schema.json`
- `protocol3.schema.json`
- `tools.json`
- `snapshot.schema.json`
- `ruleset.schema.json`
- `playbook.schema.json`
- `examples/*.json`

The fragments below are normative. W0 expands each into full JSON Schema (draft 2020-12) without changing meaning.

### 2.1 Envelope (spec §10)
- **Required fields:** `ok: bool`, `status`, `phase`, `summary: string ≤300`.
- **`status`** ∈ `done | partial | failed | rejected | noop | revit_busy | timeout`.
- **`phase`** ∈ `read | preview | commit`.
- **Optional fields:**
  - `data` (object or array), `count` (int), `handle` (string);
  - `searched` `{sources[], categories[], filter, not_searched[]}`;
  - `changes` `{touched:{added:[ref], modified:[{id, field, before, after}], deleted:[ref]}, side_effects:{added_by_category, modified_by_category, deleted_by_category}}`;
  - `verified` `{method: param_readback|exists|count_delta|none, checked, mismatches[{id, field, expected, actual}]}`;
  - `plan_hash` (string `sha256:<hex>`);
  - `highlight` `{selected, visible_in_view}`;
  - `warnings[{text, severity: warning|error, definition_id, ids[]}]`;
  - `dialogs[{id, text, answered_with}]`;
  - `error {code, message, candidates[]}`;
  - `next` (string ≤200);
  - `image {media_type:"image/png", base64, width, height, caption}`.
- **`ref`** = `{id: int64, link: int64|null}`.
- **`error.code`** ∈ `not_found | not_allowed | not_owned | doc_changed | diff_drift | revit_busy | policy_rejected | compile_error | runtime_error | timeout | invalid_args | rolled_back | no_gpu | not_implemented`.
- **Invariants** (contract tests check these on every example):
  - `ok == false` ⇒ `error` is present;
  - `error.code == not_found` ⇒ `candidates` is present (may be empty only when nothing similar exists);
  - `phase == commit && ok` ⇒ `verified.method != none` or `status == noop`;
  - `count == 0` on a read ⇒ `searched` is present.

### 2.2 Protocol 3 (spec §9)
**Requests**
- **`POST /copilot/turns`**
  - Headers: `Authorization: Bearer <token>`, `Accept: text/event-stream`.
  - Body: `{protocol:3, turn_id: uuid, session_id: string|null, text, snapshot, attachments:[{name, kind: table|image, columns?, rows?, image_b64?}]}`.
  - Responses: `409` if a turn is already active for this session; `426 {update_required:true}` on a protocol mismatch.
- **`POST /copilot/turns/{turn_id}/results`**
  - Body: `{protocol:3, request_id, results?:[{call_id, envelope}], answer?:{values:{<field_or_option_id>: value}}, snapshot}`.
  - Idempotent by `request_id`: a repeat returns the same stream.
- **`GET /copilot/turns/{turn_id}/events?after=<seq>`** replays events with a higher `seq`.
- **`POST /copilot/turns/{turn_id}/cancel`**.

**SSE events.** Wire format: `event: <type>\ndata: <json>\n\n`. Every `data` carries an integer `seq` that increases within a turn.

| Event | Data |
|---|---|
| `progress` | `{kind: thinking\|running\|awaiting_confirm\|revit_busy, text}` |
| `reply_delta` | `{text}` |
| `tool_request` | `{request_id, kind: read\|write, calls:[{call_id, idempotency_key, tool, args}], description?}` |
| `ask` | `{request_id, question, options?:[{id,label}], fields?:[{id,label,type: text\|number\|choice,unit?}], multi, purpose: clarify\|project_note, note_text?}` |
| `turn_end` | `{status: done\|cancelled\|failed\|limit\|interrupted, session_id, receipt_ids[]}` |
| `error` | `{code, message}` |

The stream ends after `tool_request`, `ask` or `turn_end`.

### 2.3 Tools (spec §11)
`tools.json` is an array of `{name, kind: read|write|code|server|pane, description, parameters}`, where `parameters` is a JSON Schema object. It is exactly what the loop sends to the model, and the add-in parses args against the same schema. The descriptions below are normative copy:

| name | kind | parameters (summary) | description |
|---|---|---|---|
| `model_overview` | read | `sections: [levels\|grids\|rooms\|types\|views\|sheets\|links\|worksets\|warnings\|project]` (required, ≥1), `category?: string`, `limit?: int ≤200 = 50` | "Summary of the open Revit model. Pick only the sections you need: levels, grids, rooms, types, views, sheets, links, worksets, warnings, project. Long lists are cut with a note saying how many more exist and how to narrow. Use this before guessing any level, type, view or link name." |
| `find_elements` | read | `queries: [{text?, category?, level?, type?, room?, parameter?:{name, op: eq\|ne\|contains\|empty\|not_empty\|gt\|lt, value?}, in_view?: bool, source?: "host"\|"all"\|"link:<name>"}]` (1–5), `group_by?: level\|type\|category\|room\|"param:<name>"`, `handle?: string`, `highlight?: bool`, `zoom?: bool`, `limit?: int ≤50 = 50` | "Find elements in the host model or links. Give 1–5 queries; each can combine free text (matches name, type, mark, room, comments; Malay and English words work) with category, level, type, room, a parameter condition or in_view. Returns exact counts, up to 50 rows, a handle for reuse, and what was and was not searched. group_by counts per level/type/category/room/parameter. highlight:true selects the results in Revit in the same call; zoom:true also zooms. Never say 'none' without the searched scope from the result." |
| `inspect` | read | `target: {ids?: int64[], handle?: string, selection?: bool}` (one required), `fields?: string[]`, `include_type?: bool`, `schedule?: string`, `explain_visibility?: {view?: string}` | "Read parameters of elements (by ids, a handle or the current selection) in display units, with storage type and read-only flags; include_type adds type parameters. schedule reads a schedule's rows by name. explain_visibility says exactly why the elements are not visible in a view (view range, filters, template, workset, phase, design option, crop, hidden category or element) and how to fix each reason." |
| `check` | read | `rule_set: string` (required), `scope?: {level?, category?, link?, selection?: bool}` | "Run a standards rule set against the model: JKR naming/parameters/LOD, model health, UBBL or MS 1184 basics, BOMBA basics, documentation hygiene. Find rule set ids with lookup(kind:'rules'). Returns per-rule pass / fail / not_verified counts with the cited clause and a handle per failing group. not_verified means the check could not see the data (for example an unloaded link): report it and never treat it as a pass. check never changes the model." |
| `set_parameters` | write | `target: {filter?: <find query>, handle?, ids?}` or `from_table?: string`, `param: string` (required), `value?: string\|number\|bool`, `type_param?: bool` | "Set one parameter on many elements: target by filter, handle or ids, or give from_table (an attached table with element ids or a match field and values). Give the value with its unit when it has one, for example '2700 mm'. Revit shows the drafter a preview with before and after values and asks for approval before anything changes; you receive the committed result with read-back. Say so when the parameter is a type parameter, because it changes every instance of that type." |
| `look` | read | `target: active_view\|view\|elements` (required), `view?: string`, `handle?`, `ids?`, `mode?: single\|multi = single` | "Get an image of the model to orient yourself: the active view, a named view, or a 3D view around elements (by handle or ids). mode 'multi' returns plan, 3D and elevation side by side. The drafter sees the same image. Use it to check placement and layout, never to count or measure: use find_elements and inspect for numbers." |
| `run_revit_code` | code | `code: string` (required, C# method-body statements), `intent: string` (required, ≤120 chars, shown to the drafter), `expected?: {adds?: int, modifies?: int, deletes?: int}` | "Run C# against the Revit API for jobs no other tool covers (sheets, views, tags, dimensions, renumbering, filters, elevations, modelling). Write method-body statements only; you get doc, uidoc, view and Print(obj). Do not open transactions: the host does. Allowed: Autodesk.Revit.DB and basic System types; no files, network, saving, syncing or dialogs. Reads run immediately; changes are previewed and approved by the drafter. Errors come back with line numbers: fix and retry. For known jobs, get the playbook with lookup first." |
| `lookup` | server | `query?: string`, `id?: string` (one required), `kind?: auto\|knowledge\|playbook\|rules = auto` | "Look things up instead of guessing. knowledge: JKR spec, JKR parameter rules, naming segments, Revit API members and fixes for Revit warnings, with citations. playbook: a step-by-step procedure with known traps for a job in the playbook list. rules: rule set ids for check. Meanings of JKR codes, parameters and naming must come from here, never from memory." |
| `ask_user` | pane | `question: string` (required), `options?: [{id,label}]` (2–4), `fields?: [{id,label,type: text\|number\|choice, unit?}]`, `multi?: bool`, `purpose?: clarify\|project_note = clarify`, `note_text?: string` | "Ask the drafter one question when something you need is missing or ambiguous: give 2–4 options taken from the model (for example the real level names), or a small form with fields. purpose 'project_note' proposes saving a project fact the drafter stated; it is saved only if they confirm. Never use this to ask for approval of changes: Revit's confirm card does that." |

**Cloud-side argument rewrites** happen before the call reaches the add-in, and are never visible to the model:
- `check.rule_set` is replaced by `rule_set_content` (the full rule set JSON from §2.5).
- `set_parameters.from_table` is replaced by `table_rows` from the turn's attachments.

### 2.4 Snapshot (spec §7.9)
```
{ doc:{kind: project|family, title, path_hash, is_workshared, mode: central|local|detached|standalone|read_only, units:{length}, phase, active_design_option, version_guid, session_nonce, revit_version},
  view:{id, name, type, level, scale, is_template},
  selection:{count, by_category:{}, ids:[≤50 int64]},
  worksharing:{active_workset}, links:[{name, loaded}], warnings_count,
  rooms:[{name, number, level}] (only when ≤150), change_seq, stale: bool }
```

### 2.5 Rule set (spec §21)
```
{ id, version, title_en, title_ms, source:{name, edition, url?},
  rules:[{ id, title_en, title_ms, cite, severity: error|warning|info,
           selector:{categories:[], levels?:[], type_name_regex?, has_param?, link?: host|all},
           assertion:{kind, param?, regex?, values?, min?, max?, unit?, other_param?, link?, tolerance_mm?, tolerance_deg?},
           unverified_value?: bool, fix_hint_en, fix_hint_ms }] }
```
- **`assertion.kind`** ∈ `not_empty | matches | in_list | between | equals_param | exists | count_le | grid_aligned_with_link | level_matches_link | clear_width_ge | turning_circle_ge | room_enclosed | room_placed | dimension_overridden | tagged_in_view | duplicate_instances | imported_cad_present | in_place_family | warnings_group`.
- **`check` result `data`:**
  ```
  {rule_set_id, version,
   totals:{checked, pass, fail, not_verified},
   rules:[{rule_id, cite, severity, pass, fail, not_verified,
           top:[{ref, value?, reason}] (≤5), fail_handle?, not_verified_reason?}]}
  ```

### 2.6 Playbook front-matter (spec §22)
- **YAML fields:**
  - `name` (kebab-case);
  - `one_liner` (≤160 chars);
  - `keywords_en[]`, `keywords_ms[]`;
  - `applies_when`, `not_for`;
  - `inputs:[{name, sources:[drafter|model|table|default]}]`;
  - `verify`;
  - `revit_tested: "2023-2027"`;
  - `tools:[subset of the 9]`;
  - `evals:[case ids]`.
- **Required body headings, in order:** `## Steps`, `## Questions`, `## Code`, `## Traps`, `## Verify`.

---

## 3. Ownership (exclusive; anything not listed belongs to the orchestrator)

| WS | Repo | Owns (create/modify/delete) |
|---|---|---|
| **W0** orchestrator | both | `contracts/copilot/**`; `BinaVibe/Copilot/Contracts/**`; skeleton files listed in W0; `app/services/assistant/contracts.py`; integration merges |
| **W1** truth core | add-in | `BinaVibe/Mcp/Tools/TxGuard.cs`; commit/rollback sites only in `Mutators*.cs`, `BulkEdit.cs`, `Inspectors.cs`, `Dimensioning.cs`, `Schedules.cs`, `SpatialEdit.cs`; the `NotImplemented` result in `ToolRegistry.cs`; `BinaVibe/DocState/OperationLedger.cs`; new `BinaVibe/Copilot/Guard/**`, `BinaVibe/Copilot/Envelope/**`; tests `TxGuardContractTests`, `EnvelopeBuilderTests`, `LegacyResultAdapterTests`, `OperationLedgerPhaseTests`, `ChangeSetMathTests` |
| **W2** read tools | add-in | `BinaVibe/Copilot/Tools/ModelOverviewTool.cs`, `FindElementsTool.cs`, `InspectTool.cs`, `LookTool.cs`; new `BinaVibe/Copilot/Read/**`; `Resources/copilot/synonyms-ms-en.json`; tests `QueryParserTests`, `SynonymTests`, `HandleStoreTests`, `VisibilityReasonTests`, `FindResultShapeTests`, `SnapshotModelTests` |
| **W3** write engine | add-in | `BinaVibe/Copilot/Tools/SetParametersTool.cs`; new `BinaVibe/Copilot/Writes/**`, `BinaVibe/Copilot/Undo/**`; `Services/TurnReceiptService.cs` (tint/undo parts, prefix "BINA:"); tests `PlanHashTests`, `ShapeComparerTests`, `ApprovalScopeTests`, `ValueParserTests`, `UndoModelTests`, `DialogPolicyTests` |
| **W4** code tool | add-in | `BinaVibe/Copilot/Tools/RunRevitCodeTool.cs`; new `BinaVibe/Copilot/Code/**`; `Resources/copilot/code-policy.json`; `Services/CodeExecutor.cs` (refactor compile parts into `Code/`); tests `CodePolicyTests`, `StatementWrapperTests`, `LoopBudgetTests`, `ErrorMapperTests`, `ModeClassifierTests` |
| **W5** pane, client, cleanup | add-in | new `Services/CopilotTurnClient.cs`, `Services/CopilotSse.cs`; `UI/Copilot/**`; `App.cs`; `BinaConfig.cs` (engine settings); deletes in §5 W5; `.github/workflows/**` and `installer/**` engine packaging; tests `CopilotSseTests`, `CopilotTurnClientTests`, `TurnBudgetTests`, `TableAttachmentTests`, `ReceiptTemplateTests` |
| **W6** check engine | add-in | `BinaVibe/Copilot/Tools/CheckTool.cs`; new `BinaVibe/Copilot/Check/**`; tests `RuleSetParseTests`, `GenericAssertionTests`, `CheckReportTests` |
| **W7** loop + turns | bina-ai, bina-alembic | `app/services/assistant/` except `lookup/**` and `contracts.py`; `app/routers/copilot.py`; `app/prompts/copilot_system.md`; `tests/assistant/**` except `tests/assistant/lookup/**`; the new migration in bina-alembic |
| **W8** knowledge, playbooks, rules | bina-ai | `app/services/assistant/lookup/**`; `data/copilot/**`; `data/naming_standards/jkr_name_generator_v2.json` (sumber fix); `tests/assistant/lookup/**` |
| **W9** evals, extract, delete, CI | bina-ai | `conftest.py`, `scripts/test-copilot.sh`, `evals/copilot/**`, `.github/workflows/copilot-fences.yml`, `app/main.py`, `app/lifecycle.py`, `app/models/factory.py`, and every file in spec §17 "extract" and "delete" lists (backend); tests `tests/evals_copilot/**`, `tests/test_extracted_seams.py`, `tests/test_old_routes_410.py` |

`Tests/Tests.csproj` is shared: each add-in workstream adds only its own `<Compile Include>` lines, in a block commented `<!-- W<N> -->`. `RevitWebAppSync.csproj`: only W5 edits it (resources, removals). Other workstreams that need a csproj change request it in their final report.

---

## 4. W0 — orchestrator setup (before spawning)

### Task 0.1: Integration branches + contracts
**Files:** create `contracts/copilot/{envelope.schema.json, protocol3.schema.json, tools.json, snapshot.schema.json, ruleset.schema.json, playbook.schema.json, README.md}` and `contracts/copilot/examples/{envelope-read-find.json, envelope-read-empty.json, envelope-preview-setparams.json, envelope-commit-setparams.json, envelope-failed-notfound.json, envelope-look.json, frames.json, ruleset-model-health.json, snapshot.json}`, all identical in both repos.
- [ ] Create both integration worktrees and branches (§1) and cherry-pick the spec commits plus this plan into the add-in one.
- [ ] Write the schemas from §2 and the examples; each example must satisfy the §2.1 invariants.
- [ ] Commit to both: `contracts(copilot): shared contracts for lean harness`.

### Task 0.2: Add-in contract types + skeletons
**Files:**
- `BinaVibe/Copilot/Contracts/Envelope.cs` (Revit-free POCOs mirroring §2.1, System.Text.Json, snake_case names);
- `BinaVibe/Copilot/Contracts/Frames.cs` (§2.2 request/event POCOs, including `ToolCallDto {CallId, IdempotencyKey, Tool, Args: JsonElement}` and `AskDto`; `Envelope.cs` also defines `WarningDto`, `DialogDto`, `ErrorDto`, `ChangesDto` and `ImageDto`);
- `BinaVibe/Copilot/Contracts/Snapshot.cs`;
- `BinaVibe/Copilot/Contracts/RuleSet.cs`;
- `BinaVibe/Copilot/Contracts/ToolContracts.cs`;
- `BinaVibe/Copilot/Tools/{ModelOverviewTool, FindElementsTool, InspectTool, CheckTool, SetParametersTool, LookTool, RunRevitCodeTool}.cs` (skeletons returning `EnvelopeFactory.NotImplemented(name)`);
- `BinaVibe/Copilot/CopilotToolCatalog.cs`;
- `BinaVibe/Copilot/CopilotToolHost.cs`;
- `Tests/CopilotContractTests.cs`.

**Interfaces (produced, normative):**
```csharp
namespace BinaVibe.Copilot.Contracts
public enum ToolKind { Read, Write, Code }
public sealed class ToolContext {            // Revit-bound; created per call on Revit's thread
    public UIApplication App; public Document Doc; public UIDocument UiDoc;
    public string TurnId; public string CallId; public string IdempotencyKey;
    public IHandleStore Handles; public SnapshotModel TurnSnapshot; }
public interface ICopilotTool { string Name { get; } ToolKind Kind { get; } Envelope Run(ToolContext ctx, JsonElement args); }
public interface IWriteTool : ICopilotTool {           // Write and Code tools
    WritePlan Plan(ToolContext ctx, JsonElement args);  // resolve targets BEFORE any change
    Envelope Execute(ToolContext ctx, JsonElement args, WritePlan plan, WritePhase phase); } // runner owns transactions
public enum WritePhase { Preview, Commit }
public sealed class WritePlan { public string Tool; public string CanonicalArgs; public long[] TargetIds;
    public Dictionary<string,string> IntendedValues; public Dictionary<string,int> ExpectedAddedByCategory;
    public Dictionary<string,int> ExpectedDeletedByCategory; public string? CodeText; public bool Previewable = true; }
public interface IHandleStore { string Put(IReadOnlyList<ElementRef> refs, string label); IReadOnlyList<ElementRef>? Get(string handle); }
public sealed record ElementRef(long Id, long? Link);
public interface IWriteBatchRunner { Task<Envelope[]> RunAsync(WriteBatchRequest req, CancellationToken ct); }
public sealed class WriteBatchRequest { public string TurnId; public string RequestId; public IReadOnlyList<ToolCallDto> Calls; public string? Description; public SnapshotModel Snapshot; }
public interface IConfirmPresenter { Task<ConfirmDecision> AskAsync(ConfirmRequest req, CancellationToken ct); }
public sealed class ConfirmRequest { public string BatchId; public Envelope[] Preview; public string PlanHash; public string? AssistantDescription; public bool OfferApproveThisBuild; }
public sealed class ConfirmDecision { public bool Approved; public bool ApproveThisBuild; public string? Reason; }
public interface IReadExecutor { Task<Envelope[]> RunAsync(string turnId, IReadOnlyList<ToolCallDto> calls, SnapshotModel snapshot, CancellationToken ct); }
public static class EnvelopeFactory { public static Envelope NotImplemented(string tool); public static Envelope NotAllowed(string tool); public static Envelope Failed(string code, string message, string[]? candidates = null); }
```
- `CopilotToolCatalog.All` lists the 7 add-in tools. `CopilotToolHost` has two methods:
  - `RunReadsAsync(...)`, which delegates to an `IReadExecutor` and returns `NotAllowed` for any name outside the catalogue;
  - `RunWritesAsync(...)`, which delegates to an `IWriteBatchRunner`.
- Both delegate fields are settable by W2/W3 at integration.
- [ ] Write `Tests/CopilotContractTests.cs`:
  - `Envelope_RoundTripsEveryExample` (deserialize every `contracts/copilot/examples/envelope-*.json`, re-serialize, assert key sets equal);
  - `Envelope_ExamplesSatisfyInvariants`;
  - `ToolsJson_HasExactlyNineTools_DescriptionsUnder600`.
- [ ] Build all TFMs, run the contract tests in the scratch harness, commit: `contracts(copilot): add-in contract types and tool skeletons`.

### Task 0.3: Backend contract models
**Files:** `app/services/assistant/__init__.py`, `app/services/assistant/contracts.py` (pydantic v2 models for Envelope, frames, ToolSpec loader `load_tools() -> list[ToolSpec]` reading `contracts/copilot/tools.json`, RuleSet, PlaybookFrontMatter), `tests/assistant/test_contracts.py` (every example validates; exactly 9 tools; descriptions ≤600).
- [ ] TDD the tests above, implement, run `uv run pytest tests/assistant/test_contracts.py -q`, commit: `contracts(copilot): backend contract models`.

---

## 5. Workstreams

Each workstream ends with:
- all three TFMs building (add-in) or `uv run pytest` green (backend);
- its tests passing;
- commits on its branch;
- a final report listing what's done, what's stubbed, any requests for files it doesn't own, and anything that needs a live Revit to verify.

### W1 — Truth core (add-in) · branch `feat/copilot-lh-w1-truth`

**Task 1: CommitOrThrow everywhere** (exactly plan A01, `~/Desktop/Copilot Improvement/plans/A01-commit-or-throw.md`; follow it, including its STOP conditions)
- [ ] Test `TxGuardContractTests.NoBareCommitInSwallowingFiles` scans `BinaVibe/Mcp/Tools/**` and `BinaVibe/Copilot/**` for `Commit(` outside `TxGuard.cs`. Assert none. The scan catches `var s = tx.Commit();` too.
- [ ] Change `CommitOrThrow` to throw on **any** status other than `Committed`, and guard every `catch` rollback with `GetStatus()==Started`.
- [ ] Commit.

**Task 2: Collect failures instead of only deleting** (plan A03, warnings half)
- [ ] Revit-free `FailureLog` (in `BinaVibe/Copilot/Guard/FailureLog.cs`): `Add(text, severity, definitionId, ids)`, `Snapshot() -> IReadOnlyList<WarningDto>`, dedupes by `(definitionId, sorted ids)`, caps at 10.
- [ ] Test `FailureLogTests.DedupesAndCaps`.
- [ ] `TxGuard`'s preprocessor writes into a thread-static `FailureLog` before deleting warnings; errors are captured the same way.
- [ ] Add a `FailuresProcessing` backstop subscription helper `FailureScope.Begin(Application)` / `Dispose()` for batches.
- [ ] Commit.

**Task 3: Envelope builder + legacy adapter + IsFailure** (plan A02 idea)
- [ ] `EnvelopeBuilder` (Revit-free): fluent `Read(summary)`, `Write(phase)`, `WithChanges(...)`, `WithVerified(...)`, `WithWarnings(FailureLog)`, `Fail(code, message, candidates)`, and `Build()`, which enforces the §2.1 invariants (throws in DEBUG, logs in RELEASE).
- [ ] `LegacyResultAdapter.ToEnvelope(Dictionary<string,object?> legacy, WritePhase? phase)`:
  - maps `ok:false` / `error` / `status:not_implemented` to failed;
  - maps counts to `count`;
  - keeps the raw dict in `data`.
- [ ] `ResultSemantics.IsFailure(dict)`.
- [ ] Tests:
  - `LegacyResultAdapterTests.OkFalseBecomesFailed`;
  - `LegacyResultAdapterTests.NotImplementedBecomesFailedNotImplemented`;
  - `EnvelopeBuilderTests.NotFoundWithoutCandidatesThrowsInDebug`.
- [ ] `ToolRegistry.NotImplemented` returns `ok:false`.
- [ ] Commit.

**Task 4: Ledger by phase + change math**
- [ ] `OperationLedger`: key becomes `(idempotencyKey, phase)`. Only `Commit` is recorded and reconciled.
- [ ] Test `OperationLedgerPhaseTests.Ledger_CommitPhaseSeen_ReturnsCachedEnvelope` (Review Focus 5) and `PreviewNeverSatisfiesCommit`.
- [ ] Revit-free `ChangeSetMath`: `Subtract(reverted)`, `SplitTouched(touchedIds, all) -> (touched, sideEffectsByCategory)`, plus a filter of known bookkeeping element ids.
- [ ] Revit-bound `ChangeRecorder` subscribes to `DocumentChanged` only while a `CallbackScope` flag is set, scoped to one document.
- [ ] `ExternalRevision` counts commits/undos/redos raised **outside** our scopes (spec §7.4).
- [ ] Tests `ChangeSetMathTests.*`.
- [ ] Commit.

### W2 — Read tools (add-in) · branch `feat/copilot-lh-w2-read`
Consumes: W0 contracts. Produces: `IReadExecutor` implementation `ReadExecutor` (Revit thread via `McpJobPump.Enqueue`), `HandleStore : IHandleStore`, `SnapshotService.Current() -> SnapshotModel`.

**Task 1: Snapshot + handles**
- [ ] Revit-free `SnapshotModel` builders.
- [ ] `HandleStore` (LRU 200 per session; handles look like `h_<8hex>`).
- [ ] Test `HandleStoreTests.PutGetAndEvicts`.
- [ ] Revit-bound `SnapshotService`, kept current from `SelectionChanged`, `ViewActivated`, `DocumentChanged`, `DocumentOpened`/`Closed`; `stale` is set when a refresh failed.
- [ ] `path_hash` = sha256 of the path.
- [ ] Commit.

**Task 2: `model_overview`**
- [ ] `ModelOverviewTool.Run` composes the existing inspectors (`SceneOverview`, `Inspectors.List*`). Each section is cut at `limit` with `next` "N more; narrow with category=…".
- [ ] It's precomputed on Idling after the pane opens and cached by `(doc version_guid, session_nonce, GetDocumentVersion)`.
- [ ] Commit.

**Task 3: `find_elements`**
- [ ] Revit-free `QueryParser` (args → normalized query plan) and `Synonyms` (load `Resources/copilot/synonyms-ms-en.json`, which W2 creates, ≥80 pairs: tandas↔toilet, pintu↔door, tingkap↔window, aras↔level, siling↔ceiling, dinding↔wall, lantai↔floor, bumbung↔roof, tangga↔stair, surau↔prayer room, kalis api↔fire rated, OKU↔accessible…).
- [ ] Revit-bound executor:
  - reuse `Inspectors.FindElementsByFilter` / `ElementFilter` collectors;
  - `source=link:*` walks `RevitLinkInstance.GetLinkDocument()`; a null link document goes into `not_searched`;
  - `group_by`, `highlight` (`Selection.SetElementIds` + `ZoomAndCenterRectangle`, no transaction);
  - `highlight.visible_in_view`.
- [ ] Tests:
  - `QueryParserTests.*`;
  - `SynonymTests.MalayTermsMapToCategories`;
  - `FindResultShapeTests.FindResult_ZeroMatches_ListsNotSearchedAndNearMatches` (Review Focus 2: builder-level, Revit-free).
- [ ] Commit.

**Task 4: `inspect` + "why can't I see it?"**
- [ ] Parameters in display units (`UnitFormatUtils`), storage type, read-only, type parameters; schedule rows via `ViewSchedule.GetTableData()` with header/blank/data labelled.
- [ ] Revit-free `VisibilityReasons` model plus a Revit-bound `VisibilityExplainer`. For one view it checks, in order:
  1. category hidden (`GetCategoryHidden`);
  2. element hidden (`IsHidden`);
  3. temporary hide/isolate;
  4. filters (`GetFilters` + `GetFilterVisibility`);
  5. template-controlled;
  6. workset visibility;
  7. phase (`ElementPhaseStatus` against view phase/filter);
  8. design option;
  9. view range / crop (bbox against the crop box and view range planes);
  10. element on another level.

  Each reason carries a fix in EN/BM.
- [ ] Test `VisibilityReasonTests.OrdersAndFormatsReasons`.
- [ ] Commit.

**Task 5: `look`**
- [ ] For element targets: TransactionGroup → transaction creates a temporary `View3D` with a section box around the targets' bbox (+10%) → commit → `ExportImage` (`PixelSize=1024`, PNG, temp folder) with no transaction → `group.RollBack()`.
- [ ] For the active view: `VisibleRegionOfCurrentView`.
- [ ] `multi` mode: plan + 3D + elevation stitched side by side (`System.Drawing` on net48, or WPF imaging).
- [ ] Return `image.base64` plus a caption with the real size in m; delete temp files.
- [ ] On export failure (no GPU) return `failed: no_gpu`.
- [ ] The whole thing runs under W1's `CallbackScope`, so `ExternalRevision` doesn't move.
- [ ] Commit.

### W3 — Write engine (add-in) · branch `feat/copilot-lh-w3-writes`
Consumes: W0 `IWriteTool`, `IWriteBatchRunner`, `IConfirmPresenter`; W1 `TxGuard`, `ChangeRecorder`, `ExternalRevision`, `OperationLedger`, `EnvelopeBuilder`. Produces: `WriteBatchRunner : IWriteBatchRunner`, `UndoService`, `SetParametersTool`.

**Task 1: Plan hash**
- [ ] Revit-free `PlanHash.Compute(IEnumerable<WritePlan>) -> "sha256:<hex>"`, over canonical JSON (sorted keys, invariant culture).
- [ ] Tests `PlanHashTests.StableAcrossKeyOrder` and `ChangesWhenTargetsChange`.
- [ ] Commit.

**Task 2: Shape comparer**
- [ ] Revit-free `ShapeComparer.Compare(plan, previewShape, commitShape) -> ShapeVerdict { Match, Drift(reason) }`:
  - modified/deleted by exact id;
  - added by the multiset of `(category, typeId, levelId, bbox rounded to 10 mm)`.
- [ ] Tests:
  - `ShapeComparer_MatchesAddedByMultisetNotIds` (Review Focus 1);
  - `DetectsExtraDeletion`.
- [ ] Commit.

**Task 3: Batch runner (spec §7.4)**
- [ ] `WriteBatchRunner.RunAsync`:
  1. fingerprint check against `req.Snapshot` (else `doc_changed`);
  2. worksharing pre-check (`WorksharingUtils.GetCheckoutStatus`; others' elements → `not_owned` with editable candidates);
  3. `Plan` for every call → plan hash;
  4. preview callback (`CallbackScope` PREVIEW; TransactionGroup; `Execute(Preview)`; capture envelopes plus TemporaryGraphics markers or a thumbnail via W2's `look` helper; `RollBack`; capture external revision R);
  5. `IConfirmPresenter.AskAsync`;
  6. Tidak → `rejected` ok:true;
  7. Ya → commit callback (R unchanged, else one re-preview; TransactionGroup "BINA: <label>"; `Execute(Commit)`; `ShapeComparer`; match → `Assimilate`, else `RollBack` + one re-preview then `diff_drift`);
  8. ledger records the commit;
  9. return envelopes.
- [ ] Non-previewable plans (`Previewable=false`) get an estimate-only card.
- [ ] `ApprovalScope` (Revit-free) enforces "Approve this build": same turn, preview shows additions only, total adds ≤ 500.
- [ ] Tests:
  - `ApprovalScopeTests.*`;
  - `Runner_DocFingerprintMismatch_FailsDocChanged` (Review Focus 4, using a fake fingerprint provider);
  - `Runner_PreviewNeverSatisfiesCommitLedger`.
- [ ] Commit.

**Task 4: `set_parameters`**
- [ ] `SetParametersTool : IWriteTool` built on `BulkEdit` `set_parameter_by_filter` internals:
  - resolve targets (filter / handle / ids / `table_rows`);
  - type vs instance;
  - `Parameter.Set` false → throw → rollback;
  - read-back into `verified`;
  - `before`/`after` in display units.
- [ ] Revit-free `ValueParser` takes "2.7m", "2700", "270 cm", "2,700 mm", "2.700" (BM decimal comma when unambiguous), with a bare number meaning the parameter's display unit.
- [ ] Tests `ValueParserTests.ValueParser_ParsesMixedLengthForms` (Review Focus 3) and `RejectsAmbiguous`.
- [ ] Commit.

**Task 5: Undo + dialogs**
- [ ] Revit-free `UndoModel` (per-document stack of committed BINA groups; any foreign commit, undo/redo of ours, sync or reload disables it).
- [ ] Revit-bound `UndoService`: one `PostCommand(Undo)` per Idling, verifies `GetTransactionNames()` equals the expected "BINA: …", `Redo` on mismatch, checks active document fingerprint and `CanPostCommand`.
- [ ] `TurnReceiptService`: tint transaction named "BINA: highlight" or selection-only; prefix "BINA:".
- [ ] `DialogPolicy`: known dialog ids → safe `OverrideResult`; unknown → `progress revit_busy`.
- [ ] Tests `UndoModelTests.*` and `DialogPolicyTests.*`.
- [ ] Commit.

### W4 — `run_revit_code` (add-in) · branch `feat/copilot-lh-w4-code`
Consumes: W0 `IWriteTool`; W1 guard; W3 runner (preview/commit path is W3's, through `IWriteTool`). Produces: `RunRevitCodeTool : IWriteTool`.

**Task 1: Policy (spec §7.8)**
- [ ] `Resources/copilot/code-policy.json`: allowed assemblies, allowed `System` types, denied Revit members (paths, Export/Save/Open/Close/link/load family/SynchronizeWithCentral/ReloadLatest/ownership/RevitLinkType.Load/Unload/Reload/PrintManager/.Application/PostCommand/ExternalEvent/UpdaterRegistry/event `+=`/TaskDialog/Transaction*/SubTransaction construction).
- [ ] `CodePolicy.Check(Compilation, SyntaxTree) -> PolicyResult` walks the bound IOperation tree and resolves every symbol and its containing assembly.
- [ ] Golden tests, one per denied category: `CodePolicyTests.Rejects_FileIo`, `_Network`, `_Process`, `_Reflection`, `_Export`, `_SaveSync`, `_ApplicationAccess`, `_EventHook`, `_TransactionConstruction`, `_TaskDialog`, `_MemberDeclarationOutsideBody`; and `Accepts_TypicalReadQuery`.
- [ ] They compile against the Nice3point Revit reference assemblies; no Revit runtime needed.
- [ ] Commit.

**Task 2: Wrapper, budget, errors**
- [ ] `StatementWrapper.Build(code) -> SyntaxTree`. It asserts the model text is statements only and computes the line offset.
- [ ] `LoopBudgetRewriter` inserts `__budget.Tick()` at loop back-edges and method/lambda entry, with a depth cap; throws `BudgetExceeded` past 2,000,000 ticks or 30 s.
- [ ] `ErrorMapper`: compile errors with lines relative to the model's code; unwrap `TargetInvocationException`; top 3 frames in the model's code.
- [ ] Tests (pure C# snippets, no Revit):
  - `StatementWrapperTests.RejectsClassDeclaration`;
  - `LoopBudgetTests.InfiniteLoopThrowsBudgetExceeded`;
  - `ErrorMapperTests.LineNumbersRelativeToModelCode`.
- [ ] Commit.

**Task 3: Mode + tool**
- [ ] `ModeClassifier`: any document-modifying symbol → Write, else Read.
- [ ] `RunRevitCodeTool`:
  - **Read** runs inside a TransactionGroup that is always rolled back, then checks `ExternalRevision` didn't move (`policy_rejected` if it did);
  - **Write** goes through W3's runner (`Plan` captures the code text and `expected`);
  - output capped at 16 KB with a "narrow your query" note;
  - DocumentChanged no-op → `noop` with an inline warning.
- [ ] Remove the Excel helpers from the wrapper.
- [ ] Test `ModeClassifierTests.*`.
- [ ] Commit.

### W5 — Pane, client, add-in cleanup · branch `feat/copilot-lh-w5-pane`
Consumes: W0 frames; W2 `ReadExecutor`, `SnapshotService`; W3 `WriteBatchRunner`, `UndoService`; implements `IConfirmPresenter`. Produces: the working pane on protocol 3.

**Task 1: SSE parsing**
- [ ] Revit-free `CopilotSse.Parse(Stream) -> IAsyncEnumerable<SseEvent>`: event/data lines, multi-line data, `seq`.
- [ ] Tests `CopilotSseTests.ParsesEventsAndSeq` and `ToleratesKeepAliveComments`.
- [ ] Commit.

**Task 2: Turn client**
- [ ] `CopilotTurnClient`:
  - `StartTurnAsync`, `PostResultsAsync` (idempotent by `request_id`, retried ×3 with backoff), `ReplayAsync(after)`, `CancelAsync`;
  - `Authorization` header;
  - release-build host allowlist (`binacloud.ai` and staging hosts from `FeedUrls`/`BinaConfig`);
  - turn binding (execute `tool_request` only for the active turn id);
  - compiled-in per-turn budgets (60 calls, 2 MB returned, 8 images);
  - 426 → `UpdateGate` flow.
- [ ] Tests (fake `HttpMessageHandler`):
  - `CopilotTurnClientTests.Client_ResumesEventsAfterSeqAndRepostsResultsIdempotently` (Review Focus 5);
  - `RefusesToolRequestForOtherTurn`;
  - `TurnBudgetTests.*`.
- [ ] Commit.

**Task 3: Pane wiring**
- [ ] `CopilotViewModel`/`ChatView` consume events:
  - `reply_delta` → message;
  - `tool_request` read → `CopilotToolHost.RunReadsAsync`;
  - write → `RunWritesAsync` (W3 shows the card through this pane's `IConfirmPresenter`);
  - `ask` → ask card/form; a project-note ask saves only on Yes;
  - `progress` → step list and busy state.
- [ ] Evidence strip from read envelopes (`searched`, `not_searched`, counts, highlight).
- [ ] Receipt headline from envelopes ("30/34 done · 4 failed (not owned)").
- [ ] Caution line when any write isn't `done`, never decided from reply text.
- [ ] Confirm card per spec §7.5:
  - deletions first, in red;
  - assistant text on its own labelled line;
  - "Approve this build" button when offered.
- [ ] Receipt templates EN/BM (Revit-free `ReceiptTemplates`), with test `ReceiptTemplateTests.RendersFractionsInBothLanguages`.
- [ ] Commit.

**Task 4: Excel in/out + Save as command**
- [ ] Revit-free `TableAttachment.Parse(xlsx|csv) -> {columns, rows}` (reuse `Services/ExcelService.cs`), sent as `attachments[kind=table]`.
- [ ] Export button on result tables and schedules (`ExcelService` write, with a file picker owned by the pane).
- [ ] "Save as command" on a successful `run_revit_code` commit or `set_parameters` stores `{tool, args, intent}` in the existing command library (`UI/Copilot/Screens/LibraryView`, `CommandSaveWindow`). Re-runs go through `CopilotToolHost` with preview, without the model.
- [ ] Test `TableAttachmentTests.ParsesHeaderAndRows`.
- [ ] Commit.

**Task 5: Delete the old pipeline (spec §17, add-in part)**
- [ ] First move the vetted catalog, tool forms and `/open-view` onto `CopilotToolHost`.
- [ ] Then delete:
  - `Services/ToolLoopRunner.cs`, the old pending/resume parts of `ToolLoopService.cs`/`ToolLoopDtos.cs`, `RevitChatRouter.cs` (replaced);
  - `UI/Copilot/RevitCopilotExecutor.cs`, `Handlers/CodeExecutionHandler.cs`, `Services/ConfirmGate.cs`, `AutoApprovable` usage;
  - `BinaVibe/Mcp/McpServer.cs`, `BinaVibe/Mcp/McpTunnelClient.cs`;
  - `Services/EngineManager.cs`, `EnginePreflight.cs`, `EngineBootManifest.cs` and engine settings in `BinaConfig.cs`;
  - engine bundle packaging in `.github/workflows/release.yml`/`installer/**`;
  - `BinaVibe/**/DEPRECATED_*.cs` (and their `<Compile Remove>` lines).
- [ ] Delete only what the build proves is unreferenced.
- [ ] Build all TFMs.
- [ ] `grep -rn "EngineMode\|ToolLoopRunner\|RevitCopilotExecutor\|McpTunnelClient" --include=*.cs .` → only comments, or nothing.
- [ ] Commit.

### W6 — `check` engine (add-in) · branch `feat/copilot-lh-w6-check`
Consumes: W0 `RuleSet` contract; W2 `IHandleStore` (via `ToolContext`); W1 `EnvelopeBuilder`. Produces: `CheckTool`.

**Task 1: Rule set parsing**
- [ ] `RuleSetLoader.Parse(JsonElement rule_set_content) -> RuleSet`. It validates kinds and units.
- [ ] Test `RuleSetParseTests.ParsesContractExample` (uses `contracts/copilot/examples/ruleset-model-health.json`) and `RejectsUnknownKind`.
- [ ] Commit.

**Task 2: Generic assertions (Revit-free)**
- [ ] `GenericAssertions.Evaluate(assertion, ElementValues) -> Outcome {Pass, Fail(reason), NotVerified(reason)}` for `not_empty`, `matches`, `in_list`, `between`, `equals_param`, `exists`, `count_le`.
- [ ] Empty / null / "0" / whitespace count as empty.
- [ ] `unverified_value` or an unreadable value → NotVerified.
- [ ] Tests `GenericAssertionTests.*` and `Evaluate_UnreadableValue_IsNotVerifiedNeverPass` (Review Focus 2).
- [ ] Commit.

**Task 3: Selectors + geometric kinds (Revit)**
- [ ] `Selector`: categories, levels, type regex, has_param, host/all links (unloaded link → every rule over it is NotVerified with reason).
- [ ] Geometric kinds, reusing existing audit code where it fits:
  - `grid_aligned_with_link` / `level_matches_link`: transform applied; match by name, then parallel within `tolerance_deg` and offset within `tolerance_mm`;
  - `clear_width_ge`, `turning_circle_ge`;
  - `room_enclosed`, `room_placed` (`Location == null`);
  - `dimension_overridden` (`ValueOverride` not empty);
  - `tagged_in_view`;
  - `duplicate_instances` (quantised bbox key);
  - `imported_cad_present`, `in_place_family`;
  - `warnings_group` (`doc.GetWarnings()` grouped by `GetFailureDefinitionId`, with each group's description from `Application.GetFailureDefinitionRegistry()` and the definition GUID in `top[].reason`).
- [ ] Commit.

**Task 4: Report + tool**
- [ ] `CheckReport` (Revit-free) builds the §2.5 `data` with `top` ≤ 5 and a fail handle per rule.
- [ ] `CheckTool.Run` validates `rule_set_content`, applies `scope`, evaluates and returns the envelope, with `searched` listing links and NotVerified reasons.
- [ ] Test `CheckReportTests.TotalsAddUpAndTopCapped`.
- [ ] Commit.

### W7 — Loop + turns (bina-ai, bina-alembic) · branch `feat/copilot-lh-w7-loop`
Consumes: W0 `contracts.py`; W8 `lookup` service (`app.services.assistant.lookup.service.lookup(query|id, kind, tenant_user) -> LookupResult`, `catalogue_lines() -> list[str]`, `suggest(text, k=2) -> list[str]`, `link_identifiers(text) -> list[KnowledgeEntry]`); `app.services.credits_meter.charge_run`. Produces: `app.routers.copilot.router`, `app.services.assistant.loop.run_step(turn_id) -> AsyncIterator[Event]`.

**Task 1: Prompt + toolbox + fences**
- [ ] `app/prompts/copilot_system.md` (≤2,048 bytes, the principles in spec §8.3 item 1, English).
- [ ] `toolbox.py`:
  - `tool_array()` from `contracts.load_tools()`;
  - `validate_args(tool, args)` via jsonschema;
  - `rewrite_for_addin(tool, args, turn)` (check → `rule_set_content`, set_parameters → `table_rows`).
- [ ] Tests:
  - `test_fences.py::test_prompt_under_2048_bytes`;
  - `test_exactly_nine_tools`;
  - `test_static_prefix_under_6000_tokens` (tiktoken `cl100k_base` as a proxy);
  - `test_assistant_package_under_2500_lines`.
- [ ] Commit.

**Task 2: Turn store**
- [ ] Migration in bina-alembic:
  - `copilot_sessions(id, user_id, created_at)`;
  - `copilot_turn_events(turn_id, seq, kind, body jsonb, created_at, primary key (turn_id, seq))`;
  - `copilot_project_notes(id, user_id, project_key, text, source, created_at)`;
  - all additive, following repo conventions.
- [ ] `turn_store.py`: `append(turn_id, kind, body) -> seq`, `events_after(turn_id, seq)`, `load_messages(turn_id)`, `record_results(turn_id, request_id, results) -> bool (False if seen)`.
- [ ] Tests (sqlite or a fake repository when Postgres is absent; marked as in W9 Task 1):
  - `TurnStore_ResultsPostIsIdempotentByRequestId` (Review Focus 5);
  - `test_events_after_seq`;
  - `test_events_scoped_to_user` (a different user's token can't read or append to a turn);
  - `test_project_note_saved_only_on_yes`.
- [ ] Commit.

**Task 3: Model client + thinking + billing + tracing**
- [ ] `model_client.py`: `stream_chat(messages, tools, thinking: bool) -> AsyncIterator[Chunk]` via `openai` against the DeepSeek base URL, model `deepseek-flash`, explicit `thinking`; `reasoning_content` passed back when thinking was used.
- [ ] `thinking_policy.py`: `should_think(step, last_envelopes, used_playbook) -> bool` per spec §8.2.
- [ ] `billing.py`:
  - `tokens_from_usage(usage) -> TokenCounts` (`prompt_cache_hit_tokens`, `prompt_cache_miss_tokens`, `completion_tokens`);
  - `charge(turn_id, cumulative)` → `credits_meter.charge_run`;
  - cancelled streams estimated.
- [ ] `tracing.py`: manual Langfuse generations with `usage_details` and `cost_details` computed in code; `mask` hashes paths and drops image bytes.
- [ ] Tests `test_thinking_policy.py::*`, `test_billing.py::test_cache_hits_billed_cheaper`.
- [ ] Commit.

**Task 4: Loop**
- [ ] `context.py`: prefix order per spec §8.3; tail = snapshot + suggested playbooks + linked identifiers + user text; history collapses at turn close.
- [ ] `loop.py`:
  - stream the model;
  - no tool calls → reply + `turn_end`;
  - server tool (`lookup`) inline;
  - reads → `tool_request{kind:read}`, persist, return;
  - writes → one `tool_request{kind:write}`;
  - `ask_user` → `ask`;
  - a `project_note` answer of Yes → `turn_store.save_note(user_id, project_key, text, source=turn_id)`, where `project_key` = the snapshot's `doc.version_guid` + `path_hash`;
  - stop rules: same tool+args twice, 3 consecutive same error codes, active time >120 s, steps >20, cancel.
- [ ] `hints.py` appends known compile/runtime error hints to `next`.
- [ ] Tests (fake model + fake add-in):
  - `test_loop.py::Loop_RevitBusyTwice_StopsAndReplies` (Review Focus 4);
  - `test_reply_text_equals_model_text`;
  - `test_only_tool_results_and_answers_appended_between_calls`;
  - `test_write_calls_batched_in_one_request`.
- [ ] Commit.

**Task 5: Routes**
- [ ] `app/routers/copilot.py` (thin) with the four routes from §2.2: auth via the existing token dependency, 409/426, SSE.
- [ ] Tests (Starlette `TestClient`): `test_routes.py::test_turn_stream_and_results_roundtrip`, `test_protocol_mismatch_426`.
- [ ] Commit. W9 includes the router in `app/main.py`.

### W8 — Knowledge, playbooks, rules (bina-ai) · branch `feat/copilot-lh-w8-knowledge`
Produces the `lookup` API that W7 consumes (signatures in W7).

**Task 1: Lookup service**
- [ ] `lookup/service.py` routes `kind`:
  - knowledge → existing `jkr_specs_kb`, `revit_api_kb`, `data/jkr_param_rules.json`, `data/naming_standards/*`;
  - playbook → `data/copilot/playbooks/*.md`;
  - rules → `data/copilot/rules/*.json`.
- [ ] Results are capped (≤6 snippets, 8 KB with an outline). Query rewrite goes through the shared provider client (not gpt-4o). The reranker is off the event loop.
- [ ] `linking.py` does exact-identifier linking (closed vocabulary from the param rules and naming segments).
- [ ] Fix the sumber segment bug.
- [ ] Add `data/copilot/warnings/*.json`: curated EN/BM fix notes keyed by Revit failure definition GUID, for the ~40 most common warnings (duplicate instances, overlapping walls, room not enclosed, highlighted walls overlap, …). `lookup(kind:knowledge)` searches them by GUID or text.
- [ ] Tests:
  - `tests/assistant/lookup/test_lookup.py::test_kind_routing`;
  - `test_caps`;
  - `test_linking_exact_identifier_only`;
  - `test_sumber_segment_maps_to_one_param`.
- [ ] Commit.

**Task 2: Rule sets**
- [ ] `data/copilot/rules/{jkr-naming, jkr-lod300-params, model-health, ubbl-basics, ms1184-basics, bomba-basics, doc-hygiene}.json` per §2.5, each rule citing its clause.
- [ ] Thresholds not confirmed from a primary source are marked `unverified_value: true`.
- [ ] Sources:
  - JKR: Garis Panduan BIM JKR / Piawaian BIM JKR / Spesifikasi Parameter (spec §20, research doc 06 §1);
  - UBBL;
  - MS 1184;
  - the IPM BOMBA guide.
- [ ] Tests:
  - `test_rules_schema.py::test_all_rule_sets_validate`;
  - `test_every_rule_has_cite_and_bilingual_titles`;
  - `test_regex_rules_accept_their_examples`.
- [ ] Commit.

**Task 3: Playbooks**
- [ ] The 16 playbooks of spec §22 in `data/copilot/playbooks/`, per §2.6. Rewritten from the audited ideas (research doc 06 §3); C# targets Revit 2023–2027 APIs (`ElementId.Value` guarded, `GroupTypeId`).
- [ ] `playbooks.py`: `catalogue_lines()` (sorted, byte-stable), `suggest(text, k=2)` (EN/BM keyword match, `not_for` checked first), unknown name → catalogue.
- [ ] Tests (`test_playbooks_lint.py`):
  - `test_size_caps`;
  - `test_front_matter_schema`;
  - `test_required_sections_in_order`;
  - `test_tools_subset_of_nine`;
  - `test_catalogue_under_600_tokens`.
- [ ] Commit.

### W9 — Evals, extract, delete, CI (bina-ai) · branch `feat/copilot-lh-w9-evals`
Consumes: W0 contracts, W7 router (at integration). Produces: the one-command test baseline, the eval harness, the cleaned backend.

**Task 1: Test baseline** (plan B01, `~/Desktop/Copilot Improvement/plans/B01-test-baseline.md`)
- [ ] Root `conftest.py` auto-marks DB/network tests.
- [ ] `scripts/test-copilot.sh` runs `tests/assistant` + `tests/evals_copilot` + the marked offline set.
- [ ] Add one line to CLAUDE.md "Commands".
- [ ] Commit.

**Task 2: Eval harness** (spec §8.9; plan B05 for seams)
- [ ] `evals/copilot/worlds/*.json`: 3 synthetic world models (JKR-style office, a 2-storey school block, MEP-heavy), each with levels, grids, rooms, typed elements, parameters and one unloaded link. No customer data.
- [ ] `simulator.py` answers the 7 add-in tools for any args per contract (find, inspect incl. visibility reasons, check generic assertions, set_parameters diffs with preview/commit, look → placeholder image), with fault injection (rollback, partial, not_owned, revit_busy, zero matches).
- [ ] `corpus/*.yaml`: 60 cases from spec §20 and research doc 06, ≥40% BM, 12 fault-injected, 6 ambiguous. Each case has `prompt`, `world`, `expected_facts`, `must_not_claim`, `expect_ask`, `expect_citation`.
- [ ] `graders.py`:
  - numbers ⊆ envelopes, normalized (1,013 / 1.013, units, names);
  - end state;
  - must-not-claim;
  - ask on ambiguity;
  - citation;
  - language match;
  - no dumps;
  - calls and latency.
- [ ] `run.py` runs k=3 and prints a summary table.
- [ ] `baseline.py` grades recorded production replies from a local path given by `COPILOT_BASELINE_DIR`, never committed.
- [ ] Tests:
  - `tests/evals_copilot/test_simulator.py::test_find_zero_lists_not_searched`;
  - `test_graders.py::test_number_normalization`;
  - `test_corpus_counts`.
- [ ] Commit.

**Task 3: Extract shared seams** (spec §17 extract list)
- [ ] Move `_deepseek`/`get_model` into `app/models/factory.py` and update the callers (cmms, kpkr, jkr_autofix, jkr_domain_agents).
- [ ] Move the codegen/repair agents used by `compile_gate`, `codegen_retry` and `revit_codegen_lint` into their own module.
- [ ] Keep only the shaping helpers that `tool_dispatch`, `codegen_judge` and `revit_stream_events` need.
- [ ] Move the shared embedder/DB URL out of `revit_recipes_kb`.
- [ ] Re-key `revit_feedback` on `turn_id`.
- [ ] Test `tests/test_extracted_seams.py::test_other_agents_import_without_revit_ai`.
- [ ] Commit.

**Task 4: Delete the old pipeline** (spec §17 delete list, backend)
- [ ] Delete:
  - `app/services/revit_turn.py`;
  - the `app/routers/revit_turn/*` turn routes (replaced by 410 stubs returning `{update_required:true}`);
  - truth gate, repair, anti-bluff and synthesis code;
  - the keyword routers, recipe push and API block;
  - the copilot LearningMachine, compression and summaries;
  - `REVIT_*` flags;
  - the Agno copilot agent factories;
  - `app/engine/*`, and `/gateway/*` if unused.
- [ ] Wire `app.routers.copilot.router` into `app/main.py`; update `lifecycle.py`.
- [ ] Tests: `tests/test_old_routes_410.py`; the full `bash scripts/test-copilot.sh` green; `uv run python -c "import app.main"` OK.
- [ ] Commit.

**Task 5: CI fences**
- [ ] `.github/workflows/copilot-fences.yml` (ubuntu, ~1 min) runs `uv sync` and `bash scripts/test-copilot.sh`. Linux only, no Windows runners.
- [ ] Commit.

---

## 6. Done = integration checklist (orchestrator)

- [ ] All 9 branches merged into both `feat/copilot-lean-harness` in the §1 order; conflicts resolved.
- [ ] Add-in: all three TFMs build; every Revit-free test passes in the scratch harness; the bare-commit grep is empty.
- [ ] Backend: `bash scripts/test-copilot.sh` green; fences green; `import app.main` OK.
- [ ] Eval: `uv run python -m evals.copilot.run` produces a table; baseline recorded.
- [ ] PRs opened to `develop` in both repos (and bina-alembic) with test evidence. **The owner merges.**
- [ ] Owner-only, after merge:
  - live-Revit battery on staging (VMPal spike or a cloud GPU PC);
  - signed staging release;
  - production promotion (spec §15 P5).
