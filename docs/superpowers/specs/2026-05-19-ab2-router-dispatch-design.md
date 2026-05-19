# AB2 — Router Dispatch Rework (Tool-First + Vetted Synthesizers)

**Date:** 2026-05-19
**Status:** Approved design (pre-implementation)
**Repo:** `revit-addin-sync` (branch off `feat/sp3b-addin-backend-alignment`,
which already contains AB1).
**Scope:** Second slice of the addin↔backend alignment. AB2 is the dispatch
rework only. AB1 (transport `/api`→`/agents`) is done. AB3 (graceful
degradation for backend-unserved aux endpoints) is a separate sub-project.

## Problem

`AIAssistantWindow.xaml.cs::ResolveActionCode(action, prompt)` dispatches by
`switch (action.Type)` and returns a C# string the executor runs. SP3a's
`/agents/revit-ai/route` emits `RouteAction.Tool` ∈ {`rename_elements`,
`set_parameter`, `open_view`, `export_schedule`, `select_elements`, `code`}
with `Type = ""` for vetted tools (or `"unvetted_code"` for the raw-C#
fallback). Because the switch is on `Type`, every vetted SP3a action falls into
`default` → `_aiService.GenerateCodeAsync(...)` (raw LLM codegen). **SP3a is
entirely bypassed** — the structured, vetted tool calls are ignored and the
addin re-asks the LLM for raw code, defeating SP3a's cost/accuracy/safety win.

Existing native synthesizers exist for `open_view` (inline in
`ResolveActionCode`), `select_elements` (`BuildNativeSelectionCode`), and
`export` (`BuildNativeExportCode`). `rename_elements` and `set_parameter` have
no synthesizer. `run_analysis` opens Cost/JKR dashboards and must keep working.

## Goal & Success Bar

Addin-only. SP3a's 5 vetted tools dispatch natively (deterministic C#
synthesis, no LLM round-trip) via a Tool-first branch. Unmatched/empty `Tool`
falls through to the existing `Type` switch **unchanged** (zero regression:
`run_analysis` dashboards, `execute_code`, legacy producers preserved). Only
`open_view` auto-runs; mutating/file/selection tools gate behind Run/Discard.
The new synthesizers live in a pure, Revit-free file unit-tested in the
`Tests` project. Done = source guards + unit tests pass; `dotnet build`/`dotnet
test` operator-verified on Windows.

## Decisions (locked during brainstorming)

- **Native synthesizers for all 5 vetted tools** (realizes the SP3 keystone:
  vetted, no-LLM, cheap, safe). **Three are new** in `VettedToolCode.cs`:
  `rename_elements`, `set_parameter`, `export_schedule`. **Two reuse** existing
  synthesizers (no rewrite; additive param-key acceptance only): `open_view`
  (inline synth already aliases `view_name`) and `select_elements`
  (`BuildNativeSelectionCode`).
- **Reality-check (found at plan grounding):** `export_schedule` was originally
  slated to reuse `BuildNativeExportCode`, but that builder is a *category
  element dump* keyed on `BuiltInCategory` via `ExtractTargetFromAction`. SP3a's
  `export_schedule` has no category — it names a Revit `ViewSchedule`
  (`schedule_name`) to export. Semantic mismatch ⇒ it is a **new** synthesizer,
  not a reuse.
- **Tool-first, Type fallback:** branch on `action.Tool` first; if `Tool` is
  empty or unrecognized, fall through to the existing `switch (action.Type)`
  **unchanged**. Additive, zero regression.
- **`unvetted_code`/`code` → `action.Code` directly** (like `execute_code`).
  No re-call to `GenerateCodeAsync` — SP3a already put raw C# in `action.Code`;
  regenerating wastes a call and is non-deterministic.
