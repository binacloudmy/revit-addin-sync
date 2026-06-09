# Bina Revit Copilot vs BIMLOGIQ — performance benchmark prompts

Run the **same prompt** in both tools, on the **same model**, and record the
metrics. Phrasing is deliberately neutral (not tool-specific) so each tool
interprets it on its own.

## How to run it fairly
- Same Revit model, same active view, same starting state for each tool.
- Run each prompt **2–3 times** and take the median (first run may be cold).
- **Undo** any model changes between runs so both tools start equal.
- For Bina, note whether it answered via **tier/vetted**, **tool-call**, or
  **codegen** (your SV says it's all codegen right now — record that).
- Stop the clock at: (a) first visible response, and (b) action complete.

## Metrics to record per prompt (per tool)
| Field | Meaning |
|---|---|
| Response time | seconds to first reply |
| Complete time | seconds until the action finished |
| Completed? | Y / N |
| Correct? | Y / Partial / N (did it do exactly what was asked?) |
| Re-prompts | # of times you had to rephrase/correct |
| Froze Revit? | Y / N (and ~how long) |
| Notes | hallucinated filters, wrong category, errors, etc. |

---

## Tier 1 — Read / query (fast, low-risk)
1. `count doors by level`
2. `how many windows are in the model?`
3. `what is the total area of all rooms?`
4. `find all walls with no fire rating`
5. `list all the sheets in this project`

## Tier 2 — Navigation & selection
6. `open the Level 1 floor plan`
7. `select all doors`
8. `select all walls on Level 1`
9. `isolate all doors in the active view`

## Tier 3 — Parameters (model changes)
10. `set the Comments parameter to "AUDIT" on all walls`
11. `set Fire Rating to 60 min on all doors`
12. `add a Yes/No instance parameter called "Reviewed" to all rooms`

## Tier 4 — Tagging & annotation
13. `tag all walls in the active view`
14. `tag every door in the active view`
15. `tag all rooms in the active view`

## Tier 5 — Graphics / visualization
16. `color walls by fire rating: red if none, green otherwise`
17. `override all doors to display in blue in this view`

## Tier 6 — Modeling / geometry
18. `create 5 grid lines spaced 5 m apart`
19. `create 3 levels 3 m apart above Level 1`
20. `rotate the selected elements by 45 degrees`

## Tier 7 — Sheets & views
21. `create a new sheet numbered A201 named "Test Sheet"`
22. `duplicate the Level 1 floor plan as a dependent view`
23. `place the Level 1 floor plan on sheet A201`

## Tier 8 — Schedules & export
24. `export the door schedule to Excel`
25. `create a schedule of all doors showing Mark and Level`

## Tier 9 — Cleanup / batch
26. `delete all unused views`
27. `purge unused families`
28. `rename all views, replacing "Level" with "L"`

## Tier 10 — Complex / conditional (stress test — where tools differ most)
29. `select all walls taller than 3 m`
30. `set Fire Rating to 60 min only on doors that are in corridor rooms`
31. `place a smoke detector at the center of each room`
32. `for each room with area under 10 m², create a callout`

## Tier 11 — MEP (BIMLOGIQ markets these; needs an MEP model)
33. `set insulation thickness on ducts based on their width`
34. `find pipes that intersect walls and create openings sized to the pipes`
35. `find the shortest path between the two selected air terminals`

---

## Scoring summary (fill in after the runs)
| Tier | Bina avg time | BIMLOGIQ avg time | Bina success % | BIMLOGIQ success % | Winner |
|---|---|---|---|---|---|
| 1 Read | | | | | |
| 2 Nav/Select | | | | | |
| 3 Parameters | | | | | |
| 4 Tagging | | | | | |
| 5 Graphics | | | | | |
| 6 Modeling | | | | | |
| 7 Sheets | | | | | |
| 8 Schedules | | | | | |
| 9 Cleanup | | | | | |
| 10 Complex | | | | | |
| 11 MEP | | | | | |

## What to watch for (interpreting the comparison)
- **Speed:** Bina's deterministic tiers (when used) should beat codegen and beat
  BIMLOGIQ's 1–2 min/task. If Bina is all-codegen right now, expect it to be
  closer to BIMLOGIQ's latency.
- **Correctness:** watch for unrequested behavior (e.g. tag "skip < 1 m / one per
  type") in both — note any assumptions the tool made that you didn't ask for.
- **Re-prompts:** BIMLOGIQ reviews report needing "very detailed, precise"
  prompts — count how often each tool needs a retry.
- **Freezes:** record Revit "Not Responding" time — Bina's tool-loop/codegen runs
  on the UI thread and can freeze; note duration.
- **Coverage:** mark N/A where a tool simply can't do it (e.g. MEP if your model
  has no MEP, or schedule/parameter creation).
