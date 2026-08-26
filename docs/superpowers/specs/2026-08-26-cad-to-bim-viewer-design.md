# CAD-to-BIM Viewer with AI Clarification

**Date:** 2026-08-26
**Repos:** revit-addin-sync (MCP tools), bina-ai (engine routes + classifier)
**Status:** Design approved, ready for implementation

## Problem

CAD-to-BIM workflow wastes AI tokens building wrong models. User attaches DWG, AI guesses wall layers, builds Revit walls. Wrong guess = wasted computation + manual cleanup.

**Solution:** Visual preview + AI clarification BEFORE building. User sees CAD geometry, AI asks questions ("Is 'A-WALL' the wall layer?"), user confirms, THEN walls are created.

## Architecture

```
Browser                     Engine (Python :48810)    Addin (C# :48820)
┌────────────────┐         ┌──────────────┐          ┌─────────────┐
│ libredwg-web   │ JSON    │ /cad/* routes│  HTTP    │ McpServer   │
│ Canvas viewer  │◄───────►│ classifier   │◄────────►│ cad_* tools │
│ AI chat sidebar│         │ stitcher     │          │ ACadSharp   │
└────────────────┘         └──────────────┘          └─────────────┘
```

- **Viewing:** libredwg-web (WASM) parses DWG directly in browser
- **Classification:** Python port of ALCM scoring + centerline stitcher
- **DWG reading for processing:** ACadSharp via existing McpServer
- **Wall creation:** Existing `cad_create_walls` tool

## MCP Tools (C# — revit-addin-sync)

### `cad_load`

Load DWG metadata via ACadSharp.

**Input:**
```json
{"dwg_ref": "attachment_abc123"}
```

**Output:**
```json
{
  "ok": true,
  "layers": ["WALL", "DOOR", "FURNITURE", "GRID"],
  "entity_counts": {"Line": 2340, "Arc": 127, "Circle": 45},
  "bounds_mm": {"min": [0, 0], "max": [45000, 32000]},
  "source_app": "plain_autocad"
}
```

### `cad_get_lines`

Extract line/arc geometry for classification.

**Input:**
```json
{"dwg_ref": "attachment_abc123", "layer_filter": "WALL"}
```

**Output:**
```json
{
  "ok": true,
  "lines": [
    {"x1": 0, "y1": 0, "x2": 5000, "y2": 0, "z": 0, "layer": "WALL"},
    ...
  ],
  "arcs": [
    {"cx": 1000, "cy": 500, "r": 800, "start_deg": 0, "end_deg": 90, "layer": "DOOR"}
  ]
}
```

### `cad_create_walls`

Create Revit walls from confirmed centerlines. Reuses existing `CadWallsFromAttachment` logic.

**Input:**
```json
{
  "centerlines": [
    {"ax": 0, "ay": 0, "bx": 5000, "by": 0, "thickness_mm": 200}
  ],
  "level": "Level 1",
  "wall_type": "Generic - 200mm"
}
```

## Engine Routes (Python — bina-ai)

New module: `app/engine/cad/`

### Files

```
app/engine/cad/
├── __init__.py
├── routes.py         # FastAPI router, mounted in engine main
├── classifier.py     # ALCM scoring: layers → target classification
├── stitcher.py       # Centerline solver: parallel pairs → centerlines
├── executor.py       # HTTP calls to McpServer cad_* tools
└── static/
    └── viewer.html   # libredwg-web viewer + AI chat sidebar
```

### Routes

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `GET /cad/viewer` | GET | Serve viewer HTML |
| `POST /cad/load` | POST | Call `cad_load` tool, cache result |
| `POST /cad/classify` | POST | Run ALCM on cached geometry |
| `POST /cad/preview` | POST | Run stitcher, return proposed walls |
| `POST /cad/clarify` | POST | AI asks/answers questions (SSE stream) |
| `POST /cad/confirm` | POST | Lock classification, call `cad_create_walls` |

### Session State

In-memory dict keyed by `session_id`:
- `dwg_ref`: attachment reference
- `layers`: cached layer list
- `lines`: cached line geometry
- `classification`: current layer → target mapping
- `proposed_walls`: centerline results
- `confirmations`: user answers to questions

## Viewer UI

Single HTML file with embedded JS. Uses `@mlightcad/libredwg-web` for DWG parsing.

### Layout

