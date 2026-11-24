# CAD electrical tools: contract and exit check

Phase 1 of the CAD electrical to BIM work. A DWG electrical layout becomes Revit devices in three tool calls:

1. `read_cad_electrical` reads the drawing.
2. `plan_cad_electrical` turns an AI-written mapping into a stored plan.
3. `place_socket_points` commits the plan.

`dump_electrical_reference` and the headless `--compare-devices` and `--fit` commands measure the result.

Phase 2 adds conduit and trunking runs (section 7): the mapping names the run layers, `plan_cad_electrical` plans the runs beside the devices, and `route_conduit` builds them as Revit conduit and cable tray.

**Copilot uses `place_cad_electrical` after `plan_cad_electrical`** (section 7). It places the devices and builds the runs in one call: one confirmation card covers the whole write, and one Ctrl+Z undoes it. `place_socket_points` and `route_conduit` stay for placing only devices or only runs, and for retries.

All names in this document are invented: SOCKET, LIGHT-A, A-WALL, E-POWER, E-CONDUIT, E-TRUNK, "Level 1".

Code:
- `BinaVibe/Mcp/Tools/Electrical/`: `CadElectricalTools.cs`, `CadElectricalMapping.cs`, `CadModelFit.cs`, `CadSymbolResolve.cs`, `CadElectricalResolver.cs`, `CadElectricalReview.cs`, `SocketPlacement.cs`, `PlacementCompare.cs`
- Phase 2: `CadRunChain.cs`, `CadRunPlan.cs`, `RunCompare.cs`, `CadElectricalWrite.cs` (`place_cad_electrical`) in the same folder, and `BinaVibe/Mcp/Tools/MutatorsMepRouting.cs` (`route_conduit`)
- `BinaVibe/Mcp/Tools/ImportDialogGuard.cs`
- `Cad2Bim/Cad2Bim.Headless/ElectricalCheckCommands.cs`, `Cad2Bim/Cad2Bim.Headless/RunCheckCommands.cs`

Units are millimetres in project-internal coordinates unless a field says otherwise.

## 1. `read_cad_electrical` (inspect)

**Args:**

| Arg | Type | Meaning |
|---|---|---|
| `dwg_ref` | string, required | `att:<guid>` for a drawing attached in the pane, or `model:<id>` (or a bare id) for a CAD link in the model |

**Path resolution:**
- `att:` resolves through the pane's attachment cache.
- `model:<id>` resolves through the `ImportInstance` and its linked file.
- An *imported* CAD has no file on disk. It is refused, and the message says to link the DWG or attach it instead.
- A missing file is refused, and the message says to reload the link.

**Reading:**
- The DWG is read by path with ACadSharp.
- There is no transaction and no undo entry.
- The scan is cached: 2 drawings for 30 minutes, keyed by path, length and last write time. The plan call that follows reuses the scan.

**Result:**

```json
{
  "ok": true,
  "dwg_ref": "att:…",
  "schema": "cad.electrical.census/1",
  "census": { "…": "slim census: block classes first (symbol-sized, non-container), then layers" },
  "roles": ["socket", "switch", "isolator", "distribution_board", "light", "fan", "comms_outlet", "other", "ignore", "unknown"],
  "hosts": ["wall", "ceiling", "none"],
  "layer_roles": ["background", "conduit", "trunking", "wiring_schematic", "ignore", "unknown"],
  "model": {
    "levels": [{ "name": "Level 1", "elevation_mm": 0.0 }],
    "family_types": [{ "family_type": "Socket Outlet : Single", "category": "Electrical Fixtures", "hosting": "face" }],
    "family_types_total": 1,
    "family_types_truncated": false,
    "conduit_types": [{ "type": "Conduit with Fittings : PVC", "with_fittings": true, "sizes_mm": [16, 20, 25, 32] }],
    "cable_tray_types": [{ "type": "Cable Tray with Fittings : Trunking", "with_fittings": true }]
  },
  "notes": []
}
```

**Slim census limits:** at most 150 block classes, 150 layers and 40 legend rows. A cap that cuts sets `blocks_truncated`, `layers_truncated` or `legend_truncated`. Each layer has name, inserts, length, long open polylines and the wall hint (`pairs_like_walls`). These caps are maximums: the size budget below decides how much is actually sent.

**Size budget:** the whole `read_cad_electrical` result is kept under 24,000 characters of JSON (`CadElectricalReview.ResultBudgetChars`).
- Why 24,000: the local Copilot engine works in a 30,000-token context, shared by the prompt, the tool list, the chat and this result. A 55,000-character result (the earlier budget) hit that limit again and again in one turn. 24,000 characters is roughly 8,000 to 12,000 tokens.
- The trade-off: the issue's caps of 150 classes and 150 layers only fit for a small drawing. On a large one, the lowest-ranked classes and layers are dropped, and `blocks_truncated` or `layers_truncated` says so. The top-ranked classes (symbol-sized, non-container) are kept, so what gets mapped is still there.
- The census gets what the rest of the result leaves (at least 10,000). The model's `family_types` count against the budget, which is why they are capped at 80.

Block classes are ranked symbol-sized non-container first. When the census is over its budget, it is shrunk from the lowest-ranked class up, one step at a time, until it fits:
1. near texts past the first 2
2. attributes
3. the symbol lists of the plans past the first 10, from the last plan: `symbol_layers` is dropped and `symbols_by_block` keeps 3
4. whole classes down to the first 30 (sets `blocks_truncated`)
5. layers, from the end, down to the first 30 (sets `layers_truncated`)
6. the rest of the classes, then the rest of the layers

`budget_trimmed` counts the entries shrunk or dropped; 0 when the census fit.

**Plans:** each census `plans` entry is one cluster of walls on the sheet, biggest first:

```json
{
  "cluster": 0, "width_m": 42.5, "height_m": 18.2, "walls": 310,
  "titles": ["LEVEL 1 POWER LAYOUT"], "symbols": 212,
  "symbols_by_block": [{ "block": "SOCKET", "count": 96 }, { "block": "LIGHT-A", "count": 4 }],
  "symbol_layers": [{ "layer": "E-POWER", "count": 98 }, { "layer": "E-LIGHT", "count": 4 }]
}
```

- `symbols_by_block`: the top 8 symbol-sized, non-container block classes drawn inside the plan, by instance count. Symbol-sized and container are judged per class, as the block ranking does.
- `symbol_layers`: the top 5 layers of those instances.
- Counts only, no coordinates.

**Picking the plan:** one sheet often holds several plans of the same floor: the power layout, the lighting layout, the small power or data layout. Their titles and sizes can be alike. Pick the plan whose `symbols_by_block` and `symbol_layers` match the request: sockets for a socket request, lights for a lighting request. Ask the user only when two plans carry the same symbols.

