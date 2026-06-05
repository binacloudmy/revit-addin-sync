# Contract: backend-dispatched vetted tools (hybrid routing)

Status: implemented.
Repos: `revit-addin-sync` (add-in) ⇄ `bina-ai` (backend, branch `feat/vetted-tool-dispatch`).

## Goal
When a prompt maps cleanly to one of the add-in's 5 deterministic **vetted Tier-1
tools**, `bina-ai` returns a structured `{vetted_tool, vetted_args}` directive and
the add-in runs its **deterministic synth** — no codegen, no compile. Codegen
becomes the last resort.

Routing is three tiers:
1. **Local fast-path** (add-in, instant/offline) — `QueryInterpreter.MatchVetted`.
2. **Backend vetted dispatch** (this contract) — for messier phrasings the regex can't parse.
3. **Codegen / tool-calling** — when no vetted tool fits.

## Request — `POST /agents/revit-ai/generate` (+ `/stream`)
Add one optional flag (backwards-safe; old add-ins omit it and only ever get code/reply):
```jsonc
{ "prompt": "...", "context": { ... }, "userId": 1, "sessionId": "...",
  "supports_vetted_dispatch": true }
```

## Response — two new fields
```jsonc
{ "success": true,
  "vetted_tool": "rename_elements",          // BackendName, or null
  "vetted_args": { "category": "Walls", "find": "CW", "replace": "C-WALL" },
  "code": "",                                  // empty when vetted_tool is set
  "is_query": false,                           // false for mutating tools
  "reply": "Rename CW → C-WALL on Walls." }
```
Streaming: emitted as a single `done` event with the same fields.

**Rule:** if `vetted_tool` is non-null and `success` is true → add-in runs that
tool with `vetted_args` and **ignores `code`**. Otherwise behaves as today
(code → run; empty → reply).

## Tool vocabulary + arg schema
`vetted_tool` = catalog **BackendName**; `vetted_args` keys = catalog **field ids**
(what the synth reads). Names must be the live model's exact names.

| `vetted_tool` | mutating? | `vetted_args` keys |
|---|---|---|
| `open_view` | no | `view` (req), `type` (opt) |
| `select_elements` | no | `category` (req), `level` (opt) |
| `rename_elements` | **yes** | `category`, `find`, `replace`, `scope?` |
| `set_parameter` | **yes** | `category`, `param`, `value`, `scope?` |
| `export_schedule` | no | `schedule`, `format` (xlsx\|csv) |
| `tag_elements` | **yes** | `category` (Walls\|Doors\|Windows\|Floors\|Furniture), `mode?` (One per type\|Every instance) |

## Add-in behavior
- `CopilotCatalog.FindByBackendName(vetted_tool)` → tool; `vetted_args` → FormValues.
- **Mutating** (`rename_elements`, `set_parameter`) → confirm card (Run/Cancel).
- **Non-mutating** → run immediately.
- Local fast-path still wins first. If a required arg is missing/unresolvable →
  graceful fallback to the reply/hint.

## Backend behavior
- `app/agents/vibe/vetted_dispatch.classify_vetted`: fast-model agent
  (`output_schema=VettedDispatch`) picks tool+args from prompt + live context,
  or declines (`tool=null`) → request falls through to the pipeline.
- Validates tool allowlist, required args, and a confidence gate (default 0.7).

## Context fields (aligned)
The add-in's `ModelContext` JSON now uses snake_case matching the backend
`RevitModelContext` (`project_name`, `active_view_name`, `active_view_type`,
`selected_element_ids`, `revit_version`), and `BuildContext` now sends
`view_names` (≤200) and `schedule_names`. So the classifier can fill exact names
for **all** vetted tools, including `open_view` and `export_schedule`.
Previously camelCased fields were silently dropped by the backend.
