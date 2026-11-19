# CAD electrical tools: contract and exit check

Phase 1 of the CAD electrical to BIM work. A DWG electrical layout becomes Revit devices in three tool calls:

1. `read_cad_electrical` reads the drawing.
2. `plan_cad_electrical` turns an AI-written mapping into a stored plan.
3. `place_socket_points` commits the plan.

`dump_electrical_reference` and the headless `--compare-devices` and `--fit` commands measure the result.

All names in this document are invented: SOCKET, LIGHT-A, A-WALL, E-POWER, "Level 1".

Code:
- `BinaVibe/Mcp/Tools/Electrical/`: `CadElectricalTools.cs`, `CadElectricalMapping.cs`, `CadModelFit.cs`, `CadSymbolResolve.cs`, `CadElectricalResolver.cs`, `CadElectricalReview.cs`, `SocketPlacement.cs`, `PlacementCompare.cs`
- `BinaVibe/Mcp/Tools/ImportDialogGuard.cs`
- `Cad2Bim/Cad2Bim.Headless/ElectricalCheckCommands.cs`

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
  "layer_roles": ["background", "conduit", "wiring_schematic", "ignore", "unknown"],
  "model": {
    "levels": [{ "name": "Level 1", "elevation_mm": 0.0 }],
    "family_types": [{ "family_type": "Socket Outlet : Single", "category": "Electrical Fixtures", "hosting": "face" }],
    "family_types_total": 1,
    "family_types_truncated": false
  },
  "notes": []
}
```

**Slim census limits:** at most 80 block classes and 60 layers. Each layer has name, inserts, length, long open polylines and the wall hint (`pairs_like_walls`).

**`hosting` values:**
- From the family's Host setting: `wall`, `ceiling`, `floor`, `roof`, `face`.
- Otherwise from the placement type: `work_plane`, `hosted`, `none`.

**`family_types`:** up to 200 types from the device categories:
- electrical fixtures and equipment
- lighting fixtures and devices
- communication, data and telephone devices
- fire alarm, security and nurse call devices

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
- `conduit` is accepted and noted as `conduit_deferred` (phase 2).

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

### Incomplete rows (do not reject)

A placeable row plans zero points and is listed under `unmapped_classes` with its instance count and a reason:

| Reason | Meaning |
|---|---|
| `role_unknown` | The row's role is `unknown` |
| `no_family_type` | The row has no family type |
| `no_mount_height` | A wall-hosted row has no mount height |
| `not_in_mapping` | A symbol-sized, non-container class off the wall-like layers that the mapping omits |

## 3. `plan_cad_electrical` (inspect)

**Args:**

| Arg | Type | Meaning |
|---|---|---|
| `dwg_ref` | string, required | As for `read_cad_electrical` |
| `mapping` | object or JSON text, required | Section 2 |
| `level` | string | Overrides the plan rows and the fit's choice |
| `cluster` | int | Census plan cluster; else the only plan row with a level, else the only cluster, else `cluster_needed` |
| `pick` | int | Chooses one of several identical copies of the floor (see rivals) |
| `run_height_mm` | number | Phase 2 (conduit); noted and ignored |
| `include_points` | bool | Diagnostic: adds `points` and `dropped`, at most 2000 rows each. Not exposed to the AI. |

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
   - Under 0.50 the call fails with `alignment_failed` and tells the user to link the DWG, move it onto the building and call again.
6. **Resolve each placeable symbol inside the footprint** to a point (`CadSymbolResolve`), then flag duplicates.
7. **Store the plan** (`socket_plan:` id, 2 hours) and return the review.

**Fit outcomes:** `accepted` at a score of 0.80 or more, `provisional` at 0.50 or more, otherwise `failed`. A provisional fit flags every point `provisional_alignment` and adds a note.

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
- counted as another plan's when it is in a different cluster.

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
| `already_present` (an existing device within 150 mm in 3D) | excluded |
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
      "outcome": "accepted", "method": "fit", "pick": null
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
    "wet_room_keywords": ["…"]
  }
}
```

The review carries no coordinates.

**`include_points`** adds:
- `points`: `index`, `role`, `block`, `room`, `host`, `xyz_mm`, `facing`, `flags`, `excluded`
- `dropped`: `block`, `reason`, `xyz_mm`

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

**Transactions:** one `TransactionGroup` with one transaction per point, then `Assimilate`. The whole run is a single undo, and a refused point fails alone.

**No doubling:** before each point, devices that exist *now* within 150 mm (3D) skip the point under `skipped_already_present`. A point picked by index keeps its place. A ceiling point is compared at the ceiling's underside, so a socket on the wall under a light is a different device.

**Placement by family:**

| Family | How it is placed |
|---|---|
| Face-based (work plane) with a local host wall | On the wall side face whose normal looks the way the point faces, through the face-reference overload. If refused, the wall overload is used. |
| Wall-hosted | Wall overload. A point with no local wall (`linked_wall`) fails with advice to use a face-based or unhosted family. |
| Unhosted | Level overload |

**Points not on a wall** (ceiling and `none` hosts) are placed by the family's `FAMILY_HOSTING_BEHAVIOR`:

| Family | How it is placed |
|---|---|
| Ceiling-hosted (or another host-based kind) | `NewFamilyInstance(point, symbol, ceiling, level, NonStructural)` on the plan's ceiling. With no ceiling, the point fails alone with `no_ceiling` and is never placed free-standing. |
| Face-based or work-plane-based | On the ceiling's bottom face over the point, through the face-reference overload, with the reference direction along X. With no ceiling, or if Revit refuses the face, the level overload is used. |
| Wall-hosted | Fails alone, because there is no wall. |
| Unhosted | Level overload, then the elevation is set from the level. |