```
┌─────────────────────────────────────┬──────────────────┐
│                                     │ Layers           │
│         CAD Canvas                  │ ☑ WALL (234)     │
│         (pan/zoom/highlight)        │ ☐ DOOR (45)      │
│                                     │ ☐ FURNITURE (89) │
│                                     ├──────────────────┤
│                                     │ AI Assistant     │
│                                     │                  │
│                                     │ "Is 'WALL' the   │
│                                     │  wall layer?"    │
│                                     │                  │
│                                     │ [Yes]  [No]      │
│                                     ├──────────────────┤
│                                     │ [Create Walls]   │
└─────────────────────────────────────┴──────────────────┘
```

### Features

- **Pan/zoom:** Mouse drag + wheel
- **Layer toggle:** Checkbox per layer
- **Highlight:** Classified entities shown in color overlay
- **AI chat:** Questions from `/cad/clarify`, answers posted back

## AI Clarification Flow

### Question Types

1. **Layer confirmation** — "Layer 'A-WALL' has 234 lines. Is this the wall layer?"
2. **Count validation** — "Found 47 wall segments after stitching. Expected?"
3. **Thickness range** — "Detected 100-300mm thickness. Correct for this project?"
4. **Ambiguity** — "Two layers match wall patterns: 'WALL' and 'A-WALL-EXT'. Which?"
5. **Door layer** — "Layer 'DOOR' has 23 swing arcs. Is this doors?"

### Confidence Thresholds

Questions generated based on classifier confidence:
- Score > 0.9: Skip question, auto-accept
- Score 0.7-0.9: Ask confirmation (yes/no)
- Score < 0.7: Ask open question (which layer?)

### Flow

```
1. User attaches DWG in Copilot pane
2. Engine calls cad_load → get layers
3. Viewer opens in browser (localhost:48810/cad/viewer?session=xxx)
4. libredwg-web renders DWG geometry
5. Engine runs classifier → generates questions
6. SSE streams questions to viewer sidebar
7. User answers (click or type)
8. Classifier updates → next question or done
9. User clicks "Create Walls"
10. Engine calls cad_create_walls → walls appear in Revit
```

## Classification Algorithm (ALCM)

Port of friend's Automatic Layer Classification Method.

### Targets

- **DoorWindow:** Focal element = swing arc (~90°, 600-1000mm radius)
- **Wall:** Focal element = end cap (line in thickness range), related = perpendicular faces

### Scoring

```
score = (N(SC_true) + 1) / (N(SC_total) + 1)
```

Where:
- SC = Sufficient Conditions (e.g., "is perpendicular", "length in range")
- Layer with highest total score wins

### Stitcher

After classification, runs centerline extraction:

1. Filter segments by classified wall layer
2. Stitch collinear segments across door gaps (max 1500mm)
3. Pair parallel segments within thickness range (50-500mm)
4. Compute centerline by averaging paired faces
5. Snap corner junctions

## Implementation Phases

| Phase | Deliverable | Est. |
|-------|-------------|------|
| 1 | `cad_load`, `cad_get_lines` MCP tools | 1 day |
| 2 | Engine `/cad/*` routes + executor | 1 day |
| 3 | Port ALCM classifier to Python | 2 days |
| 4 | Port stitcher/centerline solver to Python | 2 days |
| 5 | Viewer HTML with libredwg-web | 2 days |
| 6 | AI clarification integration | 1 day |
| 7 | End-to-end testing | 1 day |

**Total:** ~10 days

## Dependencies

### NPM (viewer)

- `@mlightcad/libredwg-web` — DWG parsing in browser

### Python (engine)

- No new deps — uses existing FastAPI, httpx for McpServer calls

### C# (addin)

- No new deps — uses existing ACadSharp

## Open Questions

1. **Viewer hosting:** Serve from engine or separate static server?
   - Decision: Engine serves at `/cad/viewer` (simplest)

2. **Session persistence:** In-memory or SQLite?
   - Decision: In-memory for now, SQLite if sessions need to survive restart

3. **Multi-floor DWGs:** How to handle Z filtering?
   - Decision: Add Z range selector to viewer UI

## References

- Friend's ALCM implementation: `/Users/adham/Downloads/cad2bim/`
- Existing CAD tools: `revit-addin-sync/BinaVibe/Mcp/Tools/CadWallsFromAttachment.cs`
- Colocate spec: `docs/superpowers/specs/2026-07-08-copilot-engine-colocate-design.md`
