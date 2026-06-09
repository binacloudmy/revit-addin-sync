# Prompt test report — branch `feat/copilot-compile-loop`

Date: 2026-06-05. Method: **static code-trace** (the prompts were *not* executed —
this is a `net8.0-windows` Revit add-in; no Windows/Revit/backend in the analysis
environment). For each prompt below I followed the actual routing code on this
branch and report the path it takes + whether a supporting capability exists.
Live pass/fail must be confirmed in Revit with the backend running.

## 1. Branch reality (important)

This branch does **NOT** contain the work from `fix/vetted-tools-live-options`:
no local `MatchVetted` fast-path, no `tag` vetted catalog tool, no
`vetted_tool` dispatch contract, no live-options dropdowns. So the routing here
is different.

What it HAS instead — a more capable architecture:
- **Tool-calling loop (default ON):** `RevitChatRouter` sends every chat prompt
  to `ToolLoopRunner` (`/tool/generate` ↔ `/tool/resume`). The backend agent
  calls tools from the add-in's **MCP `ToolRegistry`**, which the add-in runs in
  real Revit, then resumes. Falls back to **codegen** when no tool fits.
  (`BINA_VIBE_TOOL_HTTP=0` forces codegen-only.)
- **Compile-gate self-heal:** generated C# is Roslyn-checked off-thread before it
  touches Revit; failures regenerate from the compiler diagnostic (≤2 retries).
- **Catalog:** same 5 vetted (open-view, rename, set-param, select, export-sched)
  + 9 Tier-2 AI. Vetted tools are reachable from the Library form, NOT from the
  tool-loop (the registry has no open_view/select_elements tool — see gaps).

### MCP ToolRegistry on this branch (what the agent can run deterministically)
- **Inspect (read):** list_levels, list_wall_types, list_family_types,
  list_view_templates, list_worksets, get_element_parameters,
  find_elements_by_filter, get_current_selection, get_active_view,
  get_current_view_elements, get_project_info, list_views, list_sheets,
  list_schedules, list_grids, analyze_model_statistics,
  find_elements_by_parameter, get_material_quantities, get_model_warnings,
  list_view_filters
- **Mutate (write):** set_parameter, set_parameter_bulk, change_type,
  swap_element_type, delete_elements, duplicate_view, apply_view_template,
  create_view_filter, apply_view_filter, place_door, place_window,
  place_family_instance, create_wall, create_room, create_level, create_grid,
  create_floor, create_ceiling, color_elements, hide_isolate_elements,
  move_elements, rotate_elements, copy_elements, mirror_elements, create_sheet,
  place_view_on_sheet, tag_elements, place_text_note, renumber_elements,
  group_elements, pin_elements, join_geometry, export_views, execute_revit_batch

## 2. Routing for a chat prompt (traced)
1. `ChatSend` → `QueryInterpreter.Interpret`: a bare vague noun with **no verb**
   ("doors", "walls", "rooms"…) → **clarify card**. Anything with a verb proceeds.
2. `ResolveProposalAsync` → `RouteAsync` → **ToolLoop** (default): agent calls
   MCP tools (run in Revit) and/or returns codegen. No local deterministic
   fast-path and no confirm-card-before-mutation on this branch — the agent runs
   mutate tools directly.
3. If the agent returns code instead → executor compile-gate + transaction wrap.

Legend: **TOOL** = a registry tool exists (agent can run it deterministically in
Revit) · **CODEGEN** = no tool, falls to AI C# (compile-gated, less predictable) ·
**CLARIFY** = clarify card · **GAP** = capability/tool missing.

## 3. Results by prompt

### Views & Sheets
| Prompt | Predicted route | Note |
|---|---|---|
| `open view aras 02` | **CODEGEN** | No open/activate-view tool in the registry → codegen (RequestViewChange). GAP vs other branch's vetted open-view. |
| `duplicate Level 1 plan as a dependent view` | **TOOL** duplicate_view | Confirm dependent option support. |
| `create 5 sheets named A101–A105 with my titleblock` | **TOOL** create_sheet | Loop of create_sheet. |
| `place 4 views on sheet A101 in a 2×2 grid` | **TOOL** place_view_on_sheet | Grid layout is agent logic. |
| `rename Level to L in views` | **CODEGEN / TOOL set_parameter** | No dedicated rename tool; agent may set the Name param or codegen. |
| `append the level elevation to each floor plan view name` | **CODEGEN** | No tool. |
| `create interior elevations on 4 sides of 'Unit' rooms` | **CODEGEN** | No elevation tool. |

### Data & Parameters
| Prompt | Predicted route | Note |
|---|---|---|
| `set fire rating to FRR-60 on walls` | **TOOL** set_parameter_bulk | Strong fit. |
| `set FRR-60 on doors in corridor rooms` | **TOOL** find_elements_by_filter → set_parameter_bulk | Agent chains. |
| `add an instance text parameter 'Contractor' to all doors` | **CODEGEN** | No add-parameter tool. |
| `export the door schedule to excel` | **TOOL export_views / CODEGEN** | export_views may cover; else codegen. |
| `create a door schedule with Mark, Level, Width` | **CODEGEN** | No create-schedule tool. |

### Quantification / Query
| Prompt | Predicted route | Note |
|---|---|---|
| `count doors by level` | **TOOL** find_elements_by_filter / analyze_model_statistics | Read-only. |
| `what is the total length of 150mm pipes?` | **TOOL get_material_quantities / CODEGEN** | Partial. |
| `total floor area on level 1` | **TOOL analyze_model_statistics / CODEGEN** | Partial. |
| `find walls missing a fire rating` | **TOOL** find_elements_by_filter | Strong. |