These points have no facing, so `facing_method` is `not_applicable`. On a ceiling the host sets the height. It is read back, and `elevation_set_via` is `insertion_point` when it matches the plan, else `host`. Each item also carries `z_mm` (as placed), `target_z_mm` and `host_ceiling_id`.

**Facing readback:**
- **Wall-hosted and unhosted:** `FacingOrientation`, corrected by flip, then by rotation. A correction that fails is reported as `uncorrectable`.
- **Face-hosted:** the host face's outward normal at the point. If the face cannot be read, the instance's work-plane normal (`GetTotalTransform().BasisZ`) is used. `FacingOrientation` lies in the face, so it is not used. These instances are measured and never flipped or rotated, so they report `as_placed` or `uncorrectable`.

**Height:** the height is read back. If it is wrong, it is set through the elevation parameters. `elevation_set_via` is `insertion_point`, the parameter used, or `unset`.

**Result:**
- Every existing key is kept: `ok`, `plan_id`, `count`, `created[]`, `failed[]`, `facing_uncorrected(_indices)`.
- Each `created` item has `index`, `created_id`, `host` (`wall_face`, `wall`, `ceiling`, `ceiling_face` or `unhosted`), `facing`, `facing_target`, `facing_error_deg`, `facing_method` and `family_type`.
- New keys: `by_role`, `by_family_type`, `skipped_excluded`, `skipped_already_present` and `review`.
- `review` is `PlaceReview`: a headline and `needs_review` counts of `facing_uncorrected`, `elevation_unset`, `no_room`, `wet_room` and `provisional_alignment`.

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

**Output:** `schema: "cad.electrical.reference/1"` with:
- `devices`: `id`, `category`, `family_type`, `level`, `xyz_mm`, `offset_mm` (from the level), `host_kind` (`wall`, `ceiling`, `face`, `none` or the host's category) and `facing`. A face-hosted device's facing is its host face normal.
- `links`: `name`, `loaded`, `origin_mm`, `basis_x/y/z`
- `walls` (one level) or `walls_by_level`, when `include_walls` is set

### Headless

```
cad2bim-headless file.dwg --fit=walls.json [--cluster=N] [--min-wall=50]
cad2bim-headless --compare-devices <ref.json> <placed.json> [--plan=plan.json]
                 [--ref-types=text] [--ref-link=text] [--role=socket]
                 [--across=50] [--along=500] [--dz=150] [--json]
```

**`--fit`** runs `CadModelFit` on a drawing against a dump's walls, without Revit.

**`--compare-devices`** matches reference devices to placed ones one to one, nearest first in plan:
- **Match radius:** up to the pass reach, which is hypot(across, along) and at least 300 mm.
- **Outcome:** `hit` within 50 mm, `near` within 300 mm, `far` beyond that.
- **Split:** each match is split into `across_mm` and `along_mm`.
  - `across_mm` is the offset along the planned point's facing.
  - `along_mm` is the offset perpendicular to it.
  - The facing comes from the plan point at the placed device, else the placed device's dump facing, else the reference's.
  - With no facing, the smaller axis component is taken as across. Such a split cannot tell a move across an axis-aligned wall from a move along one, so give the comparison a facing (a plan or face-correct dumps).
- **Misses:** an unmatched reference device is explained by the nearest plan point's flag (`flag:<flag>`), else the nearest dropped instance's reason (`dropped:<reason>`), else `unexplained`.
- **Options:**
  - `--ref-types` keeps the devices of both dumps whose family type contains the text.
  - `--ref-link` carries the reference through the placed dump's link of that name.
  - `--role` counts only that role's plan points in the denominator.

**Text output** (invented figures):

```
reference 40, hits 22, near 9, far 5, misses 4, unexplained 1, extra placements 2
  miss flag:already_present: 2
  miss dropped:outside_model_footprint: 1
  miss unexplained: 1
limits: across <= 50 mm, along <= 500 mm, |dz| <= 150 mm
matched 36: across_ok 33, along_ok 35, height_ok 36, pass 32
of 38 drawn: pass 32 (84.2%), across_ok 33 (86.8%)
```

**Denominator:** the last line says `drawn` when `--plan` is given (every plan point, of `--role` when given, placed or excluded). Otherwise it says `placed`.

**`--json`** prints the full result:
- every row's `across_mm`, `along_mm`, `split`, `pass` and `dz_mm`
- `limits`, `pass_share`, `across_ok_share`, `denominator` and `denominator_is`

## 7. Exit check procedure

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

**Criteria:**
- **Denominator:** the sockets **drawn** on that floor's plan, which are the plan's socket points, placed or excluded. Each one is matched one to one to the nearest reference device.
- **A match passes** when all three hold:
  - across ≤ 50 mm: the right wall and the right face
  - along ≤ 500 mm: the drafter's shift along the wall
  - |dZ| ≤ 150 mm
- **Report:**
  - the **pass share** (pass ÷ drawn)
  - the **across-only share** (across_ok ÷ drawn), which counts the right wall and face whatever the along shift and height
  - the count of `unexplained` misses
  - the host agreement

**Manual checks:**
- Look at 10 sockets in plan and 3D: on the right wall face, facing the room, at the right height.
- Press Ctrl+Z once and everything goes. Redo and everything comes back.
- Run step 8 again: everything is skipped as `already_present`.
