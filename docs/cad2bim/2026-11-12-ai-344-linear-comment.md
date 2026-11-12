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

**Step 1: Layers**

![Step 1 Layers](https://uploads.linear.app/ca093664-1f63-4613-be98-06326c548bf5/d6695ab3-e8e4-4db5-ad4c-cfd42c7d5664/1d6325e0-6730-4189-9410-2b23e076c933)

**Step 2: Preview**

![Step 2 Preview](https://uploads.linear.app/ca093664-1f63-4613-be98-06326c548bf5/0923d03e-940f-4c7e-a3c9-2271d34b2932/e5e7ef26-e3ee-4bce-9c68-540b1e48c87d)

**Step 3: Build**

![Step 3 Build](https://uploads.linear.app/ca093664-1f63-4613-be98-06326c548bf5/2630d08c-82b3-43bd-bcb6-0f0d846994cd/58ee297a-f231-4351-ba54-23f6934c7df2)

**Step 4: Review**

![Step 4 Review](https://uploads.linear.app/ca093664-1f63-4613-be98-06326c548bf5/1889ffb1-ead5-4eb1-9312-0c6696c70713/cd90ee4c-a601-4622-bdff-224f7d1492db)

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
