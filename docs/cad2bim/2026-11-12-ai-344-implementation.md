# AI-344 CAD to BIM: a better flow, as built

Date: 2026-11-12. Base: `muhdnurfakhri/ai-344-cad2bim-cad-to-bim-design-a-better-flow` at `f3d38b04` (origin/develop `d2bf39d7` + design docs).
Design: `2026-11-12-cad-to-bim-flow-design.md`. Mockups: `2026-11-12-cad-to-bim-step1-layers.png` … `step4-review.png`.

## What was built

| # | Item | Where |
|---|---|---|
| 1 | Step bar **Layers → Preview → Build → Review** in the right column. Clickable, Back/Next, done steps ticked (green ✓). Thickness, height, storeys and target moved to Build. Viewport unchanged. Moving between steps never re-detects, so erase, brush and pick work stays. | `UI/CadToBim/CadToBimPanel.xaml`, `StepNavigator` in `UI/CadToBim/CadToBimFlow.cs`, step state in `CadToBimViewModel` |
| 2 | Layer roles **Wall, Door, Window, Other, Skip** in a dropdown (themed `CadToBim.Combo`). One change = one re-detect. Reason line under each row ("name has \"wall\"", "excluded by FURN*", "set by you"). "Need a look" `!` for an Other layer with ≥ 100 entities (not when you chose Other) and for a Wall layer none of whose lines ended up as a wall face; flagged rows sort first. Choices saved per drawing in `config.json` → `cadToBim.LayerRoleOverrides`. The Window role now decides which layers give window marks; `WindowLayerHints` only makes the first guess. Skip keeps the old Ignore behaviour (layer not read at all). | `LayerRole`, `LayerRoles`, `LayerRoleRules` in `CadToBimFlow.cs`; `CadToBimSettings.SetRoleOverride/RoleOverridesFor`; `CadToBimViewModel.SetRole`, `Elaborate`, `CountWallFaces` |
| 3 | Preview: "will be built" counts (walls, doors, windows, rooms, erased · brushed · picked out). Erase and Brush as before. **Pick** tool: click a door or window to leave it out, click again to bring it back (dashed red). Spot for the electrical table: `<ContentControl x:Name="PreviewExtras"/>`. | `CadOverlayViewport` (`ViewportMode.Pick`, `OpeningClicked`, `NearestOpening`), `CadToBimSession.PickedOut` |
| 4 | Build: today's controls plus "! Will create N new level(s): …" (Add to this project only). Every level the builder creates now gets a floor plan view, so its rooms are placed. Spot for Types: `<ContentControl x:Name="BuildTypes"/>`. Build button reads "Build N walls + M more". | `LevelPlanner` in `CadToBimFlow.cs`; `Cad2BimBuilder.LevelFor/CreateFloorPlan` |
| 5 | Review replaces the one-line result: headline (time + levels), CREATED rows with **Select in model** (per row or all), SKIPPED grouped by the real reason (click → numbered red rings in the viewport), WARNINGS (new levels, walls erased, openings picked out, unpaired wall lines), **Undo this build**, Build more, Done. `BuildReport` records created ids per category, new level / view / helper ids, and every skipped item (category, reason, drawing position). Fixed: every skipped opening said "no door/window family"; rooms with a short outline or a Revit failure were dropped silently. | `BuildReport.cs` (`SkippedItem`, `SkipGroup`, `SkipReason`), `Cad2BimBuilder`, `Cad2BimModelOps.Undo/Select` |
| 6 | Ctrl+Z sync: the pane asks Revit which "built" walls still exist when the Build step opens, and the build itself checks again on the Revit thread before creating anything. Gone ids are dropped, so those walls are pending again. | `BuildJobKind.CheckBuilt`, `BuildRequest.BuiltWallIds`, `BuildReport.Stale/MissingIds`, `CadToBimSession.ForgetBuilt` |
| 7 | Chat entry: an **Open in CAD to BIM** button beside a DWG attached in the Copilot prompt bar opens the pane on that file. `cad_*_from_attachment` tools unchanged. | `UI/CadToBim/CadToBimLauncher.cs`, `App.CadToBimOpenEvent`, `PromptBar.OpenInCadToBimButton` |

The build handler's one request slot now carries four jobs (`BuildJobKind`): Build, CheckBuilt, Undo, Select. Build and Undo lock the pane; a background CheckBuilt never blocks a build or an undo (they take its place in the slot).

## Deviations from the design

- Right column is 280 px (was 258) so the role dropdown, count and `!` fit on one row.
- Electrical is not a role yet (AI-346 adds it, per the task). Pick works on doors and windows only.
- "Click a layer name to solo it" (design §4.1) is not built. Checkboxes still show/hide layers.
- Clicking a skipped group marks the items with red rings but does not zoom to them: the upstream `CadViewport` has no public zoom-to API and the engine is left as is.
- No "one wall type for every thickness" warning: AI-345 replaces that behaviour.
- Chat: the smallest clean version. The button sits beside the attachment chip in the prompt bar, before sending, for every attached DWG. There is no card in the chat after a reply.
- "Done" closes the review and returns to step 1 (the pane stays open).
- When the build finds walls undone with Ctrl+Z it builds nothing and asks you to look at the new count and press Build again, instead of silently building more than the button said.
- Door swing arcs still come from every layer that is read (as today). The Door role decides which layers are read, not which arcs count.

