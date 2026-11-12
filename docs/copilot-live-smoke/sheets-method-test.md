# Sheets live test: fourteen prompts, twice each

## What this test is for

The first run of this test (11 November 2026) gave 8 of 10 right, before and after the change to stored scripts. The two wrong prompts were wrong in the same way both times: the Copilot chose a plan for the drafter, and it created a ceiling plan that nobody asked for. It also asked about sheet numbers when it did not need to.

The backend has been changed since then to fix those. Each fix is checked offline first. This live run checks two things only:

1. The fixes behave the same in real Revit as they do offline. If a live result disagrees with the offline one, write it down. It means the offline simulator is wrong somewhere.
2. Nothing that worked on 11 November got worse.

This run does not give a score. Two repeats of one prompt cannot prove a rate. A prompt that was wrong and is now right in both repeats is reported as "consistent with the offline result", not as "fixed".

The prompts are in `sheets-method-test.json` in this folder. It uses the same format as `battery.json`, so the live runner can read it: `scripts/copilot-live-smoke.ps1 -Battery docs\copilot-live-smoke\sheets-method-test.json`. You can also type the prompts by hand.

## What the Copilot should do now

- **It does not choose for the drafter.** When a level has two plans of the asked type and the drafter named neither, it asks which one and names the plans. No sheet exists until the answer.
- **It asks before it creates something that was not asked for.** When the drafter names a level that has no plan of the asked type, it says so and asks if it should create one. It does not create the plan silently and it does not skip it silently.
- **In a prompt for every level,** a level with no plan of the asked type is asked about too: create one, or skip it. It runs the sheet script first, then asks everything the script lists in one question: which plan for a level with two, and create or skip for each level with none. It does not ask about these before the script ran, and it does not ask them in two questions. A level you skip is left out; nothing is made for it.
- **It does not read the sheets back after the script.** The script's `MADE` lines are the result; the next call is the result card.
- **A script that stops to ask is not a failure.** When the script made nothing and the Copilot asks you, the pane shows no yellow "Not every change in this turn was applied" warning.
- **It does not ask about sheet numbers.** The prefix `A-` and the first number `101` are defaults. A number already in use is skipped and reported.
- **It never asks for confirmation of a change.** Changes apply at once. There is no preview card and no Apply button. The result card has an Undo button.

## What the sheet script does

For each level it finds the plan of the asked type (Floor Plan or Ceiling Plan), makes a sheet with the asked title block, gives it the next free number, and places the plan at the centre of the sheet. If any level needs a decision from the drafter, the script makes nothing and reports what to ask.

## Before you start

1. Open the test model. It must have several levels, at least one level with two floor plans, at least one level with no ceiling plan and at least one level with no floor plan. The saved model must have no sheet numbered `A-101`.
2. Write down once, and use for every run:
   - **FP1**: how many levels have exactly one floor plan.
   - The name of the level that has two floor plans, and the names of both plans.
3. The prompts contain names in angle brackets. Replace each one with a real name from the test model, in the JSON file before the runner is used, or in the text when you type by hand. The runner sends the text literally, so an unreplaced `<Title Block>` would be sent as is. Use the same names in every run.
   - `<Title Block>`: a title block type that exists in the model.
   - `<Level A>`, `<Level B>`: two levels that each have exactly one floor plan and exactly one ceiling plan. They must not be the two-plan level.
   - `<N1>`, `<N2>`: level numbers as a drafter says them, such as 1 and 3, for levels named like Level 1 and Level 3. Both levels must have exactly one floor plan.
   - `<Level With Two Plans>` and `<Plan Name>`: a level with two floor plans, and the name of the one to use.
   - `<Level Without Ceiling Plan>`: a level with no ceiling plan.
   - `<Level Without Floor Plan>`: a level with no floor plan.
   - `<Door Type>`: a door type that has doors on `<Level A>` and on at least one other level.