- **Auto-run policy:** only `open_view` auto-runs. `rename_elements`,
  `set_parameter`, `export_schedule`, `select_elements` gate behind Run/Discard
  (user reviews synthesized C# first). The full dry-run/blast-radius gate is
  SP4 — out of scope; AB2's bar is "never auto-execute a mutation."
- **Approach B (extract pure file):** synthesizers + param accessor + auto-run
  predicate live in a new dependency-free `Services/VettedToolCode.cs`,
  compile-linked into `Tests` (the AB1 CRITICAL proved Tests cannot pull
  Revit/Newtonsoft deps; only pure files are testable here). Inline-in-window
  (A) is untestable + bloats an already-large file; registry (C) is YAGNI.

## Non-Goals

- No backend / `bina-ai` change.
- No change to the existing `open_view`/`select_elements`/`export`
  synthesizers beyond *additively* accepting SP3a snake_case param keys if
  they don't already.
- No change to the `switch (action.Type)` cases (run_analysis, execute_code,
  none, default) — preserved verbatim as the fallback.
- No dry-run / blast-radius gate (SP4). No aux-endpoint work (AB3).
- No AIService/transport change (AB1, done).

## SP3a Param Keys (snake_case, from `RouteAction.Params`)

| tool | params |
|---|---|
| `rename_elements` | `target_category`, `find`, `replace`, `scope?` |
| `set_parameter` | `target_category`, `parameter_name`, `value` (str/num/bool) |
| `open_view` | `view_name`, `view_type?` |
| `export_schedule` | `schedule_name`, `format` (`xlsx`/`csv`), `output_path?` |
| `select_elements` | `target_category`, `filter?` |

## Architecture

```
loop over route.Actions
  └── ResolveActionCode(action, prompt)
        ├── action.Tool switch (NEW, first):
        │     rename_elements / set_parameter / export_schedule
        │                       → VettedToolCode.TryBuild(action)
        │     open_view         → existing open_view synthesizer (reuse)
        │     select_elements   → existing BuildNativeSelectionCode(action)
        │                          (ExtractTargetFromAction extended to read
        │                          target_category; allow-list adds it too)
        │     code (or Type=="unvetted_code") → action.Code
        │     null/empty/unknown Tool ↓
        └── existing switch (action.Type)  [UNCHANGED fallback]
  └── autoRunSafe = VettedToolCode.IsAutoRunSafe(action)   (was: Type=="open_view")
```

`Services/VettedToolCode.cs` is pure (no Revit/Newtonsoft) → compile-linked
into `Tests`, unit-tested deterministically. `AIAssistantWindow.xaml.cs` stays
lean (only the Tool-first front + the one auto-run line change).

## Components

### `Services/VettedToolCode.cs` (new, pure — only `System*` usings)

- `RouteParams.Get(IDictionary<string, object> @params, params string[] keys)`
  → first key whose value is a non-empty string, else `null`. Mirrors the
  window's `GetParamString` semantics so synthesizers + tests don't depend on
  the window.
- `BuildRenameElements(RouteAction a)` → `string?`. Requires
  `target_category`, `find`, `replace`; optional `scope`. Emits C# that
  collects elements of the category, replaces `find`→`replace` in the element
  name, honoring `scope` when present. Returns `null` if a required param is
  missing/empty.
- `BuildSetParameter(RouteAction a)` → `string?`. Requires `target_category`,
  `parameter_name`, `value`. Emits C# that sets the parameter on each element,
  type-aware (string/double/bool), guarding missing/read-only params. `null`
  if a required param is missing.
- `BuildExportSchedule(RouteAction a)` → `string?`. Requires `schedule_name`;
  optional `format` (`csv` default, or `xlsx`) and `output_path` (Desktop
  default). Emits C# that finds the `ViewSchedule` whose name matches
  `schedule_name` (case-insensitive, exact then contains), reads its
  `TableData`/visible cells, and writes csv (built-in) or xlsx (`WriteExcel`,
  same helper the legacy export uses). `null` if `schedule_name` missing or no
  matching schedule logic can be emitted.
- `TryBuild(RouteAction a)` → `string?`. Returns the synthesized C# for
  `rename_elements`/`set_parameter`/`export_schedule`; `null` for any other
  tool (caller then reuses existing synthesizers / falls through).
- `IsAutoRunSafe(RouteAction a)` → `bool`. True iff the action is `open_view`
  by `Tool` (case-insensitive), or `Tool` empty and legacy
  `Type == "open_view"`. False for everything else (esp. the mutating tools).

The emitted C# uses only the helpers the addin executor already exposes
(`doc`, `FilteredElementCollector`, `ShowMessage`, executor-wrapped
transaction) — identical conventions to the existing `open_view` synthesizer,
so no new executor capability is required.

### `AIAssistantWindow.xaml.cs`

- `ResolveActionCode`: insert the Tool-first branch **before** the existing
  `switch (action.Type)`. The Tool branch `return`s a value for every matched
  tool (synthesized C#, reused-synthesizer output, or `action.Code`); for an
  empty/unknown `Tool` it does **not** return, so control simply falls through
  to the existing `switch (action.Type)` which stays **exactly where it is,
  byte-unchanged** (not moved, not reindented). Minimal diff, zero regression.
- The loop line `bool autoRunSafe = string.Equals(action.Type, "open_view",
  …)` becomes `bool autoRunSafe = VettedToolCode.IsAutoRunSafe(action);`.
- `select_elements` reuse: `ExtractTargetFromAction` currently reads only the
  `category` param. Extend it **additively** to also read `target_category`
  (`GetParamString(p,"category") ?? GetParamString(p,"target_category")`), and
  add `target_category` to `BuildNativeSelectionCode`'s param allow-list. Do
  **not** add `filter`: its presence should keep bailing to the LLM (the native
  selector can't honor arbitrary predicates) — a bare "select all walls" sends
  only `target_category` and now dispatches natively. `open_view` already
  aliases `view_name` — no change needed. `BuildNativeExportCode` is **not**
  touched (export_schedule is the new synthesizer, not this builder).

### `Tests/Tests.csproj` + `Tests/VettedToolCodeTests.cs`

- Add `<Compile Include="..\Services\VettedToolCode.cs"
  Link="VettedToolCode.cs" />` to the existing pure-file `<ItemGroup>` (same
  pattern as AB1's `AiUrl.cs`).
- New xUnit tests (see Testing).

## Data Flow

`/route` → SP3a `RouteAction{tool,params,code}` → loop →
`ResolveActionCode` Tool-first → synthesized C# (or `action.Code`, or
fallthrough) → `AddCodeBlock` → `open_view` auto-runs; the other vetted tools
gate behind Run/Discard → `ExecuteCode` (executor auto-wraps the transaction).
No backend change.

## Error Handling

- A synthesizer with a missing/invalid required param returns `null` →
  `ResolveActionCode` falls through (existing native handler / `Type` switch /
  `default` LLM codegen / clarification). Synthesizers never throw into the
  dispatch loop (the loop's existing try/catch → `AddError` remains the
  outer net).
- `unvetted_code`/`code` with empty `action.Code` → `null` → loop's existing
  `if (string.IsNullOrWhiteSpace(code)) continue;` skips it.
- Emitted C# carries its own null/read-only/empty guards so a bad model/param
  fails gracefully *inside Revit at run time*, not in the dispatcher.

## Safety

Only `open_view` auto-runs (non-destructive navigation). `rename_elements` and
`set_parameter` (model-mutating), `export_schedule` (file write), and
`select_elements` all render to a Run/Discard row — the user sees the exact
synthesized C# before it executes. SP4 will add the dry-run/blast-radius gate;
AB2 only guarantees no mutation auto-executes.

## Testing

`Tests/VettedToolCodeTests.cs` (pure, compile-linked — runs wherever
`dotnet test` runs):

- `RouteParams.Get`: returns first non-empty by key precedence; `null` when
  all missing/empty; tolerates non-string values.
- `BuildRenameElements`: valid params → C# containing the category, `find`,
  `replace`, and (when given) `scope`; `null` when `target_category`/`find`/
  `replace` missing.
- `BuildSetParameter`: valid params → C# setting `parameter_name`; string vs
  numeric vs bool `value` handled distinctly; `null` when a required param
  missing.
- `BuildExportSchedule`: valid `schedule_name` → C# referencing `ViewSchedule`
  + the resolved name; `format=xlsx` emits `WriteExcel`, default/`csv` emits
  csv writing; `null` when `schedule_name` missing.
- `IsAutoRunSafe`: true for `Tool=="open_view"` and for `Tool==""` &
  `Type=="open_view"`; false for `rename_elements`/`set_parameter`/
  `export_schedule`/`select_elements`/`code`.
- `TryBuild`: non-null only for
  `rename_elements`/`set_parameter`/`export_schedule`; `null` for
  `open_view`/`select_elements`/`code`/unknown.

Source guard (in-session, cross-platform — `dotnet` unavailable here): `grep`
confirms `ResolveActionCode` has a `action.Tool` branch, the auto-run line
calls `VettedToolCode.IsAutoRunSafe`, the existing `switch (action.Type)`
text is still present (fallback preserved), and `VettedToolCode.cs` has no
Revit `using`.

Operator (Windows): `dotnet build revit-addin-sync.sln -c Release` +
`dotnet test Tests/Tests.csproj`; manual smoke that a "rename all walls X→Y"
and "set Fire Rating to 2 HR on doors" prompt now dispatch natively (no
`/generate` call) and present a Run/Discard row (no auto-execute).

## Out of Scope (deferred)

| Item | Disposition |
|---|---|
| Aux endpoints (explain-error/record-fix/health/commands) graceful degrade | AB3 |
| Dry-run / blast-radius gate | SP4 |
| Rewriting the existing open_view / select_elements synthesizers | Excluded (reuse + additive `target_category` only) |
| Reusing `BuildNativeExportCode` for export_schedule | Excluded (semantic mismatch — new synthesizer instead) |
| Backend / bina-ai change | Out of repo |
| Pre-existing CS1587 `<summary>` nit on `UpdateCommandCodeAsync` | Optional tidy if AB2 touches that region; not required |
