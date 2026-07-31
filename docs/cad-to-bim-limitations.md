# CAD-to-BIM: Revit Link Limitations & Alternatives

Analysis of what Revit's CAD link API can and cannot extract, and when to use
alternative paths (ezdxf, ODA, APS).

## Revit link limitations by element type

| Element | What you need from CAD | Revit link gives | Gap |
|---------|----------------------|------------------|-----|
| **Walls** | Line pairs, layer | Lines + layer | None — working now |
| **Doors** | Block name, insertion point, rotation, width/height | Insertion + rotation | **Block name** (can't map `DR-900` → `Door 900mm`), **attributes** (width/height stored in block) |
| **Windows** | Block name, insertion, width/height/sill | Insertion + rotation | Same as doors |
| **Furniture** | Block name, insertion, rotation | Insertion + rotation | **Block name** (can't map `CHAIR-01` → family) |
| **Fixtures (WC, sink)** | Block name, insertion | Insertion + rotation | Same |
| **Rooms** | Boundary + room name text | Boundary lines | **Text content** — room name is curves, not string |
| **Dimensions** | Measurement values | Nothing usable | Just geometry — no numeric value |
| **Roof** | Outline + slope annotations | Outline lines | **Text** (slope angle), **hatch** (if roof shown as hatch pattern) |
| **Stairs** | Outline + riser count text | Lines | **Text** (riser count, width) |
| **Annotations** | All text labels | Nothing | Text = curves, not readable |
| **Hatches** | Pattern boundary | Boundary only | **Pattern name** (can't tell "concrete" vs "insulation") |
| **Columns** | Rectangle/circle, layer | Full geometry | Works if on own layer |
| **Grids** | Lines + grid label text | Lines | **Text** (grid names A, B, 1, 2) |

## The three critical gaps

### 1. Block names — doors/windows/furniture

```
CAD block "DR-900" at (5000, 3000), rotation 90°
                    │
        Revit link API sees:
                    │
        GeometryInstance at (5000, 3000), rotation 90°
        Symbol.Name = ??? (often generic, not "DR-900")
```

**Problem:** Can't reliably match `DR-900` → Revit family `Single Door 900mm`.

**Workarounds:**
- Layer (all doors on `A-DOOR`) — identifies class, not type
- Geometry size (measure the rectangle = width) — fragile
- Position in wall (host assignment) — doesn't help with type

**Result:** Door placement works, **door type selection = guesswork**.

### 2. Text content — rooms, grids, annotations

```
CAD: TEXT "LIVING ROOM" at (5000, 4000)
                    │
        Revit link API sees:
                    │
        List<Curve> (the letter outlines)
        No string value
```

**Problem:** Can't read room names, grid labels, dimension values, slope
annotations. All rendered as curves.

**Workaround:** OCR on rendered image. Fragile, adds latency.

### 3. Block attributes — metadata in doors/windows

```
CAD block "DOOR-01"
├── geometry (arc + rect)
├── ATTRIBUTE "WIDTH" = "900"
├── ATTRIBUTE "HEIGHT" = "2100"
└── ATTRIBUTE "FIRE_RATING" = "FD30"
```

Revit link: sees geometry only. **Attributes = invisible.**

## Element-by-element verdict

| Element | Revit link viable? | Workaround | Better path |
|---------|-------------------|------------|-------------|
| **Walls** | Yes | — | — |
| **Columns** | Yes | — | — |
| **Doors** | Partial | Infer type from geometry width | ezdxf (block name + attributes) |
| **Windows** | Partial | Same | ezdxf |
| **Furniture** | Partial | Layer only, no type | ezdxf |
| **Rooms** | No | OCR room names | ezdxf (text content) |
| **Grids** | No | OCR grid labels | ezdxf |
| **Roof** | Partial | Outline only, no slope | ezdxf (text for slope) |
| **Stairs** | No | Can't get riser count | ezdxf + heuristics |
| **Annotations** | No | OCR | ezdxf |

## Alternative paths comparison

| Feature | Revit link | ezdxf | ODA SDK | APS |
|---------|------------|-------|---------|-----|
| **Format** | DWG/DXF | DXF only | DWG/DXF | DWG/DXF |
| **Layers** | Yes | Yes | Yes | Yes |
| **Lines/arcs/polylines** | Yes | Yes | Yes | Yes |
| **Blocks (intact)** | Partial | Full (name, insert, rotation, scale, attributes) | Full | Full |
| **Block attributes** | No | Yes | Yes | Yes |
| **Text content** | No | Yes | Yes | Yes |
| **Dimensions** | No | Yes | Yes | Yes |
| **Hatches** | Partial | Yes | Yes | Yes |
| **Xdata** | No | Yes | Yes | Yes |
| **AEC objects** | No | No | Yes (with ACA) | Yes (with ACA) |
| **Cost** | Free | Free (MIT) | $$$ (license) | $$$ (per job) |
| **Runs where** | Windows + Revit | Anywhere (Python) | Anywhere | Cloud |

## Recommended architecture

### Two paths, one pipeline

```
┌─────────────────────────────────────────────────────────┐
│                    User attaches DWG                     │
└─────────────────────────┬───────────────────────────────┘
                          │
          ┌───────────────┴───────────────┐
          │                               │
          ▼                               ▼
┌─────────────────────┐       ┌─────────────────────────┐
│   Backend (bina-ai) │       │   Revit (addin-sync)    │
│                     │       │                         │
│  ODA File Converter │       │  extract_cad_geometry   │
│         │           │       │  cad_walls_to_centerlines│
│         ▼           │       │                         │
│      ezdxf          │       │  Wall.Create            │
│         │           │       │  Door.Create            │
│         ▼           │       │  etc.                   │
│  {blocks, text,     │       │                         │
│   attributes,       │       │                         │
│   geometry}         │       │                         │
└─────────┬───────────┘       └────────────▲────────────┘
          │                                │
          │    Agent plans placement       │
          └────────────────────────────────┘
```

**Backend path (ezdxf):** Full CAD data extraction — block names, text, attributes.
Used for understanding CAD content and planning element placement.

**Revit path:** Element creation. Walls via `cad_walls_to_centerlines`, doors/windows
via family placement tools.

## Implementation phases

### Phase 1 (done)
Walls via Revit link. `cad_walls_to_centerlines` with create mode.

### Phase 2 (next)
Doors/windows. Options:
- **Minimal:** Revit link, infer type from geometry width
- **Better:** ezdxf extracts block names → type mapping → Revit creates

### Phase 3
Rooms/grids. Requires text extraction → ezdxf mandatory.

### Phase 4
Full pipeline with metadata (fire ratings, etc.) → ezdxf block attributes.

## Backend implementation (ezdxf path)

### Prerequisites
```bash
# ODA File Converter (free, registration required)
# Download from opendesign.com
sudo dpkg -i ODAFileConverter_*.deb

# ezdxf
pip install ezdxf
```

### DWG → DXF conversion
```python
import subprocess
import tempfile
from pathlib import Path

def dwg_to_dxf(dwg_path: Path) -> Path:
    """Convert DWG to DXF via ODA File Converter."""
    out_dir = tempfile.mkdtemp()
    subprocess.run([
        "ODAFileConverter",
        str(dwg_path.parent),  # input folder
        out_dir,               # output folder
        "ACAD2018", "DXF",     # output version, format
        "0", "1",              # recurse=no, audit=yes
        str(dwg_path.name)     # specific file
    ], check=True)
    return Path(out_dir) / dwg_path.with_suffix(".dxf").name
```

### DXF extraction
```python
import ezdxf

def extract_cad(dxf_path: Path) -> dict:
    doc = ezdxf.readfile(dxf_path)
    msp = doc.modelspace()
    
    return {
        "layers": [l.dxf.name for l in doc.layers],
        "blocks": [
            {
                "name": e.dxf.name,
                "x": e.dxf.insert.x,
                "y": e.dxf.insert.y,
                "rotation": e.dxf.rotation,
                "layer": e.dxf.layer,
                "scale": (e.dxf.xscale, e.dxf.yscale),
                "attributes": {a.dxf.tag: a.dxf.text for a in e.attribs},
            }
            for e in msp.query("INSERT")
        ],
        "text": [
            {
                "content": e.dxf.text if hasattr(e.dxf, 'text') else e.text,
                "x": e.dxf.insert.x,
                "y": e.dxf.insert.y,
                "layer": e.dxf.layer,
            }
            for e in msp.query("TEXT MTEXT")
        ],
        "lines": [
            {
                "x1": e.dxf.start.x, "y1": e.dxf.start.y,
                "x2": e.dxf.end.x, "y2": e.dxf.end.y,
                "layer": e.dxf.layer,
            }
            for e in msp.query("LINE")
        ],
        # + polylines, arcs, circles as needed
    }
```

## Cost comparison

| Path | Fixed cost | Per-job cost | Best for |
|------|-----------|--------------|----------|
| **Revit link** | $0 | $0 | Walls, columns (geometry only) |
| **ezdxf + ODA Converter** | $0 | $0 | Doors, windows, rooms, text |
| **ODA SDK (licensed)** | ~$2k–8k/year | $0 | High volume, no DXF conversion |
| **APS** | $0 | $0.10–$1/job | Cloud-native, AEC objects |

## Summary

**Revit link works for:** Walls, columns — geometry-based elements on known layers.

**Revit link fails for:** Anything needing block names, text content, or attributes.

**Recommended path:** ezdxf (free) for full CAD data, Revit API for element creation.
Two complementary paths feeding one agent.