4. Use an add-in build that has the warning fix (add-in commit `1e8067e` or later). An older build shows the yellow warning when the script stops to ask; write it in Notes if you see it.

## How to run

Start the backend from the branch `feat/copilot-runnable-playbooks`. At the top of the results table, write down: the backend commit (short SHA), the add-in version, the Revit version and the model file name.

Run every prompt twice. That is 28 runs, about 3 minutes each with the reopen. For each run:

1. Reopen the model. Do not save the previous changes. Every run must start from the same model.
2. Start a new Copilot chat, so nothing from the previous run carries over.
3. For s08 only: right after reopening, create a sheet numbered `A-101` by hand.
4. Type the prompt as written (with your real names filled in). Do not rephrase it.
5. Start a timer when you press Enter. Stop it when the final result card or the final reply appears.
6. If a question comes back, answer it from the table below.
7. Check the result in Revit against the expected result in the JSON file. Open the sheets and look. Do not take the Copilot's reply as proof.
8. Fill in one row of the results table.

## How to answer a question

Answer the question that was asked, not the prompt you are on. Before you type, write the question's topic in the Questions column. Use these answers:

| The question is about | Answer | Needed? |
|---|---|---|
| Which levels | `semua level` | Needed in s09 only |
| Which plan for `<Level With Two Plans>` | `<Plan Name>` | Needed in s01, s04, s09, in the same question as the next row |
| Which title block | `<Title Block>` | Needed in s06 only |
| `<Level Without Ceiling Plan>` has no ceiling plan, create one? (s10) | First repeat: `tak payah`. Second repeat: `ya, buat` | Needed |
| `<Level Without Floor Plan>` has no floor plan (s11, s12) | `no` in s11, `tak payah` in s12 | Needed |
| The door type does not exist (s13) | `tak payah` | Needed |
| A level with no floor plan, in a prompt for every level (s01, s04, s09): create one or skip? | `skip` for each such level (pick its Skip option, or type `skip`) | Needed, in the same question as the plan. A separate question about it is not needed: write it in Notes |
| Sheet number prefix or first number | `ikut default` | Not needed |
| A sheet number that is already used (s08) | `ikut default` | Not needed |
| "Shall I proceed?" or any request to confirm a change | `ya` | Not needed. Write it in Notes: the Copilot must not ask this |
| Anything else | `ikut default` | Copy the question into Notes |

If one question covers several topics, answer each topic in one message, separated by commas, in the order asked. Example: `<Plan Name>, skip`.

## The fourteen prompts

Floor plan is the default plan type when a prompt does not name one. The exact wording and the full expected result for each prompt are in the `text` and `expect` fields of the JSON file.

| ID | Language | What it tests | Expected |
|---|---|---|---|
| s01 | English | Every level | One question, after the script ran: which plan for the two-plan level, and create or skip for each level with no floor plan. Answer `skip`: then FP1 + 1 sheets from A-101. Levels with no floor plan reported as skipped |
| s02 | English | Two named levels | No question. Two sheets, A-101 and A-102 |
| s03 | English | Ceiling plans | No question. Two sheets holding ceiling plans |
| s04 | English | Given prefix and first number | One question: which plan for the two-plan level, and create or skip for each level with no floor plan. Answer `skip`: then FP1 + 1 sheets from S-201 |
| s05 | Malay | Levels named loosely by number | No question. Two sheets for the two levels the numbers mean |
| s06 | Malay | No title block named | One question about the title block only. Then two sheets |
| s07 | Malay | Two-plan level, plan named by the drafter | No question. One sheet with only the named plan |
| s08 | Malay | Sheet numbers already in use | No question. New sheets take A-102 and A-103. A-101 reported as in use |
| s09 | Malay | No levels said | Asks which levels first. Then one question: which plan for the two-plan level, and create or skip for each level with no floor plan. Answer `skip`: then FP1 + 1 sheets |
| s10 | Malay | Named level has no ceiling plan | One question: create one? `tak payah`: nothing made. `ya, buat`: one ceiling plan and one sheet |
| s11 | English | Named level has no floor plan | One question: create one? `no`: nothing made |
| s12 | Malay | Tag doors in a plan that does not exist | One question. No view and no tag made |
| s13 | English | Door type that does not exist | Says the type does not exist and names real ones. Nothing written |
| s14 | English | A plain parameter write | No question. Only the doors of that type on that level change. Undo works |

