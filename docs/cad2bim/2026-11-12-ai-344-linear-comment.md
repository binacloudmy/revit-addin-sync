**AI-344 design draft: CAD to BIM flow** (for review with Ashraf before AI-345 and AI-346)

Full doc: attached to this issue as `2026-11-12-cad-to-bim-flow-design.md` (based on develop `d2bf39d7`).

The numbers in the mockups below are examples, not real build results.

**Proposal in short**

1. Four steps in the pane: **Layers → Preview → Build → Review**. The preview stays on screen; only the right column changes.
2. Layer roles: Wall, Door, Window, Electrical (new, AI-346), Other, Skip. Picked from a dropdown, with the reason for each guess shown. Today it is a 4-state click chip and each click re-reads the DWG.
3. New Review step: created, skipped with reasons, warnings, "Undo this build".
4. The pane is the only place that builds a whole plan. The Copilot chat opens the pane with the attached DWG.

**Flow**

![CAD to BIM proposed flow](https://uploads.linear.app/ca093664-1f63-4613-be98-06326c548bf5/2d3f97d6-102d-4ef6-a5a2-b26dfbfa2952/0ae00638-8d83-49ef-87ac-d474a1f27262)

**Step 1: Layers** (right column, 258 px)

```
+--------------------------------------+
| [1 Layers] 2 Preview 3 Build 4 Review|
+--------------------------------------+
| LAYERS            18 · 2 need a look |
| [x] A-WALL      412  [Wall       v]  |
|       name has "wall"                |
| [x] A-DOOR       38  [Door       v]  |
|       name has "door" · 36 swings    |
| [x] A-GLAZ       54  [Window     v]  |
| [x] E-LIGHT     120  [Electrical v]  |
|       120 blocks, 3 block names      |
| [x] 0           903  [Other      v] !|
|       903 lines, no hint matched     |
| [ ] FURN         77  [Skip       v]  |
|       excluded: FURN*                |
+--------------------------------------+
```

**Step 2: Preview, electrical blocks** (unmapped blocks are listed, not guessed)

```
+--------------------------------------+
| ELECTRICAL BLOCKS      4 names       |
| LIGHT-DL   86  [Downlight 600   v]   |
| SSO-13A    40  [Twin socket 13A v]   |
| SW-1G      22  [1 gang switch   v]   |
| DB-MAIN     1  [Not mapped      v]  !|
|  1 block not mapped: listed, not     |
|  built. Pick a family to include it. |
+--------------------------------------+
```

**Step 4: Review**

```
+--------------------------------------+
| 1 Layers 2 Preview 3 Build [4 Review]|
+--------------------------------------+
| CREATED                     Select   |
|  Walls 210  Doors 33  Windows 52     |
|  Rooms 8    Lights 86 Power 40       |
+--------------------------------------+
| SKIPPED (13)          click to find  |
| > 2 walls    centreline too short    |
| > 2 doors    host wall not built     |
| > 1 door     narrower than 300 mm    |
| > 6 rooms    Level 2 has no plan view|
| > 1 DB       DB-MAIN not mapped      |
+--------------------------------------+
| WARNINGS (1)                         |
| ! New level made: CAD Level 2        |
+--------------------------------------+
| [ Undo this build ]  or Ctrl+Z once  |
| [ Build more ]           [ Done ]    |
+--------------------------------------+
```

**Found on develop while checking**

- Roles: there is a 4th state `—` (Other), and the Opening role does not decide windows; a settings word list does.
- Skip reasons in the status line are often wrong ("no door/window family" is shown for all four opening skip reasons). Some failed rooms are dropped with no count.
- After Ctrl+Z the pane still thinks the walls are built, so Confirm says "nothing new to build".
- Chat CAD tools use a different engine from the pane and make one undo step per tool.
- The pane engine on develop drops block names. A block reader has been built on the AI-346 branch (not merged yet).

**Open questions for Ashraf**

1. Pane builds, chat only opens the pane: agreed? Retire the chat `cad_*_from_attachment` tools later, or keep them for small jobs?
2. Electrical: which layer hint words and which family library? Block → family table per user or per project?
3. Electrical mounting heights: default per family type, or from block attributes?
4. "Save as new .rvt" undo: just say "delete the file", or a "Close and delete" button?
5. Split Opening into Door and Window? Keep both Other and Skip?
6. New levels have no plan view, so their rooms are skipped. Create a plan view per new level?
7. Is "Undo this build" worth it now, or is Ctrl+Z plus the sync fix enough?
