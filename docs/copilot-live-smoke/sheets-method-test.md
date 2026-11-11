# Sheets live test: ten prompts, before and after

## What this test is for

The Copilot is getting a new way to do a known job. Today it writes Revit code for each request. After the change, for creating sheets, it picks a tested script and fills in its inputs. We want to know if the new way gives more correct results. The only way to see that is to run the same ten prompts in live Revit twice, once on the old backend and once on the new one, and compare.

The ten prompts are in `sheets-method-test.json` in this folder. It uses the same format as `battery.json`, so the live runner can read it: `scripts/copilot-live-smoke.ps1 -Battery docs\copilot-live-smoke\sheets-method-test.json`. You can also just type the prompts by hand.

## What the sheet script does

For each level it finds the plan of the asked type (Floor Plan or Ceiling Plan), makes a sheet with the asked title block, gives it a number from a prefix and a first number (default `A-` and `101`, numbers already in use are skipped), and places the plan at the centre of the sheet. A level with no plan of that type, or with several plans and no choice given, is skipped and reported.

Changes apply at once. There is no preview card and no Apply button. The result card has an Undo button.

## Before you start

1. Open the test model. Pick one with several levels, at least one level with two floor plans, and at least one level with no ceiling plan.
2. The prompts contain names in angle brackets. Replace each one with a real name from the test model. Use the same names in both runs.
   - `<Title Block>`: a title block type that exists in the model.
   - `<Level A>`, `<Level B>`: two levels that each have a floor plan and a ceiling plan.
   - `<Level With Two Plans>` and `<Plan Name>`: a level with two floor plans, and the name of the one to use.
   - `<Level Without Ceiling Plan>`: a level with no ceiling plan.
3. For prompt s08, make sure a sheet numbered `A-101` already exists in the model.
4. Use the same add-in build for both runs. Only the backend changes.

## How to run

**Run 1, before:** start the backend from the branch `origin/integration/ai-277-all`.

**Run 2, after:** start the backend from the branch `feat/copilot-runnable-playbooks`.

For each run, for each of the ten prompts, in order:

1. Reopen the model. Do not save the previous changes. Every prompt must start from the same model.
2. Type the prompt as written (with your real names filled in). Do not rephrase it.
3. Start a timer when you press Enter. Stop it when the result card appears.
4. Check the result in Revit against the "Expected result" column below.
5. Fill in one row of the table for that prompt and run.

Some prompts (s06 and s09) are expected to get one question back. Answer it in the shortest natural way (for example `semua level`, or the title block name) and count those words too. Stop the timer when the final result card appears.

## The ten prompts

| ID | Language | What it tests | Expected result in Revit |
|---|---|---|---|
| s01 | English | Every level | One sheet per level, A-101 onwards, each floor plan placed once, nothing else changed |
| s02 | English | Two named levels | Two sheets, A-101 and A-102, one per named level, nothing else changed |
| s03 | English | Ceiling plans instead of floor plans | Two sheets holding ceiling plans, not floor plans |
| s04 | English | Given prefix and first number | Sheets numbered S-201, S-202 and so on, none starting with A- |
| s05 | Malay | Named title block | Every new sheet uses the named title block |
| s06 | Malay | No title block named | One question back about the title block, then two sheets after you answer |
| s07 | Malay | Level with two plans of the type | One sheet with only the named plan, the other plan not placed |
| s08 | Malay | Sheet numbers already in use | New sheets take the next free numbers, no duplicate of A-101 |
| s09 | Malay | No levels said, many levels in the model | One question back about which levels, then one sheet per level |
| s10 | Malay | Plan type the model does not have | No sheet made, the level reported as skipped, nothing changed |

The exact wording and the full expected result for each prompt are in the `text` and `expect` fields of the JSON file.

## Results table

Make two copies of this table, one headed "Before" and one headed "After". Fill one row per prompt.

| Prompt | Right or wrong | If wrong, the cause | Words typed | Seconds from Enter to result card | Fixes needed afterwards |
|---|---|---|---|---|---|
| s01 | | | | | |
| s02 | | | | | |
| s03 | | | | | |
| s04 | | | | | |
| s05 | | | | | |
| s06 | | | | | |
| s07 | | | | | |
| s08 | | | | | |
| s09 | | | | | |
| s10 | | | | | |

How to fill each column:

- **Right or wrong:** Right only if everything in the expected result holds: the right number of sheets, the right numbers, each plan placed exactly once, the right title block, and nothing else changed. Anything less is Wrong.
- **If wrong, the cause:** pick one.
  - Bad code: the Copilot's code failed or threw an error.
  - Wrong understanding: the code ran, but the Copilot did something other than what you asked (for example floor plans instead of ceiling plans, or the wrong numbers).
  - Wrong elements: it acted on the wrong levels, views or sheets.
- **Words typed:** all the words you typed for that prompt, including any answer to a question.
- **Seconds from Enter to result card:** the time to the final result card.
- **Fixes needed afterwards:** how many manual changes in Revit you needed to make the model correct (delete a sheet, move a view, renumber). Write 0 if none. You can use Undo on the result card to reset, but count the fixes you would have needed.

## How to tell the new path was taken

Open the code the add-in ran for that prompt (in the Copilot's details or the add-in log). If the new path was used, the code starts with the line:

```
// inputs
```

If the code does not start with that line, the Copilot wrote its own code and the new path was not taken. In the "After" run, note for each prompt whether `// inputs` was there. A prompt that answered correctly without it does not count as a result for the new method.

## What to compare at the end

Compare the two tables: how many prompts were right before and after, the causes of the wrong ones, the words typed, the seconds, and the fixes needed. Write down any prompt where the "After" run did not start with `// inputs`.