s11 to s14 are new. They check that "ask first" also holds outside the sheet script, and that a plain write did not get worse.

## Results table

Above the table, write the backend commit (short SHA), the add-in version, the Revit version and the model file name. Fill one row per run, so two rows per prompt.

| Prompt | Repeat | Right or wrong | If wrong, the cause | Playbook named in the turn | Code starts with `// inputs` | Script report readable | Questions (topic, needed or not) | Wrote before the answer | Made something not asked for | Failed tool calls | Seconds | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| s01 | 1 | | | | | | | | | | | |
| s01 | 2 | | | | | | | | | | | |

How to fill each column:

- **Right or wrong:** Right only if everything in the expected result holds: the right questions before anything is made, the right number of sheets, the right numbers, each plan placed exactly once, the right title block, and nothing else changed. A right result with a question that was not needed is still Right. The question is counted in its own column.
- **If wrong, the cause:** pick one.
  - Bad code: the Copilot's code failed or threw an error.
  - Chose for the drafter: it picked a plan, a level or a title block that the drafter did not name.
  - Made something not asked for: it created a view, a type or a sheet beyond the request without asking first.
  - Wrong understanding: the code ran, but the Copilot did something other than what you asked (for example floor plans instead of ceiling plans).
  - Wrong elements: it acted on the wrong levels, views, sheets or doors.
- **Playbook named in the turn:** the playbook the backend offered for this prompt. It is in the turn export, in the user message, after the word `playbook` inside `<playbooks>`. For s01 to s11 it should be `sheet-set`.
- **Code starts with `// inputs`:** yes, no, or no code ran. Yes means the stored script ran. To see the code, open the turn's activity in the Copilot pane or the turn export.
- **Script report readable:** yes if the output of the script shows lines that start with `MADE`, `SKIP` or `ASK`. No if it shows `System.Collections.Generic.List`.
- **Questions:** for each question, its topic and whether it was needed, from the answer table.
- **Wrote before the answer:** yes if any new element existed in the model before you answered a question.
- **Made something not asked for:** yes if a view, a type or a sheet beyond the request was created.
- **Failed tool calls:** the number of tool results with `ok: false` in the turn export.
- **Seconds:** from Enter to the final result card or final reply.
- **Notes:** for s01, s04 and s09, whether the plan and the levels with no floor plan came in one question or in two, and whether a question came before the script ran. Whether the yellow warning showed. For s13, a question or a plain reply. Any question you answered with `ikut default`. Anything else odd.

## Export after the run

For each turn, save beside the results table:

- every row of `ai.copilot_turn_events` for the turn (turn id, sequence, kind, body, created at), all kinds, unmodified. The `assistant` rows hold the model's messages with its tool calls. The `results` rows hold the tool results exactly as the model received them.
- the turn's row in `ai.copilot_turns`.
- the backend commit SHA.

## What to compare at the end

Compare with the results of 11 November (`sheets-method-results-2026-11-11.md`), prompt by prompt for s01 to s10:

- Which prompts changed from wrong to right, or from right to wrong, and whether both repeats agree.
- How many questions were asked, and how many were not needed.
- Every run where the playbook named in the turn was not `sheet-set` (s01 to s11), where the code did not start with `// inputs`, or where the script report was not readable.
- Every run that wrote before the answer or made something not asked for.

Then list any live result that disagrees with the offline eval for the same case.