**`conduit_types` and `cable_tray_types`:** up to 50 each, as `Family : Type`. `with_fittings` says whether bends can be built. `sizes_mm` are the conduit trade sizes (nominal diameters in the model's size lists) of the type's standard. When the standard cannot be read, they are every standard's sizes.

**`hosting` values:**
- From the family's Host setting: `wall`, `ceiling`, `floor`, `roof`, `face`.
- Otherwise from the placement type: `work_plane`, `hosted`, `none`.

**`family_types`:** up to 80 types from the device categories, in this order:
1. electrical fixtures and equipment
2. lighting fixtures and devices
3. communication, data and telephone devices
4. fire alarm, security and nurse call devices

Within a category they are sorted by name. When the model has more, `family_types_truncated` is true and `family_types_total` gives the full count.

**Errors and notes:**
- `aec_refused`: `ok:false`. The drawing holds only AutoCAD vertical-product objects. Export it with EXPORTTOAUTOCAD and attach the exported file.
- `symbols_are_exploded`: `ok:true` with a note. Every symbol-sized insert sits on a wall layer, so the symbols are probably loose linework.
- `aec_warning: …`: `ok:true` with a note.

## 2. Mapping schema

The AI writes the mapping from the census. Every row carries `evidence`.

```json
{
  "symbols": [
    {"block": "SOCKET", "role": "socket", "family_type": "Socket Outlet : Single",
     "mount_height_mm": 300, "host": "wall", "evidence": "legend: 'SWITCHED SOCKET OUTLET'"},
    {"block": "LIGHT-A", "role": "light", "family_type": null, "mount_height_mm": null,
     "host": "ceiling", "evidence": "legend row"},
    {"block": "anon:1a2b3c4d", "role": "unknown", "evidence": "no legend row, no text"}
  ],
  "layers": [ {"layer": "A-WALL", "role": "background"}, {"layer": "E-POWER", "role": "ignore"} ],
  "plans":  [ {"cluster": 0, "level": "Level 1", "evidence": "title text"} ]
}
```

**Symbol rows:**
- `block` is the census block-class key: a block name or `anon:xxxxxxxx`.
- `role` is one of the roles above. Every role except `ignore` and `unknown` is placeable.
- `host` is `wall`, `ceiling` or `none`.
  - When it is null on a placeable row, it defaults to `ceiling` for `light` and `fan` and to `wall` otherwise.
  - A defaulted host is noted as `host_defaulted: <block> -> <host>`.
- `mount_height_mm` is measured from the level, which is finished floor level. It must be 0 to 4000.
- `family_type` is `Family : Type`, or the type name alone when only one family has that type name. The model's spelling replaces the mapping's.

**Layer rows:** `role` is a layer role.
- `background` layers are the walls the drawing is aligned on.
- `conduit` and `trunking` layers are runs (section 7). Their rows carry more fields and need `evidence`:

```json
"layers": [
  {"layer": "E-CONDUIT", "role": "conduit", "conduit_type": "Conduit with Fittings : PVC",
   "diameter_mm": 20, "run_height_mm": null, "evidence": "legend: '20mm PVC CONDUIT'"},
  {"layer": "E-TRUNK", "role": "trunking", "tray_type": "Cable Tray with Fittings : Trunking",
   "width_mm": 100, "height_mm": 50, "evidence": "text '100x50 TRUNKING'"}
]
```

| Field | Row | Meaning |
|---|---|---|
| `conduit_type` | conduit | `Family : Type`, or the type name alone when only one family has it. The model's spelling replaces the mapping's. |
| `diameter_mm` | conduit | Trade size, above 0 and at most 200. It must be one of the type's sizes. |
| `tray_type` | trunking | A cable tray type, matched like `conduit_type` |
| `width_mm`, `height_mm` | trunking | Above 0, at most 2000 wide and 1000 high |
| `run_height_mm` | both | Height of the run above the level, -1000 to 10000. It wins over the call's `run_height_mm`. |
| `evidence` | both | Required |

Type and size are never defaulted.

**Plan rows:** map a census plan cluster to a level.

**Name matching:** a block, layer or level matches exactly first, then by a unique case-insensitive match. It is checked against the uncapped scan, not the slim census.

A JSON string holding the object is accepted and parsed first.

### Error codes (reject the call)

All issues come back in one pass:

```json
{"ok": false, "error": "mapping_invalid", "issues": [{"path", "code", "message", "candidates"}]}
```

| Code | When |
|---|---|
| `not_an_object` | The mapping, a list or a row is not an object, or the text is not JSON |
| `unknown_role` | A symbol or layer role is not in its list |
| `unknown_host` | A host is not `wall`, `ceiling` or `none` |
| `duplicate_block`, `duplicate_layer` | Mapped twice |
| `block_not_in_drawing`, `layer_not_in_drawing` | No such class or layer (near names in `candidates`) |
| `missing_evidence` | A symbol or plan row has no evidence |
| `bad_mount_height` | Not a number, or outside 0–4000 |
| `cluster_out_of_range` | A plan row's cluster is not a plan of the drawing |
| `level_not_found` | No such level |
| `family_type_not_found`, `family_type_ambiguous` | No match, or several (listed) |
| `missing_evidence` (layer row) | A conduit or trunking row has no evidence |
| `conduit_type_not_found`, `conduit_type_ambiguous` | No conduit type matches, or several (listed) |
| `tray_type_not_found`, `tray_type_ambiguous` | No cable tray type matches, or several (listed) |
| `bad_diameter` | Not a number, at most 0, or over 200 |
| `bad_tray_size` | A width or height that is not a number, at most 0, or over its limit |
| `bad_run_height` | Not a number, or outside -1000 to 10000 |
| `diameter_not_in_standard` | Not one of the conduit type's sizes (the sizes are the `candidates`) |

### Incomplete rows (do not reject)

A placeable row plans zero points and is listed under `unmapped_classes` with its instance count and a reason:

| Reason | Meaning |
|---|---|
| `role_unknown` | The row's role is `unknown` |
| `no_family_type` | The row has no family type |
| `no_mount_height` | A wall-hosted row has no mount height |
| `not_in_mapping` | A symbol-sized, non-container class off the wall-like layers that the mapping omits |

A run layer row plans no runs and is listed under the conduit block's `unplanned_layers` with its piece count, length and a reason:

| Reason | Meaning |
|---|---|
| `no_conduit_type`, `no_diameter` | A conduit row without its type or size |
| `no_tray_type`, `no_tray_size` | A trunking row without its type, width or height |
| `no_run_height` | Neither the row nor the call gives a run height |
| `too_many_pieces` | The layer has more than 20000 pieces in the plan: it is not a run layer |

## 3. `plan_cad_electrical` (inspect)

**Args:**

| Arg | Type | Meaning |
|---|---|---|
| `dwg_ref` | string, required | As for `read_cad_electrical` |
| `mapping` | object or JSON text, required | Section 2 |
| `level` | string | Overrides the plan rows and the fit's choice |
| `cluster` | int | Census plan cluster; else the only plan row with a level, else the only cluster, else `cluster_needed` |
| `pick` | int | Chooses one of several identical copies of the floor (see rivals) |
| `link_id` | int | A CAD link or import in the model, moved onto the building by the user. The drawing is placed where its transform puts it, and the fit is not run. See **Positioned link**. |
| `confirm_low_score` | bool, default false | Place from a positioned link that scores provisional (0.25 to 0.50). Without it such a link is refused with `link_low_score`. |
| `run_height_mm` | number | Height of conduit and trunking runs above the level, -1000 to 10000, for run rows that give none. Never defaulted. Out of range: `bad_run_height`. |
| `include_points` | bool | Diagnostic: adds `points`, `dropped` and `runs`, at most 2000 rows each. Not exposed to the AI. |

**Flow:**

1. **Scan and check the mapping.** The scan comes from the cache.
2. **Pick the cluster.**
3. **Pick the background layers.** In order: the mapping's `background` rows, else the census wall-like layers, else the CAD to BIM pane's wall-name hints. If none are found, the call fails with `no_background`.
4. **Pick the level.** In order: the `level` arg, else the cluster's plan rows when they name exactly one level, else the best fit over all levels.
   - Several levels in the rows give `level_needed`.
   - A tie of the best fits within 0.05 at the same place gives `level_ambiguous`. This happens with typical floors.
   - A level that fits a *different* place in the same cluster is not an error. It is noted as "this cluster also fits <level> at another plan; call again with level=…".
5. **Fit the drawing to the model's walls on that level** (`CadModelFit`).
   - The drawing's wall centrelines are fitted on the model's structural walls first. Walls of 50 mm or less are finishes and are left out, unless fewer than 4 walls would remain.
   - Then wall faces are tried.
   - When both fail, a positioned link of the same drawing is tried (see **Positioned link**).
   - Under 0.50, with no link to place from, the call fails with `alignment_failed` and tells the user to link the DWG, move it onto the building and call again.
   - With `link_id`, the link's position is used instead of the fit.
6. **Resolve each placeable symbol inside the footprint** to a point (`CadSymbolResolve`), then flag duplicates.
7. **Plan the runs** of the conduit and trunking layers (section 7).
8. **Store the plan** (`socket_plan:` id, 2 hours) and return the review.

**Fit outcomes:** `accepted` at a score of 0.80 or more, `provisional` at 0.50 or more, otherwise `failed`. A provisional fit flags every point `provisional_alignment` and adds a note. A positioned link has its own bands (see **Positioned link**).

**`alignment_failed` fields:** `level`, `cluster`, `score`, `score_uncropped`, `inside_share`, `method` (`fit` or `fit_faces`), and where the fit came closest:
- `best_rotation_deg`, in (-180, 180];
- `best_tx_mm`, `best_ty_mm`.

A link put at the origin unturned and moved by (`best_tx_mm`, `best_ty_mm`, 0) lies there, when `best_rotation_deg` is 0. `crop_too_small` carries the same fields. `links_tried` is added when links of the drawing were measured and ignored.

**What provisional means:** a provisional fit is *not* a misalignment. The drawing very likely sits correctly on the level; only the wall overlap score is below the confident threshold (a drawing's walls rarely match the model's exactly). The points are placed and flagged so the drafter can spot-check a few. Do not tell the user the DWG is not aligned. The review's `alignment.meaning` says this in one line, and the headline reads "Alignment 0.51 (provisional: likely right, spot-check)."

`alignment.meaning` by outcome:

| `outcome` | `meaning` |
|---|---|
| `accepted` | "Confident fit: the drawing's walls lie on this level's walls. Points are placed as planned." |
| `provisional` | "Provisional fit: the drawing very likely sits correctly on this level, but the wall overlap score is below the confident threshold. Points are placed and flagged so the drafter can spot-check a few. This is not a misalignment." |
| `failed` | "Failed fit: the drawing's walls do not lie on this level's walls well enough to place anything. Check the level and the background layers; or link the DWG into the model, move it onto the building, and run plan_cad_electrical again." |

A failed fit returns `alignment_failed` rather than a review, so `failed` is listed for completeness.

With method `linked_instance` the meaning names the link:

| `outcome` | `meaning` |
|---|---|
| `accepted` | "Placed from your positioned link: the link's position lays the drawing's walls on this level's walls. Points are placed as planned." |
| `provisional` | "Placed from your positioned link, provisional: the link's position lays only part of the drawing's walls on this level's walls. Points are placed and flagged so the drafter can spot-check a few." |

The headline then reads "Alignment 0.61 from your positioned link."

### Positioned link

When the fit cannot place the drawing, the user can link the DWG (`link_file`) and move it onto the building (`move_elements`). The link's total transform is then the placement.

`link_file` links a DWG at the origin and never moves it. When `dwg_ref` is such a link (unturned, at the internal origin) and the plan's own fit passed (not `failed`) with a rotation within 1 degree of 0, 90, 180 or 270, the plan result carries `link_place` (`link_id`, `rotation_deg`, `tx_mm`, `ty_mm`) and a note: rotate the link about 0,0 with `rotate_elements` when `rotation_deg` is not 0, then `move_elements` by (`tx_mm`, `ty_mm`), then plan again with `link_id`. The plan itself stays read-only.

**When it is used:**
- With `link_id`, always. The fit is not run.
- Without it, only after both the centreline fit and the face fit fail. A passing fit is never overridden.

**Which links:**
- `link_id` names one instance, linked or imported. A link must be of the drawing behind `dwg_ref` (same full path, else same file name); a link of another drawing is refused at once with `link_is_other_drawing`, before the drawing is read.
- Without it, the model's CAD links of the same file: by full path, else by file name. Imports have no file, so they need `link_id`.
- An `att:` ref resolves to the file the pane attached, not to a copy. A link of that file matches by path. A link made from a copy elsewhere matches by file name.

**The transform:** the rotation is `atan2(BasisX.Y, BasisX.X)`, and the translation is the link's origin in mm. A mirrored, tilted or scaled link is refused with `link_unusable`. Revit puts a link's unit into its geometry, not its transform, so `scaled` is also a link whose plan bounding box is not 0.8-1.25 times the drawing's extent as the engine reads it (a link in another unit). `link_file` links a DWG in the engine's unit and returns it as `unit`.

**The level:** the `level` arg, else the cluster's plan rows. Else, with `link_id` or a link found automatically after the fit failed, the level the link's position scores best on (each found link is measured on every level, never only on the failed fit's best level). A second level within 0.05 gives `level_ambiguous`.

**Link outcomes:** the user placed the link, so the bands are lower than the fit's:
- `accepted` at 0.50 or more;
- `provisional` from 0.25 to 0.50, placed only with `confirm_low_score=true` (else `link_low_score`): every point is flagged `provisional_alignment`, and a note asks for a spot-check;
- below 0.25 nothing is placed.

The crop guard holds: too little drawing wall inside the footprint fails as for a fit.

**Several links:**
- Found automatically, a single matching link that clears 0.25 is used, and a note says so.
- Several matching links give `link_ambiguous`. The caller passes `link_id`.
- Links below 0.25 are ignored, and `alignment_failed` gains `links_tried`.

**Recorded:** the plan's alignment has method `linked_instance` and `link_id`. `params_used.link_id` is the link used, else null.

**Errors:**

| Error | When | Extra fields |
|---|---|---|
| `link_not_found` | `link_id` is not a CAD link or import of this model | `link_id` |
| `link_is_other_drawing` | `link_id` is a CAD link of another file than `dwg_ref`'s drawing (for example the architect's model). Checked first, so it fails fast | `link_id`, `link_file`, `drawing_file`. Call `list_cad_links` and pick the link whose name matches `drawing_file` |
| `link_unusable` | The link is mirrored, tilted or scaled, or its transform cannot be read | `link_id`, `reason`: `mirrored`, `tilted`, `scaled`, `without a readable transform` |
| `link_misaligned` | `link_id` scores below 0.25 on the level, or lays too little of the drawing inside it | `link_id`, `level`, `cluster`, `score`, `score_uncropped`, `inside_share`, `reason` (`alignment_failed` or `crop_too_small`), `rotation_deg`, `tx_mm`, `ty_mm` (the link's placement). `candidates`: the clusters' `{cluster, titles, score}` under the link, best first. Bounded under the 50 s HTTP cap: clusters without walls are skipped, at most 12 others are measured, and none after 30 s of plan time; `clusters_truncated` is true when a cluster was left out. When another cluster scores better, the message names it. |
| `link_low_score` | The link (given or found) scores provisional, 0.25 to 0.50, and `confirm_low_score` is not true | `link_id`, `level`, `cluster`, `score`. Check the level and the link's place, or pass `confirm_low_score=true` |
| `link_ambiguous` | No `link_id`, the fit failed, and the model holds several links of the drawing, one of them clearing 0.25 | `level`, `cluster`. `candidates`: `{link_id, score, outcome, unusable, rotation_deg, tx_mm, ty_mm}`, best first |
| `level_ambiguous` | With a link (`link_id` or found) and no level given, two levels score within 0.05 | `cluster`, `link_id`. `candidates`: `{level, score, outcome}` |

`links_tried` rows on `alignment_failed` have the `link_ambiguous` candidate shape.

### Footprint crop

The score is measured inside the level's **model footprint**:
- The footprint is a 1000 mm occupancy grid of the model walls.
- A cell is inside when it lies within both its row's and its column's range of marked cells. The grid is then grown by one cell.
- So the middle of a hall is inside, the notch of an L is outside, and a stray far wall does not stretch it.

**Ranking:** candidates are ranked by covered length, uncropped. A shift by one bay can score the same cropped, but it covers less length.

**Scores:**
- `score` = covered ÷ (inside the footprint or covered)
- `score_uncropped` = covered ÷ all
- `inside_share` = inside ÷ all

When the drawing's wall length inside the footprint is under 0.25 × the model's wall length, the fit fails with `crop_too_small`.

**Membership:** a symbol belongs to the plan when, carried onto the model, it lies inside the footprint, whatever its census cluster. A symbol outside the footprint is:
- `outside_model_footprint` when it is in the chosen cluster. It is counted in the review and listed in `dropped`.
- counted as another plan's when it is in a different cluster. With `include_points` it is listed last in `dropped` as `other_plan`.

### Rivals and `pick`

One sheet can hold copies of the same floor. They fit equally well, and geometry cannot choose between them.
- **Rivals** are other placements whose covered length is at least 0.97 × the best, at a different place (rotation more than 1° away, or a shift of more than 2000 mm).
- With rivals and no valid `pick`, the call returns `plan_ambiguous`. Its `candidates` are `{pick, titles, score}`, ordered top to bottom and then left to right in the drawing.
- The caller passes `pick` with the candidate whose titles name the level.
- A picked candidate that failed returns `alignment_failed`.

### Resolving a symbol (wall host)

1. **Candidate walls.** A wall is a candidate when the symbol is within thickness/2 + 400 mm of its centreline, and from 150 mm before its start to 150 mm past its end. The score is `perp × 10 + |axial − length/2| × 0.1`, and the lowest wins. No candidate gives `no_wall_in_reach`.
2. **Side.** The side is the sign of the drawn centre's offset from the centreline.
   - Under 15 mm from the centreline, the room lookup is probed 150 mm past each face, and the side with a room wins.
   - Rooms on both sides, or on neither, flag `side_ambiguous` and use the + side.
3. **Snap.** The point is snapped to that face, facing out of it. The symbol's rotation is not used.
4. **Room.** The room is looked up 150 mm in front of the face, else at the drawn centre. The room gives the point its name and the wet check.
5. **Height.** Z = **level elevation + mount height**. The room's lower offset is not used, because legend heights are from finished floor level. `params_used.height_basis` is `"level"`.
6. **Linked wall.** A wall in a linked model flags `linked_wall` and gives an unhosted point.

### Resolving a symbol (ceiling and `none` hosts)

Both stay at the drawn centre, with no facing. The room is looked up there.

**Ceiling host:**
1. **Ceiling.** Local `Ceiling` elements whose level is the target level are tried. A ceiling's bounding box must contain the point. Then a vertical line through the point must cross one of the ceiling's bottom faces (`HostObjectUtils.GetBottomFaces`). That is the point-in-outline test, and it handles holes and slopes. Where two ceilings cover the point, the lower underside wins. Ceilings in a linked model are not used.
2. **Height.** With a ceiling, the point takes its `HostCeilingId`, its host becomes `ceiling`, and Z is the ceiling's underside at the point. The mapping's mount height is not used, and the point reports the underside's height above the level as its mount height.
3. **No ceiling.** Z = level elevation + mount height, and the point is unhosted. With no mount height either, the point is flagged `no_height`. If the row's family must sit on a ceiling (hosting `ceiling` or `hosted`), the point is flagged `no_ceiling`. A face, work plane or unhosted family is not flagged, because it can stand at the level plus the mount height.

**`none` host:** Z = level elevation + mount height, unhosted. With no mount height, the point is flagged `no_height`.

`params_used.ceiling_height_basis` is `"ceiling_underside, else level"`.

### Flags and defaults

| Flag | Default |
|---|---|
| `no_wall_in_reach` | excluded |
| `no_room` | kept |
| `wet_room` (room name contains a wet keyword) | kept |
| `already_present` (an existing device of the row's category within 150 mm in plan and within 150 mm of the point's Z, at its contact height; see **No doubling** and **Contact height** below) | excluded |
| `above_wall_top` (a wall point higher than the top of its wall: the wall's side face does not reach it, so a face-based family is placed through the wall overload instead of on the face) | kept |
| `linked_wall` | kept, unhosted |
| `provisional_alignment` | kept |
| `side_ambiguous` | kept |
| `duplicate_in_drawing` (same role within 20 mm in plan) | excluded |
| `family_host_mismatch` (the family's hosting cannot take the row's host: a ceiling family on a `wall` or `none` row, a wall family on a `ceiling` or `none` row, a floor or roof family on any row) | excluded |
| `no_ceiling` (a ceiling row found no ceiling, and its family is ceiling-hosted or host-based) | excluded |
| `no_height` (no ceiling was found and the row has no mount height) | excluded |

**Dropped symbols:** a symbol nested inside another mapped placeable symbol is dropped as `nested_in_mapped`.

**Result:**

```json
{
  "ok": true,
  "plan_id": "socket_plan:…",
  "dwg_ref": "…",
  "level": "Level 1",
  "cluster": 0,
  "review": {
    "headline": "Level 1: 24 drawn, 22 will be placed, 2 excluded. Alignment 0.91.",
    "alignment": {
      "level": "Level 1", "cluster": 0, "rotation_deg": 0, "score": 0.91,
      "score_uncropped": 0.47, "inside_share": 0.52, "model_coverage": 0.6,
      "outcome": "accepted",
      "meaning": "Confident fit: the drawing's walls lie on this level's walls. Points are placed as planned.",
      "method": "fit", "pick": null
    },
    "drawn": 24,
    "will_place": 22,
    "excluded": 2,
    "outside_model_footprint": 0,
    "rooms": [{ "room": "Office", "role": "socket", "drawn": 6, "will_place": 6 }],
    "flagged": [{ "flag": "already_present", "default": "excluded", "count": 2, "items": [], "truncated": false }],
    "unmapped_classes": [{ "block": "LIGHT-A", "instances": 12, "reason": "no_family_type" }],
    "family_types": [],
    "notes": []
  },
  "params_used": {
    "dwg_ref": "…", "cluster": 0, "level": "Level 1", "pick": null,
    "background_layers": ["A-WALL"], "pair_across_layers": true,
    "min_wall_mm": 50, "thin_walls_ignored": 3, "height_basis": "level",
    "reach_mm": 400, "axial_tolerance_mm": 150, "existing_mm": 150, "duplicate_mm": 20,
    "wet_room_keywords": ["…"],
    "run_height_mm": 2700, "run_join_mm": 20, "min_run_mm": 300, "min_tray_run_mm": 1000,
    "clutter_max_mm": 1000, "clutter_axis_tolerance_deg": 5, "drop_reach_mm": 300, "min_drop_mm": 300, "run_height_basis": "level"
  }
}
```

With a conduit or trunking row in the mapping, `review` also has a `conduit` block (section 7) and the headline ends with "Conduit: 12 runs, 84.0 m, 30 drops, 4 open ends."

The review carries no coordinates.

**`include_points`** adds:
- `points`: `index`, `role`, `block`, `room`, `host`, `xyz_mm`, `height_above_level_mm`, `facing`, `flags`, `excluded`. `xyz_mm` is absolute; `height_above_level_mm` is its Z less the plan level's elevation.
- `dropped`: `block`, `class` (the mapping row's role, or `unmapped`), `reason`, `xyz_mm`. With `points`, it lists every symbol drawn on the plan, so the exit check can tell a socket the drawing does not show:
  - first the real drops: `outside_model_footprint`, `nested_in_mapped`, `not_in_mapping` and the other unplanned reasons
  - then the symbols that are never placed: `ignored_class` (a row with role `ignore`), and non-placeable symbols outside the footprint (`outside_model_footprint`). Containers and unmapped blocks that are not symbol-sized or sit on a wall layer are not symbols and are left out.
  - then other plans' placeable symbols (`other_plan`)
- `dropped_lists_every_symbol`: true when the 2000-row caps cut nothing but `other_plan` rows
- `runs`: `index`, `layer`, `kind`, `points_mm`, `start` and `end` (`kind`, `point_index`, `element_id`, `drop_mm`), `length_mm`, `flags`, `excluded`
- on each point, `anchors_mm` and `along_offset_mm`: the anchor diagnostic (section 6). These are not used to place.

**Other errors:**
- `cluster_needed`, `cluster_out_of_range`: with `candidates` of `{cluster, titles, symbols, width_m, height_m, level}`
- `level_not_found`
- `no_model_walls`
- `aec_refused`

## 4. `place_socket_points` (mutate)

**Args:**

| Arg | Type | Meaning |
|---|---|---|
| `plan_id` | string, required | From `suggest_socket_points` or `plan_cad_electrical`. The plan must belong to the same document. |
| `family_type` | string | Used only for points whose plan names no type. The point's own type wins. |
| `indices` | int[] | Explicit points. These win even over excluded points. |
| `roles` | string[] | Filter, e.g. `["socket"]` |
| `level` | string | Level override |
| `mount_height_mm` | number | Replaces the plan's mount height (Z moves by the difference). A point on a ceiling keeps the ceiling's underside. |
| `facing_offset_deg` | number | Corrects a family whose front axis is authored off |

**Selection:** without `indices`, every non-excluded point is placed, filtered by `roles`. If no selected point has a family type, the call returns `ok:false` before any transaction.

**Transactions:** one `TransactionGroup` with one transaction per point, then `Assimilate`. The whole run is a single undo, and a refused point fails alone. A run that places nothing (every point skipped or failed) rolls its group back and leaves no undo entry (`OverheadPlacement.KeepGroup`); so do `route_conduit` with no run built and `place_cad_electrical` with neither. The turn receipt's green highlight is a separate transaction: clearing the previous turn's highlight at the end of a turn is one more undo step, even after a run that changed nothing.

**Placed ids:** each placed point records its device's id on the stored plan, so `route_conduit` can find the device for a drop (section 7).

**No doubling:** before each point, a device that exists *now* where the point would go skips the point under `skipped_already_present`. A point picked by index keeps its place. The rule (`OverheadPlacement.Matches`), with 150 mm for both distances:
- Never a device of another category, when both categories are known.
- Within 150 mm in plan, and then one of:
  - hosted on the point's own ceiling, whatever its height reads, so a second run skips every light the first one placed;
  - its contact height (below) within 150 mm of the point's Z. A ceiling point is compared at the ceiling's underside, so a socket on the wall under a light is a different device;
  - of the point's category, with its LocationPoint Z within 150 mm of the point's Z: an instance whose origin is at the point is the one placed there, whatever its host reports;
  - its contact height not trusted (a work-plane-based family read at its origin, off a vertical plane), and on the point's level band: its own level is the point's level, or with no level of its own its origin is between the point's level and the next one up. With no band known, plan alone.

`plan_cad_electrical` flags `already_present` by the same rule, with the row's family type's category.

**Contact height:** where a device meets its host, under its origin. A work-plane-based family's LocationPoint need not lie on its work plane: in a live test, two light families hosted on the same underside with "Offset from Host" 0 read +175 mm and -2044 mm. So:
- on a face (`HostFace` readable, face not vertical): that face's Z under the origin, plus "Offset from Host" along the face normal (`host_face`)
- on a ceiling with no face (a ceiling-hosted family): the ceiling's underside there (`ceiling_underside`)
- a work-plane-based family on a vertical plane (hosted on a wall, its face read or not, or its own work plane normal level): the LocationPoint's Z (`vertical_plane`). Its origin can sit off the plane only along the plane's normal, which is level. Before this rule, a face-based socket that the wall overload placed (the fallback, below) could fall to the level rule and read the level plus an "Offset from Host" of about 0, a mount height under where it stood, so a second run did not find it and placed it again.
- a work-plane-based or unhosted family on a level (or a horizontal work plane, or no host): that plane plus the offset parameter read back, "Offset from Host" first for a work-plane-based family and "Elevation from Level" first for an unhosted one (`level_offset`). A disagreement with the origin alone never moves it: a live test read a light's origin 2044 mm under a level offset that had taken. It gives way to the origin (`OverheadPlacement.LevelOrOrigin`) only when it is more than 20 mm off the origin and either the offset alone equals the origin's Z on a level not at 0 (`offset_absolute`: the offset is measured from elevation 0, and level + offset counted the level twice; a live test read 9900 mm for a wall-fallback socket at 6100 mm on a level at 3800 mm), or the origin is within 1 mm of the Z the tool has just placed it at (`origin_verified`, wall rows only).
- anything else: the LocationPoint's Z (`location`). For a work-plane-based family this height is not trusted, and the already-present check falls back to the level band.

**Placement by family:**

| Family | How it is placed |
|---|---|
| Face-based (work plane) with a local host wall | On the wall side face whose normal looks the way the point faces, through the face-reference overload. If refused, the wall overload is used (`host: "wall"`). A point over the wall's top (`above_wall_top`) has no face under it and always takes the wall overload. |
| Wall-hosted | Wall overload. A point with no local wall (`linked_wall`) fails with advice to use a face-based or unhosted family. |
| Unhosted | Level overload |

**Points not on a wall** (ceiling and `none` hosts) are placed by the family's `FAMILY_HOSTING_BEHAVIOR`:

| Family | How it is placed |
|---|---|
| Ceiling-hosted (or another host-based kind) | `NewFamilyInstance(point, symbol, ceiling, level, NonStructural)` on the plan's ceiling. With no ceiling, the point fails alone with `no_ceiling` and is never placed free-standing. |
| Face-based or work-plane-based | On a bottom face of the ceiling, through the face-reference overload, at the point where a vertical through the target crosses that face, with the reference direction along X. Faces that look down are tried, the one holding the target (within 5 mm) first, two at most. Each try is measured (below) and rolled back if it is off. With no ceiling, or if Revit refuses every face, the level overload is used. |
| Wall-hosted | Fails alone, because there is no wall. |
| Unhosted | Level overload, then the height is set from the level. |

These points have no facing, so `facing_method` is `not_applicable`.

**Height on a ceiling:** the host sets the height and `elevation_set_via` is `host`. The contact height is measured after placing. More than 20 mm off the target: a face-placed device is tried on the next face that looks down. If it is still off, or the family is ceiling-hosted, the point fails alone with `height_mismatch`, and its transaction is rolled back. The reason lists each try: the face's Z, the contact height and how it was read, and the origin's Z. It never falls back to a free-standing device at the level.

**Height at the level** (no ceiling, or the face was refused): a work-plane-based family placed on a level has no "Elevation from Level", so "Offset from Host" (`INSTANCE_FREE_HOST_OFFSET_PARAM`) is set first, then "Elevation from Level" (`INSTANCE_ELEVATION_PARAM`). An unhosted family tries them the other way round. Each is set to the target minus the level, and the first whose read-back value equals the value set (within 1 mm) wins. `elevation_set_via` is that parameter, or `insertion_point` for a family with neither parameter whose insertion point is already right. The point fails alone with `elevation_unset` only when every parameter is missing, read-only, refused, or reads back a different value; the reason says which. It is never left at the level. The height reported is the level plus the parameter, as in **Contact height**, never the origin.

Each item also carries:
- `z_mm`: the contact height, as placed (absolute, see **Heights** below)
- `height_via`: how it was read (`host_face`, `ceiling_underside`, `level_offset`, `offset_absolute`, `origin_verified`, `vertical_plane` or `location`)
- `origin_z_mm`: the raw LocationPoint Z
- `target_z_mm` and `host_ceiling_id`
- `geometry_z_mm`: the instance's bounding box, bottom and top
- `geometry_touches_host`: whether that box comes within 50 mm of the contact height. A recessed fitting spans it, and a surface or pendant one hangs from it. `false` means the family draws the fitting well away from where it is hosted. Those indices are listed in `geometry_off_host_indices`.

**Facing readback:**
- **Wall-hosted and unhosted:** `FacingOrientation`, corrected by flip, then by rotation. A correction that fails is reported as `uncorrectable`.
- **Face-hosted:** the host face's outward normal at the point. If the face cannot be read, the instance's work-plane normal (`GetTotalTransform().BasisZ`) is used. `FacingOrientation` lies in the face, so it is not used. These instances are measured and never flipped or rotated, so they report `as_placed` or `uncorrectable`.

**Height (wall points):** the height is read back. If it is wrong, it is set through the elevation parameters. `elevation_set_via` is `insertion_point`, the parameter used, or `unset`. Each wall item also carries `z_mm` (the contact height as placed, the height a later run's already-present check reads), `height_via`, `height_known`, `target_z_mm`, `origin_z_mm` only when the origin is more than 1 mm off `z_mm`, and `above_wall_top: true` when the plan flagged the point.

**Result:**
- Every existing key is kept: `ok`, `plan_id`, `count`, `created[]`, `failed[]`, `facing_uncorrected(_indices)`.
- Each `created` item has `index`, `created_id`, `host` (`wall_face`, `wall`, `ceiling`, `ceiling_face` or `unhosted`), `facing`, `facing_target`, `facing_error_deg`, `facing_method` and `family_type`.
- New keys: `by_role`, `by_family_type`, `skipped_excluded`, `skipped_already_present`, `geometry_off_host_indices` and `review`.
- A `failed` item's `reason` starts with a code where one applies: `no_ceiling`, `height_mismatch` (a ceiling device did not land within 20 mm of the underside) or `elevation_unset` (a device placed at the level could not be given its height).
- `review` is `PlaceReview`: a headline, `needs_review` counts of `facing_uncorrected`, `elevation_unset`, `no_room`, `wet_room` and `provisional_alignment`, and `heights`.

**Heights:** `z_mm`, `target_z_mm`, `origin_z_mm` and `geometry_z_mm` are absolute, in project-internal coordinates. On a level at 3800 mm, a socket at 300 mm reads `z_mm: 4100`. That is correct, not a wrong height. Every `created` item (wall, ceiling and unhosted alike) also carries:
- `level_name`: the level the device was placed on
- `level_elevation_mm`: that level's elevation
- `height_above_level_mm`: `z_mm` less the level's elevation, to 1 mm. This is the mounting height. Show this one to the drafter, never `z_mm`. It is null when the device has no level.

`review.heights` is `[{level, height_above_level_mm, count}]`, one entry per level and height, heights low to high. The headline ends with them, up to 5 per level: "40 placed; 2 things to check. Heights 300 mm (42), 1500 mm (48) above Level 1." `place_cad_electrical`'s headline carries the same sentence after its device counts.

## 5. Import dialog guard

When the add-in links a DWG to read it (the pane's scratch link, `DwgScratchCache.LinkInto`), a drawing with geometry past 1E9 units causes two problems:
- **The extents dialog.** Revit raises a TaskDialog: "Geometry in the file … has extents greater than 1E9 … will be truncated". `ImportDialogGuard` answers it with OK from App's `DialogBoxShowing` handler, only inside the link's scope (`ImportDialogGuard.Begin()`), and only for that text. Other dialogs are recorded and left alone.
- **The out-of-range warning.** Revit posts "Imported model is out of range". `TxGuard.StartSwallowing` on the link transaction deletes it.

A link that rolls back returns null, which keeps the fallback contract.

Dialogs seen inside a scope are logged to `%APPDATA%\RevitWebAppSync\import-dialogs.log`. The match is on English text, so a localised Revit still shows the dialog.

## 6. Measuring tools

### `dump_electrical_reference` (inspect, add-in only, not advertised)

**Args:**

| Arg | Type | Meaning |
|---|---|---|
| `out_path` | string, required | Refused inside any git working tree |
| `level` | string | Only the devices on this level |
| `include_walls` | bool | Adds the structural snapshot's wall centrelines |
| `include_runs` | bool | Adds `runs`: every conduit and cable tray segment (fittings left out), of the level when given |

**Output:** `schema: "cad.electrical.reference/1"` with:
- `devices`: `id`, `category`, `family_type`, `level`, `xyz_mm`, `offset_mm` (from the level), `host_kind` (`wall`, `ceiling`, `face`, `none` or the host's category) and `facing`. A face-hosted device's facing is its host face normal.
- `links`: `name`, `loaded`, `origin_mm`, `basis_x/y/z`
- `walls` (one level) or `walls_by_level`, when `include_walls` is set
- `runs`, when `include_runs` is set: `id`, `kind` (`conduit` or `cable_tray`), `type`, `level`, `start_mm`, `end_mm`, and `diameter_mm` or `width_mm` and `height_mm`
- `cad_imports`: every `ImportInstance` of the document (linked or imported, whatever the level): `id`, `file` (the file name from `DwgReader.PathOf`, null when there is none), `is_linked`, and from `GetTotalTransform()`: `origin_mm`, `basis_length_x/y/z` (unitless) and `scale_guess`. `scale_guess` is `"unit"` when all three lengths are 1 (within 1e-6), the length when all three agree, else the three lengths. It answers whether the total transform carries the drawing's unit scale. A transform that cannot be read gives `transform_error`.
- `device_types`: one row per device `FamilySymbol` among the dumped devices, in first-seen order: `family_type`, `category`, `count`, `sample_id` (the instance read), `has_connector_manager` (its `MEPModel` has a `ConnectorManager`), `connectors` (`id`, `domain` as the Revit enum name, such as `DomainCableTrayConduit` or `DomainElectrical`, `connector_type`, and `direction`, the connector's Z axis) and `has_conduit_connector` (a `DomainCableTrayConduit` connector exists). It answers whether the socket families have conduit connectors.

The tool result adds the `cad_imports` and `device_types` counts.

### Headless

```
cad2bim-headless file.dwg --fit=walls.json [--cluster=N] [--min-wall=50]
cad2bim-headless --compare-devices <ref.json> <placed.json> [--plan=plan.json]
                 [--ref-types=text] [--ref-link=text] [--role=socket]
                 [--across=50] [--along=500] [--dz=150]
                 [--anchor=centre|insert|wall_edge_mid|bbox_min|bbox_max|all] [--json]
cad2bim-headless file.dwg --runs --layers=E-CONDUIT[,E-TRUNK] [--cluster=N] [--min-run=300] [--points-out=runs.json]
cad2bim-headless --compare-runs <ref.json> <placed.json> [--kind=conduit|cable_tray] [--tol=100] [--3d] [--json]
```

**`--runs`** chains each named layer as `plan_cad_electrical` does (section 7), without Revit or a fit, and prints per layer: pieces (and pieces outside the cluster), arcs, runs, length, open ends, junctions and junction ends, bends, free arcs, short runs dropped, closed loops and runs with short legs. `--cluster` keeps the pieces in that census plan's box; without it, the whole sheet. Clutter is dropped and counted as in the plan. `--min-run` sets the shortest run kept (default 300, conduit); pass `--min-run=1000` for trunking layers, as the plan does. The first line prints the values used. No coordinate is printed. `--points-out` writes the runs' points in drawing mm, and is refused inside a git working tree.

**`--compare-runs`** reads the `runs` of two `include_runs` dumps. Each segment is walked in 25 mm steps; a step is covered when it lies within `--tol` (100 mm) of a segment of the other dump, in plan unless `--3d`. It prints the reference length and the share covered by created runs, the created length along the reference, and the extra created length. `--kind` keeps one kind.

**`--fit`** runs `CadModelFit` on a drawing against a dump's walls, without Revit.

**`--compare-devices`** matches reference devices to placed ones one to one, nearest first in plan, so every pair within 50 mm is taken before any pair beyond it. It reports the issue's rule first (section 8):
- **Issue metric:** the reference devices with a placed device within 50 mm (plan distance, one to one), as a share of the reference devices, PASS at 90% or more.
- **Miss codes:** every other reference device gets the first code that applies. None is left blank:

  | Code | When |
  |---|---|
  | `near_<N>mm` | a placed device within 500 mm: its own match beyond 50 mm, else the nearest placed device. The row gives the device and its offset across and along the wall. |
  | `plan_flag:<flag>` | the nearest plan point within 500 mm carries a flag that explains a miss: one that excludes (`no_wall_in_reach`, `already_present`, `duplicate_in_drawing`, `family_host_mismatch`, `no_ceiling`, `no_height`) or `above_wall_top`. `plan_flag:excluded` for an excluded point with none of them. |
  | `dropped:<reason>` | the nearest dropped drawing symbol within 500 mm, with its reason (`not_in_mapping`, `outside_model_footprint`, `other_plan`, `nested_in_mapped`, `ignored_class`...) |
  | `not_in_drawing` | no plan point and no dropped symbol of any class within 1000 mm: the drafter modelled a socket that the drawing does not show. Only when the plan says `dropped_lists_every_symbol`. |
  | `unexplained` | none of these. The rule requires 0. |

  Without `--plan`, only `near_<N>mm` can apply, and the output says so.
- **Extra placements:** a placed device with no reference device within 50 mm is `near_reference` (a reference device within 500 mm) or `not_in_reference` (the drafter did not model it).
- **Exit gate:** PASS when the share passes and `unexplained` is 0.

The rest is a **diagnostic**, not the issue's rule:
- **Match radius:** up to the pass reach, which is hypot(across, along) and at least 500 mm.
- **Outcome:** `hit` within 50 mm, `near` within 300 mm, `far` beyond that, `miss` when unmatched.
- **Split:** each match is split into `across_mm` and `along_mm`.
  - `across_mm` is the offset along the planned point's facing.
  - `along_mm` is the offset perpendicular to it.
  - The facing comes from the plan point at the placed device, else the placed device's dump facing, else the reference's.
  - With no facing, the smaller axis component is taken as across. Such a split cannot tell a move across an axis-aligned wall from a move along one, so give the comparison a facing (a plan or face-correct dumps).
- **Options:**
  - `--ref-types` keeps the devices of both dumps whose family type contains the text.
  - `--ref-link` carries the reference through the placed dump's link of that name.
  - `--role` counts only that role's plan points in the diagnostic's denominator.

**Text output** (invented figures):

```
issue rule: ≥ 90% of sockets placed within 50mm of a hand-modelled reference floor on staging, with every miss explained by a flag (not silent).
reference 40, within 50 mm 36 (90.0%): PASS (needs >= 90%)
misses 4, unexplained 0: PASS (needs 0)
exit gate: PASS
miss codes:
  dropped:not_in_mapping: 1
  near_<N>mm: 1
  not_in_drawing: 1
  plan_flag:already_present: 1
misses:
  ref 2001: near_320mm (placed 6001, across 10 mm, along 320 mm)
  ref 2002: plan_flag:already_present
  ref 2003: dropped:not_in_mapping
  ref 2004: not_in_drawing
extra placements 3 (no reference within 50 mm):
  near_reference: 2
  not_in_reference: 1
diagnostic (not the issue's rule): matches within 502 mm split across and along the wall
  hits 36, near 0, far 1, unmatched 3
  limits: across <= 50 mm, along <= 500 mm, |dz| <= 150 mm
  matched 37: across_ok 37, along_ok 37, height_ok 37, pass 37
  of 38 drawn: pass 37 (97.4%), across_ok 37 (97.4%)
```

**`--json`** has the same parts: `issue` (`rule`, `reference`, `within_tolerance`, `share`, `required_share`, `share_passes`, `misses`, `unexplained`, `gate_passes`), `miss_codes`, `explainers` (`plan`, `every_drawn_symbol`), `extra_placements` (`count`, `codes`), `diagnostic`, then `rows` (each with `within_tolerance`, `miss_code` and the near device's `near_across_mm` and `near_along_mm`) and `extra_rows`.

**Denominator:** the last line says `drawn` when `--plan` is given (every plan point, of `--role` when given, placed or excluded). Otherwise it says `placed`.

**`--json`** prints the full result:
- every row's `across_mm`, `along_mm`, `split`, `pass` and `dz_mm`
- `limits`, `pass_share`, `across_ok_share`, `denominator` and `denominator_is`

### Anchor diagnostic

The plan places a symbol at its drawn centre, which is the centre of its geometry's box, snapped onto the wall face. A drafter may have modelled from another point of the symbol. The anchor diagnostic shows which point that was. It does not change placement.

**In the plan.** With `include_points`, each point has `anchors_mm`. This maps each candidate to the point the resolver would make of it: the same wall, side and face snap, at the point's Z, in model mm. It is `null` when the candidate does not apply. `along_offset_mm` gives each candidate's signed offset along the host wall from `centre`. It is `null` with no host wall.

| Candidate | Point of the symbol |
|---|---|
| `centre` | the drawn centre (what the plan places) |
| `insert` | the block's insertion point. `null` when it is more than 2 times the symbol's extent from the drawn centre, which is a far base point. |
| `wall_edge_mid` | the midpoint of the box side nearest the host wall's face, in the symbol's local frame (rotation and mirror applied). `null` with no host wall. |
| `bbox_min`, `bbox_max` | the local box's min and max corners |

A ceiling or `none` device, or one with no wall in reach, keeps each candidate as drawn.

**In the compare.**
- `--anchor=<name>` re-scores with each placed device moved to that candidate. A device is matched to its plan point, the nearest within 100 mm, one to one, and moves by that point's anchor less its centre. A device with no such point, or whose point lacks the anchor, stays where it is. The first line counts both.
- `--anchor=all` prints the issue metric (within 50 mm, one to one) for every candidate, best first. Both options need a plan that has `anchors_mm`.

Output on a synthetic case where the reference sockets stand at the insert points (one point's insert is far, and its reference stands at its centre):

```
anchor diagnostic (not the issue's rule, placement unchanged): the placed side moved to each candidate anchor
anchor         within 50 mm            moved  kept  unexplained
insert         4/4 (100.0%)                3     1            0
centre         1/4 (25.0%)                 4     0            0
wall_edge_mid  1/4 (25.0%)                 4     0            0
bbox_min       0/4 (0.0%)                  4     0            0
bbox_max       0/4 (0.0%)                  4     0            0
```

## 7. Conduit and trunking runs (phase 2)

Conduit and trunking are drawn as lines and polylines on their own layers. A `conduit` layer becomes Revit conduit with drops to the devices; a `trunking` layer becomes Revit cable tray, with no drops.

### Planning (inside `plan_cad_electrical`)

For each conduit or trunking row, in mapping order:

1. **Unplanned rows.** A row without its type, its size or a run height plans nothing (section 2). The block's `status` is `needs_run_height` when a run height is missing, else `planned`.
2. **Pieces.** The layer's straight pieces and arcs, without symbol linework (blocks up to 2000 mm across). A piece belongs to the plan by the symbols' rule: its midpoint, carried onto the model, lies inside the level's footprint. Without a footprint, it lies inside the cluster's box grown by 400 mm. The rest are counted as `pieces_outside_footprint`.
3. **Clutter.** Run layers also carry annotation: ticks, arrows, short leaders. Before chaining, a piece is dropped as clutter when all three hold:
   - it is shorter than 1000 mm
   - neither of its ends is within 20 mm of another piece
   - it is more than 5 degrees off the layer's main axes. These are the direction most of the layer's length runs along (1 degree bins, modulo 90) and the direction at 90 degrees to it.

   A short axis-aligned stub stays, and so does a short piece touching a run. Dropped pieces are counted as `clutter_dropped`.
4. **Chaining** (`CadRunChain`, drawing mm):
   - Pieces along one line join when their ends are within 20 mm. If that merge would move an end by more than 20 mm, the pieces are chained as drawn.
   - An arc of radius up to 1000 mm whose ends meet two pieces within 20 mm, each along the arc's tangent (within 5 degrees), is a bend. The two pieces are extended to their intersection, which becomes the corner. Any other arc is a free arc: counted, not chained.
   - Ends within 20 mm are one node. A node within 20 mm of another piece's interior splits that piece there, so a branch meets the run it touches. Two pieces that cross with no end at the crossing stay apart.
   - At a junction (three or more pieces at one node), every piece ends there, as a `junction` end: a run that went straight through is split. `route_conduit` then joins the ends that meet there (below).
   - Vertices turning less than 1 degree are removed. A run shorter than the kind's minimum is dropped and counted (`dropped_short`): 300 mm for conduit, 1000 mm for cable tray (trunking). A run with a junction at both ends is kept however short: it is part of the network.
   - Runs are ordered by layer, then lowest x, then lowest y, so indices are the same from call to call.
5. **Planning** (`CadRunPlan`, model mm):
   - Each point is carried onto the model by the fit. Z is the level plus the run height: the row's, else the call's.
   - **Drops (conduit only).** Each open end looks for a device within 300 mm. Devices are the plan's points that are not excluded, and the model's devices between this level and the next. Distance is to the device's point, or to its drawn symbol's edge, whichever is nearer. A drawn run stops at the symbol, while the point sits on the wall face.
   - The nearest device wins. Another within 50 mm of that distance flags `device_ambiguous`.
   - An end within 150 mm of the device moves onto it. A farther end gets a horizontal jog to it, unless the jog runs straight on, in which case the last leg is lengthened. Then a vertical leg goes to the device's height: the point's Z, or an existing device's contact height. The drop sits at the wall face, where the device point is.
   - One drop per device. A later end at the same device stops at the top of that drop (`shares_drop`). A vertical under 300 mm is left out (`drop_too_short`).

### Run flags

| Flag | Default |
|---|---|
| `open_end` (a conduit end reaches no device) | kept |
| `junction` (an end at a junction) | kept |
| `shares_drop` | kept |
| `short_leg` (a leg under 150 mm: its elbows may not fit) | kept |
| `drop_too_short` | kept |
| `device_ambiguous` | kept |
| `provisional_alignment` | kept |
| `closed_loop` | excluded |

### The conduit block (`review.conduit`)

Present when the mapping has a conduit or trunking row. It carries counts, names and run indices only, never a coordinate.

```json
{
  "status": "planned",
  "runs": 12, "will_route": 12, "total_length_m": 84.0, "drops": 30,
  "open_ends": 4, "junctions": 2,
  "by_layer": [{ "layer": "E-CONDUIT", "kind": "conduit", "type": "Conduit with Fittings : PVC",
                 "diameter_mm": 20, "width_mm": null, "height_mm": null, "run_height_mm": 2700,
                 "runs": 12, "will_route": 12, "length_m": 84.0 }],
  "flagged": [{ "flag": "open_end", "default": "kept", "count": 4, "runs": [1, 5, 7, 9], "truncated": false }],
  "unplanned_layers": [{ "layer": "E-CONDUIT-2", "reason": "no_run_height", "pieces": 40, "length_m": 81.2 }],
  "dropped_short": 3,
  "free_arcs": 0,
  "pieces_outside_footprint": 11,
  "clutter_dropped": 7
}
```

- `status`: `planned` or `needs_run_height`.
- `open_ends` counts conduit ends that reach no device. Closed loops and tray runs are not counted.
- `junctions` counts run ends at a junction.
- `flagged.runs` lists up to 50 run indices per flag; `count` is always the full count.

### `route_conduit` (mutate)

There are two modes. Passing both, or neither, is an error.

**`points_mm` mode** builds one run.

| Arg | Type | Meaning |
|---|---|---|
| `points_mm` | `[[x, y, z], ...]` mm, required | At least 2 points |
| `level` | string, required | Level name |
| `conduit_type_name` | string | `Family : Type` or a unique type name. Default: the first conduit type with fittings |
| `diameter_mm` | number | Must be one of the type's sizes |
| `tray_type_name` | string | Builds cable tray instead (trunking). Not with `conduit_type_name` |
| `width_mm`, `height_mm` | number | Cable tray size |
| `connect_start_to`, `connect_end_to` | element id | Snap that end to the element's free connector within 50 mm |

The run is built in one transaction, with an elbow at each corner. The result has the same keys as `route_pipe`: `ok`, `new_ids`, `fitting_ids`, `total_length_mm`, `open_connectors {count, origins_mm}` and `warnings`. A failure rolls the run back and returns `{ok: false, error, failed_corner_index}`.

**`plan_id` mode** builds a drawing plan's runs.

| Arg | Type | Meaning |
|---|---|---|
| `plan_id` | string, required | From `plan_cad_electrical`, for the same document |
| `run_indices` | int[] | Explicit runs. These win even over excluded runs. Otherwise every run that is not excluded. |

1. **Types.** Before any transaction, each distinct type is resolved once:
   - A run with corners needs a with-fittings type. A conduit type also needs a Bend assigned.
   - A conduit diameter must be one of the type's sizes.
   - A type error fails every run that uses it.
2. **Skips.** A run whose created elements all still exist is skipped as `already_routed`. After an undo, it is built again.
3. **Transactions.** One `TransactionGroup`, with one transaction per run. A run that fails rolls back alone. The group is assimilated when at least one run was built, so the call is one undo step.
4. **Device ends (conduit).** The device is found by:
   - the run end's element id (an existing device), else
   - the plan point's placed device (`place_socket_points` records it), else
   - a device within 150 mm of the planned point.
   
   If the device has a free conduit connector within 300 mm, and that connector's axis is within 30 degrees of vertical, the drop is moved onto the connector and connected. Otherwise the drop stays open, with a warning:
   - `device_not_placed`: no device was found. Place the devices first.
   - `no_drop`: the end has no vertical leg.
   - `no_conduit_connector`
   - `connector_not_vertical`

**Result:**
- `ok`: true when a run was built, or when none failed
- `plan_id`, `runs_created`, `runs_failed`
- `created`: `index`, `new_ids`, `fitting_ids`, `length_mm`, `start` and `end` (`kind`, `point_index`, `element_id`, `connected`)
- `failed`: `index`, `reason`, `failed_corner_index`
- `skipped`: `index`, `reason`
- `new_ids`, `fitting_ids`, `total_length_mm`, `open_connectors`, `warnings`: flat over all the runs, as `route_pipe` returns them

No runs selected gives `ok: false`. If the plan has no runs at all, the error says to run `plan_cad_electrical` with `run_height_mm`.

**Junctions (plan mode):** after the runs are built, the ends of the runs built in that call that meet at one junction (same type and level, within 20 mm) are joined in one transaction each, inside the call's group: two running straight on are connected, two at an angle get an elbow, three a tee (the two most opposite are the main), four a cross. More than four, or a fitting Revit refuses, is a warning, and the runs stay built. A junction whose other runs were dropped as short keeps its one open end.

**Tray fittings (plan mode):** before routing, the cable tray type's routing fittings are read (horizontal bend or Bend, Tee, Cross, Union). A type with none is swapped for the project's tray type with the most fittings (one with a bend first), and a warning names both. When no tray type has any, only straight runs are built: a run with a corner is `skipped` with the reason, and every join that needs a fitting is a warning ("tray type X has no fittings; joins skipped"). A join the type lacks the fitting for (no tee, no cross) is skipped the same way. A corner elbow that fails is rolled back alone in its own sub-transaction and warned about; the run's legs stay.

### `place_cad_electrical` (mutate)

The one write after `plan_cad_electrical`: devices and runs together, behind one confirmation card.

| Arg | Type | Meaning |
|---|---|---|
| `plan_id` | string, required | From `plan_cad_electrical`, for the same document |
| `roles` | string[] | Device filter, as for `place_socket_points` |
| `indices` | int[] | Explicit device points, as for `place_socket_points` |
| `run_indices` | int[] | Explicit runs, as for `route_conduit` |
| `include_runs` | bool, default true | False places the devices only |

1. **Devices first**, with exactly the `place_socket_points` logic (section 4) for the plan's points and the `roles` / `indices` selection. The plan's own family types and mount heights are used; there is no `family_type`, `level`, `mount_height_mm` or `facing_offset_deg` override. Each placed point records its device id, so the drops of step 2 connect to the devices just placed.
2. **Then the runs**, with exactly the `route_conduit` plan-mode logic for the selected runs. Skipped when the plan has no runs or `include_runs` is false.
3. **One undo.** Both steps keep their own `TransactionGroup`, inside one outer group (`BinaVibe: place_cad_electrical`). The outer group is assimilated when a device or a run was built, else rolled back. One Ctrl+Z undoes the whole write.

A device error that `place_socket_points` would throw (no family type in the document, for example) does not stop the runs: `devices` is then `{ok: false, error}`, and the drops stay open with `device_not_placed`.

**Result:**

```json
{
  "ok": true,
  "plan_id": "socket_plan:…",
  "devices": { "…": "the place_socket_points result (section 4)" },
  "runs": { "…": "the route_conduit plan-mode result, or {\"skipped\": \"no_runs\"}" },
  "review": {
    "headline": "Devices: 22 placed, 0 failed, 2 already present. Conduit: 12 runs routed, 0 failed, 0 skipped, 84.0 m.",
    "devices": { "…": "devices.review" },
    "open_connectors": { "count": 4, "origins_mm": [] },
    "run_warnings": 4
  },
  "new_ids": [1001, 1002, 2001]
}
```

- `ok`: true when either half succeeded (`devices.ok`, or `runs.ok` when the runs were not skipped). When neither did, `error` repeats the headline.
- `new_ids`: flat, for the receipt: the devices' `created_id`s, then the runs' segment ids, then their fitting ids.
- `review.open_connectors` is the runs' (null when skipped); `run_warnings` counts `runs.warnings`.

## 8. Exit check procedure

Keep every file in a private folder outside all repositories. Record numbers only: no client names, layer or block names, or drawing content in any repo, issue or commit.

1. Build the add-in and load it in Revit.
2. Open the architectural model and save a working copy. Check that the target level has rooms.
3. Load the drafter's socket family into the working copy.
4. In the drafter's electrical model, run `dump_electrical_reference` with `out_path=<private>\ref.json` and `level="Level 1"`.
5. In the working copy, run `dump_electrical_reference` with `include_walls=true`. Run the headless `--fit` on the DWG until the right cluster scores 0.80 or more.
6. Attach the DWG and run `read_cad_electrical`. Write `mapping.json` with:
   - the socket classes, family type and legend heights
   - the background layers
   - the cluster and level
7. Run `plan_cad_electrical` with `include_points=true` and save the result as `plan.json`. Check the alignment score, the counts and the flags. Resolve `plan_ambiguous` with `pick`.
8. Run `place_socket_points` with the `plan_id` and `roles=["socket"]`.
9. Run `dump_electrical_reference` with the level again and save it as `placed.json`.
10. Run the comparison:

    ```
    --compare-devices ref.json placed.json --plan=plan.json --role=socket --ref-types=<socket type text>
    ```

    Add `--ref-link` when the reference is a link of the working copy.

**The rule** (Linear AI-390, verbatim): "Phase 1 proof case (this is the Phase 1 exit gate): ≥ 90% of sockets placed within 50mm of a hand-modelled reference floor on staging, with every miss explained by a flag (not silent)."

**Criteria**, as the comparison reports them first:
- **Share:** the reference sockets with a placed socket within 50 mm in plan, matched one to one, divided by the reference sockets. It passes at 90% or more.
- **Every miss explained:** each reference socket without a placed socket within 50 mm gets a miss code (`near_<N>mm`, `plan_flag:<flag>`, `dropped:<reason>`, `not_in_drawing`), see `--compare-devices` above. The count of `unexplained` must be 0. Give the comparison the `include_points` plan, or only `near_<N>mm` can apply; a plan without `dropped_lists_every_symbol` cannot show `not_in_drawing`.
- **Exit gate:** both hold.
- **Also report** the extra placements and their codes (`near_reference`, `not_in_reference`).

**Diagnostic** (not the issue's rule; it shows why sockets are off, not whether the gate passes):
- The denominator is the sockets **drawn** on that floor's plan, which are the plan's socket points, placed or excluded.
- A match passes the split when all three hold:
  - across ≤ 50 mm: the right wall and the right face
  - along ≤ 500 mm: the drafter's shift along the wall
  - |dZ| ≤ 150 mm
- It reports the pass share (pass ÷ drawn), the across-only share (across_ok ÷ drawn, the right wall and face whatever the along shift and height), and the host agreement.

**Manual checks:**
- Look at 10 sockets in plan and 3D: on the right wall face, facing the room, at the right height.
- Press Ctrl+Z once and everything goes. Redo and everything comes back.
- Run step 8 again: everything is skipped as `already_present`.

### Conduit and trunking (phase 2)

Run this on the phase 1 working copy, with the devices already placed. Keep every file private, as above.

1. Run the headless `--runs` on the drawing's run layers, without Revit. Check that the run count and length look like the drawing.
2. Build the add-in and open the working copy.
3. Check that the model has a Conduit with Fittings type, with a Bend assigned and the wanted size. For trunking, check for a Cable Tray with Fittings type. Load one if needed.
4. Add the conduit and trunking rows to `mapping.json`. Run `plan_cad_electrical` without `run_height_mm`: expect `needs_run_height`, and the points unchanged.
5. Run it again with `run_height_mm` and `include_points=true`, and save the result as `plan.json`. Check the runs, length, drops, open ends and junctions against the drawing.
6. Run `route_conduit` with the `plan_id`. On a fresh working copy, run `place_cad_electrical` with the `plan_id` instead: check one confirmation card, devices and runs both built, and one Ctrl+Z undoing both.
7. **Pass:**
   - created + failed + skipped = the runs selected
   - every failed run has a reason
   - the total created length is within 2% of the planned length
8. Look at 5 runs in plan and in 3D. Each should be on the drawn line and at the run height, with elbows at the corners and drops at the devices.
9. Press Ctrl+Z once: every run goes. Redo: they come back.
10. Run step 6 again: every run is skipped as `already_routed`.
11. Run `route_conduit` once in `points_mm` mode with a type without fittings and three points. Expect a clear error and no elements. Then run it with a type with fittings: two legs and one elbow.
12. In the drafter's model and in the working copy, run `dump_electrical_reference` with `include_runs=true` and the level. Then compare:

    ```
    --compare-runs ref.json placed.json --kind=conduit
    --compare-runs ref.json placed.json --kind=cable_tray
    ```

    Record the reference share covered, the created share along the reference, and the extra length.
