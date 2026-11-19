# JKR Compliance pane: Clear Sheet redesign

Date: 2026-11-19 · Author: Irfan Hakimi (with Claude) · Status: draft for approval

## 1. Intent

Ashraf (team lead) asked Irfan to redesign the JKR Compliance pane in the Revit add-in to follow the
"Clear Sheet" design: "redesign compliance ikut ni", "checkout from thrashraf/jkr-compliance"
(chat, 2026-11-18). The pane today uses the dark "Direction C" look and carries some behaviour from
the older yellow Binazone prototype. Clear Sheet is a light, plainer design for the same audit flow.

Who uses the pane: architects and BIM staff who audit an open Revit model against JKR Borang Model
Audit BIM010 before they submit it to JKR. They read the result, fix or ignore issues, and export
the Borang.

## 2. Sources of truth

| What | Where |
|---|---|
| Clear Sheet design (17 screens) | `C:\Bina\model-audit\JKR-clean-redesign.html` (downloaded by Ashraf from artifact `ERQFCvsHc9uyZmK2jwgKrQ`; the link belongs to another account). It is a nested bundle: open it in a browser to view. |
| Code base | `binacloudmy/revit-addin-sync`, branch `thrashraf/jkr-compliance` (tip `02af0ebf`, 2026-11-18) |
| Ashraf's checklist of behaviour Clear Sheet does not draw | Section 5 below |
| Old prototype (NOT the target) | `C:\Bina\model-audit\design-artifact\project\` (yellow Binazone prototype, 10 Nov) |

The rule: **Clear Sheet decides everything it draws, looks and behaviour.** For behaviour that
Clear Sheet does not draw, Ashraf's checklist decides: ticked = remove, unticked = keep and restyle
to Clear Sheet.

## 3. Goals, non-goals, anti-goals

Goals
- Every pane screen and pop-up window matches Clear Sheet: layout, copy, colours, type, states.
- Ticked checklist items are removed from the product, including their dead code, strings and tests.
- Unticked checklist items still work and look like they belong in Clear Sheet.
- The audit engine, the 61 spec checks, the Borang PDF and the audit file keep working as today.
- BM / EN both work on every screen (Clear Sheet draws both; see the 320 px "keputusan" screen).

Non-goals
- No change to the spec checks' logic (`Services/JkrSpec/*`) except the C1.6.1 message text.
- No billing, credits, consent or pricing work (billing is undecided by Ashraf).
- No change to the Borang BIM010 PDF layout (it is the HOD's template, "unchanged").
- No merge of other `feat/jkr-*` branches (for example `feat/jkr-ai-autofix`).

Anti-goals (a failure even if it builds)
- A pane that looks like Clear Sheet but drops a kept behaviour (for example, no way back from Ignore).
- A fixed item counted as passed before a re-scan (the Borang must stay honest).
- Two looks side by side (dark leftovers in any window).
- Pushing, merging or opening a PR without Irfan's explicit go-ahead.

## 4. Approaches

| | Approach | Trade-off | Verdict |
|---|---|---|---|
| A | **Reskin in place.** Replace the design tokens with Clear Sheet's, then rework each existing view and window, screen by screen. Keep the screen models (`UI/Jkr/Panel/Core/*`) and their tests; change them only where Clear Sheet or the checklist changes behaviour. | Large XAML diff, but logic, tests and Ashraf's step structure stay. One look to maintain. | **Chosen** (Claude's call; Irfan asked for the best approach) |
| B | New light views next to the dark ones, switched in `JkrComplianceDashboardHost`. | Easy rollback, but doubles the UI code and the dark views rot. Nobody asked to keep dark. | Rejected |
| C | Runtime dark/light theme switch. | Keeps both looks alive; adds a theme layer nobody asked for. | Rejected |

## 5. Ashraf's checklist (behaviour Clear Sheet does not draw)

| # | Item | Decision | What it means in code (base branch) |
|---|---|---|---|
| 1 | Paid "Generate room schedule" + consent checkbox | **Remove** | `FixKind.Generate`, `PanelCore.Consent`/`ToggleConsent`/`ConfirmDisabled`, consent panel in `JkrConfirmWindow`, Generate block in `DetailWindow`, `CanGen`/`Gen*` strings, `PanelFixture` Generate item, related tests. C1.6.1 still **fails** when a model has no room schedule; its message must drop "'Generate room schedule' (paid) is offered" (`SpecChecks.BC.cs:763-765`). It becomes a normal open item with how-to-fix steps. |
| 2 | Undo toast after Ignore (6 s) | **Remove** | `PanelCore.Undo`/`UndoIds`/`UndoToastTime`, `ToastUndo` in `JkrPaneShell` and `DetailWindow`, `UndoRequested` wiring in the host, `UndoLastDecision` in the old panel. Ignore still shows a plain toast. The way back from Ignore is Reopen (item 3). |
| 3 | "Mark as Manual" and "Reopen" | **Keep, restyle** | Today they live only in the dead `IssueFocusWindow`. Add them to the Clear Sheet detail: **Reopen** on Ignored and Resolved issues; **Mark as Manual** on open issues where `JkrTierMap.CanMarkManual` allows. Exact placement: section 7. |
| 4 | "How to fix manually" steps, FIX FAILED box | **Keep, restyle** | Already in `DetailWindow` (`Steps`, `FixError`). Restyle as Clear Sheet blocks. |
| 5 | Spec quote (expand) and "Open PDF" link | **Remove** | The live detail already shows only "Source: …". The quote and "Open PDF" exist only in the dead `IssueFocusWindow` (deleted, section 8). Clicking the Source line to open the PDF stays (Claude's call: it is the same single line Clear Sheet draws). |
| 6 | Fixed card with the applied diff, and "Fixed · re-scan pending" | **Remove** | Remove the "Done" block and the `fixedRow` state label from the detail. A fixed item moves to the **Resolved** tab (Clear Sheet draws Open / Ignored / Resolved / Manual). The rule "fixed is not passed until a re-scan" stays in the data and the Borang (`BorangDoc` remark "fixed, not yet re-scanned"); only the card and label go. |
| 7 | Old Export Report dialog | **Keep, restyle** | `ExportWindow` stays reachable from "Export issues" in the Borang preview, restyled. Clear Sheet's preview footer offers Excel (.xlsx) and CSV (.csv) with issue counts; the dialog keeps any options not ticked away. |
| 8 | Export options "Include request/response JSON" and "Include spec references" | **Remove** | `CbJson`, `CbSpec` in `ExportWindow` and whatever they switch in the export code. |
| 9 | Category pills and "Clear section filter" pill | Keep, restyle | |
| 10 | Five section tiles as a filter strip | Keep, restyle | Clear Sheet draws a list of sections with tick strips; the tiles' filter behaviour moves onto those rows (a row click filters / opens the section). |
| 11 | Progress ring and the big hero tile ("n need a person") | Keep, restyle | Clear Sheet's result head: "Does not comply · 22 spec checks fail · 18% compliant, 19 of 104 results pass · 13 checks wait for semak manual". |
| 12 | State tag filter row with counts on the checklist | Keep, restyle | Clear Sheet draws it inside a section (All / Fails / Pass / Semak manual / Listed). |
| 13 | Inventory table per check | Keep, restyle | |
| 14 | Run progress bar and key legend | Keep, restyle | Clear Sheet's running and "stopped in C · 41 of 61 checks done" screens. |
| 15 | Start: LOD choice, project/file rows | Keep, restyle | Clear Sheet uses LOD buttons ("Level of development · Required"), discipline preset from the file name, and a "Borang sections · 61 checks" list. |
| 16 | Dark theme (Direction C) | **Replaced** by Clear Sheet's light theme | Rule in section 2: Clear Sheet decides the look. |

## 6. Clear Sheet look (tokens)

Taken from the design file. Exact hex values must be re-read from the file when tokens are written.

- Background white `#FFFFFF`; page wash `#FAF9F5`/`#F5F4EF`; surfaces `#F8F9FA`, `#F1F3F6`, `#EEF0F3`.
- Ink `#15181D`; secondary text `#4B5361`, `#646B78`; faint `#8A919C`; lines `#E5E7EB`, `#D0D4DA`, `#C9CED6`.
- States: fail `#C2331F`, pass `#3DA570`, warning / medium `#B26B00`, semak manual `#5B3FD0` (outlined), brand amber `#F2A91F` (only the top step of the logo; "Ink only, like every action in Clear Sheet").
- Tick strip per section: one small square per check: fail, pass, partly (half green), manual (purple outline), listed (grey), not applicable, not checked (dashed).
- Type: Public Sans (UI) and IBM Plex Mono (values, file names, element ids). The fonts must be bundled with the add-in or fall back cleanly (Segoe UI / Consolas) when not installed.
- Pane width 380 px; Clear Sheet also draws a 320 px BM layout, so nothing may clip at 320 px.
- New logo: "a tick that climbs in steps", 20 px in the pane header, 32 px on the ribbon. Replaces `JkrCompliance16.svg` / `JkrCompliance32.svg`.
- AI-fix mark: option 1 "Spark bolt" (marked Recommended) to tell an AI fixer proposal from a ready-made fix.

## 7. Screen map (Clear Sheet → code)

| Clear Sheet screen | Code to change |
|---|---|
| Logo / mark | Ribbon icons, pane header (`JkrPaneShell.xaml`) |
| Set up the audit | `StartView.xaml`, `StartScreenModel` |
| Audit running; run failed ("stopped in section C") | `RunView.xaml`, `RunScreenModel` |
| Result (sections / **Aspects** toggle), no failures, 320 px BM | `ResultsView.xaml` summary + checklist, `ResultsScreenModel`, `SpecChecklist` |
| Section D (checklist inside a section) | `ResultsView.xaml` checklist |
| Issues (Open / Ignored / Resolved / Manual tabs, severity legend, Ignore all / Fix all n) | `ResultsView.xaml` issue list |
| Issue with a fix; issue with an AI fixer proposal; semak manual issue | `DetailWindow.xaml`, `DetailScreenModel` |
| Confirm changes (ready-made fixes + "Proposed by the AI fixer" group, "5 ready, 2 on the way", 500+ elements warning) | `JkrConfirmWindow`, `JkrConfirmModel`, `PanelCore` confirm |
| Fix outcome (grouped reasons, Locate, "Copy list for BIM manager") | `FixResultWindow`, `FixResultModel` |
| Borang BIM010 preview (Excel / CSV / Save PDF) | `BorangPreviewWindow`; `ExportWindow` (item 7) |
| Edge states: no model, login required, run failed, LOD changed + backend unavailable | `JkrPaneShell.xaml`, `ResultsView.xaml` banners |

Detail actions (item 3 placement, Claude's call): the action bar keeps Clear Sheet's order
(Locate in 3D · Ignore (A) · Apply fix (F)). On an Ignored or Resolved issue, the Ignore/Apply pair
is replaced by **Reopen**. **Mark as Manual** is a secondary text button beside Ignore on open issues
that allow it. Semak manual issues keep Clear Sheet's "Mark as reviewed".

New behaviour Clear Sheet draws that the branch lacks:
1. **Aspects** view on the result: the same 61 checks regrouped as Naming (N), Parameters and
   information (P), Quality and integrity (Q), Design geometry (G), each with %, tick strip and fail
   count. Needs an aspect for every check (see open question Q1).
2. **AI fixer proposal** as its own kind: the spark-bolt mark on the row and in the detail,
   "Proposed by the AI fixer" with a reason line, "Apply AI fix (F)", and its own group in the confirm
   window. The branch already asks the backend AI fixer during Fix all (`AiFix`); this makes the
   proposal visible as a separate kind.
3. **Proposals arriving during confirm**: "Still working on 2 more. They join this list when ready."
   and the button "Write 5 changes" counting only ready ones (see open question Q2).
4. "Other JKR rules · not in the Borang spec · n fail" row under the sections (exists in
   `SpecChecklist`; restyle and place as drawn).

## 8. Dead code to delete

- `UI/Jkr/Modals/IssueFocusWindow.*` and the `OpenFocusWindow` path in `JkrComplianceDashboardPanel`
  (unreachable: the pane opens `DetailWindow`). Port nothing from it except Mark as Manual / Reopen.
- Old dashboard visuals inside `JkrComplianceDashboardPanel.xaml` that are hidden (`EngineHost` is
  collapsed). Delete only what is provably unreachable; the panel stays as the engine.
- Old controls only used by deleted code (`CategoryPill`, `PillStat`, `ProgressRing`, `IssueRow`, …),
  each checked with a search before deletion.

## 9. Constraints

- WPF / XAML, C#, Revit add-in. No Storyboards (they crash Revit; see `RunView.xaml` comment).
- Keep keyboard keys: J/K move, Enter opens, F fix, A ignore, / search, Esc closes.
- Work on a new local branch off `origin/thrashraf/jkr-compliance`, in its own git worktree, so
  Irfan's `feat/jkr-binazone-panel` (2 unpushed commits) is untouched. No push, merge or PR without
  Irfan's go-ahead.
- Tests: the existing `Tests/*` for the pane models must pass; update tests whose expected strings
  or behaviour change; delete tests for removed features. `UiHarness/JkrPaneShots.cs` produces
  screenshots to compare against Clear Sheet.

## 10. Left to the builder

- Order of work and commit size (suggested: tokens and shell first, then removals, then one screen
  per step, new behaviour last).
- Exact XAML structure, control reuse, and how tokens are named.
- How to bundle the fonts.
- Screenshot comparison method against the design file.

## 10a. Calls made after the builder check (Claude's calls, correct if wrong)

- Edge states: the target is **"edge states, refined"** (the later version): "Waiting for a model",
  "41 of 61 checks done" with the A–E strip, split banners, Reset / Export Borang on the degraded result.
- Reopen: it only changes status and never touches the model. It is offered on **Ignored** and
  **marked-manual** items. A fixed (Resolved) item is undone in Revit with Ctrl+Z and a re-scan,
  so the list never disagrees with the model.
- Aspects: built against a draft N / P / Q / G mapping in one table, and tests lock the mechanism,
  not the 9 / 14 / 26 / 12 counts. Ashraf confirms the mapping before any merge (Q1).
- Export (item 7): the Borang preview keeps Clear Sheet's footer. "Export issues" opens the
  restyled export dialog with the format (Excel / CSV) and the remaining include-open /
  include-resolved options; Save PDF saves directly.

- Mark as Manual (decided 2026-11-19, option b): a marked item **stays in the Open tab and still
  counts as a fail**. It carries a "Manual" tag ("you'll fix it in Revit"), is left out of Fix all and
  AI fixer requests, and the Borang prints it as not compliant with the remark "handled manually".
  A re-scan after the user fixes it in Revit passes it as normal. Reopen removes the tag. Rejected:
  moving it to the Manual tab (lets users lift the % by marking real failures; mixes it with
  semak manual) and filing it under Ignored (hides work the user still owns).

## 11. Open questions

- Q1 (Ashraf): which aspect (N / P / Q / G) does each of the 61 checks belong to? The design shows
  counts 9 / 14 / 26 / 12. Proposal: Irfan drafts the mapping from each check's subject, Ashraf
  confirms.
- Q2: can the backend AI fixer return proposals one by one, so the confirm window can show "2 on the
  way"? If not, the first release shows the confirm only when all proposals are back.