## Revit test checklist

Build with `dotnet build RevitWebAppSync.csproj` (deploys to your Addins folders), start Revit, open a project with Level 1 and Level 2.

1. **Open.** Ribbon → CAD to BIM → pick a plan DWG. Expect: pane opens on step **1 Layers**, title shows the file name, Layers list with a dropdown per row and a grey reason line under each.
2. **Need a look.** Look for `0` or another big layer with no hint. Expect: amber `!` and "N entities, no name hint matched", sorted to the top; the header says "N · M need a look".
3. **Change a role.** Set a door layer (e.g. A-DOOR) to *Window*. Expect: one "Reading…" pass, the row now says "set by you", Preview window count changes. Close and reopen the same DWG: the choice is still *Window*. Open a different DWG: its own guesses.
4. **Skip.** Set a layer to *Skip*. Expect: it is hidden and not read (its linework disappears from detection).
5. **Steps.** Press *Next: Preview*. Expect: "1 Layers ✓" in green, counts on the right. Click "1 Layers" in the step bar and back to "2 Preview": counts unchanged.
6. **Erase / Brush / Pick.** Erase a wall, brush a missed one, pick a door (dashed red). Go back to Layers and forward again: all three still applied. Pick the door again: it comes back.
7. **Build step.** *Next: Build*. Expect: thickness, height, storeys, target. With *Stack as storeys* on a sheet with more plans than levels above the chosen level: amber "! Will create 1 new level: CAD Level N". The button reads "Build N walls + M more".
8. **Build.** Press Build. Expect: one undo step in Revit, pane opens **4 Review**: "✓ Built in x s on Level 1, …", created counts, skipped groups with real reasons, warnings (e.g. "New level made: CAD Level 2 (with a floor plan)", "1 wall erased in preview").
9. **Rooms on new levels.** Open the new CAD Level's floor plan in Revit. Expect: the plan view exists and rooms are placed on it (before this change they were skipped).
10. **Select in model.** Click *Select in model* (all) and the *Doors* row. Expect: those elements selected and the view zooms to them.
11. **Click to find.** Click a skipped group. Expect: numbered red rings on those items in the viewport; status "Showing N … skipped: <reason>".
12. **Undo this build.** Click it, confirm. Expect: everything from this build (incl. a new level and its plan) deleted in one Revit undo step named "CAD to BIM: undo build"; the pane returns to **2 Preview** with the walls counted as unbuilt. Ctrl+Z in Revit brings them back.
13. **Ctrl+Z sync.** Build again, then press Ctrl+Z in Revit. In the pane click *Build more* → *Next: Build*. Expect: the wall count is back to the full number (status mentions walls no longer in the model). If you press Build before that, expect "Nothing was built. Check the count, then build again."
14. **New .rvt.** Choose *Save as new .rvt beside the drawing*, Build. Expect: the file is saved and opened; in Review, *Undo this build* shows a message to close and delete that file, nothing is deleted.
15. **Chat entry.** In Copilot, attach a DWG (paperclip or drop). Expect: an "Open in CAD to BIM" box beside the chip; clicking it opens the pane on that file (step 1). The chat's own DWG tools still work when you send.
16. **Dark theme.** Toggle the Copilot moon. Expect: step bar, dropdowns, review colours all readable.

## Known gaps

- No zoom to skipped items (see deviations).
- The level forecast uses the level list read when the pane was opened from the ribbon (plus levels this pane created). Levels added by hand later are not seen until the pane is reopened.
- For "Save as new .rvt" no level forecast is shown (the template's levels are unknown before the build).
- Undo this build deletes only what is still in the model and in the project the build went into; switching to another project first gives "Switch back to … first".
- XAML was checked by a throwaway runtime parse and render of all four steps (light theme) outside Revit; not yet seen inside Revit.

## Tests

`dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Cad"`: 192 before, 236 after (all pass). New: `Tests/CadToBimFlowTests.cs` (roles, need-a-look, steps, level forecast, skip groups, pruning, pick, view-model flow for review / undo / select / Ctrl+Z sync), one settings round-trip test.

## Notes for AI-345 and AI-346

- **AI-345 (Types in Build):** put the TYPES section in `CadToBimPanel.xaml` at `<ContentControl x:Name="BuildTypes"/>` (step 3, between WALLS and PLANS ON SHEET). The builder's type choice is still `DefaultWallType` / `FirstSymbol` in `Cad2BimBuilder.cs`; pass chosen types through `BuildRequest`. Skip reason `NoFamily` already exists for doors and windows.
- **AI-346 (Electrical):** add `Electrical` to `LayerRole` and one `RoleChoice` line in `LayerRoles.Choices` (`CadToBimFlow.cs`); give it name hints in `LayerRoleRules.Guess` (and a settings hint list). Roles are saved by name, so nothing saved shifts. Put the block → family table at `<ContentControl x:Name="PreviewExtras"/>` (step 2). For Review, add a `BuildCategory.Electrical`, its ids list in `BuildReport` (`IdsOf`, `AllCreatedIds`), a `CreatedRow`, and a skip reason such as "not mapped"; `BuildReport.Group` and the Review list pick it up. Pick can extend `NearestOpening`-style hit testing to electrical points.
