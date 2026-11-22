# Tags live test: ten prompts, the baseline

## What this test is for

Making sheets was hardened first: a stored script does the work and decides when the drafter must be asked, and the backend sends that question. This test measures the next job, tagging, **before** it is hardened. The Copilot still writes its own tagging code from the `tag-all-in-view` playbook text and asks its own questions. The numbers from this run are the "before". The same ten prompts are run again after the tag job is hardened.

This run does not give a score. One run per prompt cannot prove a rate.

The prompts are in `tags-method-test.json` in this folder, in the same format as `battery.json`.

## What the Copilot should do

- **It tags what was asked, in the view that was asked.** "This view" is the active view. A plan named in the prompt is that plan, also when another view is active. It never tags a different view instead.
- **It does not choose for the drafter.** No category said: it asks which. Several tag types for the category and none named: it asks which.
- **It asks before it creates something that was not asked for.** A plan that does not exist is not created silently, and the job is not moved to another plan silently.
- **It does not ask what has a default.** Leaders are off unless asked for. With one tag type loaded for the category there is nothing to ask.
- **It does not tag twice.** A door that already has a tag in the view gets no second one.
- **It never asks for confirmation of a change.** Changes apply at once and can be undone.

## Before you start

1. Open the test model. Write down once, and use for every run:
   - `<Plan A>`: a floor plan that shows at least five doors and some windows, with no door tags and no window tags in it.
   - **D**: the number of doors visible in `<Plan A>`. **W**: the number of windows visible in it. Count them in Revit (select one door, Select All Instances, Visible in View, read the count; sum over the door types), not through the Copilot.
   - `<Plan B>`: another floor plan that shows doors and has no door tags. Its door count is **DB**.
   - `<3D View>`: any 3D view.
   - `<Level Without Floor Plan>`: a level with no floor plan.
   - The door tag types loaded in the model. If there are two or more, `<Door Tag Type B>` is one that is not the default. If there is only one, t08 is not applicable.
2. Replace the placeholders in the JSON file, or in the text when you type by hand. Use the same names in every run.

## How to run

For each of the ten prompts, in order:

1. Reopen the model. Do not save the previous changes.
2. Make the view named in the step's expected result the active view (`<Plan A>` unless it says otherwise).
3. Start a new Copilot chat.
4. Type the prompt as written. Start a timer when you press Enter; stop it at the final result card or reply.
5. If a question comes back, answer it from the step's expected result (it names the answer). For a question the step does not expect, answer with the default it offers, or `default`, and mark it not needed.
6. Check the result in Revit: count the tags in the view (select one tag, Select All Instances, Visible in View), look for doubled tags, and look at one other view to see it was not touched.
7. Fill in one row of the table.

t07 is two messages in one chat; the row is about the second.

## The ten prompts

| ID | What it tests | Expected |
|---|---|---|
| t01 | All doors in the active plan (English) | D door tags, one per door. No question with one tag type |
| t02 | The same in Malay | As t01. Any question is in English |
| t03 | Windows | W window tags. No door tagged |
| t04 | The active view is a 3D view | Says the view cannot be tagged and asks which view. Tags nothing before the answer |
| t05 | A plan that does not exist | One question: create or skip. Nothing made, nothing tagged elsewhere |
| t06 | A named plan that is not the active view | DB tags in `<Plan B>`, none in `<Plan A>` |
| t07 | Everything already tagged | No new tag. Says they are already tagged |
| t08 | A named tag type | D tags of that type. Not applicable with one tag type |
| t09 | A tag type that does not exist | Says so and names the real types. Tags nothing |
| t10 | No category said | One question: which category. Then D door tags only |

## Results table

Above the table, write the backend commit (short SHA), the add-in version, the Revit version, the PC and the model file name.

| Prompt | Right or wrong | If wrong, the cause | Playbook named in the turn | Own code or stored script | Tags expected / made | Wrong view tagged | Doubled tags | Questions (topic, needed or not) | Wrote before the answer | Failed tool calls | Model steps | Seconds | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| t01 | | | | | | | | | | | | | |

How to fill each column:

- **Right or wrong:** Right only if everything in the expected result holds. A right result with a question that was not needed is still Right; the question is counted in its own column.
- **If wrong, the cause:** pick one. Bad code (the code failed or threw). Chose for the drafter (picked a category, a tag type or a view the drafter did not name). Wrong view (tagged a view other than the one asked for). Made something not asked for (a view, a tag type). Wrong count (missed elements or tagged twice). Wrong understanding (did something else).
- **Playbook named in the turn:** from the turn export, the name after `playbook` in `<playbooks>`. It should be `tag-all-in-view`.
- **Own code or stored script:** "stored script" if the code starts with `// inputs`; otherwise "own code". Before the hardening it is expected to be own code.
- **Tags expected / made:** the expected count (D, W or DB) and the count you found in Revit.
- **Questions:** each question's topic and whether the step expected it.
- **Wrote before the answer:** yes if any tag or view existed before you answered a question.
- **Failed tool calls, model steps, seconds:** from the turn export, as in the sheets test.
- **Notes:** the failed calls by tool, any text the Copilot wrote before a tool call, anything odd in the pane.

## Export after the run

As in the sheets test: for each turn every row of `ai.copilot_turn_events`, the turn's row in `ai.copilot_turns`, and the backend commit, saved beside the results.

## What to report at the end

How many of the ten were right; for each wrong one the cause; how many questions were asked and how many were not needed; how many runs tagged the wrong view, doubled a tag, or wrote before an answer; failed tool calls by tool; and the average model steps and seconds. These are the numbers the hardened version is compared with.
