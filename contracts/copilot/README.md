# Copilot shared contracts (protocol 3)

The wire contracts between the Revit add-in (`revit-addin-sync`) and the cloud
loop (`bina-ai`) for the lean-harness copilot. Spec:
`docs/superpowers/specs/2026-10-24-copilot-lean-harness-design.md` §9–§11, §21,
§22 (add-in repo); plan §2.

**This folder is byte-identical in both repos.** Change it only through the
orchestrator, in both repos in the same change, and check with
`diff -r <add-in>/contracts/copilot <bina-ai>/contracts/copilot`.

## Files

| File | What it is |
|---|---|
| `envelope.schema.json` | What every add-in tool returns, including the four invariants below |
| `protocol3.schema.json` | Turn/results request bodies, SSE event data, the 426 body, the add-in-side shape of rewritten args; `x-routes` lists the four routes |
| `tools.json` | The 9 tools exactly as the loop sends them to the model: `[{name, kind, description, parameters}]` |
| `snapshot.schema.json` | The Revit context snapshot sent with every request |
| `ruleset.schema.json` | `check` rule sets, plus `$defs/check_result` (the `data` of a check envelope) |
| `playbook.schema.json` | Playbook YAML front-matter; `x-body-headings` and `x-max-bytes` for the body |
| `examples/*.json` | One valid instance of each; the contract tests in both repos load them |

Implementations: `BinaVibe/Copilot/Contracts/` (C#, System.Text.Json) and
`app/services/assistant/contracts.py` (pydantic v2). Their tests check every
example, the enums against these schemas, and the 9-tool / 600-character
limits.

## Conventions

- JSON keys are `snake_case`. Element ids are int64 JSON integers.
- **Null means absent.** Every nullable field is optional. Producers omit null
  fields (C# `WhenWritingNull`, pydantic `exclude_none`); readers also accept an
  explicit `null`. Example: a host element's ref is `{"id": 312045}`; a linked
  one is `{"id": 77, "link": 9001}` (the link instance id).
- Objects are closed (`additionalProperties: false`) except envelope `data` and
  tool `args`.
- Values are in display units with their unit (`"2700 mm"`).
- Lists in envelopes carry at most 50 rows, plus the exact `count` and a
  `handle`.
- Text limits are counted in Unicode code points.

## Envelope invariants

1. `ok == false` ⇒ `error` is present.
2. `error.code == not_found` ⇒ `error.candidates` is present (empty only when
   nothing similar exists).
3. `phase == commit && ok` ⇒ `verified.method != none`, or `status == noop`.
4. `count == 0` on a read ⇒ `searched` is present.

## Protocol 3 in one paragraph

`POST /copilot/turns` opens an SSE stream; the stream ends after a
`tool_request`, an `ask` or a `turn_end`. The add-in runs the calls, then
`POST /copilot/turns/{turn_id}/results` (idempotent by `request_id`) opens the
next stream. `GET /copilot/turns/{turn_id}/events?after=<seq>` replays a
dropped stream; `seq` starts at 1 and increases within a turn. Read requests
never carry `set_parameters`; write requests carry only `set_parameters` and
`run_revit_code`, so every write reaches Revit's confirm card.

## Cloud-side rewrites (never visible to the model)

- `check.rule_set` (an id) becomes `rule_set_content` (the full rule set).
- `set_parameters.from_table` (an attachment name) becomes `table_rows`: one
  object per table row, keyed by column name.

`protocol3.schema.json#/$defs/addin_args_check` and `addin_args_set_parameters`
describe what the add-in receives.
