# Revit Copilot redesign: drafter-ready result pane

Date: 2026-11-10 · Tracking: Linear [AI-277](https://linear.app/bina-ai/issue/AI-277) (35 sub-issues, AI-278 to AI-312) · Evidence base: add-in `fix/copilot-jumble` @ `4354365`, bina-ai `fix/copilot-jumble` @ `138d4673`

## Intent

The 9 Nov downlight demo and the 10 Nov brochure run did the right work in the model, but the pane output could not be used by a drafter:

- replies of 128 words, with family names inline;
- Copilot's own choices buried in the last bullet;
- "Not searched" lines that read like errors;
- duplicated buttons;
- in the 10 Nov run, 152 listed "changes" across 10 types, including values that were written and then put back.

To check the result, the drafter had to open Type Properties by hand.

This plan comes from a review of both repos (pane, engine, backend, dead code) and research on BIMLOGIQ Argus, Agent A and Autodesk Assistant.

## Design

- Redesign canvas, 10 boards, interactive: https://claude.ai/artifact/C4Z7To5H6ZiqS7tCfncutW
- Design chat: https://claude.ai/chat/90db8126-e8ed-426f-b332-0b2b63aa3b6c?artifact=59938bc2-2a10-45ee-b285-43e56752e043
- First clickable prototype: https://claude.ai/artifact/4ijgfmbW6V2v54QQ94rZRQ

Every finished turn renders the same pattern:

1. One sentence, with counts read back from Revit.
2. "Check N things": each item has answer buttons, and an answer is a write.
3. The full list, with Before, After and a source per value: brochure page, your prompt, Copilot's choice, or Excel row.
4. Rows linked to Revit: clicking a row selects and zooms to the elements.
5. Undo all, Copy table, Save as command, and next-step chips.

Boards:
- 1 new chat
- 2 working
- 3 brochure fill result
- 4 source of a value
- 5 isolate
- 7 ten types, one changed back
- 8 sheet generation
- 9 sheets from Excel
- 10 smart tags

Board 6 (a wide write asks once) is dropped by D1. Boards 8 to 10 use example data.

## Decisions (Ashraf, 2026-11-10)

- **D1:** writes auto-apply. There is no confirm step of any kind, and Undo is the safety net.
- **D2:** delete the MCP-era tools in BinaVibe. Move the sheet, viewport and tag code out first.
- **D3:** delete the bina-ai recipe loop. The lean loop never feeds it.
- **D4:** Compliance dashboard, Cost import/export, Rollback and BimDiscipline stay in the code, hidden.
- **D5:** the pane defaults to dark.

## Findings

**Root cause.** The final answer is free model prose. Counts, assumptions, per-value source and language have no structured channel, so the pane guesses. For example, the Source column is one attachment name regexed from the prompt (`CopilotViewModel.cs:1627-1632`).

**Pane** (`UI/Copilot`, about 24k lines):
- The UI is built in C# code-behind: 534 hand-built controls and 0 DataTemplates.
- Any change, even a 24 px resize, rebuilds the whole thread (`ChatView.xaml.cs:446-477`).
- Card state lives in the visual tree, so an editable cell would lose its text.
- The result card is attached after the reply is drawn (`CopilotViewModel.cs:1440,1446`).
- `CopilotViewModel` is 1,996 lines; `ChatMessage` has about 45 fields, 12 of them unused.

**Engine** (`BinaVibe`, about 57k lines):
- The 7 lean tools sit on a legacy `ToolRegistry` with 176 arms; the pane calls 6 of them by name.
- There are two Revit-thread dispatchers (`McpJobPump`, `RevitThread`).
- Envelopes are half-typed.
- Undo works per turn only, has no Redo, and stops working after the drafter's next edit (`UndoModel.cs:69-73`).
- The confirm stack (about 1,050 lines) has been unreachable since auto-apply.
- No sheet, viewport or tag tool exists on the current path. The only implementation is in `Mutators.cs` and `TypedJobs.cs`.

**Backend** (bina-ai):
- English is hard-coded (`copilot_system.md:5`, `context.py:40`), yet 34 of 67 evals expect Malay.
- Brochure PDFs are pasted into the prompt as text: 20,000 characters, kept in every later turn's history.
- Turns take 56 to 116 s. Thinking is forced on the final step, and the playbook costs an extra call.
- About 12k lines of the earlier generation still load at startup.
- The flag-fence test fails on this branch.

**Dead code:**
- add-in, safe to delete now: about 7.8k lines;
- MCP-era tools, deleted by D2: about 24k lines;
- bina-ai: about 12k lines;
- stale docs: about 2.3k lines.

Details are in each Linear issue.

## Target shape

- **Typed `turn_end.result`** in protocol 3, with these fields:
  - `reply`, `lang`, `kind` (`changes`, `created`, `findings`, `selection`), `counts`;
  - `values[{param, target, before, after, source, verified}]`, `checks[{text, actions}]`, `not_searched`.

  The server fills facts from the envelopes; the model supplies only the sentence, the checks and the sources.
- **`set_parameters`** takes a per-value `source`, and its change rows carry scope, names, raw value plus unit, and source.
- **Brochures** become a document attachment with pages, so a value can cite its page and snippet.
- **Pane:**
  - a WPF-free `Turn` model and a `ChatSession` reducer;
  - one retained view per turn;
  - Revit work behind `IRevitActions`;
  - one token set, dark by default.
- **Engine:**
  - one Revit-thread dispatcher;
  - per-batch Undo and Redo with net-change reporting;
  - new `create_sheets` and `place_tags` tools built on the moved code.
- **Constraint:** replace code in place, with no `v2` folders or files.

## Plan

Each item is a Linear sub-issue of AI-277 with why, what, evidence, done-when and "blocks" links.

| Phase | Items |
|---|---|
| Week 1 (Todo) | AI-278 render order · AI-279 wrong "always ask" note · AI-280 "drafter approves" tool text · AI-281 flag fence · AI-282 reply language · AI-283 evals · AI-284 test linking · AI-285 one Save as command · AI-286 no final-step thinking · AI-287 inline playbook · AI-288 opt-in rewrite · AI-289 step timing · AI-290 delete confirm stack · AI-291 Undo/Redo per batch · AI-292 safe delete · AI-293 hidden features stay hidden |
| Weeks 2 to 3 | AI-294 typed turn result · AI-295 server-built result · AI-296 per-value source · AI-297 document attachment · AI-298 typed tool results · AI-299 one dispatcher · AI-300 replace by-name registry calls · AI-301 live smoke tests · AI-302 bina-ai legacy delete · AI-303 MCP-era delete (after AI-300, AI-304) · AI-304 move sheet/tag code |
| Then | AI-305 Turn model · AI-306 retained views · AI-307 result card · AI-308 working state · AI-309 home and tabs · AI-310 ports and view-model split · AI-311 tokens, dark · AI-312 history keeps cards |

Not yet in Linear (free-plan issue limit); kept as a checklist in AI-277:
- #46 `create_sheets`
- #47 sheets from Excel with a remembered column match
- #48 `place_tags`
- #49 sheet and tag result cards
- #29 pop-out grid
- #34 dead members
- #35 unused tokens
- #36 stale docs
- #39 follow-up chips
- #41 ribbon commands
- #42 JKR/Bomba checklists
- #43 selection as context

## Left to the builder

- XAML DataTemplates versus code-built views: spike in Revit's pane host first, because storyboards have crashed there.
- How far Undo can survive unrelated drafter edits within Revit's undo stack.
- Ring and preview drawing details, as in the 2026-11-09 result-pattern spec.