### Selection & Graphics
| Prompt | Predicted route | Note |
|---|---|---|
| `select all doors` | **CODEGEN** | No set-selection tool in registry → codegen. GAP vs vetted select. |
| `select walls taller than 3m with fire rating 0` | **CODEGEN** | find_elements_by_filter can find, but no select-into-UI tool. |
| `color walls by fire rating (0 gray,1 yellow,≥3 red)` | **TOOL** color_elements (+ find) | Strong fit. |
| `isolate all doors in this view` | **TOOL** hide_isolate_elements | Strong. |
| `purge unused families` | **CODEGEN** | No purge tool; delete_elements partial. |
| `delete all unused views` | **TOOL delete_elements / CODEGEN** | Partial. |

### Annotation / Tagging
| Prompt | Predicted route | Note |
|---|---|---|
| `tag all walls in this view` | **TOOL** tag_elements | Registry has tag_elements (different impl from the vetted tag tool on the other branch; behavior is the agent/tool's, no confirm card here). |
| `tag every door instance` | **TOOL** tag_elements | Mode handling is tool/agent-side. |
| `create a callout for each room with area < 600 sqf` | **CODEGEN** | No callout tool. |
| `uniformly space the selected dimensions at 1000mm` | **CODEGEN** | No dimension tool. |

### MEP
| Prompt | Predicted route | Note |
|---|---|---|
| `set insulation thickness on ducts by width` | **CODEGEN** | No MEP insulation tool. |
| `find pipe-wall intersections and create openings` | **CODEGEN** | No penetration tool. |
| `shortest path between the two selected air terminals` | **CODEGEN** | No routing tool. |
| `resize ducts based on flow` | **CODEGEN** | No tool. |
| `find clashes between pipes and walls` | **CODEGEN** | No clash tool on this branch. |

### Modeling / Geometry
| Prompt | Predicted route | Note |
|---|---|---|
| `create 4 horizontal + 6 vertical grids at 4m, name 1–4 / A–F` | **TOOL** create_grid | Strong fit. |
| `create 3 levels at 3.5m spacing` | **TOOL** create_level | Strong. |
| `rotate the selected elements 90° around their center` | **TOOL** rotate_elements | Strong. |
| `place a smoke detector at the center of each room` | **TOOL** place_family_instance (+ find rooms) | Strong. |
| `create a wall / floor / ceiling / room` | **TOOL** create_wall/floor/ceiling/room | Strong. |

### Collaboration / Worksets / Links
| Prompt | Predicted route | Note |
|---|---|---|
| `create worksets from this Excel file` | **CODEGEN** | No workset-create or Excel tool. |
| `move all furniture to the 'FF&E' workset` | **TOOL set_parameter / CODEGEN** | Workset param set, partial. |
| `copy the grids from the linked structural model` | **CODEGEN** | copy_elements is in-model; link source not covered. |
| `place a tag in each room of the linked architectural model` | **CODEGEN** | Linked-room tagging not covered. |

### Excel I/O
| Prompt | Predicted route | Note |
|---|---|---|
| `import door types from doors.xlsx` | **CODEGEN** | No Excel tool (executor has ClosedXML for codegen). |
| `export Room/Window data to Excel on desktop` | **CODEGEN** | No Excel-export tool. |

## 4. Bugs / gaps found by inspection
1. **No open/activate-view tool** in the registry → `open view X` from chat goes
   to codegen (works, but slower/less reliable than the vetted open-view on the
   other branch, and no disambiguation).
2. **No set-selection tool** → `select all X` from chat goes to codegen. The
   vetted `select` tool only runs from the Library form here.
3. **No confirm-before-mutate** on this branch — the tool-loop runs mutate tools
   (set_parameter_bulk, delete_elements, color_elements, tag_elements…) directly.
   Destructive prompts execute without the confirm/preview we added elsewhere.
4. **Two diverging `tag_elements`** now exist: the MCP mutator here vs the vetted
   synth on `fix/vetted-tools-live-options`. Decide which is canonical before
   merging the branches.
5. **Opinionated codegen recipes still apply** on the codegen path (e.g. the
   tag "skip <1 m / one per type" recipe) — only fixed on the other branch.

## 5. Verdict
Capability **coverage on this branch is high** — the MCP mutator set natively
covers a large share of the BIMLOGIQ list (params, color, hide/isolate, tag,
grids/levels/walls/floors/ceilings/rooms, move/rotate/copy/mirror, sheets,
duplicate view, view filters, renumber, place door/window/family) plus rich
read/query tools. The weak spots are MEP specifics, schedules/parameters
creation, Excel I/O, callouts/dimensions, worksets-from-Excel, and linked-model
ops — all of which fall to codegen.

BUT: every result here is **agent/LLM-decided** (which tool, what args) — there
is no deterministic local routing or mutation-confirm on this branch. So
real-world reliability depends on the backend tool agent + the live model, and
must be measured by actually running each prompt in Revit.

## 6. Recommendation
Before judging parity, decide the **branch strategy**: this branch (tool-loop
breadth) and `fix/vetted-tools-live-options` (deterministic vetted fast-path +
confirm + live options + tag tool) are two different bets that should be
**merged** — tool-loop breadth for the long tail, vetted/deterministic + confirm
for the common, destructive, and offline cases.
